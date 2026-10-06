namespace Archestro.MeetingVault.Services;

public static class IntelligenceSelfTestService
{
    public static async Task RunAsync()
    {
        var settings = new Models.AppSettings
        {
            AiMaxThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
            AiContextTokens = 2048
        };

        var llm = new LocalLlmService(settings);
        await llm.SelfTestAsync();

        File.WriteAllText(
            Path.Combine(AppPaths.Logs, "intelligence-self-test-pass.txt"),
            $"PASS {DateTimeOffset.Now:o}{Environment.NewLine}" +
            $"Runtime: llama.cpp{Environment.NewLine}" +
            $"Model: Qwen3-4B-Q4_K_M{Environment.NewLine}" +
            $"Mode: fully local/offline after model installation");
    }
}
