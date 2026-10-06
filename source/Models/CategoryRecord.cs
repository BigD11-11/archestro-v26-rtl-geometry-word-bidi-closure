namespace Archestro.MeetingVault.Models;

public sealed class CategoryRecord
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7B8798";
    public int SortOrder { get; set; }
    public int MeetingCount { get; set; }
    public bool Pinned { get; set; }

    public string Label => MeetingCount > 0 ? $"{Name}  {MeetingCount}" : Name;
    public string CountText => MeetingCount == 1 ? "1 meeting" : $"{MeetingCount} meetings";
    public string HomeStateLabel => Pinned ? "Visible on Home" : "Under More";
    public string PinActionLabel => Pinned ? "Move to More" : "Show on Home";
}
