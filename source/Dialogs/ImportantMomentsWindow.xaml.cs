using System.Windows;
using Archestro.MeetingVault.Models;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault.Dialogs;

public partial class ImportantMomentsWindow : Window
{
    private readonly MeetingRecord _meeting;

    public ImportantMomentsWindow(MeetingRecord meeting, IReadOnlyList<ImportantMark> marks)
    {
        InitializeComponent();
        AppearanceService.Apply(this);
        FlowDirection = AppearanceService.IsArabic
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        _meeting = meeting;

        ChromeTitleText.Text = AppearanceService.IsArabic
            ? "اللحظات المهمة"
            : "Important Moments";
        MeetingTitleText.Text = meeting.PrimaryTitle;
        ApplyLocalizedCopy();

        var contexts = TranscriptContextService.BuildMomentContexts(
            marks,
            meeting.SrtPath);

        MomentsList.ItemsSource = contexts;
        CountText.Text = AppearanceService.IsArabic
            ? (contexts.Count == 1 ? "لحظة مهمة واحدة" : $"{contexts.Count} لحظات مهمة")
            : (contexts.Count == 1 ? "1 marked moment" : $"{contexts.Count} marked moments");
    }

    private void CopyTime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string timestamp)
        {
            Clipboard.SetText(timestamp);
            CountText.Text = AppearanceService.IsArabic
                ? $"تم نسخ {timestamp}"
                : $"Copied {timestamp}";
        }
    }

    private void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_meeting.AudioPath) && File.Exists(_meeting.AudioPath))
            ProcessService.OpenPath(_meeting.AudioPath);
        else if (!string.IsNullOrWhiteSpace(_meeting.RecordingPath) && File.Exists(_meeting.RecordingPath))
            ProcessService.OpenPath(_meeting.RecordingPath);
        else
            MessageBox.Show(
                AppearanceService.IsArabic ? "ملف التسجيل غير متاح." : "The recording file is not available.",
                AppearanceService.IsArabic ? "اللحظات المهمة" : "Important Moments",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyLocalizedCopy()
    {
        if (!AppearanceService.IsArabic) return;

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Important Moments"] = "اللحظات المهمة",
            ["Each mark is linked to the transcribed phrase spoken at that moment. The same marker is placed beside that phrase in the transcript."] =
                "كل علامة مرتبطة بالعبارة المنطوقة في تلك اللحظة، وتظهر العلامة نفسها بجانب العبارة داخل النص.",
            ["Copy time"] = "نسخ الوقت",
            ["Open Recording"] = "فتح التسجيل",
            ["Close"] = "إغلاق",
            ["Minimize"] = "تصغير",
            ["Maximize / Restore"] = "تكبير / استعادة"
        };

        TranslateCopy(this, map);
    }

    private static void TranslateCopy(DependencyObject root, IReadOnlyDictionary<string, string> map)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TextBlock text &&
                map.TryGetValue(text.Text, out var translatedText))
                text.Text = translatedText;
            else if (child is System.Windows.Controls.Button button &&
                     button.Content is string content &&
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
