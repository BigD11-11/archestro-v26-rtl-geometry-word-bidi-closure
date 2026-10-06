using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace Archestro.MeetingVault.Services;

public static class AppearanceService
{
    public static string RequestedTheme { get; private set; } = "System";
    public static string ResolvedTheme { get; private set; } = "Dark";
    public static string CurrentLanguage { get; private set; } = "English";

    private static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["AppBackground"]="#0A1018", ["PageBrush"]="#0A1018", ["PanelBrush"]="#F2111822",
        ["PanelBrush2"]="#F516202C", ["SurfaceBrush"]="#FF182434", ["SurfaceHoverBrush"]="#FF213247",
        ["InputBrush"]="#FF111B28", ["MenuBrush"]="#FF151F2C", ["BorderBrush"]="#344559",
        ["PrimaryTextBrush"]="#F3F5F7", ["SecondaryTextBrush"]="#A1ADBA", ["IconBrush"]="#D6DEE7",
        ["DisabledBrush"]="#687584", ["RecorderFaceBrush"]="#182534", ["RecorderPrimaryBrush"]="#F5F7FA",
        ["RecorderSecondaryBrush"]="#AAB6C3", ["StatusBrush"]="#D3A45E", ["WatermarkBrush"]="#263341",
        ["MutedBrush"]="#8C99A7", ["BlueBrush"]="#8C78BF", ["GreenBrush"]="#4FA97D",
        ["AmberBrush"]="#D3A45E", ["RedBrush"]="#E56A70", ["SidebarBrush"]="#F50D151F",
        ["TitleBarBrush"]="#FF0A1119", ["ChromeBrush"]="#FF0D151F", ["ChromeBorderBrush"]="#3B4654",
        ["PrimaryActionBrush"]="#8068B1", ["PrimaryActionBorderBrush"]="#9B86C3",
        ["PrimaryActionTextBrush"]="#F7F9FB", ["FocusBrush"]="#9B86C3",
        ["AccentSoftBrush"]="#2A213B", ["AccentSoftBorderBrush"]="#665780",
        ["AccentTextBrush"]="#B6A7D6", ["MenuHoverBrush"]="#262037", ["RecorderRingBrush"]="#8C78BF",
        ["RecordingAccentBrush"]="#E56A70", ["WarningAccentBrush"]="#D3A45E",
        ["LogoPlateBrush"]="#FF10243A", ["LogoPlateBorderBrush"]="#D2A04F",
        ["ChatUserBrush"]="#2A213B", ["ChatAssistantBrush"]="#121C28", ["ChatEvidenceBrush"]="#182536",
        ["DangerSoftBrush"]="#2B1820", ["DangerBorderBrush"]="#75404B", ["DangerTextBrush"]="#FF8589",
        ["InputPlaceholderBrush"]="#7F8D9C", ["ScrollTrackBrush"]="#0E1722", ["ScrollThumbBrush"]="#40536B",
        ["ScrollThumbHoverBrush"]="#607A98", ["EvidenceCardBrush"]="#121C28", ["EvidenceRowBrush"]="#172434"
    };

    private static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["AppBackground"]="#ECE9E5", ["PageBrush"]="#ECE9E5", ["PanelBrush"]="#FFF7F5F2",
        ["PanelBrush2"]="#FFF1EEEA", ["SurfaceBrush"]="#FFE8E3DE", ["SurfaceHoverBrush"]="#FFDED7D1",
        ["InputBrush"]="#FFFBF9F6", ["MenuBrush"]="#FFF7F4F0", ["BorderBrush"]="#C8BFB5",
        ["PrimaryTextBrush"]="#242A31", ["SecondaryTextBrush"]="#65717C", ["IconBrush"]="#414A54",
        ["DisabledBrush"]="#A39C96", ["RecorderFaceBrush"]="#E7E2DD", ["RecorderPrimaryBrush"]="#283039",
        ["RecorderSecondaryBrush"]="#69747D", ["StatusBrush"]="#8F713B", ["WatermarkBrush"]="#CEC6BE",
        ["MutedBrush"]="#747D85", ["BlueBrush"]="#7A5FA6", ["GreenBrush"]="#3F8B68",
        ["AmberBrush"]="#9A743A", ["RedBrush"]="#C95B61", ["SidebarBrush"]="#FFF5F1EC",
        ["TitleBarBrush"]="#FFF1EDE8", ["ChromeBrush"]="#FFF6F2ED", ["ChromeBorderBrush"]="#C8BFB5",
        ["PrimaryActionBrush"]="#8F713B", ["PrimaryActionBorderBrush"]="#B28E53",
        ["PrimaryActionTextBrush"]="#FFFFFF", ["FocusBrush"]="#B28E53",
        ["AccentSoftBrush"]="#F2E7D5", ["AccentSoftBorderBrush"]="#D2B888",
        ["AccentTextBrush"]="#7A5B2B", ["MenuHoverBrush"]="#E8DED2", ["RecorderRingBrush"]="#9A743A",
        ["RecordingAccentBrush"]="#C45D65", ["WarningAccentBrush"]="#8F713B",
        ["LogoPlateBrush"]="#FF10243A", ["LogoPlateBorderBrush"]="#D2A04F",
        ["ChatUserBrush"]="#F1E6D5", ["ChatAssistantBrush"]="#FFF9F5F1", ["ChatEvidenceBrush"]="#EEE8E2",
        ["DangerSoftBrush"]="#F8E2E2", ["DangerBorderBrush"]="#D8A0A4", ["DangerTextBrush"]="#9E3F47",
        ["InputPlaceholderBrush"]="#7B756F", ["ScrollTrackBrush"]="#E3DDD6", ["ScrollThumbBrush"]="#A89D91",
        ["ScrollThumbHoverBrush"]="#8C7E70", ["EvidenceCardBrush"]="#FFF9F5F1", ["EvidenceRowBrush"]="#F3EEE9"
    };

    public static void Configure(string? requestedTheme, string? language)
    {
        RequestedTheme = NormalizeRequested(requestedTheme);
        ResolvedTheme = Resolve(RequestedTheme);
        CurrentLanguage = string.Equals(language, "Arabic", StringComparison.OrdinalIgnoreCase) ? "Arabic" : "English";
        ApplyResources(Application.Current?.Resources, RequestedTheme, ResolvedTheme);
    }

    public static void SetLanguage(string? language)
    {
        CurrentLanguage = string.Equals(language, "Arabic", StringComparison.OrdinalIgnoreCase) ? "Arabic" : "English";
    }

    public static string Resolve(string? requested)
    {
        var normalized = NormalizeRequested(requested);
        if (normalized == "Light") return "Light";
        if (normalized == "Dark") return "Dark";
        return IsWindowsLightMode() ? "Light" : "Dark";
    }

    public static void Apply(Window window, string? requested = null)
    {
        if (requested is not null)
        {
            RequestedTheme = NormalizeRequested(requested);
            ResolvedTheme = Resolve(RequestedTheme);
            ApplyResources(Application.Current?.Resources, RequestedTheme, ResolvedTheme);
        }

        ApplyResources(window.Resources, RequestedTheme, ResolvedTheme);
        window.Background = ResourceBrush("AppBackground");
        window.Foreground = ResourceBrush("PrimaryTextBrush");
        window.FlowDirection = CurrentLanguage == "Arabic"
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
    }

    public static bool IsArabic => CurrentLanguage == "Arabic";

    private static string NormalizeRequested(string? requested) =>
        string.Equals(requested, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" :
        string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "System";

    private static void ApplyResources(
        ResourceDictionary? resources,
        string requested,
        string resolved)
    {
        if (resources is null) return;

        var palette = resolved == "Light" ? Light : Dark;
        foreach (var pair in palette)
            resources[pair.Key] = Brush(pair.Value);

        // System follows Windows light/dark while also inheriting the Windows accent.
        // That keeps System semantically correct and visually distinguishable from
        // Archestro's curated fixed Dark/Light palettes.
        if (requested == "System")
        {
            var accent = SystemParameters.WindowGlassColor;
            if (accent.A == 0)
                accent = resolved == "Light"
                    ? (Color)ColorConverter.ConvertFromString("#3E78BF")
                    : (Color)ColorConverter.ConvertFromString("#5A8FD3");

            var action = resolved == "Light"
                ? Blend(accent, Colors.Black, 0.18)
                : Blend(accent, Colors.White, 0.16);

            var curatedAction = (Color)ColorConverter.ConvertFromString(
                resolved == "Light" ? Light["PrimaryActionBrush"] : Dark["PrimaryActionBrush"]);

            // If the Windows accent happens to be almost identical to Archestro's
            // fixed palette, nudge System toward a calm teal. This keeps System
            // visibly identifiable while still deriving from Windows accent.
            if (ColorDistance(action, curatedAction) < 28)
            {
                var systemSignature = (Color)ColorConverter.ConvertFromString(
                    resolved == "Light" ? "#267B83" : "#3A8E96");
                action = Blend(action, systemSignature, 0.45);
            }

            var border = resolved == "Light"
                ? Blend(action, Colors.White, 0.18)
                : Blend(action, Colors.White, 0.28);

            resources["BlueBrush"] = Brush(action);
            resources["PrimaryActionBrush"] = Brush(action);
            resources["PrimaryActionBorderBrush"] = Brush(border);
            resources["FocusBrush"] = Brush(border);
            resources["RecorderRingBrush"] = Brush(action);
            resources["AccentTextBrush"] = Brush(action);

            var softTint = resolved == "Light"
                ? Blend(action, Colors.White, 0.86)
                : Blend(action, Colors.Black, 0.72);
            var softHover = resolved == "Light"
                ? Blend(action, Colors.White, 0.78)
                : Blend(action, Colors.Black, 0.58);

            resources["SurfaceHoverBrush"] = Brush(softTint);
            resources["MenuHoverBrush"] = Brush(softTint);
            resources["AccentSoftBrush"] = Brush(softTint);
            resources["AccentSoftBorderBrush"] = Brush(border);
            resources["BorderBrush"] = Brush(softHover);
            resources["ChatUserBrush"] = Brush(softTint);
            resources["LogoPlateBorderBrush"] = Brush(border);
        }
    }

    private static double ColorDistance(Color a, Color b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    private static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte x, byte y) =>
            (byte)Math.Round(x + ((y - x) * amount));

        return Color.FromArgb(
            255,
            Mix(a.R, b.R),
            Mix(a.G, b.G),
            Mix(a.B, b.B));
    }

    private static SolidColorBrush ResourceBrush(string key) =>
        Application.Current?.Resources[key] as SolidColorBrush ?? Brush(Dark[key]);

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private static SolidColorBrush Brush(Color color) => new(color);

    private static bool IsWindowsLightMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i != 0;
        }
        catch { return false; }
    }
}
