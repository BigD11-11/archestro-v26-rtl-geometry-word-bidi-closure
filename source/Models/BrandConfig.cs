namespace Archestro.MeetingVault.Models;

public sealed class BrandConfig
{
    public string ProductName { get; set; } = "Archestro Meeting Vault";
    public string ProductSubtitle { get; set; } = "Private Offline Meeting Intelligence";
    public string CompanyName { get; set; } = "Archestro";
    public string WindowTitle { get; set; } = "Archestro Meeting Vault";
    public string Footer { get; set; } = "© {year} Archestro. All rights reserved.";
    public string FullLogo { get; set; } = "Assets/ArchestroLogoFull.png";
    public string Symbol { get; set; } = "Assets/ArchestroSymbol.png";
    public string Watermark { get; set; } = "Assets/ArchestroWatermark.png";
    public string DefaultAppearance { get; set; } = "System";
    public bool AllowCustomerBranding { get; set; } = true;
}
