namespace Archestro.MeetingVault.Services;

public static class GreetingService
{
    public static string GetGreeting(DateTimeOffset now, string? displayName)
    {
        var name = string.IsNullOrWhiteSpace(displayName)
            ? "Mr. Mohammed Bin Ali Abu Tamim"
            : displayName.Trim();

        var h = now.Hour;
        if (h >= 6 && h < 12) return $"Good morning, {name}";
        if (h >= 12 && h < 18) return $"Good afternoon, {name}";
        if (h >= 18) return $"Good evening, {name}";
        return $"Welcome back, {name}";
    }
}
