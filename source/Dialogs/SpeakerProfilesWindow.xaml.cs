using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Archestro.MeetingVault.Models;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault.Dialogs;

public partial class SpeakerProfilesWindow : Window
{
    private readonly SpeakerProfileStore _profiles = new();
    private readonly MeetingRepository _repository = new();
    private readonly SpeakerTranscriptService _transcripts = new();
    private readonly MeetingAudioPlayer _player = new();
    private readonly SpeakerIntelligenceService _speakerService;
    private List<SpeakerProfileDirectoryItem> _allProfiles = new();
    private SpeakerProfileDirectoryItem? _selectedProfile;
    private string? _pendingPhotoPath;

    public SpeakerProfilesWindow(SpeakerIntelligenceService speakerService)
    {
        InitializeComponent();
        _speakerService = speakerService;
        AppearanceService.Apply(this);
        FlowDirection = AppearanceService.IsArabic
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        ChromeTitleText.Text = T("People • Remembered voices", "الأشخاص • الأصوات المحفوظة");
        ApplyLocalizedCopy();

        Loaded += (_, _) => RefreshDirectory();
        Closed += (_, _) => _player.Dispose();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDirectory();

    private void RefreshDirectory(string? preferredProfileId = null)
    {
        preferredProfileId ??= _selectedProfile?.Profile.Id;
        var meetings = _repository.Search(null, null, 5000);
        var analyses = meetings
            .Select(m => (Meeting: m, Result: _transcripts.Load(m)))
            .Where(x => x.Result is not null)
            .Select(x => (x.Meeting, Result: x.Result!))
            .ToList();

        _allProfiles = _profiles.Load()
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => BuildProfileItem(profile, analyses))
            .ToList();

        ApplyProfileFilter();

        if (!string.IsNullOrWhiteSpace(preferredProfileId))
        {
            var preferred = _allProfiles.FirstOrDefault(x => x.Profile.Id.Equals(preferredProfileId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                ProfilesList.SelectedItem = preferred;
        }

        if (_allProfiles.Count == 0)
        {
            SelectedPersonText.Text = T("No saved people yet", "لا يوجد أشخاص محفوظون بعد");
            SelectedPersonInfoText.Text =
                T("Open Speaker Intelligence for a meeting, name a speaker, then choose Remember Voice.", "افتح ذكاء المتحدثين لاجتماع، سمِّ المتحدث، ثم اختر تذكر الصوت.");
            MatchesList.ItemsSource = Array.Empty<SpeakerMeetingMatchItem>();
            EmptyMatchesText.Visibility = Visibility.Visible;
            TestVoiceButton.IsEnabled = false;
            ForgetVoiceButton.IsEnabled = false;
            AddPhotoButton.IsEnabled = false;
            ResetSelectedPhoto();
            FooterStatusText.Text = T("Voice profiles stay local on this Windows account.", "تبقى بصمات الصوت محليًا على حساب ويندوز هذا.");
        }
    }

    private SpeakerProfileDirectoryItem BuildProfileItem(
        SpeakerProfile profile,
        IEnumerable<(MeetingRecord Meeting, SpeakerAnalysisResult Result)> analyses)
    {
        var matches = new List<SpeakerMeetingMatchItem>();

        foreach (var pair in analyses)
        {
            var meeting = pair.Meeting;
            var result = pair.Result;

            foreach (var speakerIndex in result.Segments
                         .Select(x => x.SpeakerIndex)
                         .Distinct()
                         .OrderBy(x => x))
            {
                var assignedName = result.DisplayNameFor(speakerIndex);
                result.SpeakerMatchCandidates.TryGetValue(speakerIndex, out var candidate);
                result.SpeakerMatchScores.TryGetValue(speakerIndex, out var score);
                result.SpeakerMatchKinds.TryGetValue(speakerIndex, out var kind);

                var confirmedByProfile = profile.ConfirmedSpeakerRefs.Contains(
                    $"{meeting.Id}:{speakerIndex}",
                    StringComparer.OrdinalIgnoreCase);
                var assigned = assignedName.Equals(
                    profile.Name,
                    StringComparison.OrdinalIgnoreCase);
                var candidateMatch = !string.IsNullOrWhiteSpace(candidate) &&
                                     candidate.Equals(
                                         profile.Name,
                                         StringComparison.OrdinalIgnoreCase);
                var possibleCandidate =
                    candidateMatch &&
                    string.Equals(
                        kind,
                        "Possible",
                        StringComparison.OrdinalIgnoreCase);

                if (!confirmedByProfile && !assigned && !possibleCandidate)
                    continue;

                var firstLine = result.Lines
                    .Where(x => x.SpeakerIndex == speakerIndex)
                    .OrderBy(x => x.StartSeconds)
                    .FirstOrDefault();

                var resolvedKind = confirmedByProfile ||
                                   string.Equals(kind, "Confirmed", StringComparison.OrdinalIgnoreCase)
                    ? "Confirmed"
                    : string.Equals(kind, "Auto match", StringComparison.OrdinalIgnoreCase)
                        ? "Auto match"
                        : possibleCandidate
                            ? "Possible"
                            : "Named";

                matches.Add(new SpeakerMeetingMatchItem
                {
                    Meeting = meeting,
                    SpeakerIndex = speakerIndex,
                    PersonName = profile.Name,
                    MatchKind = resolvedKind,
                    Score = score,
                    StartSeconds = firstLine?.StartSeconds ?? 0,
                    Snippet = BuildSnippet(firstLine?.Text),
                    RecognitionThreshold = _speakerService.RecognitionThreshold
                });
            }
        }

        matches = matches
            .OrderByDescending(x => x.Meeting.StartLocal)
            .ToList();

        var confirmed = matches.Count(x => x.MatchKind == "Confirmed");
        var automatic = matches.Count(x => x.MatchKind == "Auto match");
        var possible = matches.Count(x => x.MatchKind == "Possible");

        return new SpeakerProfileDirectoryItem
        {
            Profile = profile,
            Matches = matches,
            Summary = AppearanceService.IsArabic
                ? $"{matches.Count} اجتماع • {confirmed} مؤكد • {automatic} تلقائي • {possible} محتمل"
                : $"{matches.Count} meeting(s) • {confirmed} confirmed • {automatic} auto • {possible} possible",
            UpdatedText = AppearanceService.IsArabic
                ? $"ملف محلي • {Math.Max(1, profile.SampleCount)} عينة محفوظة • تم التحديث {profile.UpdatedLocal:dd MMM yyyy}"
                : $"Local profile • {Math.Max(1, profile.SampleCount)} remembered sample(s) • updated {profile.UpdatedLocal:dd MMM yyyy}"
        };
    }

    private static string BuildSnippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "No timed transcript snippet is available for this speaker.";

        var clean = string.Join(
            " ",
            text.Split(
                new[] { '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries));

        return clean.Length <= 220
            ? clean
            : clean[..217] + "…";
    }

    private void PeopleSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyProfileFilter();

    private void ApplyProfileFilter()
    {
        var query = PeopleSearchBox?.Text?.Trim() ?? "";
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allProfiles
            : _allProfiles
                .Where(x => x.Name.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

        ProfilesList.ItemsSource = null;
        ProfilesList.ItemsSource = filtered;

        if (_selectedProfile is null && filtered.Count > 0)
        {
            ProfilesList.SelectedIndex = 0;
            return;
        }

        if (_selectedProfile is not null)
        {
            var replacement = filtered.FirstOrDefault(x =>
                x.Profile.Id.Equals(
                    _selectedProfile.Profile.Id,
                    StringComparison.OrdinalIgnoreCase));

            if (replacement is not null)
                ProfilesList.SelectedItem = replacement;
        }
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProfile = ProfilesList.SelectedItem as SpeakerProfileDirectoryItem;

        if (_selectedProfile is null)
        {
            SelectedPersonText.Text = T("Select a saved person", "اختر شخصًا محفوظًا");
            SelectedPersonInfoText.Text =
                T("Choose a profile to see confirmed, automatic, and possible voice matches.", "اختر ملفًا لرؤية المطابقات المؤكدة والتلقائية والمحتملة.");
            MatchesList.ItemsSource = Array.Empty<SpeakerMeetingMatchItem>();
            EmptyMatchesText.Visibility = Visibility.Visible;
            TestVoiceButton.IsEnabled = false;
            ForgetVoiceButton.IsEnabled = false;
            AddPhotoButton.IsEnabled = false;
            ResetSelectedPhoto();
            return;
        }

        SelectedPersonText.Text = _selectedProfile.Name;
        SelectedPersonInfoText.Text = T(
            $"{_selectedProfile.Summary}. Confirmed means you approved the voice; percentages are voice similarity, not identity proof.",
            $"{_selectedProfile.Summary}. المؤكد يعني أنك اعتمدت الصوت؛ النسب هي تشابه صوتي وليست إثبات هوية.");

        MatchesList.ItemsSource = null;
        MatchesList.ItemsSource = _selectedProfile.Matches;
        EmptyMatchesText.Visibility = _selectedProfile.Matches.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        TestVoiceButton.IsEnabled = _selectedProfile.Matches.Count > 0;
        ForgetVoiceButton.IsEnabled = true;
        AddPhotoButton.IsEnabled = true;
        _pendingPhotoPath = null;
        SavePhotoButton.Visibility = Visibility.Visible;
        SavePhotoButton.IsEnabled = false;
        UpdateSelectedPhoto();
    }

    private SpeakerMeetingMatchItem? BestTestMatch()
    {
        if (_selectedProfile is null) return null;

        return _selectedProfile.Matches
            .OrderBy(x => x.MatchKind == "Confirmed" ? 0 :
                          x.MatchKind == "Auto match" ? 1 :
                          x.MatchKind == "Named" ? 2 : 3)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.Meeting.StartLocal)
            .FirstOrDefault();
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        var match = BestTestMatch();
        if (match is null)
        {
            FooterStatusText.Text = T("No matched meeting audio is available for this profile yet.", "لا يوجد صوت اجتماع مطابق متاح لهذا الملف بعد.");
            return;
        }

        PlayMatch(match);
    }

    private void PlayMatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SpeakerMeetingMatchItem match })
            PlayMatch(match);
    }

    private void PlayMatch(SpeakerMeetingMatchItem match)
    {
        var path = File.Exists(match.Meeting.AudioPath)
            ? match.Meeting.AudioPath
            : match.Meeting.RecordingPath;

        if (!File.Exists(path))
        {
            FooterStatusText.Text = T("Meeting audio is not available for this match.", "صوت الاجتماع غير متاح لهذه المطابقة.");
            return;
        }

        var sampleTime = TimeSpan.FromSeconds(match.StartSeconds).ToString(@"hh\:mm\:ss");
        var window = new AudioPreviewWindow(path, match.StartSeconds, match.Meeting.PrimaryTitle, sampleTime)
        {
            Owner = this
        };
        window.Show();
        window.Activate();
        FooterStatusText.Text = T(
            $"Opened audio preview for {_selectedProfile?.Name ?? match.PersonName} from “{match.Meeting.PrimaryTitle}” at {sampleTime}.",
            $"تم فتح معاينة الصوت لـ {_selectedProfile?.Name ?? match.PersonName} من «{match.Meeting.PrimaryTitle}» عند {sampleTime}.");
    }

    private void OpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpeakerMeetingMatchItem match })
            return;

        if (File.Exists(match.Meeting.TranscriptPath))
        {
            var window = new TranscriptViewerWindow(match.Meeting.TranscriptPath, match.Meeting.PrimaryTitle, match.StartSeconds)
            {
                Owner = this
            };
            window.Show();
            window.Activate();
            return;
        }

        FooterStatusText.Text = T("Transcript is not available for this meeting.", "النص غير متاح لهذا الاجتماع.");
    }

    private void OpenSpeakerView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpeakerMeetingMatchItem match })
            return;

        var window = new SpeakerTranscriptWindow(
            match.Meeting,
            _speakerService);
        window.Closed += (_, _) => RefreshDirectory();
        window.Show();
        window.Activate();
    }

    private async void ConfirmMatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpeakerMeetingMatchItem match } ||
            _selectedProfile is null ||
            !match.CanConfirm)
            return;

        var result = _transcripts.Load(match.Meeting);
        if (result is null)
        {
            FooterStatusText.Text = T("Speaker analysis must be refreshed before this match can be confirmed.", "يجب تحديث تحليل المتحدثين قبل تأكيد هذه المطابقة.");
            return;
        }

        try
        {
            FooterStatusText.Text =
                $"Confirming {_selectedProfile.Name} from this meeting…";

            await _speakerService.RememberSpeakerAsync(
                match.Meeting,
                result,
                match.SpeakerIndex,
                _selectedProfile.Name);

            FooterStatusText.Text =
                $"Confirmed: {_selectedProfile.Name}. The local voice profile was strengthened with this meeting.";
            RefreshDirectory();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Confirm voice match",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void ForgetVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        var answer = MessageBox.Show(
            $"Forget the local voice profile for “{_selectedProfile.Name}”?\n\nExisting meeting transcripts and speaker labels are not deleted.",
            "Forget voice",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        if (_profiles.Remove(_selectedProfile.Name))
        {
            FooterStatusText.Text =
                $"Local voice profile forgotten: {_selectedProfile.Name}. Existing meeting records were kept.";
            _selectedProfile = null;
            RefreshDirectory();
        }
    }

    private void AddPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Supported images|*.png;*.jpg;*.jpeg;*.bmp|PNG|*.png|JPEG|*.jpg;*.jpeg|Bitmap|*.bmp",
            Title = T("Choose a person photo", "اختر صورة الشخص")
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _pendingPhotoPath = dialog.FileName;
            SelectedPersonPhoto.Source = LoadPhotoBitmap(_pendingPhotoPath);
            SelectedPersonPhoto.Visibility = Visibility.Visible;
            SelectedPersonPhotoPlaceholder.Visibility = Visibility.Collapsed;
            SavePhotoButton.Visibility = Visibility.Visible;
            SavePhotoButton.IsEnabled = true;
            FooterStatusText.Text = T(
                "Photo preview ready • choose Save photo to keep this change.",
                "معاينة الصورة جاهزة • اختر حفظ الصورة لتثبيت التغيير.");
        }
        catch (Exception ex)
        {
            LogPhotoFailure("preview", ex);
            _pendingPhotoPath = null;
            SavePhotoButton.Visibility = Visibility.Visible;
            SavePhotoButton.IsEnabled = false;
            FooterStatusText.Text = T(
                "Photo preview failed. Use a valid PNG, JPG, or BMP image.",
                "تعذرت معاينة الصورة. استخدم صورة PNG أو JPG أو BMP صالحة.");
        }
    }

    private void SavePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || string.IsNullOrWhiteSpace(_pendingPhotoPath)) return;

        var profileId = _selectedProfile.Profile.Id;
        try
        {
            if (string.IsNullOrWhiteSpace(profileId))
                profileId = _profiles.EnsureStableId(_selectedProfile.Name);
            SavePhotoButton.IsEnabled = false;
            FooterStatusText.Text = T("Saving photo locally…", "جارٍ حفظ الصورة محليًا…");
            _profiles.SetPhoto(profileId, _pendingPhotoPath);

            RefreshDirectory(profileId);
            var refreshed = _allProfiles.FirstOrDefault(x =>
                x.Profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.Profile.PhotoPath) || !File.Exists(refreshed.Profile.PhotoPath))
                throw new IOException("The saved photo could not be verified after profile reload.");

            _selectedProfile = refreshed;
            ProfilesList.SelectedItem = refreshed;
            _pendingPhotoPath = null;
            SavePhotoButton.Visibility = Visibility.Visible;
            SavePhotoButton.IsEnabled = false;
            UpdateSelectedPhoto();
            FooterStatusText.Text = T("Photo saved ✓", "تم حفظ الصورة ✓");
        }
        catch (Exception ex)
        {
            LogPhotoFailure("save", ex);
            SavePhotoButton.IsEnabled = true;
            SavePhotoButton.Visibility = Visibility.Visible;
            FooterStatusText.Text = T(
                "Photo was not saved. The detailed error was written to the local Archestro log.",
                "لم يتم حفظ الصورة. تم تسجيل تفاصيل الخطأ في سجل Archestro المحلي.");
        }
    }

    private static BitmapSource LoadPhotoBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("The image contains no readable frame.");
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static void LogPhotoFailure(string stage, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            File.AppendAllText(
                Path.Combine(AppPaths.Logs, "people-photo-errors.log"),
                $"[{DateTimeOffset.Now:O}] stage={stage}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    private void UpdateSelectedPhoto()
    {
        var path = _selectedProfile?.Profile.PhotoPath ?? string.Empty;
        var hasPhoto = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        AddPhotoButton.Content = T(hasPhoto ? "Change photo" : "Choose photo", hasPhoto ? "تغيير الصورة" : "اختيار صورة");
        if (string.IsNullOrWhiteSpace(_pendingPhotoPath))
        {
            SavePhotoButton.Visibility = Visibility.Visible;
            SavePhotoButton.IsEnabled = false;
        }

        if (!hasPhoto)
        {
            ResetSelectedPhoto();
            return;
        }

        try
        {
            // Load from a file stream into memory so WPF cannot serve a stale URI image-cache entry
            // after the owner replaces a profile photo with a newer file.
            SelectedPersonPhoto.Source = LoadPhotoBitmap(path);
            SelectedPersonPhoto.Visibility = Visibility.Visible;
            SelectedPersonPhotoPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ResetSelectedPhoto();
        }
    }

    private void ResetSelectedPhoto()
    {
        SelectedPersonPhoto.Source = null;
        SelectedPersonPhoto.Visibility = Visibility.Collapsed;
        SelectedPersonPhotoPlaceholder.Visibility = Visibility.Visible;
    }

    private void StopAudio_Click(object sender, RoutedEventArgs e) => _player.Stop();

    private static string T(string en, string ar) =>
        AppearanceService.IsArabic ? ar : en;

    private void ApplyLocalizedCopy()
    {
        if (!AppearanceService.IsArabic) return;

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["People"] = "الأشخاص",
            ["Remembered voices, matched meetings, and the evidence behind every match — all kept local. Matches appear after Speaker Intelligence analyzes a meeting."] =
                "الأصوات المحفوظة والاجتماعات المطابقة والدليل خلف كل مطابقة — كلها محلية. تظهر المطابقات بعد تحليل المتحدثين للاجتماع.",
            ["Refresh"] = "تحديث",
            ["▶ Test voice"] = "▶ اختبار الصوت",
            ["Forget voice"] = "نسيان الصوت",
            ["Saved people"] = "الأشخاص المحفوظون",
            ["Select a saved person"] = "اختر شخصًا محفوظًا",
            ["Choose a profile to see confirmed, automatic, and possible voice matches."] =
                "اختر ملفًا لرؤية المطابقات المؤكدة والتلقائية والمحتملة.",
            ["Confirmed means you explicitly saved or approved the voice. Percentages are model similarity, not identity proof."] =
                "المؤكد يعني أنك حفظت أو اعتمدت الصوت صراحةً. النسب تشابه صوتي وليست إثبات هوية.",
            ["No matched meetings for this person yet."] = "لا توجد اجتماعات مطابقة لهذا الشخص بعد.",
            ["Play sample"] = "تشغيل عينة",
            ["Open transcript"] = "فتح النص",
            ["Speaker view"] = "عرض المتحدث",
            ["Confirm match"] = "تأكيد المطابقة",
            ["Add photo"] = "إضافة صورة",
            ["Change photo"] = "تغيير الصورة",
            ["Stop audio"] = "إيقاف الصوت",
            ["Voice profiles stay local on this Windows account."] = "تبقى ملفات الصوت محليًا على حساب Windows هذا.",
            ["▶ Play sample"] = "▶ تشغيل عينة",
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
            if (child is TextBlock text && map.TryGetValue(text.Text, out var translatedText))
                text.Text = translatedText;
            else if (child is Button button && button.Content is string content &&
                     map.TryGetValue(content, out var translatedButton))
                button.Content = translatedButton;

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

public sealed class SpeakerProfileDirectoryItem
{
    public SpeakerProfile Profile { get; set; } = new();
    public List<SpeakerMeetingMatchItem> Matches { get; set; } = new();
    public string Name => Profile.Name;
    public string Summary { get; set; } = "";
    public string UpdatedText { get; set; } = "";
}

public sealed class SpeakerMeetingMatchItem
{
    public MeetingRecord Meeting { get; set; } = new();
    public int SpeakerIndex { get; set; }
    public string PersonName { get; set; } = "";
    public string MatchKind { get; set; } = "";
    public float Score { get; set; }
    public double StartSeconds { get; set; }
    public string Snippet { get; set; } = "";
    public float RecognitionThreshold { get; set; }

    public string MeetingTitle => Meeting.PrimaryTitle;
    public string MeetingMeta =>
        $"{Meeting.StartLocal:dd MMM yyyy • hh:mm tt} • {Meeting.DurationText}";

    public bool CanConfirm => MatchKind is "Possible" or "Auto match" or "Named";

    public Visibility ConfirmVisibility =>
        CanConfirm ? Visibility.Visible : Visibility.Collapsed;

    public string ConfidenceLabel
    {
        get
        {
            var arabic = AppearanceService.IsArabic;
            return MatchKind switch
            {
                "Confirmed" => arabic ? "مؤكد بواسطتك" : "Confirmed by you",
                "Auto match" => arabic
                    ? $"مطابقة مرجحة • {PersonName} • تشابه صوتي {Score:P0}"
                    : $"Likely {PersonName} • {Score:P0} voice similarity",
                "Possible" => arabic
                    ? $"مطابقة محتملة • {PersonName} • تشابه صوتي {Score:P0}"
                    : $"Possible {PersonName} • {Score:P0} voice similarity",
                "Named" => arabic ? "اسم محفوظ • الدرجة غير متاحة" : "Named • score unavailable",
                _ => MatchKind
            };
        }
    }

    public string ConfidenceColor =>
        MatchKind switch
        {
            "Confirmed" => "#36D98A",
            "Auto match" => Score >= Math.Min(0.99f, RecognitionThreshold + 0.12f)
                ? "#36D98A"
                : "#7AA0C0",
            "Possible" => "#E9A63A",
            _ => "#9DA9B8"
        };


}
