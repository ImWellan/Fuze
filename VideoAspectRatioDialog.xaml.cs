using System.Windows;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public partial class VideoAspectRatioDialog : Window
{
    public VideoAspectRatioDialog(string currentAspectRatio)
    {
        InitializeComponent();
        LocalizationService.ApplyToWindow(this);
        var parts = currentAspectRatio.Split(':', StringSplitOptions.TrimEntries);
        WidthTextBox.Text = parts.Length == 2 && int.TryParse(parts[0], out var width) && width > 0
            ? width.ToString()
            : "16";
        HeightTextBox.Text = parts.Length == 2 && int.TryParse(parts[1], out var height) && height > 0
            ? height.ToString()
            : "9";
        Loaded += (_, _) =>
        {
            WidthTextBox.Focus();
            WidthTextBox.SelectAll();
        };
    }

    public string AspectRatio { get; private set; } = "16:9";
    public event EventHandler? Applied;

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthTextBox.Text, out var width) || width is < 1 or > 10000 ||
            !int.TryParse(HeightTextBox.Text, out var height) || height is < 1 or > 10000)
        {
            ValidationText.Text = LocalizationService.Get("Entrez deux nombres compris entre 1 et 10 000.");
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        AspectRatio = $"{width}:{height}";
        Applied?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Close();

    private void NumericTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private void NumericTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ValidationText.Visibility = Visibility.Collapsed;

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm_OnClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
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
}
