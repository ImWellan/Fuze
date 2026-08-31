using System.Windows;
using System.Windows.Input;
using FusePlayer.Services;
using FusePlayer.Models;

namespace FusePlayer;

public sealed record ShortcutDisplayItem(string Name, string Description, string Gesture);

public partial class ShortcutsDialog : Window
{
    private readonly Dictionary<string, string> _currentShortcuts;
    private readonly int _rewindSeconds;
    private readonly int _forwardSeconds;

    public event EventHandler? ModifyRequested;

    public ShortcutsDialog(IReadOnlyDictionary<string, string>? currentShortcuts,
        int rewindSeconds, int forwardSeconds)
    {
        InitializeComponent();
        _currentShortcuts = ShortcutCatalog.Normalize(currentShortcuts);
        _rewindSeconds = rewindSeconds;
        _forwardSeconds = forwardSeconds;
        RefreshLocalizedContent();
    }

    public void RefreshLocalizedContent()
    {
        LocalizationService.ApplyToWindow(this);
        ShortcutItemsControl.ItemsSource = ShortcutCatalog.Definitions
            .Select(definition => new ShortcutDisplayItem(
                LocalizationService.Get(definition.Name),
                LocalizationService.Get(GetDescription(definition, _rewindSeconds, _forwardSeconds)),
                LocalizationService.Get(ShortcutCatalog.Format(
                    _currentShortcuts.GetValueOrDefault(definition.Id)))))
            .ToArray();
    }

    private static string GetDescription(ShortcutDefinition definition,
        int rewindSeconds, int forwardSeconds) => definition.Id switch
    {
        "seek-back" or "seek-back-secondary" => LocalizationService.Format(
            "Recule de {0} seconde{1}.", rewindSeconds, rewindSeconds == 1 ? "" : "s"),
        "seek-forward" or "seek-forward-secondary" => LocalizationService.Format(
            "Avance de {0} seconde{1}.", forwardSeconds, forwardSeconds == 1 ? "" : "s"),
        _ => definition.Description
    };

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Modify_OnClick(object sender, RoutedEventArgs e)
    {
        ModifyRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
