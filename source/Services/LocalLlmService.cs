using System.Diagnostics;
using System.Text;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class LocalLlmService
{
    private readonly AppSettings _settings;
    private static readonly SemaphoreSlim SharedInferenceGate = new(1, 1);
    public string RuntimeMode => File.Exists(Path.Combine(RuntimeRoot, "llama-server.exe"))
        ? "Persistent warm local server + serialized CLI fallback"
        : "Serialized local compatibility mode";

    public LocalLlmService(AppSettings settings)
    {
        _settings = settings;
    }

    public string RuntimeRoot => ResolveRuntimeRoot();
    public string ModelPath => ResolveModelPath();

    private string ResolveGeneratorExecutable()
    {
        var completion = Path.Combine(RuntimeRoot, "llama-completion.exe");
        if (File.Exists(completion)) return completion;

        var cli = Path.Combine(RuntimeRoot, "llama-cli.exe");
        if (File.Exists(cli)) return cli;

        throw new InvalidOperationException(
            "Local AI runtime is missing. Expected llama-completion.exe or llama-cli.exe.");
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens = 1200,
        CancellationToken cancellationToken = default,
        int? contextTokensOverride = null,
        bool preferJsonObject = false)
    {
        await SharedInferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (!File.Exists(ModelPath))
            throw new InvalidOperationException(
                "Local Intelligence model is missing. Re-run Build 3 preparation or install the Intelligence Pack.");

        var effectiveSystemPrompt = systemPrompt.Trim();
        var effectiveUserPrompt = userPrompt.Trim() + "\n\n/no_think";
        var contextTokens = contextTokensOverride ?? _settings.AiContextTokens;

        // Prefer a warm one-slot local llama-server when the installed runtime supports it.
        // This keeps the model resident between questions and reuses prompt cache. Any server
        // incompatibility immediately falls back to the proven one-shot CLI path below.
        var warm = await LocalLlmWarmServer.TryGenerateAsync(
            RuntimeRoot,
            ModelPath,
            effectiveSystemPrompt,
            userPrompt,
            maxTokens,
            contextTokens,
            _settings.AiMaxThreads,
            preferJsonObject,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(warm))
        {
            warm = StripThinking(warm);
            warm = RepairMojibakeIfNeeded(warm);
            if (!string.IsNullOrWhiteSpace(warm))
                return warm.Trim();
        }

        var generator = ResolveGeneratorExecutable();

        var psi = new ProcessStartInfo
        {
            FileName = generator,
            WorkingDirectory = RuntimeRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Never inherit an interactive console/stdin. llama.cpp chat-template tools may
            // otherwise remain alive waiting for another turn even after generation.
            RedirectStandardInput = true,

            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(ModelPath);
        // llama.cpp documents one-and-done custom-system turns with Jinja +
        // --single-turn. Qwen ships a chat template, so make that path explicit.
        psi.ArgumentList.Add("--jinja");
        psi.ArgumentList.Add("-sys");
        psi.ArgumentList.Add(effectiveSystemPrompt);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(effectiveUserPrompt);

        // Both llama-cli and llama-completion support a predefined one-turn flow.
        // Use the documented long form for clarity across runtime builds.
        psi.ArgumentList.Add("--single-turn");

        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(Math.Clamp(maxTokens, 16, 4096).ToString());
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Math.Clamp(contextTokens, 1024, 32768).ToString());
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(Math.Clamp(_settings.AiMaxThreads, 1, 12).ToString());
        psi.ArgumentList.Add("--temp");
        psi.ArgumentList.Add("0.30");
        psi.ArgumentList.Add("--top-p");
        psi.ArgumentList.Add("0.80");
        psi.ArgumentList.Add("--top-k");
        psi.ArgumentList.Add("20");
        psi.ArgumentList.Add("--no-display-prompt");
        psi.ArgumentList.Add("--no-warmup");
        psi.ArgumentList.Add("--simple-io");

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Could not start local AI runtime.");

        // Critical non-interactive contract:
        // redirected stdin is closed immediately, so any accidental interactive read receives EOF
        // instead of inheriting an open console and hanging indefinitely.
        process.StandardInput.Close();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var hardTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(maxTokens <= 32 ? 180 : 600));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, hardTimeout.Token);

        using var registration = linkedCancellation.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            hardTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Local AI runtime exceeded its hard timeout ({(maxTokens <= 32 ? 180 : 600)} seconds).");
        }

        // Ensure asynchronous stdout/stderr handlers have drained after process exit.
        process.WaitForExit();

        var output = stdout.ToString().Trim();
        if (process.ExitCode != 0)
        {
            var detail = stderr.ToString().Trim();
            throw new InvalidOperationException(
                "Local AI runtime failed." +
                (string.IsNullOrWhiteSpace(detail) ? "" : Environment.NewLine + detail));
        }

        output = StripThinking(output);
        output = RepairMojibakeIfNeeded(output);

        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Local AI returned an empty response.");

        return output.Trim();
        }
        finally
        {
            SharedInferenceGate.Release();
        }
    }

    public async Task SelfTestAsync(CancellationToken cancellationToken = default)
    {
        // Runtime health check: prove actual local generation without turning harmless
        // punctuation/formatting variance into a false product failure.
        const string sentinel = "ARCHOK";

        var text = await GenerateAsync(
            "You are a local runtime health check. Reply with the requested sentinel and no explanation.",
            $"Reply with exactly: {sentinel}",
            32,
            cancellationToken,
            contextTokensOverride: 1024).ConfigureAwait(false);

        var normalized = new string(
            text.Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        if (!normalized.Contains(sentinel, StringComparison.Ordinal))
        {
            var preview = text.Length <= 500 ? text : text[..500];
            throw new InvalidOperationException(
                "Local AI generated output but the health-check sentinel was not found." +
                Environment.NewLine +
                $"Expected normalized sentinel: {sentinel}" +
                Environment.NewLine +
                $"Generated output preview: {preview}");
        }
    }

    private static string RepairMojibakeIfNeeded(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var suspicious =
            text.Contains('Ø') ||
            text.Contains('Ù') ||
            text.Contains('Ã') ||
            text.Contains('Â');

        if (!suspicious)
            return text;

        try
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            var repaired = Encoding.UTF8.GetString(bytes);

            var beforeScore = MojibakeScore(text);
            var afterScore = MojibakeScore(repaired);
            return afterScore < beforeScore ? repaired : text;
        }
        catch
        {
            return text;
        }
    }

    private static int MojibakeScore(string text) =>
        text.Count(c => c is 'Ø' or 'Ù' or 'Ã' or 'Â' or '�');

    private static string StripThinking(string text)
    {
        while (true)
        {
            var start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var end = text.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                text = text[..start];
                break;
            }
            text = text.Remove(start, end + "</think>".Length - start);
        }
        return text.Trim();
    }

    private static string ResolveRuntimeRoot()
    {
        var env = Environment.GetEnvironmentVariable("ARCHESTRO_AI_RUNTIME_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var beside = Path.Combine(AppContext.BaseDirectory, "AI", "llama");
        if (Directory.Exists(beside)) return beside;

        return AppPaths.AiRuntime;
    }

    private static string ResolveModelPath()
    {
        var env = Environment.GetEnvironmentVariable("ARCHESTRO_AI_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        var beside = Path.Combine(
            AppContext.BaseDirectory, "AI", "Models", "Qwen3-4B-Q4_K_M.gguf");
        if (File.Exists(beside)) return beside;

        return Path.Combine(AppPaths.AiModels, "Qwen3-4B-Q4_K_M.gguf");
    }
}
