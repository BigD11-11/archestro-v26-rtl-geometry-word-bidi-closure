using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed record IntelligenceProviderMetadata(string Id, string DisplayName, string DefaultModel, bool SupportsStructuredOutput);
public sealed record IntelligenceGenerationResult(string Text, string Provider, string Model, int? InputTokens, int? OutputTokens, int RequestCount = 1);
public sealed record ProviderConnectionResult(bool Success, string Provider, string Model, string Message);

public interface IIntelligenceProvider
{
    IntelligenceProviderMetadata Metadata { get; }
    Task<IntelligenceGenerationResult> GenerateStructuredAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken);
    Task<IntelligenceGenerationResult> GenerateTextAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken);
    Task<ProviderConnectionResult> TestConnectionAsync(string model, CancellationToken cancellationToken);
}

public sealed class LocalIntelligenceProvider(LocalLlmService local) : IIntelligenceProvider
{
    public IntelligenceProviderMetadata Metadata { get; } = new("Local", "Local", "Qwen3-4B-Q4_K_M", true);
    public Task<IntelligenceGenerationResult> GenerateStructuredAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken) => Generate(systemPrompt, userPrompt, model, maxTokens, cancellationToken, true, null);
    public Task<IntelligenceGenerationResult> GenerateTextAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken) => Generate(systemPrompt, userPrompt, model, maxTokens, cancellationToken, false, null);

    public Task<IntelligenceGenerationResult> GenerateWithContextAsync(string systemPrompt, string userPrompt, string model, int maxTokens,
        CancellationToken cancellationToken, int? contextTokensOverride, bool structured) =>
        Generate(systemPrompt, userPrompt, model, maxTokens, cancellationToken, structured, contextTokensOverride);

    private async Task<IntelligenceGenerationResult> Generate(string system, string user, string model, int maxTokens, CancellationToken token, bool json, int? contextTokensOverride)
    {
        var text = await local.GenerateAsync(system, user, maxTokens, token, contextTokensOverride: contextTokensOverride, preferJsonObject: json).ConfigureAwait(false);
        return new(text, "Local", model, null, null);
    }

    public async Task<ProviderConnectionResult> TestConnectionAsync(string model, CancellationToken cancellationToken)
    {
        await local.SelfTestAsync(cancellationToken).ConfigureAwait(false);
        return new(true, "Local", model, "Local AI is ready.");
    }
}

/// <summary>Chat-completions-compatible provider adapter shared by OpenAI, Gemini, DeepSeek and Groq.</summary>
public sealed class OpenAiCompatibleIntelligenceProvider : IIntelligenceProvider
{
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly TimeSpan _requestTimeout;
    public IntelligenceProviderMetadata Metadata { get; }

    public OpenAiCompatibleIntelligenceProvider(string provider, string model, string apiKey, HttpClient? client = null, TimeSpan? requestTimeout = null)
    {
        _apiKey = apiKey;
        _client = client ?? SharedClient;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(120);
        (var id, var name, _endpoint, var defaultModel) = provider.Trim().ToLowerInvariant() switch
        {
            "openai" => ("OpenAI", "OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-6-luna"),
            "gemini" or "google gemini" => ("Gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-3.5-flash-lite"),
            "deepseek" => ("DeepSeek", "DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-flash"),
            "groq" => ("Groq", "Groq", "https://api.groq.com/openai/v1/chat/completions", "openai/gpt-oss-20b"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), "Select a supported intelligence provider.")
        };
        Metadata = new(id, name, defaultModel, true);
    }

    public Task<IntelligenceGenerationResult> GenerateStructuredAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken) =>
        SendAsync(systemPrompt, userPrompt, model, maxTokens, structured: true, cancellationToken);

    public Task<IntelligenceGenerationResult> GenerateTextAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken) =>
        SendAsync(systemPrompt, userPrompt, model, maxTokens, structured: false, cancellationToken);

    public async Task<ProviderConnectionResult> TestConnectionAsync(string model, CancellationToken cancellationToken)
    {
        _ = await GenerateTextAsync("Reply with OK only.", "Connection test.", model, 12, cancellationToken).ConfigureAwait(false);
        return new(true, Metadata.Id, model, "Connection succeeded.");
    }

    private async Task<IntelligenceGenerationResult> SendAsync(string system, string user, string model, int maxTokens, bool structured, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) throw new ProviderRequestException("Add an API key before testing this provider.", HttpStatusCode.Unauthorized);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? Metadata.DefaultModel : model,
            ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            [Metadata.Id is "OpenAI" or "Groq" ? "max_completion_tokens" : "max_tokens"] = Math.Clamp(maxTokens, 8, 8192)
        };
        // DeepSeek V4 Flash enables high-effort thinking by default. Small completion
        // budgets (including the connection probe) can be consumed before visible
        // content is returned, which the chat-completions adapter correctly rejects
        // as an empty response. Product answers use the provider's documented
        // non-thinking mode so the full budget remains available to the user-facing
        // answer; no reasoning content is sent to or surfaced by the application.
        if (Metadata.Id.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase))
            payload["thinking"] = new { type = "disabled" };
        if (structured) payload["response_format"] = new { type = "json_object" };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("The provider request timed out."); }
            catch (HttpRequestException)
            { throw new ProviderRequestException("The provider could not be reached. Check the connection and provider settings."); }

            using (response)
            {
                if (IsRetryable(response.StatusCode) && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 250, 3000)), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new ProviderRequestException(SafeStatusMessage(response.StatusCode), response.StatusCode, attempt + 1);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var content = ExtractContent(root);
                if (string.IsNullOrWhiteSpace(content)) throw new ProviderRequestException("The provider returned an empty response.");
                int? input = ReadUsage(root, "prompt_tokens", "input_tokens");
                int? output = ReadUsage(root, "completion_tokens", "output_tokens");
                return new(content, Metadata.Id, (string.IsNullOrWhiteSpace(model) ? Metadata.DefaultModel : model), input, output, attempt + 1);
            }
        }
        throw new ProviderRequestException("The provider request did not complete.");
    }

    private static string ExtractContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return string.Empty;
        var message = choices[0].TryGetProperty("message", out var msg) ? msg : default;
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
            return string.Join("", content.EnumerateArray().Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(x => x.TryGetProperty("text", out var text) ? text.GetString() : null));
        return string.Empty;
    }

    private static int? ReadUsage(JsonElement root, params string[] keys)
    {
        if (!root.TryGetProperty("usage", out var usage)) return null;
        foreach (var key in keys) if (usage.TryGetProperty(key, out var value) && value.TryGetInt32(out var count)) return count;
        return null;
    }

    private static bool IsRetryable(HttpStatusCode code) => code == (HttpStatusCode)429 || (int)code >= 500;
    private static string SafeStatusMessage(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The provider rejected authentication. Check the API key and access permissions.",
        (HttpStatusCode)429 => "The provider rate limit was reached. Try again shortly.",
        _ => $"The provider returned HTTP {(int)code}. No cached report was changed."
    };
}

public sealed class ProviderRequestException(string message, HttpStatusCode? statusCode = null, int requestCount = 1) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public int RequestCount { get; } = requestCount;
}

public sealed class IntelligenceProviderRouter
{
    private readonly AppSettings _settings;
    private readonly IIntelligenceProvider _local;
    private readonly Func<string, string, IIntelligenceProvider> _cloudFactory;
    private readonly Action<string, string, long, int, int?, int?, bool> _telemetry;

    public IntelligenceProviderRouter(AppSettings settings, IIntelligenceProvider local,
        Func<string, string, IIntelligenceProvider>? cloudFactory = null,
        Action<string, string, long, int, int?, int?, bool>? telemetry = null)
    {
        _settings = settings; _local = local;
        _cloudFactory = cloudFactory ?? ((provider, key) => new OpenAiCompatibleIntelligenceProvider(provider, "", key));
        _telemetry = telemetry ?? ProviderTelemetry.Write;
    }

    public IIntelligenceProvider LocalProvider => _local;
    public bool IsCloudSelected => _settings.CloudIntelligenceEnabled && !IsLocalProvider(_settings.IntelligenceProvider);

    public async Task<IntelligenceGenerationResult> GenerateAsync(string system, string user, int maxTokens,
        CancellationToken token, string? modelOverride = null, bool structured = false, int? contextTokensOverride = null)
    {
        if (!IsCloudSelected)
            return await CallAsync(_local, system, user, maxTokens, token, modelOverride ?? _settings.IntelligenceModel, structured).ConfigureAwait(false);
        if (!_settings.CloudIntelligenceConsentAccepted)
            throw new InvalidOperationException("Enable cloud mode and accept the privacy disclosure before sending meeting text.");

        var providerName = _settings.IntelligenceProvider;
        var model = string.IsNullOrWhiteSpace(modelOverride) ? CloudProviderDefaults.Model(providerName, _settings.CloudIntelligenceModel) : modelOverride;
        var key = CloudSecretProtector.Unprotect(_settings.EncryptedIntelligenceApiKey);
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderRequestException("Add an API key in Settings before using this provider.", HttpStatusCode.Unauthorized);
        var provider = _cloudFactory(providerName, key);
        var timer = Stopwatch.StartNew();
        var fallback = false; var requests = 0; int? input = null; int? output = null;
        try
        {
            var result = await CallAsync(provider, system, user, maxTokens, token, model, structured).ConfigureAwait(false);
            requests = result.RequestCount; input = result.InputTokens; output = result.OutputTokens;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (_settings.CloudLocalFallbackEnabled)
        {
            fallback = true;
            requests = exception is ProviderRequestException providerError ? providerError.RequestCount : 1;
            if (_local is LocalIntelligenceProvider local)
                return await local.GenerateWithContextAsync(system, user, _settings.IntelligenceModel, maxTokens, token, contextTokensOverride, structured).ConfigureAwait(false);
            return await CallAsync(_local, system, user, maxTokens, token, _settings.IntelligenceModel, structured).ConfigureAwait(false);
        }
        finally
        {
            timer.Stop();
            _telemetry(providerName, model, timer.ElapsedMilliseconds, requests, input, output, fallback);
            key = string.Empty;
        }
    }

    public async Task<ProviderConnectionResult> TestConnectionAsync(string providerName, string model, string apiKey, CancellationToken token)
    {
        var provider = IsLocalProvider(providerName) ? _local : _cloudFactory(providerName, apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await provider.TestConnectionAsync(string.IsNullOrWhiteSpace(model) ? provider.Metadata.DefaultModel : model, timeout.Token).ConfigureAwait(false);
    }

    private static Task<IntelligenceGenerationResult> CallAsync(IIntelligenceProvider provider, string system, string user,
        int maxTokens, CancellationToken token, string model, bool structured) =>
        structured
            ? provider.GenerateStructuredAsync(system, user, model, maxTokens, token)
            : provider.GenerateTextAsync(system, user, model, maxTokens, token);

    private static bool IsLocalProvider(string provider) => string.IsNullOrWhiteSpace(provider) || provider.Equals("Local", StringComparison.OrdinalIgnoreCase);
}

public static class CloudProviderDefaults
{
    public static string Model(string provider, string configured = "") => !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : provider.Trim().ToLowerInvariant() switch
    {
        "openai" => "gpt-6-luna",
        "gemini" or "google gemini" => "gemini-3.5-flash-lite",
        "deepseek" => "deepseek-flash",
        "groq" => "openai/gpt-oss-20b",
        _ => "Qwen3-4B-Q4_K_M"
    };
}

public static class ProviderTelemetry
{
    public static void Write(string provider, string model, long elapsedMs, int requests, int? inputTokens, int? outputTokens, bool fallback)
    {
        var row = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, provider, model, elapsedMs, requests, inputTokens, outputTokens, fallbackUsed = fallback });
        try { File.AppendAllText(Path.Combine(AppPaths.Logs, "intelligence-provider-telemetry.jsonl"), row + Environment.NewLine, Encoding.UTF8); }
        catch { }
    }
}
