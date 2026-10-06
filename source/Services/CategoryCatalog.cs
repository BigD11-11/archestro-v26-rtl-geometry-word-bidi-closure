namespace Archestro.MeetingVault.Services;

public static class CategoryCatalog
{
    public sealed record DefaultCategory(string Name, string Color, int SortOrder, string[] Keywords);

    public static readonly IReadOnlyList<DefaultCategory> Defaults = new[]
    {
        new DefaultCategory("Internal", "#64748B", 10, new[] { "internal", "داخلي" }),
        new DefaultCategory("Management", "#3B82F6", 20, new[] { "management", "executive", "board", "إدارة", "ادارة" }),
        new DefaultCategory("Clients", "#22C7D9", 30, new[] { "client", "customer", "عميل", "عملاء" }),
        new DefaultCategory("Projects", "#8B5CF6", 40, new[] { "project", "projects", "مشروع", "مشاريع" }),
        new DefaultCategory("Sales", "#27C281", 50, new[] { "sales", "commercial", "مبيعات", "تجاري" }),
        new DefaultCategory("Contracts", "#C69A52", 60, new[] { "contract", "agreement", "عقد", "اتفاقية", "اتفاقيه" }),
        new DefaultCategory("Procurement", "#F59E0B", 70, new[] { "procurement", "purchase", "supplier", "مشتريات", "مورد" }),
        new DefaultCategory("HR", "#EC4899", 80, new[] { "hr", "human resources", "recruitment", "موارد بشرية", "توظيف" }),
        new DefaultCategory("Other", "#64748B", 900, new[] { "other", "external", "أخرى", "اخرى", "خارجي" }),
        new DefaultCategory("Uncategorized", "#64748B", 999, Array.Empty<string>())
    };

    public static IEnumerable<string> Names => Defaults.Select(x => x.Name);

    public static string ColorFor(string category) =>
        Defaults.FirstOrDefault(x => x.Name.Equals(category, StringComparison.OrdinalIgnoreCase))?.Color
        ?? ColorFromName(category);

    public static string ColorFromName(string name)
    {
        var palette = new[] { "#3B82F6","#8B5CF6","#27C281","#F59E0B","#22C7D9",
                              "#EF5350","#C69A52","#64748B","#14B8A6","#A855F7" };
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name ?? "");
        return palette[Math.Abs(hash % palette.Length)];
    }
}
