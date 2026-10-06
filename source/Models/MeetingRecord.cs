namespace Archestro.MeetingVault.Models;

public sealed class MeetingRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FolderPath { get; set; } = "";
    public string Title { get; set; } = "";
    public bool HasExplicitTitle { get; set; }
    public string SuggestedTitle { get; set; } = "";
    public DateTimeOffset StartLocal { get; set; }
    public DateTimeOffset? EndLocal { get; set; }
    public int DurationSeconds { get; set; }
    public string RecordingPath { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
    public string SrtPath { get; set; } = "";
    public string TranscriptionStatus { get; set; } = "Saved";
    public string Notes { get; set; } = "";
    public string Category { get; set; } = "Uncategorized";
    public string CategoryColor { get; set; } = "#7B8798";
    public int MarksCount { get; set; }

    public string PrimaryTitle =>
        HasExplicitTitle && !string.IsNullOrWhiteSpace(Title)
            ? Title
            : StartLocal.ToString("dd MMM yyyy • hh:mm tt");

    public string SuggestedLine =>
        !HasExplicitTitle && !string.IsNullOrWhiteSpace(SuggestedTitle)
            ? $"✨ Suggested: {SuggestedTitle}"
            : "";

    public string AgeText
    {
        get
        {
            var span = DateTimeOffset.Now - StartLocal;
            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
            if (span.TotalHours < 48) return "Yesterday";
            return $"{(int)span.TotalDays} days ago";
        }
    }

    public string StatusDisplay =>
        Services.TranscriptionProgressService.GetDisplay(Id, TranscriptionStatus);

    public string StatusPrimaryDisplay =>
        Services.TranscriptionProgressService.GetPrimaryLine(Id, TranscriptionStatus);

    public string StatusSecondaryDisplay =>
        Services.TranscriptionProgressService.GetSecondaryLine(Id, TranscriptionStatus);

    public bool ShowTranscriptAction
    {
        get
        {
            var status = Services.TranscriptionProgressService.GetDisplay(
                Id,
                TranscriptionStatus ?? "");

            if (status.Contains("ready", StringComparison.OrdinalIgnoreCase))
                return false;

            return !string.IsNullOrWhiteSpace(AudioPath);
        }
    }

    public string TranscriptActionLabel
    {
        get
        {
            var status = Services.TranscriptionProgressService.GetDisplay(
                Id,
                TranscriptionStatus ?? "");

            if (status.Contains("Transcribing", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Pass ", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("repair", StringComparison.OrdinalIgnoreCase))
                return "⚡ Active";

            if (status.Contains("Queued", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("waiting", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Manual start", StringComparison.OrdinalIgnoreCase))
                return "⚡ Prioritize";

            return "⚡ Transcribe Now";
        }
    }

    public bool CanTranscribeNow
    {
        get
        {
            var status = Services.TranscriptionProgressService.GetDisplay(Id, TranscriptionStatus ?? "");

            if (status.Contains(
                    "waiting for current transcript",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "next after current transcript",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "Transcribing",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "Pass ",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "recovery",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "repair",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "ready",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return
                status.Contains(
                    "Queued",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "waiting for lighter system load",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "interrupted",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "canceled",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "on demand",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "Manual start",
                    StringComparison.OrdinalIgnoreCase) ||
                status.Contains(
                    "waiting for memory",
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    public string DurationText => TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString(@"hh\:mm\:ss");
    public string DateLine => $"{StartLocal:dd MMM yyyy, hh:mm tt}  •  {AgeText}";
    public string MetaLine => $"{StartLocal:dd MMM yyyy, hh:mm tt}  •  {AgeText}  •  {DurationText}";
}
