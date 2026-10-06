using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Archestro.MeetingVault.Models;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault.Dialogs;

public partial class SpeakerTranscriptWindow : Window
{
    private static string T(string en, string ar) => AppearanceService.IsArabic ? ar : en;
    private readonly MeetingRecord _meeting;
    private readonly SpeakerIntelligenceService _speakerService;
    private readonly SpeakerTranscriptService _transcripts = new();
    private readonly MeetingAudioPlayer _player = new();
    private readonly SpeakerProfileStore _profiles = new();
    private SpeakerAnalysisResult? _result;
    private int? _selectedSpeakerIndex;
    private bool _busy;

    public SpeakerTranscriptWindow(
        MeetingRecord meeting,
        SpeakerIntelligenceService speakerService)
    {
        InitializeComponent();
        _meeting = meeting;
        _speakerService = speakerService;
        AppearanceService.Apply(this);
        FlowDirection = AppearanceService.IsArabic
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        ChromeTitleText.Text = T("Speaker Intelligence", "ذكاء المتحدثين");
        MeetingTitleText.Text = meeting.PrimaryTitle;
        ApplyLocalizedCopy();
        Loaded += (_, _) => LoadExisting();
        Closed += (_, _) => _player.Dispose();
    }

    private void LoadExisting()
    {
        _result = _speakerService.Load(_meeting);

        var profileCount = _profiles.Load().Count;
        VoiceProfileInfoText.Text = profileCount == 0
            ? T(
                "No remembered voices yet • profiles stay local on this Windows account.",
                "لا توجد أصوات محفوظة بعد • تبقى الملفات الصوتية التعريفية محليًا على هذا الحساب.")
            : T(
                $"{profileCount} remembered voice profile(s) available for future auto-matching.",
                $"{profileCount} بصمة صوتية محفوظة ومتاحة للمطابقة التلقائية مستقبلًا.");

        if (_result is null)
        {
            SummaryText.Text = T(
                "No speaker analysis yet.",
                "لا يوجد تحليل للمتحدثين بعد.");
            StatusText.Text = T(
                "Ready to analyze",
                "جاهز للتحليل");
            TranscriptList.ItemsSource = Array.Empty<SpeakerTranscriptLine>();
            EmptyStateText.Visibility = Visibility.Visible;
            RenderSpeakerChips();
            return;
        }

        EmptyStateText.Visibility = Visibility.Collapsed;
        _transcripts.RefreshNames(_result);
        TranscriptList.ItemsSource = null;
        TranscriptList.ItemsSource = _result.Lines;
        var autoMatches = _result.SpeakerMatchKinds.Values.Count(x =>
            x.Equals("Auto match", StringComparison.OrdinalIgnoreCase));
        var confirmedMatches = _result.SpeakerMatchKinds.Values.Count(x =>
            x.Equals("Confirmed", StringComparison.OrdinalIgnoreCase));

        SummaryText.Text = T(
            $"{_result.DetectedSpeakerCount} speaker(s) • {_result.Lines.Count} timed transcript line(s)",
            $"{_result.DetectedSpeakerCount} متحدث • {_result.Lines.Count} سطر نصي موقّت");

        StatusText.Text = autoMatches + confirmedMatches > 0
            ? T(
                $"Speaker analysis ready • {confirmedMatches} confirmed • {autoMatches} auto-matched",
                $"تحليل المتحدثين جاهز • {confirmedMatches} مؤكد • {autoMatches} مطابق تلقائيًا")
            : T(
                "Speaker analysis ready",
                "تحليل المتحدثين جاهز");
        RenderSpeakerChips();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            SetBusy(true);
            StatusText.Text = T(
                "Analyzing speakers…",
                "جارٍ تحليل المتحدثين…");

            var progress = new Progress<int>(p =>
            {
                SpeakerProgressBar.Value = p;
                StatusText.Text = T(
                    $"Analyzing speakers… {p}%",
                    $"جارٍ تحليل المتحدثين… {p}%");
            });

            _result = await _speakerService.AnalyzeAsync(_meeting, progress);
            LoadExisting();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Speaker Intelligence",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            StatusText.Text = T(
                "Analysis not completed",
                "لم يكتمل التحليل");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = !busy;
        RenameSpeakerButton.IsEnabled = !busy;
        RememberVoiceButton.IsEnabled = !busy;
        RejectMatchButton.IsEnabled = !busy;
        NewPersonButton.IsEnabled = !busy;
        SpeakerProgressBar.Visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!busy)
            SpeakerProgressBar.Value = 0;

        // Deliberately do not disable the whole Window. System disabled templates can
        // paint native white surfaces and break the selected Archestro theme.
    }

    private void RenderSpeakerChips()
    {
        SpeakerChipsPanel.Children.Clear();
        if (_result is null) return;

        foreach (var index in _result.Segments
                     .Select(x => x.SpeakerIndex)
                     .Distinct()
                     .OrderBy(x => x))
        {
            var displayName = _result.DisplayNameFor(index);
            var chipLabel = displayName;

            if (_result.SpeakerMatchKinds.TryGetValue(index, out var kind))
            {
                if (kind.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    chipLabel = T(
                        $"{displayName} • Confirmed by you",
                        $"{displayName} • مؤكد بواسطتك");
                }
                else if (_result.SpeakerMatchScores.TryGetValue(index, out var score))
                {
                    if (kind.Equals("Auto match", StringComparison.OrdinalIgnoreCase))
                        chipLabel = T(
                            $"Likely {displayName} • {score:P0} voice similarity",
                            $"على الأرجح {displayName} • تشابه صوتي {score:P0}");
                    else if (kind.Equals("Possible", StringComparison.OrdinalIgnoreCase) &&
                             _result.SpeakerMatchCandidates.TryGetValue(index, out var candidate))
                        chipLabel = T(
                            $"Possible {candidate} • {score:P0} voice similarity",
                            $"احتمال {candidate} • تشابه صوتي {score:P0}");
                }
            }

            var button = new Button
            {
                Content = chipLabel,
                Tag = index,
                Margin = new Thickness(0, 0, 8, 5),
                Padding = new Thickness(10, 6, 10, 6),
                Background = (Brush)FindResource("SurfaceBrush"),
                Foreground = (Brush)FindResource("PrimaryTextBrush"),
                BorderBrush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(_result.ColorFor(index)))
            };
            button.Click += SpeakerChip_Click;
            SpeakerChipsPanel.Children.Add(button);
        }
    }

    private void SpeakerChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } || _result is null) return;

        _selectedSpeakerIndex = index;
        var selectedName = _result.DisplayNameFor(index);
        var selectedSuffix = "";

        if (_result.SpeakerMatchKinds.TryGetValue(index, out var kind))
        {
            if (kind.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))
                selectedSuffix = " • Confirmed by you";
            else if (_result.SpeakerMatchScores.TryGetValue(index, out var score))
                selectedSuffix = $" • {kind} {score:P0}";
        }

        StatusText.Text = T(
            $"Selected: {selectedName}{selectedSuffix}",
            $"تم اختيار: {selectedName}{selectedSuffix}");

        TranscriptList.ItemsSource = null;
        TranscriptList.ItemsSource = _result.Lines
            .Where(x => x.SpeakerIndex == index)
            .ToList();
    }

    private void RejectMatch_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || !_selectedSpeakerIndex.HasValue)
        {
            MessageBox.Show(T("Select a speaker chip first.", "اختر متحدثًا أولًا."), T("Not this person", "ليس هذا الشخص"));
            return;
        }

        var index = _selectedSpeakerIndex.Value;
        _speakerService.RejectCandidate(_meeting, _result, index);
        StatusText.Text = T(
            "Candidate removed. This speaker is no longer auto-linked to that person.",
            "تم إلغاء المطابقة. هذا المتحدث لم يعد مرتبطًا تلقائيًا بذلك الشخص.");
        LoadExisting();
        _selectedSpeakerIndex = index;
    }

    private async void NewPerson_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || !_selectedSpeakerIndex.HasValue)
        {
            MessageBox.Show(T("Select a speaker chip first.", "اختر متحدثًا أولًا."), T("New person", "شخص جديد"));
            return;
        }

        var index = _selectedSpeakerIndex.Value;
        var dialog = new TextPromptWindow(
            T("New person", "شخص جديد"),
            T("Person name", "اسم الشخص"),
            "") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;

        var name = dialog.Result.Trim();
        try
        {
            SetBusy(true);
            StatusText.Text = T("Creating a new local person and voice profile…", "جارٍ إنشاء شخص جديد وبصمته الصوتية محليًا…");
            _speakerService.RejectCandidate(_meeting, _result, index);
            _speakerService.RenameSpeaker(_meeting, _result, index, name);
            await _speakerService.RememberSpeakerAsync(_meeting, _result, index, name);
            StatusText.Text = T(
                $"New person saved locally: {name}. Open People to add/change the optional photo.",
                $"تم حفظ الشخص محليًا: {name}. افتح الأشخاص لإضافة أو تغيير الصورة الاختيارية.");
            LoadExisting();
            _selectedSpeakerIndex = index;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, T("New person", "شخص جديد"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenameSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || !_selectedSpeakerIndex.HasValue)
        {
            MessageBox.Show(
                T("Select a speaker chip first.", "اختر متحدثًا أولًا."),
                "Rename Speaker");
            return;
        }

        var index = _selectedSpeakerIndex.Value;
        var dialog = new TextPromptWindow(
            "Rename Speaker",
            "Speaker name",
            _result.DisplayNameFor(index)) { Owner = this };

        if (dialog.ShowDialog() != true ||
            string.IsNullOrWhiteSpace(dialog.Result))
            return;

        _speakerService.RenameSpeaker(
            _meeting,
            _result,
            index,
            dialog.Result);

        LoadExisting();
        _selectedSpeakerIndex = index;
    }

    private async void RememberVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || !_selectedSpeakerIndex.HasValue)
        {
            MessageBox.Show(
                T("Select a speaker chip first.", "اختر متحدثًا أولًا."),
                "Remember Voice");
            return;
        }

        var index = _selectedSpeakerIndex.Value;
        var current = _result.DisplayNameFor(index);

        if (current.StartsWith(
                "Speaker ",
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                T(
                    "Rename this speaker first, then choose Remember Voice.",
                    "أعد تسمية المتحدث أولًا ثم اختر تذكر الصوت."),
                "Remember Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            T(
                $"Save a LOCAL voice profile for “{current}”? Future meetings can automatically match this speaker when confidence is high.\n\nThe profile stays on this Windows user account and can be deleted later.",
                $"هل تريد حفظ بصمة صوتية محلية لـ “{current}”؟ يمكن للاجتماعات القادمة مطابقة هذا المتحدث تلقائيًا عندما تكون الثقة عالية.\n\nتبقى البصمة على حساب ويندوز هذا ويمكن حذفها لاحقًا."),
            "Remember Voice",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            SetBusy(true);
            StatusText.Text = T(
                "Creating local voice profile…",
                "جارٍ إنشاء البصمة الصوتية المحلية…");

            await _speakerService.RememberSpeakerAsync(
                _meeting,
                _result,
                index,
                current);

            StatusText.Text = T(
                $"Voice profile saved locally: {current}",
                $"تم حفظ البصمة الصوتية محليًا: {current}");

            VoiceProfileInfoText.Text = T(
                $"Future meetings can auto-match {current} when voice confidence is high.",
                $"يمكن للاجتماعات القادمة مطابقة {current} تلقائيًا عندما تكون ثقة الصوت عالية.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Remember Voice",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenPeople_Click(object sender, RoutedEventArgs e)
    {
        var window = new SpeakerProfilesWindow(_speakerService);
        window.Closed += (_, _) => LoadExisting();
        window.Show();
        window.Activate();
    }

    private void PlayLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: double seconds }) return;

        var path = File.Exists(_meeting.AudioPath)
            ? _meeting.AudioPath
            : _meeting.RecordingPath;

        if (!File.Exists(path))
        {
            MessageBox.Show(
                T(
                    "Meeting audio is missing.",
                    "ملف صوت الاجتماع غير متوفر."),
                "Play");
            return;
        }

        _player.PlayFrom(path, seconds);
    }

    private void StopAudio_Click(object sender, RoutedEventArgs e) =>
        _player.Stop();

    private void OpenOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_meeting.TranscriptPath))
            ProcessService.OpenPath(_meeting.TranscriptPath);
    }

    private void ApplyLocalizedCopy()
    {
        if (!AppearanceService.IsArabic) return;

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["↻ Analyze / Refresh"] = "↻ تحليل / تحديث",
            ["People"] = "الأشخاص",
            ["Open original transcript"] = "فتح النص الأصلي",
            ["Rename Speaker"] = "إعادة تسمية المتحدث",
            ["Remember Voice"] = "تذكر الصوت",
            ["Not this person"] = "ليس هذا الشخص",
            ["New person"] = "شخص جديد",
            ["Stop Audio"] = "إيقاف الصوت",
            ["Play"] = "تشغيل",
            ["▶ Play"] = "▶ تشغيل",
            ["No speaker analysis yet."] = "لا يوجد تحليل للمتحدثين بعد.",
            ["Give a speaker a name, then Remember Voice to help Archestro recognize the same person in future meetings."] =
                "سمِّ المتحدث ثم اختر تذكر الصوت لمساعدة Archestro على التعرف على الشخص نفسه في الاجتماعات القادمة.",
            ["Voice profiles stay local on this Windows account."] = "تبقى ملفات الصوت محليًا على حساب Windows هذا.",
            ["Minimize"] = "تصغير",
            ["Maximize / Restore"] = "تكبير / استعادة",
            ["Close"] = "إغلاق"
        };

        TranslateCopy(this, map);
    }

    private static void TranslateCopy(DependencyObject root, IReadOnlyDictionary<string, string> map)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && button.Content is string content &&
                map.TryGetValue(content, out var translated))
                button.Content = translated;
            else if (child is TextBlock text &&
                     map.TryGetValue(text.Text, out var translatedText))
                text.Text = translatedText;

            if (child is FrameworkElement element && element.ToolTip is string tip &&
                map.TryGetValue(tip, out var translatedTip))
                element.ToolTip = translatedTip;

            TranslateCopy(child, map);
        }
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
    private void ChromeMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ChromeMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();

}
