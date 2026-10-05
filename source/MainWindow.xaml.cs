using System.Diagnostics;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Line = System.Windows.Shapes.Line;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Archestro.MeetingVault.Dialogs;
using Archestro.MeetingVault.Models;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault;

public partial class MainWindow : Window
{
    private readonly BrandingService _brandingService = new();
    private readonly BrandConfig _brand;
    private readonly MeetingRepository _repo = new();
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService = new();
    private readonly RecordingService _recording;
    private readonly TranscriptionService _transcription;
    private readonly AudioMeterService _audioMeter = new();
    private readonly SpeakerIntelligenceService _speakerIntelligence;
    private readonly MeetingIntelligenceService _meetingIntelligence;
    private readonly LicenseService _license = new();

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _visualTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _tickerMotionTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private CancellationTokenSource? _searchRefreshCts;
    private bool _libraryVisualPrewarmed;
    private bool _loadingSettingsView;
    private string _resolvedAppearance = "Dark";
    private HwndSource? _hwndSource;

    private readonly List<ImportantMark> _marks = new();
    private readonly List<double> _waveSamples = new();

    private DateTimeOffset? _recordStarted;
    private DateTimeOffset? _pauseStarted;
    private TimeSpan _pausedTotal = TimeSpan.Zero;
    private bool _recentVisible = true;
    private string _selectedCategory = "All";

    private double _latestMicLevel;
    private double _latestSystemLevel;
    private double _pulsePhase;
    private DateTimeOffset _lastAudioDeviceProbe = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTranscriptUiRefresh = DateTimeOffset.MinValue;
    private string _lastTickerText = "";
    private bool _tickerActive;
    private DateTimeOffset _tickerLastTick = DateTimeOffset.Now;
    private double _tickerTextWidth = 1;
    private double _tickerViewportWidth = 1;
    private double _tickerX;

    private readonly ObservableCollection<VaultChatMessage> _vaultMessages = new();
    private readonly MeetingAudioPlayer _vaultEvidencePlayer = new();
    private bool _vaultAskBusy;
    private bool _vaultFocusMode;
    private WindowState _vaultPreviousWindowState = WindowState.Normal;
    private GridLength _vaultPreviousSidebarWidth = new(232);
    private Thickness _vaultPreviousMainMargin = new(24, 18, 24, 12);

    private readonly DispatcherTimer _vaultBusyTimer = new() { Interval = TimeSpan.FromMilliseconds(480) };
    private readonly DispatcherTimer _settingsToastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _vaultBusyTick;
    private DateTimeOffset? _vaultBusyStarted;
    private bool _libraryAudioImportBusy;
    private SpeakerProfilesWindow? _peopleWindow;
    private readonly Dictionary<string, IntelligenceWindow> _intelligenceWindows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpeakerTranscriptWindow> _speakerWindows =
        new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        _brand = _brandingService.Load();
        InitializeComponent();

        _settings = _settingsService.LoadOrDiscover();
        ApplyCommercialBranding();
        ApplyPolishedAppearance(_settings.Appearance);
        _recentVisible = _settings.ShowRecentRecordings;

        _recording = new RecordingService(_settings, _repo);
        _transcription = new TranscriptionService(_settings, _repo);
        _speakerIntelligence = new SpeakerIntelligenceService(_settings);
        _meetingIntelligence = new MeetingIntelligenceService(_settings, _repo);

        _audioMeter.LevelsChanged += (mic, system) =>
        {
            _latestMicLevel = mic;
            _latestSystemLevel = system;
        };

        _clockTimer.Tick += (_, _) => UpdateClockAndStatus();
        _visualTimer.Tick += (_, _) => UpdateLiveVisuals();
        _searchDebounceTimer.Tick += async (_, _) =>
        {
            _searchDebounceTimer.Stop();
            await RefreshLibraryAsync();
        };
        _tickerMotionTimer.Tick += (_, _) => AdvanceTranscriptionTicker();
        _vaultBusyTimer.Tick += (_, _) => AdvanceVaultBusyPulse();
        _settingsToastTimer.Tick += (_, _) => HideSettingsSaveToast();

        if (VaultConversationList is not null)
            VaultConversationList.ItemsSource = _vaultMessages;

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += (_, _) => UpdateWindowBoundsForState();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FitWindowToWorkArea();
        ApplyRecentVisibility();
        SetActiveNav("Home");
        UpdateNameState();
        UpdateSearchPlaceholder();

        _audioMeter.Start();
        _transcription.RecoverInterruptedStatuses();
        RefreshStatusCards();
        RefreshAll();
        ApplyLanguage(_settings.PreferredLanguage);
        UpdateClockAndStatus();
        _clockTimer.Start();
        _visualTimer.Start();
        _tickerLastTick = DateTimeOffset.Now;
        _tickerMotionTimer.Start();
        EnsureVaultWelcomeMessage();

        FooterText.Text = _brand.Footer.Replace("{year}", DateTime.Now.Year.ToString());
        Keyboard.ClearFocus();

        // Keep the Library/Search visual tree resident and arranged before the owner first clicks Search.
        ScheduleSearchPrewarm();

        await Task.Run(() => new MeetingImportService(_repo).ImportExisting());

        if (_settings.ResumeQueuedTranscriptsOnStartup)
            ResumeQuietBackgroundQueue();

        RefreshAll();
        ApplyLanguage(_settings.PreferredLanguage);

        try
        {
            File.WriteAllText(
                Path.Combine(AppPaths.Logs, "startup-ready.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = "READY",
                    time = DateTimeOffset.Now,
                    version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? ""
                }));
        }
        catch { }
    }


    private void ApplyPolishedAppearance(string? requested)
    {
        AppearanceService.Configure(requested, _settings.PreferredLanguage);
        _resolvedAppearance = AppearanceService.ResolvedTheme;
        AppearanceService.Apply(this, AppearanceService.RequestedTheme);

        // Keep recorder/status accents in the same semantic palette as the selected theme.
        if (FindResource("PrimaryActionBrush") is SolidColorBrush accent)
        {
            RecordStateText.Foreground = accent;
            if (RecordGlowEllipse.Fill is RadialGradientBrush glow && glow.GradientStops.Count >= 2)
            {
                var strong = accent.Color;
                glow.GradientStops[0].Color = Color.FromArgb(0x66, strong.R, strong.G, strong.B);
                glow.GradientStops[1].Color = Color.FromArgb(0x00, strong.R, strong.G, strong.B);
            }
        }

        // Theme changes must not reset the independent ticker motion phase.
        Dispatcher.BeginInvoke(new Action(() => RecalculateTickerGeometry(resetPosition: false)), DispatcherPriority.Background);
    }

    private void UpdateThemeResolvedLabel()
    {
        if (ThemeResolvedText is null)
            return;

        var requested = SettingsAppearance is not null && SettingsAppearance.SelectedItem is not null
            ? ComboValue(SettingsAppearance, _settings.Appearance)
            : _settings.Appearance;

        if (requested.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            ThemeResolvedText.Text = IsArabicLanguage
                ? $"النظام → {LocalizedThemeName(_resolvedAppearance)} • يتبع مظهر Windows"
                : $"System → {_resolvedAppearance} • follows Windows + accent";
        }
        else
        {
            ThemeResolvedText.Text = IsArabicLanguage
                ? $"معاينة مباشرة: {LocalizedThemeName(requested)}"
                : $"Live theme: {requested}";
        }
    }

    private void FitWindowToWorkArea()
    {
        // Normal-state sizing only. Maximized geometry is handled per active monitor
        // by WM_GETMINMAXINFO below; never cap MaxWidth/MaxHeight from primary WorkArea.
        if (WindowState != WindowState.Normal)
            return;

        var work = SystemParameters.WorkArea;
        var margin = 16d;
        var availableWidth = Math.Max(900d, work.Width - margin);
        var availableHeight = Math.Max(620d, work.Height - margin);

        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);

        Left = work.Left + Math.Max(0, (work.Width - Width) / 2d);
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2d);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyMonitorWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyMonitorWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var work = info.rcWork;
        var monitorRect = info.rcMonitor;

        mmi.ptMaxPosition.x = Math.Abs(work.left - monitorRect.left);
        mmi.ptMaxPosition.y = Math.Abs(work.top - monitorRect.top);
        mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
        mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class VaultChatMessage
    {
        public string Role { get; set; } = "";
        public string Body { get; set; } = "";
        public string Meta { get; set; } = "";
        public bool IsUser { get; set; }
        public IReadOnlyList<VaultEvidenceCard> EvidenceCards { get; set; } = Array.Empty<VaultEvidenceCard>();
        public HorizontalAlignment Alignment { get; set; } = HorizontalAlignment.Left;
        public Thickness BubbleMargin { get; set; }
        public FlowDirection FlowDirection { get; set; } = FlowDirection.LeftToRight;
        public TextAlignment TextAlignment { get; set; } = TextAlignment.Left;
        public XmlLanguage Language { get; set; } = XmlLanguage.GetLanguage("en-US");
    }


    private sealed class VaultEvidenceCard
    {
        public string Header { get; set; } = "";
        public IReadOnlyList<EvidenceRef> Items { get; set; } = Array.Empty<EvidenceRef>();
    }

    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private void UpdateWindowBoundsForState()
    {
        if (WindowState == WindowState.Normal)
            Dispatcher.BeginInvoke(new Action(FitWindowToWorkArea), DispatcherPriority.Background);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            FitWindowToWorkArea();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            if (WindowState == WindowState.Normal)
                FitWindowToWorkArea();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.F)
        {
            OpenSearch_Click(SearchNavButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_settings.Appearance.Equals("System", StringComparison.OrdinalIgnoreCase))
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyPolishedAppearance("System");
            UpdateThemeResolvedLabel();
        }), DispatcherPriority.Background);
    }

    private void ApplyCommercialBranding()
    {
        Title = _brand.WindowTitle;
        TitleProductText.Text = $"{_brand.CompanyName} • Meeting Vault";
        SidebarBrandName.Text = _brand.CompanyName.ToUpperInvariant();
        SidebarProductName.Text = "Meeting Vault";
        FooterText.Text = _brand.Footer.Replace("{year}", DateTime.Now.Year.ToString());
    }

    private string EffectiveGreetingName()
    {
        if (!string.IsNullOrWhiteSpace(_settings.GreetingName)) return _settings.GreetingName.Trim();
        if (!string.IsNullOrWhiteSpace(_settings.DisplayName)) return _settings.DisplayName.Trim();
        return Environment.UserName;
    }

    private void LoadCommercialSettingsView()
    {
        _loadingSettingsView = true;
        try
        {
            SettingsDisplayName.Text = _settings.DisplayName;
            SettingsOrganization.Text = _settings.Organization;
            SettingsJobTitle.Text = _settings.JobTitle;
            SettingsGreetingName.Text = _settings.GreetingName;
            SelectCombo(SettingsAppearance, string.IsNullOrWhiteSpace(_settings.Appearance) ? "System" : _settings.Appearance);
            SelectCombo(SettingsPreferredLanguage, string.IsNullOrWhiteSpace(_settings.PreferredLanguage) ? "English" : _settings.PreferredLanguage);
            SelectCombo(SettingsIntelligenceProvider, string.IsNullOrWhiteSpace(_settings.IntelligenceProvider) ? "Local" : _settings.IntelligenceProvider);
            SettingsCloudModel.Text = CloudProviderDefaults.Model(_settings.IntelligenceProvider, _settings.CloudIntelligenceModel);
            SettingsCloudApiKey.Clear();
            SettingsCloudEnabled.IsChecked = _settings.CloudIntelligenceEnabled;
            SettingsCloudFallback.IsChecked = _settings.CloudLocalFallbackEnabled;
            CloudProviderKeyStatus.Text = string.IsNullOrWhiteSpace(_settings.EncryptedIntelligenceApiKey)
                ? Ui("No API key saved.", "لم يتم حفظ مفتاح API.")
                : Ui("A key is stored encrypted for this Windows user.", "المفتاح محفوظ مشفرًا لهذا مستخدم Windows.");
            UpdateThemeResolvedLabel();
        }
        finally
        {
            _loadingSettingsView = false;
        }
    }

    private void SettingsAppearance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettingsView || !IsLoaded) return;
        var requested = ComboValue(SettingsAppearance, _settings.Appearance);
        _settings.Appearance = requested;
        _settingsService.Save(_settings);
        ApplyPolishedAppearance(requested);
        UpdateThemeResolvedLabel();
    }

    private void SettingsPreferredLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettingsView || !IsLoaded) return;
        var language = ComboValue(SettingsPreferredLanguage, _settings.PreferredLanguage);
        _settings.PreferredLanguage = language;
        _settingsService.Save(_settings);
        ApplyLanguage(language);
        ApplyPolishedAppearance(_settings.Appearance);
        UpdateThemeResolvedLabel();
    }

    private static void SelectCombo(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            var canonical = item.Tag?.ToString() ?? item.Content?.ToString();
            if (string.Equals(canonical, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string ComboValue(ComboBox box, string fallback) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString()
        ?? (box.SelectedItem as ComboBoxItem)?.Content?.ToString()
        ?? fallback;

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        HomeView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Hidden;
        CategoriesView.Visibility = Visibility.Collapsed;
        IntelligenceView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SetActiveNav("Settings");
        LoadCommercialSettingsView();
        ApplyLanguage(_settings.PreferredLanguage);
    }

    private void SaveCommercialSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.DisplayName = string.IsNullOrWhiteSpace(SettingsDisplayName.Text)
            ? Environment.UserName : SettingsDisplayName.Text.Trim();
        _settings.Organization = SettingsOrganization.Text.Trim();
        _settings.JobTitle = SettingsJobTitle.Text.Trim();
        _settings.GreetingName = SettingsGreetingName.Text.Trim();
        _settings.Appearance = ComboValue(SettingsAppearance, "System");
        _settings.PreferredLanguage = ComboValue(SettingsPreferredLanguage, "English");
        _settings.IntelligenceProvider = ComboValue(SettingsIntelligenceProvider, "Local");
        _settings.CloudIntelligenceModel = SettingsCloudModel.Text.Trim();
        if (!string.IsNullOrWhiteSpace(SettingsCloudApiKey.Password))
        {
            _settings.EncryptedIntelligenceApiKey = CloudSecretProtector.Protect(SettingsCloudApiKey.Password);
            SettingsCloudApiKey.Clear();
        }
        _settings.CloudIntelligenceEnabled = SettingsCloudEnabled.IsChecked == true && _settings.CloudIntelligenceConsentAccepted;
        _settings.CloudLocalFallbackEnabled = SettingsCloudFallback.IsChecked == true;

        _settingsService.Save(_settings);
        ApplyPolishedAppearance(_settings.Appearance);
        ApplyLanguage(_settings.PreferredLanguage);
        UpdateThemeResolvedLabel();
        UpdateClockAndStatus();

        ShowSettingsSaveToast(
            IsArabicLanguage ? "تم حفظ الإعدادات بنجاح." : "Settings saved successfully.");
    }

    private void SettingsIntelligenceProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettingsView || !IsLoaded) return;
        var previous = _settings.IntelligenceProvider;
        var selected = ComboValue(SettingsIntelligenceProvider, "Local");
        var previousDefault = CloudProviderDefaults.Model(previous);
        _settings.IntelligenceProvider = selected;
        if (string.IsNullOrWhiteSpace(SettingsCloudModel.Text) || SettingsCloudModel.Text == previousDefault)
            SettingsCloudModel.Text = CloudProviderDefaults.Model(selected);
        _settings.CloudIntelligenceModel = SettingsCloudModel.Text.Trim();
        _settingsService.Save(_settings);
    }

    private void SettingsCloudEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettingsView) return;
        if (SettingsCloudEnabled.IsChecked == true && !EnsureCloudConsent())
        {
            SettingsCloudEnabled.IsChecked = false;
            _settings.CloudIntelligenceEnabled = false;
            _settingsService.Save(_settings);
            return;
        }
        _settings.CloudIntelligenceEnabled = SettingsCloudEnabled.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private bool EnsureCloudConsent()
    {
        if (_settings.CloudIntelligenceConsentAccepted) return true;
        const string disclosure = "Cloud mode sends selected meeting transcript/evidence text to the chosen provider. Audio is not uploaded by default.";
        var answer = MessageBox.Show(disclosure + "\n\n" +
            (IsArabicLanguage ? "هل توافق على إرسال النص والأدلة المحددة إلى المزود السحابي؟ لن يُرفع الصوت." : "Do you consent to sending selected text and evidence to the cloud provider? Audio will not be uploaded."),
            Ui("Cloud privacy consent", "موافقة الخصوصية للسحابة"), MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return false;
        _settings.CloudIntelligenceConsentAccepted = true;
        _settingsService.Save(_settings);
        return true;
    }

    private async void TestCloudProvider_Click(object sender, RoutedEventArgs e)
    {
        var provider = ComboValue(SettingsIntelligenceProvider, "Local");
        if (provider != "Local" && SettingsCloudEnabled.IsChecked != true)
        {
            CloudProviderKeyStatus.Text = Ui("Enable cloud mode before testing; no network request was sent.", "فعّل الوضع السحابي قبل الاختبار؛ لم يُرسل أي طلب عبر الشبكة.");
            return;
        }
        if (provider != "Local" && !EnsureCloudConsent()) return;
        var enteredKey = SettingsCloudApiKey.Password;
        var succeeded = false;
        try
        {
            var key = provider == "Local" ? "" : !string.IsNullOrWhiteSpace(enteredKey)
                ? enteredKey
                : CloudSecretProtector.Unprotect(_settings.EncryptedIntelligenceApiKey);
            var model = SettingsCloudModel.Text.Trim();
            var router = new IntelligenceProviderRouter(_settings, new LocalIntelligenceProvider(_meetingIntelligence.LocalModel));
            var result = await router.TestConnectionAsync(provider, model, key, CancellationToken.None);
            _settings.IntelligenceProvider = provider;
            _settings.CloudIntelligenceModel = model;
            if (provider != "Local" && !string.IsNullOrWhiteSpace(enteredKey))
                _settings.EncryptedIntelligenceApiKey = CloudSecretProtector.Protect(enteredKey);
            _settingsService.Save(_settings);
            succeeded = true;
            ShowSettingsSaveToast(IsArabicLanguage ? "نجح اختبار الاتصال." : "Connection test succeeded.");
            CloudProviderKeyStatus.Text = result.Message;
        }
        catch (Exception ex)
        {
            CloudProviderKeyStatus.Text = ex.Message;
        }
        finally { if (succeeded) SettingsCloudApiKey.Clear(); }
    }

    private void RemoveCloudProviderKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.EncryptedIntelligenceApiKey = string.Empty;
        SettingsCloudApiKey.Clear();
        CloudProviderKeyStatus.Text = Ui("No API key saved.", "لم يتم حفظ مفتاح API.");
        _settingsService.Save(_settings);
    }

    private void ShowSettingsSaveToast(string message)
    {
        if (SettingsSaveToast is null || SettingsSaveToastText is null) return;
        SettingsSaveToastText.Text = message;
        SettingsSaveToast.Visibility = Visibility.Visible;
        _settingsToastTimer.Stop();
        _settingsToastTimer.Start();
    }

    private void HideSettingsSaveToast()
    {
        _settingsToastTimer.Stop();
        if (SettingsSaveToast is not null)
            SettingsSaveToast.Visibility = Visibility.Collapsed;
    }

    private bool IsArabicLanguage =>
        _settings.PreferredLanguage.Equals("Arabic", StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> ArabicUi =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Home"] = "الرئيسية",
            ["Library"] = "المكتبة",
            ["Search"] = "البحث",
            ["Categories"] = "التصنيفات",
            ["Settings"] = "الإعدادات",
            ["Intelligence Provider"] = "مزود الذكاء",
            ["Provider"] = "المزود",
            ["Model"] = "النموذج",
            ["API key (stored encrypted for this Windows user)"] = "مفتاح API (محفوظ بتشفير مرتبط بمستخدم Windows)",
            ["Enable cloud acceleration"] = "تفعيل التسريع السحابي",
            ["Use Local AI if the selected cloud provider is unavailable"] = "استخدم الذكاء المحلي إذا تعذر المزود السحابي المحدد",
            ["Cloud mode sends selected meeting transcript/evidence text to the chosen provider. Audio is not uploaded by default."] = "يرسل الوضع السحابي نص الاجتماع والأدلة المحددة إلى المزود المختار. لا يُرفع الصوت افتراضيًا.",
            ["No API key saved."] = "لم يتم حفظ مفتاح API.",
            ["A key is stored encrypted for this Windows user."] = "المفتاح محفوظ مشفرًا لمستخدم Windows الحالي.",
            ["Test Connection"] = "اختبار الاتصال",
            ["Remove Key"] = "إزالة المفتاح",
            ["Local (default)"] = "محلي (افتراضي)",
            ["Cloud privacy consent"] = "موافقة الخصوصية للسحابة",
            ["Privacy"] = "الخصوصية",
            ["Hide meeting names on Home with one click."] = "إخفاء أسماء الاجتماعات من الرئيسية بنقرة واحدة.",
            ["Welcome"] = "مرحبًا",
            ["Meeting name (optional)"] = "اسم الاجتماع (اختياري)",
            ["Optional • name is committed automatically when recording starts"] = "اختياري • يتم اعتماد الاسم تلقائيًا عند بدء التسجيل",
            ["READY TO RECORD"] = "جاهز للتسجيل",
            ["START MEETING"] = "بدء الاجتماع",
            ["Tap anywhere on the circle"] = "اضغط في أي مكان داخل الدائرة",
            ["LIVE AUDIO TIMELINE"] = "المخطط الصوتي المباشر",
            ["MARK THIS MOMENT"] = "علّم هذه اللحظة",
            ["Ready when the meeting starts."] = "جاهز عند بدء الاجتماع.",
            ["Microphone"] = "الميكروفون",
            ["System Audio"] = "صوت النظام",
            ["Storage"] = "التخزين",
            ["Transcription"] = "التفريغ النصي",
            ["Checking…"] = "جارٍ الفحص…",
            ["Recent recordings"] = "التسجيلات الأخيرة",
            ["Quick categories"] = "التصنيفات السريعة",
            ["Search meetings, notes, or transcript..."] = "ابحث في الاجتماعات أو الملاحظات أو النص...",
            ["Meeting"] = "الاجتماع",
            ["Import a recording"] = "استيراد تسجيل",
            ["Drop an audio file here, or choose a file. It stays local and appears as a recording in your Library."] = "اسحب ملفًا صوتيًا هنا أو اختر ملفًا من جهازك. يبقى محليًا ويظهر كتسجيل داخل المكتبة.",
            ["Import audio"] = "استيراد ملف صوتي",
            ["Transcript ready"] = "النص جاهز",
            ["Imported local recording"] = "تسجيل محلي مستورد",
            ["Suggested:"] = "مقترح:",
            ["Date & Time"] = "التاريخ والوقت",
            ["Duration"] = "المدة",
            ["Category"] = "التصنيف",
            ["Status"] = "الحالة",
            ["Actions"] = "الإجراءات",
            ["Choose what stays visible on Home. Everything else remains available under More."] = "اختر ما يظهر في الرئيسية. يبقى كل شيء آخر متاحًا ضمن المزيد.",
            ["Meetings"] = "الاجتماعات",
            ["Manage"] = "إدارة",
            ["Personalize Archestro Meeting Vault for this user and organization."] = "خصّص Archestro Meeting Vault لهذا المستخدم والمنظمة.",
            ["Profile"] = "الملف الشخصي",
            ["Display name"] = "اسم العرض",
            ["Organization"] = "المنظمة",
            ["Job title"] = "المسمى الوظيفي",
            ["Greeting name (optional)"] = "اسم الترحيب (اختياري)",
            ["Appearance"] = "المظهر",
            ["Theme"] = "السمة",
            ["System follows the Windows app appearance automatically."] = "يتبع النظام مظهر تطبيقات Windows تلقائيًا.",
            ["System follows Windows and uses the Windows accent."] = "يتبع النظام مظهر Windows ويستخدم لون التمييز الخاص به.",
            ["Preferred language"] = "اللغة المفضلة",
            ["Commercial foundation"] = "الأساس التجاري",
            ["Core shell ready • Speaker Intelligence is Build 2."] = "النظام الأساسي جاهز • ذكاء المتحدثين هو الإصدار 2.",
            ["View all recordings  →"] = "عرض كل التسجيلات  ←",
            ["Manage Categories"] = "إدارة التصنيفات",
            ["Save Settings"] = "حفظ الإعدادات",
            ["Import voice"] = "استيراد صوت",
            ["Open meeting"] = "فتح الاجتماع",
            ["Speaker view"] = "عرض المتحدث",
            ["System"] = "النظام",
            ["Dark"] = "داكن",
            ["Light"] = "فاتح",
            ["English"] = "الإنجليزية",
            ["Arabic"] = "العربية",
            ["All"] = "الكل",
            ["Internal"] = "داخلي",
            ["Management"] = "الإدارة",
            ["Clients"] = "العملاء",
            ["Projects"] = "المشاريع",
            ["Sales"] = "المبيعات",
            ["Contracts"] = "العقود",
            ["Procurement"] = "المشتريات",
            ["HR"] = "الموارد البشرية",
            ["Other"] = "أخرى",
            ["Uncategorized"] = "غير مصنف",
            ["Recording"] = "التسجيل",
            ["Transcript"] = "النص",
            ["Speakers"] = "المتحدثون",
            ["Intelligence"] = "التحليل",
            ["Add Category"] = "إضافة تصنيف",
            ["Rename"] = "إعادة تسمية",
            ["Delete"] = "حذف",
            ["Use"] = "استخدام",
            ["Transcribe Now"] = "تفريغ الآن",
            ["Speaker Intelligence"] = "ذكاء المتحدثين",
            ["Local Intelligence"] = "التحليل المحلي",
            ["Show recent recordings"] = "إظهار التسجيلات الأخيرة",
            ["Recent recordings visible"] = "التسجيلات الأخيرة ظاهرة",
            ["People"] = "الأشخاص",
            ["Vault Intelligence"] = "ذكاء الأرشيف",
            ["Ask anything about your meetings…"] = "اسأل أي شيء عن اجتماعاتك…",
            ["Find mentions"] = "البحث عن ذكر",
            ["Ask the Vault"] = "اسأل الأرشيف",
            ["Private • Local • Evidence-linked"] = "خاص • محلي • مرتبط بالدليل",
            ["Move to More"] = "نقل إلى المزيد",
            ["Visible on Home"] = "ظاهر في الرئيسية",
            ["Saved people"] = "الأشخاص المحفوظون",
            ["Refresh"] = "تحديث",
            ["Test voice"] = "اختبار الصوت",
            ["Forget voice"] = "نسيان الصوت",
            ["Meeting brief"] = "موجز الاجتماع",
            ["Ask this meeting"] = "اسأل هذا الاجتماع",
            ["Ask my vault"] = "اسأل أرشيفي",
            ["Build brief"] = "إنشاء الموجز",
            ["Brief style"] = "أسلوب الموجز",
            ["Executive summary"] = "الملخص التنفيذي",
            ["Key points"] = "النقاط الرئيسية",
            ["Decisions"] = "القرارات",
            ["Next actions"] = "الخطوات التالية",
            ["Commitments & deadlines"] = "الالتزامات والمواعيد",
            ["Risks & open questions"] = "المخاطر والأسئلة المفتوحة",
            ["+  Add Category"] = "+  إضافة تصنيف",
            ["Ask across your meeting memory. Archestro finds the exact meetings, speakers and moments behind every answer."] =
                "اسأل عبر ذاكرة اجتماعاتك. يربط Archestro الإجابة بالاجتماعات والمتحدثين واللحظات الدقيقة.",
            ["Find mentions = exact recall  •  Ask = evidence-grounded answer"] =
                "البحث عن ذكر = استرجاع مباشر  •  السؤال = إجابة مرتبطة بالدليل",
            ["Search an exact phrase or ask a question across your local meeting archive."] =
                "ابحث عن عبارة محددة أو اسأل سؤالًا عبر أرشيف اجتماعاتك المحلي.",
            ["Ask a question, or search an exact word to see every matching meeting."] =
                "اسأل سؤالًا أو ابحث عن كلمة محددة لرؤية كل اجتماع مطابق.",
            ["Tap the circle to start"] = "اضغط على الدائرة للبدء",
            ["Ⅱ PAUSED • NOT RECORDING"] = "Ⅱ متوقف مؤقتًا • لا يتم التسجيل",
            ["Transcription ready • new recordings will move through capture, language recovery, and finalization here."] =
                "التفريغ جاهز • تمر التسجيلات الجديدة هنا عبر الالتقاط ومعالجة اللغة والإنهاء.",
            ["◉  Hide recent recordings"] = "◉  إخفاء التسجيلات الأخيرة",
            ["◉  Hide"] = "◉  إخفاء",
            ["⏸  PAUSE"] = "⏸  إيقاف مؤقت",
            ["✓ Use"] = "✓ استخدام",
            ["✓ Use title"] = "✓ استخدام الاسم",
            ["▶ Recording"] = "▶ التسجيل",
            ["📝 Transcript"] = "📝 النص",
            ["👥 Speakers"] = "👥 المتحدثون",
            ["★ Meeting Report"] = "★ تقرير الاجتماع",
            ["Executive summary, decisions, actions and evidence"] = "ملخص تنفيذي وقرارات ومهام وأدلة",
            ["Meeting Report"] = "تقرير الاجتماع",
            ["Generate report"] = "إنشاء التقرير",
            ["Report lens"] = "نمط التقرير",
            ["Export Word"] = "تصدير Word",
            ["Open file location"] = "فتح موقع الملف",
            ["✦ Intelligence"] = "✦ التحليل",
            ["▶ Play"] = "▶ تشغيل",
            ["Open transcript"] = "فتح النص",
            ["Optional. If left blank, date/time stays primary and a suggested title is created after transcription."] =
                "اختياري. إذا تُرك فارغًا يبقى التاريخ والوقت الاسم الأساسي ويُنشأ اسم مقترح بعد التفريغ.",
            ["Click anywhere on the circle to start, or Stop & Save while recording."] =
                "اضغط في أي مكان داخل الدائرة للبدء، أو استخدم إيقاف وحفظ أثناء التسجيل.",
            ["Pause private/off-record discussion. Press again to resume."] =
                "أوقف التسجيل مؤقتًا للمحادثات الخاصة، واضغط مرة أخرى للاستئناف.",
            ["Edit meeting title"] = "تعديل اسم الاجتماع",
            ["Start now or move this queued transcript to the front. Active transcripts remain visible but cannot be duplicated."] =
                "ابدأ الآن أو انقل التفريغ المنتظر إلى المقدمة. يبقى العمل النشط ظاهرًا ولا يمكن تكراره.",
            ["Search meeting titles, suggested titles, notes, and transcript text."] =
                "ابحث في أسماء الاجتماعات والأسماء المقترحة والملاحظات ونصوص التفريغ.",
            ["Start now or prioritize a queued transcript. Active work stays visible instead of disappearing."] =
                "ابدأ الآن أو أعطِ أولوية لتفريغ منتظر. يبقى العمل النشط ظاهرًا بدل أن يختفي.",
            ["Search or ask about any meeting, topic, decision, action or phrase."] =
                "ابحث أو اسأل عن أي اجتماع أو موضوع أو قرار أو إجراء أو عبارة.",
            ["Minimize"] = "تصغير",
            ["Maximize / Restore"] = "تكبير / استعادة",
            ["Close"] = "إغلاق",
        };

    private static readonly IReadOnlyDictionary<string, string> EnglishUi =
        ArabicUi.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    private void ApplyLanguage(string? requested)
    {
        var arabic = string.Equals(requested, "Arabic", StringComparison.OrdinalIgnoreCase);
        var map = arabic ? ArabicUi : EnglishUi;

        TranslateVisualTree(this, map);
        TranslateComboItems(SettingsAppearance, arabic);
        TranslateComboItems(SettingsPreferredLanguage, arabic);
        TranslateComboItems(SettingsIntelligenceProvider, arabic);
        SettingsCloudEnabled.Content = arabic ? ArabicUi["Enable cloud acceleration"] : "Enable cloud acceleration";
        SettingsCloudFallback.Content = arabic ? ArabicUi["Use Local AI if the selected cloud provider is unavailable"] : "Use Local AI if the selected cloud provider is unavailable";
        LocalizeCanonicalComboItems();
        ApplyLanguageSensitiveInputDirection(this);

        FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Language = XmlLanguage.GetLanguage(arabic ? "ar-SA" : "en-US");
        AppearanceService.SetLanguage(arabic ? "Arabic" : "English");
        AppearanceService.Apply(this);
        ApplyRecentVisibility();
    }

    private void TranslateComboItems(ComboBox box, bool arabic)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            var canonical = item.Tag?.ToString() ?? item.Content?.ToString() ?? "";
            var display = ReferenceEquals(box, SettingsIntelligenceProvider) ? canonical switch
            {
                "Local" => "Local (default)",
                "Gemini" => "Google Gemini",
                _ => canonical
            } : canonical;
            item.Content = arabic && ArabicUi.TryGetValue(display, out var translated) ? translated : display;
        }
    }


    private void LocalizeCanonicalComboItems()
    {
        foreach (var box in new[] { SettingsAppearance, SettingsPreferredLanguage })
        {
            if (box is null) continue;
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                var canonical = item.Tag?.ToString();
                if (string.IsNullOrWhiteSpace(canonical)) continue;
                item.Content = IsArabicLanguage && ArabicUi.TryGetValue(canonical, out var ar) ? ar : canonical;
            }
        }
    }

    private void ApplyLanguageSensitiveInputDirection(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox box)
            {
                var languageId = ReferenceEquals(box, SettingsCloudModel) ? FlowDirection.LeftToRight : (IsArabicLanguage ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
                box.FlowDirection = languageId;
                box.TextAlignment = languageId == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left;
                box.CaretBrush = (Brush)FindResource("PrimaryTextBrush");
            }
            if (child is PasswordBox passwordBox)
                passwordBox.FlowDirection = FlowDirection.LeftToRight;
            ApplyLanguageSensitiveInputDirection(child);
        }
    }

    private string LocalizedThemeName(string canonical) =>
        IsArabicLanguage && ArabicUi.TryGetValue(canonical, out var ar) ? ar : canonical;

    private string Ui(string english, string arabic) => IsArabicLanguage ? arabic : english;

    private static void TranslateVisualTree(DependencyObject root, IReadOnlyDictionary<string, string> map)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBlock text && map.TryGetValue(text.Text, out var translatedText))
                text.Text = translatedText;
            else if (child is Button button && button.Content is string content && map.TryGetValue(content, out var translatedContent))
                button.Content = translatedContent;

            if (child is FrameworkElement element && element.ToolTip is string tip && map.TryGetValue(tip, out var translatedTip))
                element.ToolTip = translatedTip;

            TranslateVisualTree(child, map);
        }
    }

    private void ResumeQuietBackgroundQueue()
    {
        foreach (var meeting in _repo.Search(null, null, 1000))
        {
            if (string.IsNullOrWhiteSpace(meeting.AudioPath) ||
                !File.Exists(meeting.AudioPath))
                continue;

            if (!string.IsNullOrWhiteSpace(meeting.TranscriptPath) &&
                File.Exists(meeting.TranscriptPath))
                continue;

            var status = meeting.TranscriptionStatus ?? "";

            var resumable =
                status.Contains("Queued", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("interrupted", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("recording has priority", StringComparison.OrdinalIgnoreCase);

            if (!resumable)
                continue;

            if (meeting.DurationSeconds <
                Math.Max(1, _settings.AutoTranscribeMinimumSeconds))
                continue;

            QueueTranscript(
                meeting,
                forceMixedLanguageRecovery: false);
        }
    }

    private void UpdateClockAndStatus()
    {
        var now = DateTimeOffset.Now;
        if (IsArabicLanguage)
        {
            var ar = CultureInfo.GetCultureInfo("ar-SA");
            HeaderClock.Text =
                $"{now.ToString("dddd، dd MMMM yyyy", ar)}  •  {now.ToString("hh:mm tt", ar)}";
            GreetingText.Text = $"{(now.Hour < 12 ? "صباح الخير" : "مساء الخير")}، {EffectiveGreetingName()}";
        }
        else
        {
            HeaderClock.Text = $"{now:dddd, dd MMMM yyyy}  •  {now:hh:mm tt}";
            GreetingText.Text = GreetingService.GetGreeting(now, EffectiveGreetingName());
        }

        if (now - _lastAudioDeviceProbe >= TimeSpan.FromSeconds(2.5))
        {
            _lastAudioDeviceProbe = now;
            if (_audioMeter.RefreshIfDeviceChanged())
                RefreshStatusCards();
        }

        if (_recording.IsRecording && _recordStarted.HasValue)
        {
            var elapsed = GetRecordedElapsed(now);
            TimerText.Text = elapsed.ToString(@"hh\:mm\:ss");
            WaveCurrentText.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        }
        else
        {
            TimerText.Text = "00:00:00";
            WaveCurrentText.Text = "00:00";
        }

        if (TranscriptionProgressService.HasActive &&
            now - _lastTranscriptUiRefresh >= TimeSpan.FromSeconds(1))
        {
            _lastTranscriptUiRefresh = now;
            RecentList.Items.Refresh();
            LibraryList.Items.Refresh();
        }

        RefreshStatusCards();
    }

    private TimeSpan GetRecordedElapsed(DateTimeOffset now)
    {
        if (!_recordStarted.HasValue) return TimeSpan.Zero;
        var pausedNow = _pauseStarted.HasValue ? now - _pauseStarted.Value : TimeSpan.Zero;
        var elapsed = now - _recordStarted.Value - _pausedTotal - pausedNow;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private void UpdateLiveVisuals()
    {
        _pulsePhase += 0.42;

        if (_recording.IsRecording && !_recording.IsPaused)
        {
            var sample = Math.Clamp(Math.Max(_latestMicLevel, _latestSystemLevel), 0, 1);
            _waveSamples.Add(sample);

            if (_waveSamples.Count > 2400)
            {
                var compressed = new List<double>(_waveSamples.Count / 2 + 1);
                for (var i = 0; i < _waveSamples.Count; i += 2)
                {
                    if (i + 1 < _waveSamples.Count)
                        compressed.Add(Math.Max(_waveSamples[i], _waveSamples[i + 1]));
                    else
                        compressed.Add(_waveSamples[i]);
                }
                _waveSamples.Clear();
                _waveSamples.AddRange(compressed);
            }

            RecordGlowEllipse.Opacity = 0.22 + 0.10 * ((Math.Sin(_pulsePhase) + 1) / 2);
        }
        else if (_recording.IsPaused)
        {
            RecordGlowEllipse.Opacity = 0.16;
        }
        else
        {
            RecordGlowEllipse.Opacity = 0.20 + 0.05 * ((Math.Sin(_pulsePhase * 0.45) + 1) / 2);
        }

        UpdateWaveform();
    }

    private void UpdateWaveform()
    {
        var width = WaveformCanvas.ActualWidth;
        var height = WaveformCanvas.ActualHeight;
        if (width < 20 || height < 20) return;

        var mid = height / 2;
        WaveformMidLine.X1 = 0;
        WaveformMidLine.X2 = width;
        WaveformMidLine.Y1 = mid;
        WaveformMidLine.Y2 = mid;

        var points = new PointCollection();
        if (_waveSamples.Count == 0)
        {
            points.Add(new Point(0, mid));
            points.Add(new Point(width, mid));
        }
        else
        {
            var target = Math.Max(40, Math.Min(320, (int)(width / 3)));
            var bucketSize = Math.Max(1.0, _waveSamples.Count / (double)target);
            var rendered = new List<double>();

            for (var bucket = 0; bucket < target; bucket++)
            {
                var start = (int)Math.Floor(bucket * bucketSize);
                if (start >= _waveSamples.Count) break;
                var end = Math.Min(_waveSamples.Count, (int)Math.Ceiling((bucket + 1) * bucketSize));
                var peak = 0.0;
                for (var i = start; i < end; i++)
                    peak = Math.Max(peak, _waveSamples[i]);
                rendered.Add(peak);
            }

            for (var i = 0; i < rendered.Count; i++)
            {
                var x = rendered.Count == 1 ? 0 : i * width / (rendered.Count - 1);
                var amplitude = 2.0 + Math.Pow(rendered[i], 0.65) * height * 0.39;
                var direction = i % 2 == 0 ? -1 : 1;
                points.Add(new Point(x, mid + direction * amplitude));
            }
        }

        WaveformLine.Points = points;
        UpdateMarkerCanvas(width, height);
    }

    private void UpdateMarkerCanvas(double width, double height)
    {
        MarkerCanvas.Width = width;
        MarkerCanvas.Height = height;
        MarkerCanvas.Children.Clear();

        if (_marks.Count == 0 || !_recordStarted.HasValue) return;

        var totalSeconds = Math.Max(1, GetRecordedElapsed(DateTimeOffset.Now).TotalSeconds);

        foreach (var mark in _marks)
        {
            var x = Math.Clamp(mark.OffsetSeconds / totalSeconds, 0, 1) * width;

            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 8,
                Y2 = height - 4,
                Stroke = (Brush)FindResource("RecordingAccentBrush"),
                StrokeThickness = 1.2,
                Opacity = 0.75
            };
            MarkerCanvas.Children.Add(line);

            var pin = new Border
            {
                Background = (Brush)FindResource("RecordingAccentBrush"),
                BorderBrush = (Brush)FindResource("FocusBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Width = 16,
                Height = 16,
                ToolTip = $"Important moment • {TimeSpan.FromSeconds(mark.OffsetSeconds):hh\\:mm\\:ss}"
            };
            pin.Child = new TextBlock
            {
                Text = "•",
                Foreground = (Brush)FindResource("PrimaryActionTextBrush"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            Canvas.SetLeft(pin, Math.Clamp(x - 8, 0, Math.Max(0, width - 16)));
            Canvas.SetTop(pin, 0);
            MarkerCanvas.Children.Add(pin);

            var label = new Border
            {
                Background = (Brush)FindResource("SurfaceBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 2, 5, 2),
                IsHitTestVisible = false
            };
            label.Child = new TextBlock
            {
                Text = TimeSpan.FromSeconds(mark.OffsetSeconds).ToString(@"mm\:ss"),
                Foreground = (Brush)FindResource("AccentTextBrush"),
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold
            };

            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelWidth = label.DesiredSize.Width;
            var labelY = (_marks.IndexOf(mark) % 2 == 0) ? 20 : 38;
            Canvas.SetLeft(label, Math.Clamp(x - labelWidth / 2, 0, Math.Max(0, width - labelWidth)));
            Canvas.SetTop(label, labelY);
            MarkerCanvas.Children.Add(label);
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        RecordHitButton.IsEnabled = false;

        try
        {
            if (!_recording.IsRecording)
            {
                RecordStateText.Text = IsArabicLanguage ? "جارٍ التجهيز…" : "PREPARING…";
                RecordStateText.Foreground = (Brush)FindResource("BlueBrush");
                RecordLabel.Text = IsArabicLanguage ? "يرجى الانتظار" : "PLEASE WAIT";
                RecordSubText.Text = IsArabicLanguage ? "جارٍ تجهيز الصوت المحلي…" : "Preparing native audio…";
                HintText.Text = IsArabicLanguage ? "جارٍ فحص الميكروفون وصوت النظام. سيبدأ التسجيل تلقائيًا." : "Checking microphone and system audio. Recording will start automatically.";

                // Refresh audio endpoints immediately in case a USB microphone was just connected.
                _audioMeter.RefreshIfDeviceChanged();
                RefreshStatusCards();

                _marks.Clear();
                _waveSamples.Clear();
                MarkerCanvas.Children.Clear();
                _pausedTotal = TimeSpan.Zero;
                _pauseStarted = null;

                var requestedName = MeetingNameBox.Text.Trim();

                // The live meeting always wins over background AI work.
                // Any active transcript is canceled safely and re-queued for quiet time.
                _transcription.YieldToRecording();

                await _recording.StartAsync(requestedName, CancellationToken.None);

                _recordStarted = DateTimeOffset.Now;
                MeetingNameBox.IsReadOnly = true;
                NameCommitText.Text = string.IsNullOrWhiteSpace(requestedName)
                    ? (IsArabicLanguage ? "التاريخ والوقت هما الاسم الأساسي • سيظهر اسم مقترح بعد التفريغ" : "Date/time is the primary title • suggested title will appear after transcription")
                    : (IsArabicLanguage ? "✓ تم حفظ اسم الاجتماع مع هذا التسجيل" : "✓ Meeting name saved with this recording");

                SetRecordingVisualState();
            }
            else
            {
                RecordStateText.Text = IsArabicLanguage ? "جارٍ الحفظ…" : "SAVING…";
                RecordSubText.Text = IsArabicLanguage ? "جارٍ إنهاء الصوت بأمان" : "Finalizing audio safely";
                HintText.Text = IsArabicLanguage ? "جارٍ حفظ الاجتماع بأمان…" : "Saving the meeting safely…";

                var meeting = await _recording.StopAsync(_marks, CancellationToken.None);

                ResetRecorderUi();
                RefreshAll();

                if (meeting.DurationSeconds < Math.Max(1, _settings.AutoTranscribeMinimumSeconds))
                {
                    meeting.TranscriptionStatus = "Saved • transcript on demand";
                    _repo.Upsert(meeting);
                    MeetingMetadataService.Write(meeting);
                    RefreshAll();
                    RefreshStatusCards();
                }
                else
                {
                    QueueTranscript(meeting, forceMixedLanguageRecovery: false);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Archestro Meeting Vault", MessageBoxButton.OK, MessageBoxImage.Warning);

            if (!_recording.IsRecording)
                ResetRecorderUi();
        }
        finally
        {
            RecordHitButton.IsEnabled = true;
        }
    }

    private void SetRecordingVisualState()
    {
        RecordCircle.Stroke = (Brush)FindResource("RecordingAccentBrush");
        RecordGlowEllipse.Fill = CreateGlowBrush(((SolidColorBrush)FindResource("RecordingAccentBrush")).Color, 82);
        RecordStateText.Text = IsArabicLanguage ? "● جارٍ التسجيل" : "● RECORDING";
        RecordStateText.Foreground = (Brush)FindResource("RecordingAccentBrush");
        RecordLabel.Text = IsArabicLanguage ? "إيقاف وحفظ" : "STOP & SAVE";
        RecordSubText.Text = IsArabicLanguage ? "اضغط على الدائرة للإيقاف والحفظ" : "Tap the circle to stop & save";
        PauseButton.Visibility = Visibility.Visible;
        PauseButton.Content = IsArabicLanguage ? "⏸  إيقاف مؤقت" : "⏸  PAUSE";
        MarkButton.IsEnabled = true;
        WaveformPauseOverlay.Visibility = Visibility.Collapsed;
        TimelinePauseStatus.Visibility = Visibility.Collapsed;
        HintText.Text = IsArabicLanguage ? "التسجيل مباشر. علّم هذه اللحظة لحفظ النقطة الدقيقة فقط." : "Recording is live. MARK THIS MOMENT saves only the exact point.";
    }

    private void SetPausedVisualState()
    {
        RecordCircle.Stroke = (Brush)FindResource("WarningAccentBrush");
        RecordGlowEllipse.Fill = CreateGlowBrush(((SolidColorBrush)FindResource("WarningAccentBrush")).Color, 74);
        RecordStateText.Text = IsArabicLanguage ? "Ⅱ التسجيل متوقف مؤقتًا" : "Ⅱ RECORDING PAUSED";
        RecordStateText.Foreground = (Brush)FindResource("WarningAccentBrush");
        RecordLabel.Text = IsArabicLanguage ? "متوقف مؤقتًا" : "PAUSED";
        RecordSubText.Text = IsArabicLanguage ? "هذا الجزء لا يتم تسجيله" : "This section is not being recorded";
        PauseButton.Content = IsArabicLanguage ? "▶  استئناف" : "▶  RESUME";
        MarkButton.IsEnabled = false;
        WaveformPauseOverlay.Visibility = Visibility.Visible;
        TimelinePauseStatus.Visibility = Visibility.Visible;
        HintText.Text = IsArabicLanguage ? "التسجيل متوقف مؤقتًا. اضغط استئناف عند انتهاء المحادثة الخاصة." : "Recording is paused. Press RESUME when the private discussion ends.";
    }

    private void ResetRecorderUi()
    {
        _recordStarted = null;
        _pauseStarted = null;
        _pausedTotal = TimeSpan.Zero;

        MeetingNameBox.IsReadOnly = false;
        MeetingNameBox.Text = "";

        RecordCircle.Stroke = (Brush)FindResource("RecorderRingBrush");
        RecordGlowEllipse.Fill = CreateGlowBrush(((SolidColorBrush)FindResource("RecorderRingBrush")).Color, 72);
        RecordStateText.Text = IsArabicLanguage ? "جاهز للتسجيل" : "READY TO RECORD";
        RecordStateText.Foreground = (Brush)FindResource("BlueBrush");
        RecordLabel.Text = IsArabicLanguage ? "بدء الاجتماع" : "START MEETING";
        RecordSubText.Text = IsArabicLanguage ? "اضغط على الدائرة للبدء" : "Tap the circle to start";
        PauseButton.Visibility = Visibility.Collapsed;
        PauseButton.Content = IsArabicLanguage ? "⏸  إيقاف مؤقت" : "⏸  PAUSE";
        MarkButton.IsEnabled = false;
        WaveformPauseOverlay.Visibility = Visibility.Collapsed;
        TimelinePauseStatus.Visibility = Visibility.Collapsed;
        HintText.Text = IsArabicLanguage ? "جاهز عند بدء الاجتماع." : "Ready when the meeting starts.";

        TimerText.Text = "00:00:00";
        WaveCurrentText.Text = "00:00";
        _waveSamples.Clear();
        _marks.Clear();
        WaveformLine.Points = new PointCollection();
        MarkerCanvas.Children.Clear();

        UpdateNameState();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording.IsRecording) return;

        PauseButton.IsEnabled = false;
        try
        {
            if (!_recording.IsPaused)
            {
                await _recording.PauseAsync(CancellationToken.None);
                _pauseStarted = DateTimeOffset.Now;
                SetPausedVisualState();
            }
            else
            {
                await _recording.ResumeAsync(CancellationToken.None);
                if (_pauseStarted.HasValue)
                    _pausedTotal += DateTimeOffset.Now - _pauseStarted.Value;

                _pauseStarted = null;
                SetRecordingVisualState();
                HintText.Text = IsArabicLanguage ? "تم استئناف التسجيل." : "Recording resumed.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Pause / Resume", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PauseButton.IsEnabled = true;
        }
    }

    private void Mark_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording.IsRecording || _recording.IsPaused || !_recordStarted.HasValue) return;

        var offset = (int)Math.Max(0, GetRecordedElapsed(DateTimeOffset.Now).TotalSeconds);
        _marks.Add(new ImportantMark
        {
            OffsetSeconds = offset,
            CreatedLocal = DateTimeOffset.Now
        });

        UpdateWaveform();
        HintText.Text = $"Marked this moment at {TimeSpan.FromSeconds(offset):hh\\:mm\\:ss}";
    }

    private void MeetingNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateNameState();
    }

    private void MeetingNameBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (MeetingNamePlaceholder is not null)
            MeetingNamePlaceholder.Visibility = Visibility.Collapsed;
    }

    private void MeetingNameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateNameState();
    }

    private void UpdateNameState()
    {
        if (MeetingNamePlaceholder is not null)
            MeetingNamePlaceholder.Visibility =
                string.IsNullOrWhiteSpace(MeetingNameBox.Text) &&
                !MeetingNameBox.IsKeyboardFocusWithin
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        if (NameCommitText is null) return;

        if (_recording.IsRecording) return;

        NameCommitText.Text = string.IsNullOrWhiteSpace(MeetingNameBox.Text)
            ? (IsArabicLanguage
                ? "اختياري • يتم اعتماد الاسم تلقائيًا عند بدء التسجيل"
                : "Optional • name is committed automatically when recording starts")
            : (IsArabicLanguage
                ? "✓ جاهز • سيتم حفظ هذا الاسم عند بدء التسجيل"
                : "✓ Ready • this name will be saved when recording starts");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        if (!IsLoaded) return;

        LibraryCountText.Text = IsArabicLanguage ? "جارٍ البحث…" : "Searching…";
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
    }

    private void UpdateSearchPlaceholder()
    {
        if (SearchPlaceholder is null || SearchBox is null) return;
        SearchPlaceholder.Visibility =
            string.IsNullOrWhiteSpace(SearchBox.Text) &&
            !SearchBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;
    }


    private void LocalizedDynamicContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject root) return;
        LocalizeDynamicVisualTree(root);
    }

    private void LocalizeDynamicVisualTree(DependencyObject root)
    {
        if (root is Button button && button.Content is string content && ArabicUi.TryGetValue(content, out var arButton))
            button.Content = IsArabicLanguage ? arButton : content;
        if (root is FrameworkElement element && element.ToolTip is string tip && ArabicUi.TryGetValue(tip, out var arTip))
            element.ToolTip = IsArabicLanguage ? arTip : tip;
        if (root is TextBlock text && !BindingOperations.IsDataBound(text, TextBlock.TextProperty) && ArabicUi.TryGetValue(text.Text ?? string.Empty, out var arText))
            text.Text = IsArabicLanguage ? arText : text.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            LocalizeDynamicVisualTree(VisualTreeHelper.GetChild(root, i));
    }
    private void RefreshAll()
    {
        RefreshRecent();
        RefreshCategories();
        RefreshLibrary();
        if (IsArabicLanguage)
            ApplyLanguage(_settings.PreferredLanguage);
    }

    private void RefreshRecent()
    {
        RecentList.ItemsSource = _repo.Recent(5);
    }

    private void RefreshLibrary()
    {
        var results = _repo.Search(SearchBox.Text, _selectedCategory);
        LibraryList.ItemsSource = results;
        LibraryCountText.Text = IsArabicLanguage
            ? $"عدد الاجتماعات: {results.Count}"
            : $"{results.Count} meeting{(results.Count == 1 ? "" : "s")}";
    }

    private async Task RefreshLibraryAsync()
    {
        _searchRefreshCts?.Cancel();
        _searchRefreshCts?.Dispose();
        _searchRefreshCts = new CancellationTokenSource();

        var token = _searchRefreshCts.Token;
        var query = SearchBox.Text;
        var category = _selectedCategory;

        try
        {
            var results = await Task.Run(
                () => _repo.Search(query, category),
                token);

            if (token.IsCancellationRequested)
                return;

            LibraryList.ItemsSource = results;
            LibraryCountText.Text = IsArabicLanguage
                ? $"عدد الاجتماعات: {results.Count}"
                : $"{results.Count} meeting{(results.Count == 1 ? "" : "s")}";
        }
        catch (OperationCanceledException)
        {
            // A newer query superseded this one.
        }
    }

    private void ScheduleSearchPrewarm()
    {
        _ = Task.Run(() =>
        {
            try
            {
                _repo.Search(null, null, 5);
            }
            catch
            {
                // Search remains available; the normal path will surface real errors.
            }
        });

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_libraryVisualPrewarmed)
                return;

            LibraryView.Visibility = Visibility.Hidden;
            LibraryView.UpdateLayout();
            _libraryVisualPrewarmed = true;
        }), DispatcherPriority.ApplicationIdle);
    }

    private void RefreshCategories()
    {
        var categories = _repo.GetCategories()
            .Where(x => !x.Name.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase) || x.MeetingCount > 0)
            .ToList();

        BuildCategoryChips(HomeCategoryPanel, categories, 10, includeAll: false, openLibraryOnClick: true);
        BuildCategoryChips(LibraryCategoryPanel, categories, 12, includeAll: true, openLibraryOnClick: false);
        RefreshCategoryManager();
    }

    private void BuildCategoryChips(
        Panel panel,
        IReadOnlyList<CategoryRecord> categories,
        int maxVisible,
        bool includeAll,
        bool openLibraryOnClick)
    {
        panel.Children.Clear();

        if (includeAll)
            panel.Children.Add(CreateCategoryButton("All", "#2F80ED", openLibraryOnClick, active: _selectedCategory == "All"));

        var visible = categories
            .OrderByDescending(x => x.Pinned)
            .ThenBy(x => x.SortOrder)
            .Take(maxVisible)
            .ToList();
        foreach (var category in visible)
        {
            var active = _selectedCategory.Equals(category.Name, StringComparison.OrdinalIgnoreCase);
            var button = CreateCategoryButton(category.Name, category.Color, openLibraryOnClick, active);
            button.ToolTip = IsArabicLanguage
                ? (category.MeetingCount == 1 ? "اجتماع واحد" : $"{category.MeetingCount} اجتماعات")
                : (category.MeetingCount == 1 ? "1 meeting" : $"{category.MeetingCount} meetings");
            panel.Children.Add(button);
        }

        var remaining = categories
            .Where(c => visible.All(v => !v.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (remaining.Count > 0)
        {
            var more = new Button
            {
                Content = IsArabicLanguage ? "المزيد ▾" : "More ▾",
                Style = (Style)FindResource("ChipButton")
            };
            more.Click += (_, _) =>
            {
                var menu = new ContextMenu();
                foreach (var category in remaining)
                {
                    var item = new MenuItem
                    {
                        Header = category.MeetingCount > 0
                            ? $"{(IsArabicLanguage && ArabicUi.TryGetValue(category.Name, out var arName) ? arName : category.Name)}  ({category.MeetingCount})"
                            : (IsArabicLanguage && ArabicUi.TryGetValue(category.Name, out var arNameOnly) ? arNameOnly : category.Name),
                        Tag = category.Name
                    };
                    item.Click += CategoryMenuItem_Click;
                    menu.Items.Add(item);
                }
                menu.PlacementTarget = more;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            panel.Children.Add(more);
        }
    }

    private Button CreateCategoryButton(string name, string color, bool openLibraryOnClick, bool active)
    {
        var button = new Button
        {
            Content = IsArabicLanguage && ArabicUi.TryGetValue(name, out var localizedCategory) ? localizedCategory : name,
            Tag = name,
            Style = (Style)FindResource("ChipButton")
        };

        if (active)
        {
            button.Background = (Brush)FindResource("PrimaryActionBrush");
            button.BorderBrush = (Brush)FindResource("PrimaryActionBorderBrush");
            button.Foreground = (Brush)FindResource("PrimaryActionTextBrush");
        }
        else
        {
            button.BorderBrush = BrushFromHex(color);
        }

        if (openLibraryOnClick)
            button.Click += HomeCategoryChip_Click;
        else
            button.Click += LibraryCategoryChip_Click;
        return button;
    }

    private void HomeCategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string category) return;
        _selectedCategory = category;
        ShowLibrary();
        RefreshCategories();
        RefreshLibrary();
    }

    private void LibraryCategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string category) return;
        _selectedCategory = category;
        RefreshCategories();
        RefreshLibrary();
    }

    private void CategoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string category) return;
        _selectedCategory = category;
        ShowLibrary();
        RefreshCategories();
        RefreshLibrary();
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextPromptWindow("Add Category", "Category name", "") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var name = dlg.Result.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (!_repo.AddCategory(name, CategoryCatalog.ColorFromName(name)))
        {
            MessageBox.Show("This category already exists.", "Categories", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedCategory = name;
        RefreshCategories();
        RefreshLibrary();
        ShowCategories();
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private void OpenSearch_Click(object sender, RoutedEventArgs e)
    {
        // Search navigation must render immediately. Do not perform synchronous DB refresh
        // before the view becomes visible; the library is already populated by RefreshAll().
        HomeView.Visibility = Visibility.Collapsed;
        CategoriesView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        IntelligenceView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        SetActiveNav("Search");

        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!SearchBox.IsKeyboardFocusWithin)
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }
        }), DispatcherPriority.Input);
    }

    private void OpenPeople_Click(object sender, RoutedEventArgs e)
    {
        if (_peopleWindow is not null)
        {
            if (_peopleWindow.WindowState == WindowState.Minimized)
                _peopleWindow.WindowState = WindowState.Normal;
            _peopleWindow.Show();
            _peopleWindow.Activate();
            return;
        }

        var window = new SpeakerProfilesWindow(_speakerIntelligence);
        _peopleWindow = window;
        SetActiveNav("People");

        window.Closed += (_, _) =>
        {
            _peopleWindow = null;
            RefreshAll();
            if (PeopleNavButton.Tag as string == "Active")
                SetActiveNav("Home");
        };

        window.Show();
        window.Activate();
    }

    private void OpenVaultIntelligence_Click(object sender, RoutedEventArgs e)
    {
        HomeView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Hidden;
        CategoriesView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        IntelligenceView.Visibility = Visibility.Visible;
        SetActiveNav("Intelligence");

        EnsureVaultWelcomeMessage();
        VaultIntelligenceStatusText.Text = IsArabicLanguage
            ? "ابحث عن كلمة محددة أو اسأل سؤالًا عبر أرشيف اجتماعاتك المحلي."
            : "Search an exact phrase or ask a question across your local meeting archive.";
        UpdateVaultQueryDirection();
        VaultIntelligenceQueryBox.Focus();
        Keyboard.Focus(VaultIntelligenceQueryBox);
    }


    private void SetVaultBusy(bool busy, string status)
    {
        _vaultAskBusy = busy;
        if (VaultBusyPulse is not null) VaultBusyPulse.Opacity = busy ? 0.45 : 0;
        if (VaultIntelligenceStatusText is not null && !string.IsNullOrWhiteSpace(status)) VaultIntelligenceStatusText.Text = status;
        if (busy)
        {
            _vaultBusyStarted ??= DateTimeOffset.Now;
            _vaultBusyTick = 0;
            if (VaultBusyElapsedText is not null)
                VaultBusyElapsedText.Visibility = Visibility.Visible;
            UpdateVaultBusyElapsed();
            _vaultBusyTimer.Start();
        }
        else
        {
            _vaultBusyTimer.Stop();
            _vaultBusyStarted = null;
            if (VaultBusyPulse is not null) VaultBusyPulse.Opacity = 0;
            if (VaultBusyElapsedText is not null)
            {
                VaultBusyElapsedText.Text = string.Empty;
                VaultBusyElapsedText.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void AdvanceVaultBusyPulse()
    {
        if (!_vaultAskBusy || VaultBusyPulse is null) return;
        _vaultBusyTick++;
        VaultBusyPulse.Opacity = (_vaultBusyTick % 2 == 0) ? 0.95 : 0.35;
        UpdateVaultBusyElapsed();
    }

    private void UpdateVaultBusyElapsed()
    {
        if (!_vaultAskBusy || _vaultBusyStarted is null || VaultBusyElapsedText is null) return;
        var elapsed = DateTimeOffset.Now - _vaultBusyStarted.Value;
        var elapsedText = elapsed.ToString(@"mm\:ss");
        VaultBusyElapsedText.Text = IsArabicLanguage
            ? $"المدة {elapsedText}"
            : $"Elapsed {elapsedText}";
    }

    private void VaultIntelligenceQueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateVaultQueryDirection();
    }

    private void UpdateVaultQueryDirection()
    {
        if (VaultIntelligenceQueryBox is null) return;
        var text = VaultIntelligenceQueryBox.Text ?? string.Empty;
        var firstStrong = text.FirstOrDefault(ch => char.IsLetter(ch));
        var arabic = firstStrong != default && firstStrong >= '\u0600' && firstStrong <= '\u06FF';
        if (firstStrong == default) arabic = IsArabicLanguage;

        var caret = VaultIntelligenceQueryBox.CaretIndex;
        VaultIntelligenceQueryBox.FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        VaultIntelligenceQueryBox.TextAlignment = arabic ? TextAlignment.Right : TextAlignment.Left;
        VaultIntelligenceQueryBox.CaretIndex = Math.Clamp(caret, 0, VaultIntelligenceQueryBox.Text.Length);
    }

    private void VaultIntelligenceQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;

        e.Handled = true;
        VaultAsk_Click(sender, new RoutedEventArgs());
    }

    private void EnsureVaultWelcomeMessage()
    {
        if (_vaultMessages.Count > 0)
            return;

        AddVaultMessage(
            IsArabicLanguage ? "Archestro" : "Archestro",
            IsArabicLanguage
                ? "اسأل عن أي اجتماع، كلمة، قرار أو موضوع. سأربط الإجابة بالاجتماع والمتحدث واللحظة التي جاءت منها."
                : "Ask about any meeting, phrase, decision or topic. I’ll link the answer back to the meeting, speaker and moment it came from.",
            IsArabicLanguage
                ? "خاص • محلي • مرتبط بالدليل"
                : "Private • local • evidence-linked",
            user: false);
    }

    private static bool ContainsArabicScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Any(ch =>
            (ch >= '\u0600' && ch <= '\u06FF') ||
            (ch >= '\u0750' && ch <= '\u077F') ||
            (ch >= '\u08A0' && ch <= '\u08FF'));
    }

    private static string NormalizeVaultIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var value = text.Trim().ToLowerInvariant()
            .Replace("؟", "?")
            .Replace("أ", "ا")
            .Replace("إ", "ا")
            .Replace("آ", "ا")
            .Replace("ى", "ي");
        return System.Text.RegularExpressions.Regex.Replace(value, @"[^\p{L}\p{N}\s?]", " ")
            .Replace("  ", " ")
            .Trim();
    }

    private bool TryBuildVaultIdentityAnswer(string question, out string answer)
    {
        var normalized = NormalizeVaultIntent(question).TrimEnd('?').Trim();
        var assistantIdentity = normalized is
            "من انت" or "من تكون" or "ما انت" or "عرف بنفسك" or "عرفني بنفسك" or
            "who are you" or "what are you" or "introduce yourself" or "tell me about yourself";
        if (assistantIdentity)
        {
            answer = ContainsArabicScript(question)
                ? "أنا Archestro Meeting Intelligence، مساعدك الذكي المحلي للاجتماعات من Archestro. أساعدك في البحث داخل ذاكرة اجتماعاتك، العثور على القرارات والإجراءات والمتحدثين والتواريخ واللحظات المرتبطة بالدليل، وتشغيل التسجيل أو فتح النص عند الموضع الصحيح. أعمل على بيانات اجتماعاتك المحلية، وأنا تحت أمرك في أي سؤال يتعلق بها."
                : "I’m Archestro Meeting Intelligence, Archestro’s private local meeting-memory assistant. I help you search your meetings, find evidence-linked decisions, actions, speakers, dates and moments, and jump back to the exact transcript or audio. I work with your local meeting archive and I’m ready to help with anything inside it.";
            return true;
        }

        var userIdentity = normalized is "من انا" or "من اكون" or "who am i";
        if (userIdentity)
        {
            var display = string.IsNullOrWhiteSpace(_settings.DisplayName) ? "this account" : _settings.DisplayName.Trim();
            answer = ContainsArabicScript(question)
                ? $"بحسب إعدادات Archestro، اسم العرض لهذا الحساب هو {display}. لا أخمّن هويتك من الصوت أو محتوى الاجتماعات؛ أعتمد فقط على المعلومات التي تحفظها أنت داخل البرنامج."
                : $"According to Archestro settings, the display name for this account is {display}. I don’t guess your identity from voice or meeting content; I use only information you explicitly save in the product.";
            return true;
        }

        answer = string.Empty;
        return false;
    }

    private void AddVaultMessage(string role, string body, string meta, bool user, IReadOnlyList<VaultEvidenceCard>? evidenceCards = null)
    {
        var arabicBody = ContainsArabicScript(body);
        _vaultMessages.Add(new VaultChatMessage
        {
            Role = role,
            Body = body,
            Meta = meta,
            IsUser = user,
            EvidenceCards = evidenceCards ?? Array.Empty<VaultEvidenceCard>(),
            Alignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BubbleMargin = user
                ? new Thickness(150, 6, 0, 6)
                : new Thickness(0, 6, 150, 6),
            FlowDirection = arabicBody ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            TextAlignment = arabicBody ? TextAlignment.Right : TextAlignment.Left,
            Language = XmlLanguage.GetLanguage(arabicBody ? "ar-SA" : "en-US")
        });

        Dispatcher.BeginInvoke(new Action(() =>
        {
            VaultConversationList?.UpdateLayout();
            VaultConversationScrollViewer?.ScrollToEnd();
        }), DispatcherPriority.Loaded);
    }


    private static IReadOnlyList<VaultEvidenceCard> BuildVaultEvidenceCards(IEnumerable<EvidenceRef> evidence)
    {
        return evidence
            .GroupBy(x => new { x.MeetingId, x.MeetingTitle, x.MeetingDateText, x.MeetingCategory })
            .Select(g => new VaultEvidenceCard
            {
                Header = BuildVaultEvidenceHeader(g.Key.MeetingTitle, g.Key.MeetingDateText, g.Key.MeetingCategory),
                Items = g.Take(8).ToList()
            })
            .ToList();
    }

    private static string BuildVaultEvidenceHeader(string title, string dateText, string category)
    {
        var parts = new List<string>();
        var cleanTitle = (title ?? string.Empty).Trim();
        var cleanDate = (dateText ?? string.Empty).Trim();
        var cleanCategory = (category ?? string.Empty).Trim();

        // Auto-generated meeting titles can duplicate the exact date/time shown beside them.
        // Keep one human-readable date instead of a technical-looking repeated header.
        var normalizedTitle = System.Text.RegularExpressions.Regex.Replace(cleanTitle, @"\s+", " ");
        var normalizedDate = System.Text.RegularExpressions.Regex.Replace(cleanDate, @"\s+", " ");
        var titleLooksGeneratedDate = !string.IsNullOrWhiteSpace(normalizedDate) &&
            normalizedTitle.Contains(normalizedDate, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(cleanTitle) && !titleLooksGeneratedDate)
            parts.Add(cleanTitle);
        if (!string.IsNullOrWhiteSpace(cleanDate))
            parts.Add(cleanDate);
        if (!string.IsNullOrWhiteSpace(cleanCategory) &&
            !cleanCategory.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
            parts.Add(cleanCategory);

        return parts.Count > 0 ? string.Join(" • ", parts) : "Meeting evidence";
    }

    private static string CleanVaultAnswerForPresentation(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return string.Empty;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(answer, @"\[(?:V|E)\d{4,6}\]\s*", string.Empty);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\[(?:\d{1,2}:)?\d{2}:\d{2}\]\s*", string.Empty);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[ \t]{2,}", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?:\r?\n){3,}", Environment.NewLine + Environment.NewLine);
        return cleaned.Trim();
    }

    private void VaultFocusMode_Click(object sender, RoutedEventArgs e)
    {
        _vaultFocusMode = !_vaultFocusMode;
        if (_vaultFocusMode)
        {
            _vaultPreviousWindowState = WindowState;
            _vaultPreviousMainMargin = MainContentShell?.Margin ?? new Thickness(24, 18, 24, 12);
            _vaultPreviousSidebarWidth = SidebarColumn?.Width ?? new GridLength(232);

            if (SidebarShell is not null) SidebarShell.Visibility = Visibility.Collapsed;
            if (SidebarColumn is not null) SidebarColumn.Width = new GridLength(0);
            if (MainHeaderShell is not null) MainHeaderShell.Visibility = Visibility.Collapsed;
            if (MainContentShell is not null)
            {
                Grid.SetColumn(MainContentShell, 0);
                Grid.SetColumnSpan(MainContentShell, 2);
                MainContentShell.Margin = new Thickness(16, 10, 16, 10);
            }
            if (VaultFocusModeButton is not null)
                VaultFocusModeButton.Content = IsArabicLanguage ? "↙  إنهاء التركيز" : "↙  Exit focus";
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (SidebarColumn is not null) SidebarColumn.Width = _vaultPreviousSidebarWidth;
            if (SidebarShell is not null) SidebarShell.Visibility = Visibility.Visible;
            if (MainHeaderShell is not null) MainHeaderShell.Visibility = Visibility.Visible;
            if (MainContentShell is not null)
            {
                Grid.SetColumn(MainContentShell, 1);
                Grid.SetColumnSpan(MainContentShell, 1);
                MainContentShell.Margin = _vaultPreviousMainMargin;
            }
            if (VaultFocusModeButton is not null)
                VaultFocusModeButton.Content = IsArabicLanguage ? "⛶  وضع التركيز" : "⛶  Focus mode";
            WindowState = _vaultPreviousWindowState;
        }
        Dispatcher.BeginInvoke(new Action(() => VaultConversationScrollViewer?.ScrollToEnd()), DispatcherPriority.Loaded);
    }

    private void VaultEvidencePlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EvidenceRef evidence }) return;
        var meeting = _repo.Get(evidence.MeetingId);
        if (meeting is null) return;
        var path = File.Exists(meeting.AudioPath) ? meeting.AudioPath : meeting.RecordingPath;
        if (!File.Exists(path)) return;
        try
        {
            var window = new AudioPreviewWindow(path, evidence.StartSeconds, meeting.PrimaryTitle, evidence.TimeText)
            {
                Owner = this
            };
            window.Show();
            window.Activate();
        }
        catch { }
    }

    private void VaultEvidenceOpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EvidenceRef evidence }) return;
        var meeting = _repo.Get(evidence.MeetingId);
        if (meeting is null || string.IsNullOrWhiteSpace(meeting.TranscriptPath) || !File.Exists(meeting.TranscriptPath)) return;

        var window = new TranscriptViewerWindow(meeting.TranscriptPath, meeting.PrimaryTitle, evidence.StartSeconds)
        {
            Owner = this
        };
        window.Show();
        window.Activate();
    }

    private void VaultEvidenceSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EvidenceRef evidence }) return;
        var meeting = _repo.Get(evidence.MeetingId);
        if (meeting is null) return;
        var window = new SpeakerTranscriptWindow(meeting, _speakerIntelligence) { Owner = this };
        window.Show();
        window.Activate();
    }

    private void VaultEvidenceMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EvidenceRef evidence }) return;
        var meeting = _repo.Get(evidence.MeetingId);
        if (meeting is null || string.IsNullOrWhiteSpace(meeting.FolderPath) || !Directory.Exists(meeting.FolderPath)) return;
        ProcessService.OpenPath(meeting.FolderPath);
    }

    private void VaultFindMentions_Click(object sender, RoutedEventArgs e)
    {
        var query = VaultIntelligenceQueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        EnsureVaultWelcomeMessage();
        AddVaultMessage(
            IsArabicLanguage ? "أنت" : "You",
            query,
            IsArabicLanguage ? "بحث مباشر" : "Find mentions",
            user: true);

        VaultIntelligenceQueryBox.Clear();

        var hits = _meetingIntelligence.FindVaultMentions(query, 80);
        if (hits.Count == 0)
        {
            AddVaultMessage(
                "Archestro",
                IsArabicLanguage
                    ? $"لم أجد ذكرًا مباشرًا أو مطابقًا بعد التطبيع لـ «{query}» في النصوص المحلية."
                    : $"I couldn’t find an exact or normalized mention of “{query}” in the local transcripts.",
                IsArabicLanguage ? "لا توجد مطابقة مباشرة" : "No direct match",
                user: false);
            VaultIntelligenceStatusText.Text = IsArabicLanguage
                ? "لم يتم العثور على ذكر مباشر."
                : "No direct mention found.";
            return;
        }

        var groups = hits
            .GroupBy(x => new { x.MeetingId, x.MeetingTitle, x.MeetingStartLocal, x.MeetingCategory })
            .OrderByDescending(g => g.Key.MeetingStartLocal)
            .ToList();

        var sb = new System.Text.StringBuilder();
        foreach (var group in groups)
        {
            var first = group.First();
            var title = group.Key.MeetingTitle;
            var date = group.Key.MeetingStartLocal == default
                ? ""
                : group.Key.MeetingStartLocal.ToString("dd MMM yyyy • hh:mm tt");
            sb.Append("• ").Append(title);
            if (!string.IsNullOrWhiteSpace(date))
                sb.Append(" — ").Append(date);
            if (!string.IsNullOrWhiteSpace(group.Key.MeetingCategory))
                sb.Append(" — ").Append(group.Key.MeetingCategory);
            sb.AppendLine();

            foreach (var hit in group.Take(5))
            {
                var text = hit.Text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 240) text = text[..240] + "…";

                sb.Append("  [").Append(hit.TimeText).Append("]");
                if (!string.IsNullOrWhiteSpace(hit.Speaker))
                    sb.Append(" ").Append(hit.Speaker);
                if (!string.IsNullOrWhiteSpace(hit.SpeakerMatchLabel))
                    sb.Append(" • ").Append(hit.SpeakerMatchLabel);
                sb.AppendLine();
                sb.Append("  ").AppendLine(text);
            }
            sb.AppendLine();
        }

        AddVaultMessage(
            "Archestro",
            IsArabicLanguage
                ? $"وجدت {hits.Count} موضع دليل في {groups.Count} اجتماعًا. النتائج مجمعة حسب الاجتماع أدناه."
                : $"Found {hits.Count} evidence match(es) across {groups.Count} meeting(s). Results are grouped by meeting below.",
            IsArabicLanguage ? "بحث مباشر • دليل مطابق" : "Find mentions • exact/normalized evidence",
            user: false,
            evidenceCards: BuildVaultEvidenceCards(hits));

        VaultIntelligenceStatusText.Text = IsArabicLanguage
            ? $"تم العثور على {hits.Count} موضعًا في {groups.Count} اجتماعًا."
            : $"Found {hits.Count} evidence match(es) across {groups.Count} meeting(s).";
    }

    private async void VaultAsk_Click(object sender, RoutedEventArgs e)
    {
        var question = VaultIntelligenceQueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(question) || _vaultAskBusy) return;

        var responseTimer = System.Diagnostics.Stopwatch.StartNew();
        EnsureVaultWelcomeMessage();
        AddVaultMessage(
            IsArabicLanguage ? "أنت" : "You",
            question,
            IsArabicLanguage ? "سؤال للأرشيف" : "Ask the Vault",
            user: true);

        VaultIntelligenceQueryBox.Clear();

        if (TryBuildVaultIdentityAnswer(question, out var identityAnswer))
        {
            AddVaultMessage(
                "Archestro",
                identityAnswer,
                ContainsArabicScript(question)
                    ? "هوية المنتج • محلي • خاص"
                    : "Product identity • local • private",
                user: false);
            VaultIntelligenceStatusText.Text = ContainsArabicScript(question)
                ? "جاهز لسؤال آخر من أرشيف اجتماعاتك."
                : "Ready for another question from your meeting archive.";
            return;
        }

        try
        {
            SetVaultBusy(true, IsArabicLanguage ? "أجهّز التحليل المحلي…" : "Preparing locally…");
            await Task.Delay(120);
            SetVaultBusy(true, IsArabicLanguage ? "أراجع أدلة الاجتماعات…" : "Reading meeting evidence…");
            var answer = await _meetingIntelligence.AskVaultAsync(question);
            SetVaultBusy(true, IsArabicLanguage ? "أبني الإجابة المرتبطة بالدليل…" : "Building evidence-grounded answer…");
            SetVaultBusy(true, IsArabicLanguage ? "أكتب الإجابة…" : "Writing response…");
            AddVaultMessage(
                "Archestro",
                CleanVaultAnswerForPresentation(answer.Answer),
                IsArabicLanguage
                    ? $"إجابة مرتبطة بأدلة الاجتماعات المحلية • وقت الرد {FormatVaultResponseTime(responseTimer.Elapsed)}"
                    : $"Evidence-grounded answer from your local Vault • response time {FormatVaultResponseTime(responseTimer.Elapsed)}",
                user: false,
                evidenceCards: BuildVaultEvidenceCards(answer.Evidence
                    .Select(id => answer.EvidenceIndex.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    .Where(x => x is not null)
                    .Cast<EvidenceRef>()
                    .ToList()));

            VaultIntelligenceStatusText.Text = IsArabicLanguage
                ? "الإجابة مرتبطة بأدلة اجتماعاتك المحلية."
                : "Answer grounded in local meeting evidence.";
        }
        catch (Exception ex)
        {
            AddVaultMessage(
                "Archestro",
                IsArabicLanguage
                    ? "تعذر إكمال السؤال الآن. لم يتم تعديل أي بيانات."
                    : "I couldn’t complete that question just now. No meeting data was changed.",
                IsArabicLanguage
                    ? $"{ex.Message} • بعد {FormatVaultResponseTime(responseTimer.Elapsed)}"
                    : $"{ex.Message} • after {FormatVaultResponseTime(responseTimer.Elapsed)}",
                user: false);
            VaultIntelligenceStatusText.Text = IsArabicLanguage
                ? "تعذر إكمال السؤال."
                : "Vault Intelligence could not complete this question.";
        }
        finally
        {
            SetVaultBusy(false, "");
        }
    }

    private static string FormatVaultResponseTime(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
            return elapsed.ToString(@"h\:mm\:ss");
        return elapsed.ToString(@"m\:ss");
    }

    private static string RenderVaultAnswer(AskAnswer answer, bool arabic)
    {
        var sb = new System.Text.StringBuilder();
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
        sb.AppendLine(arabic ? "الدليل:" : "Evidence:");

        foreach (var e in selected)
        {
            sb.Append("• ").Append(e.MeetingTitle);
            if (!string.IsNullOrWhiteSpace(e.MeetingDateText))
                sb.Append(" — ").Append(e.MeetingDateText);
            if (!string.IsNullOrWhiteSpace(e.MeetingCategory))
                sb.Append(" — ").Append(e.MeetingCategory);
            sb.AppendLine();

            sb.Append("  ").Append(e.TimeText);
            if (!string.IsNullOrWhiteSpace(e.Speaker))
                sb.Append(" • ").Append(e.Speaker);
            if (!string.IsNullOrWhiteSpace(e.SpeakerMatchLabel))
                sb.Append(" • ").Append(e.SpeakerMatchLabel);
            sb.AppendLine();

            var text = e.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 320) text = text[..320] + "…";
            sb.Append("  ").AppendLine(text);
        }

        return sb.ToString().TrimEnd();
    }

    private async void LibraryImportAudio_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RunLibraryAudioImportCommandAsync(paths: null);
    }

    private async void LibraryAudioImportCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_libraryAudioImportBusy || IsWithinImportButton(e.OriginalSource as DependencyObject))
            return;
        e.Handled = true;
        await RunLibraryAudioImportCommandAsync(paths: null);
    }

    private static bool IsWithinImportButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private Task<IEnumerable<string>?> ChooseLibraryAudioFilesAsync()
    {
        if (_libraryAudioImportBusy) return Task.FromResult<IEnumerable<string>?>(null);

        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Audio / recording files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.mka;*.mp4;*.mov|All files|*.*",
            Title = IsArabicLanguage ? "اختر تسجيلًا لاستيراده" : "Choose recording(s) to import"
        };

        return Task.FromResult(dialog.ShowDialog(this) == true
            ? (IEnumerable<string>?)dialog.FileNames
            : null);
    }

    private async Task RunLibraryAudioImportCommandAsync(IEnumerable<string>? paths)
    {
        if (_libraryAudioImportBusy) return;
        var started = await LibraryAudioImportCommand.ExecuteAsync(
            paths,
            ChooseLibraryAudioFilesAsync,
            ImportLibraryAudioFilesAsync);
        if (!started && paths is not null)
        {
            LibraryAudioImportStatusText.Text = IsArabicLanguage
                ? "الملف المحدد غير مدعوم أو غير متاح. لم تُضف أي تسجيلات."
                : "The selected file is unsupported or unavailable. No recordings were added.";
        }
    }

    private void LibraryAudioImport_DragEnter(object sender, DragEventArgs e)
    {
        SetLibraryImportDragVisual(IsSupportedDrop(e));
        LibraryAudioImport_DragOver(sender, e);
    }

    private void LibraryAudioImport_DragLeave(object sender, DragEventArgs e) =>
        SetLibraryImportDragVisual(active: false);

    private void SetLibraryImportDragVisual(bool active)
    {
        LibraryAudioImportCard.BorderBrush = active
            ? (Brush)FindResource("AccentTextBrush")
            : (Brush)FindResource("BorderBrush");
        LibraryAudioImportCard.BorderThickness = active ? new Thickness(2) : new Thickness(1);
        LibraryAudioImportCard.Background = active
            ? (Brush)FindResource("AccentSoftBrush")
            : (Brush)FindResource("SurfaceBrush");
    }

    private static bool IsSupportedDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        e.Data.GetData(DataFormats.FileDrop) is string[] files &&
        LibraryAudioImportCommand.SupportedFiles(files).Count > 0;

    private void LibraryAudioImport_DragOver(object sender, DragEventArgs e)
    {
        if (_libraryAudioImportBusy || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        e.Effects = LibraryAudioImportCommand.SupportedFiles(files).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LibraryAudioImport_Drop(object sender, DragEventArgs e)
    {
        SetLibraryImportDragVisual(active: false);
        if (_libraryAudioImportBusy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        e.Handled = true;
        await RunLibraryAudioImportCommandAsync(files);
    }

    private async Task ImportLibraryAudioFilesAsync(IEnumerable<string> paths)
    {
        var files = LibraryAudioImportCommand.SupportedFiles(paths).ToList();
        if (files.Count == 0)
        {
            LibraryAudioImportStatusText.Text = IsArabicLanguage
                ? "لم يتم العثور على ملف صوتي مدعوم."
                : "No supported audio file was found.";
            return;
        }

        _libraryAudioImportBusy = true;
        LibraryImportAudioButton.IsEnabled = false;
        var importer = new MeetingImportService(_repo);
        var imported = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                LibraryAudioImportStatusText.Text = IsArabicLanguage
                    ? $"جارٍ الاستيراد محليًا {i + 1}/{files.Count}: {Path.GetFileName(file)}"
                    : $"Importing locally {i + 1}/{files.Count}: {Path.GetFileName(file)}";

                try
                {
                    var progress = new Progress<string>(message => LibraryAudioImportStatusText.Text = message);
                    await importer.ImportAudioFileAsync(file, _settings, progress, CancellationToken.None);
                    imported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    LibraryAudioImportStatusText.Text = IsArabicLanguage
                        ? $"تعذر استيراد {Path.GetFileName(file)}: {ex.Message}"
                        : $"Could not import {Path.GetFileName(file)}: {ex.Message}";
                }
            }

            RefreshLibrary();
            RefreshRecent();
            LibraryAudioImportStatusText.Text = IsArabicLanguage
                ? $"تم الاستيراد: {imported} • فشل: {failed} • التسجيلات جاهزة، واضغط «تفريغ الآن» عندما تريد التحليل."
                : $"Imported: {imported} • failed: {failed} • recordings are ready; choose Transcribe Now when you want analysis.";
        }
        finally
        {
            _libraryAudioImportBusy = false;
            LibraryImportAudioButton.IsEnabled = true;
        }
    }

    private void OpenCategories_Click(object sender, RoutedEventArgs e) => ShowCategories();

    private void ShowLibrary()
    {
        HomeView.Visibility = Visibility.Collapsed;
        CategoriesView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        IntelligenceView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        SetActiveNav("Library");
        RefreshCategories();
        RefreshLibrary();
    }

    private void ShowCategories()
    {
        HomeView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Hidden;
        SettingsView.Visibility = Visibility.Collapsed;
        IntelligenceView.Visibility = Visibility.Collapsed;
        CategoriesView.Visibility = Visibility.Visible;
        SetActiveNav("Categories");
        RefreshCategories();
        RefreshCategoryManager();
    }

    private void BackHome_Click(object sender, RoutedEventArgs e)
    {
        LibraryView.Visibility = Visibility.Hidden;
        CategoriesView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        IntelligenceView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        SetActiveNav("Home");
        RefreshRecent();
        RefreshCategories();
    }

    private void SetActiveNav(string page)
    {
        HomeNavButton.Tag = page == "Home" ? "Active" : null;
        LibraryNavButton.Tag = page == "Library" ? "Active" : null;
        SearchNavButton.Tag = page == "Search" ? "Active" : null;
        IntelligenceNavButton.Tag = page == "Intelligence" ? "Active" : null;
        PeopleNavButton.Tag = page == "People" ? "Active" : null;
        CategoriesNavButton.Tag = page == "Categories" ? "Active" : null;
        SettingsNavButton.Tag = page == "Settings" ? "Active" : null;
    }

    private void RefreshCategoryManager()
    {
        if (CategoryManagerList is null) return;
        CategoryManagerList.ItemsSource = _repo.GetCategories()
            .Where(c => !c.Name.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase) || c.MeetingCount > 0)
            .ToList();
    }

    private void ToggleCategoryPinned_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string name) return;
        var category = _repo.GetCategories().FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (category is null) return;

        _repo.SetCategoryPinned(name, !category.Pinned);
        RefreshCategories();
    }

    private void MoveCategoryUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string name)
        {
            _repo.MoveCategory(name, -1);
            RefreshCategories();
        }
    }

    private void MoveCategoryDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string name)
        {
            _repo.MoveCategory(name, 1);
            RefreshCategories();
        }
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string oldName) return;

        var dlg = new TextPromptWindow("Rename Category", "Category name", oldName) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var newName = dlg.Result.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        if (!_repo.RenameCategory(oldName, newName))
        {
            MessageBox.Show("That category name is already in use.", "Categories",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedCategory.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            _selectedCategory = newName;

        RefreshAll();
        RefreshCategoryManager();
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string name) return;

        if (name.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Uncategorized is a system category and cannot be deleted.", "Categories",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            $"Delete '{name}'? Meetings currently using it will move to Uncategorized.",
            "Delete Category",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        if (_repo.DeleteCategory(name))
        {
            if (_selectedCategory.Equals(name, StringComparison.OrdinalIgnoreCase))
                _selectedCategory = "All";

            RefreshAll();
            RefreshCategoryManager();
        }
    }

    private void ToggleRecent_Click(object sender, RoutedEventArgs e)
    {
        _recentVisible = !_recentVisible;
        _settings.ShowRecentRecordings = _recentVisible;
        _settingsService.Save(_settings);
        ApplyRecentVisibility();
    }

    private void ApplyRecentVisibility()
    {
        RecentPanel.Visibility = _recentVisible ? Visibility.Visible : Visibility.Collapsed;
        HomeRecordColumn.Width = _recentVisible
            ? new GridLength(1.17, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
        HomeRecentColumn.Width = _recentVisible
            ? new GridLength(0.83, GridUnitType.Star)
            : new GridLength(0);

        if (_recentVisible)
        {
            PrivacyToggleButton.Content = IsArabicLanguage ? "●  التسجيلات الأخيرة ظاهرة" : "●  Recent recordings visible";
            PrivacyToggleButton.Background = (Brush)FindResource("AccentSoftBrush");
            PrivacyToggleButton.BorderBrush = (Brush)FindResource("AccentSoftBorderBrush");
            PrivacyToggleButton.Foreground = (Brush)FindResource("AccentTextBrush");
            PrivacyToggleButton.ToolTip = IsArabicLanguage
                ? "اضغط لإخفاء التسجيلات الأخيرة من الصفحة الرئيسية."
                : "Click to hide recent recordings from Home.";
        }
        else
        {
            PrivacyToggleButton.Content = IsArabicLanguage ? "○  إظهار التسجيلات الأخيرة" : "○  Show recent recordings";
            PrivacyToggleButton.Background = (Brush)FindResource("SurfaceBrush");
            PrivacyToggleButton.BorderBrush = (Brush)FindResource("BorderBrush");
            PrivacyToggleButton.Foreground = (Brush)FindResource("PrimaryTextBrush");
            PrivacyToggleButton.ToolTip = IsArabicLanguage
                ? "اضغط لإظهار التسجيلات الأخيرة في الصفحة الرئيسية."
                : "Click to show recent recordings on Home.";
        }
    }

    private void RefreshStatusCards()
    {
        SetStatus(MicStatusText,
            _audioMeter.MicrophoneReady ? Ui("Ready", "جاهز") : Ui("Issue", "مشكلة"),
            _audioMeter.MicrophoneReady);

        SetStatus(SystemAudioStatusText,
            _audioMeter.SystemAudioReady ? Ui("Ready", "جاهز") : Ui("Issue", "مشكلة"),
            _audioMeter.SystemAudioReady);

        try
        {
            var root = System.IO.Path.GetPathRoot(AppPaths.Root);
            var drive = string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
            if (drive is not null)
            {
                var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                var good = freeGb >= 10;
                SetStatus(StorageStatusText, good ? (IsArabicLanguage ? $"{freeGb:0} GB متاح" : $"{freeGb:0} GB free") : (IsArabicLanguage ? $"{freeGb:0.0} GB منخفض" : $"{freeGb:0.0} GB low"), good);
            }
        }
        catch
        {
            SetStatus(StorageStatusText, Ui("Check", "تحقق"), false);
        }

        var buzzReady = File.Exists(_settings.BuzzExe);
        var processing = _repo.Recent(8).Any(m =>
            m.TranscriptionStatus.Contains("Transcribing", StringComparison.OrdinalIgnoreCase) ||
            m.TranscriptionStatus.Contains("Queued", StringComparison.OrdinalIgnoreCase));

        if (!buzzReady)
        {
            SetStatus(TranscriptionStatusText, Ui("Install / Repair", "تثبيت / إصلاح"), false);
            SetTranscriptionTicker(Ui("Transcription engine needs repair before the next meeting.", "يحتاج محرك التفريغ إلى إصلاح قبل الاجتماع التالي."), active: false);
        }
        else if (processing || TranscriptionProgressService.HasActive)
        {
            var live = TranscriptionProgressService.GetAnyDisplay() ?? Ui("Processing transcript", "جارٍ معالجة التفريغ");
            TranscriptionStatusText.Text = live;
            TranscriptionStatusText.Foreground =
                (Brush)FindResource("WarningAccentBrush");

            SetTranscriptionTicker(
                IsArabicLanguage
                    ? "مباشر • " + live + "   •   الصوت محفوظ محليًا بأمان   •   سيُنهي Archestro التفريغ عند اكتمال هذه المرحلة"
                    : "LIVE • " + live + "   •   Audio is safe locally   •   Archestro will finalize the transcript when this pass completes",
                active: true);
        }
        else
        {
            SetStatus(TranscriptionStatusText, Ui("Ready", "جاهز"), true);
            SetTranscriptionTicker(
                IsArabicLanguage
                    ? "التفريغ جاهز • تمر التسجيلات الجديدة هنا عبر المسودة والحفاظ على اللغة وتوقيت المتحدثين والإنهاء."
                    : "Transcription ready • new recordings move through draft, language preservation, speaker timing, and finalization here.",
                active: false);
        }
    }

    private void SetTranscriptionTicker(string text, bool active)
    {
        if (TranscriptionTickerText is null ||
            TranscriptionTickerViewport is null ||
            TranscriptionTickerTransform is null)
            return;

        var contentChanged = !string.Equals(_lastTickerText, text, StringComparison.Ordinal);
        var activationChanged = _tickerActive != active;

        _tickerActive = active;
        TranscriptionTickerText.Foreground = (Brush)FindResource(
            active ? "WarningAccentBrush" : "AccentTextBrush");

        if (contentChanged)
        {
            _lastTickerText = text;
            TranscriptionTickerText.Text = text;
        }

        if (!active)
        {
            _tickerX = 0;
            TranscriptionTickerTransform.X = 0;
            return;
        }

        // Generic UI refreshes, clicks and theme application must not restart motion.
        // Text/stage changes preserve phase; only idle→active starts a fresh traversal.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // A stage/text update must not snap the rail back to the beginning.
            // Only a transition from idle to active starts a new traversal.
            RecalculateTickerGeometry(
                resetPosition: activationChanged && active);
        }), DispatcherPriority.Background);
    }

    private void RestartTranscriptionTicker()
    {
        // Historical name kept for existing callers. It now recalculates geometry only,
        // preserving the current position so theme/focus/layout refreshes never restart the feed.
        Dispatcher.BeginInvoke(
            new Action(() => RecalculateTickerGeometry(resetPosition: false)),
            DispatcherPriority.Background);
    }

    private void RecalculateTickerGeometry(bool resetPosition)
    {
        if (TranscriptionTickerText is null ||
            TranscriptionTickerViewport is null ||
            TranscriptionTickerTransform is null)
            return;

        TranscriptionTickerText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _tickerTextWidth = Math.Max(1, TranscriptionTickerText.DesiredSize.Width);
        _tickerViewportWidth = Math.Max(1, TranscriptionTickerViewport.ActualWidth);

        if (!_tickerActive)
        {
            _tickerX = 0;
            TranscriptionTickerTransform.X = 0;
            return;
        }

        if (resetPosition ||
            double.IsNaN(_tickerX) ||
            double.IsInfinity(_tickerX) ||
            _tickerX < -_tickerTextWidth - 60 ||
            _tickerX > _tickerViewportWidth + 60)
        {
            _tickerX = _tickerViewportWidth;
        }

        TranscriptionTickerTransform.X = _tickerX;
        _tickerLastTick = DateTimeOffset.Now;
    }

    private void AdvanceTranscriptionTicker()
    {
        if (!_tickerActive ||
            TranscriptionTickerTransform is null ||
            TranscriptionTickerViewport is null ||
            TranscriptionTickerText is null)
            return;

        var now = DateTimeOffset.Now;
        var elapsed = Math.Clamp((now - _tickerLastTick).TotalSeconds, 0, 0.25);
        _tickerLastTick = now;

        if (_tickerViewportWidth <= 1 || _tickerTextWidth <= 1)
            RecalculateTickerGeometry(resetPosition: false);

        const double pixelsPerSecond = 58.0;
        _tickerX -= pixelsPerSecond * elapsed;

        if (_tickerX <= -_tickerTextWidth - 18)
            _tickerX = _tickerViewportWidth + 18;

        TranscriptionTickerTransform.X = _tickerX;
    }

    private void SetStatus(TextBlock block, string text, bool good)
    {
        block.Text = text;
        block.Foreground = (Brush)FindResource(good ? "GreenBrush" : "DangerTextBrush");
    }

    private MeetingRecord? GetMeeting(object sender)
    {
        if (sender is not FrameworkElement element || element.Tag is not string id) return null;
        return _repo.Get(id);
    }

    private void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is not MeetingRecord meeting) return;
        TryOpenRecording(meeting);
    }

    private void UseSuggestedTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string id)
            return;

        var meeting = _repo.Get(id);
        if (meeting is null || string.IsNullOrWhiteSpace(meeting.SuggestedTitle))
            return;

        _repo.UpdateEditableFields(
            meeting.Id,
            meeting.SuggestedTitle.Trim(),
            true,
            meeting.Notes,
            meeting.Category);

        RefreshAll();
    }

    private void EditMeetingTitle_Click(object sender, RoutedEventArgs e)
    {
        var meeting = GetMeeting(sender);
        if (meeting is null) return;

        var initial = meeting.HasExplicitTitle
            ? meeting.Title
            : (!string.IsNullOrWhiteSpace(meeting.SuggestedTitle)
                ? meeting.SuggestedTitle
                : meeting.PrimaryTitle);

        var dialog = new TextPromptWindow(
            "Edit Meeting Title",
            "Meeting title",
            initial) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        var title = dialog.Result.Trim();
        _repo.UpdateEditableFields(
            meeting.Id,
            title,
            !string.IsNullOrWhiteSpace(title),
            meeting.Notes,
            meeting.Category);

        RefreshAll();
    }

    private void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        var meeting = GetMeeting(sender);
        if (meeting is not null) TryOpenRecording(meeting);
    }

    private void TryOpenRecording(MeetingRecord meeting)
    {
        try
        {
            var path = File.Exists(meeting.AudioPath) ? meeting.AudioPath : meeting.RecordingPath;
            ProcessService.OpenPath(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Recording", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void TranscribeNow_Click(object sender, RoutedEventArgs e)
    {
        var meeting = GetMeeting(sender);
        if (meeting is null) return;

        if (string.IsNullOrWhiteSpace(meeting.AudioPath) ||
            !File.Exists(meeting.AudioPath))
        {
            MessageBox.Show(
                "The meeting audio file is not available.",
                "Transcribe Now",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var promoted = _transcription.RequestStartNow(meeting);

        if (!promoted)
        {
            QueueTranscript(
                meeting,
                forceMixedLanguageRecovery: false,
                startImmediately: true);
        }

        if (_recording.IsRecording)
        {
            MessageBox.Show(
                "Transcribe Now is queued because a live meeting is recording. It will start as soon as recording stops.",
                "Transcribe Now",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        RefreshAll();
        RefreshStatusCards();
    }

    private void OpenMeetingReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_license.IntelligenceAllowed)
            {
                MessageBox.Show(
                    IsArabicLanguage
                        ? "هذه النسخة لا تتضمن تقرير الاجتماع المحلي. يلزم ترخيص Intelligence."
                        : "This device is licensed for Archestro Meeting Vault Core. Upgrade to Intelligence to build Meeting Reports.",
                    "Archestro Meeting Vault",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var meeting = GetMeeting(sender);
            if (meeting is null) return;

            if (!File.Exists(meeting.SrtPath) && !File.Exists(meeting.TranscriptPath))
            {
                MessageBox.Show(
                    IsArabicLanguage
                        ? "أكمل التفريغ النصي أولًا. تقرير الاجتماع يعتمد على نص الاجتماع والأدلة."
                        : "Finish transcription first. Meeting Report needs transcript evidence.",
                    IsArabicLanguage ? "تقرير الاجتماع" : "Meeting Report",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_intelligenceWindows.TryGetValue(meeting.Id, out var existing))
            {
                existing.OpenReportView();
                return;
            }

            var window = new IntelligenceWindow(meeting, _meetingIntelligence, openReportOnLoad: true);
            _intelligenceWindows[meeting.Id] = window;
            window.Closed += (_, _) => _intelligenceWindows.Remove(meeting.Id);
            window.Show();
            window.OpenReportView();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, IsArabicLanguage ? "تقرير الاجتماع" : "Meeting Report",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenIntelligence_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_license.IntelligenceAllowed)
            {
                MessageBox.Show(
                    IsArabicLanguage
                        ? "هذه النسخة لا تتضمن التحليل المحلي. يلزم ترخيص Intelligence."
                        : "This device is licensed for Archestro Meeting Vault Core. Upgrade to Intelligence to use local meeting analysis.",
                    "Archestro Meeting Vault",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var meeting = GetMeeting(sender);
            if (meeting is null) return;

            if (!File.Exists(meeting.SrtPath) && !File.Exists(meeting.TranscriptPath))
            {
                MessageBox.Show(
                    IsArabicLanguage
                        ? "أكمل التفريغ النصي أولًا. يحتاج التحليل إلى دليل من نص الاجتماع."
                        : "Finish transcription first. Intelligence needs meeting transcript evidence.",
                    "Archestro Intelligence",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_intelligenceWindows.TryGetValue(meeting.Id, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Show();
                existing.Activate();
                return;
            }

            var window = new IntelligenceWindow(meeting, _meetingIntelligence);
            _intelligenceWindows[meeting.Id] = window;
            window.Closed += (_, _) => _intelligenceWindows.Remove(meeting.Id);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Archestro Intelligence",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenSpeakerTranscript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var meeting = GetMeeting(sender);
            if (meeting is null) return;

            if (!File.Exists(meeting.SrtPath))
            {
                MessageBox.Show(
                    IsArabicLanguage
                        ? "أكمل التفريغ أولًا. يحتاج تحليل المتحدثين إلى نص موقّت (.srt)."
                        : "Finish transcription first. Speaker Intelligence needs the timed transcript (.srt).",
                    "Speaker Intelligence",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_speakerWindows.TryGetValue(meeting.Id, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Show();
                existing.Activate();
                return;
            }

            var window = new SpeakerTranscriptWindow(meeting, _speakerIntelligence);
            _speakerWindows[meeting.Id] = window;
            window.Closed += (_, _) => _speakerWindows.Remove(meeting.Id);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Speaker Intelligence",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var meeting = GetMeeting(sender);
            if (meeting is null) return;

            if (!File.Exists(meeting.TranscriptPath))
            {
                var failed = meeting.TranscriptionStatus.Contains(
                    "failed", StringComparison.OrdinalIgnoreCase);

                var onDemand = meeting.TranscriptionStatus.Contains(
                    "on demand", StringComparison.OrdinalIgnoreCase);

                if (onDemand)
                {
                    var answer = MessageBox.Show(
                        "This very short recording was saved without automatic transcription to keep the computer fast. Transcribe it now?",
                        "Transcript",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (answer == MessageBoxResult.Yes)
                        QueueTranscript(meeting, forceMixedLanguageRecovery: false);

                    return;
                }

                if (failed)
                {
                    var answer = MessageBox.Show(
                        "The previous transcription attempt failed. Retry it now?",
                        "Transcript",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (answer == MessageBoxResult.Yes)
                        _ = RetryTranscriptAsync(meeting);

                    return;
                }

                MessageBox.Show(
                    $"Transcript is not ready yet. Current status: {meeting.StatusDisplay}",
                    "Transcript",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var transcriptWindow = new TranscriptViewerWindow(meeting.TranscriptPath, meeting.PrimaryTitle)
            {
                Owner = this
            };
            transcriptWindow.Show();
            transcriptWindow.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Transcript", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void QueueTranscript(
        MeetingRecord meeting,
        bool forceMixedLanguageRecovery,
        bool startImmediately = false)
    {
        if (_transcription.IsPending(meeting.Id))
        {
            RefreshAll();
            return;
        }

        _ = Task.Run(async () =>
        {
            await _transcription.TranscribeAsync(
                meeting,
                CancellationToken.None,
                forceMixedLanguageRecovery,
                startImmediately);

            await Dispatcher.InvokeAsync(() =>
            {
                RefreshAll();
                RefreshStatusCards();
            });
        });

        RefreshAll();
        RefreshStatusCards();
    }

    private async Task RetryTranscriptAsync(MeetingRecord meeting)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(meeting.AudioPath) || !File.Exists(meeting.AudioPath))
            {
                MessageBox.Show(
                    "The meeting audio file is not available for retry.",
                    "Transcript",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            QueueTranscript(meeting, forceMixedLanguageRecovery: false);

            MessageBox.Show(
                "Transcript added to Smart Background. It normally starts after a short grace period when system load is reasonable. Use ⚡ Transcribe Now to skip the wait.",
                "Transcript",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Transcript retry could not complete: {ex.Message}",
                "Transcript",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var meeting = GetMeeting(sender);
        if (meeting is null || sender is not FrameworkElement anchor) return;

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false
        };
        AppearanceService.Apply(this);
        string L(string en, string ar) => IsArabicLanguage ? ar : en;

        var openFolder = new MenuItem { Header = L("Open Folder", "فتح المجلد") };
        openFolder.Click += (_, _) => TryOpen(meeting.FolderPath);
        menu.Items.Add(openFolder);

        var meetingReport = new MenuItem { Header = L("★ Meeting Report", "★ تقرير الاجتماع") };
        meetingReport.Click += (_, _) =>
        {
            if (_intelligenceWindows.TryGetValue(meeting.Id, out var existingReport))
            {
                existingReport.OpenReportView();
                return;
            }
            if (!File.Exists(meeting.SrtPath) && !File.Exists(meeting.TranscriptPath))
            {
                MessageBox.Show(
                    L("Finish transcription first. Meeting Report needs transcript evidence.", "أكمل التفريغ النصي أولًا. تقرير الاجتماع يعتمد على نص الاجتماع والأدلة."),
                    L("Meeting Report", "تقرير الاجتماع"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            var reportWindow = new IntelligenceWindow(meeting, _meetingIntelligence, openReportOnLoad: true);
            _intelligenceWindows[meeting.Id] = reportWindow;
            reportWindow.Closed += (_, _) => _intelligenceWindows.Remove(meeting.Id);
            reportWindow.Show();
            reportWindow.OpenReportView();
        };
        menu.Items.Add(meetingReport);

        var marks = _repo.GetMarks(meeting.Id);
        var importantMoments = new MenuItem
        {
            Header = marks.Count > 0
                ? $"Important Moments ({marks.Count})"
                : "Important Moments (none)",
            IsEnabled = marks.Count > 0
        };
        importantMoments.Click += (_, _) =>
        {
            var dlg = new ImportantMomentsWindow(meeting, marks) { Owner = this };
            dlg.ShowDialog();
        };
        menu.Items.Add(importantMoments);

        var retryTranscript = new MenuItem
        {
            Header = L("Retry Transcript", "إعادة محاولة التفريغ"),
            IsEnabled =
                !string.IsNullOrWhiteSpace(meeting.AudioPath) &&
                File.Exists(meeting.AudioPath) &&
                !_transcription.IsPending(meeting.Id) &&
                !meeting.TranscriptionStatus.Contains("Transcribing", StringComparison.OrdinalIgnoreCase)
        };
        retryTranscript.Click += (_, _) => _ = RetryTranscriptAsync(meeting);
        menu.Items.Add(retryTranscript);

        var improveBilingual = new MenuItem
        {
            Header = L("Improve Arabic + English — bounded quality repair", "تحسين العربية + الإنجليزية"),
            IsEnabled =
                !string.IsNullOrWhiteSpace(meeting.AudioPath) &&
                File.Exists(meeting.AudioPath) &&
                !_transcription.IsPending(meeting.Id)
        };
        improveBilingual.Click += (_, _) =>
        {
            QueueTranscript(meeting, forceMixedLanguageRecovery: true);
            MessageBox.Show(
                "Bilingual quality repair was added to the queue. It uses bounded whole-file Arabic/English recovery — no chunk farm. Use it only if the completed transcript still missed a spoken language.",
                "Improve Transcript",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        };
        menu.Items.Add(improveBilingual);

        var cancelTranscript = new MenuItem
        {
            Header = L("Cancel Transcript", "إلغاء التفريغ"),
            IsEnabled = _transcription.IsPending(meeting.Id)
        };
        cancelTranscript.Click += async (_, _) =>
        {
            await _transcription.CancelAndWaitAsync(meeting.Id, TimeSpan.FromSeconds(8));
            RefreshAll();
            RefreshStatusCards();
        };
        menu.Items.Add(cancelTranscript);

        var diagnosticsFolder = Path.Combine(
            meeting.FolderPath,
            "_TRANSCRIPTION_DIAGNOSTICS");

        var openTranscriptDiagnostics = new MenuItem
        {
            Header = L("Open Transcript Diagnostics", "فتح تشخيص التفريغ"),
            IsEnabled = Directory.Exists(diagnosticsFolder)
        };
        openTranscriptDiagnostics.Click += (_, _) =>
        {
            if (Directory.Exists(diagnosticsFolder))
                ProcessService.OpenPath(diagnosticsFolder);
        };
        menu.Items.Add(openTranscriptDiagnostics);

        var rename = new MenuItem { Header = L("Rename Meeting", "إعادة تسمية الاجتماع") };
        rename.Click += (_, _) =>
        {
            var current = meeting.HasExplicitTitle ? meeting.Title : "";
            var dlg = new TextPromptWindow("Rename Meeting", "Meeting name", current) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                var title = dlg.Result.Trim();
                _repo.UpdateEditableFields(
                    meeting.Id,
                    title,
                    !string.IsNullOrWhiteSpace(title),
                    meeting.Notes,
                    meeting.Category);
                RefreshAll();
            }
        };
        menu.Items.Add(rename);

        if (!meeting.HasExplicitTitle && !string.IsNullOrWhiteSpace(meeting.SuggestedTitle))
        {
            var useSuggested = new MenuItem { Header = $"Use Suggested Title — {meeting.SuggestedTitle}" };
            useSuggested.Click += (_, _) =>
            {
                _repo.UpdateEditableFields(
                    meeting.Id,
                    meeting.SuggestedTitle,
                    true,
                    meeting.Notes,
                    meeting.Category);
                RefreshAll();
            };
            menu.Items.Add(useSuggested);
        }

        var note = new MenuItem { Header = L("Add / Edit Note", "إضافة / تعديل ملاحظة") };
        note.Click += (_, _) =>
        {
            var dlg = new TextPromptWindow("Meeting Note", "Notes", meeting.Notes, multiline: true) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _repo.UpdateEditableFields(
                    meeting.Id,
                    meeting.Title,
                    meeting.HasExplicitTitle,
                    dlg.Result.Trim(),
                    meeting.Category);
                RefreshAll();
            }
        };
        menu.Items.Add(note);

        var categoryMenu = new MenuItem { Header = L("Change Category", "تغيير التصنيف") };
        foreach (var category in _repo.GetCategories())
        {
            var item = new MenuItem
            {
                Header = category.Name,
                IsChecked = category.Name.Equals(meeting.Category, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) =>
            {
                _repo.UpdateEditableFields(
                    meeting.Id,
                    meeting.Title,
                    meeting.HasExplicitTitle,
                    meeting.Notes,
                    category.Name);
                RefreshAll();
            };
            categoryMenu.Items.Add(item);
        }

        categoryMenu.Items.Add(new Separator());
        var add = new MenuItem { Header = L("+ Add Category…", "+ إضافة تصنيف…") };
        add.Click += (_, _) => AddCategory_Click(this, new RoutedEventArgs());
        categoryMenu.Items.Add(add);
        menu.Items.Add(categoryMenu);

        menu.Items.Add(new Separator());
        var deleteMeeting = new MenuItem
        {
            Header = L("🗑 Delete Meeting…", "🗑 حذف الاجتماع…"),
            Foreground = BrushFromHex("#FF7B80")
        };
        deleteMeeting.Click += async (_, _) => await DeleteMeetingAsync(meeting);
        menu.Items.Add(deleteMeeting);

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async Task DeleteMeetingAsync(MeetingRecord meeting)
    {
        var answer = MessageBox.Show(
            $"Delete '{meeting.PrimaryTitle}'?\n\nThe meeting folder (recording, audio, transcript and marks) will be moved to the Windows Recycle Bin and removed from Meeting Vault.",
            "Delete Meeting",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _transcription.CancelAndWaitAsync(meeting.Id, TimeSpan.FromSeconds(10));

            var meetingsRoot = Path.GetFullPath(AppPaths.Meetings)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var folder = Path.GetFullPath(meeting.FolderPath);

            if (!folder.StartsWith(meetingsRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Safety check stopped deletion because the meeting folder is outside the Meeting Vault archive.");

            if (Directory.Exists(folder))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    folder,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }

            _repo.Delete(meeting.Id);
            RefreshAll();
            RefreshStatusCards();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The meeting could not be deleted safely: {ex.Message}",
                "Delete Meeting",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static void TryOpen(string path)
    {
        try
        {
            ProcessService.OpenPath(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Archestro Meeting Vault", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source is Button) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();


    private static RadialGradientBrush CreateGlowBrush(Color color, byte alpha)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
        return brush;
    }

    private static SolidColorBrush BrushFromHex(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_recording?.IsRecording == true)
        {
            MessageBox.Show(
                "A meeting is still recording. Click the large recording circle to Stop & Save before closing.",
                "Archestro Meeting Vault",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }

        try { _audioMeter.Dispose(); } catch { }
        _clockTimer.Stop();
        _visualTimer.Stop();
        _searchDebounceTimer.Stop();
        try { SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged; } catch { }
        base.OnClosing(e);
    }
}

public sealed class UiDynamicTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        if (!AppearanceService.IsArabic || string.IsNullOrWhiteSpace(text)) return text;
        var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Uncategorized"] = "غير مصنف",
            ["Transcript ready"] = "النص جاهز",
            ["Imported local recording"] = "تسجيل محلي مستورد",
            ["Recording"] = "التسجيل",
            ["Transcript"] = "النص",
            ["Speakers"] = "المتحدثون",
            ["Intelligence"] = "التحليل",
            ["Meeting Report"] = "تقرير الاجتماع",
            ["Transcribe Now"] = "تفريغ الآن"
        };
        if (exact.TryGetValue(text, out var translated)) return translated;
        return text
            .Replace("Suggested:", "مقترح:", StringComparison.OrdinalIgnoreCase)
            .Replace(" days ago", " يوم مضى", StringComparison.OrdinalIgnoreCase)
            .Replace(" day ago", " يوم مضى", StringComparison.OrdinalIgnoreCase)
            .Replace(" hr ago", " ساعة مضت", StringComparison.OrdinalIgnoreCase)
            .Replace(" hrs ago", " ساعات مضت", StringComparison.OrdinalIgnoreCase)
            .Replace("AM", "ص", StringComparison.OrdinalIgnoreCase)
            .Replace("PM", "م", StringComparison.OrdinalIgnoreCase)
            .Replace("Jan", "ينا", StringComparison.OrdinalIgnoreCase)
            .Replace("Feb", "فبر", StringComparison.OrdinalIgnoreCase)
            .Replace("Mar", "مار", StringComparison.OrdinalIgnoreCase)
            .Replace("Apr", "أبر", StringComparison.OrdinalIgnoreCase)
            .Replace("May", "ماي", StringComparison.OrdinalIgnoreCase)
            .Replace("Jun", "يون", StringComparison.OrdinalIgnoreCase)
            .Replace("Jul", "يول", StringComparison.OrdinalIgnoreCase)
            .Replace("Aug", "أغس", StringComparison.OrdinalIgnoreCase)
            .Replace("Sep", "سبت", StringComparison.OrdinalIgnoreCase)
            .Replace("Oct", "أكت", StringComparison.OrdinalIgnoreCase)
            .Replace("Nov", "نوف", StringComparison.OrdinalIgnoreCase)
            .Replace("Dec", "ديس", StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

