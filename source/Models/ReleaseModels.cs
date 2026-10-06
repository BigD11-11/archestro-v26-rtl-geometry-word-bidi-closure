namespace Archestro.MeetingVault.Models;

public enum ProductEdition
{
    Core,
    Intelligence
}

public sealed class LocalLicense
{
    public string LicenseId { get; set; } = "";
    public string Customer { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public ProductEdition Edition { get; set; } = ProductEdition.Core;
    public DateTimeOffset PurchasedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset MaintenanceUntil { get; set; } = DateTimeOffset.Now.AddMonths(6);
    public int Seats { get; set; } = 1;
    public bool BrandingEntitlement { get; set; }
    public bool Evaluation { get; set; }
    public DateTimeOffset? EvaluationUntil { get; set; }
}

public sealed class ReleaseManifest
{
    public string Product { get; set; } = "Archestro Meeting Vault";
    public string Version { get; set; } = "1.0.0";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<string, ReleasePackage> Packages { get; set; } = new();
}

public sealed class ReleasePackage
{
    public string Edition { get; set; } = "Core";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Url { get; set; } = "";
}
