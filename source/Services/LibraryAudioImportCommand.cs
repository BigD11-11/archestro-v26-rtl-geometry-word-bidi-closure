namespace Archestro.MeetingVault.Services;

/// <summary>Single routing point for picker, card surface, icon button and file-drop imports.</summary>
public static class LibraryAudioImportCommand
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".mka", ".mp4", ".mov"
    };

    public static IReadOnlyList<string> SupportedFiles(
        IEnumerable<string> paths,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && fileExists(path) &&
                           SupportedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<bool> ExecuteAsync(
        IEnumerable<string>? paths,
        Func<Task<IEnumerable<string>?>> chooseFiles,
        Func<IReadOnlyList<string>, Task> importFiles,
        Func<string, bool>? fileExists = null)
    {
        if (paths is null)
            paths = await chooseFiles().ConfigureAwait(true);
        if (paths is null)
            return false;

        var supported = SupportedFiles(paths, fileExists);
        if (supported.Count == 0)
            return false;

        await importFiles(supported).ConfigureAwait(true);
        return true;
    }
}
