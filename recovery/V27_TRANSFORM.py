from pathlib import Path
import sys

root = Path(sys.argv[1]) / "PATCH" / "src" / "Archestro.MeetingVault"

def replace_required(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"required transform anchor missing: {label}")
    return text.replace(old, new)

# XAML: physical shells use LTR coordinates; Arabic/English direction belongs to content.
p = root / "Dialogs" / "IntelligenceWindow.xaml"
s = p.read_text(encoding="utf-8-sig")
s = replace_required(s, """    <Style x:Key="ReportNavHost" TargetType="Grid">
      <Setter Property="HorizontalAlignment" Value="Left"/>
      <Style.Triggers>
        <DataTrigger Binding="{Binding FlowDirection, RelativeSource={RelativeSource AncestorType=Window}}" Value="RightToLeft">
          <Setter Property="HorizontalAlignment" Value="Right"/>
        </DataTrigger>
        <DataTrigger Binding="{Binding FlowDirection, RelativeSource={RelativeSource AncestorType=Window}}" Value="LeftToRight">
          <Setter Property="HorizontalAlignment" Value="Left"/>
        </DataTrigger>
      </Style.Triggers>
    </Style>
""", """    <Style x:Key="ReportNavHost" TargetType="Grid">
      <Setter Property="HorizontalAlignment" Value="Left"/>
      <Setter Property="FlowDirection" Value="LeftToRight"/>
    </Style>
""", "ReportNavHost style")
s = replace_required(s,
    '<Grid x:Name="ReportNavBounds" Grid.Row="0" HorizontalAlignment="Stretch" Margin="0,0,0,10">',
    '<Grid x:Name="ReportNavBounds" Grid.Row="0" HorizontalAlignment="Stretch" FlowDirection="LeftToRight" Margin="0,0,0,10">',
    "ReportNavBounds")
s = replace_required(s, """        <Grid x:Name="ReportNavHost" Style="{StaticResource ReportNavHost}"
                    FlowDirection="{Binding FlowDirection, RelativeSource={RelativeSource AncestorType=Window}}"
                    KeyboardNavigation.TabNavigation="Once">""", """        <Grid x:Name="ReportNavHost" Style="{StaticResource ReportNavHost}"
                    FlowDirection="LeftToRight"
                    KeyboardNavigation.TabNavigation="Once">""", "ReportNavHost flow")
for name in ("ReportNavReportButton", "ReportNavMeetingAskButton", "ReportNavVaultAskButton"):
    s = replace_required(s, f'x:Name="{name}"', f'x:Name="{name}" FlowDirection="{{Binding FlowDirection, RelativeSource={{RelativeSource AncestorType=Window}}}}"', name)
s = replace_required(s,
    '<Grid x:Name="ReportSectionGrid" FlowDirection="{Binding FlowDirection, RelativeSource={RelativeSource AncestorType=Window}}">',
    '<Grid x:Name="ReportSectionGrid" FlowDirection="LeftToRight">',
    "ReportSectionGrid")
for name in ("TopicsCard","KeyPointsCard","DecisionsCard","ActionsCard","CommitmentsCard","RisksCard","ImportantMomentsCard","ParticipantsCard"):
    s = replace_required(s, f'x:Name="{name}"', f'x:Name="{name}" FlowDirection="{{Binding FlowDirection, RelativeSource={{RelativeSource AncestorType=Window}}}}"', name)
p.write_bytes(s.encode("utf-8"))

# Code-behind: explicit physical placement in LTR shells, RTL only inside Arabic content.
p = root / "Dialogs" / "IntelligenceWindow.xaml.cs"
s = p.read_text(encoding="utf-8-sig")
old = """    private void ApplyReportPhysicalOrder(bool rtl)
    {
        Grid.SetColumn(ReportNavReportButton, rtl ? 4 : 0);
        Grid.SetColumn(ReportNavMeetingAskButton, 2);
        Grid.SetColumn(ReportNavVaultAskButton, rtl ? 0 : 4);

        foreach (var (first, second) in new[]
        {
            (TopicsCard, KeyPointsCard), (DecisionsCard, ActionsCard),
            (CommitmentsCard, RisksCard), (ImportantMomentsCard, ParticipantsCard)
        })
        {
            var row = Grid.GetRow(first);
            Grid.SetColumn(first, rtl ? 2 : 0);
            Grid.SetColumn(second, rtl ? 0 : 2);
            Grid.SetRow(second, row);
        }
    }"""
new = """    private void ApplyReportPhysicalOrder(bool rtl)
    {
        // Keep geometry in physical LTR coordinates; apply RTL only to Arabic content.
        ReportNavBounds.FlowDirection = FlowDirection.LeftToRight;
        ReportNavHost.FlowDirection = FlowDirection.LeftToRight;
        ReportSectionGrid.FlowDirection = FlowDirection.LeftToRight;
        ReportNavHost.HorizontalAlignment = rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var contentDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ReportNavReportButton.FlowDirection = contentDirection;
        ReportNavMeetingAskButton.FlowDirection = contentDirection;
        ReportNavVaultAskButton.FlowDirection = contentDirection;

        Grid.SetColumn(ReportNavReportButton, rtl ? 4 : 0);
        Grid.SetColumn(ReportNavMeetingAskButton, 2);
        Grid.SetColumn(ReportNavVaultAskButton, rtl ? 0 : 4);

        foreach (var (first, second) in new[]
        {
            (TopicsCard, KeyPointsCard), (DecisionsCard, ActionsCard),
            (CommitmentsCard, RisksCard), (ImportantMomentsCard, ParticipantsCard)
        })
        {
            var row = Grid.GetRow(first);
            first.FlowDirection = contentDirection;
            second.FlowDirection = contentDirection;
            Grid.SetColumn(first, rtl ? 2 : 0);
            Grid.SetColumn(second, rtl ? 0 : 2);
            Grid.SetRow(second, row);
        }
    }"""
s = replace_required(s, old, new, "ApplyReportPhysicalOrder")
p.write_bytes(s.encode("utf-8"))

# Word exporter: create modern settings and use logical start alignment for RTL.
p = root / "Services" / "MeetingReportWordExporter.cs"
s = p.read_text(encoding="utf-8-sig")
s = replace_required(s, """        main.Document = new W.Document(new W.Body());
        AddStyles(main, arabic);
        var body = main.Document.Body!;""", """        main.Document = new W.Document(new W.Body());
        AddStyles(main, arabic);
        AddDocumentSettings(main, arabic);
        var body = main.Document.Body!;""", "AddDocumentSettings call")
s = replace_required(s,
    'W.JustificationValues.Right : W.JustificationValues.Left',
    'W.JustificationValues.Start : W.JustificationValues.Left',
    "paragraph start alignment")
s = replace_required(s,
    'if (arabic) paragraphProperties.Append(new W.Justification { Val = W.JustificationValues.Right });',
    'if (arabic) paragraphProperties.Append(new W.Justification { Val = W.JustificationValues.Start });',
    "style start alignment")
needle = """    private static void AddStyles(MainDocumentPart main, bool arabic)
    {"""
insert = """    private static void AddDocumentSettings(MainDocumentPart main, bool arabic)
    {
        var part = main.AddNewPart<DocumentSettingsPart>();
        var compatibility = new W.Compatibility(
            new W.CompatibilitySetting
            {
                Name = W.CompatSettingNameValues.CompatibilityMode,
                Uri = "http://schemas.microsoft.com/office/word",
                Val = "15"
            });
        part.Settings = new W.Settings(
            new W.ThemeFontLanguages
            {
                Val = "en-US",
                EastAsia = "en-US",
                Bidi = arabic ? "ar-SA" : "en-US"
            },
            compatibility);
        part.Settings.Save();
    }

"""
s = replace_required(s, needle, insert + needle, "AddDocumentSettings method")
p.write_bytes(s.encode("utf-8"))

# QA: assert physical shell/content split and serialized Word settings/start alignment.
p = root / "Services" / "SelfTestService.cs"
s = p.read_text(encoding="utf-8-sig")
s = replace_required(s,
    '                Check(area.Width > 1300, $"{Language(arabic)} content viewport too narrow: {area.Width:0.0}px.");',
    '''                Check(window.ReportNavBounds.FlowDirection == FlowDirection.LeftToRight, $"{Language(arabic)} nav physical shell must remain LTR.");
                Check(window.ReportNavHost.FlowDirection == FlowDirection.LeftToRight, $"{Language(arabic)} nav host physical shell must remain LTR.");
                Check(window.ReportSectionGrid.FlowDirection == FlowDirection.LeftToRight, $"{Language(arabic)} section grid physical shell must remain LTR.");
                Check(window.ReportNavReportButton.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight), $"{Language(arabic)} nav content direction is wrong.");
                Check(window.TopicsCard.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight), $"{Language(arabic)} card content direction is wrong.");

                Check(area.Width > 1300, $"{Language(arabic)} content viewport too narrow: {area.Width:0.0}px.");''',
    "layout shell assertions")
s = replace_required(s, '?.Value == "right",\n                    "Arabic body paragraph is not right-justified.");', '?.Value == "start",\n                    "Arabic body paragraph is not logically start-aligned for RTL.");', "body start assertion")
s = replace_required(s, '?.Value == "right";', '?.Value == "start";', "style start assertion")
s = replace_required(s, '                    "Arabic BodyText paragraph style is not right-justified.");', '                    "Arabic BodyText paragraph style is not logically start-aligned for RTL.");', "style message")
needle = '            var arabicRuns = xml.Descendants(w + "r")'
insert = '''            var settingsEntry = archive.GetEntry("word/settings.xml") ?? throw new InvalidOperationException("DOCX is missing word/settings.xml.");
            using (var settingsStream = settingsEntry.Open())
            {
                var settingsXml = XDocument.Load(settingsStream);
                var themeFontLang = settingsXml.Descendants(w + "themeFontLang").FirstOrDefault();
                RequireFixture(themeFontLang?.Attribute(w + "bidi")?.Value == "ar-SA",
                    "Arabic DOCX settings are missing themeFontLang bidi=ar-SA.");
                var compat = settingsXml.Descendants(w + "compatSetting").FirstOrDefault(node => node.Attribute(w + "name")?.Value == "compatibilityMode");
                RequireFixture(compat?.Attribute(w + "val")?.Value == "15",
                    "Arabic DOCX settings are missing modern Word compatibilityMode=15.");
            }

'''
s = replace_required(s, needle, insert + needle, "settings assertions")
s = replace_required(s, '?.Value == "right"),', '?.Value == "start"),', "semantic proof start count")
p.write_bytes(s.encode("utf-8"))
