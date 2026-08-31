using System.Windows;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public partial class InputDialog : Window
{
    public InputDialog(string initialValue = "https://")
    {
        InitializeComponent();
        LocalizationService.ApplyToWindow(this);
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string Value => ValueTextBox.Text.Trim();

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(Value, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, LocalizationService.Get("L’adresse saisie n’est pas valide."),
                LocalizationService.Get("Flux réseau"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ValueTextBox_OnKeyDown(object sender, KeyEventArgs e)
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
}
