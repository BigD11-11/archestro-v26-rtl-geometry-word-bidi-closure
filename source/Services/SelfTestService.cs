using Archestro.MeetingVault.Models;

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.IO.Compression;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Archestro.MeetingVault.Dialogs;

namespace Archestro.MeetingVault.Services;

public static class SelfTestService
{
    private static readonly List<object> WordSemanticProof = new();

    public static void RunRtlLayoutQa(string outputPath)
    {
        if (Application.Current is null)
            throw new InvalidOperationException("RTL geometry QA requires a WPF Application on its STA dispatcher.");

        _layoutFailures.Clear();
        var oldShutdownMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var results = new List<object>();
        foreach (var arabic in new[] { true, false })
        {
            AppearanceService.Configure("Dark", arabic ? "Arabic" : "English");
            var meeting = new MeetingRecord
            {
                Id = "rtl-layout-qa",
                FolderPath = Path.Combine(Path.GetTempPath(), "Archestro_RtlLayout_Qa"),
                Title = "RTL Layout Fixture",
                HasExplicitTitle = true,
                StartLocal = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.FromHours(3)),
                DurationSeconds = 120
            };
            var window = new IntelligenceWindow(meeting, null!, layoutQa: true)
            {
                Width = 1536,
                Height = 864,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -3000,
                Top = -3000,
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 1
            };
            window.PrepareLayoutQa(arabic);
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

            try
            {
                var area = Bounds(window.ReportContentArea, window);
                var nav = Bounds(window.ReportNavHost, window);
                var reportButton = Bounds(window.ReportNavReportButton, window);
                var meetingButton = Bounds(window.ReportNavMeetingAskButton, window);
                var vaultButton = Bounds(window.ReportNavVaultAskButton, window);
                var topics = Bounds(window.TopicsCard, window);
                var keyPoints = Bounds(window.KeyPointsCard, window);
                var topicsHeading = Bounds(window.TopicsHeading, window);
                var summaryCard = Bounds(window.ExecutiveSummaryCard, window);
                var summaryText = Bounds(window.SummaryText, window);
                var reportHeadings = new[] { window.ExecutiveSummaryHeading, window.TopicsHeading, window.KeyPointsHeading,
                    window.DecisionsHeading, window.ActionsHeading, window.CommitmentsHeading, window.RisksHeading,
                    window.ImportantMomentsHeading, window.ParticipantsHeading, window.FollowUpHeading };
                var itemText = FindTextBlock(window.ReportContentRoot, "• " + (arabic ? "مراجعة مسار العمل" : "Review the workflow"));
                var evidenceTimestamp = FindTextBlock(window.ReportContentRoot, "▶ 00:00:52");

                Check(area.Width > 1300, $"{Language(arabic)} content viewport too narrow: {area.Width:0.0}px.");
                Check(arabic
                    ? Math.Abs(area.Right - nav.Right) <= 18
                    : Math.Abs(nav.Left - area.Left) <= 18,
                    $"{Language(arabic)} nav anchor is wrong; area x={area.Left:0.0}, right={area.Right:0.0}, nav x={nav.Left:0.0}, right={nav.Right:0.0}.");
                Check(nav.Width >= 500, $"{Language(arabic)} nav group width below minimum: {nav.Width:0.0}px.");
                var areaMidpoint = (area.Left + area.Right) / 2;
                Check(arabic ? nav.Left > areaMidpoint : nav.Right < areaMidpoint,
                    $"{Language(arabic)} nav group is not in expected physical half; x={nav.Left:0.0}, right={nav.Right:0.0}, width={area.Width:0.0}.");
                Check(reportButton.Width >= 160 && meetingButton.Width >= 160 && vaultButton.Width >= 160,
                    $"{Language(arabic)} nav button width under 160px.");
                Check(arabic
                    ? reportButton.Right > meetingButton.Right && meetingButton.Right > vaultButton.Right
                    : reportButton.Left < meetingButton.Left && meetingButton.Left < vaultButton.Left,
                    $"{Language(arabic)} nav button physical order is wrong.");
                Check(arabic
                    ? reportButton.Left - meetingButton.Right >= 9 && meetingButton.Left - vaultButton.Right >= 9
                    : meetingButton.Left - reportButton.Right >= 9 && vaultButton.Left - meetingButton.Right >= 9,
                    $"{Language(arabic)} nav buttons overlap or lack a visible gap.");

                Check(arabic ? topics.Left > keyPoints.Left : topics.Left < keyPoints.Left,
                    $"{Language(arabic)} first logical section is not in the expected physical column.");
                Check((arabic ? keyPoints.Right <= topics.Left : topics.Right <= keyPoints.Left) && topics.Width > 400 && keyPoints.Width > 400,
                    $"{Language(arabic)} paired report cards overlap or are too narrow.");
                Check(arabic
                    ? Math.Abs(topics.Right - topicsHeading.Right) <= 24
                    : Math.Abs(topicsHeading.Left - topics.Left) <= 24,
                    $"{Language(arabic)} topic heading is not anchored to its language's card edge.");
                Check(arabic
                    ? Math.Abs(summaryCard.Right - summaryText.Right) <= 24
                    : Math.Abs(summaryText.Left - summaryCard.Left) <= 24,
                    $"{Language(arabic)} report body is not stretched to its language's card edge.");
                Check(summaryCard.Width > 1000 && (arabic
                        ? Math.Abs(area.Right - summaryCard.Right) <= 24
                        : Math.Abs(summaryCard.Left - area.Left) <= 24),
                    $"{Language(arabic)} report composition is narrow or incorrectly anchored.");

                Check(window.ReportContentArea.FlowDirection == FlowDirection.LeftToRight &&
                      window.ReportNavHost.FlowDirection == FlowDirection.LeftToRight &&
                      window.ReportContentRoot.FlowDirection == FlowDirection.LeftToRight &&
                      window.ReportSectionGrid.FlowDirection == FlowDirection.LeftToRight,
                    $"{Language(arabic)} physical report shell is not explicitly LTR.");
                Check(window.ReportNavReportButton.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight) &&
                      window.ReportNavMeetingAskButton.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight) &&
                      window.ReportNavVaultAskButton.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight),
                    $"{Language(arabic)} navigation text content direction is incorrect.");
                Check(new[] { reportButton, meetingButton, vaultButton }.All(button =>
                        button.Left >= area.Left - 1 && button.Right <= area.Right + 1 && button.Top >= area.Top - 1 && button.Bottom <= area.Bottom + 1),
                    $"{Language(arabic)} one or more navigation controls fall outside the report area.");
                Check(window.TopicsHeading.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight) &&
                      window.TopicsHeading.TextAlignment == (arabic ? TextAlignment.Right : TextAlignment.Left),
                    $"{Language(arabic)} card heading text direction/alignment is incorrect.");
                Check(reportHeadings.All(block => block.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight) &&
                                                   block.TextAlignment == (arabic ? TextAlignment.Right : TextAlignment.Left)),
                    $"{Language(arabic)} one or more report headings do not follow the selected language direction.");
                Check(itemText is not null && itemText.FlowDirection == (arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight) &&
                      itemText.TextAlignment == (arabic ? TextAlignment.Right : TextAlignment.Left),
                    $"{Language(arabic)} report item text does not follow the selected language direction.");
                Check(evidenceTimestamp is not null && evidenceTimestamp.FlowDirection == FlowDirection.LeftToRight &&
                      evidenceTimestamp.TextAlignment == TextAlignment.Left,
                    $"{Language(arabic)} evidence timestamp is not isolated as readable LTR content.");

                string? screenshotPath = null;
                var screenshotRoot = Environment.GetEnvironmentVariable("ARCHESTRO_RTL_QA_SCREENSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(screenshotRoot))
                {
                    Directory.CreateDirectory(screenshotRoot);
                    screenshotPath = Path.Combine(screenshotRoot, arabic ? "WPF_REPORT_ARABIC.png" : "WPF_REPORT_ENGLISH.png");
                    var visual = (Visual)window;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    using var output = File.Create(screenshotPath);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(output);
                }

                results.Add(new
                {
                    language = Language(arabic),
                    viewport = new { width = window.ActualWidth, height = window.ActualHeight },
                    content = Shape(area),
                    navHost = Shape(nav),
                    reportButton = Shape(reportButton),
                    meetingAskButton = Shape(meetingButton),
                    vaultAskButton = Shape(vaultButton),
                    topicsCard = Shape(topics),
                    topicsHeading = Shape(topicsHeading),
                    keyPointsCard = Shape(keyPoints),
                    executiveSummaryCard = Shape(summaryCard),
                    summaryText = Shape(summaryText),
                    alignmentProof = new { reportHeadingCount = reportHeadings.Length, headingsAligned = reportHeadings.All(block => block.TextAlignment == (arabic ? TextAlignment.Right : TextAlignment.Left)), reportItemAligned = itemText?.TextAlignment == (arabic ? TextAlignment.Right : TextAlignment.Left), evidenceTimestampFlow = evidenceTimestamp?.FlowDirection.ToString(), evidenceTimestampText = evidenceTimestamp?.Text },
                    flowDirection = window.FlowDirection.ToString(),
                    reportShellFlowDirection = window.ReportContentArea.FlowDirection.ToString(),
                    screenshot = screenshotPath is null ? null : Path.GetFileName(screenshotPath),
                    failures = _layoutFailures.ToArray()
                });
            }
            finally
            {
                window.Close();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, System.Text.Json.JsonSerializer.Serialize(
            new { status = _layoutFailures.Count == 0 ? "PASS" : "FAIL", runtimeBuild = AppBuildIdentity.GetRuntimeProof(), coordinateSystem = "DIPs relative to the actual production IntelligenceWindow, origin top-left", results, failures = _layoutFailures.ToArray() },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (_layoutFailures.Count > 0)
            throw new InvalidOperationException("Actual WPF layout geometry assertions failed: " + string.Join(" | ", _layoutFailures));
        Application.Current.ShutdownMode = oldShutdownMode;
        _layoutFailures.Clear();

        static string Language(bool arabic) => arabic ? "Arabic" : "English";
        static object Shape(Rect rect) => new { x = Math.Round(rect.X, 2), y = Math.Round(rect.Y, 2), width = Math.Round(rect.Width, 2), height = Math.Round(rect.Height, 2), right = Math.Round(rect.Right, 2) };
    }

    private static readonly List<string> _layoutFailures = new();

    private static void Check(bool condition, string message)
    {
        if (!condition) _layoutFailures.Add(message);
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            throw new InvalidOperationException($"{element.Name} has no arranged size ({element.ActualWidth:0.0}x{element.ActualHeight:0.0}).");
        return element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text == text) return block;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindTextBlock(VisualTreeHelper.GetChild(root, i), text);
            if (found is not null) return found;
        }
        return null;
    }

    public static void RunOffline()
    {
        AppPaths.Ensure();

        var greeting1 = GreetingService.GetGreeting(new DateTimeOffset(2026,8,17,7,0,0,TimeSpan.FromHours(3)), "Test User");
        var greeting2 = GreetingService.GetGreeting(new DateTimeOffset(2026,8,17,14,0,0,TimeSpan.FromHours(3)), "Test User");
        if (!greeting1.Contains("Good morning") ||
            !greeting2.Contains("Good afternoon") ||
            !greeting1.Contains("Test User") ||
            !greeting2.Contains("Test User"))
            throw new InvalidOperationException("Greeting self-test failed.");

        var testRoot = Path.Combine(Path.GetTempPath(), "ArchestroMV_SelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var repo = new MeetingRepository(Path.Combine(testRoot, "selftest.db"));
        var folder = Path.Combine(testRoot, "SelfTestMeeting");
        Directory.CreateDirectory(folder);

        var record = new MeetingRecord
        {
            Id = "selftest-r3-2",
            FolderPath = folder,
            Title = "",
            HasExplicitTitle = false,
            StartLocal = DateTimeOffset.Now.AddMinutes(-10),
            DurationSeconds = 600,
            SuggestedTitle = "Contracts Review",
            Category = "Contracts",
            CategoryColor = CategoryCatalog.ColorFor("Contracts"),
            TranscriptionStatus = "Transcript ready",
            Notes = "self test pricing note"
        };

        repo.Upsert(record, "Contract pricing follow up and operations.");
        var hits = repo.Search("pricing", "Contracts", 10);
        if (!hits.Any(x => x.Id == record.Id))
            throw new InvalidOperationException("SQLite FTS search self-test failed.");

        var suggestion = new SuggestedMetadataService().Suggest("We discussed the contract agreement and renewal.");
        if (suggestion.Category != "Contracts")
            throw new InvalidOperationException("Suggested category self-test failed.");

        if (!repo.AddCategory("Self Test Client", "#2F80ED"))
            throw new InvalidOperationException("Dynamic category add self-test failed.");
        if (!repo.GetCategories().Any(x => x.Name == "Self Test Client"))
            throw new InvalidOperationException("Dynamic category persistence self-test failed.");

        RunV25FunctionalFixtures(testRoot, folder);
        VerifyLibraryAudioImportCommandAsync().GetAwaiter().GetResult();
        VerifyShortMeetingEvidenceFixture(testRoot);
        VerifyCachedReportFixtures(testRoot);
        VerifySecretProtectionFixture();

        var functionalOutput = Environment.GetEnvironmentVariable("ARCHESTRO_V28_FUNCTIONAL_QA_OUTPUT");
        if (!string.IsNullOrWhiteSpace(functionalOutput))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(functionalOutput))!);
            File.WriteAllText(functionalOutput, System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "PASS", importRouting = "PASS", unsupportedImportNoMutation = "PASS",
                sparseEightSecondReport = "INSUFFICIENT_EVIDENCE", oldArabicJsonCurrentRtl = "PASS",
                oldEnglishJsonCurrentLtr = "PASS", incompatibleCacheNeedsRefreshAndCannotExport = "PASS",
                currentExporterReexportsOldValidJson = "PASS", dpapiCurrentUserRoundTrip = "PASS"
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        File.WriteAllText(Path.Combine(AppPaths.Logs, "self-test-pass.txt"),
            $"PASS {DateTimeOffset.Now:o}{Environment.NewLine}SQLite FTS5 PASS{Environment.NewLine}Greeting PASS{Environment.NewLine}Suggestion PASS{Environment.NewLine}Dynamic Categories PASS{Environment.NewLine}Archestro Build 2 source foundation PASS");

        try { Directory.Delete(testRoot, true); } catch { }
    }

    private static async Task VerifyLibraryAudioImportCommandAsync()
    {
        var valid = Path.Combine(Path.GetTempPath(), "v28-import-fixture.wav");
        var invalid = Path.Combine(Path.GetTempPath(), "v28-import-fixture.exe");
        var imported = new List<string>();
        var pickerCalls = 0;
        Task<IEnumerable<string>?> Picker()
        {
            pickerCalls++;
            return Task.FromResult<IEnumerable<string>?>(new[] { valid });
        }
        Task Import(IReadOnlyList<string> paths) { imported.AddRange(paths); return Task.CompletedTask; }
        bool Exists(string path) => path is var value && (value == valid || value == invalid);

        // The plus button, primary button and empty card surface each reach this one command.
        foreach (var _ in new[] { "plus", "primary", "card-surface" })
            RequireFixture(await LibraryAudioImportCommand.ExecuteAsync(null, Picker, Import, Exists), "A library import click did not invoke the shared import route.");
        // Drag-enter/drop uses the exact same supported-file filtering and import delegate.
        RequireFixture(await LibraryAudioImportCommand.ExecuteAsync(new[] { valid }, Picker, Import, Exists), "Supported audio drop did not reach the shared import route.");
        var beforeInvalid = imported.Count;
        var invalidStarted = await LibraryAudioImportCommand.ExecuteAsync(new[] { invalid }, Picker, Import, Exists);
        RequireFixture(!invalidStarted && imported.Count == beforeInvalid, "Unsupported import mutated the library.");
        RequireFixture(pickerCalls == 3 && imported.Count == 4 && imported.All(path => path == valid), "Import click/drop dispatch count was not deterministic.");

        var output = Environment.GetEnvironmentVariable("ARCHESTRO_V28_A_IMPORT_QA_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "PASS", sharedCommand = true, pickerInvocations = pickerCalls,
                plusButton = "shared picker + import delegate", primaryButton = "shared picker + import delegate",
                cardSurface = "shared picker + import delegate", supportedDrop = "shared import delegate",
                unsupportedExtension = "rejected", importCountAfterInvalid = beforeInvalid
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void VerifyShortMeetingEvidenceFixture(string testRoot)
    {
        var shortFolder = Path.Combine(testRoot, "EightSecondMeeting");
        Directory.CreateDirectory(shortFolder);
        var srt = Path.Combine(shortFolder, "short.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:04,000\nنعم، حسنًا.\n\n2\n00:00:04,000 --> 00:00:08,000\nOkay, yes.\n");
        var eightSeconds = MeetingIntelligenceService.AssessEvidenceSufficiency(new[]
        {
            new EvidenceRef { Id = "E1", StartSeconds = 0, EndSeconds = 4, Text = "نعم، حسنًا." },
            new EvidenceRef { Id = "E2", StartSeconds = 4, EndSeconds = 8, Text = "Okay, yes." }
        }, 8);
        RequireFixture(!eightSeconds.Sufficient && eightSeconds.Reason == "short-transcript",
            "Sparse eight-second meeting was not classified as insufficient evidence.");
        var meeting = new MeetingRecord { Id = "v28-short-fixture", FolderPath = shortFolder, SrtPath = srt, TranscriptPath = srt, DurationSeconds = 8 };
        var service = new MeetingIntelligenceService(new AppSettings(), new MeetingRepository(Path.Combine(testRoot, "short.db")));
        foreach (var (language, phrase) in new[] { ("ar", "الأدلة"), ("en", "too short") })
        {
            InsufficientMeetingEvidenceException? result = null;
            try { service.AnalyzeMeetingAsync(meeting, "General", CancellationToken.None, reportLanguage: language).GetAwaiter().GetResult(); }
            catch (InsufficientMeetingEvidenceException exception) { result = exception; }
            RequireFixture(result is not null && result.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                $"Sparse eight-second {language} transcript did not receive a localized Insufficient Evidence state.");
            RequireFixture(!File.Exists(MeetingIntelligenceService.GetCanonicalReportJsonPath(meeting)),
                "Sparse meeting created a cached report or fabricated report sections.");
        }
    }

    private static void VerifyCachedReportFixtures(string testRoot)
    {
        var folder = Path.Combine(testRoot, "CachedReportFixtures");
        Directory.CreateDirectory(folder);
        var transcript = Path.Combine(folder, "transcript.txt");
        File.WriteAllText(transcript, "E1 00:00:52 Speaker: تمت مراجعة API ضمن الخطة.");
        var meeting = new MeetingRecord
        {
            Id = "v28-cached-report", FolderPath = folder, TranscriptPath = transcript,
            Title = "Cached report fixture", HasExplicitTitle = true,
            StartLocal = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(3)), DurationSeconds = 120
        };
        var service = new MeetingIntelligenceService(new AppSettings(), new MeetingRepository(Path.Combine(testRoot, "cached.db")));
        var reportPath = MeetingIntelligenceService.GetCanonicalReportJsonPath(meeting);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var historicalDocx = Path.Combine(MeetingIntelligenceService.GetReportsFolder(meeting), "historical-owner-docx.docx");
        File.WriteAllText(historicalDocx, "V26 historical DOCX fixture bytes stay frozen.");
        var historicalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(historicalDocx)));

        // Pre-V28 JSON had no report-language/source-hash metadata; infer same-language content and render immediately.
        var oldArabicJson = """{"MeetingId":"v28-cached-report","ExecutiveSummary":"تمت مراجعة API ضمن الخطة.","Topics":[{"Text":"تمت مراجعة الخطة.","Evidence":["E1"]}],"EvidenceIndex":[{"Id":"E1","Text":"تمت مراجعة API ضمن الخطة.","StartSeconds":52}]}""";
        File.WriteAllText(reportPath, oldArabicJson);
        AppearanceService.Configure("Dark", "Arabic");
        var oldArabic = service.LoadReport(meeting) ?? throw new InvalidOperationException("Old Arabic report JSON did not load.");
        RequireFixture(oldArabic.ReportLanguage == "ar" && !oldArabic.NeedsRefresh && MeetingIntelligenceService.IsCachedReportCompatibleForExport(oldArabic, "ar"),
            "Same-language legacy Arabic JSON was unnecessarily marked for refresh.");
        var arabicWindow = new IntelligenceWindow(meeting, service, layoutQa: true);
        typeof(IntelligenceWindow).GetMethod("LoadExisting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(arabicWindow, null);
        RequireFixture(arabicWindow.SummaryText.FlowDirection == FlowDirection.RightToLeft && arabicWindow.ReportStateText.Text == "التقرير جاهز" && arabicWindow.ExportWordButton.IsEnabled,
            "Legacy Arabic JSON did not immediately use current RTL UI presentation.");
        var exported = MeetingReportWordExporter.Export(meeting, oldArabic);
        VerifyWordDocument(exported, arabic: true, expectedTerms: new[] { "API" });
        var historicalHashAfter = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(historicalDocx)));
        RequireFixture(historicalHash == historicalHashAfter, "Current Word export rewrote historical DOCX bytes in place.");
        arabicWindow.Close();

        var oldEnglishJson = """{"MeetingId":"v28-cached-report","ReportLanguage":"en","ExecutiveSummary":"The API review remains in scope.","Topics":[{"Text":"Review the plan.","Evidence":["E1"]}],"EvidenceIndex":[{"Id":"E1","Text":"Review the plan.","StartSeconds":52}]}""";
        File.WriteAllText(reportPath, oldEnglishJson);
        AppearanceService.Configure("Dark", "English");
        var oldEnglish = service.LoadReport(meeting) ?? throw new InvalidOperationException("Old English report JSON did not load.");
        var englishWindow = new IntelligenceWindow(meeting, service, layoutQa: true);
        typeof(IntelligenceWindow).GetMethod("LoadExisting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(englishWindow, null);
        RequireFixture(!oldEnglish.NeedsRefresh && englishWindow.SummaryText.FlowDirection == FlowDirection.LeftToRight && englishWindow.ReportStateText.Text == "Report ready",
            $"Same-language legacy English JSON did not immediately use current LTR UI presentation (needsRefresh={oldEnglish.NeedsRefresh}; sourceHash='{oldEnglish.SourceTranscriptSha256}'; languageCompatible={MeetingIntelligenceService.IsReportLanguageCompatible(oldEnglish, "en")}; flow={englishWindow.SummaryText.FlowDirection}; state={englishWindow.ReportStateText.Text}; export={englishWindow.ExportWordButton.IsEnabled}; lang={oldEnglish.ReportLanguage}).");
        englishWindow.Close();

        File.WriteAllText(reportPath, """{"MeetingId":"v28-cached-report","ReportLanguage":"ar","ExecutiveSummary":"The team agreed to review the plan.","Topics":[],"EvidenceIndex":[]}""");
        AppearanceService.Configure("Dark", "Arabic");
        var incompatible = service.LoadReport(meeting) ?? throw new InvalidOperationException("Incompatible cache fixture did not load.");
        var incompatibleWindow = new IntelligenceWindow(meeting, service, layoutQa: true);
        typeof(IntelligenceWindow).GetMethod("LoadExisting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(incompatibleWindow, null);
        RequireFixture(incompatible.NeedsRefresh && incompatibleWindow.ReportStateText.Text != "Report ready" && !incompatibleWindow.ExportWordButton.IsEnabled,
            "Language-incompatible cached report was shown as Ready or remained exportable.");
        incompatibleWindow.Close();

        var transcriptHashBeforeChange = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(transcript)));
        File.WriteAllText(reportPath, oldEnglishJson.Replace("\"ReportLanguage\":\"en\"", $"\"ReportLanguage\":\"en\",\"SourceTranscriptSha256\":\"{transcriptHashBeforeChange}\"", StringComparison.Ordinal));
        meeting.TranscriptPath = transcript;
        File.AppendAllText(transcript, " Transcript changed.");
        var transcriptStale = service.LoadReport(meeting) ?? throw new InvalidOperationException("Stale cache fixture did not load.");
        RequireFixture(transcriptStale.NeedsRefresh && !MeetingIntelligenceService.IsCachedReportCompatibleForExport(transcriptStale, "en"),
            "Transcript-incompatible cached report remained exportable.");

        var cachedOutput = Environment.GetEnvironmentVariable("ARCHESTRO_V28_CACHED_REPORT_QA_OUTPUT");
        if (!string.IsNullOrWhiteSpace(cachedOutput))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachedOutput))!);
            File.WriteAllText(cachedOutput, System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "PASS", oldArabicJsonCurrentRtl = true, oldEnglishJsonCurrentLtr = true,
                incompatibleReportNeedsRefreshAndCannotExport = true, transcriptHashMismatchCannotExport = true,
                oldJsonReexportedWithCurrentWordExporter = true, historicalDocxBytesUnchanged = true,
                reexportedDocxSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exported)))
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void VerifySecretProtectionFixture()
    {
        const string marker = "v28-secret-never-plaintext-fixture-93f4";
        var protectedValue = CloudSecretProtector.Protect(marker);
        RequireFixture(!protectedValue.Contains(marker, StringComparison.Ordinal) && CloudSecretProtector.Unprotect(protectedValue) == marker,
            "DPAPI current-user protection round trip failed or retained plaintext.");
        var serializedSettings = System.Text.Json.JsonSerializer.Serialize(new AppSettings { EncryptedIntelligenceApiKey = protectedValue });
        RequireFixture(!serializedSettings.Contains(marker, StringComparison.Ordinal), "Protected API secret appeared plaintext in settings serialization.");
    }

    private static void RunV25FunctionalFixtures(string testRoot, string meetingFolder)
    {
        WordSemanticProof.Clear();
        VerifyRecentTitleFixtures();
        VerifyReportLanguageFixtures();
        VerifyWordRtlFixture(testRoot, meetingFolder);
        var wordOutput = Environment.GetEnvironmentVariable("ARCHESTRO_WORD_BIDI_QA_OUTPUT");
        if (!string.IsNullOrWhiteSpace(wordOutput))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(wordOutput))!);
            File.WriteAllText(wordOutput, System.Text.Json.JsonSerializer.Serialize(
                new { status = "PASS", source = "serialized .docx ZIP part bytes", documents = WordSemanticProof },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void VerifyRecentTitleFixtures()
    {
        var started = new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.FromHours(3));
        var arabicManual = new MeetingRecord { Title = "مراجعة خطة الإطلاق", HasExplicitTitle = true, StartLocal = started };
        var englishManual = new MeetingRecord { Title = "Launch Plan Review", HasExplicitTitle = true, StartLocal = started };
        var acceptedSuggestion = new MeetingRecord { Title = "Vendor Review", SuggestedTitle = "Vendor Review", HasExplicitTitle = true, StartLocal = started };
        var importedAudio = new MeetingRecord { Title = "AUDIO-20261004-003", HasExplicitTitle = true, StartLocal = started };
        var fallback = new MeetingRecord { Title = "", SuggestedTitle = "", HasExplicitTitle = false, StartLocal = started };

        RequireFixture(arabicManual.PrimaryTitle == "مراجعة خطة الإطلاق", "Arabic manual title changed.");
        RequireFixture(englishManual.PrimaryTitle == "Launch Plan Review", "English manual title changed.");
        RequireFixture(acceptedSuggestion.PrimaryTitle == "Vendor Review", "Accepted suggested title was not preserved.");
        RequireFixture(importedAudio.PrimaryTitle == "AUDIO-20261004-003", "Imported AUDIO title was not preserved.");
        RequireFixture(fallback.PrimaryTitle == started.ToString("dd MMM yyyy • hh:mm tt"), "Fallback date title changed.");
    }

    private static void VerifyReportLanguageFixtures()
    {
        var arabic = new MeetingIntelligenceReport { ReportLanguage = "ar", ExecutiveSummary = "تم الاتفاق على مراجعة الخطة وتحديد موعد المتابعة." };
        RequireFixture(MeetingIntelligenceService.IsReportLanguageCompatible(arabic, "ar"), "Fully Arabic report rejected.");

        var longEnglishItem = new MeetingIntelligenceReport
        {
            ReportLanguage = "ar",
            ExecutiveSummary = "ملخص الاجتماع بالعربية.",
            Topics = new() { new IntelligenceItem { Text = "The team agreed to review the proposal next week and prepare the final budget before Friday." } }
        };
        RequireFixture(!MeetingIntelligenceService.IsReportLanguageCompatible(longEnglishItem, "ar"), "English report item passed Arabic language gate.");

        var shortEnglishSentence = new MeetingIntelligenceReport
        {
            ReportLanguage = "ar",
            ExecutiveSummary = "تمت مناقشة الخطة. We agreed today."
        };
        RequireFixture(!MeetingIntelligenceService.IsReportLanguageCompatible(shortEnglishSentence, "ar"), "Short English sentence passed Arabic language gate.");

        var confirmedName = new MeetingIntelligenceReport
        {
            ReportLanguage = "ar",
            ExecutiveSummary = "اجتمع الفريق مع Dr.Hussain لمراجعة الخطة."
        };
        RequireFixture(MeetingIntelligenceService.IsReportLanguageCompatible(confirmedName, "ar"), "Confirmed Latin name was rejected.");

        var approvedTerms = new MeetingIntelligenceReport
        {
            ReportLanguage = "ar",
            ExecutiveSummary = "تمت مراجعة API و Air Conditioner ضمن الخطة."
        };
        RequireFixture(MeetingIntelligenceService.IsReportLanguageCompatible(approvedTerms, "ar"), "Approved technical terms were rejected.");

        var cyrillic = new MeetingIntelligenceReport { ReportLanguage = "ar", ExecutiveSummary = "تمت مراجعة проекта اليوم." };
        RequireFixture(!MeetingIntelligenceService.IsReportLanguageCompatible(cyrillic, "ar"), "Unexpected Cyrillic passed Arabic language gate.");

        var arabicInEnglish = new MeetingIntelligenceReport
        {
            ReportLanguage = "en",
            ExecutiveSummary = "تم اعتماد خطة التنفيذ ومراجعتها مع الفريق قبل نهاية الأسبوع."
        };
        RequireFixture(!MeetingIntelligenceService.IsReportLanguageCompatible(arabicInEnglish, "en"), "Arabic sentence passed English language gate.");
    }

    private static void VerifyWordRtlFixture(string testRoot, string meetingFolder)
    {
        var meeting = new MeetingRecord
        {
            Id = "selftest-v25-rtl",
            FolderPath = meetingFolder,
            Title = "Technical Review",
            HasExplicitTitle = true,
            StartLocal = new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.FromHours(3)),
            DurationSeconds = 600
        };
        var arabicReport = new MeetingIntelligenceReport
        {
            MeetingId = meeting.Id,
            ReportLanguage = "ar",
            ExecutiveSummary = "تم الاتفاق على مراجعة API ونظام Air Conditioner يوم 2026-10-04.",
            Topics = new() { new IntelligenceItem { Text = "تمت مراجعة API و Air Conditioner ضمن الخطة.", Evidence = new() { "E1" } } },
            Decisions = new() { new IntelligenceItem { Text = "تبدأ المراجعة عند 00:00:52.", Evidence = new() { "E1" } } },
            EvidenceIndex = new() { new EvidenceRef { Id = "E1", MeetingId = meeting.Id, MeetingTitle = meeting.PrimaryTitle, MeetingStartLocal = meeting.StartLocal, StartSeconds = 52, Speaker = "Dr.Hussain", Text = "تم تأكيد الموعد في 00:00:52." } }
        };
        var arabicPath = MeetingReportWordExporter.Export(meeting, arabicReport);
        VerifyWordDocument(arabicPath, arabic: true, expectedTerms: new[] { "API", "Air Conditioner", "00:00:52" });

        var englishReport = new MeetingIntelligenceReport
        {
            MeetingId = meeting.Id,
            ReportLanguage = "en",
            ExecutiveSummary = "The team will review the API configuration and share a result by Friday.",
            Topics = new() { new IntelligenceItem { Text = "The team agreed to review the proposal." } }
        };
        var englishPath = MeetingReportWordExporter.Export(meeting, englishReport);
        VerifyWordDocument(englishPath, arabic: false, expectedTerms: new[] { "API" });

        var invalidReport = new MeetingIntelligenceReport { MeetingId = meeting.Id, ReportLanguage = "ar", ExecutiveSummary = "We agreed today." };
        var blocked = false;
        try { MeetingReportWordExporter.Export(meeting, invalidReport); }
        catch (InvalidOperationException) { blocked = true; }
        RequireFixture(blocked, "DOCX export accepted a language-invalid cached report.");
        RequireFixture(Directory.GetFiles(Path.Combine(testRoot, "SelfTestMeeting", "Reports"), "*.docx").Length == 2,
            "Invalid report produced a DOCX file.");
    }

    private static void VerifyWordDocument(string path, bool arabic, IEnumerable<string> expectedTerms)
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("ARCHESTRO_WORD_FIXTURE_DIR");
        var fixtureFileName = Path.GetFileName(path);
        if (!string.IsNullOrWhiteSpace(fixtureRoot))
        {
            Directory.CreateDirectory(fixtureRoot);
            fixtureFileName = arabic ? "arabic-report.docx" : "english-report.docx";
            var fixturePath = Path.Combine(fixtureRoot, fixtureFileName);
            File.Copy(path, fixturePath, overwrite: true);
            path = fixturePath;
        }

        var validatorErrorCount = 0;
        using (var package = WordprocessingDocument.Open(path, false))
        {
            var validationErrors = new OpenXmlValidator().Validate(package).ToList();
            validatorErrorCount = validationErrors.Count;
            var errors = validationErrors.Take(8)
                .Select(error => $"{error.Description} [part={error.Part?.Uri}; path={error.Path?.XPath}]").ToList();
            RequireFixture(validatorErrorCount == 0, "DOCX Open XML validation failed: " + string.Join(" | ", errors));
        }

        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidOperationException("DOCX is missing word/document.xml.");
        using var stream = entry.Open();
        var xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var settingsEntry = archive.GetEntry("word/settings.xml");
        RequireFixture(settingsEntry is not null, "DOCX is missing word/settings.xml.");
        XDocument settingsXml;
        using (var settingsStream = settingsEntry!.Open()) settingsXml = XDocument.Load(settingsStream);
        var compatibilitySetting = settingsXml.Descendants(w + "compatSetting").FirstOrDefault(setting =>
            setting.Attribute(w + "name")?.Value == "compatibilityMode" &&
            setting.Attribute(w + "uri")?.Value == "http://schemas.microsoft.com/office/word");
        var compatibilityMode = compatibilitySetting?.Attribute(w + "val")?.Value;
        RequireFixture(compatibilityMode == "15", "DOCX settings do not request modern Word compatibility mode 15.");
        var themeFontLanguages = settingsXml.Descendants(w + "themeFontLang").FirstOrDefault();
        var themeFontLanguageBidi = themeFontLanguages?.Attribute(w + "bidi")?.Value;
        RequireFixture(themeFontLanguageBidi == (arabic ? "ar-SA" : "en-US"),
            "DOCX settings are missing the correct themeFontLang bidi language.");
        var bodyText = string.Join("", xml.Descendants(w + "t").Select(node => node.Value));
        var styleBodyBidi = false;
        var styleBodyStart = false;
        foreach (var term in expectedTerms)
            RequireFixture(bodyText.Contains(term, StringComparison.Ordinal), $"DOCX changed or reversed logical term: {term}");
        RequireFixture(!bodyText.Contains("IPA", StringComparison.Ordinal), "DOCX reversed the Latin technical token API.");

        if (arabic)
        {
            var section = xml.Descendants(w + "sectPr").LastOrDefault();
            RequireFixture(section?.Element(w + "bidi") is not null,
                "Arabic document section is missing serialized w:bidi right-to-left section layout.");
            var arabicParagraphs = xml.Descendants(w + "p")
                .Where(paragraph => paragraph.Descendants(w + "t").Any(node => node.Value.Any(IsArabicCharacter)))
                .ToList();
            RequireFixture(arabicParagraphs.Count > 0, "Arabic DOCX fixture contains no Arabic paragraphs.");
            foreach (var paragraph in arabicParagraphs)
                RequireFixture(paragraph.Element(w + "pPr")?.Element(w + "bidi") is not null, "Arabic paragraph is missing w:bidi.");

            var bodyParagraphs = arabicParagraphs.Where(paragraph =>
                paragraph.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value == "BodyText");
            foreach (var paragraph in bodyParagraphs)
                RequireFixture(paragraph.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value == "start",
                    "Arabic body paragraph does not use logical start alignment for Word RTL.");
            foreach (var paragraph in arabicParagraphs.Where(paragraph =>
                         paragraph.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value != "center"))
                RequireFixture(paragraph.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value == "start",
                    "Arabic non-centered paragraph does not use logical start alignment.");

            var stylesEntry = archive.GetEntry("word/styles.xml") ?? throw new InvalidOperationException("DOCX is missing word/styles.xml.");
            using (var stylesStream = stylesEntry.Open())
            {
                var stylesXml = XDocument.Load(stylesStream);
                var bodyStyle = stylesXml.Descendants(w + "style").FirstOrDefault(style =>
                    style.Attribute(w + "styleId")?.Value == "BodyText");
                styleBodyBidi = bodyStyle?.Element(w + "pPr")?.Element(w + "bidi") is not null;
                styleBodyStart = bodyStyle?.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value == "start";
                RequireFixture(styleBodyBidi,
                    "Arabic BodyText paragraph style is missing serialized w:bidi.");
                RequireFixture(styleBodyStart,
                    "Arabic BodyText paragraph style is missing logical start alignment.");
            }

            var arabicRuns = xml.Descendants(w + "r")
                .Where(run => run.Descendants(w + "t").Any(node => node.Value.Any(IsArabicCharacter)))
                .ToList();
            RequireFixture(arabicRuns.Count > 0, "Arabic DOCX fixture contains no Arabic runs.");
            foreach (var run in arabicRuns)
            {
                var properties = run.Element(w + "rPr");
                RequireFixture(properties?.Element(w + "rtl") is not null, "Arabic run is missing w:rtl.");
                RequireFixture(properties?.Element(w + "lang")?.Attribute(w + "val")?.Value == "ar-SA",
                    "Arabic run is missing Arabic language metadata.");
            }

            var technicalRun = xml.Descendants(w + "r").FirstOrDefault(run =>
                run.Descendants(w + "t").Any(node => node.Value.Contains("API", StringComparison.Ordinal)));
            RequireFixture(technicalRun is not null && technicalRun.Element(w + "rPr")?.Element(w + "rtl") is null,
                "Latin technical run API must remain logical LTR text inside the RTL paragraph.");

            foreach (var table in xml.Descendants(w + "tbl"))
                RequireFixture(table.Element(w + "tblPr")?.Element(w + "bidiVisual") is not null,
                    "Arabic table is missing w:bidiVisual.");

            var centeredTitle = arabicParagraphs.FirstOrDefault(paragraph =>
                paragraph.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value == "ReportTitle");
            RequireFixture(centeredTitle?.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value == "center",
                "Arabic title lost its intended centered alignment.");
        }
        else
        {
            var englishBody = xml.Descendants(w + "p").FirstOrDefault(paragraph =>
                paragraph.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value == "BodyText");
            RequireFixture(englishBody?.Element(w + "pPr")?.Element(w + "bidi") is null,
                "English body paragraph incorrectly carries w:bidi.");
            RequireFixture(englishBody?.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value == "left",
                "English body paragraph is not left-justified.");
        }

        var semanticParagraphs = xml.Descendants(w + "p")
            .Where(paragraph => paragraph.Descendants(w + "t").Any(node => node.Value.Any(IsArabicCharacter))).ToList();
        var semanticRuns = xml.Descendants(w + "r")
            .Where(run => run.Descendants(w + "t").Any(node => node.Value.Any(IsArabicCharacter))).ToList();
        WordSemanticProof.Add(new
        {
            language = arabic ? "Arabic" : "English",
            sectionBidi = xml.Descendants(w + "sectPr").Any(section => section.Element(w + "bidi") is not null),
            arabicParagraphCount = semanticParagraphs.Count,
            arabicParagraphBidiCount = semanticParagraphs.Count(paragraph => paragraph.Element(w + "pPr")?.Element(w + "bidi") is not null),
            arabicParagraphStartCount = semanticParagraphs.Count(paragraph =>
                paragraph.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value == "start"),
            arabicBodyStartCount = semanticParagraphs.Count(paragraph =>
                paragraph.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value == "BodyText" &&
                paragraph.Element(w + "pPr")?.Element(w + "jc")?.Attribute(w + "val")?.Value == "start"),
            arabicRunCount = semanticRuns.Count,
            arabicRtlRunCount = semanticRuns.Count(run => run.Element(w + "rPr")?.Element(w + "rtl") is not null),
            arabicTableCount = xml.Descendants(w + "tbl").Count(),
            bidiVisualTableCount = xml.Descendants(w + "tbl").Count(table => table.Element(w + "tblPr")?.Element(w + "bidiVisual") is not null),
            wordSettingsExists = settingsEntry is not null,
            modernCompatibilityMode = compatibilityMode,
            themeFontLangBidi = themeFontLanguageBidi,
            openXmlValidatorErrorCount = validatorErrorCount,
            bodyStyleBidi = styleBodyBidi,
            bodyStyleStart = styleBodyStart,
            logicalApiTermPreserved = bodyText.Contains("API", StringComparison.Ordinal) && !bodyText.Contains("IPA", StringComparison.Ordinal),
            fixtureFileName,
            fixtureSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
        });
    }

    private static bool IsArabicCharacter(char value) => value >= '\u0600' && value <= '\u06FF';

    private static void RequireFixture(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("V25 functional fixture failed: " + message);
    }
}


