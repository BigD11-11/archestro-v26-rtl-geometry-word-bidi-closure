using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed record SubtitleCue(double StartSeconds, double EndSeconds, string Text);

public sealed record ImportantMomentContext(
    ImportantMark Mark,
    string Timestamp,
    string Context,
    double CueStartSeconds,
    double CueEndSeconds);

public static class TranscriptContextService
{
    private static readonly Regex TimeRegex = new(
        @"(?<sh>\d{2}):(?<sm>\d{2}):(?<ss>\d{2})[,.](?<sms>\d{3})\s*-->\s*" +
        @"(?<eh>\d{2}):(?<em>\d{2}):(?<es>\d{2})[,.](?<ems>\d{3})",
        RegexOptions.Compiled);

    public static IReadOnlyList<SubtitleCue> ParseSrt(string? srtPath)
    {
        if (string.IsNullOrWhiteSpace(srtPath) || !File.Exists(srtPath))
            return Array.Empty<SubtitleCue>();

        var lines = File.ReadAllLines(srtPath);
        var cues = new List<SubtitleCue>();

        for (var i = 0; i < lines.Length; i++)
        {
            var m = TimeRegex.Match(lines[i].Trim());
            if (!m.Success)
                continue;

            var start = ToSeconds(m, "s");
            var end = ToSeconds(m, "e");
            var text = new StringBuilder();

            i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(Regex.Replace(lines[i].Trim(), "<.*?>", ""));
                i++;
            }

            var clean = Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(clean))
                cues.Add(new SubtitleCue(start, end, clean));
        }

        return cues;
    }

    public static IReadOnlyList<ImportantMomentContext> BuildMomentContexts(
        IReadOnlyList<ImportantMark> marks,
        string? srtPath)
    {
        var cues = ParseSrt(srtPath);
        return marks
            .OrderBy(m => m.OffsetSeconds)
            .Select(mark =>
            {
                var cue = FindNearestCue(mark.OffsetSeconds, cues);
                return new ImportantMomentContext(
                    mark,
                    mark.Timestamp,
                    cue?.Text ?? "Transcript context is not available for this moment yet.",
                    cue?.StartSeconds ?? mark.OffsetSeconds,
                    cue?.EndSeconds ?? mark.OffsetSeconds);
            })
            .ToList();
    }

    public static string BuildAnnotatedTranscript(
        string fallbackTranscript,
        IReadOnlyList<ImportantMark> marks,
        string? srtPath,
        bool usedArabicRetry)
    {
        var cues = ParseSrt(srtPath);
        var contexts = BuildMomentContexts(marks, srtPath);
        var sb = new StringBuilder();

        if (contexts.Count > 0)
        {
            // Keep important moments inline with the transcript evidence. Older builds
            // duplicated the same phrase in a summary block and again at the marked cue.
            sb.AppendLine(
                $"IMPORTANT MOMENTS / اللحظات المهمة • {contexts.Count} " +
                (contexts.Count == 1 ? "marker" : "markers") +
                " linked inline below");
            sb.AppendLine("────────────────────────────────────────");
            sb.AppendLine();
        }

        if (cues.Count == 0)
        {
            sb.AppendLine(fallbackTranscript.Trim());
        }
        else
        {
            // Put each mark immediately before the transcribed phrase that owns/nearest to that time.
            var marksByCue = new Dictionary<int, List<ImportantMomentContext>>();

            foreach (var moment in contexts)
            {
                var cueIndex = FindCueIndex(moment.Mark.OffsetSeconds, cues);
                if (cueIndex < 0) continue;

                if (!marksByCue.TryGetValue(cueIndex, out var bucket))
                {
                    bucket = new List<ImportantMomentContext>();
                    marksByCue[cueIndex] = bucket;
                }
                bucket.Add(moment);
            }

            for (var i = 0; i < cues.Count; i++)
            {
                if (marksByCue.TryGetValue(i, out var cueMarks))
                {
                    foreach (var mark in cueMarks.OrderBy(x => x.Mark.OffsetSeconds))
                        sb.AppendLine($"📍 [{mark.Timestamp}] IMPORTANT MOMENT / لحظة مهمة");
                }

                sb.AppendLine(cues[i].Text);
                sb.AppendLine();
            }
        }

        if (usedArabicRetry)
        {
            sb.AppendLine();
            sb.AppendLine("Language recovery: Arabic-script retry applied automatically.");
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static SubtitleCue? FindNearestCue(double seconds, IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count == 0) return null;

        foreach (var cue in cues)
        {
            if (seconds >= cue.StartSeconds - 0.75 && seconds <= cue.EndSeconds + 0.75)
                return cue;
        }

        return cues
            .OrderBy(c => DistanceToCue(seconds, c))
            .FirstOrDefault(c => DistanceToCue(seconds, c) <= 4.0);
    }

    private static int FindCueIndex(double seconds, IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count == 0) return -1;

        for (var i = 0; i < cues.Count; i++)
        {
            if (seconds >= cues[i].StartSeconds - 0.75 &&
                seconds <= cues[i].EndSeconds + 0.75)
                return i;
        }

        var candidate = Enumerable.Range(0, cues.Count)
            .Select(i => new { Index = i, Distance = DistanceToCue(seconds, cues[i]) })
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        return candidate is not null && candidate.Distance <= 4.0
            ? candidate.Index
            : -1;
    }

    private static double DistanceToCue(double seconds, SubtitleCue cue)
    {
        if (seconds < cue.StartSeconds) return cue.StartSeconds - seconds;
        if (seconds > cue.EndSeconds) return seconds - cue.EndSeconds;
        return 0;
    }

    private static double ToSeconds(Match m, string prefix)
    {
        var h = int.Parse(m.Groups[prefix + "h"].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[prefix + "m"].Value, CultureInfo.InvariantCulture);
        var sec = int.Parse(m.Groups[prefix + "s"].Value, CultureInfo.InvariantCulture);
        var ms = int.Parse(m.Groups[prefix + "ms"].Value, CultureInfo.InvariantCulture);
        return h * 3600 + min * 60 + sec + ms / 1000.0;
    }
}
