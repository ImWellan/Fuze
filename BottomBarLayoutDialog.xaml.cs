using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FusePlayer.Services;
using FusePlayer.Models;

namespace FusePlayer;

/// <summary>
/// Fenêtre dédiée à la mise en page de la barre inférieure. PreviewChanged
/// permet à la fenêtre principale de refléter les changements pendant l’édition.
/// </summary>
public partial class BottomBarLayoutDialog : Window
{
    private sealed record CenterLockChoice(string Id, string Label);

    private static readonly (string Id, string Label)[] LayoutItems =
    [
        ("playlist", "File des médias"),
        ("previous", "Média précédent"),
        ("rewind", "Reculer"),
        ("play", "Lecture / pause"),
        ("forward", "Avancer"),
        ("next", "Média suivant"),
        ("title", "Titre du média"),
        ("timeline", "Temps"),
        ("screenshot", "Capture"),
        ("shuffle", "Lecture aléatoire"),
        ("repeat", "Répéter le média"),
        ("audio", "Pistes audio"),
        ("subtitles", "Pistes de sous-titres"),
        ("speed", "Vitesse"),
        ("sync", "Synchronisation"),
        ("pan", "Déplacement de l’écran"),
        ("gear", "Options"),
        ("mute", "Muet"),
        ("volume", "Volume"),
        ("fullscreen", "Plein écran")
    ];

    private readonly ObservableCollection<BottomBarLayoutEditorItem> _leftItems = [];
    private readonly ObservableCollection<BottomBarLayoutEditorItem> _centerItems = [];
    private readonly ObservableCollection<BottomBarLayoutEditorItem> _rightItems = [];
    private readonly ObservableCollection<BottomBarLayoutEditorItem> _availableItems = [];
    private readonly ObservableCollection<BottomBarLayoutPresetData> _presets = [];
    private readonly Dictionary<string, double> _horizontalPositions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<BottomBarLayoutPresetData> _undoHistory = new();
    private readonly Stack<BottomBarLayoutPresetData> _redoHistory = new();
    private string? _centerLockedItemId;
    private BottomBarLayoutPresetData? _pendingExternalEdit;
    private bool _loading;
    private ListBox? _dragSource;
    private Point _dragStart;

    public BottomBarLayoutDialog(
        IReadOnlyList<BottomBarLayoutPresetData> presets,
        string activePresetName)
    {
        InitializeComponent();
        LocalizationService.ApplyToWindow(this);
        LeftListBox.ItemsSource = _leftItems;
        CenterListBox.ItemsSource = _centerItems;
        RightListBox.ItemsSource = _rightItems;
        AvailableItemsComboBox.ItemsSource = _availableItems;
        PresetComboBox.ItemsSource = _presets;
        CenterLockedItemComboBox.ItemsSource = new[]
        {
            new CenterLockChoice(string.Empty, LocalizationService.Get("Aucun bouton verrouillé"))
        }.Concat(LayoutItems.Select(item => new CenterLockChoice(
            item.Id, LocalizationService.Get(item.Label)))).ToArray();

        foreach (var preset in presets)
            _presets.Add(ClonePreset(preset));
        if (_presets.Count == 0)
            _presets.Add(CreateFallbackPreset());

        _loading = true;
        PresetComboBox.SelectedItem = _presets.FirstOrDefault(preset =>
            string.Equals(preset.Name, activePresetName, StringComparison.OrdinalIgnoreCase))
            ?? _presets[0];
        _loading = false;
        LoadPreset(PresetComboBox.SelectedItem as BottomBarLayoutPresetData);
        UpdateHistoryButtons();
    }

    public event Action<BottomBarLayoutPresetData>? PreviewChanged;
    public event Action<bool>? EditingEnabledChanged;
    public event Action<string>? ItemAdded;
    public event Action<bool>? FreeDragModeChanged;

    public IReadOnlyList<BottomBarLayoutPresetData> ResultPresets { get; private set; } = [];
    public string ActivePresetName { get; private set; } = "Fuze — compacte";
    public bool ResultAccepted { get; private set; }
    public bool CanEditActivePreset => true;
    public bool FreeDragModeEnabled => FreeDragModeCheckBox.IsChecked == true;

    private static BottomBarLayoutPresetData CreateFallbackPreset() => new()
    {
        Name = "Fuze — compacte",
        IsBuiltIn = true,
        LeftItems = ["playlist", "previous", "rewind", "play", "forward", "next", "title"],
        CenterItems = ["timeline"],
        RightItems = ["screenshot", "shuffle", "repeat", "audio", "subtitles", "speed", "sync", "pan", "gear", "mute", "volume", "fullscreen"],
        Spacing = 4,
        CenterBarOffset = 10
    };

    private static BottomBarLayoutPresetData ClonePreset(BottomBarLayoutPresetData source) => new()
    {
        Name = source.Name?.Trim() ?? string.Empty,
        IsBuiltIn = IsBuiltInPreset(source),
        LeftItems = [.. source.LeftItems ?? []],
        CenterItems = [.. source.CenterItems ?? []],
        RightItems = [.. source.RightItems ?? []],
        HorizontalPositions = (source.HorizontalPositions ?? new Dictionary<string, double>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value))
            .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0d, 1d),
                StringComparer.OrdinalIgnoreCase),
        Spacing = Math.Clamp(source.Spacing, 0, 24),
        TitleWidth = Math.Clamp(source.TitleWidth, 80, 800),
        CenterBarOffset = Math.Clamp(IsBuiltInPreset(source) && (source.CenterBarOffset is 1 or 4)
            ? 10
            : source.CenterBarOffset, -34, 18),
        HideVerticalGuides = source.HideVerticalGuides,
        HideHorizontalCenterGuide = source.HideHorizontalCenterGuide,
        SplitTimeline = source.SplitTimeline,
        CenterLockedItemId = string.IsNullOrWhiteSpace(source.CenterLockedItemId)
            ? null
            : source.CenterLockedItemId.Trim()
    };

    private static bool IsBuiltInPreset(BottomBarLayoutPresetData preset) =>
        preset.IsBuiltIn ||
        string.Equals(preset.Name, "fuze new", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuse Classic 4", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuze — compacte", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuze — cinéma", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuze — classique", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuse classique", StringComparison.OrdinalIgnoreCase);

    private static BottomBarLayoutEditorItem? FindItem(string id) =>
        LayoutItems.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            is var match && !string.IsNullOrWhiteSpace(match.Id)
            ? new BottomBarLayoutEditorItem(match.Id, LocalizationService.Get(match.Label))
            : null;

    private static void FillItems(ObservableCollection<BottomBarLayoutEditorItem> target,
        IEnumerable<string>? ids)
    {
        if (ids is null)
            return;
        foreach (var id in ids)
        {
            var item = FindItem(id);
            if (item is not null)
                target.Add(item);
        }
    }

    private void LoadPreset(BottomBarLayoutPresetData? preset) => LoadPreset(preset, true);

    private void LoadPreset(BottomBarLayoutPresetData? preset, bool publish)
    {
        if (preset is null)
            return;
        _loading = true;
        _leftItems.Clear();
        _centerItems.Clear();
        _rightItems.Clear();
        _horizontalPositions.Clear();
        FillItems(_leftItems, preset.LeftItems);
        FillItems(_centerItems, preset.CenterItems);
        FillItems(_rightItems, preset.RightItems);
        foreach (var pair in preset.HorizontalPositions ?? new Dictionary<string, double>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value))
                _horizontalPositions[pair.Key] = Math.Clamp(pair.Value, 0d, 1d);
        }
        PresetNameTextBox.Text = string.Empty;
        PresetNameErrorTextBlock.Visibility = Visibility.Collapsed;
        SpacingTextBox.Text = Math.Clamp(preset.Spacing, 0, 24).ToString();
        TitleWidthTextBox.Text = Math.Clamp(preset.TitleWidth, 80, 800).ToString();
        CenterBarOffsetTextBox.Text = Math.Clamp(preset.CenterBarOffset, -34, 18).ToString();
        HideVerticalGuidesCheckBox.IsChecked = preset.HideVerticalGuides;
        HideHorizontalCenterGuideCheckBox.IsChecked = preset.HideHorizontalCenterGuide;
        SplitTimelineCheckBox.IsChecked = preset.SplitTimeline;
        _centerLockedItemId = IsItemOnBar(preset.CenterLockedItemId ?? string.Empty)
            ? preset.CenterLockedItemId
            : null;
        CenterLockedItemComboBox.SelectedValue = _centerLockedItemId ?? string.Empty;
        RefreshAvailableItems();
        _loading = false;
        UpdatePresetEditingState(preset);
        if (publish)
            PublishPreview();
    }

    private void UpdatePresetEditingState(BottomBarLayoutPresetData preset)
    {
        const bool canEdit = true;
        AddAvailableItemButton.IsEnabled = canEdit;
        RemoveAvailableItemButton.IsEnabled = canEdit;
        AvailableItemsComboBox.IsEnabled = canEdit;
        SpacingTextBox.IsEnabled = canEdit;
        TitleWidthTextBox.IsEnabled = canEdit;
        CenterBarOffsetTextBox.IsEnabled = canEdit;
        HideVerticalGuidesCheckBox.IsEnabled = canEdit;
        HideHorizontalCenterGuideCheckBox.IsEnabled = canEdit;
        SplitTimelineCheckBox.IsEnabled = canEdit;
        CenterLockedItemComboBox.IsEnabled = canEdit;
        ClearCenterLockButton.IsEnabled = canEdit && !string.IsNullOrWhiteSpace(_centerLockedItemId);
        SavePresetButton.IsEnabled = canEdit;
        DeletePresetButton.IsEnabled = canEdit;
        BuiltInPresetNotice.Visibility = Visibility.Visible;
        EditingEnabledChanged?.Invoke(canEdit);
    }

    /// <summary>
    /// Synchronise les listes de la fenêtre avec un déplacement réalisé sur
    /// la vraie barre inférieure de la fenêtre principale.
    /// </summary>
    public void UpdateFromExternal(BottomBarLayoutPresetData draft)
    {
        var newName = PresetNameTextBox.Text;
        LoadPreset(draft, false);
        PresetNameTextBox.Text = newName;
    }

    public void BeginExternalEdit(BottomBarLayoutPresetData snapshot)
    {
        _pendingExternalEdit ??= ClonePreset(snapshot);
    }

    public void CommitExternalEdit()
    {
        if (_pendingExternalEdit is null)
            return;

        _undoHistory.Push(ClonePreset(_pendingExternalEdit));
        _pendingExternalEdit = null;
        _redoHistory.Clear();
        UpdateHistoryButtons();
    }

    public void CancelExternalEdit() => _pendingExternalEdit = null;

    private void UpdateHistoryButtons()
    {
        if (UndoLayoutButton is null || RedoLayoutButton is null)
            return;
        UndoLayoutButton.IsEnabled = _undoHistory.Count > 0;
        RedoLayoutButton.IsEnabled = _redoHistory.Count > 0;
    }

    private void ApplyHistorySnapshot(BottomBarLayoutPresetData snapshot)
    {
        var name = PresetNameTextBox.Text;
        LoadPreset(ClonePreset(snapshot), false);
        PresetNameTextBox.Text = name;
        PreviewChanged?.Invoke(ClonePreset(BuildDraft()));
    }

    private void UndoLayout_OnClick(object sender, RoutedEventArgs e)
    {
        if (_undoHistory.Count == 0)
            return;
        _redoHistory.Push(ClonePreset(BuildDraft()));
        ApplyHistorySnapshot(_undoHistory.Pop());
        UpdateHistoryButtons();
    }

    private void RedoLayout_OnClick(object sender, RoutedEventArgs e)
    {
        if (_redoHistory.Count == 0)
            return;
        _undoHistory.Push(ClonePreset(BuildDraft()));
        ApplyHistorySnapshot(_redoHistory.Pop());
        UpdateHistoryButtons();
    }

    private BottomBarLayoutPresetData BuildDraft() => new()
    {
        Name = string.IsNullOrWhiteSpace(PresetNameTextBox.Text)
            ? LocalizationService.Get("Fuze — personnalisé")
            : PresetNameTextBox.Text.Trim(),
        IsBuiltIn = false,
        LeftItems = [.. _leftItems.Select(item => item.Id)],
        CenterItems = [.. _centerItems.Select(item => item.Id)],
        RightItems = [.. _rightItems.Select(item => item.Id)],
        HorizontalPositions = new Dictionary<string, double>(_horizontalPositions,
            StringComparer.OrdinalIgnoreCase),
        Spacing = int.TryParse(SpacingTextBox.Text, out var spacing) ? Math.Clamp(spacing, 0, 24) : 4,
        TitleWidth = int.TryParse(TitleWidthTextBox.Text, out var titleWidth)
            ? Math.Clamp(titleWidth, 80, 800)
            : 230,
        CenterBarOffset = int.TryParse(CenterBarOffsetTextBox.Text, out var centerBarOffset)
            ? Math.Clamp(centerBarOffset, -34, 18)
            : 10,
        HideVerticalGuides = HideVerticalGuidesCheckBox.IsChecked == true,
        HideHorizontalCenterGuide = HideHorizontalCenterGuideCheckBox.IsChecked == true,
        SplitTimeline = SplitTimelineCheckBox.IsChecked == true,
        CenterLockedItemId = string.IsNullOrWhiteSpace(_centerLockedItemId)
            ? null
            : _centerLockedItemId
    };

    private void CaptureDraft(BottomBarLayoutPresetData target)
    {
        var draft = BuildDraft();
        target.Name = draft.Name;
        target.LeftItems = draft.LeftItems;
        target.CenterItems = draft.CenterItems;
        target.RightItems = draft.RightItems;
        target.HorizontalPositions = draft.HorizontalPositions;
        target.Spacing = draft.Spacing;
        target.TitleWidth = draft.TitleWidth;
        target.CenterBarOffset = draft.CenterBarOffset;
        target.HideVerticalGuides = draft.HideVerticalGuides;
        target.HideHorizontalCenterGuide = draft.HideHorizontalCenterGuide;
        target.SplitTimeline = draft.SplitTimeline;
        target.CenterLockedItemId = draft.CenterLockedItemId;
    }

    private void PublishPreview()
    {
        RefreshAvailableItems();
        var draft = BuildDraft();
        PreviewChanged?.Invoke(ClonePreset(draft));
    }

    private void RefreshAvailableItems()
    {
        var selectedId = AvailableItemsComboBox.SelectedValue as string;
        _availableItems.Clear();
        foreach (var item in LayoutItems)
            _availableItems.Add(new BottomBarLayoutEditorItem(item.Id,
                LocalizationService.Get(item.Label)));

        if (!string.IsNullOrWhiteSpace(selectedId))
            AvailableItemsComboBox.SelectedValue = selectedId;
        if (AvailableItemsComboBox.SelectedIndex < 0 && _availableItems.Count > 0)
            AvailableItemsComboBox.SelectedIndex = 0;
    }

    private bool IsItemOnBar(string id) =>
        _leftItems.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ||
        _centerItems.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ||
        _rightItems.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private void AddSelectedAvailableItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditActivePreset || AvailableItemsComboBox.SelectedItem is not BottomBarLayoutEditorItem selected ||
            IsItemOnBar(selected.Id) || FindItem(selected.Id) is not { } item)
            return;

        // Une commande réintégrée est ajoutée à droite; les flèches de la
        // section Lignes permettent ensuite de choisir sa zone exacte.
        _rightItems.Add(item);
        AssignPositionForNewItem(selected.Id);
        ItemAdded?.Invoke(selected.Id);
        PublishPreview();
    }

    private void RemoveSelectedAvailableItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditActivePreset || AvailableItemsComboBox.SelectedItem is not BottomBarLayoutEditorItem selected)
            return;

        var id = selected.Id;

        foreach (var collection in new[] { _leftItems, _centerItems, _rightItems })
        {
            for (var index = collection.Count - 1; index >= 0; index--)
            {
                if (string.Equals(collection[index].Id, id, StringComparison.OrdinalIgnoreCase))
                    collection.RemoveAt(index);
            }
        }
        _horizontalPositions.Remove(id);
        if (string.Equals(id, _centerLockedItemId, StringComparison.OrdinalIgnoreCase))
        {
            _centerLockedItemId = null;
            CenterLockedItemComboBox.SelectedValue = string.Empty;
        }
        if (string.Equals(id, "timeline", StringComparison.OrdinalIgnoreCase))
        {
            _horizontalPositions.Remove("elapsed_time");
            _horizontalPositions.Remove("duration_time");
        }
        PublishPreview();
    }

    private void AssignPositionForNewItem(string id)
    {
        if (string.Equals(id, "timeline", StringComparison.OrdinalIgnoreCase) &&
            SplitTimelineCheckBox.IsChecked == true)
        {
            _horizontalPositions["elapsed_time"] = 0.44;
            _horizontalPositions["duration_time"] = 0.56;
            _horizontalPositions.Remove("timeline");
            return;
        }

        if (_horizontalPositions.ContainsKey(id))
            return;

        var count = _leftItems.Count + _centerItems.Count + _rightItems.Count;
        _horizontalPositions[id] = count <= 1 ? 0.5 : Math.Clamp(0.08 + ((count - 1) * 0.05), 0.08, 0.92);
    }

    private ObservableCollection<BottomBarLayoutEditorItem> GetCollection(ListBox list) =>
        list.Name switch
        {
            nameof(LeftListBox) => _leftItems,
            nameof(CenterListBox) => _centerItems,
            _ => _rightItems
        };

    private IEnumerable<ListBox> GetLists() => [LeftListBox, CenterListBox, RightListBox];

    private void PresetComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        _undoHistory.Clear();
        _redoHistory.Clear();
        _pendingExternalEdit = null;
        UpdateHistoryButtons();
        LoadPreset(PresetComboBox.SelectedItem as BottomBarLayoutPresetData);
    }

    private void PresetNameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (PresetNameErrorTextBlock is not null)
            PresetNameErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void SpacingTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && CanEditActivePreset && int.TryParse(SpacingTextBox.Text, out var spacing) &&
            spacing is >= 0 and <= 24)
            PublishPreview();
    }

    private void TitleWidthTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && CanEditActivePreset &&
            int.TryParse(TitleWidthTextBox.Text, out var width) && width is >= 80 and <= 800)
            PublishPreview();
    }

    private void CenterBarOffsetTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && CanEditActivePreset &&
            int.TryParse(CenterBarOffsetTextBox.Text, out var offset) && offset is >= -34 and <= 18)
            PublishPreview();
    }

    private void HideVerticalGuidesCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading && CanEditActivePreset)
            PublishPreview();
    }

    private void HideHorizontalCenterGuideCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading && CanEditActivePreset)
            PublishPreview();
    }

    private void SplitTimelineCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        // WPF déclenche Checked/Unchecked après avoir changé IsChecked. Le
        // brouillon courant reflète donc déjà le nouvel état; on conserve
        // explicitement l'état inverse pour que Retour puisse restaurer à la
        // fois la case et les positions de ses compteurs.
        var previousSplitState = SplitTimelineCheckBox.IsChecked != true;
        var previousSnapshot = BuildDraft();
        previousSnapshot.SplitTimeline = previousSplitState;
        _undoHistory.Push(ClonePreset(previousSnapshot));
        _redoHistory.Clear();

        if (SplitTimelineCheckBox.IsChecked == true)
        {
            var center = _horizontalPositions.TryGetValue("timeline", out var timelinePosition)
                ? timelinePosition
                : 0.5;
            _horizontalPositions["elapsed_time"] = Math.Clamp(center - 0.06, 0d, 1d);
            _horizontalPositions["duration_time"] = Math.Clamp(center + 0.06, 0d, 1d);
            _horizontalPositions.Remove("timeline");
        }
        else
        {
            var elapsed = _horizontalPositions.TryGetValue("elapsed_time", out var elapsedPosition)
                ? elapsedPosition
                : 0.47;
            var duration = _horizontalPositions.TryGetValue("duration_time", out var durationPosition)
                ? durationPosition
                : 0.53;
            _horizontalPositions["timeline"] = Math.Clamp((elapsed + duration) / 2d, 0d, 1d);
            _horizontalPositions.Remove("elapsed_time");
            _horizontalPositions.Remove("duration_time");
        }

        PublishPreview();
        UpdateHistoryButtons();
    }

    private void FreeDragModeCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            FreeDragModeChanged?.Invoke(FreeDragModeEnabled);
    }

    private void CenterLockedItemComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !CanEditActivePreset)
            return;

        var selectedId = CenterLockedItemComboBox.SelectedValue as string;
        _centerLockedItemId = string.IsNullOrWhiteSpace(selectedId) ||
                              !IsItemOnBar(selectedId)
            ? null
            : selectedId;
        ClearCenterLockButton.IsEnabled = !string.IsNullOrWhiteSpace(_centerLockedItemId);
        PublishPreview();
    }

    private void ClearCenterLockButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditActivePreset)
            return;
        _centerLockedItemId = null;
        _loading = true;
        CenterLockedItemComboBox.SelectedValue = string.Empty;
        _loading = false;
        ClearCenterLockButton.IsEnabled = false;
        PublishPreview();
    }

    private void MoveItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditActivePreset || sender is not Button { Tag: string direction })
            return;
        var list = GetLists().FirstOrDefault(candidate => candidate.SelectedItem is BottomBarLayoutEditorItem);
        if (list is null)
            return;
        var source = GetCollection(list);
        var index = list.SelectedIndex;
        if (index < 0 || index >= source.Count)
            return;
        var target = source;
        if (direction is "left" or "right")
        {
            var listIndex = Array.IndexOf(GetLists().ToArray(), list);
            var targetIndex = direction == "left" ? listIndex - 1 : listIndex + 1;
            if (targetIndex is < 0 or > 2)
                return;
            target = GetCollection(GetLists().ElementAt(targetIndex));
        }
        var item = source[index];
        source.RemoveAt(index);
        if (!ReferenceEquals(source, target))
            target.Add(item);
        else
            source.Insert(Math.Clamp(direction == "up" ? index - 1 : index + 1, 0, source.Count), item);
        PublishPreview();
    }

    private void LayoutList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ListBox selected)
            return;
        foreach (var list in GetLists())
        {
            if (!ReferenceEquals(list, selected))
                list.UnselectAll();
        }
    }

    private void LayoutList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanEditActivePreset || sender is not ListBox list)
            return;
        _dragSource = list;
        _dragStart = e.GetPosition(list);
    }

    private void LayoutList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!CanEditActivePreset || e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list ||
            list.SelectedItem is not BottomBarLayoutEditorItem item || !ReferenceEquals(_dragSource, list))
            return;
        var point = e.GetPosition(list);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        DragDrop.DoDragDrop(list, item, DragDropEffects.Move);
        _dragSource = null;
    }

    private static ListBoxItem? FindItemContainer(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item)
                return item;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void LayoutList_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanEditActivePreset && e.Data.GetDataPresent(typeof(BottomBarLayoutEditorItem))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void LayoutList_OnDrop(object sender, DragEventArgs e)
    {
        if (!CanEditActivePreset || sender is not ListBox targetList || _dragSource is null ||
            !e.Data.GetDataPresent(typeof(BottomBarLayoutEditorItem)) ||
            e.Data.GetData(typeof(BottomBarLayoutEditorItem)) is not BottomBarLayoutEditorItem item)
            return;
        var source = GetCollection(_dragSource);
        var target = GetCollection(targetList);
        source.Remove(item);
        var index = target.Count;
        var container = FindItemContainer(targetList.InputHitTest(e.GetPosition(targetList)) as DependencyObject);
        if (container is not null)
        {
            var hitIndex = targetList.ItemContainerGenerator.IndexFromContainer(container);
            if (hitIndex >= 0)
                index = hitIndex;
        }
        target.Insert(Math.Clamp(index, 0, target.Count), item);
        targetList.SelectedItem = item;
        _dragSource = null;
        PublishPreview();
        e.Handled = true;
    }

    private void SavePreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditActivePreset || PresetComboBox.SelectedItem is not BottomBarLayoutPresetData preset ||
            !int.TryParse(SpacingTextBox.Text, out var spacing) || spacing is < 0 or > 24 ||
            !int.TryParse(CenterBarOffsetTextBox.Text, out var centerBarOffset) || centerBarOffset is < -34 or > 18)
            return;
        CaptureDraft(preset);
        preset.Spacing = spacing;
        preset.CenterBarOffset = centerBarOffset;
        PresetComboBox.Items.Refresh();
        PublishPreview();
    }

    private void SavePresetAs_OnClick(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(PresetNameTextBox.Text)
            ? LocalizationService.Get("Fuze — personnalisé")
            : PresetNameTextBox.Text.Trim();
        var baseName = name;
        var suffix = 2;
        while (_presets.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {suffix++}";
        var preset = BuildDraft();
        preset.Name = name;
        preset.IsBuiltIn = false;
        _presets.Add(preset);
        PresetComboBox.SelectedItem = preset;
    }

    private void DeletePreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not BottomBarLayoutPresetData preset ||
            IsBuiltInPreset(preset) || _presets.Count <= 1)
            return;
        _presets.Remove(preset);
        PresetComboBox.SelectedIndex = 0;
    }

    private void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SpacingTextBox.Text, out var spacing) || spacing is < 0 or > 24 ||
            !int.TryParse(CenterBarOffsetTextBox.Text, out var centerBarOffset) || centerBarOffset is < -34 or > 18)
            return;

        var name = PresetNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || _presets.Any(preset =>
                string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            PresetNameErrorTextBlock.Text = LocalizationService.Get(string.IsNullOrWhiteSpace(name)
                ? "Entrez un nouveau nom pour créer cette interface."
                : "Ce nom existe déjà. Choisissez un autre nom.");
            PresetNameErrorTextBlock.Visibility = Visibility.Visible;
            PresetNameTextBox.Focus();
            PresetNameTextBox.SelectAll();
            return;
        }

        var created = BuildDraft();
        created.Name = name;
        created.IsBuiltIn = false;
        created.Spacing = spacing;
        created.CenterBarOffset = centerBarOffset;
        _presets.Add(created);
        ResultPresets = _presets.Select(ClonePreset).ToArray();
        ActivePresetName = created.Name;
        ResultAccepted = true;
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        ResultAccepted = false;
        Close();
    }

    private void ResizeTopLeft_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        ResizeFromCorner(e.HorizontalChange, e.VerticalChange, fromLeft: true, fromTop: true);

    private void ResizeTopRight_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        ResizeFromCorner(e.HorizontalChange, e.VerticalChange, fromLeft: false, fromTop: true);

    private void ResizeBottomLeft_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        ResizeFromCorner(e.HorizontalChange, e.VerticalChange, fromLeft: true, fromTop: false);

    private void ResizeBottomRight_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) =>
        ResizeFromCorner(e.HorizontalChange, e.VerticalChange, fromLeft: false, fromTop: false);

    private void ResizeFromCorner(double horizontalChange, double verticalChange,
        bool fromLeft, bool fromTop)
    {
        if (WindowState != WindowState.Normal)
            return;

        var currentWidth = Math.Max(MinWidth, ActualWidth);
        var currentHeight = Math.Max(MinHeight, ActualHeight);
        var newWidth = Math.Max(MinWidth, currentWidth + (fromLeft ? -horizontalChange : horizontalChange));
        var newHeight = Math.Max(MinHeight, currentHeight + (fromTop ? -verticalChange : verticalChange));

        if (fromLeft && newWidth != currentWidth)
        {
            var left = double.IsNaN(Left) ? RestoreBounds.Left : Left;
            Left = left + currentWidth - newWidth;
        }
        if (fromTop && newHeight != currentHeight)
        {
            var top = double.IsNaN(Top) ? RestoreBounds.Top : Top;
            Top = top + currentHeight - newHeight;
        }

        Width = newWidth;
        Height = newHeight;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ResultAccepted = false;
            Close();
            e.Handled = true;
        }
    }
}
