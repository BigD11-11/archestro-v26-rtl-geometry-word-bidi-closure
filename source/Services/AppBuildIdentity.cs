using System.Reflection;
using System.Security.Cryptography;

namespace Archestro.MeetingVault.Services;

public static class AppBuildIdentity
{
    public static object GetRuntimeProof()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
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
        return new { marker, executable, sha256 };
    }
}
