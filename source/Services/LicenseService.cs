using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class LicenseService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "Archestro.MeetingVault.LocalLicense.v1");

    public string LicensePath =>
        Path.Combine(AppPaths.System, "license.dat");

    public LocalLicense? Load()
    {
        try
        {
            if (!File.Exists(LicensePath)) return null;
            var encrypted = File.ReadAllBytes(LicensePath);
            var clear = ProtectedData.Unprotect(
                encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<LocalLicense>(
                clear,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    public void Save(LocalLicense license)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LicensePath)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(
            license, new JsonSerializerOptions { WriteIndented = true });
        var encrypted = ProtectedData.Protect(
            clear, Entropy, DataProtectionScope.CurrentUser);

        var temp = LicensePath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, LicensePath, true);
    }

    public ProductEdition EffectiveEdition =>
        Load()?.Edition ?? ProductEdition.Intelligence; // owner/dev fallback only

    public bool IntelligenceAllowed =>
        EffectiveEdition == ProductEdition.Intelligence;
}
