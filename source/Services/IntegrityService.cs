using System.Security.Cryptography;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public static class IntegrityService
{
    public static void Write(MeetingRecord meeting)
    {
        var lines = new List<string>();
        foreach (var file in new[]
        {
            meeting.RecordingPath,
            meeting.AudioPath,
            meeting.TranscriptPath,
            meeting.SrtPath
        }.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(stream));
            lines.Add($"{hash}  {Path.GetFileName(file)}");
        }

        var path = Path.Combine(meeting.FolderPath, "07_SHA256_Integrity.txt");
        var temp = path + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, path, true);
    }
}
