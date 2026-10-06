using System.Windows;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault.Dialogs;

public partial class TranscriptViewerWindow : Window
{
    private readonly string _transcriptPath;

    public TranscriptViewerWindow(string transcriptPath, string meetingTitle, double? focusSeconds = null)
    {
        InitializeComponent();
        AppearanceService.Apply(this);
        FlowDirection = AppearanceService.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _transcriptPath = transcriptPath;

        ChromeTitleText.Text = AppearanceService.IsArabic ? "النص" : "Transcript";
        MeetingTitleText.Text = meetingTitle;
        var focusText = focusSeconds.HasValue
            ? TimeSpan.FromSeconds(focusSeconds.Value).ToString(@"hh\:mm\:ss")
            : string.Empty;
        FocusInfoText.Text = focusSeconds.HasValue
            ? (AppearanceService.IsArabic ? $"موضع قريب من {focusText}" : $"Approximate focus: {focusText}")
            : (AppearanceService.IsArabic ? "عرض النص المحلي المرتبط بهذا الاجتماع." : "Showing the local transcript linked to this meeting.");
        OpenExternalButton.Content = AppearanceService.IsArabic ? "فتح الملف" : "Open file";
        CloseButton.Content = AppearanceService.IsArabic ? "إغلاق" : "Close";

        try
        {
            TranscriptBox.Text = File.Exists(_transcriptPath)
                ? File.ReadAllText(_transcriptPath)
                : (AppearanceService.IsArabic ? "ملف النص غير موجود." : "Transcript file was not found.");
        }
        catch (Exception ex)
        {
            TranscriptBox.Text = ex.Message;
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_transcriptPath))
            ProcessService.OpenPath(_transcriptPath);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Chrome_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (e.ClickCount == 2 && ResizeMode != ResizeMode.NoResize)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } catch { }
    }

    private void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();
}
