using System.Text.Json;
using Archestro.MeetingVault.Models;
using NAudio.Wave;

namespace Archestro.MeetingVault.Services;

public sealed class MeetingImportService
{
    private readonly MeetingRepository _repository;

    public MeetingImportService(MeetingRepository repository) => _repository = repository;

    public int ImportExisting()
    {
        if (!Directory.Exists(AppPaths.Meetings)) return 0;
        var count = 0;

        foreach (var metaPath in Directory.EnumerateFiles(AppPaths.Meetings, "05_Meeting_Metadata.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                var folder = Path.GetDirectoryName(metaPath)!;

                var id = GetString(root, "Id");
                if (string.IsNullOrWhiteSpace(id)) id = DeterministicId(folder);

                var start = TryDate(root, "StartLocal") ?? new DateTimeOffset(File.GetCreationTime(metaPath));
                var end = TryDate(root, "EndLocal");
                var title = GetString(root, "MeetingName");
                var explicitTitle = TryBool(root, "HasExplicitTitle") ??
                    (!string.IsNullOrWhiteSpace(title) && !title.Equals("Meeting", StringComparison.OrdinalIgnoreCase));
                var suggestedTitle = GetString(root, "SuggestedTitle");
                var notes = GetString(root, "Notes");
                var category = GetString(root, "Category");
                if (string.IsNullOrWhiteSpace(category)) category = "Uncategorized";
                var color = GetString(root, "CategoryColor");
                if (string.IsNullOrWhiteSpace(color)) color = _repository.GetCategoryColor(category);
                var status = GetString(root, "TranscriptionStatus");
                var duration = TryInt(root, "DurationSeconds") ?? 0;
                var marksCount = TryInt(root, "MarksCount") ?? CountMarks(folder);

                var recording = Directory.EnumerateFiles(folder, "01_Original_Capture.*").FirstOrDefault() ?? GetString(root, "RecordingPath");
                var audio = Path.Combine(folder, "02_Meeting_Audio.m4a");
                var transcript = Path.Combine(folder, "03_Transcript.txt");
                var srt = Path.Combine(folder, "04_Transcript.srt");

                var transcriptExists = File.Exists(transcript);
                if (string.IsNullOrWhiteSpace(status))
                    status = transcriptExists ? "Transcript ready" : "Saved";

                var record = new MeetingRecord
                {
                    Id = id,
                    FolderPath = folder,
                    Title = title,
                    HasExplicitTitle = explicitTitle,
                    SuggestedTitle = suggestedTitle,
                    StartLocal = start,
                    EndLocal = end,
                    DurationSeconds = duration,
                    RecordingPath = File.Exists(recording) ? recording : "",
                    AudioPath = File.Exists(audio) ? audio : "",
                    TranscriptPath = transcriptExists ? transcript : "",
                    SrtPath = File.Exists(srt) ? srt : "",
                    TranscriptionStatus = status,
                    Notes = notes,
                    Category = category,
                    CategoryColor = color,
                    MarksCount = marksCount
                };

                var transcriptText = transcriptExists ? File.ReadAllText(transcript) : "";
                if (!string.IsNullOrWhiteSpace(transcriptText))
                {
                    var suggested = new SuggestedMetadataService().Suggest(transcriptText);
                    if (string.IsNullOrWhiteSpace(record.SuggestedTitle))
                        record.SuggestedTitle = suggested.SuggestedTitle;
                    // Preserve imported/user category authority. Suggested metadata may propose
                    // a title, but category assignment remains an explicit user action.
                }

                _repository.Upsert(record, transcriptText);
                count++;
            }
            catch
            {
                // One damaged legacy meeting must not block the rest of the library.
            }
        }
        return count;
    }

    public async Task<MeetingRecord> ImportAudioFileAsync(
        string sourcePath,
        AppSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The recording file was not found.", sourcePath);

        var title = Path.GetFileNameWithoutExtension(sourcePath).Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "Imported recording";
        var start = DateTimeOffset.Now;
        var folder = AppPaths.CreateMeetingFolder(title, start);
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".audio";

        var original = Path.Combine(folder, "01_Original_Capture" + extension.ToLowerInvariant());
        var audio = Path.Combine(folder, "02_Meeting_Audio.m4a");
        var ffLog = Path.Combine(AppPaths.Logs, $"ffmpeg_import_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        try
        {
            progress?.Report($"Importing locally • {Path.GetFileName(sourcePath)}");
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(original, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, cancellationToken);

            if (!File.Exists(original) || new FileInfo(original).Length == 0)
                throw new IOException("The source recording could not be copied into the local Vault.");

            progress?.Report("Preparing a local working audio copy…");
            var ffmpeg = settings.FfmpegExe;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
                throw new InvalidOperationException("The local audio converter is not available.");

            var code = await ProcessService.RunAsync(
                ffmpeg,
                new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", original,
                    "-vn",
                    "-ac", "1",
                    "-ar", "16000",
                    "-c:a", "aac",
                    "-b:a", "128k",
                    audio
                },
                ffLog,
                cancellationToken);

            if (code != 0 || !File.Exists(audio) || new FileInfo(audio).Length < 256)
                throw new InvalidOperationException("The recording was copied, but the local working audio could not be prepared.");

            var durationSeconds = 0;
            try
            {
                using var reader = new MediaFoundationReader(audio);
                durationSeconds = (int)Math.Max(0, Math.Round(reader.TotalTime.TotalSeconds));
            }
            catch { }

            var meeting = new MeetingRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                FolderPath = folder,
                Title = title,
                HasExplicitTitle = true,
                StartLocal = start,
                EndLocal = durationSeconds > 0 ? start.AddSeconds(durationSeconds) : start,
                DurationSeconds = durationSeconds,
                RecordingPath = original,
                AudioPath = audio,
                TranscriptPath = Path.Combine(folder, "03_Transcript.txt"),
                SrtPath = Path.Combine(folder, "04_Transcript.srt"),
                TranscriptionStatus = "Saved • transcript on demand",
                Notes = "Imported local recording",
                Category = "Uncategorized",
                CategoryColor = CategoryCatalog.ColorFor("Uncategorized"),
                MarksCount = 0
            };

            MeetingMetadataService.Write(meeting);
            MeetingMetadataService.WriteMarks(meeting, Array.Empty<ImportantMark>());
            IntegrityService.Write(meeting);
            _repository.Upsert(meeting);

            progress?.Report("Imported • ready to transcribe");
            return meeting;
        }
        catch
        {
            // Keep owner data clean if import fails before the meeting becomes a valid Vault record.
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch { }
            throw;
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static int? TryInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? value : null;

    private static bool? TryBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean()
            : null;

    private static DateTimeOffset? TryDate(JsonElement root, string name)
    {
        var raw = GetString(root, name);
        return DateTimeOffset.TryParse(raw, out var value) ? value : null;
    }

    private static int CountMarks(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "06_Important_Marks.json");
            if (!File.Exists(path)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    private static string DeterministicId(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }
}
