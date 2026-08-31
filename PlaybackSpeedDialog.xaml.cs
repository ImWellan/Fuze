using System.Globalization;
using System.Windows;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public partial class PlaybackSpeedDialog : Window
{
    public PlaybackSpeedDialog(float currentRate)
    {
        InitializeComponent();
        LocalizationService.ApplyToWindow(this);
        SpeedTextBox.Text = currentRate.ToString("0.00", LocalizationService.CurrentLanguage == "fr"
            ? CultureInfo.GetCultureInfo("fr-CA")
            : CultureInfo.GetCultureInfo("en-US"));
        DataObject.AddPastingHandler(SpeedTextBox, SpeedTextBox_OnPaste);
        Loaded += (_, _) =>
        {
            SpeedTextBox.Focus();
            SpeedTextBox.SelectAll();
        };
    }

    public float Rate { get; private set; }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        var normalized = SpeedTextBox.Text.Trim().Replace(',', '.');
        if (!float.TryParse(normalized, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var rate) || rate is < 0.05f or > 10f)
        {
            ValidationText.Text = LocalizationService.Get("Entrez une vitesse comprise entre 0,05× et 10,00×.");
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        Rate = rate;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SpeedTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character) && character is not ',' and not '.');
    }

    private static void SpeedTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text ||
            text.Any(character => !char.IsDigit(character) && character is not ',' and not '.'))
            e.CancelCommand();
    }

    private void SpeedTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
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
            DialogResult = false;
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
