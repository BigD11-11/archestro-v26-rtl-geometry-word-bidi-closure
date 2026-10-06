using System.Collections.Concurrent;
using System.Text.Json;

namespace Archestro.MeetingVault.Services;

public static class TranscriptionProgressService
{
    private sealed class ActiveJob
    {
        public DateTimeOffset Started { get; init; }
        public int AudioSeconds { get; init; }
        public string Stage { get; set; } = "Transcribing";
    }

    private sealed class Metrics
    {
        public List<double> RealTimeFactors { get; set; } = new();
    }

    private static readonly ConcurrentDictionary<string, ActiveJob> Active = new();
    private static readonly object MetricsGate = new();
    private static Metrics? _metrics;

    private static string MetricsPath =>
        Path.Combine(AppPaths.System, "transcription_metrics.json");

    public static void Begin(string meetingId, int audioSeconds, string stage = "Transcribing")
    {
        Active[meetingId] = new ActiveJob
        {
            Started = DateTimeOffset.Now,
            AudioSeconds = Math.Max(1, audioSeconds),
            Stage = stage
        };
    }

    public static void SetStage(string meetingId, string stage)
    {
        if (Active.TryGetValue(meetingId, out var job))
            job.Stage = stage;
    }

    public static void Complete(string meetingId, bool success)
    {
        if (!Active.TryRemove(meetingId, out var job))
            return;

        if (!success)
            return;

        var elapsed = Math.Max(1, (DateTimeOffset.Now - job.Started).TotalSeconds);
        var rtf = elapsed / Math.Max(1, job.AudioSeconds);

        // Ignore pathological samples; keep a compact history from this actual PC.
        if (rtf <= 0 || rtf > 50)
            return;

        lock (MetricsGate)
        {
            var metrics = LoadMetrics();
            metrics.RealTimeFactors.Add(rtf);

            while (metrics.RealTimeFactors.Count > 12)
                metrics.RealTimeFactors.RemoveAt(0);

            SaveMetrics(metrics);
        }
    }

    public static bool HasActive => !Active.IsEmpty;

    public static string? GetAnyDisplay()
    {
        var item = Active.OrderBy(kv => kv.Value.Started).FirstOrDefault();
        return string.IsNullOrWhiteSpace(item.Key)
            ? null
            : BuildDisplay(item.Value);
    }

    public static string GetDisplay(string meetingId, string fallback)
    {
        return Active.TryGetValue(meetingId, out var job)
            ? BuildDisplay(job)
            : fallback;
    }

    public static string GetPrimaryLine(string meetingId, string fallback)
    {
        if (Active.TryGetValue(meetingId, out var job))
            return job.Stage;

        var text = fallback ?? "";

        if (text.StartsWith("Pass 1 • bilingual transcription", StringComparison.OrdinalIgnoreCase))
            return "Transcribing • Arabic + English";

        if (text.StartsWith("Queued • smart background starts in ", StringComparison.OrdinalIgnoreCase))
            return "Queued • smart background";

        if (text.Contains("waiting for current transcript", StringComparison.OrdinalIgnoreCase))
            return "Queued";

        return text;
    }

    public static string GetSecondaryLine(string meetingId, string fallback)
    {
        if (Active.TryGetValue(meetingId, out var job))
        {
            var elapsed = DateTimeOffset.Now - job.Started;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            var elapsedText = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");

            var recoveryStage =
                job.Stage.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
                job.Stage.Contains("repair", StringComparison.OrdinalIgnoreCase);

            if (recoveryStage)
                return $"{elapsedText} elapsed";

            var ratio = GetHistoricalMedianRtf();
            if (!ratio.HasValue)
                return $"{elapsedText} elapsed";

            var estimatedTotal = TimeSpan.FromSeconds(job.AudioSeconds * ratio.Value);
            var remaining = estimatedTotal - elapsed;

            if (remaining <= TimeSpan.Zero)
                return $"{elapsedText} elapsed • finishing…";

            var remainingText = remaining.TotalHours >= 1
                ? remaining.ToString(@"hh\:mm\:ss")
                : remaining.ToString(@"mm\:ss");

            return $"{elapsedText} elapsed • ~{remainingText} left";
        }

        var text = fallback ?? "";
        const string prefix = "Queued • smart background starts in ";

        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "Starts in " + text[prefix.Length..];

        if (text.Contains("waiting for current transcript", StringComparison.OrdinalIgnoreCase))
            return "Waiting for current transcript";

        return "";
    }

    private static string BuildDisplay(ActiveJob job)
    {
        var elapsed = DateTimeOffset.Now - job.Started;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        var elapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        var recoveryStage =
            job.Stage.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
            job.Stage.Contains("repair", StringComparison.OrdinalIgnoreCase);

        if (recoveryStage)
            return $"{job.Stage} • {elapsedText} elapsed";

        var ratio = GetHistoricalMedianRtf();
        if (!ratio.HasValue)
            return $"{job.Stage} • {elapsedText} elapsed";

        var estimatedTotal = TimeSpan.FromSeconds(job.AudioSeconds * ratio.Value);
        var remaining = estimatedTotal - elapsed;

        if (remaining <= TimeSpan.Zero)
            return $"{job.Stage} • {elapsedText} elapsed • finishing…";

        var remainingText = remaining.TotalHours >= 1
            ? remaining.ToString(@"hh\:mm\:ss")
            : remaining.ToString(@"mm\:ss");

        return $"{job.Stage} • {elapsedText} elapsed • ~{remainingText} left";
    }

    private static double? GetHistoricalMedianRtf()
    {
        lock (MetricsGate)
        {
            var values = LoadMetrics().RealTimeFactors
                .Where(x => x > 0 && x <= 50)
                .OrderBy(x => x)
                .ToList();

            if (values.Count == 0)
                return null;

            var mid = values.Count / 2;
            return values.Count % 2 == 1
                ? values[mid]
                : (values[mid - 1] + values[mid]) / 2.0;
        }
    }

    private static Metrics LoadMetrics()
    {
        if (_metrics is not null)
            return _metrics;

        try
        {
            if (File.Exists(MetricsPath))
            {
                _metrics = JsonSerializer.Deserialize<Metrics>(
                    File.ReadAllText(MetricsPath)) ?? new Metrics();
            }
            else
            {
                _metrics = new Metrics();
            }
        }
        catch
        {
            _metrics = new Metrics();
        }

        return _metrics;
    }

    private static void SaveMetrics(Metrics metrics)
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(
                MetricsPath,
                JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
