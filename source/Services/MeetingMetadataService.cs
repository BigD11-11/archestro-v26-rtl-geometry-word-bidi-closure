using System.Text.Json;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public static class MeetingMetadataService
{
    public static string MetadataPath(MeetingRecord meeting) =>
        Path.Combine(meeting.FolderPath, "05_Meeting_Metadata.json");

    public static void Write(MeetingRecord meeting)
    {
        if (string.IsNullOrWhiteSpace(meeting.FolderPath)) return;
        Directory.CreateDirectory(meeting.FolderPath);

        var data = new
        {
            Schema = "EMV-R3.2",
            meeting.Id,
            MeetingName = meeting.Title,
            meeting.HasExplicitTitle,
            meeting.SuggestedTitle,
            StartLocal = meeting.StartLocal.ToString("o"),
            EndLocal = meeting.EndLocal?.ToString("o"),
            meeting.DurationSeconds,
            meeting.RecordingPath,
            meeting.AudioPath,
            meeting.TranscriptPath,
            meeting.SrtPath,
            meeting.TranscriptionStatus,
            meeting.Notes,
            meeting.Category,
            meeting.CategoryColor,
            meeting.MarksCount,
            UpdatedLocal = DateTimeOffset.Now.ToString("o")
        };

        var path = MetadataPath(meeting);
        var temp = path + ".tmp";
        File.WriteAllText(temp,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    public static void WriteMarks(MeetingRecord meeting, IReadOnlyList<ImportantMark> marks)
    {
        var path = Path.Combine(meeting.FolderPath, "06_Important_Marks.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp,
            JsonSerializer.Serialize(marks, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
