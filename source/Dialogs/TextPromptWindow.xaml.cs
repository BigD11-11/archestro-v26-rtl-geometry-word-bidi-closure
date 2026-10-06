using System.Windows;
using System.Windows.Controls;
using Archestro.MeetingVault.Services;

namespace Archestro.MeetingVault.Dialogs;

public partial class TextPromptWindow : Window
{
    public string Result => ValueBox.Text;

    public TextPromptWindow(string title, string prompt, string initial, bool multiline = false)
    {
        InitializeComponent();
        AppearanceService.Apply(this);
        FlowDirection = AppearanceService.IsArabic
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        Title = TranslateKnown(title);
        ChromeTitleText.Text = Title;
        PromptText.Text = TranslateKnown(prompt);
        ValueBox.Text = initial;
        ValueBox.FlowDirection = AppearanceService.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ValueBox.TextAlignment = AppearanceService.IsArabic ? TextAlignment.Right : TextAlignment.Left;

        if (AppearanceService.IsArabic)
        {
            TranslateButtonContent(this);
        }

        if (multiline)
        {
            Height = 410;
            ValueBox.Height = 155;
            ValueBox.AcceptsReturn = true;
            ValueBox.TextWrapping = TextWrapping.Wrap;
            ValueBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            ValueBox.VerticalContentAlignment = VerticalAlignment.Top;
        }
        else
        {
            ValueBox.AcceptsReturn = false;
            ValueBox.TextWrapping = TextWrapping.NoWrap;
            ValueBox.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ValueBox.VerticalContentAlignment = VerticalAlignment.Center;
        }

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string TranslateKnown(string value)
    {
        if (!AppearanceService.IsArabic) return value;

        return value switch
        {
            "Add Category" => "إضافة تصنيف",
            "Category name" => "اسم التصنيف",
            "Rename Category" => "إعادة تسمية التصنيف",
            "Edit Meeting Title" => "تعديل اسم الاجتماع",
            "Meeting title" => "اسم الاجتماع",
            "Rename Meeting" => "إعادة تسمية الاجتماع",
            "Meeting name" => "اسم الاجتماع",
            "Meeting Note" => "ملاحظة الاجتماع",
            "Notes" => "الملاحظات",
            "Rename Speaker" => "إعادة تسمية المتحدث",
            "Speaker name" => "اسم المتحدث",
            _ => value
        };
    }

    private static void TranslateButtonContent(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button b && b.Content is string text)
            {
                if (text == "Save") b.Content = "حفظ";
                else if (text == "Cancel") b.Content = "إلغاء";
            }
            if (child is FrameworkElement element && element.ToolTip is string tip)
            {
                if (tip == "Close") element.ToolTip = "إغلاق";
                else if (tip == "Minimize") element.ToolTip = "تصغير";
                else if (tip == "Maximize / Restore") element.ToolTip = "تكبير / استعادة";
            }
            TranslateButtonContent(child);
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
    private void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();

}
