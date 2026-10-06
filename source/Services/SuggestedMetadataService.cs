using System.Text.RegularExpressions;

namespace Archestro.MeetingVault.Services;

public sealed class SuggestedMetadataService
{
    public (string SuggestedTitle, string Category, string CategoryColor) Suggest(string transcript)
    {
        var text = transcript ?? "";
        var normalized = Normalize(text);

        var category = CategoryCatalog.Defaults
            .Where(x => x.Name != "Uncategorized" && x.Name != "Other")
            .FirstOrDefault(x => x.Keywords.Any(k => normalized.Contains(Normalize(k), StringComparison.OrdinalIgnoreCase)))
            ?.Name ?? "Uncategorized";

        var topic =
            ContainsAny(normalized, "contract", "agreement", "عقد", "اتفاقية", "اتفاقيه") ? "Contract Review" :
            ContainsAny(normalized, "commercial", "pricing", "price", "quotation", "سعر", "تسعير", "تجاري", "عرض سعر") ? "Commercial Follow-up" :
            ContainsAny(normalized, "operations", "operation", "fleet", "تشغيل", "عمليات", "اسطول", "أسطول") ? "Operations Review" :
            ContainsAny(normalized, "strategy", "strategic", "استراتيجية", "استراتيجي") ? "Strategy Review" :
            ContainsAny(normalized, "project update", "progress", "milestone", "تحديث المشروع", "التقدم", "مرحلة") ? "Project Update" :
            ContainsAny(normalized, "follow up", "follow-up", "followup", "متابعة") ? "Follow-up" :
            ContainsAny(normalized, "weekly", "اسبوعي", "أسبوعي") ? "Weekly Review" :
            "Meeting";

        string title;
        if (!category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
            title = $"{category} {topic}";
        else
            title = ExtractFirstUsefulLine(text) ?? topic;

        return (TrimTitle(title), category, CategoryCatalog.ColorFor(category));
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant().Replace('’', '\'').Replace('ـ', ' '), @"\s+", " ").Trim();

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(v => text.Contains(Normalize(v), StringComparison.OrdinalIgnoreCase));

    private static string? ExtractFirstUsefulLine(string transcript)
    {
        foreach (var raw in transcript.Split(new[] { '\r', '\n', '.', '؟', '?' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = Regex.Replace(raw, @"\s+", " ").Trim();
            if (line.Length < 16) continue;
            if (line.StartsWith("hello", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("good morning", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("good afternoon", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("السلام", StringComparison.OrdinalIgnoreCase))
                continue;

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words.Take(9));
        }
        return null;
    }

    private static string TrimTitle(string title) =>
        title.Length <= 72 ? title : title[..72].TrimEnd();
}
