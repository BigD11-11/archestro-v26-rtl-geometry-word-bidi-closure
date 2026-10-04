using System.Text.RegularExpressions;
using Archestro.MeetingVault.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Archestro.MeetingVault.Services;

public static class MeetingReportWordExporter
{
    private const string Navy = "132238";
    private const string Gold = "C79A3B";
    private const string LightGold = "F4EBD6";
    private const string LightPanel = "F4F7FA";
    private const string Border = "D7DEE8";
    private const string Muted = "667085";

    public static string Export(MeetingRecord meeting, MeetingIntelligenceReport report)
    {
        var reportsFolder = MeetingIntelligenceService.GetReportsFolder(meeting);
        Directory.CreateDirectory(reportsFolder);
        var arabic = string.Equals(report.ReportLanguage, "ar", StringComparison.OrdinalIgnoreCase);
        var expectedLanguage = arabic ? "ar" : "en";
        if (!MeetingIntelligenceService.IsReportLanguageCompatible(report, expectedLanguage))
            throw new InvalidOperationException(arabic
                ? "هذا التقرير لا يطابق اللغة العربية الحالية. أعد إعداد التقرير قبل التصدير."
                : "This report does not match the current English output language. Regenerate it before export.");

        var title = MeetingIntelligenceService.BestMeetingReportTitle(meeting);
        var prefix = arabic ? "تقرير الاجتماع" : "Meeting Report";
        var safe = AppPaths.Sanitize($"{prefix} - {title} - {meeting.StartLocal:yyyy-MM-dd}");
        var path = Path.Combine(reportsFolder, safe + ".docx");

        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        AddDocumentSettings(main, arabic);
        main.Document = new W.Document(new W.Body());
        AddStyles(main, arabic);
        var body = main.Document.Body!;

        AddCover(body, meeting, report, title, arabic);
        AddExecutiveSummary(body, report, arabic);
        AddBulletSection(body, arabic ? "المحاور الرئيسية" : "Key Topics", report.Topics, report, arabic);
        AddBulletSection(body, arabic ? "أبرز النقاط" : "Key Points", report.KeyPoints, report, arabic);
        AddItemTable(body, arabic ? "القرارات" : "Decisions", report.Decisions, report, arabic, TableKind.Decisions);
        AddItemTable(body, arabic ? "المهام والخطوات التالية" : "Action Items", report.ActionItems, report, arabic, TableKind.Actions);
        AddBulletSection(body, arabic ? "الالتزامات والمواعيد" : "Commitments & Deadlines",
            report.Commitments.Concat(report.Deadlines).ToList(), report, arabic, showOwner: true);
        AddItemTable(body, arabic ? "المخاطر والملاحظات" : "Risks & Concerns", report.Risks, report, arabic, TableKind.Risks);
        AddBulletSection(body, arabic ? "الأسئلة والنقاط المفتوحة" : "Open Questions", report.OpenItems, report, arabic);
        AddBulletSection(body, arabic ? "اللحظات المهمة" : "Important Moments", report.ImportantMoments, report, arabic, showOwner: true);
        AddBulletSection(body, arabic ? "مساهمات المشاركين" : "Participant Contributions", report.ParticipantContributions, report, arabic, showOwner: true);
        AddBulletSection(body, arabic ? "المتابعة القادمة" : "Follow-up", report.FollowUp, report, arabic, showOwner: true);
        AddEvidenceAppendix(body, report, arabic);

        body.Append(CreateRule());
        body.Append(CreateParagraph(
            arabic
                ? $"تم إنشاء التقرير محليًا بواسطة Archestro Meeting Vault • {report.GeneratedLocal:yyyy-MM-dd HH:mm}"
                : $"Generated locally by Archestro Meeting Vault • {report.GeneratedLocal:yyyy-MM-dd HH:mm}",
            "EvidenceText", arabic, muted: true));

        var sectionProperties = new W.SectionProperties(
            new W.PageSize { Width = 11906, Height = 16838 },
            new W.PageMargin { Top = 850, Bottom = 850, Left = 900, Right = 900 });
        if (arabic) sectionProperties.Append(new W.BiDi());
        body.Append(sectionProperties);
        main.Document.Save();
        return path;
    }

    private static void AddCover(W.Body body, MeetingRecord meeting, MeetingIntelligenceReport report, string title, bool arabic)
    {
        body.Append(CreateParagraph("ARCHESTRO MEETING VAULT", "Eyebrow", false, color: Gold));
        body.Append(CreateParagraph(arabic ? "تقرير الاجتماع" : "Meeting Report", "ReportTitle", arabic, center: true));
        body.Append(CreateParagraph(title, "ReportSubtitle", arabic));

        var meta = new W.Table();
        var props = CreateTableProperties(arabic,
            new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Nil },
                new W.LeftBorder { Val = W.BorderValues.Nil },
                new W.BottomBorder { Val = W.BorderValues.Nil },
                new W.RightBorder { Val = W.BorderValues.Nil },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Nil },
                new W.InsideVerticalBorder { Val = W.BorderValues.Nil }));
        meta.Append(props);
        meta.Append(CreateTableGrid(4));
        var type = MeetingIntelligenceService.LocalizedModeLabel(report.MeetingMode, arabic);
        meta.Append(CreateMetaRow(
            arabic
                ? new[] { $"التاريخ: {meeting.StartLocal:yyyy-MM-dd}", $"الوقت: {meeting.StartLocal:HH:mm}", $"المدة: {meeting.DurationText}", $"نوع التقرير: {type}" }
                : new[] { $"Date: {meeting.StartLocal:yyyy-MM-dd}", $"Time: {meeting.StartLocal:HH:mm}", $"Duration: {meeting.DurationText}", $"Report type: {type}" },
            arabic));
        body.Append(meta);
        body.Append(CreateParagraph(
            arabic ? "ملخص تنفيذي وقرارات ومهام موثقة بالدليل" : "Executive summary, decisions and actions grounded in meeting evidence",
            "Tagline", arabic, color: Muted));
        body.Append(CreateRule());
    }

    private static void AddExecutiveSummary(W.Body body, MeetingIntelligenceReport report, bool arabic)
    {
        body.Append(CreateSectionHeading(arabic ? "الملخص التنفيذي" : "Executive Summary", arabic));
        var table = new W.Table();
        var props = CreateTableProperties(arabic,
            new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 },
                new W.LeftBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 },
                new W.BottomBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 },
                new W.RightBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 }));
        table.Append(props);
        table.Append(CreateTableGrid(1));
        var cell = new W.TableCell();
        cell.Append(new W.TableCellProperties(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = LightPanel }));
        cell.Append(CreateParagraph(
            string.IsNullOrWhiteSpace(report.ExecutiveSummary)
                ? (arabic ? "لا توجد خلاصة مؤكدة مدعومة بالدليل." : "No supported executive summary was found.")
                : report.ExecutiveSummary.Trim(),
            "BodyText", arabic));
        table.Append(new W.TableRow(cell));
        body.Append(table);
    }

    private enum TableKind { Decisions, Actions, Risks }

    private static void AddBulletSection(W.Body body, string heading, IEnumerable<IntelligenceItem> source, MeetingIntelligenceReport report, bool arabic, bool showOwner = false)
    {
        body.Append(CreateSectionHeading(heading, arabic));
        var items = source.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
        if (items.Count == 0)
        {
            body.Append(CreateParagraph(arabic ? "لا توجد عناصر مؤكدة في هذا القسم." : "No confirmed items in this section.", "EvidenceText", arabic, muted: true));
            return;
        }
        foreach (var item in items)
        {
            var line = "• " + item.Text.Trim();
            if (showOwner && !string.IsNullOrWhiteSpace(item.Owner) && !IsGenericSpeaker(item.Owner))
                line += arabic ? $" — {item.Owner}" : $" — {item.Owner}";
            body.Append(CreateParagraph(line, "BulletText", arabic));
            var evidence = EvidenceLabel(item, report, arabic);
            if (!string.IsNullOrWhiteSpace(evidence))
                body.Append(CreateParagraph(evidence, "EvidenceText", arabic, muted: true, indentTwips: arabic ? 0 : 280));
        }
    }

    private static void AddItemTable(W.Body body, string heading, IEnumerable<IntelligenceItem> source, MeetingIntelligenceReport report, bool arabic, TableKind kind)
    {
        body.Append(CreateSectionHeading(heading, arabic));
        var items = source.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
        if (items.Count == 0)
        {
            body.Append(CreateParagraph(arabic ? "لا توجد عناصر مؤكدة في هذا القسم." : "No confirmed items in this section.", "EvidenceText", arabic, muted: true));
            return;
        }

        var table = new W.Table();
        var props = CreateTableProperties(arabic,
            new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 },
                new W.LeftBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 },
                new W.BottomBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 },
                new W.RightBorder { Val = W.BorderValues.Single, Color = Border, Size = 4 },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Color = Border, Size = 3 },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Color = Border, Size = 3 }));
        table.Append(props);

        string[] headers = kind switch
        {
            TableKind.Actions => arabic ? new[] { "المهمة", "المسؤول", "الموعد", "الدليل" } : new[] { "Action", "Owner", "Due", "Evidence" },
            TableKind.Risks => arabic ? new[] { "الخطر / الملاحظة", "الحالة", "الدليل" } : new[] { "Risk / Concern", "Status", "Evidence" },
            _ => arabic ? new[] { "القرار", "المسؤول / المعتمد", "الدليل" } : new[] { "Decision", "Owner / Approver", "Evidence" }
        };
        table.Append(CreateTableGrid(headers.Length));
        table.Append(CreateTableRow(headers, arabic, true));
        foreach (var item in items)
        {
            var owner = string.IsNullOrWhiteSpace(item.Owner) || IsGenericSpeaker(item.Owner) ? "—" : item.Owner;
            string[] cells = kind switch
            {
                TableKind.Actions => new[] { item.Text, owner, string.IsNullOrWhiteSpace(item.Due) ? "—" : item.Due, EvidenceLabel(item, report, arabic) },
                TableKind.Risks => new[] { item.Text, string.IsNullOrWhiteSpace(item.Severity) ? "—" : item.Severity, EvidenceLabel(item, report, arabic) },
                _ => new[] { item.Text, owner, EvidenceLabel(item, report, arabic) }
            };
            table.Append(CreateTableRow(cells, arabic, false));
        }
        body.Append(table);
    }

    private static W.TableRow CreateTableRow(IEnumerable<string> values, bool arabic, bool header)
    {
        var row = new W.TableRow();
        foreach (var value in values)
        {
            var cell = new W.TableCell();
            var cp = new W.TableCellProperties();
            if (header) cp.PrependChild(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = Navy });
            cell.Append(cp);
            cell.Append(CreateParagraph(value ?? "", header ? "TableHeader" : "TableCell", arabic, color: header ? "FFFFFF" : null, center: header));
            row.Append(cell);
        }
        return row;
    }

    private static W.TableRow CreateMetaRow(IEnumerable<string> values, bool arabic)
    {
        var row = new W.TableRow();
        foreach (var value in values)
        {
            var cell = new W.TableCell();
            cell.Append(new W.TableCellProperties(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = LightPanel }));
            cell.Append(CreateParagraph(value, "EvidenceText", arabic, muted: true));
            row.Append(cell);
        }
        return row;
    }

    private static void AddEvidenceAppendix(W.Body body, MeetingIntelligenceReport report, bool arabic)
    {
        body.Append(CreateSectionHeading(arabic ? "ملحق الأدلة" : "Evidence Appendix", arabic));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AllItems(report)) foreach (var id in item.Evidence) used.Add(id);
        var evidence = report.EvidenceIndex.Where(x => used.Contains(x.Id)).Take(80).ToList();
        if (evidence.Count == 0)
        {
            body.Append(CreateParagraph(arabic ? "لا توجد أدلة إضافية لعرضها." : "No additional evidence to display.", "EvidenceText", arabic, muted: true));
            return;
        }
        foreach (var e in evidence)
        {
            var speaker = IsGenericSpeaker(e.Speaker) ? "" : e.Speaker;
            var meta = e.TimeText + (string.IsNullOrWhiteSpace(speaker) ? "" : $" • {speaker}");
            body.Append(CreateParagraph(meta, "EvidenceText", arabic, muted: true));
            body.Append(CreateParagraph(e.Text, "BodyText", arabic));
        }
    }

    private static IEnumerable<IntelligenceItem> AllItems(MeetingIntelligenceReport r) =>
        r.KeyPoints.Concat(r.Topics).Concat(r.Decisions).Concat(r.ActionItems).Concat(r.Commitments).Concat(r.Deadlines)
            .Concat(r.Risks).Concat(r.OpenItems).Concat(r.ImportantMoments).Concat(r.ParticipantContributions).Concat(r.FollowUp).Concat(r.CommercialPoints);

    private static string EvidenceLabel(IntelligenceItem item, MeetingIntelligenceReport report, bool arabic)
    {
        var refs = item.Evidence.Select(id => report.EvidenceIndex.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x is not null).Cast<EvidenceRef>().Take(3)
            .Select(e => e.TimeText + (string.IsNullOrWhiteSpace(e.Speaker) || IsGenericSpeaker(e.Speaker) ? "" : " • " + e.Speaker)).ToList();
        if (refs.Count == 0) return "";
        return (arabic ? "الدليل: " : "Evidence: ") + string.Join(" | ", refs);
    }

    private static bool IsGenericSpeaker(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("Speaker", StringComparison.OrdinalIgnoreCase) || value.Equals("Transcript", StringComparison.OrdinalIgnoreCase);

    private static W.Paragraph CreateSectionHeading(string text, bool arabic) =>
        CreateParagraph(text, "SectionHeading", arabic, spacingBefore: 280, spacingAfter: 110, color: Navy);

    private static W.Paragraph CreateRule()
    {
        var p = new W.Paragraph(new W.ParagraphProperties(
            new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Color = Gold, Size = 10, Space = 1 }),
            new W.SpacingBetweenLines { Before = "100", After = "150" }));
        return p;
    }

    private static W.Paragraph CreateParagraph(string text, string styleId, bool rtl, bool muted = false, int indentTwips = 0, int spacingBefore = 0, int spacingAfter = 70, string? color = null, bool center = false)
    {
        var pp = new W.ParagraphProperties();
        pp.Append(new W.ParagraphStyleId { Val = styleId });
        if (rtl) pp.Append(new W.BiDi());
        pp.Append(new W.SpacingBetweenLines { Before = spacingBefore.ToString(), After = spacingAfter.ToString(), Line = "300", LineRule = W.LineSpacingRuleValues.Auto });
        if (indentTwips > 0)
            pp.Append(rtl
                ? new W.Indentation { Right = indentTwips.ToString() }
                : new W.Indentation { Left = indentTwips.ToString() });
        pp.Append(new W.Justification { Val = center ? W.JustificationValues.Center : (rtl ? W.JustificationValues.Start : W.JustificationValues.Left) });
        var p = new W.Paragraph(pp);
        foreach (var segment in SplitByScript(text ?? "")) p.Append(CreateRun(segment.Text, segment.Arabic, muted, color));
        return p;
    }

    private static W.Run CreateRun(string text, bool rtl, bool muted, string? color)
    {
        var rp = new W.RunProperties(new W.RunFonts { Ascii = "Aptos", HighAnsi = "Aptos", ComplexScript = "Arial" });
        if (!string.IsNullOrWhiteSpace(color)) rp.Append(new W.Color { Val = color });
        else if (muted) rp.Append(new W.Color { Val = Muted });
        if (rtl) rp.Append(new W.RightToLeftText());
        rp.Append(new W.Languages { Val = rtl ? "ar-SA" : "en-US", Bidi = rtl ? "ar-SA" : "en-US" });
        return new W.Run(rp, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static W.TableProperties CreateTableProperties(bool rtl, W.TableWidth width, W.TableBorders borders)
    {
        var properties = new W.TableProperties();
        if (rtl) properties.Append(new W.BiDiVisual());
        properties.Append(width);
        properties.Append(borders);
        return properties;
    }

    private static W.TableGrid CreateTableGrid(int columns) => new(
        Enumerable.Range(0, columns).Select(_ => new W.GridColumn()));

    private static void AddStyles(MainDocumentPart main, bool arabic)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new W.Styles();
        AddStyle(part.Styles, "ReportTitle", 34, true, 0, 120, arabic);
        AddStyle(part.Styles, "ReportSubtitle", 26, true, 0, 90, arabic);
        AddStyle(part.Styles, "Eyebrow", 16, true, 0, 40, arabic);
        AddStyle(part.Styles, "Tagline", 18, false, 0, 120, arabic);
        AddStyle(part.Styles, "SectionHeading", 24, true, 240, 90, arabic);
        AddStyle(part.Styles, "BodyText", 20, false, 0, 70, arabic);
        AddStyle(part.Styles, "BulletText", 19, false, 0, 45, arabic);
        AddStyle(part.Styles, "EvidenceText", 15, false, 0, 45, arabic);
        AddStyle(part.Styles, "TableHeader", 17, true, 0, 0, arabic);
        AddStyle(part.Styles, "TableCell", 17, false, 0, 0, arabic);
        part.Styles.Save();
    }

    private static void AddStyle(W.Styles styles, string id, int size, bool bold, int before, int after, bool arabic)
    {
        var style = new W.Style { Type = W.StyleValues.Paragraph, StyleId = id, CustomStyle = true };
        style.Append(new W.StyleName { Val = id });
        style.Append(new W.BasedOn { Val = "Normal" });
        var paragraphProperties = new W.StyleParagraphProperties();
        if (arabic) paragraphProperties.Append(new W.BiDi());
        paragraphProperties.Append(new W.SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "300", LineRule = W.LineSpacingRuleValues.Auto });
        if (arabic) paragraphProperties.Append(new W.Justification { Val = W.JustificationValues.Start });
        style.Append(paragraphProperties);
        var rp = new W.StyleRunProperties(new W.RunFonts { Ascii = "Aptos", HighAnsi = "Aptos", ComplexScript = "Arial" });
        if (bold) rp.Append(new W.Bold());
        rp.Append(new W.FontSize { Val = size.ToString() }, new W.FontSizeComplexScript { Val = size.ToString() });
        style.Append(rp);
        styles.Append(style);
    }

    private static void AddDocumentSettings(MainDocumentPart main, bool arabic)
    {
        var settingsPart = main.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings = new W.Settings(
            new W.Compatibility(
                new W.CompatibilitySetting
                {
                    Name = W.CompatSettingNameValues.CompatibilityMode,
                    Uri = "http://schemas.microsoft.com/office/word",
                    Val = "15"
                }),
            new W.ThemeFontLanguages
            {
                Val = arabic ? "ar-SA" : "en-US",
                Bidi = arabic ? "ar-SA" : "en-US",
                EastAsia = "en-US"
            });
        settingsPart.Settings.Save();
    }

    private static IEnumerable<(string Text, bool Arabic)> SplitByScript(string text)
    {
        if (string.IsNullOrEmpty(text)) return new[] { ("", false) };
        var list = new List<(string, bool)>();
        var current = new System.Text.StringBuilder();
        bool? arabic = null;
        foreach (var ch in text)
        {
            var isArabic = ch >= '\u0600' && ch <= '\u06FF';
            var strong = char.IsLetterOrDigit(ch);
            if (strong && arabic.HasValue && arabic.Value != isArabic && current.Length > 0)
            {
                list.Add((current.ToString(), arabic.Value)); current.Clear();
            }
            if (strong) arabic = isArabic;
            current.Append(ch);
        }
        if (current.Length > 0) list.Add((current.ToString(), arabic ?? false));
        return list;
    }

}
