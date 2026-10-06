using System.Text.Json;
using Archestro.MeetingVault.Models;

namespace Archestro.MeetingVault.Services;

public sealed class BrandingService
{
    public BrandConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "brand.json");
        if (!File.Exists(path)) return new BrandConfig();

        try
        {
            return JsonSerializer.Deserialize<BrandConfig>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new BrandConfig();
        }
        catch
        {
            return new BrandConfig();
        }
    }
}
