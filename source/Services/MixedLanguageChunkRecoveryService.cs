using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed record MixedLanguageRecoveryResult(
    bool Success,
    string Text,
    string SrtPath,
    int ChunkCount,
    bool ContainsArabic,
    bool ContainsEnglish);

public sealed class MixedLanguageChunkRecoveryService
{
    private readonly AppSettings _settings;

    public MixedLanguageChunkRecoveryService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<MixedLanguageRecoveryResult> RecoverAsync(
        MeetingRecord meeting,
        string workRoot,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(meeting.AudioPath) ||
            !File.Exists(meeting.AudioPath) ||
            string.IsNullOrWhiteSpace(_settings.FfmpegExe) ||
            !File.Exists(_settings.FfmpegExe) ||
            string.IsNullOrWhiteSpace(_settings.BuzzExe) ||
            !File.Exists(_settings.BuzzExe))
        {
            return Failed;
        }

        var chunkSeconds = ChooseChunkSeconds(meeting.DurationSeconds);
        var chunkRoot = Path.Combine(workRoot, "MIXED_CHUNK_RECOVERY");
        var audioDir = Path.Combine(chunkRoot, "audio");
        var outputDir = Path.Combine(chunkRoot, "output");

        TryResetDirectory(chunkRoot);
        Directory.CreateDirectory(audioDir);
        Directory.CreateDirectory(outputDir);

        var chunkPattern = Path.Combine(audioDir, "chunk_%05d.wav");
        var ffLog = Path.Combine(
            AppPaths.Logs,
            $"mixed_chunks_ffmpeg_{meeting.Id[..Math.Min(8, meeting.Id.Length)]}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        var ff = await ProcessService.RunDetailedAsync(
            _settings.FfmpegExe,
            new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", meeting.AudioPath,
                "-vn",
                "-ac", "1",
                "-ar", "16000",
                "-f", "segment",
                "-segment_time", chunkSeconds.ToString(CultureInfo.InvariantCulture),
                "-reset_timestamps", "1",
                chunkPattern
            },
            ffLog,
            audioDir,
            ct);

        if (ff.ExitCode != 0)
            return Failed;

        var chunks = Directory.EnumerateFiles(audioDir, "chunk_*.wav")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chunks.Count == 0)
            return Failed;

        // Keep Windows command lines comfortably below the practical limit.
        const int BatchSize = 30;

        for (var offset = 0; offset < chunks.Count; offset += BatchSize)
        {
            var batch = chunks.Skip(offset).Take(BatchSize).ToList();
            var args = new List<string>
            {
                "add",
                "--task", "transcribe",
                "--model-type", _settings.BuzzModelType,
                "--model-size", _settings.BuzzModelSize,
                "--txt",
                "--srt",
                "--hide-gui",
                "--output-directory", outputDir
            };

            // Deliberately omit --language. Each temporary audio file gets its
            // own automatic language detection inside Buzz/Whisper.
            args.AddRange(batch);

            var log = Path.Combine(
                AppPaths.Logs,
                $"buzz_mixed_chunks_{meeting.Id[..Math.Min(8, meeting.Id.Length)]}_{offset:0000}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            var run = await ProcessService.RunDetailedAsync(
                _settings.BuzzExe,
                args,
                log,
                outputDir,
                ct);

            if (run.ExitCode != 0)
                return Failed;

            // Buzz may finish export slightly after the process path reports done.
            await WaitForBatchOutputsAsync(batch, outputDir, TimeSpan.FromSeconds(25), ct);
        }

        var mergedCues = new List<SubtitleCue>();
        var textBuilder = new StringBuilder();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var stem = Path.GetFileNameWithoutExtension(chunk);

            var srt = FindOutputByStem(outputDir, stem, ".srt");
            var txt = FindOutputByStem(outputDir, stem, ".txt");

            IReadOnlyList<SubtitleCue> localCues = Array.Empty<SubtitleCue>();
            string localText = "";

            if (!string.IsNullOrWhiteSpace(srt) && File.Exists(srt))
            {
                localCues = TranscriptContextService.ParseSrt(srt);
                localText = string.Join(
                    Environment.NewLine,
                    localCues.Select(c => c.Text)
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
            }

            if (string.IsNullOrWhiteSpace(localText) &&
                !string.IsNullOrWhiteSpace(txt) &&
                File.Exists(txt))
            {
                localText = (await File.ReadAllTextAsync(txt, ct)).Trim();
            }

            if (string.IsNullOrWhiteSpace(localText))
                continue;

            var baseSeconds = i * chunkSeconds;

            if (localCues.Count > 0)
            {
                foreach (var cue in localCues)
                {
                    mergedCues.Add(new SubtitleCue(
                        cue.StartSeconds + baseSeconds,
                        cue.EndSeconds + baseSeconds,
                        cue.Text));
                }
            }
            else
            {
                // TXT-only fallback: place the chunk text across its chunk window.
                mergedCues.Add(new SubtitleCue(
                    baseSeconds,
                    Math.Min(
                        Math.Max(baseSeconds + 0.5, baseSeconds + chunkSeconds),
                        Math.Max(baseSeconds + chunkSeconds, meeting.DurationSeconds)),
                    localText));
            }

            if (textBuilder.Length > 0)
                textBuilder.AppendLine();

            textBuilder.Append(localText);
        }

        if (mergedCues.Count == 0 || string.IsNullOrWhiteSpace(textBuilder.ToString()))
            return Failed;

        var mergedSrt = Path.Combine(chunkRoot, "MIXED_AUTO_CHUNKED.srt");
        WriteSrt(mergedCues, mergedSrt);

        var mergedText = textBuilder.ToString().Trim();
        var hasArabic = ContainsArabic(mergedText);
        var hasEnglish = HasStrongEnglishEvidence(mergedText);

        return new MixedLanguageRecoveryResult(
            true,
            mergedText,
            mergedSrt,
            chunks.Count,
            hasArabic,
            hasEnglish);
    }

    private static int ChooseChunkSeconds(int durationSeconds)
    {
        // QUALITY-FIRST: keep language re-detection windows short.
        // Processing may take longer, but Arabic/English code-switching gets
        // more opportunities to be recognized correctly.
        if (durationSeconds <= 30 * 60)
            return 10;

        return 15;
    }

    private static async Task WaitForBatchOutputsAsync(
        IReadOnlyList<string> batch,
        string outputDir,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now <= deadline)
        {
            var completed = 0;

            foreach (var chunk in batch)
            {
                var stem = Path.GetFileNameWithoutExtension(chunk);
                if (FindOutputByStem(outputDir, stem, ".srt") is not null ||
                    FindOutputByStem(outputDir, stem, ".txt") is not null)
                {
                    completed++;
                }
            }

            if (completed >= batch.Count)
                return;

            await Task.Delay(500, ct);
        }
    }

    private static string? FindOutputByStem(
        string outputDir,
        string stem,
        string extension)
    {
        try
        {
            return Directory.EnumerateFiles(
                    outputDir,
                    "*" + extension,
                    SearchOption.AllDirectories)
                .Where(p =>
                    Path.GetFileNameWithoutExtension(p)
                        .Contains(stem, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static bool ContainsArabic(string text) =>
        Regex.IsMatch(text ?? "", @"[\u0600-\u06FF]");

    public static bool HasStrongEnglishEvidence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var commonEnglish = new HashSet<string>(
            new[]
            {
                "a","about","all","and","are","be","can","client","company",
                "contract","do","english","for","from","good","have","hello",
                "how","i","in","is","it","meeting","need","of","on","or",
                "please","project","record","recording","so","test","that",
                "the","this","time","to","we","what","when","will","with",
                "work","working","yes","you","your"
            },
            StringComparer.OrdinalIgnoreCase);

        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value)
            .Where(v => v.Length > 1)
            .ToList();

        if (tokens.Count < 2)
            return false;

        var hits = tokens.Count(commonEnglish.Contains);

        if (tokens.Count <= 7)
            return hits >= 2;

        return hits / (double)tokens.Count >= 0.32;
    }

    private static void WriteSrt(
        IReadOnlyList<SubtitleCue> cues,
        string path)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < cues.Count; i++)
        {
            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(
                $"{FormatSrt(cues[i].StartSeconds)} --> {FormatSrt(cues[i].EndSeconds)}");
            sb.AppendLine(cues[i].Text.Trim());
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string FormatSrt(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);

        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    private static void TryResetDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }

        Directory.CreateDirectory(path);
    }

    private static readonly MixedLanguageRecoveryResult Failed =
        new(false, "", "", 0, false, false);
}
