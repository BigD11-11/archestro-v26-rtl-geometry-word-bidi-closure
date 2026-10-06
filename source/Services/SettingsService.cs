using System.Security.Cryptography;
using System.Text.Json;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings LoadOrDiscover()
    {
        AppPaths.Ensure();
        if (File.Exists(AppPaths.Settings))
        {
            var existing = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), JsonOptions);
            if (existing is not null)
            {
                var discovered = FindBuzz();
                var bridgeReady = IsNativeBridge(discovered);

                if (bridgeReady &&
                    (string.IsNullOrWhiteSpace(existing.BuzzExe) ||
                     !File.Exists(existing.BuzzExe) ||
                     !IsNativeBridge(existing.BuzzExe)))
                {
                    existing.BuzzExe = discovered;
                }

                if (IsNativeBridge(existing.BuzzExe))
                    existing.TranscriptionEngine = "native-whisper";

                existing.Appearance =
                    existing.Appearance.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" :
                    existing.Appearance.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "System";
                existing.PreferredLanguage =
                    existing.PreferredLanguage.Equals("Arabic", StringComparison.OrdinalIgnoreCase) ? "Arabic" : "English";
                existing.ProcessingMode = existing.ProcessingMode is "Cloud Fast" or "Custom" ? existing.ProcessingMode : "Offline";
                existing.TranscriptionProvider = existing.TranscriptionProvider.Equals("Groq", StringComparison.OrdinalIgnoreCase) ? "Groq" : "Local";
                existing.NewAudioPolicy = existing.NewAudioPolicy is "Import only" or "Import + Transcribe + Report" ? existing.NewAudioPolicy : "Import + Transcribe";
                Save(existing);
                return existing;
            }
        }

        var settings = new AppSettings
        {
            ObsPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            BuzzExe = FindBuzz(),
            FfmpegExe = FindExecutable("ffmpeg.exe")
        };

        Save(settings);
        return settings;
    }

    public void Save(AppSettings settings) =>
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(settings, JsonOptions));

    private static bool IsNativeBridge(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path) &&
        Path.GetFileName(path).Equals(
            "ArchestroTranscriptionBridge.exe",
            StringComparison.OrdinalIgnoreCase);

    private static string FindBuzz()
    {
        var environmentBridge = Environment.GetEnvironmentVariable("ARCHESTRO_TRANSCRIPTION_BRIDGE");
        var appBase = AppContext.BaseDirectory;

        var candidates = new[]
        {
            environmentBridge ?? "",
            Path.Combine(appBase, "_runtime", "TranscriptionNative", "v1", "bridge_v2_active", "ArchestroTranscriptionBridge.exe"),
            Path.Combine(appBase, "Runtime", "Transcription", "ArchestroTranscriptionBridge.exe"),
            Path.Combine(appBase, "_runtime", "TranscriptionNative", "bridge", "ArchestroTranscriptionBridge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Buzz", "Buzz.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Buzz-1.4.4", "Buzz.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buzz", "Buzz.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Buzz", "Buzz.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Buzz", "Buzz.exe")
        };

        var direct = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (!Directory.Exists(root)) return "";

        return Directory.EnumerateFiles(root, "ArchestroTranscriptionBridge.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.EnumerateFiles(root, "Buzz.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? "";
    }

    private static string FindExecutable(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "_runtime", "ffmpeg", name);
        if (File.Exists(bundled)) return bundled;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(part.Trim(), name);
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        var localWinget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(localWinget))
            return Directory.EnumerateFiles(localWinget, name, SearchOption.AllDirectories).FirstOrDefault() ?? "";

        return "";
    }
}
