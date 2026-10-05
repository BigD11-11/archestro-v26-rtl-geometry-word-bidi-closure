using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Archestro.MeetingVault.Services;

/// <summary>
/// Best-effort persistent llama.cpp server used to avoid re-loading the local model on every Vault question.
/// Any incompatibility falls back to the existing one-shot CLI path in LocalLlmService.
/// </summary>
public static class LocalLlmWarmServer
{
    private static readonly SemaphoreSlim LifecycleGate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static Process? _process;
    private static string _modelPath = "";
    private static int _port = 51988;
    private static int _contextCapacity;
    private static readonly AsyncLocal<bool> SkipNextWarmRequest = new();
    public static string LastTimingSummary { get; private set; } = "";

    public static void BypassNextRequestToCli() => SkipNextWarmRequest.Value = true;

    static LocalLlmWarmServer()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    public static async Task<string?> TryGenerateAsync(
        string runtimeRoot,
        string modelPath,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        int contextTokens,
        int threads,
        bool preferJsonObject,
        CancellationToken cancellationToken)
    {
        if (SkipNextWarmRequest.Value)
        {
            SkipNextWarmRequest.Value = false;
            WriteRuntimeDiagnostic("request-bypass", "reason=bounded-compact-retry; route=one-shot-cli");
            return null;
        }
        var serverExe = Path.Combine(runtimeRoot, "llama-server.exe");
        if (!File.Exists(serverExe) || !File.Exists(modelPath))
            return null;

        try
        {
            if (!await EnsureRunningAsync(serverExe, runtimeRoot, modelPath, contextTokens, threads, cancellationToken).ConfigureAwait(false))
                return null;

            var payload = new Dictionary<string, object?>
            {
                ["model"] = Path.GetFileNameWithoutExtension(modelPath),
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt.Trim() },
                    new { role = "user", content = userPrompt.Trim() + "\n\n/no_think" }
                },
                ["max_tokens"] = Math.Clamp(maxTokens, 16, 4096),
                ["temperature"] = 0.30,
                ["top_p"] = 0.80,
                ["stream"] = false,
                ["cache_prompt"] = true
            };
            if (preferJsonObject)
                payload["response_format"] = new { type = "json_object" };

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            var requestClock = Stopwatch.StartNew();
            WriteRuntimeDiagnostic("request-start", $"maxTokens={Math.Clamp(maxTokens, 16, 4096)}; contextTokens={Math.Clamp(contextTokens, 2048, 16384)}");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            requestClock.Stop();
            WriteRuntimeDiagnostic("request-response", $"elapsedMs={requestClock.ElapsedMilliseconds}; statusCode={(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;
            var first = choices[0];
            if (!first.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                return null;

            if (doc.RootElement.TryGetProperty("timings", out var timings))
            {
                static double N(JsonElement e, string name) =>
                    e.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : 0;
                LastTimingSummary =
                    $"cache_n={N(timings, "cache_n"):0}; prompt_n={N(timings, "prompt_n"):0}; " +
                    $"prompt_ms={N(timings, "prompt_ms"):0}; predicted_n={N(timings, "predicted_n"):0}; " +
                    $"predicted_ms={N(timings, "predicted_ms"):0}; tok_s={N(timings, "predicted_per_second"):0.0}";
            }

            return content.GetString()?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteRuntimeDiagnostic("request-canceled", "cancellationRequested=true; ownedServerStopped=true");
            Stop();
            throw;
        }
        catch (Exception exception)
        {
            // Compatibility is intentionally best-effort. The caller retains the proven CLI fallback.
            WriteRuntimeDiagnostic("request-fallback", $"errorType={exception.GetType().Name}; ownedServerStopped=true");
            Stop();
            return null;
        }
    }

    private static async Task<bool> EnsureRunningAsync(
        string serverExe,
        string runtimeRoot,
        string modelPath,
        int contextTokens,
        int threads,
        CancellationToken cancellationToken)
    {
        await LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } &&
                string.Equals(_modelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
                _contextCapacity >= contextTokens &&
                await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                return true;

            Stop();

            var psi = new ProcessStartInfo
            {
                FileName = serverExe,
                WorkingDirectory = runtimeRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(Math.Clamp(contextTokens, 2048, 16384).ToString());
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(Math.Clamp(threads, 1, 8).ToString());
            psi.ArgumentList.Add("-np");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add("127.0.0.1");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(_port.ToString());
            psi.ArgumentList.Add("--cache-prompt");
            psi.ArgumentList.Add("--threads-http");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("--no-webui");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            if (!process.Start())
                return false;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _modelPath = modelPath;
            _contextCapacity = Math.Clamp(contextTokens, 2048, 16384);
            WriteRuntimeDiagnostic("server-started", "ownedProcess=true; externalNetwork=false");

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(75);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                    return false;
                if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    WriteRuntimeDiagnostic("server-healthy", "endpoint=loopback");
                    return true;
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            Stop();
            return false;
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    private static async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync($"http://127.0.0.1:{_port}/health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _modelPath = "";
        _contextCapacity = 0;
    }

    private static void WriteRuntimeDiagnostic(string stage, string fields)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var line = $"[{DateTimeOffset.UtcNow:O}] stage=local-runtime-{stage}; {fields}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(AppPaths.Logs, "meeting-report-stage-diagnostics.log"), line, new UTF8Encoding(false));
        }
        catch { }
    }
}
