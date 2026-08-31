using System.Windows;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public partial class VideoZoomDialog : Window
{
    public VideoZoomDialog(int currentPercent)
    {
        InitializeComponent();
        LocalizationService.ApplyToWindow(this);
        ZoomPercentTextBox.Text = Math.Clamp(currentPercent, 50, 1000).ToString();
        Loaded += (_, _) =>
        {
            ZoomPercentTextBox.Focus();
            ZoomPercentTextBox.SelectAll();
        };
    }

    public int ZoomPercent { get; private set; }
    public event EventHandler? Applied;

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ZoomPercentTextBox.Text, out var percent) || percent is < 50 or > 1000)
        {
            ValidationText.Text = LocalizationService.Get("Entrez une valeur comprise entre 50 et 1 000 %.");
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        ZoomPercent = percent;
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
