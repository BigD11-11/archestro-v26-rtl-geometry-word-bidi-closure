using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public static class CloudProviderQaService
{
    public static async Task RunAsync(string? outputPath = null)
    {
        var key = "v28-fixture-key-never-log";
        var success = Completion("{\"answer\":\"fixture-json\"}");
        string? openAiBody = null;
        string? openAiUrl = null;
        System.Net.Http.Headers.AuthenticationHeaderValue? openAiAuth = null;
        var openAiHandler = new StubHandler(async (request, _) =>
        {
            openAiBody = await request.Content!.ReadAsStringAsync();
            openAiUrl = request.RequestUri?.AbsoluteUri;
            openAiAuth = request.Headers.Authorization;
            return success();
        });
        using var openAiClient = new HttpClient(openAiHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var openAi = new OpenAiCompatibleIntelligenceProvider("OpenAI", "", key, openAiClient);
        var structured = await openAi.GenerateStructuredAsync("system-json", "user-json", "model-test", 256, CancellationToken.None);
        using (var body = JsonDocument.Parse(openAiBody!))
        {
            Require(body.RootElement.GetProperty("model").GetString() == "model-test", "OpenAI model field missing.");
            Require(body.RootElement.GetProperty("messages").GetArrayLength() == 2, "OpenAI-compatible messages are not in chat-completions form.");
            Require(body.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "Structured JSON response mode missing.");
            Require(body.RootElement.GetProperty("max_completion_tokens").GetInt32() == 256, "OpenAI completion token budget missing.");
        }
        Require(new Uri(openAiUrl!).Host == "api.openai.com" && openAiAuth?.Scheme == "Bearer" &&
                openAiAuth.Parameter == key, "OpenAI request URI or bearer auth shape is invalid.");
        using (var parsed = JsonDocument.Parse(structured.Text))
            Require(parsed.RootElement.GetProperty("answer").GetString() == "fixture-json", "Structured response content did not parse as JSON.");
        Require(structured.InputTokens == 4 && structured.OutputTokens == 3, "Provider usage metadata was not captured.");

        string? geminiBody = null;
        string? geminiUrl = null;
        var geminiHandler = new StubHandler(async (request, _) => { geminiBody = await request.Content!.ReadAsStringAsync(); geminiUrl = request.RequestUri?.AbsoluteUri; return success(); });
        using var geminiClient = new HttpClient(geminiHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var gemini = new OpenAiCompatibleIntelligenceProvider("Gemini", "", key, geminiClient);
        _ = await gemini.GenerateStructuredAsync("json", "{}", "gemini-custom", 128, CancellationToken.None);
        using (var body = JsonDocument.Parse(geminiBody!))
            Require(body.RootElement.GetProperty("max_tokens").GetInt32() == 128, "Gemini compatible request token limit missing.");
        Require(geminiUrl == "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "Gemini OpenAI-compatible endpoint is wrong.");

        string? deepSeekBody = null;
        string? deepSeekUrl = null;
        var deepSeekHandler = new StubHandler(async (request, _) =>
        {
            deepSeekBody = await request.Content!.ReadAsStringAsync();
            deepSeekUrl = request.RequestUri?.AbsoluteUri;
            return success();
        });
        using var deepSeekClient = new HttpClient(deepSeekHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var deepSeek = new OpenAiCompatibleIntelligenceProvider("DeepSeek", "", key, deepSeekClient);
        var deepSeekConnection = await deepSeek.TestConnectionAsync("deepseek-flash", CancellationToken.None);
        using (var body = JsonDocument.Parse(deepSeekBody!))
        {
            Require(body.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled",
                "DeepSeek connection probe must disable default reasoning so its short output budget produces visible content.");
            Require(body.RootElement.GetProperty("max_tokens").GetInt32() == 12,
                "DeepSeek connection probe token budget changed unexpectedly.");
        }
        Require(deepSeekConnection.Succeeded && deepSeekUrl == "https://api.deepseek.com/chat/completions",
            "DeepSeek connection probe endpoint or success result is invalid.");

        var authHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("provider error echoed " + key) }));
        using var authClient = new HttpClient(authHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var authProvider = new OpenAiCompatibleIntelligenceProvider("DeepSeek", "", key, authClient);
        var authError = await CaptureAsync(() => authProvider.GenerateTextAsync("s", "u", "deepseek-flash", 12, CancellationToken.None));
        Require(authError is ProviderRequestException { StatusCode: HttpStatusCode.Unauthorized } && authHandler.Calls == 1,
            "401 should fail once without retry.");
        Require(!authError!.Message.Contains(key, StringComparison.Ordinal), "API key was exposed in a provider exception.");

        var retryHandler = new StubHandler((_, index) => Task.FromResult(index == 1
            ? new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(250)) } }
            : Completion("retry-ok")()));
        using var retryClient = new HttpClient(retryHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var groq = new OpenAiCompatibleIntelligenceProvider("Groq", "", key, retryClient);
        var retried = await groq.GenerateTextAsync("s", "u", "openai/gpt-oss-20b", 12, CancellationToken.None);
        Require(retryHandler.Calls == 2 && retried.RequestCount == 2, "429 did not perform exactly one bounded retry.");

        var timeoutHandler = new CancellationAwareHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Completion("unreachable")();
        });
        using var timeoutClient = new HttpClient(timeoutHandler)
        { Timeout = Timeout.InfiniteTimeSpan };
        var timeoutProvider = new OpenAiCompatibleIntelligenceProvider("OpenAI", "", key, timeoutClient, TimeSpan.FromMilliseconds(40));
        var timeoutError = await CaptureAsync(() => timeoutProvider.GenerateTextAsync("s", "u", "gpt-6-luna", 12, CancellationToken.None));
        Require(timeoutError is TimeoutException, "Provider request timeout was not classified.");

        var cancelHandler = new CancellationAwareHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Completion("unreachable")();
        });
        using var cancelClient = new HttpClient(cancelHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var cancelProvider = new OpenAiCompatibleIntelligenceProvider("OpenAI", "", key, cancelClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        var canceled = await CaptureAsync(() => cancelProvider.GenerateTextAsync("s", "u", "gpt-6-luna", 12, cancellation.Token));
        Require(canceled is OperationCanceledException, "Caller cancellation was not honored.");

        var fallbackHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var fallbackClient = new HttpClient(fallbackHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var fallbackSettings = SettingsForCloud(key, enabled: true);
        var telemetryRows = new List<string>();
        var fallbackLocal = new FakeLocalProvider();
        var fallbackRouter = new IntelligenceProviderRouter(fallbackSettings, fallbackLocal,
            (provider, secret) => new OpenAiCompatibleIntelligenceProvider(provider, "", secret, fallbackClient),
            (provider, model, elapsed, count, input, output, usedFallback) => telemetryRows.Add(JsonSerializer.Serialize(new { provider, model, elapsed, count, input, output, usedFallback })));
        var fallbackResult = await fallbackRouter.GenerateAsync("s", "local-fallback-fixture", 30, CancellationToken.None, structured: true);
        Require(fallbackResult.Provider == "Local" && fallbackLocal.CallCount == 1 && fallbackHandler.Calls == 2,
            "Transient provider failure did not fall back locally after the bounded retry.");
        Require(telemetryRows.Count == 1 && telemetryRows[0].Contains("usedFallback\":true", StringComparison.Ordinal) &&
                !telemetryRows[0].Contains(key, StringComparison.Ordinal) && !telemetryRows[0].Contains("local-fallback-fixture", StringComparison.Ordinal),
            "Provider telemetry included sensitive content or missed fallback metadata.");

        var disabledHandler = new StubHandler((_, _) => Task.FromResult(Completion("unexpected-network")()));
        using var disabledClient = new HttpClient(disabledHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var disabledSettings = SettingsForCloud(key, enabled: false);
        var disabledRouter = new IntelligenceProviderRouter(disabledSettings, new FakeLocalProvider(),
            (provider, secret) => new OpenAiCompatibleIntelligenceProvider(provider, "", secret, disabledClient));
        var localResult = await disabledRouter.GenerateAsync("s", "local-only", 20, CancellationToken.None);
        Require(localResult.Provider == "Local" && disabledHandler.Calls == 0, "Cloud-disabled mode made a network request.");

        var noConsent = SettingsForCloud(key, enabled: true);
        noConsent.CloudIntelligenceConsentAccepted = false;
        var noConsentHandler = new StubHandler((_, _) => Task.FromResult(Completion("unexpected")()));
        using var noConsentClient = new HttpClient(noConsentHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var noConsentRouter = new IntelligenceProviderRouter(noConsent, new FakeLocalProvider(),
            (provider, secret) => new OpenAiCompatibleIntelligenceProvider(provider, "", secret, noConsentClient));
        var consentError = await CaptureAsync(() => noConsentRouter.GenerateAsync("s", "no-send", 10, CancellationToken.None));
        Require(consentError is InvalidOperationException && noConsentHandler.Calls == 0, "Cloud request occurred before explicit consent.");

        var evidence = new
        {
            status = "PASS", openAiCompatibleShape = "PASS", geminiCompatibleShape = "PASS", structuredJsonParse = "PASS",
            deepSeekThinkingDisabled = "PASS",
            unauthorizedNoRetryAndRedacted = "PASS", retry429Once = "PASS", timeout = "PASS", cancellation = "PASS",
            localFallback = "PASS", telemetryRedacted = "PASS", cloudDisabledZeroCalls = "PASS", noConsentZeroCalls = "PASS",
            realCredentialsUsed = false, callsToLiveProviders = 0
        };
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static AppSettings SettingsForCloud(string key, bool enabled) => new()
    {
        IntelligenceProvider = "OpenAI", CloudIntelligenceModel = "gpt-6-luna", CloudIntelligenceEnabled = enabled,
        CloudIntelligenceConsentAccepted = true, CloudLocalFallbackEnabled = true,
        EncryptedIntelligenceApiKey = CloudSecretProtector.Protect(key)
    };

    private static Func<HttpResponseMessage> Completion(string content) => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
            usage = new { prompt_tokens = 4, completion_tokens = 3 }
        }), Encoding.UTF8, "application/json")
    };

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("V28 provider QA failed: " + message);
    }

    private sealed class FakeLocalProvider : IIntelligenceProvider
    {
        public int CallCount { get; private set; }
        public IntelligenceProviderMetadata Metadata { get; } = new("Local", "Local", "local-fixture", true);
        public Task<IntelligenceGenerationResult> GenerateStructuredAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken) => Generate(model);
        public Task<IntelligenceGenerationResult> GenerateTextAsync(string systemPrompt, string userPrompt, string model, int maxTokens, CancellationToken cancellationToken) => Generate(model);
        private Task<IntelligenceGenerationResult> Generate(string model) { CallCount++; return Task.FromResult(new IntelligenceGenerationResult("local-result", "Local", model, null, null)); }
        public Task<ProviderConnectionResult> TestConnectionAsync(string model, CancellationToken cancellationToken) => Task.FromResult(new ProviderConnectionResult(true, "Local", model, "Local test passed."));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, Interlocked.Increment(ref _calls));
    }

    private sealed class CancellationAwareHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}
