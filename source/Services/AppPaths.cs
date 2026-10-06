namespace Archestro.MeetingVault.Services;

public static class AppPaths
{
    public static string Root => GetRoot();
    public static string Meetings => Path.Combine(Root, "Meetings");
    public static string Inbox => Path.Combine(Root, "Inbox");
    public static string System => Path.Combine(Root, "System");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string Database => Path.Combine(System, "meeting-vault.db");
    public static string Settings => Path.Combine(System, "settings.json");
    public static string SpeakerModels => Path.Combine(System, "SpeakerModels");
    public static string SpeakerProfilePhotos => Path.Combine(System, "PeoplePhotos");
    public static string SpeakerVoiceSamples => Path.Combine(System, "PeopleVoiceSamples");
    public static string SpeakerProfiles => Path.Combine(System, "speaker-profiles.json");
    public static string Intelligence => Path.Combine(System, "Intelligence");
    public static string AiRuntime => Path.Combine(System, "AI", "llama");
    public static string AiModels => Path.Combine(System, "AI", "Models");

    private static string GetRoot()
    {
        var qaRoot = Environment.GetEnvironmentVariable("ARCHESTRO_QA_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(qaRoot))
        {
            if (!Path.IsPathFullyQualified(qaRoot))
                throw new InvalidOperationException("ARCHESTRO_QA_DATA_ROOT must be an absolute path.");
            return Path.GetFullPath(qaRoot);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Archestro Meeting Vault");
    }

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Meetings);
        Directory.CreateDirectory(System);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(SpeakerModels);
        Directory.CreateDirectory(SpeakerProfilePhotos);
        Directory.CreateDirectory(SpeakerVoiceSamples);
        Directory.CreateDirectory(Intelligence);
        Directory.CreateDirectory(AiRuntime);
        Directory.CreateDirectory(AiModels);
    }

    public static string CreateMeetingFolder(string? requestedName, DateTimeOffset start)
    {
        var safe = Sanitize(string.IsNullOrWhiteSpace(requestedName) ? "Meeting" : requestedName!);
        var year = start.ToString("yyyy");
        var month = start.ToString("MM - MMMM", global::System.Globalization.CultureInfo.InvariantCulture);
        var day = start.ToString("yyyy-MM-dd");
        var parent = Path.Combine(Meetings, year, month, day);
        Directory.CreateDirectory(parent);

        var baseName = $"{start:HH-mm}__{safe}";
        var candidate = Path.Combine(parent, baseName);
        var counter = 2;
        while (Directory.Exists(candidate))
            candidate = Path.Combine(parent, $"{baseName}__{counter++}");

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    public static string Sanitize(string input)
    {
        var result = input;
        foreach (var c in Path.GetInvalidFileNameChars())
            result = result.Replace(c.ToString(), "");
        result = string.Join("-", result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (result.Length > 72) result = result[..72];
        return string.IsNullOrWhiteSpace(result) ? "Meeting" : result;
    }
}
