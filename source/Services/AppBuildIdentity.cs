using System.Reflection;
using System.Security.Cryptography;

namespace Archestro.MeetingVault.Services;

public static class AppBuildIdentity
{
    private static readonly Assembly EntryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

    public static string CustomerVersion => ReadMetadata("CustomerVersion") ?? EntryAssembly.GetName().Version?.ToString(3) ?? "unknown";
    public static string InternalBuild => ReadMetadata("InternalBuild") ?? "unmarked";
    public static string SourceCommit => ReadMetadata("SourceCommit") ?? "unknown";
    public static string BuildTimestampUtc => ReadMetadata("BuildTimestampUtc") ?? "unknown";

    public static object GetRuntimeProof()
    {
        var assembly = EntryAssembly;
        var marker = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var executable = Environment.ProcessPath;
        string? sha256 = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            {
                using var stream = File.OpenRead(executable);
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }
        }
        catch { }
        return new
        {
            customerVersion = CustomerVersion,
            internalBuild = InternalBuild,
            sourceCommit = SourceCommit,
            buildTimestampUtc = BuildTimestampUtc,
            marker,
            executable,
            sha256
        };
    }

    private static string? ReadMetadata(string key) => EntryAssembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
}
