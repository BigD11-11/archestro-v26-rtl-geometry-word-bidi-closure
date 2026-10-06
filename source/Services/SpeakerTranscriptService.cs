using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class SpeakerTranscriptService
{
    private static readonly Regex TimeRegex = new(
        @"(?<s>\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(?<e>\d{2}:\d{2}:\d{2},\d{3})",
        RegexOptions.Compiled);

    public List<SpeakerTranscriptLine> Align(
        string srtPath,
        IReadOnlyList<SpeakerSegment> segments,
        SpeakerAnalysisResult result)
    {
        if (!File.Exists(srtPath))
            throw new FileNotFoundException("Timed transcript (.srt) is required for speaker alignment.", srtPath);

        var blocks = Regex.Split(File.ReadAllText(srtPath), @"\r?\n\r?\n");
        var lines = new List<SpeakerTranscriptLine>();
        var index = 0;

        foreach (var block in blocks)
        {
            var raw = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var timeLineIndex = Array.FindIndex(raw, x => TimeRegex.IsMatch(x));
            if (timeLineIndex < 0) continue;

            var m = TimeRegex.Match(raw[timeLineIndex]);
            var start = ParseSrtTime(m.Groups["s"].Value);
            var end = ParseSrtTime(m.Groups["e"].Value);
            var text = string.Join(" ", raw.Skip(timeLineIndex + 1))
                .Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var speaker = PickSpeaker(start, end, segments);
            lines.Add(new SpeakerTranscriptLine
            {
                Index = ++index,
                StartSeconds = start,
                EndSeconds = end,
                SpeakerIndex = speaker,
                SpeakerName = result.DisplayNameFor(speaker),
                SpeakerColor = result.ColorFor(speaker),
                Text = text
            });
        }

        return lines;
    }

    public void Save(MeetingRecord meeting, SpeakerAnalysisResult result)
    {
        Directory.CreateDirectory(meeting.FolderPath);

        var json = Path.Combine(meeting.FolderPath, "08_Speaker_Diarization.json");
        var transcriptJson = Path.Combine(meeting.FolderPath, "09_Speaker_Transcript.json");
        var transcriptTxt = Path.Combine(meeting.FolderPath, "10_Speaker_Transcript.txt");

        File.WriteAllText(json, JsonSerializer.Serialize(result, JsonOptions()), Encoding.UTF8);
        File.WriteAllText(transcriptJson, JsonSerializer.Serialize(result.Lines, JsonOptions()), Encoding.UTF8);

        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            sb.Append('[')
              .Append(TimeSpan.FromSeconds(line.StartSeconds).ToString(@"hh\:mm\:ss"))
              .Append("] ")
              .Append(line.SpeakerName)
              .AppendLine();
            sb.AppendLine(line.Text);
            sb.AppendLine();
        }
        File.WriteAllText(transcriptTxt, sb.ToString(), Encoding.UTF8);
    }

    public SpeakerAnalysisResult? Load(MeetingRecord meeting)
    {
        var path = Path.Combine(meeting.FolderPath, "08_Speaker_Diarization.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SpeakerAnalysisResult>(
                File.ReadAllText(path), JsonOptions());
        }
        catch
        {
            return null;
        }
    }

    public void RefreshNames(SpeakerAnalysisResult result)
    {
        foreach (var line in result.Lines)
        {
            line.SpeakerName = result.DisplayNameFor(line.SpeakerIndex);
            line.SpeakerColor = result.ColorFor(line.SpeakerIndex);
        }
    }

    private static int PickSpeaker(
        double start,
        double end,
        IReadOnlyList<SpeakerSegment> segments)
    {
        var bestSpeaker = 0;
        var bestOverlap = -1.0;

        foreach (var seg in segments)
        {
            var overlap = Math.Max(0, Math.Min(end, seg.EndSeconds) - Math.Max(start, seg.StartSeconds));
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestSpeaker = seg.SpeakerIndex;
            }
        }

        if (bestOverlap > 0) return bestSpeaker;

        var midpoint = (start + end) / 2.0;
        return segments
            .OrderBy(x => Math.Abs(((x.StartSeconds + x.EndSeconds) / 2.0) - midpoint))
            .Select(x => x.SpeakerIndex)
            .FirstOrDefault();
    }

    private static double ParseSrtTime(string text)
    {
        if (!TimeSpan.TryParseExact(
            text,
            @"hh\:mm\:ss\,fff",
            CultureInfo.InvariantCulture,
            out var ts))
            return 0;
        return ts.TotalSeconds;
    }

    private static JsonSerializerOptions JsonOptions() =>
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
