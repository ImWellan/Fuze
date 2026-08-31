using System.Windows;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public partial class ConfirmCloseDialog : Window
{
    public ConfirmCloseDialog()
        : this("Fermer Fuse", "Fermer Fuse ?",
            "La lecture en cours sera arrêtée et la session sera enregistrée.",
            "Fermer Fuse")
    {
    }

    public ConfirmCloseDialog(string title, string question, string description,
        string confirmLabel)
    {
        InitializeComponent();
        Title = LocalizationService.Get(title);
        DialogTitleTextBlock.Text = LocalizationService.Get(title);
        DialogQuestionTextBlock.Text = LocalizationService.Get(question);
        DialogDescriptionTextBlock.Text = LocalizationService.Get(description);
        DialogConfirmButton.Content = LocalizationService.Get(confirmLabel);
        LocalizationService.ApplyToWindow(this);
    }

    public bool Confirmed { get; private set; }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm_OnClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_OnClick(sender, e);
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
