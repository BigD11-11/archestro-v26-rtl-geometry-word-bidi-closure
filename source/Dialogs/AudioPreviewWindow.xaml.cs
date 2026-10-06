using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault.Dialogs;

public partial class AudioPreviewWindow : Window
{
    private readonly MeetingAudioPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly string _audioPath;
    private readonly double _startSeconds;
    private bool _updatingSlider;

    public AudioPreviewWindow(string audioPath, double startSeconds, string meetingTitle, string? context = null)
    {
        InitializeComponent();
        AppearanceService.Apply(this);
        FlowDirection = AppearanceService.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        _audioPath = audioPath;
        _startSeconds = startSeconds;
        ChromeTitleText.Text = AppearanceService.IsArabic ? "معاينة الصوت" : "Audio Preview";
        MeetingTitleText.Text = meetingTitle;
        ContextText.Text = string.IsNullOrWhiteSpace(context)
            ? (AppearanceService.IsArabic ? "تحكم كامل بالتشغيل المحلي." : "Full control over local playback.")
            : (AppearanceService.IsArabic ? $"نقطة البدء: {context}" : $"Start point: {context}");

        if (AppearanceService.IsArabic)
        {
            BackButton.Content = "-٥ث";
            PlayPauseButton.Content = "إيقاف مؤقت";
            StopButton.Content = "إيقاف";
            ForwardButton.Content = "+١٠ث";
        }

        Loaded += AudioPreviewWindow_Loaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _player.Dispose();
        };
        _timer.Tick += (_, _) => UpdateUi();
    }

    private void AudioPreviewWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        _player.Load(_audioPath, _startSeconds);
        _player.Play();
        _timer.Start();
        UpdateUi();
    }

    private void UpdateUi()
    {
        if (!_player.IsLoaded) return;

        _updatingSlider = true;
        PositionSlider.Maximum = Math.Max(1, _player.TotalTime.TotalSeconds);
        PositionSlider.Value = Math.Min(PositionSlider.Maximum, _player.CurrentTime.TotalSeconds);
        _updatingSlider = false;

        CurrentTimeText.Text = $"{_player.CurrentTime.ToString(@"mm\:ss")} / {_player.TotalTime.ToString(@"mm\:ss")}";
        StatusText.Text = _player.IsPlaying
            ? (AppearanceService.IsArabic ? "يعمل" : "Playing")
            : (AppearanceService.IsArabic ? "متوقف" : "Paused");
        PlayPauseButton.Content = _player.IsPlaying
            ? (AppearanceService.IsArabic ? "إيقاف مؤقت" : "Pause")
            : (AppearanceService.IsArabic ? "تشغيل" : "Play");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying)
            _player.Pause();
        else
            _player.Play();
        UpdateUi();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _player.Seek(_startSeconds);
        UpdateUi();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _player.Seek(_player.CurrentTime.TotalSeconds - 5);
        UpdateUi();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        _player.Seek(_player.CurrentTime.TotalSeconds + 10);
        UpdateUi();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider) return;
        if (!PositionSlider.IsMouseCaptureWithin && !PositionSlider.IsKeyboardFocusWithin) return;
        StatusText.Text = AppearanceService.IsArabic ? "جارٍ السحب" : "Seeking";
    }

    private void PositionSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_updatingSlider) return;
        _player.Seek(PositionSlider.Value);
        UpdateUi();
    }

    private void Chrome_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();
}
