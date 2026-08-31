using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public partial class GoToTimeDialog : Window
{
    private readonly long _maximumMilliseconds;

    public GoToTimeDialog(long initialMilliseconds, long maximumMilliseconds)
    {
        InitializeComponent();
        LocalizationService.ApplyToWindow(this);
        _maximumMilliseconds = Math.Max(0, maximumMilliseconds);
        SetFields(Math.Clamp(initialMilliseconds, 0,
            _maximumMilliseconds > 0 ? _maximumMilliseconds : long.MaxValue));
        DurationHintText.Text = _maximumMilliseconds > 0
            ? LocalizationService.Format("Durée du média : {0}", FormatPosition(_maximumMilliseconds))
            : LocalizationService.Get("La durée totale du média n’est pas encore disponible.");

        foreach (var textBox in new[]
                 {
                     HoursTextBox, MinutesTextBox, SecondsTextBox, MillisecondsTextBox
                 })
        {
            DataObject.AddPastingHandler(textBox, NumericTextBox_OnPaste);
        }

        Loaded += (_, _) =>
        {
            HoursTextBox.Focus();
            HoursTextBox.SelectAll();
        };
    }

    public long TargetMilliseconds { get; private set; }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadTarget(out var target, out var error))
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        TargetMilliseconds = target;
        DialogResult = true;
    }

    private bool TryReadTarget(out long target, out string error)
    {
        target = 0;
        error = string.Empty;
        if (!TryReadNumber(HoursTextBox, out var hours) ||
            !TryReadNumber(MinutesTextBox, out var minutes) ||
            !TryReadNumber(SecondsTextBox, out var seconds) ||
            !TryReadNumber(MillisecondsTextBox, out var milliseconds))
        {
            error = LocalizationService.Get("Utilisez uniquement des nombres entiers.");
            return false;
        }

        if (minutes > 59 || seconds > 59 || milliseconds > 999)
        {
            error = LocalizationService.Get(
                "Les minutes et secondes vont de 0 à 59; les millisecondes de 0 à 999.");
            return false;
        }

        try
        {
            target = checked((((hours * 60) + minutes) * 60 + seconds) * 1000 + milliseconds);
        }
        catch (OverflowException)
        {
            error = LocalizationService.Get("Cette position est trop grande.");
            return false;
        }

        if (_maximumMilliseconds > 0 && target > _maximumMilliseconds)
        {
            error = LocalizationService.Format("Cette position dépasse la durée du média ({0}).",
                FormatPosition(_maximumMilliseconds));
            return false;
        }

        return true;
    }

    private static bool TryReadNumber(TextBox textBox, out long value) =>
        string.IsNullOrWhiteSpace(textBox.Text)
            ? (value = 0) == 0
            : long.TryParse(textBox.Text, out value) && value >= 0;

    private void SetFields(long milliseconds)
    {
        var total = Math.Max(0, milliseconds);
        var hours = total / 3_600_000;
        total %= 3_600_000;
        var minutes = total / 60_000;
        total %= 60_000;
        var seconds = total / 1000;
        var remainingMilliseconds = total % 1000;

        HoursTextBox.Text = hours.ToString("00");
        MinutesTextBox.Text = minutes.ToString("00");
        SecondsTextBox.Text = seconds.ToString("00");
        MillisecondsTextBox.Text = remainingMilliseconds.ToString("000");
    }

    private static string FormatPosition(long milliseconds)
    {
        var total = Math.Max(0, milliseconds);
        var hours = total / 3_600_000;
        total %= 3_600_000;
        var minutes = total / 60_000;
        total %= 60_000;
        var seconds = total / 1000;
        return $"{hours:00}:{minutes:00}:{seconds:00}.{total % 1000:000}";
    }

    private void Reset_OnClick(object sender, RoutedEventArgs e)
    {
        SetFields(0);
        ValidationText.Visibility = Visibility.Collapsed;
        HoursTextBox.Focus();
        HoursTextBox.SelectAll();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void NumericTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private static void NumericTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text ||
            text.Any(character => !char.IsDigit(character)))
            e.CancelCommand();
    }

    private void NumericTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
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
