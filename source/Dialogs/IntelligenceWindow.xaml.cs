using System.Text;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Archestro.MeetingVault.Models;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault.Dialogs;

public partial class IntelligenceWindow : Window
{
    private sealed record ReportLensOption(string Key, string Label, string Description)
    {
        public override string ToString() => Label;
    }
    private readonly MeetingRecord _meeting;
    private readonly MeetingIntelligenceService _service;
    private readonly MeetingAudioPlayer _player = new();
    private MeetingIntelligenceReport? _report;
    private bool _busy;
    private CancellationTokenSource? _reportGenerationCts;
    private readonly DispatcherTimer _reportElapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _reportStarted;
    private string? _lastWordReportPath;
    private readonly bool _openReportOnLoad;
    private readonly bool _layoutQa;
    private bool _syncNavigation;

    public IntelligenceWindow(MeetingRecord meeting, MeetingIntelligenceService service, bool openReportOnLoad = false, bool layoutQa = false)
    {
        InitializeComponent();
        AppearanceService.Apply(this);
        FlowDirection = AppearanceService.IsArabic
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        _meeting = meeting;
        _service = service;
        _openReportOnLoad = openReportOnLoad;
        _layoutQa = layoutQa;

        ChromeTitleText.Text = T("Archestro Meeting Intelligence", "ذكاء اجتماعات Archestro");
        ApplyLocalizedCopy();
        MeetingTitleText.Text = meeting.PrimaryTitle;
        var lensOptions = BuildReportLensOptions();
        ModeCombo.ItemsSource = lensOptions;
        ModeCombo.DisplayMemberPath = nameof(ReportLensOption.Label);
        ModeCombo.SelectedValuePath = nameof(ReportLensOption.Key);
        ModeCombo.SelectedValue = "General";
        UpdateModeHelp();

        _reportElapsedTimer.Tick += (_, _) => UpdateReportElapsed();
        IntelTabs.SelectionChanged += IntelTabs_SelectionChanged;
        if (!_layoutQa)
            Loaded += IntelligenceWindow_Loaded;
        Closed += (_, _) =>
        {
            CancelReportGeneration();
            _reportElapsedTimer.Stop();
            _player.Dispose();
        };
    }

    private async void IntelligenceWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateReadiness();
        LoadExisting();

        if (_openReportOnLoad)
        {
            IntelTabs.SelectedIndex = 0;
            WindowState = WindowState.Maximized;
        }

        // V22: opening Meeting Report never starts generation automatically.
        // The user first chooses a report lens, then explicitly starts generation.
        await Dispatcher.InvokeAsync(() => ApplyLocalizedCopy());
        UpdateGenerateButtonCopy();
    }

    private void LoadExisting()
    {
        _report = _service.LoadReport(_meeting);
        if (_report is null)
        {
            SummaryText.Text = T(
                "Generate a polished meeting report with executive summary, decisions, actions, participants and evidence.",
                "أنشئ تقرير اجتماع احترافيًا يشمل الملخص التنفيذي والقرارات والمهام والمشاركين والأدلة.");
            ApplyReportTextDirection(SummaryText);
            StatusText.Text = T(
                "Choose a report type, then start generation.",
                "اختر نوع التقرير ثم ابدأ إعداد التقرير.");
            ReportStateText.Text = T("Not generated", "لم يتم الإنشاء");
            ClearLists();
            ExportWordButton.IsEnabled = false;
            OpenReportFolderButton.IsEnabled = Directory.Exists(MeetingIntelligenceService.GetReportsFolder(_meeting));
            return;
        }

        ModeCombo.SelectedValue = _report.MeetingMode;
        UpdateModeHelp();

        var expectedReportLanguage = AppearanceService.IsArabic ? "ar" : "en";
        if (!MeetingIntelligenceService.IsReportLanguageCompatible(_report, expectedReportLanguage))
        {
            _report.NeedsRefresh = true;
            SummaryText.Text = T(
                "This saved report was created with a different language profile. Regenerate it before review or export.",
                "هذا التقرير محفوظ من إصدار سابق ولا يطابق اللغة العربية الحالية. أعد إعداد التقرير قبل المراجعة أو التصدير.");
            ApplyReportTextDirection(SummaryText);
            ClearLists();
            var refreshMessage = T("Regenerate the report to refresh this section.", "أعد إعداد التقرير لتحديث هذا القسم باللغة الحالية.");
            EnsureEmptyState(TopicsList, 0, refreshMessage);
            EnsureEmptyState(KeyPointsList, 0, refreshMessage);
            EnsureEmptyState(DecisionsList, 0, refreshMessage);
            EnsureEmptyState(ActionsList, 0, refreshMessage);
            EnsureEmptyState(CommitmentsList, 0, refreshMessage);
            EnsureEmptyState(RisksList, 0, refreshMessage);
            EnsureEmptyState(ImportantMomentsList, 0, refreshMessage);
            EnsureEmptyState(ParticipantsList, 0, refreshMessage);
            EnsureEmptyState(FollowUpList, 0, refreshMessage);
            ReportStateText.Text = T("Needs regeneration", "يحتاج إعادة إعداد");
            StatusText.Text = T(
                "Saved report language does not match the current application language",
                "لغة التقرير المحفوظ لا تطابق لغة التطبيق الحالية");
            ExportWordButton.IsEnabled = false;
            OpenReportFolderButton.IsEnabled = true;
            UpdateGenerateButtonCopy();
            return;
        }

        SummaryText.Text = _report.ExecutiveSummary;
        ApplyReportTextDirection(SummaryText);
        BindItems(TopicsList, _report.Topics, _report, showOwner: false);
        BindItems(KeyPointsList, _report.KeyPoints, _report, showOwner: false);
        BindItems(DecisionsList, _report.Decisions, _report, showOwner: true);
        BindItems(ActionsList, _report.ActionItems, _report, showOwner: true);
        BindItems(
            CommitmentsList,
            _report.Commitments.Concat(_report.Deadlines).ToList(),
            _report,
            showOwner: true);
        BindItems(
            RisksList,
            _report.Risks.Concat(_report.OpenItems).Concat(_report.CommercialPoints).ToList(),
            _report,
            showOwner: false);
        BindItems(ImportantMomentsList, _report.ImportantMoments, _report, showOwner: true);
        BindItems(ParticipantsList, _report.ParticipantContributions, _report, showOwner: true);
        BindItems(FollowUpList, _report.FollowUp, _report, showOwner: true);

        EnsureEmptyState(TopicsList, _report.Topics.Count, T("No major topics found.", "لم يتم العثور على محاور رئيسية مؤكدة."));
        EnsureEmptyState(KeyPointsList, _report.KeyPoints.Count, T("No substantive key points found.", "لم يتم العثور على نقاط رئيسية جوهرية."));
        EnsureEmptyState(DecisionsList, _report.Decisions.Count, T("No confirmed decisions found.", "لم يتم العثور على قرارات مؤكدة."));
        EnsureEmptyState(ActionsList, _report.ActionItems.Count, T("No explicit next actions found.", "لم يتم العثور على مهام أو إجراءات صريحة."));
        EnsureEmptyState(CommitmentsList, _report.Commitments.Count + _report.Deadlines.Count, T("No confirmed commitments or deadlines found.", "لم يتم العثور على التزامات أو مواعيد مؤكدة."));
        EnsureEmptyState(RisksList, _report.Risks.Count + _report.OpenItems.Count + _report.CommercialPoints.Count, T("No confirmed risks or open items found.", "لم يتم العثور على مخاطر أو نقاط مفتوحة مؤكدة."));
        EnsureEmptyState(ImportantMomentsList, _report.ImportantMoments.Count, T("No important moments were extracted.", "لم يتم استخراج لحظات مهمة مؤكدة."));
        EnsureEmptyState(ParticipantsList, _report.ParticipantContributions.Count, T("No confirmed participant contributions found.", "لم يتم العثور على مساهمات مؤكدة للمشاركين."));
        EnsureEmptyState(FollowUpList, _report.FollowUp.Count, T("No follow-up items were confirmed.", "لم يتم العثور على نقاط متابعة مؤكدة."));

        ReportStateText.Text = _report.NeedsRefresh
            ? T("Needs refresh", "يحتاج تحديث")
            : T("Report ready", "التقرير جاهز");
        StatusText.Text = _report.NeedsRefresh
            ? T("Meeting report exists • transcript changed since generation", "يوجد تقرير • تم تعديل نص الاجتماع بعد إنشائه")
            : T("Meeting report ready • processed locally", "تقرير الاجتماع جاهز • تمت المعالجة محليًا");
        ExportWordButton.IsEnabled = !_busy && !_report.NeedsRefresh;
        OpenReportFolderButton.IsEnabled = true;
        UpdateGenerateButtonCopy();
    }

    private void UpdateReadiness()
    {
        var transcriptReady =
            !string.IsNullOrWhiteSpace(_meeting.TranscriptPath) &&
            File.Exists(_meeting.TranscriptPath);

        if (!transcriptReady)
        {
            ReadinessText.Text = T("Transcript needed", "النص مطلوب");
            ReadinessDetailText.Text = T(
                "Finish transcription first. Intelligence stays grounded in what was actually said.",
                "أكمل التفريغ النصي أولًا. يبقى التحليل مرتبطًا بما قيل فعليًا.");
            GenerateButton.IsEnabled = false;
            return;
        }

        ReadinessText.Text = T("Ready", "جاهز");
        ReadinessDetailText.Text = T(
            "Meeting Report = executive summary, decisions, actions and evidence • Ask this meeting = this transcript only • Ask my vault = your local archive.",
            "تقرير الاجتماع = ملخص تنفيذي وقرارات ومهام وأدلة • اسأل هذا الاجتماع = هذا النص فقط • اسأل أرشيفي = اجتماعاتك المحلية.");
        GenerateButton.IsEnabled = true;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        var mode = ModeCombo.SelectedValue?.ToString() ?? "General";
        await GenerateBriefAsync(mode, automatic: false);
    }

    private async Task GenerateBriefAsync(string mode, bool automatic)
    {
        CancelReportGeneration();
        if (_busy) return;
        var operation = new CancellationTokenSource();
        _reportGenerationCts = operation;
        var generationCompleted = false;

        try
        {
            var lensLabel = CurrentLens()?.Label ?? mode;
            SetBusy(true, T(
                $"Preparing the {lensLabel} report locally…",
                $"جارٍ إعداد التقرير بنمط «{lensLabel}» محليًا…"));

            _reportStarted = DateTimeOffset.Now;
            ReportProgressBar.Value = 2;
            ReportProgressBar.Visibility = Visibility.Visible;
            ReportProgressText.Text = T("Preparing report…", "جارٍ تجهيز التقرير…");
            _reportElapsedTimer.Start();
            StartReportWritingMotion();

            var progress = new Progress<MeetingReportProgress>(p =>
            {
                ReportProgressBar.Visibility = Visibility.Visible;
                ReportProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
                ReportProgressText.Text = string.IsNullOrWhiteSpace(p.Detail)
                    ? p.Stage
                    : p.Stage + " • " + p.Detail;
                UpdateReportEta(p.Percent);
            });

            _report = await _service.AnalyzeMeetingAsync(
                _meeting,
                mode,
                operation.Token,
                progress,
                AppearanceService.IsArabic ? "ar" : "en");
            generationCompleted = true;
            LoadExisting();
            IntelTabs.SelectedIndex = 0;
            StatusText.Text = T("Meeting report ready • processed locally", "تقرير الاجتماع جاهز • تمت المعالجة محليًا");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusText.Text = T("Report generation was cancelled. No partial report was saved.", "أُلغي إنشاء التقرير. لم يتم حفظ تقرير جزئي.");
            ReportStateText.Text = T("Cancelled", "أُلغي");
        }
        catch (Exception ex)
        {
            if (ex is InsufficientMeetingEvidenceException)
            {
                StatusText.Text = ex.Message;
                if (_report is null)
                {
                    ReportStateText.Text = T("Insufficient evidence", "أدلة غير كافية");
                    SummaryText.Text = ex.Message;
                    ApplyReportTextDirection(SummaryText);
                    ClearLists();
                    ExportWordButton.IsEnabled = false;
                }
                if (!automatic)
                    MessageBox.Show(ex.Message, T("Insufficient evidence", "أدلة غير كافية"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ex is ReportLanguageValidationException)
            {
                StatusText.Text = ex.Message;
                if (_report is null)
                {
                    ReportStateText.Text = T("Needs revision", "يحتاج مراجعة");
                    SummaryText.Text = ex.Message;
                    ApplyReportTextDirection(SummaryText);
                    ClearLists();
                    ExportWordButton.IsEnabled = false;
                }
                if (!automatic)
                    MessageBox.Show(ex.Message, T("Report language check", "فحص لغة التقرير"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ex is TimeoutException)
            {
                StatusText.Text = T(
                    "Report generation reached its time limit. Retry with a shorter meeting selection.",
                    "انتهت المهلة المحددة لإنشاء التقرير. أعد المحاولة بعد اختيار مقطع أقصر.");
                ReportStateText.Text = T("Timed out", "انتهت المهلة");
                return;
            }
            if (!automatic)
            {
                MessageBox.Show(
                    ex.Message,
                    T("Meeting Report", "تقرير الاجتماع"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            StatusText.Text = automatic
                ? T("Report not generated yet • use Generate report to retry", "لم يتم إنشاء التقرير بعد • استخدم إنشاء التقرير للمحاولة مجددًا")
                : T("Meeting report not completed", "لم يكتمل تقرير الاجتماع");
        }
        finally
        {
            if (ReferenceEquals(_reportGenerationCts, operation))
                _reportGenerationCts = null;
            operation.Dispose();
            _reportElapsedTimer.Stop();
            StopReportWritingMotion();
            UpdateReportElapsed();
            SetBusy(false, "");
            if (generationCompleted)
            {
                ReportProgressBar.Value = 100;
                ReportProgressText.Text = T("Report ready", "التقرير جاهز");
            }
        }
    }

    private void CancelReportGeneration()
    {
        var active = _reportGenerationCts;
        _reportGenerationCts = null;
        if (active is null) return;
        try { active.Cancel(); }
        catch (ObjectDisposedException) { }
        active.Dispose();
    }

    private void UpdateReportElapsed()
    {
        if (_reportStarted is null)
        {
            ReportElapsedText.Text = "";
            ReportEtaText.Text = "";
            return;
        }
        var elapsed = DateTimeOffset.Now - _reportStarted.Value;
        ReportElapsedText.Text = T("Elapsed ", "الوقت المنقضي ") + elapsed.ToString(@"mm\:ss");
        UpdateReportEta(ReportProgressBar.Value);
    }

    private void UpdateReportEta(double percent)
    {
        if (_reportStarted is null || percent < 8 || percent >= 100)
        {
            ReportEtaText.Text = percent >= 100 ? T("Ready", "جاهز") : T("Estimating remaining time…", "جارٍ تقدير الوقت المتبقي…");
            return;
        }
        var elapsed = DateTimeOffset.Now - _reportStarted.Value;
        var remainingSeconds = elapsed.TotalSeconds * (100d - percent) / Math.Max(1d, percent);
        remainingSeconds = Math.Clamp(remainingSeconds, 10, 60 * 30);
        var remaining = TimeSpan.FromSeconds(remainingSeconds);
        ReportEtaText.Text = T("About ", "متبقي تقريبًا ") + (remaining.TotalMinutes >= 1 ? $"{Math.Ceiling(remaining.TotalMinutes):0} " + T("min", "د") : $"{Math.Ceiling(remaining.TotalSeconds):0} " + T("sec", "ث"));
    }

    private void StartReportWritingMotion()
    {
        ReportWritingGlyph.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(-3, 5, TimeSpan.FromMilliseconds(650))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        ReportWritingTransform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void StopReportWritingMotion()
    {
        ReportWritingTransform.BeginAnimation(TranslateTransform.XProperty, null);
        ReportWritingGlyph.Visibility = Visibility.Collapsed;
        ReportWritingTransform.X = 0;
    }

    public void OpenReportView()
    {
        IntelTabs.SelectedIndex = 0;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        UpdateGenerateButtonCopy();
        StatusText.Text = _report is null
            ? T("Choose a report type, then start generation.", "اختر نوع التقرير ثم ابدأ إعداد التقرير.")
            : StatusText.Text;
    }

    private void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            MessageBox.Show(T("Generate the meeting report first.", "أنشئ تقرير الاجتماع أولًا."), T("Meeting Report", "تقرير الاجتماع"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var expectedReportLanguage = AppearanceService.IsArabic ? "ar" : "en";
        if (!MeetingIntelligenceService.IsCachedReportCompatibleForExport(_report, expectedReportLanguage))
        {
            MessageBox.Show(
                T("This saved report needs a refresh or does not match the current language. Refresh it before exporting.", "يحتاج هذا التقرير المحفوظ إلى تحديث أو لا يطابق اللغة الحالية. حدّثه قبل التصدير."),
                T("Meeting Report", "تقرير الاجتماع"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            _lastWordReportPath = MeetingReportWordExporter.Export(_meeting, _report);
            StatusText.Text = T("Word report exported", "تم تصدير تقرير Word");
            OpenReportFolderButton.IsEnabled = true;
            ProcessService.OpenPath(_lastWordReportPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, T("Export Word", "تصدير Word"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenReportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = MeetingIntelligenceService.GetReportsFolder(_meeting);
            Directory.CreateDirectory(folder);
            if (!string.IsNullOrWhiteSpace(_lastWordReportPath) && File.Exists(_lastWordReportPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastWordReportPath}\"") { UseShellExecute = true });
                return;
            }
            ProcessService.OpenPath(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, T("Report location", "موقع التقرير"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void AskMeeting_Click(object sender, RoutedEventArgs e)
    {
        var q = MeetingQuestionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(q) || _busy) return;

        MeetingQuestionBox.Clear();
        MeetingQuestionEchoText.Text = q;
        MeetingQuestionEchoBorder.Visibility = Visibility.Visible;
        ApplyTextDirection(MeetingQuestionEchoText, q);
        try
        {
            SetBusy(true, T("Reading this meeting transcript locally…", "أراجع نص هذا الاجتماع محليًا…"));
            var answer = await _service.AskThisMeetingAsync(_meeting, q);
            MeetingAnswerBox.Text = RenderAnswer(answer);
            ApplyTextDirection(MeetingAnswerBox, MeetingAnswerBox.Text);
            StatusText.Text = T("Answer grounded in this meeting", "الإجابة مرتبطة بدليل هذا الاجتماع");
        }
        catch (Exception ex)
        {
            MeetingAnswerBox.Text = ex.Message;
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private async void AskVault_Click(object sender, RoutedEventArgs e)
    {
        var q = VaultQuestionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(q) || _busy) return;

        VaultQuestionBox.Clear();
        VaultQuestionEchoText.Text = q;
        VaultQuestionEchoBorder.Visibility = Visibility.Visible;
        ApplyTextDirection(VaultQuestionEchoText, q);
        try
        {
            SetBusy(true, T("Reading relevant meetings from your local vault…", "أراجع الاجتماعات المرتبطة من أرشيفك المحلي…"));
            var answer = await _service.AskVaultAsync(q);
            VaultAnswerBox.Text = RenderAnswer(answer);
            ApplyTextDirection(VaultAnswerBox, VaultAnswerBox.Text);
            StatusText.Text = T("Answer grounded in local Vault evidence", "الإجابة مرتبطة بأدلة أرشيفك المحلي");
        }
        catch (Exception ex)
        {
            VaultAnswerBox.Text = ex.Message;
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        BusyText.Text = message;

        // Never disable the entire Window. WPF system disabled templates can fall back
        // to white/default surfaces. Disable only actionable controls and preserve theme.
        GenerateButton.IsEnabled = !busy &&
            !string.IsNullOrWhiteSpace(_meeting.TranscriptPath) &&
            File.Exists(_meeting.TranscriptPath);
        ModeCombo.IsEnabled = !busy;
        AskMeetingButton.IsEnabled = !busy;
        AskVaultButton.IsEnabled = !busy;
        ExportWordButton.IsEnabled = !busy && _report is not null && !_report.NeedsRefresh;
        OpenReportFolderButton.IsEnabled = !busy && (_report is not null || Directory.Exists(MeetingIntelligenceService.GetReportsFolder(_meeting)));

        if (busy)
            StatusText.Text = message;
        else
            UpdateGenerateButtonCopy();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateModeHelp();

    private List<ReportLensOption> BuildReportLensOptions()
    {
        if (AppearanceService.IsArabic)
        {
            return new()
            {
                new("General", "عام", "نظرة شاملة ومتوازنة على أهم ما ورد في الاجتماع."),
                new("Executive", "تنفيذي", "يركز على القرارات والمسؤوليات والتصعيدات والمواعيد والمخاطر الرئيسية."),
                new("Contracts", "العقود", "يركز على النطاق والالتزامات والشروط التجارية والموافقات والتغييرات والمخاطر التعاقدية."),
                new("Procurement", "المشتريات", "يركز على الموردين والعروض والكميات والمدد والموافقات والمتابعات."),
                new("Sales", "المبيعات والعملاء", "يركز على احتياجات العميل والاعتراضات والفرص والالتزامات والخطوات التالية."),
                new("Project", "المشاريع", "يركز على التقدم والمخرجات والمعوقات والاعتماديات والمسؤولين والمواعيد."),
                new("HR", "الموارد البشرية", "يركز على المسؤوليات وإجراءات الأفراد والالتزامات والمتابعات والنقاط المفتوحة."),
                new("Custom", "مخصص", "مراجعة مرنة مع بقاء جميع النتائج مرتبطة بنص الاجتماع وأدلته.")
            };
        }

        return new()
        {
            new("General", "General", "Balanced overview of the meeting."),
            new("Executive", "Executive", "Decisions, owners, escalations, deadlines and major risks."),
            new("Contracts", "Contracts", "Scope, obligations, commercial terms, approvals, changes and exposure."),
            new("Procurement", "Procurement", "Suppliers, quotations, approvals, lead times, quantities and follow-ups."),
            new("Sales", "Sales & Customer", "Customer needs, objections, opportunities, commitments and next actions."),
            new("Project", "Project", "Progress, deliverables, blockers, dependencies, owners and deadlines."),
            new("HR", "People / HR", "Responsibilities, people actions, commitments, follow-ups and open issues."),
            new("Custom", "Custom", "Flexible review grounded in the meeting transcript and evidence.")
        };
    }

    private ReportLensOption? CurrentLens() =>
        ModeCombo.SelectedItem as ReportLensOption;

    private void UpdateModeHelp()
    {
        ModeHelpText.Text = CurrentLens()?.Description ?? T(
            "Choose the report perspective that best matches this meeting.",
            "اختر زاوية التقرير الأنسب لطبيعة هذا الاجتماع.");
    }

    private void UpdateGenerateButtonCopy()
    {
        if (GenerateButton is null) return;
        GenerateButton.Content = _report is null
            ? T("Start report", "ابدأ إعداد التقرير")
            : T("Regenerate report", "إعادة إعداد التقرير");
    }

    private string RenderAnswer(AskAnswer answer)
    {
        var sb = new StringBuilder();
        sb.AppendLine(answer.Answer.Trim());

        var selected = answer.Evidence
            .Select(id => answer.EvidenceIndex.FirstOrDefault(x =>
                x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x is not null)
            .Cast<EvidenceRef>()
            .ToList();

        if (selected.Count == 0)
            return sb.ToString().TrimEnd();

        sb.AppendLine();
        sb.AppendLine(T("Evidence used", "الأدلة المستخدمة"));

        foreach (var e in selected)
        {
            sb.Append("• ").Append(e.MeetingTitle);
            if (!string.IsNullOrWhiteSpace(e.MeetingDateText))
                sb.Append("  •  ").Append(e.MeetingDateText);
            sb.AppendLine();
            sb.Append("  ").Append(e.TimeText);
            if (!IsGenericSpeakerLabel(e.Speaker))
                sb.Append("  •  ").Append(e.Speaker);
            sb.AppendLine();
            sb.Append("  ↳ ").AppendLine(e.Text);
        }

        return sb.ToString().TrimEnd();
    }

    private void BindItems(
        ItemsControl control,
        IEnumerable<IntelligenceItem> items,
        MeetingIntelligenceReport report,
        bool showOwner)
    {
        var panel = new List<FrameworkElement>();

        foreach (var item in items)
        {
            var stack = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FlowDirection = AppearanceService.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
            var itemText = new TextBlock
            {
                Text = "• " + item.Text,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplyReportTextDirection(itemText);
            stack.Children.Add(itemText);

            var meta = new List<string>();
            if (showOwner && !IsGenericSpeakerLabel(item.Owner))
                meta.Add(T("Owner: ", "المسؤول: ") + item.Owner);
            if (!string.IsNullOrWhiteSpace(item.Due))
                meta.Add(T("Due: ", "الموعد: ") + item.Due);

            if (meta.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = string.Join("  •  ", meta),
                    Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                    Margin = AppearanceService.IsArabic ? new Thickness(0, 3, 12, 0) : new Thickness(12, 3, 0, 0),
                    FontSize = 10.5,
                    TextAlignment = AppearanceService.IsArabic ? TextAlignment.Right : TextAlignment.Left,
                    FlowDirection = AppearanceService.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    Language = System.Windows.Markup.XmlLanguage.GetLanguage(AppearanceService.IsArabic ? "ar-SA" : "en-US")
                });
            }

            var evidencePanel = new WrapPanel
            {
                Margin = AppearanceService.IsArabic ? new Thickness(0, 5, 12, 0) : new Thickness(12, 5, 0, 0),
                HorizontalAlignment = AppearanceService.IsArabic ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                FlowDirection = AppearanceService.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
            foreach (var id in item.Evidence)
            {
                var evidence = report.EvidenceIndex.FirstOrDefault(x =>
                    x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (evidence is null) continue;

                var label = new StackPanel { Orientation = Orientation.Horizontal };
                if (!IsGenericSpeakerLabel(evidence.Speaker))
                {
                    label.Children.Add(new TextBlock
                    {
                        Text = evidence.Speaker + "  ",
                        Foreground = SpeakerAccentBrush(evidence.Speaker),
                        FontWeight = FontWeights.SemiBold
                    });
                }
                label.Children.Add(new TextBlock
                {
                    Text = "▶ " + evidence.TimeText,
                    Foreground = (Brush)FindResource("PrimaryTextBrush"),
                    FlowDirection = FlowDirection.LeftToRight,
                    TextAlignment = TextAlignment.Left,
                    Language = System.Windows.Markup.XmlLanguage.GetLanguage("en-US")
                });
                var button = new Button
                {
                    Content = label,
                    Tag = evidence,
                    Margin = new Thickness(0, 0, 6, 4),
                    Padding = new Thickness(8, 5, 8, 5),
                    Background = (Brush)FindResource("SurfaceBrush"),
                    BorderBrush = (Brush)FindResource("BorderBrush")
                };
                button.Click += EvidencePlay_Click;
                evidencePanel.Children.Add(button);
            }

            stack.Children.Add(evidencePanel);
            panel.Add(stack);
        }

        control.ItemsSource = panel;
    }

    private void EvidencePlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EvidenceRef evidence }) return;

        var meeting = evidence.MeetingId.Equals(
            _meeting.Id,
            StringComparison.OrdinalIgnoreCase)
            ? _meeting
            : null;

        if (meeting is null) return;

        var path = File.Exists(meeting.AudioPath)
            ? meeting.AudioPath
            : meeting.RecordingPath;

        if (!File.Exists(path)) return;
        _player.PlayFrom(path, evidence.StartSeconds);
        StopAudioButton.Visibility = Visibility.Visible;
        StatusText.Text = T("Playing evidence audio", "جارٍ تشغيل مقطع الدليل");
    }

    private void StopAudio_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        StopAudioButton.Visibility = Visibility.Collapsed;
        StatusText.Text = _report is null
            ? T("Choose a report type, then start generation.", "اختر نوع التقرير ثم ابدأ إعداد التقرير.")
            : T("Meeting report ready", "تقرير الاجتماع جاهز");
    }

    private static bool IsGenericSpeakerLabel(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("Speaker", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Transcript", StringComparison.OrdinalIgnoreCase);

    private static void ApplyReportTextDirection(TextBlock block)
    {
        var rtl = AppearanceService.IsArabic;
        block.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        block.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
        block.HorizontalAlignment = HorizontalAlignment.Stretch;
        block.Language = System.Windows.Markup.XmlLanguage.GetLanguage(rtl ? "ar-SA" : "en-US");
    }

    private static void ApplyTextDirection(TextBlock block, string? text)
    {
        var value = text ?? "";
        var arabic = value.Count(c => c >= '\u0600' && c <= '\u06FF');
        var latin = value.Count(c => c <= 127 && char.IsLetter(c));
        var rtl = arabic > 0 && arabic >= latin * 0.30;
        block.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        block.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
        block.Language = System.Windows.Markup.XmlLanguage.GetLanguage(rtl ? "ar-SA" : "en-US");
    }

    private static Brush SpeakerAccentBrush(string? name)
    {
        var palette = new[] { "#C79A3B", "#7C9DFF", "#7ED6A5", "#D78BFF", "#F08A8A", "#65C7D0" };
        var key = name ?? string.Empty;
        var hash = key.Aggregate(17, (acc, ch) => unchecked(acc * 31 + ch));
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[Math.Abs(hash) % palette.Length]));
    }

    private static void ApplyTextDirection(TextBox box, string? text)
    {
        var value = text ?? string.Empty;
        var arabic = value.Count(c => c >= '\u0600' && c <= '\u06FF');
        var latin = value.Count(c => c <= 127 && char.IsLetter(c));
        var rtl = arabic > 0 && arabic >= latin * 0.30;
        box.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        box.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
        box.Language = System.Windows.Markup.XmlLanguage.GetLanguage(rtl ? "ar-SA" : "en-US");
    }

    private static string T(string en, string ar) =>
        AppearanceService.IsArabic ? ar : en;

    private void ApplyLocalizedCopy()
    {
        var rtl = AppearanceService.IsArabic;

        var reportBlocks = new[]
        {
            ExecutiveSummaryHeading, SummaryText,
            TopicsHeading, TopicsHelp, KeyPointsHeading, KeyPointsHelp,
            DecisionsHeading, DecisionsHelp, ActionsHeading, ActionsHelp,
            CommitmentsHeading, CommitmentsHelp, RisksHeading, RisksHelp,
            ImportantMomentsHeading, ImportantMomentsHelp,
            ParticipantsHeading, ParticipantsHelp, FollowUpHeading, FollowUpHelp
        };
        foreach (var block in reportBlocks)
        {
            block.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            block.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
            block.HorizontalAlignment = HorizontalAlignment.Stretch;
            block.Language = System.Windows.Markup.XmlLanguage.GetLanguage(rtl ? "ar-SA" : "en-US");
        }

        ApplyReportPhysicalOrder(rtl);

        if (!rtl) return;

        ProductEyebrowText.Text = "ذكاء الاجتماعات من Archestro";
        ProductIntroText.Text = "حوّل حديث الاجتماع إلى قرارات واضحة، ومهام قابلة للتنفيذ، وخلاصة موثقة باللحظة والمتحدث — أو اسأل مباشرة عن هذا الاجتماع وأرشيفك المحلي.";
        ReportLensLabel.Text = "نوع التقرير";
        ReportTab.Header = "تقرير الاجتماع";
        AskMeetingTab.Header = "اسأل هذا الاجتماع";
        AskVaultTab.Header = "اسأل أرشيفي";
        ReportNavReportButton.Content = "تقرير الاجتماع";
        ReportNavMeetingAskButton.Content = "اسأل هذا الاجتماع";
        ReportNavVaultAskButton.Content = "اسأل أرشيفي";
        AutomationProperties.SetName(ReportNavReportButton, "تقرير الاجتماع");
        AutomationProperties.SetName(ReportNavMeetingAskButton, "اسأل هذا الاجتماع");
        AutomationProperties.SetName(ReportNavVaultAskButton, "اسأل أرشيفي");
        ExecutiveSummaryHeading.Text = "الملخص التنفيذي";
        TopicsHeading.Text = "المحاور الرئيسية";
        TopicsHelp.Text = "الموضوعات التي شكّلت مسار الاجتماع.";
        KeyPointsHeading.Text = "أبرز النقاط";
        KeyPointsHelp.Text = "أهم المعلومات التي تستحق الاحتفاظ بها.";
        DecisionsHeading.Text = "القرارات";
        DecisionsHelp.Text = "ما تم اتخاذ قرار بشأنه فعليًا.";
        ActionsHeading.Text = "المهام والخطوات التالية";
        ActionsHelp.Text = "ما الذي يجب تنفيذه ومن المسؤول عنه إذا تم تحديده صراحةً.";
        CommitmentsHeading.Text = "الالتزامات والمواعيد";
        CommitmentsHelp.Text = "الوعود والمواعيد والمتابعات الحساسة للوقت.";
        RisksHeading.Text = "المخاطر والأسئلة المفتوحة";
        RisksHelp.Text = "ما بقي غير واضح أو معطلًا أو معرضًا للمخاطر.";
        ImportantMomentsHeading.Text = "اللحظات المهمة";
        ImportantMomentsHelp.Text = "لحظات تستحق الرجوع إليها في التسجيل.";
        ParticipantsHeading.Text = "مساهمات المشاركين";
        ParticipantsHelp.Text = "ما ساهم به كل متحدث مؤكد أو مسمى.";
        FollowUpHeading.Text = "المتابعة القادمة";
        FollowUpHelp.Text = "ما ينبغي متابعته بعد هذا الاجتماع.";
        AskMeetingHelp.Text = "اسأل سؤالًا مباشرًا عن هذا الاجتماع. يجيب Archestro من نص هذا الاجتماع فقط ويعرض الأدلة المستخدمة.";
        AskVaultHelp.Text = "اسأل عبر أرشيف اجتماعاتك المحلي. يبحث Archestro في الاجتماعات ذات الصلة ويربط الإجابة بالأدلة.";
        AskMeetingButton.Content = "اسأل هذا الاجتماع";
        AskVaultButton.Content = "اسأل أرشيفي";
        AutomationProperties.SetName(AskMeetingButton, "اسأل هذا الاجتماع");
        AutomationProperties.SetName(AskVaultButton, "اسأل أرشيفي");
        ExportWordButton.Content = "تصدير Word";
        OpenReportFolderButton.Content = "فتح موقع الملف";
        StopAudioButton.Content = "إيقاف الصوت";
        UpdateGenerateButtonCopy();
        UpdateModeHelp();
    }

    private void ApplyReportPhysicalOrder(bool rtl)
    {
        var contentDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ReportNavReportButton.FlowDirection = contentDirection;
        ReportNavMeetingAskButton.FlowDirection = contentDirection;
        ReportNavVaultAskButton.FlowDirection = contentDirection;

        Grid.SetColumn(ReportNavReportButton, rtl ? 4 : 0);
        Grid.SetColumn(ReportNavMeetingAskButton, 2);
        Grid.SetColumn(ReportNavVaultAskButton, rtl ? 0 : 4);

        MeetingQuestionInputGrid.FlowDirection = FlowDirection.LeftToRight;
        VaultQuestionInputGrid.FlowDirection = FlowDirection.LeftToRight;
        Grid.SetColumn(MeetingQuestionBox, 0);
        Grid.SetColumn(AskMeetingButton, 1);
        Grid.SetColumn(VaultQuestionBox, 0);
        Grid.SetColumn(AskVaultButton, 1);
        MeetingQuestionBox.FlowDirection = contentDirection;
        VaultQuestionBox.FlowDirection = contentDirection;
        MeetingQuestionBox.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
        VaultQuestionBox.TextAlignment = rtl ? TextAlignment.Right : TextAlignment.Left;
        MeetingQuestionBox.Language = System.Windows.Markup.XmlLanguage.GetLanguage(rtl ? "ar-SA" : "en-US");
        VaultQuestionBox.Language = System.Windows.Markup.XmlLanguage.GetLanguage(rtl ? "ar-SA" : "en-US");
        AskMeetingButton.FlowDirection = contentDirection;
        AskVaultButton.FlowDirection = contentDirection;
        AskMeetingButton.Margin = new Thickness(8, 0, 0, 0);
        AskVaultButton.Margin = new Thickness(8, 0, 0, 0);

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
    }


    private static void EnsureEmptyState(ItemsControl control, int count, string message)
    {
        if (count > 0) return;
        control.ItemsSource = new FrameworkElement[]
        {
            new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72,
                Margin = new Thickness(0, 8, 0, 8),
                FlowDirection = AppearanceService.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                TextAlignment = AppearanceService.IsArabic ? TextAlignment.Right : TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Language = System.Windows.Markup.XmlLanguage.GetLanguage(AppearanceService.IsArabic ? "ar-SA" : "en-US")
            }
        };
    }

    private void ClearLists()
    {
        TopicsList.ItemsSource = null;
        KeyPointsList.ItemsSource = null;
        DecisionsList.ItemsSource = null;
        ActionsList.ItemsSource = null;
        CommitmentsList.ItemsSource = null;
        RisksList.ItemsSource = null;
        ImportantMomentsList.ItemsSource = null;
        ParticipantsList.ItemsSource = null;
        FollowUpList.ItemsSource = null;
    }

    private void Chrome_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;

        if (e.ClickCount == 2 && ResizeMode != ResizeMode.NoResize)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch { }
    }

    private void IntelTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReadinessDetailText is null || IntelTabs is null) return;
        _syncNavigation = true;
        ReportNavReportButton.IsChecked = IntelTabs.SelectedIndex == 0;
        ReportNavMeetingAskButton.IsChecked = IntelTabs.SelectedIndex == 1;
        ReportNavVaultAskButton.IsChecked = IntelTabs.SelectedIndex == 2;
        _syncNavigation = false;
        if (IntelTabs.SelectedItem == ReportTab)
            ReadinessDetailText.Text = T("Choose a report type, generate locally, then review evidence-linked results.", "اختر نوع التقرير، ثم أنشئه محليًا وراجع النتائج المرتبطة بالدليل.");
        else if (IntelTabs.SelectedItem == AskMeetingTab)
            ReadinessDetailText.Text = T("This mode answers only from the selected meeting transcript.", "هذا الوضع يجيب من نص هذا الاجتماع فقط ويربط الإجابة بأدلته.");
        else if (IntelTabs.SelectedItem == AskVaultTab)
            ReadinessDetailText.Text = T("This mode searches your local meeting archive and links the answer to relevant meetings.", "هذا الوضع يبحث في أرشيف اجتماعاتك المحلي ويربط الإجابة بالاجتماعات والأدلة ذات الصلة.");
    }

    private void ReportNav_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncNavigation || (!_layoutQa && IntelTabs is null) || sender is not RadioButton button || button.IsChecked != true)
            return;
        _syncNavigation = true;
        IntelTabs.SelectedIndex = ReferenceEquals(button, ReportNavReportButton) ? 0
            : ReferenceEquals(button, ReportNavMeetingAskButton) ? 1 : 2;
        _syncNavigation = false;
    }

    internal void PrepareLayoutQa(bool arabic)
    {
        _report = new MeetingIntelligenceReport
        {
            MeetingId = _meeting.Id,
            ReportLanguage = arabic ? "ar" : "en",
            ExecutiveSummary = arabic
                ? "تم الاتفاق على مراجعة واجهة التقرير وتوثيق النتائج."
                : "The team agreed to review the report interface and document the results.",
            Topics = new() { new IntelligenceItem { Text = arabic ? "مراجعة مسار العمل" : "Review the workflow" } },
            KeyPoints = new() { new IntelligenceItem { Text = arabic ? "تأكيد الموعد النهائي" : "Confirm the deadline" } },
            EvidenceIndex = new() { new EvidenceRef { Id = "V28-E1", StartSeconds = 52, Speaker = "Speaker 1" } }
        };
        _report.Topics[0].Evidence.Add("V28-E1");
        SummaryText.Text = _report.ExecutiveSummary;
        ApplyReportTextDirection(SummaryText);
        BindItems(TopicsList, _report.Topics, _report, showOwner: false);
        BindItems(KeyPointsList, _report.KeyPoints, _report, showOwner: false);
        IntelTabs.SelectedIndex = 0;
        UpdateLayout();
    }

    private void ChromeMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ChromeMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (ChromeMaximizeButton is not null)
            ChromeMaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();

}
