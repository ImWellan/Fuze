using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FusePlayer.Services;

/// <summary>
/// Lightweight, resource-backed localization for the user interface.
/// Translation is resolved once when a window is created or settings are applied;
/// there is no network call and no per-frame work.
/// </summary>
public static class LocalizationService
{
    private sealed class OriginalTextState
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    }

    private static readonly ResourceManager Resources =
        new("FusePlayer.Resources.Strings", typeof(LocalizationService).Assembly);
    private static readonly ConditionalWeakTable<DependencyObject, OriginalTextState> OriginalValues = new();
    // Les éléments créés dynamiquement reçoivent souvent déjà le texte traduit
    // via Get(). Conserver le lien sortie → source permet de les retraduire
    // lorsque la langue change sans devoir recréer toute la fenêtre.
    private static readonly Dictionary<string, string> TranslatedSources =
        new(StringComparer.Ordinal);
    private static readonly object TranslationSourcesLock = new();

    public static string CurrentLanguage { get; private set; } = "en";
    public static event EventHandler? LanguageChanged;

    public static CultureInfo CurrentCulture =>
        CultureInfo.GetCultureInfo(CurrentLanguage);

    public static void SetLanguage(string? language)
    {
        var normalized = string.Equals(language?.Trim(), "fr",
            StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        if (string.Equals(CurrentLanguage, normalized, StringComparison.Ordinal))
            return;

        CurrentLanguage = normalized;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        var value = TryGet(source, out var translated) ? translated : source;
        if (!string.Equals(value, source, StringComparison.Ordinal))
        {
            lock (TranslationSourcesLock)
                TranslatedSources[value] = source;
        }

        return value;
    }

    /// <summary>
    /// Resolves a localized format string and applies its arguments. Keeping
    /// formatting here means dynamically-created menu entries and toasts use
    /// the same resource path as their static XAML counterparts.
    /// </summary>
    public static string Format(string source, params object?[] args)
    {
        var template = Get(source);
        return string.Format(CurrentCulture, template, args);
    }

    private static bool TryGet(string source, out string value)
    {
        try
        {
            var key = GetResourceKey(source);
            // Do not let the neutral English resource leak into a French
            // window. Missing French entries intentionally keep their source
            // text, which also makes switching back from English reversible.
            value = CurrentLanguage == "fr"
                ? Resources.GetResourceSet(CurrentCulture, createIfNotExists: true,
                    tryParents: false)?.GetString(key, ignoreCase: false) ?? string.Empty
                : Resources.GetString(key, CurrentCulture) ?? string.Empty;
            return value.Length > 0;
        }
        catch (MissingManifestResourceException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static void Translate(DependencyObject element, string property,
        string current, Action<string> setter)
    {
        var state = OriginalValues.GetOrCreateValue(element);
        if (!state.Values.TryGetValue(property, out var source))
        {
            source = ResolveSource(current) ?? current;
            if (!TryGet(source, out _))
                return;
            state.Values[property] = source;
        }

        if (TryGet(source, out var translated))
        {
            if (!string.Equals(current, translated, StringComparison.Ordinal))
                setter(translated);
        }
        else if (CurrentLanguage == "fr" && !string.Equals(current, source,
                     StringComparison.Ordinal))
        {
            // The French resource deliberately contains only entries that
            // need a distinction from the original French UI.
            setter(source);
        }
    }

    private static string? ResolveSource(string current)
    {
        lock (TranslationSourcesLock)
            return TranslatedSources.TryGetValue(current, out var source) ? source : null;
    }

    private static string GetResourceKey(string source) => source switch
    {
        "PARAMÈTRES" => "section_settings",
        "GÉNÉRAL" => "section_general",
        "MÉDIA" => "section_media",
        "SYSTÈME" => "section_system",
        "INTERFACE" => "section_interface",
        "VIDÉO" => "section_video",
        "AUDIO" => "section_audio",
        "SOUS-TITRES" => "section_subtitles",
        "FILE DES MÉDIAS" => "section_media_queue",
        "MODE AUDIO ADAPTATIF" => "section_adaptive_audio",
        "MISE EN PAGE DE LA BARRE INFÉRIEURE" => "section_bottom_bar_layout",
        "PÉRIPHÉRIQUE DE SORTIE" => "section_output_device",
        "RACCOURCIS" => "section_shortcuts",
        "RACCOURCIS CLAVIER" => "section_keyboard_shortcuts",
        "CAPTURES D’ÉCRAN" => "section_screenshots",
        _ => source
    };

    public static void ApplyToWindow(Window window)
    {
        ApplyToElement(window);
    }

    /// <summary>
    /// Applies localization to a Menu and every MenuItem in its item
    /// hierarchy.  MenuItem children are generated through the Items
    /// collection and are not always exposed by WPF's logical/visual tree
    /// traversal (especially while a popup submenu is closed), so they need
    /// an explicit pass when the language changes.
    /// </summary>
    public static void ApplyToMenuHierarchy(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            ApplyToMenuItem(item);
    }

    private static void ApplyToMenuItem(MenuItem item)
    {
        ApplyProperties(item);

        foreach (var child in item.Items.OfType<MenuItem>())
            ApplyToMenuItem(child);
    }

    public static void ApplyToElement(DependencyObject root)
    {
        ApplyProperties(root);

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyObject)
                ApplyToElement(dependencyObject);
        }

        // Some templated controls (notably menu and combo-box items) are only
        // present in the visual tree, so include those children as well.
        // FrameworkContentElement instances such as Run do not expose a
        // VisualTreeHelper child collection.
        if (root is not Visual && root is not Visual3D)
            return;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is not null && !IsLogicalChild(root, child))
                ApplyToElement(child);
        }
    }

    private static bool IsLogicalChild(DependencyObject parent, DependencyObject child)
    {
        foreach (var logicalChild in LogicalTreeHelper.GetChildren(parent))
        {
            if (ReferenceEquals(logicalChild, child))
                return true;
        }

        return false;
    }

    private static void ApplyProperties(DependencyObject element)
    {
        switch (element)
        {
            case Window window when !string.IsNullOrWhiteSpace(window.Title):
                Translate(window, "Title", window.Title, value => window.Title = value);
                break;
            case TextBlock textBlock:
                if (!BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty))
                    Translate(textBlock, "Text", textBlock.Text, value => textBlock.Text = value);
                // Traduire un Run ou un Hyperlink peut modifier la collection
                // d'inlines du TextBlock. Prendre une copie avant la boucle
                // évite l'InvalidOperationException « collection modifiée »
                // observée à l'ouverture de la fenêtre À propos de Fuze.
                var inlines = textBlock.Inlines.OfType<DependencyObject>().ToArray();
                foreach (var inline in inlines)
                    ApplyToElement(inline);
                break;
            case Run run:
                if (!BindingOperations.IsDataBound(run, Run.TextProperty))
                    Translate(run, "Text", run.Text, value => run.Text = value);
                break;
            case Label label when label.Content is string content &&
                                  !BindingOperations.IsDataBound(label, ContentControl.ContentProperty):
                Translate(label, "Content", content, value => label.Content = value);
                break;
            case Button button when button.Content is string content &&
                                   !BindingOperations.IsDataBound(button, ContentControl.ContentProperty):
                Translate(button, "Content", content, value => button.Content = value);
                break;
            case CheckBox checkBox when checkBox.Content is string content &&
                                      !BindingOperations.IsDataBound(checkBox, ContentControl.ContentProperty):
                Translate(checkBox, "Content", content, value => checkBox.Content = value);
                break;
            case MenuItem menuItem when menuItem.Header is string header &&
                                        !BindingOperations.IsDataBound(menuItem, HeaderedItemsControl.HeaderProperty):
                Translate(menuItem, "Header", header, value => menuItem.Header = value);
                if (!string.IsNullOrWhiteSpace(menuItem.InputGestureText) &&
                    !BindingOperations.IsDataBound(menuItem, MenuItem.InputGestureTextProperty))
                    Translate(menuItem, "InputGestureText", menuItem.InputGestureText,
                        value => menuItem.InputGestureText = value);
                break;
            case TabItem tabItem when tabItem.Header is string header &&
                                      !BindingOperations.IsDataBound(tabItem, HeaderedContentControl.HeaderProperty):
                Translate(tabItem, "Header", header, value => tabItem.Header = value);
                break;
            case ComboBoxItem comboBoxItem when comboBoxItem.Content is string content &&
                                               !BindingOperations.IsDataBound(comboBoxItem, ContentControl.ContentProperty):
                Translate(comboBoxItem, "Content", content, value => comboBoxItem.Content = value);
                break;
            case ListBoxItem listBoxItem when listBoxItem.Content is string content &&
                                             !BindingOperations.IsDataBound(listBoxItem, ContentControl.ContentProperty):
                Translate(listBoxItem, "Content", content, value => listBoxItem.Content = value);
                break;
            case RadioButton radioButton when radioButton.Content is string content &&
                                             !BindingOperations.IsDataBound(radioButton, ContentControl.ContentProperty):
                Translate(radioButton, "Content", content, value => radioButton.Content = value);
                break;
        }

        if (ToolTipService.GetToolTip(element) is string toolTip &&
            !BindingOperations.IsDataBound(element, ToolTipService.ToolTipProperty))
            Translate(element, "ToolTip", toolTip,
                value => ToolTipService.SetToolTip(element, value));

        var automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(automationName) &&
            !BindingOperations.IsDataBound(element, AutomationProperties.NameProperty))
            Translate(element, "AutomationName", automationName,
                value => AutomationProperties.SetName(element, value));
    }
}
