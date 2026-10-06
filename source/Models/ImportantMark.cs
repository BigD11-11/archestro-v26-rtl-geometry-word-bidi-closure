namespace Archestro.MeetingVault.Models;

public sealed class ImportantMark
{
    public long Id { get; set; }
    public string MeetingId { get; set; } = "";
    public int OffsetSeconds { get; set; }
    public DateTimeOffset CreatedLocal { get; set; }

    public string Timestamp => TimeSpan.FromSeconds(Math.Max(0, OffsetSeconds)).ToString(@"hh\:mm\:ss");
}
