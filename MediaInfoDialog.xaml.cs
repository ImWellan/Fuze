using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FusePlayer.Services;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FusePlayer;

public partial class MediaInfoDialog : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HitLeft = 10;
    private const int HitRight = 11;
    private const int HitTop = 12;
    private const int HitTopLeft = 13;
    private const int HitTopRight = 14;
    private const int HitBottom = 15;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;

    private HwndSource? _windowSource;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private sealed record InformationSection(string Key, string Header,
        List<string> Lines, bool HasNumberedItems);

    private sealed record InformationItem(int Number, List<string> Lines);

    public MediaInfoDialog(string title, string information,
        string heading = "Informations sur le média", bool compact = false)
    {
        InitializeComponent();
        Title = LocalizationService.Get(heading);
        DialogHeadingText.Text = LocalizationService.Get(heading);
        MediaTitleText.Text = title;
        if (compact)
        {
            Width = 560;
            Height = 410;
            MinWidth = 480;
            MinHeight = 340;
        }
        BuildInformationSections(information);
        LocalizationService.ApplyToWindow(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProcedure);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowProcedure);
            _windowSource = null;
        }

        base.OnClosed(e);
    }

    private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest || WindowState != WindowState.Normal ||
            !GetCursorPos(out var cursor) || !GetWindowRect(window, out var bounds))
            return IntPtr.Zero;

        var dpi = GetDpiForWindow(window);
        if (dpi == 0)
            dpi = 96;

        var edge = Math.Max(4, (int)Math.Ceiling(4 * dpi / 96d));
        var corner = Math.Max(edge, (int)Math.Ceiling(12 * dpi / 96d));
        var onLeft = cursor.X >= bounds.Left && cursor.X < bounds.Left + edge;
        var onRight = cursor.X < bounds.Right && cursor.X >= bounds.Right - edge;
        var onTop = cursor.Y >= bounds.Top && cursor.Y < bounds.Top + edge;
        var onBottom = cursor.Y < bounds.Bottom && cursor.Y >= bounds.Bottom - edge;
        var nearLeft = cursor.X < bounds.Left + corner;
        var nearRight = cursor.X >= bounds.Right - corner;
        var nearTop = cursor.Y < bounds.Top + corner;
        var nearBottom = cursor.Y >= bounds.Bottom - corner;

        int hit;
        if ((onTop && nearLeft) || (onLeft && nearTop))
            hit = HitTopLeft;
        else if ((onTop && nearRight) || (onRight && nearTop))
            hit = HitTopRight;
        else if ((onBottom && nearLeft) || (onLeft && nearBottom))
            hit = HitBottomLeft;
        else if ((onBottom && nearRight) || (onRight && nearBottom))
            hit = HitBottomRight;
        else if (onLeft)
            hit = HitLeft;
        else if (onRight)
            hit = HitRight;
        else if (onTop)
            hit = HitTop;
        else if (onBottom)
            hit = HitBottom;
        else
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(hit);
    }

    private void BuildInformationSections(string information)
    {
        InformationSectionsPanel.Children.Clear();
        foreach (var section in ParseSections(information))
        {
            var expander = CreateExpander(section.Header, $"section.{section.Key}",
                "MediaInfoSectionExpander", section.Key != "tags");

            AttachLazyContent(expander, section.HasNumberedItems
                ? () => CreateNumberedItems(section)
                : () => CreateInformationLines(section.Lines));

            InformationSectionsPanel.Children.Add(expander);
        }
    }

    private FrameworkElement CreateNumberedItems(InformationSection section)
    {
        var itemsPanel = new StackPanel();
        foreach (var item in ParseItems(section.Lines))
        {
            // L'état des pistes et chapitres ne persiste pas entre les médias.
            // Seul l'état de leur catégorie principale est mémorisé.
            var itemExpander = CreateTransientExpander(item.Number.ToString(),
                "MediaInfoItemExpander", true);
            AttachLazyContent(itemExpander, () => CreateInformationLines(item.Lines));
            itemsPanel.Children.Add(itemExpander);
        }
        return itemsPanel;
    }

    private Expander CreateExpander(string header, string stateKey, string styleKey,
        bool defaultExpanded)
    {
        var expander = new Expander
        {
            Header = header,
            Style = (Style)FindResource(styleKey),
            IsExpanded = MediaInfoExpansionStore.Get(stateKey, defaultExpanded)
        };
        expander.Expanded += (_, _) => MediaInfoExpansionStore.Set(stateKey, true);
        expander.Collapsed += (_, _) => MediaInfoExpansionStore.Set(stateKey, false);
        return expander;
    }

    private Expander CreateTransientExpander(string header, string styleKey,
        bool defaultExpanded) => new()
    {
        Header = header,
        Style = (Style)FindResource(styleKey),
        IsExpanded = defaultExpanded
    };

    private static void AttachLazyContent(Expander expander,
        Func<FrameworkElement> contentFactory)
    {
        var contentCreated = false;
        void EnsureContent()
        {
            if (contentCreated)
                return;
            contentCreated = true;
            expander.Content = contentFactory();
        }

        expander.Expanded += (_, _) => EnsureContent();
        if (expander.IsExpanded)
            EnsureContent();
    }

    private static List<InformationSection> ParseSections(string information)
    {
        var sections = new List<InformationSection>();
        InformationSection? current = null;
        foreach (var rawLine in information.Replace("\r\n", "\n").Split('\n'))
        {
            if (TryGetSection(rawLine.Trim(), out var key, out var header, out var hasItems))
            {
                current = new InformationSection(key, header, [], hasItems);
                sections.Add(current);
                continue;
            }

            if (current is not null)
                current.Lines.Add(rawLine);
        }

        foreach (var section in sections)
            TrimBlankLines(section.Lines);
        return sections;
    }

    private static bool TryGetSection(string line, out string key, out string header,
        out bool hasItems)
    {
        key = string.Empty;
        header = string.Empty;
        hasItems = false;
        if (line.Equals("GÉNÉRAL", StringComparison.OrdinalIgnoreCase))
        {
            key = "general";
            header = LocalizationService.Get("Général");
            return true;
        }

        var separator = line.IndexOf('—');
        var category = (separator >= 0 ? line[..separator] : line).Trim();
        var suffix = separator >= 0 ? line[separator..].Trim() : string.Empty;
        switch (category.ToUpperInvariant())
        {
            case "VIDÉO":
                key = "video";
                header = JoinHeader("Vidéo", suffix);
                hasItems = true;
                return true;
            case "AUDIO":
                key = "audio";
                header = JoinHeader("Audio", suffix);
                hasItems = true;
                return true;
            case "SOUS-TITRES":
                key = "subtitles";
                header = JoinHeader("Sous-titres", suffix);
                hasItems = true;
                return true;
            case "CHAPITRES":
                key = "chapters";
                header = JoinHeader("Chapitres", suffix);
                hasItems = true;
                return true;
            case "BALISES":
                key = "tags";
                header = JoinHeader("Balises", suffix);
                return true;
            default:
                return false;
        }
    }

    private static string JoinHeader(string name, string suffix) =>
        string.IsNullOrWhiteSpace(suffix)
            ? LocalizationService.Get(name)
            : $"{LocalizationService.Get(name)} {suffix}";

    private static List<InformationItem> ParseItems(List<string> lines)
    {
        var items = new List<InformationItem>();
        InformationItem? current = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith('.') &&
                int.TryParse(trimmed[..^1], out var number))
            {
                current = new InformationItem(number, []);
                items.Add(current);
                continue;
            }

            if (current is not null)
                current.Lines.Add(line);
        }

        foreach (var item in items)
            TrimBlankLines(item.Lines);
        return items;
    }

    private static void TrimBlankLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
    }

    private static FrameworkElement CreateInformationLines(IEnumerable<string> lines)
    {
        var materializedLines = lines.ToArray();
        var preferCanadianFrench = materializedLines.Any(line =>
            line.Contains("VFQ", StringComparison.OrdinalIgnoreCase));
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var line in materializedLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                panel.Children.Add(new Border { Height = 7 });
                continue;
            }

            panel.Children.Add(CreateInformationLine(line, preferCanadianFrench));
        }
        return panel;
    }

    private static FrameworkElement CreateInformationLine(string line,
        bool preferCanadianFrench)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
            return CreateTextLine(line);

        // Garder le libellé et la valeur côte à côte. Les deux cellules sont
        // alignées en haut afin qu'une valeur qui revient sur deux lignes ne
        // soit pas centrée verticalement par rapport au libellé.
        var row = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 5)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = CreateTextLine(line[..(colon + 1)].TrimEnd());
        label.TextWrapping = TextWrapping.NoWrap;
        label.Foreground = new SolidColorBrush(Color.FromRgb(167, 173, 182));
        label.FontWeight = FontWeights.SemiBold;
        label.VerticalAlignment = VerticalAlignment.Top;
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.TextAlignment = TextAlignment.Left;
        label.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var value = line[(colon + 1)..].TrimStart();
        FrameworkElement valueElement = CreateTextLine(value);
        if (line.TrimStart().StartsWith("Langue", StringComparison.OrdinalIgnoreCase) ||
            line.TrimStart().StartsWith("Language", StringComparison.OrdinalIgnoreCase))
            valueElement = CreateLanguageValue(value, preferCanadianFrench) ?? valueElement;

        valueElement.VerticalAlignment = VerticalAlignment.Top;
        valueElement.HorizontalAlignment = HorizontalAlignment.Left;
        if (valueElement is TextBlock valueText)
            valueText.TextAlignment = TextAlignment.Left;
        valueElement.Margin = new Thickness(0);
        Grid.SetColumn(valueElement, 1);
        row.Children.Add(valueElement);
        return row;
    }

    private static FrameworkElement? CreateLanguageValue(string value,
        bool preferCanadianFrench)
    {
        var openingParenthesis = value.LastIndexOf('(');
        var closingParenthesis = value.LastIndexOf(')');
        if (openingParenthesis < 0 || closingParenthesis <= openingParenthesis)
            return null;

        var code = value[(openingParenthesis + 1)..closingParenthesis].Trim();
        var flag = CreateLanguageFlag(code, preferCanadianFrench);
        if (flag is null)
            return null;

        var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        flag.Margin = new Thickness(1, 2, 6, 0);
        Grid.SetColumn(flag, 0);
        row.Children.Add(flag);
        var text = CreateTextLine(value);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static TextBlock CreateTextLine(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(241, 242, 244)),
        FontFamily = new FontFamily("Cascadia Mono"),
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static FrameworkElement? CreateLanguageFlag(string code,
        bool preferCanadianFrench)
    {
        var normalized = code.Trim().Replace('_', '-').ToLowerInvariant();
        var language = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        var country = normalized switch
        {
            "fr-ca" or "en-ca" => "ca",
            "fr-fr" => "fr",
            "en-us" => "us",
            "en-gb" => "us",
            _ => language switch
            {
                "fr" or "fre" or "fra" => preferCanadianFrench ? "ca" : "fr",
                "en" or "eng" => "us",
                "ja" or "jpn" => "jp",
                "es" or "spa" => "es",
                "de" or "ger" or "deu" => "de",
                "it" or "ita" => "it",
                "pt" or "por" => "pt",
                "ko" or "kor" => "kr",
                "zh" or "chi" or "zho" => "cn",
                _ => string.Empty
            }
        };
        if (string.IsNullOrEmpty(country))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri($"pack://application:,,,/Assets/Flags/{country}.png",
                UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return new Border
            {
                Width = 20,
                Height = 15,
                CornerRadius = new CornerRadius(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(0.5),
                ClipToBounds = true,
                Child = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.UniformToFill,
                    SnapsToDevicePixels = true
                }
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                          UriFormatException)
        {
            return null;
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            e.Handled = true;
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}

internal static class MediaInfoExpansionStore
{
    private static readonly object Sync = new();
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fuze", "media-info-expansion.json");
    private static readonly Dictionary<string, bool> States = Load();

    public static bool Get(string key, bool defaultValue)
    {
        lock (Sync)
            return States.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public static void Set(string key, bool value)
    {
        lock (Sync)
        {
            if (States.TryGetValue(key, out var current) && current == value)
                return;
            States[key] = value;
            Save();
        }
    }

    private static Dictionary<string, bool> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath)) ??
                   new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(States,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // L'interface reste utilisable même si le profil Windows est en lecture seule.
        }
    }
}
