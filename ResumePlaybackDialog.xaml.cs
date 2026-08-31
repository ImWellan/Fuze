using System.Windows;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public partial class ResumePlaybackDialog : Window
{
    public ResumePlaybackDialog(string mediaTitle, long positionMilliseconds)
    {
        InitializeComponent();
        MediaTitleText.Text = mediaTitle;
        MediaTitleText.ToolTip = mediaTitle;
        PositionText.Text = $"{LocalizationService.Get("Position trouvée")} : {FormatPosition(positionMilliseconds)}";
        LocalizationService.ApplyToWindow(this);
    }

    public bool Resume { get; private set; }

    private void Resume_OnClick(object sender, RoutedEventArgs e)
    {
        Resume = true;
        DialogResult = true;
    }

    private void Restart_OnClick(object sender, RoutedEventArgs e)
    {
        Resume = false;
        DialogResult = false;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Resume_OnClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Restart_OnClick(sender, e);
            e.Handled = true;
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        DragMove();
        e.Handled = true;
    }

    private static string FormatPosition(long milliseconds)
    {
        var total = Math.Max(0, milliseconds);
        var hours = total / 3_600_000;
        total %= 3_600_000;
        var minutes = total / 60_000;
        total %= 60_000;
        var seconds = total / 1000;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }
}
