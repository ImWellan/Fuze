using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FusePlayer.Models;
using FusePlayer.Playback;
using FusePlayer.Services;
using Microsoft.Win32;

namespace FusePlayer;

public sealed class PriorityTitleItem(string title) : INotifyPropertyChanged
{
    public string Title { get; set; } = title;

    private bool _isDragging;
    public bool IsDragging
    {
        get => _isDragging;
        set
        {
            if (_isDragging == value)
                return;

            _isDragging = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragging)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class BottomBarLayoutEditorItem(string id, string sourceLabel) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public string SourceLabel { get; } = sourceLabel;

    private string _label = LocalizationService.Get(sourceLabel);
    public string Label => _label;

    public void RefreshLocalization()
    {
        var label = LocalizationService.Get(SourceLabel);
        if (string.Equals(_label, label, StringComparison.Ordinal))
            return;

        _label = label;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ShortcutBindingItem(ShortcutDefinition definition, string encodedGesture) :
    INotifyPropertyChanged
{
    private readonly ShortcutDefinition _definition = definition;
    public string Id => _definition.Id;

    private string _name = LocalizationService.Get(definition.Name);
    public string Name => _name;

    private string _description = LocalizationService.Get(definition.Description);
    public string Description => _description;

    public string DefaultGesture { get; } = ShortcutCatalog.Encode(
        definition.DefaultKey, definition.DefaultModifiers);

    private string _encodedGesture = encodedGesture;
    public string EncodedGesture
    {
        get => _encodedGesture;
        set
        {
            if (string.Equals(_encodedGesture, value, StringComparison.Ordinal))
                return;
            _encodedGesture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncodedGesture)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayGesture)));
        }
    }

    public string DisplayGesture => LocalizationService.Get(ShortcutCatalog.Format(EncodedGesture));

    public void RefreshLocalization()
    {
        var name = LocalizationService.Get(_definition.Name);
        var description = LocalizationService.Get(_definition.Description);
        if (!string.Equals(_name, name, StringComparison.Ordinal))
        {
            _name = name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }

        if (!string.Equals(_description, description, StringComparison.Ordinal))
        {
            _description = description;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayGesture)));
    }

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               Description.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               DisplayGesture.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record SubtitleSettingsSnapshot(
    bool StartupTitleOverlayEnabled,
    bool PreferOriginalTitleForStartup,
    string StartupTitlePosition,
    int StartupTitleDelayMilliseconds,
    int StartupTitleDurationMilliseconds,
    string StartupTitleFont,
    int StartupTitleFontSize,
    string StartupTitleTextColor,
    string StartupTitleBorderColor,
    double StartupTitleBorderSize,
    bool StartupTitleShadow,
    int StartupTitleMarginX,
    int StartupTitleMarginY,
    bool StartupTitleScaleWithWindow,
    bool AutoSelectPreferredSubtitle,
    string PreferredSubtitleProfile,
    IReadOnlyList<string> PreferredSubtitleTitlePriorities,
    bool PreferSdhSubtitles,
    bool DisableSubtitlesByDefault,
    bool AutoLoadExternalSubtitles,
    string SubtitleEncoding,
    string SubtitleFont,
    int SubtitleFontSize,
    string SubtitleTextColor,
    string SubtitleBorderColor,
    double SubtitleBorderSize,
    bool SubtitleShadow,
    bool SubtitleForcePosition,
    string SubtitlePosition,
    int SubtitleMarginX,
    int SubtitleMarginY,
    bool SubtitleScaleWithWindow);

public sealed record VideoDisplayDescription(
    string Id,
    string Description,
    string NumberLabel = "",
    string FriendlyName = "",
    string Details = "");

public sealed record PlaybackSettingsSnapshot(
    bool ResumePlayback,
    int ResumePromptStartSkipPercent,
    int ResumePromptEndSkipPercent,
    bool AutoPlayOnOpen,
    bool ConfirmClose,
    bool BufferingEnabled,
    bool PreventSleepDuringPlayback,
    bool RememberMediaSettings,
    int RecentMediaRetentionDays,
    int RecentMediaFolderDepth,
    int PlaylistFolderDepth,
    bool RepeatPlaylist,
    bool EnhancedPlaybackEnabled,
    bool EnhancedFolderAdvanceEnabled,
    bool EnhancedFolderShowNameEnabled,
    bool ShowEnhancedUpcomingInPlaylist,
    bool ShowEnhancedNextFolderInPlaylist,
    bool FileAssociationsEnabled,
    IReadOnlyList<string> FileAssociationExtensions,
    IReadOnlyList<CustomFileAssociationData> CustomFileAssociationTypes);

public sealed record InterfaceSettingsSnapshot(
    int TopBarAutoHideDelayMilliseconds,
    int BottomBarAutoHideDelayMilliseconds,
    int PlaylistScrollSpeed,
    int VolumeControlStyle,
    int VolumePopupHideDelayMilliseconds,
    int VolumeIndicatorHideDelayMilliseconds,
    bool HideInterfaceOnVideoStart,
    bool ShowSynchronizationButton,
    bool ShowShuffleButton,
    bool ShowRepeatButton,
    bool ShowSpeedButton,
    bool ShowPlaylistButton,
    bool ShowAdditionalMediaInformation,
    bool AutoCompactMissingBottomBarItems,
    bool ShufflePlayback,
    bool RepeatPlayback,
    bool ShowScreenshotButton,
    bool AdaptiveInterfaceScale,
    bool AutoHideCursor,
    int CursorAutoHideDelayMilliseconds,
    bool AlwaysOnTop,
    bool ShowOsd,
    bool DiscordActivityEnabled,
    bool DiagnosticLoggingEnabled,
    bool DisableToolTips,
    string InterfaceLanguage,
    bool TogglePlaybackOnSingleClick,
    bool ToggleFullscreenOnDoubleClick,
    IReadOnlyList<BottomBarLayoutPresetData> BottomBarLayoutPresets,
    string ActiveBottomBarLayoutPreset,
    bool ShowChapterNameInSeekPreview = true,
    bool ShowVideoPanButton = false);

public sealed record VideoSettingsSnapshot(
    bool StartFullscreen,
    string PreferredDisplay,
    string VideoOutput,
    bool HardwareDecoding,
    bool Deinterlacing,
    string HdrMode,
    string ScreenshotBaseDirectory,
    string ScreenshotFolderName,
    string ScreenshotFormat,
    string ScreenshotAffixMode,
    string ScreenshotAffixText,
    bool ScreenshotSequentialNumbering,
    bool CopyScreenshotsToClipboard,
    int CustomZoomPercent = 100,
    string CustomAspectRatio = "16:9");

public sealed record ShortcutSettingsSnapshot(
    IReadOnlyDictionary<string, string> KeyboardShortcuts,
    bool MouseWheelTimelineEnabled,
    bool MouseWheelVolumeEnabled,
    bool CenterWheelVolumeEnabled,
    bool CenterWheelTimelineEnabled,
    bool MouseWheelAudioTracksEnabled,
    bool MouseWheelSubtitleTracksEnabled,
    bool IgnoreKeyboardVolumeButtons);

public partial class SettingsDialog : Window
{
    private static string DiagnosticLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Fuze", "diagnostic.log");
    private const string AdaptiveAudioDeviceTag = "__adaptive_audio__";

    private enum EditTarget
    {
        None,
        Title,
        Subtitle
    }

    private sealed class StyleDraft
    {
        public string Font { get; set; } = "Arial";
        public int FontSize { get; set; } = 42;
        public string TextColor { get; set; } = "#FFFFFFFF";
        public string BorderColor { get; set; } = "#FF000000";
        public double BorderSize { get; set; } = 2.5;
        public bool Shadow { get; set; } = true;
        public bool ForcePosition { get; set; }
        public string Position { get; set; } = "bottom-center";
        public int MarginX { get; set; } = 20;
        public int MarginY { get; set; } = 36;
        public bool ScaleWithWindow { get; set; } = true;
    }

    private readonly ObservableCollection<PriorityTitleItem> _audioPriorityItems = [];
    private readonly ObservableCollection<PriorityTitleItem> _subtitlePriorityItems = [];
    private readonly ObservableCollection<BottomBarLayoutEditorItem> _bottomBarLeftItems = [];
    private readonly ObservableCollection<BottomBarLayoutEditorItem> _bottomBarCenterItems = [];
    private readonly ObservableCollection<BottomBarLayoutEditorItem> _bottomBarRightItems = [];
    private readonly ObservableCollection<BottomBarLayoutPresetData> _bottomBarPresets = [];
    private readonly ObservableCollection<ShortcutBindingItem> _shortcutItems = [];
    private readonly List<CustomFileAssociationData> _customFileAssociationTypes = [];
    private readonly Dictionary<string, Grid> _customFileAssociationRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _adaptiveAudioDeviceSelectors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly StyleDraft _titleStyle = new();
    private readonly StyleDraft _subtitleStyle = new();
    private EditTarget _editTarget;
    private bool _loadingStyleDraft;
    private PriorityTitleItem? _draggedPriorityItem;
    private Point _priorityDragStart;
    private Action? _pendingPriorityPromptAction;
    private ICollectionView? _shortcutView;
    private ShortcutBindingItem? _editingShortcut;
    private string _capturedShortcutGesture = string.Empty;
    private ListBox? _bottomBarDragSource;
    private Point _bottomBarDragStart;
    private bool _loadingBottomBarLayout;
    private bool _loadingAudioDeviceSelection;
    private bool _loadingInterfaceLanguage;
    private string _languageBeforeDialog = "en";
    private string _audioDeviceBeforeAdaptive = "auto";

    public SettingsDialog(int rewindSeconds, int forwardSeconds,
        bool prioritizeChapters, bool playNextMediaAutomatically,
        PlaybackSettingsSnapshot playbackSettings,
        InterfaceSettingsSnapshot interfaceSettings,
        IReadOnlyList<VideoDisplayDescription> videoDisplays,
        VideoSettingsSnapshot videoSettings,
        ShortcutSettingsSnapshot shortcutSettings,
        bool resetVolumeOnStartup, int startupVolume,
        IReadOnlyList<AudioDeviceDescription> audioDevices,
        string selectedAudioDevice, bool audioPassthrough,
        bool audioExclusive, bool disableAudioByDefault, bool autoSelectPreferredAudio,
        string preferredAudioProfile, IReadOnlyList<string> preferredAudioTitlePriorities,
        AudioOutputMode audioOutputMode,
        AudioTreatmentMode audioTreatmentMode, int defaultAudioDelayMilliseconds,
        bool audioNormalization,
        bool autoSwitchAudioDevice,
        bool adaptiveAudioModeEnabled,
        IReadOnlyList<AdaptiveAudioDisplayMappingData> adaptiveAudioDisplayMappings,
        SubtitleSettingsSnapshot subtitleSettings)
    {
        _languageBeforeDialog = string.Equals(interfaceSettings.InterfaceLanguage?.Trim(), "fr",
            StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        LocalizationService.SetLanguage(interfaceSettings.InterfaceLanguage);
        InitializeComponent();
        Closed += SettingsDialog_OnClosed;
        MoveReorganizedSettingsSections();
        InitializeBottomBarLayoutEditor(interfaceSettings.BottomBarLayoutPresets,
            interfaceSettings.ActiveBottomBarLayoutPreset);
        ToolTipService.SetIsEnabled(this, !interfaceSettings.DisableToolTips);
        if (interfaceSettings.DisableToolTips)
        {
            AddHandler(FrameworkElement.LoadedEvent,
                new RoutedEventHandler(SettingsDialog_OnElementLoaded), true);
        }
        DiagnosticLogPathTextBlock.Text = DiagnosticLogPath;
        BuildFileAssociationChoices(playbackSettings.FileAssociationExtensions,
            playbackSettings.CustomFileAssociationTypes);
        RewindTextBox.Text = rewindSeconds.ToString(CultureInfo.InvariantCulture);
        ForwardTextBox.Text = forwardSeconds.ToString(CultureInfo.InvariantCulture);
        PrioritizeChaptersCheckBox.IsChecked = prioritizeChapters;
        PlayNextMediaCheckBox.IsChecked = playNextMediaAutomatically;
        EnhancedPlaybackCheckBox.IsChecked = playbackSettings.EnhancedPlaybackEnabled;
        EnhancedFolderAdvanceCheckBox.IsChecked = playbackSettings.EnhancedFolderAdvanceEnabled;
        EnhancedFolderShowNameCheckBox.IsChecked = playbackSettings.EnhancedFolderShowNameEnabled;
        ShowEnhancedUpcomingInPlaylistCheckBox.IsChecked = playbackSettings.ShowEnhancedUpcomingInPlaylist;
        ShowEnhancedNextFolderInPlaylistCheckBox.IsChecked = playbackSettings.ShowEnhancedNextFolderInPlaylist;
        ResumePlaybackCheckBox.IsChecked = playbackSettings.ResumePlayback;
        ResumePromptStartSkipPercentTextBox.Text = playbackSettings.ResumePromptStartSkipPercent
            .ToString(CultureInfo.InvariantCulture);
        ResumePromptEndSkipPercentTextBox.Text = playbackSettings.ResumePromptEndSkipPercent
            .ToString(CultureInfo.InvariantCulture);
        AutoPlayOnOpenCheckBox.IsChecked = playbackSettings.AutoPlayOnOpen;
        ConfirmCloseCheckBox.IsChecked = playbackSettings.ConfirmClose;
        BufferingEnabledCheckBox.IsChecked = playbackSettings.BufferingEnabled;
        PreventSleepDuringPlaybackCheckBox.IsChecked = playbackSettings.PreventSleepDuringPlayback;
        RememberMediaSettingsCheckBox.IsChecked = playbackSettings.RememberMediaSettings;
        RecentMediaRetentionDaysTextBox.Text = Math.Clamp(playbackSettings.RecentMediaRetentionDays, 0, 3650)
            .ToString(CultureInfo.InvariantCulture);
        RecentMediaFolderDepthTextBox.Text = Math.Clamp(playbackSettings.RecentMediaFolderDepth, 0, 10)
            .ToString(CultureInfo.InvariantCulture);
        PlaylistFolderDepthTextBox.Text = Math.Clamp(playbackSettings.PlaylistFolderDepth, 0, 10)
            .ToString(CultureInfo.InvariantCulture);
        RepeatPlaylistCheckBox.IsChecked = playbackSettings.RepeatPlaylist;
        FileAssociationsEnabledCheckBox.IsChecked = playbackSettings.FileAssociationsEnabled;
        FileAssociationsEnabledCheckBox.Checked += (_, _) => UpdateFileAssociationChoicesState();
        FileAssociationsEnabledCheckBox.Unchecked += (_, _) => UpdateFileAssociationChoicesState();
        UpdateFileAssociationChoicesState();
        TopBarAutoHideDelayTextBox.Text = FormatAutoHideDelay(interfaceSettings.TopBarAutoHideDelayMilliseconds);
        BottomBarAutoHideDelayTextBox.Text = FormatAutoHideDelay(interfaceSettings.BottomBarAutoHideDelayMilliseconds);
        PlaylistScrollSpeedTextBox.Text = Math.Clamp(
                interfaceSettings.PlaylistScrollSpeed <= 0 ? 20 : interfaceSettings.PlaylistScrollSpeed, 1, 100)
            .ToString(CultureInfo.InvariantCulture);
        VolumeIndicatorHideDelayTextBox.Text = FormatAutoHideDelay(
            interfaceSettings.VolumeIndicatorHideDelayMilliseconds);
        SelectTaggedItem(VolumeControlStyleComboBox,
            Math.Clamp(interfaceSettings.VolumeControlStyle, 0, 3).ToString(CultureInfo.InvariantCulture), "3");
        VolumePopupHideDelayTextBox.Text = FormatAutoHideDelay(
            interfaceSettings.VolumePopupHideDelayMilliseconds);
        HideInterfaceOnVideoStartCheckBox.IsChecked = interfaceSettings.HideInterfaceOnVideoStart;
        ShowSynchronizationButtonCheckBox.IsChecked = interfaceSettings.ShowSynchronizationButton;
        ShowShuffleButtonCheckBox.IsChecked = interfaceSettings.ShowShuffleButton;
        ShowRepeatButtonCheckBox.IsChecked = interfaceSettings.ShowRepeatButton;
        ShowSpeedButtonCheckBox.IsChecked = interfaceSettings.ShowSpeedButton;
        ShowPlaylistButtonCheckBox.IsChecked = interfaceSettings.ShowPlaylistButton;
        ShowVideoPanButtonCheckBox.IsChecked = interfaceSettings.ShowVideoPanButton;
        ShowAdditionalMediaInformationCheckBox.IsChecked = interfaceSettings.ShowAdditionalMediaInformation;
        AutoCompactMissingBottomBarItemsCheckBox.IsChecked =
            interfaceSettings.AutoCompactMissingBottomBarItems;
        ShufflePlaybackCheckBox.IsChecked = interfaceSettings.ShufflePlayback;
        RepeatPlaybackCheckBox.IsChecked = interfaceSettings.RepeatPlayback;
        ShowScreenshotButtonCheckBox.IsChecked = interfaceSettings.ShowScreenshotButton;
        AdaptiveInterfaceScaleCheckBox.IsChecked = interfaceSettings.AdaptiveInterfaceScale;
        AutoHideCursorCheckBox.IsChecked = interfaceSettings.AutoHideCursor;
        CursorAutoHideDelayTextBox.Text = FormatAutoHideDelay(
            interfaceSettings.CursorAutoHideDelayMilliseconds);
        AlwaysOnTopCheckBox.IsChecked = interfaceSettings.AlwaysOnTop;
        ShowOsdCheckBox.IsChecked = interfaceSettings.ShowOsd;
        DisableToolTipsCheckBox.IsChecked = interfaceSettings.DisableToolTips;
        ShowChapterNameInSeekPreviewCheckBox.IsChecked = interfaceSettings.ShowChapterNameInSeekPreview;
        _loadingInterfaceLanguage = true;
        SelectTaggedItem(InterfaceLanguageComboBox, interfaceSettings.InterfaceLanguage, "en");
        _loadingInterfaceLanguage = false;
        TogglePlaybackOnSingleClickCheckBox.IsChecked = interfaceSettings.TogglePlaybackOnSingleClick;
        ToggleFullscreenOnDoubleClickCheckBox.IsChecked = interfaceSettings.ToggleFullscreenOnDoubleClick;
        DiagnosticLoggingCheckBox.IsChecked = interfaceSettings.DiagnosticLoggingEnabled;

        StartVideoFullscreenCheckBox.IsChecked = videoSettings.StartFullscreen;
        foreach (var display in videoDisplays)
        {
            VideoDisplayComboBox.Items.Add(new ComboBoxItem
            {
                Content = display.Description,
                Tag = display.Id
            });
        }

        if (VideoDisplayComboBox.Items.Count == 0)
        {
            VideoDisplayComboBox.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Get("Automatique (écran actuel)"),
                Tag = "auto"
            });
        }

        SelectTaggedItem(VideoDisplayComboBox, videoSettings.PreferredDisplay, "auto");
        SelectTaggedItem(VideoOutputComboBox, videoSettings.VideoOutput, "auto");
        CustomZoomPercentTextBox.Text = Math.Clamp(videoSettings.CustomZoomPercent, 50, 1000)
            .ToString(CultureInfo.InvariantCulture);
        CustomAspectRatioTextBox.Text = NormalizeAspectRatio(videoSettings.CustomAspectRatio);
        HardwareDecodingCheckBox.IsChecked = videoSettings.HardwareDecoding;
        DeinterlacingCheckBox.IsChecked = videoSettings.Deinterlacing;
        SelectTaggedItem(HdrModeComboBox, videoSettings.HdrMode, "auto");
        ScreenshotBaseDirectoryTextBox.Text = string.IsNullOrWhiteSpace(videoSettings.ScreenshotBaseDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : videoSettings.ScreenshotBaseDirectory;
        ScreenshotFolderNameTextBox.Text = string.IsNullOrWhiteSpace(videoSettings.ScreenshotFolderName)
            ? "Fuze"
            : videoSettings.ScreenshotFolderName;
        SelectTaggedItem(ScreenshotFormatComboBox, videoSettings.ScreenshotFormat, "png");
        SelectTaggedItem(ScreenshotAffixModeComboBox, videoSettings.ScreenshotAffixMode, "prefix");
        ScreenshotAffixTextBox.Text = videoSettings.ScreenshotAffixText ?? string.Empty;
        ScreenshotSequentialNumberingCheckBox.IsChecked = videoSettings.ScreenshotSequentialNumbering;
        CopyScreenshotsToClipboardCheckBox.IsChecked = videoSettings.CopyScreenshotsToClipboard;

        var shortcuts = ShortcutCatalog.Normalize(shortcutSettings.KeyboardShortcuts);
        foreach (var definition in ShortcutCatalog.Definitions)
            _shortcutItems.Add(new ShortcutBindingItem(definition, shortcuts[definition.Id]));
        ShortcutListBox.ItemsSource = _shortcutItems;
        _shortcutView = CollectionViewSource.GetDefaultView(_shortcutItems);
        _shortcutView.Filter = item => item is ShortcutBindingItem shortcut &&
                                     shortcut.Matches(ShortcutSearchTextBox.Text.Trim());
        MouseWheelTimelineCheckBox.IsChecked = shortcutSettings.MouseWheelTimelineEnabled;
        MouseWheelVolumeCheckBox.IsChecked = shortcutSettings.MouseWheelVolumeEnabled;
        CenterWheelVolumeCheckBox.IsChecked = shortcutSettings.CenterWheelVolumeEnabled;
        CenterWheelTimelineCheckBox.IsChecked = shortcutSettings.CenterWheelTimelineEnabled &&
                                                 !shortcutSettings.CenterWheelVolumeEnabled;
        MouseWheelAudioTracksCheckBox.IsChecked = shortcutSettings.MouseWheelAudioTracksEnabled;
        MouseWheelSubtitleTracksCheckBox.IsChecked = shortcutSettings.MouseWheelSubtitleTracksEnabled;
        IgnoreKeyboardVolumeButtonsCheckBox.IsChecked = shortcutSettings.IgnoreKeyboardVolumeButtons;

        ResetVolumeCheckBox.IsChecked = resetVolumeOnStartup;
        StartupVolumeSlider.Value = Math.Clamp(startupVolume, 0, 125);
        _loadingAudioDeviceSelection = true;
        _audioDeviceBeforeAdaptive = string.IsNullOrWhiteSpace(selectedAudioDevice) ||
                                     selectedAudioDevice.Equals(AdaptiveAudioDeviceTag,
                                         StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : selectedAudioDevice;
        foreach (var device in audioDevices)
        {
            AudioDeviceComboBox.Items.Add(new ComboBoxItem
            {
                Content = device.Description,
                Tag = device.Name,
                ToolTip = device.Name.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? LocalizationService.Get("Suit le périphérique sélectionné dans Windows")
                    : device.Name
            });
        }

        AddAdaptiveAudioDeviceOption();
        SelectTaggedItem(AudioDeviceComboBox,
            adaptiveAudioModeEnabled ? AdaptiveAudioDeviceTag : _audioDeviceBeforeAdaptive,
            "auto");
        AudioPassthroughCheckBox.IsChecked = audioPassthrough;
        AudioExclusiveCheckBox.IsChecked = audioExclusive;
        DisableAudioByDefaultCheckBox.IsChecked = disableAudioByDefault;
        AutoSelectPreferredAudioCheckBox.IsChecked = autoSelectPreferredAudio;
        SelectTaggedItem(PreferredAudioComboBox,
            autoSelectPreferredAudio ? preferredAudioProfile : "disabled", "disabled");
        AddPriorityItems(_audioPriorityItems, preferredAudioTitlePriorities);
        PreferredAudioTitlesListBox.ItemsSource = _audioPriorityItems;
        SelectTaggedItem(AudioOutputModeComboBox, audioOutputMode, AudioOutputMode.Automatic);
        NightModeCheckBox.IsChecked = audioTreatmentMode.HasFlag(AudioTreatmentMode.Night);
        DialogueBoostCheckBox.IsChecked = audioTreatmentMode.HasFlag(AudioTreatmentMode.DialogueBoost);
        HeadphoneBinauralCheckBox.IsChecked = audioTreatmentMode.HasFlag(AudioTreatmentMode.HeadphoneBinaural);
        SurroundDownmixCheckBox.IsChecked = audioTreatmentMode.HasFlag(AudioTreatmentMode.SurroundDownmix);
        AudioDelayTextBox.Text = Math.Clamp(defaultAudioDelayMilliseconds, -30000, 30000)
            .ToString(CultureInfo.InvariantCulture);
        AudioNormalizationCheckBox.IsChecked = audioNormalization;
        AutoSwitchAudioDeviceCheckBox.IsChecked = autoSwitchAudioDevice;
        BuildAdaptiveAudioMappings(videoDisplays, audioDevices, adaptiveAudioDisplayMappings);
        UpdateAdaptiveAudioControls();
        _loadingAudioDeviceSelection = false;

        PopulateSubtitleChoices();
        StartupTitleOverlayCheckBox.IsChecked = subtitleSettings.StartupTitleOverlayEnabled;
        PreferOriginalTitleCheckBox.IsChecked = subtitleSettings.PreferOriginalTitleForStartup;
        StartupTitleDelayTextBox.Text = Math.Clamp(subtitleSettings.StartupTitleDelayMilliseconds, 0, 30000)
            .ToString(CultureInfo.InvariantCulture);
        StartupTitleDurationTextBox.Text = Math.Clamp(subtitleSettings.StartupTitleDurationMilliseconds, 250, 30000)
            .ToString(CultureInfo.InvariantCulture);
        AutoSelectPreferredSubtitleCheckBox.IsChecked = subtitleSettings.AutoSelectPreferredSubtitle;
        SelectTaggedItem(PreferredSubtitleComboBox, subtitleSettings.PreferredSubtitleProfile, "default");
        AddPriorityItems(_subtitlePriorityItems, subtitleSettings.PreferredSubtitleTitlePriorities);
        PreferredSubtitleTitlesListBox.ItemsSource = _subtitlePriorityItems;
        PreferSdhSubtitlesCheckBox.IsChecked = subtitleSettings.PreferSdhSubtitles;
        DisableSubtitlesByDefaultCheckBox.IsChecked = subtitleSettings.DisableSubtitlesByDefault;
        AutoLoadExternalSubtitlesCheckBox.IsChecked = subtitleSettings.AutoLoadExternalSubtitles;
        SelectTaggedItem(SubtitleEncodingComboBox, subtitleSettings.SubtitleEncoding, "auto");
        SetDraft(_titleStyle,
            subtitleSettings.StartupTitleFont,
            subtitleSettings.StartupTitleFontSize,
            subtitleSettings.StartupTitleTextColor,
            subtitleSettings.StartupTitleBorderColor,
            subtitleSettings.StartupTitleBorderSize,
            subtitleSettings.StartupTitleShadow,
            true,
            subtitleSettings.StartupTitlePosition,
            subtitleSettings.StartupTitleMarginX,
            subtitleSettings.StartupTitleMarginY,
            subtitleSettings.StartupTitleScaleWithWindow);
        SetDraft(_subtitleStyle,
            subtitleSettings.SubtitleFont,
            subtitleSettings.SubtitleFontSize,
            subtitleSettings.SubtitleTextColor,
            subtitleSettings.SubtitleBorderColor,
            subtitleSettings.SubtitleBorderSize,
            subtitleSettings.SubtitleShadow,
            subtitleSettings.SubtitleForcePosition,
            subtitleSettings.SubtitlePosition,
            subtitleSettings.SubtitleMarginX,
            subtitleSettings.SubtitleMarginY,
            subtitleSettings.SubtitleScaleWithWindow);
        LoadStyleDraft(_subtitleStyle);
        _editTarget = EditTarget.None;

        UpdateStartupVolumeControls();
        UpdatePreferredAudioControls();
        UpdatePassthroughControls();
        UpdateStartupTitleControls();
        UpdatePreferredSubtitleControls();
        UpdateSubtitlePositionControls();
        UpdateEditTargetVisuals();
        UpdatePriorityCounts();
        UpdateSubtitlePreview();

        Loaded += (_, _) =>
        {
            ShowSelectedCategory();
            RewindTextBox.Focus();
            RewindTextBox.SelectAll();
            UpdateSubtitlePreview();
        };

        LocalizationService.ApplyToWindow(this);
    }

    public int RewindSeconds { get; private set; }
    public int ForwardSeconds { get; private set; }
    public bool BottomBarLayoutEditorRequested { get; private set; }
    public bool SettingsImported { get; set; }
    public string BottomBarLayoutEditorBasePresetName { get; private set; } = "Fuze — classique";
    public bool PrioritizeChapters { get; private set; }
    public bool PlayNextMediaAutomatically { get; private set; }
    public PlaybackSettingsSnapshot PlaybackSettings { get; private set; } =
        new(true, 5, 5, true, false, true, true, false, 0,
            2, 2, false, true, false, true, true, false, false, [], []);
    public InterfaceSettingsSnapshot InterfaceSettings { get; private set; } =
        new(350, 500, 20, 0, 2000, 1000, true, false, false, false,
            false, false, false, true, true, true, false, false, true, 3000, true, true, true, false,
            false, "en", true, true, [], "Fuze — classique");
    public bool ResetVolumeOnStartup { get; private set; }
    public int StartupVolume { get; private set; }
    public VideoSettingsSnapshot VideoSettings { get; private set; } = new(
        true, "auto", "auto", true, false, "auto",
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Fuze", "png", "prefix", "Fuze", false, false);
    public ShortcutSettingsSnapshot ShortcutSettings { get; private set; } = new(
        ShortcutCatalog.CreateDefaults(), true, true, true, false, true, true, false);
    public string SelectedAudioDevice { get; private set; } = "auto";
    public bool AudioPassthrough { get; private set; }
    public bool AudioExclusive { get; private set; }
    public bool DisableAudioByDefault { get; private set; }
    public bool AutoSelectPreferredAudio { get; private set; }
    public string PreferredAudioProfile { get; private set; } = "disabled";
    public string[] PreferredAudioTitlePriorities { get; private set; } = [];
    public AudioOutputMode AudioOutputMode { get; private set; } = AudioOutputMode.Automatic;
    public AudioTreatmentMode AudioTreatmentMode { get; private set; }
    public int DefaultAudioDelayMilliseconds { get; private set; }
    public bool AudioNormalization { get; private set; }
    public bool AutoSwitchAudioDevice { get; private set; }
    public bool AdaptiveAudioModeEnabled { get; private set; }
    public AdaptiveAudioDisplayMappingData[] AdaptiveAudioDisplayMappings { get; private set; } = [];
    public SubtitleSettingsSnapshot SubtitleSettings { get; private set; } = new(
        true, false, "top-center", 250, 3000,
        "Arial", 42, "#FFFFFFFF", "#FF000000", 2.5, true, 20, 36, true,
        false, "default", [], false, false, false, "auto", "Arial", 42,
        "#FFFFFFFF", "#FF000000", 2.5, true, false, "bottom-center", 20, 36, true);

    /// <summary>
    /// Déclenché pendant l’édition afin que la fenêtre principale puisse
    /// appliquer provisoirement la mise en page sans attendre Enregistrer.
    /// </summary>
    public event Action<BottomBarLayoutPresetData>? BottomBarLayoutPreviewChanged;
    public event Action<string>? ExportSettingsRequested;
    public event Action<string>? ImportSettingsRequested;

    public void ShowTransferNotice(string title, string message) =>
        ShowPriorityPrompt(title, message, "OK");

    private void OpenBottomBarLayoutDialog_OnClick(object sender, RoutedEventArgs e)
    {
        // L’éditeur doit manipuler la vraie barre de la fenêtre principale,
        // et non une copie modale dans les paramètres. On signale la demande
        // au propriétaire, qui ferme cette fenêtre puis ouvre l’éditeur flottant.
        BottomBarLayoutEditorBasePresetName =
            (BottomBarInterfaceComboBox.SelectedItem as BottomBarLayoutPresetData)?.Name
            ?? PrimaryClassicBottomBarPresetName;
        BottomBarLayoutEditorRequested = true;
        DialogResult = false;
    }

    private static readonly (string Id, string Label)[] BottomBarLayoutItems =
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

    private static BottomBarLayoutPresetData CreateCompactBottomBarPreset() => new()
    {
        Name = "Fuze — compacte",
        IsBuiltIn = true,
        LeftItems = ["playlist", "previous", "rewind", "play", "forward", "next", "title"],
        CenterItems = ["timeline"],
        RightItems = ["screenshot", "shuffle", "repeat", "audio", "subtitles", "speed", "sync", "pan", "gear", "mute", "volume", "fullscreen"],
        Spacing = 4,
        CenterBarOffset = 10
    };

    private static BottomBarLayoutPresetData CreateCinemaBottomBarPreset() => new()
    {
        Name = "Fuze — cinéma",
        IsBuiltIn = true,
        LeftItems = ["previous", "rewind", "play", "forward", "next"],
        CenterItems = ["title", "timeline"],
        RightItems = ["playlist", "audio", "subtitles", "mute", "volume", "pan", "fullscreen"],
        Spacing = 5,
        CenterBarOffset = 10
    };

    private static BottomBarLayoutPresetData CreateClassicBottomBarPreset() => new()
    {
        Name = "Fuze — classique",
        IsBuiltIn = true,
        LeftItems = ["playlist", "previous", "rewind", "play", "forward", "next", "title"],
        CenterItems = ["timeline"],
        RightItems = ["screenshot", "shuffle", "repeat", "audio", "subtitles", "speed", "sync", "pan", "gear", "mute", "volume", "fullscreen"],
        HorizontalPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["playlist"] = 0.010067909033480732,
            ["previous"] = 0.45033164876816173,
            ["rewind"] = 0.4744946304485155,
            ["play"] = 0.5,
            ["forward"] = 0.5255053695514845,
            ["next"] = 0.5496683512318383,
            ["title"] = 0.18961228679722045,
            ["screenshot"] = 0.03355969677826911,
            ["shuffle"] = 0.7315224257738472,
            ["repeat"] = 0.7556854074542009,
            ["audio"] = 0.7872315224257739,
            ["subtitles"] = 0.8261607706885661,
            ["speed"] = 0.8644188250157928,
            ["sync"] = 0.7073594440934934,
            ["pan"] = 0.7190000000000000,
            ["gear"] = 0.6831964624131396,
            ["mute"] = 0.8952937460518003,
            ["volume"] = 0.9422773215413771,
            ["fullscreen"] = 0.9892608970309539,
            ["elapsed_time"] = 0.3949581490840177,
            ["duration_time"] = 0.6083978205938092
        },
        Spacing = 4,
        TitleWidth = 500,
        CenterBarOffset = 10,
        SplitTimeline = true
    };

    private static BottomBarLayoutPresetData CreatePrimaryClassicBottomBarPreset()
    {
        var preset = CreateClassicBottomBarPreset();
        preset.Name = PrimaryClassicBottomBarPresetName;
        preset.CenterLockedItemId = "play";
        return preset;
    }

    private const string PrimaryClassicBottomBarPresetName = "Fuze — classique";

    private static bool IsPrimaryClassicBottomBarPresetName(string? name) =>
        string.Equals(name?.Trim(), "fuze new", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), "Fuse Classic 4", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), "fuse classique 4", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), "fuze — classique 4", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuiltInBottomBarPreset(BottomBarLayoutPresetData preset) =>
        preset.IsBuiltIn ||
        string.Equals(preset.Name, PrimaryClassicBottomBarPresetName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuze — compacte", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuze — cinéma", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuse classique", StringComparison.OrdinalIgnoreCase);

    private static BottomBarLayoutPresetData CloneBottomBarPreset(BottomBarLayoutPresetData source) => new()
    {
        Name = source.Name?.Trim() ?? string.Empty,
        IsBuiltIn = IsBuiltInBottomBarPreset(source),
        LeftItems = [.. source.LeftItems ?? []],
        CenterItems = [.. source.CenterItems ?? []],
        RightItems = [.. source.RightItems ?? []],
        HorizontalPositions = (source.HorizontalPositions ?? new Dictionary<string, double>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value))
            .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0d, 1d),
                StringComparer.OrdinalIgnoreCase),
        Spacing = Math.Clamp(source.Spacing, 0, 24),
        TitleWidth = Math.Clamp(source.TitleWidth, 80, 800),
        CenterBarOffset = Math.Clamp((source.IsBuiltIn ||
            string.Equals(source.Name, "Fuze — compacte", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source.Name, "Fuze — cinéma", StringComparison.OrdinalIgnoreCase)) &&
            (source.CenterBarOffset is 1 or 4) ? 10 : source.CenterBarOffset, -34, 18),
        HideVerticalGuides = source.HideVerticalGuides,
        HideHorizontalCenterGuide = source.HideHorizontalCenterGuide,
        SplitTimeline = source.SplitTimeline,
        CenterLockedItemId = string.IsNullOrWhiteSpace(source.CenterLockedItemId)
            ? null
            : source.CenterLockedItemId.Trim()
    };

    private static IReadOnlyList<BottomBarLayoutPresetData> NormalizeBottomBarPresets(
        IReadOnlyList<BottomBarLayoutPresetData>? source)
    {
        var result = new List<BottomBarLayoutPresetData>();
        BottomBarLayoutPresetData? primaryClassic = null;
        if (source is not null)
        {
            foreach (var candidate in source)
            {
                if (candidate is null)
                    continue;

                var preset = CloneBottomBarPreset(candidate);
                if (IsPrimaryClassicBottomBarPresetName(preset.Name))
                {
                    // La version 4 devient le modèle classique principal, tout
                    // en conservant les autres créations de l'utilisateur.
                    preset.Name = PrimaryClassicBottomBarPresetName;
                    preset.IsBuiltIn = true;
                    var usedPrimary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var list in new[] { preset.LeftItems, preset.CenterItems, preset.RightItems })
                        list.RemoveAll(item => !BottomBarLayoutItems.Any(known =>
                                string.Equals(known.Id, item, StringComparison.OrdinalIgnoreCase)) ||
                            !usedPrimary.Add(item));
                    primaryClassic ??= preset;
                    continue;
                }
                if (IsBuiltInBottomBarPreset(preset))
                    continue;
                if (string.IsNullOrWhiteSpace(preset.Name) ||
                    result.Any(existing => string.Equals(existing.Name, preset.Name,
                        StringComparison.OrdinalIgnoreCase)))
                    continue;

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var list in new[] { preset.LeftItems, preset.CenterItems, preset.RightItems })
                    list.RemoveAll(item => !BottomBarLayoutItems.Any(known =>
                            string.Equals(known.Id, item, StringComparison.OrdinalIgnoreCase)) ||
                        !used.Add(item));

                result.Add(preset);
            }
        }

        result.Insert(0, CreateCinemaBottomBarPreset());
        result.Insert(0, CreateCompactBottomBarPreset());
        result.Insert(2, primaryClassic ?? CreatePrimaryClassicBottomBarPreset());
        return result;
    }

    private void InitializeBottomBarLayoutEditor(
        IReadOnlyList<BottomBarLayoutPresetData>? presets,
        string? activePresetName)
    {
        _loadingBottomBarLayout = true;
        _bottomBarPresets.Clear();
        foreach (var preset in NormalizeBottomBarPresets(presets))
            _bottomBarPresets.Add(preset);

        BottomBarLeftListBox.ItemsSource = _bottomBarLeftItems;
        BottomBarCenterListBox.ItemsSource = _bottomBarCenterItems;
        BottomBarRightListBox.ItemsSource = _bottomBarRightItems;
        BottomBarPresetComboBox.ItemsSource = _bottomBarPresets;
        BottomBarPresetComboBox.DisplayMemberPath = nameof(BottomBarLayoutPresetData.DisplayName);
        BottomBarInterfaceComboBox.ItemsSource = _bottomBarPresets;
        var normalizedActivePresetName = IsPrimaryClassicBottomBarPresetName(activePresetName)
            ? PrimaryClassicBottomBarPresetName
            : string.Equals(activePresetName, "Fuse classique",
                StringComparison.OrdinalIgnoreCase)
                ? "Fuze — classique"
                : activePresetName;
        var selected = _bottomBarPresets.FirstOrDefault(preset =>
            string.Equals(preset.Name, normalizedActivePresetName, StringComparison.OrdinalIgnoreCase))
            ?? _bottomBarPresets.FirstOrDefault(preset =>
                string.Equals(preset.Name, PrimaryClassicBottomBarPresetName,
                    StringComparison.OrdinalIgnoreCase))
            ?? _bottomBarPresets[0];
        BottomBarPresetComboBox.SelectedItem = selected;
        BottomBarInterfaceComboBox.SelectedItem = selected;
        _loadingBottomBarLayout = false;
        LoadBottomBarLayoutPreset(selected);
        UpdateBottomBarInterfaceDeleteButtonState();
    }

    private void BottomBarInterfaceComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingBottomBarLayout ||
            BottomBarInterfaceComboBox.SelectedItem is not BottomBarLayoutPresetData preset)
            return;

        _loadingBottomBarLayout = true;
        BottomBarPresetComboBox.SelectedItem = preset;
        _loadingBottomBarLayout = false;
        LoadBottomBarLayoutPreset(preset);
        UpdateBottomBarInterfaceDeleteButtonState();
    }

    private void UpdateBottomBarInterfaceDeleteButtonState()
    {
        if (DeleteBottomBarInterfaceButton is null)
            return;

        DeleteBottomBarInterfaceButton.IsEnabled =
            BottomBarInterfaceComboBox.SelectedItem is BottomBarLayoutPresetData preset &&
            !IsBuiltInBottomBarPreset(preset);
    }

    private void DeleteBottomBarInterface_OnClick(object sender, RoutedEventArgs e)
    {
        if (BottomBarInterfaceComboBox.SelectedItem is not BottomBarLayoutPresetData preset)
            return;

        if (IsBuiltInBottomBarPreset(preset))
        {
            MessageBox.Show(this,
                LocalizationService.Get("Les modèles intégrés à Fuse ne peuvent pas être supprimés. Vous pouvez supprimer uniquement vos interfaces personnalisées."),
                LocalizationService.Get("Mise en page de la barre inférieure"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            UpdateBottomBarInterfaceDeleteButtonState();
            return;
        }

        var confirmationDialog = new ConfirmCloseDialog(
            "Supprimer l’interface",
            "Supprimer cette interface ?",
            $"L’interface personnalisée « {preset.Name} » sera retirée de vos modèles enregistrés.",
            "Supprimer")
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = true,
            ShowActivated = true
        };
        if (confirmationDialog.ShowDialog() != true || !confirmationDialog.Confirmed)
            return;

        var removedIndex = _bottomBarPresets.IndexOf(preset);
        _bottomBarPresets.Remove(preset);
        if (_bottomBarPresets.Count == 0)
            return;

        var replacement = _bottomBarPresets[Math.Clamp(removedIndex, 0, _bottomBarPresets.Count - 1)];
        _loadingBottomBarLayout = true;
        BottomBarInterfaceComboBox.SelectedItem = replacement;
        BottomBarPresetComboBox.SelectedItem = replacement;
        _loadingBottomBarLayout = false;
        LoadBottomBarLayoutPreset(replacement);
        UpdateBottomBarInterfaceDeleteButtonState();
    }

    private ObservableCollection<BottomBarLayoutEditorItem> GetBottomBarCollection(ListBox listBox) =>
        listBox.Name switch
        {
            nameof(BottomBarLeftListBox) => _bottomBarLeftItems,
            nameof(BottomBarCenterListBox) => _bottomBarCenterItems,
            _ => _bottomBarRightItems
        };

    private void LoadBottomBarLayoutPreset(BottomBarLayoutPresetData? preset)
    {
        if (preset is null)
            return;

        _loadingBottomBarLayout = true;
        _bottomBarLeftItems.Clear();
        _bottomBarCenterItems.Clear();
        _bottomBarRightItems.Clear();
        AddBottomBarItems(_bottomBarLeftItems, preset.LeftItems);
        AddBottomBarItems(_bottomBarCenterItems, preset.CenterItems);
        AddBottomBarItems(_bottomBarRightItems, preset.RightItems);
        BottomBarPresetNameTextBox.Text = preset.Name;
        BottomBarSpacingTextBox.Text = preset.Spacing.ToString(CultureInfo.InvariantCulture);
        _loadingBottomBarLayout = false;
        PublishBottomBarLayoutPreview();
    }

    private static void AddBottomBarItems(
        ObservableCollection<BottomBarLayoutEditorItem> destination,
        IEnumerable<string>? ids)
    {
        if (ids is null)
            return;
        foreach (var id in ids)
        {
            var item = BottomBarLayoutItems.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(item.Id))
                destination.Add(new BottomBarLayoutEditorItem(item.Id, item.Label));
        }
    }

    private void CaptureBottomBarLayoutDraft(BottomBarLayoutPresetData preset)
    {
        preset.Name = string.IsNullOrWhiteSpace(BottomBarPresetNameTextBox.Text)
            ? "Fuze — personnalisé"
            : BottomBarPresetNameTextBox.Text.Trim();
        preset.LeftItems = [.. _bottomBarLeftItems.Select(item => item.Id)];
        preset.CenterItems = [.. _bottomBarCenterItems.Select(item => item.Id)];
        preset.RightItems = [.. _bottomBarRightItems.Select(item => item.Id)];
        preset.Spacing = TryParseRange(BottomBarSpacingTextBox, 0, 24, out var spacing)
            ? spacing
            : 4;
    }

    private BottomBarLayoutPresetData CreateCurrentBottomBarLayoutDraft() => new()
    {
        Name = string.IsNullOrWhiteSpace(BottomBarPresetNameTextBox.Text)
            ? "Fuze — personnalisé"
            : BottomBarPresetNameTextBox.Text.Trim(),
        LeftItems = [.. _bottomBarLeftItems.Select(item => item.Id)],
        CenterItems = [.. _bottomBarCenterItems.Select(item => item.Id)],
        RightItems = [.. _bottomBarRightItems.Select(item => item.Id)],
        HorizontalPositions = (BottomBarPresetComboBox.SelectedItem as BottomBarLayoutPresetData)?
            .HorizontalPositions is { } positions
                ? new Dictionary<string, double>(positions, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        Spacing = TryParseRange(BottomBarSpacingTextBox, 0, 24, out var spacing) ? spacing : 4,
        TitleWidth = (BottomBarPresetComboBox.SelectedItem as BottomBarLayoutPresetData)?.TitleWidth ?? 230,
        CenterBarOffset = (BottomBarPresetComboBox.SelectedItem as BottomBarLayoutPresetData)?.CenterBarOffset ?? 10,
        HideVerticalGuides = (BottomBarPresetComboBox.SelectedItem as BottomBarLayoutPresetData)?.HideVerticalGuides ?? false,
        HideHorizontalCenterGuide = (BottomBarPresetComboBox.SelectedItem as BottomBarLayoutPresetData)?.HideHorizontalCenterGuide ?? false,
        SplitTimeline = (BottomBarPresetComboBox.SelectedItem as BottomBarLayoutPresetData)?.SplitTimeline ?? false,
        CenterLockedItemId = (BottomBarPresetComboBox.SelectedItem as BottomBarLayoutPresetData)?.CenterLockedItemId
    };

    private static Border CreateBottomBarPreviewItem(string id, string label, double spacing)
    {
        var display = id switch
        {
            "timeline" => "00:00 / -00:00",
            "title" => LocalizationService.Get("Titre du média"),
            "volume" => "🔊",
            _ => LocalizationService.Get(label)
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x46, 0x52)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            // Conserver exactement l'espacement demandé, y compris zéro;
            // l'ancien minimum d'un pixel rendait les valeurs 0 à 3
            // visuellement identiques dans l'aperçu des paramètres.
            Margin = new Thickness(spacing / 2d, 0, spacing / 2d, 0),
            Child = new TextBlock
            {
                Text = display,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xEF)),
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 112,
                VerticalAlignment = VerticalAlignment.Center
            },
            ToolTip = LocalizationService.Get(label)
        };
    }

    private void PublishBottomBarLayoutPreview()
    {
        var draft = CreateCurrentBottomBarLayoutDraft();
        RefreshBottomBarLayoutPreview(draft);
        BottomBarLayoutPreviewChanged?.Invoke(CloneBottomBarPreset(draft));
    }

    private void RefreshBottomBarLayoutPreview(BottomBarLayoutPresetData draft)
    {
        if (BottomBarPreviewLeft is null)
            return;
        BottomBarPreviewLeft.Children.Clear();
        BottomBarPreviewCenter.Children.Clear();
        BottomBarPreviewRight.Children.Clear();
        var hosts = new[] { BottomBarPreviewLeft, BottomBarPreviewCenter, BottomBarPreviewRight };
        var lists = new[] { draft.LeftItems, draft.CenterItems, draft.RightItems };
        for (var group = 0; group < hosts.Length; group++)
        {
            foreach (var id in lists[group])
            {
                var item = BottomBarLayoutItems.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(item.Id))
                    hosts[group].Children.Add(CreateBottomBarPreviewItem(item.Id, item.Label, draft.Spacing));
            }
        }
    }

    private void BottomBarSpacingTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingBottomBarLayout && TryParseRange(BottomBarSpacingTextBox, 0, 24, out _))
            PublishBottomBarLayoutPreview();
    }

    private void BottomBarPresetComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingBottomBarLayout &&
            e.RemovedItems.OfType<BottomBarLayoutPresetData>().FirstOrDefault() is { } previous)
            CaptureBottomBarLayoutDraft(previous);

        if (!_loadingBottomBarLayout && BottomBarPresetComboBox.SelectedItem is BottomBarLayoutPresetData preset)
            LoadBottomBarLayoutPreset(preset);
    }

    private void SaveBottomBarPreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (BottomBarPresetComboBox.SelectedItem is not BottomBarLayoutPresetData preset)
            return;
        if (!TryParseRange(BottomBarSpacingTextBox, 0, 24, out _))
        {
            MessageBox.Show(this, LocalizationService.Get("Entrez un espacement compris entre 0 et 24."),
                LocalizationService.Get("Barre inférieure"), MessageBoxButton.OK, MessageBoxImage.Warning);
            BottomBarSpacingTextBox.Focus();
            return;
        }

        CaptureBottomBarLayoutDraft(preset);
        BottomBarPresetComboBox.Items.Refresh();
        PublishBottomBarLayoutPreview();
    }

    private void SaveBottomBarPresetAs_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseRange(BottomBarSpacingTextBox, 0, 24, out _))
        {
            MessageBox.Show(this, LocalizationService.Get("Entrez un espacement compris entre 0 et 24."),
                LocalizationService.Get("Barre inférieure"), MessageBoxButton.OK, MessageBoxImage.Warning);
            BottomBarSpacingTextBox.Focus();
            return;
        }

        var name = BottomBarPresetNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Fuze — personnalisé";
        var baseName = name;
        var suffix = 2;
        while (_bottomBarPresets.Any(preset => string.Equals(preset.Name, name,
                   StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {suffix++}";

        var preset = new BottomBarLayoutPresetData { Name = name };
        CaptureBottomBarLayoutDraft(preset);
        preset.Name = name;
        _bottomBarPresets.Add(preset);
        BottomBarPresetComboBox.SelectedItem = preset;
    }

    private void DeleteBottomBarPreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (BottomBarPresetComboBox.SelectedItem is not BottomBarLayoutPresetData preset ||
            _bottomBarPresets.Count <= 1)
            return;
        if (preset.IsBuiltIn)
        {
            MessageBox.Show(this, LocalizationService.Get("Les modèles Fuse intégrés ne peuvent pas être supprimés."),
                LocalizationService.Get("Barre inférieure"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _bottomBarPresets.Remove(preset);
        BottomBarPresetComboBox.SelectedIndex = 0;
    }

    private void MoveBottomBarItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction })
            return;
        var selectedList = new[] { BottomBarLeftListBox, BottomBarCenterListBox, BottomBarRightListBox }
            .FirstOrDefault(list => list.SelectedItem is BottomBarLayoutEditorItem);
        if (selectedList is null)
            return;

        var source = GetBottomBarCollection(selectedList);
        var index = selectedList.SelectedIndex;
        if (index < 0 || index >= source.Count)
            return;

        var destination = source;
        if (direction is "left" or "right")
        {
            var listIndex = Array.IndexOf(new[] { BottomBarLeftListBox, BottomBarCenterListBox, BottomBarRightListBox }, selectedList);
            var destinationIndex = direction == "left" ? listIndex - 1 : listIndex + 1;
            if (destinationIndex is < 0 or > 2)
                return;
            destination = GetBottomBarCollection(new[] { BottomBarLeftListBox, BottomBarCenterListBox, BottomBarRightListBox }[destinationIndex]);
        }

        var item = source[index];
        source.RemoveAt(index);
        if (!ReferenceEquals(source, destination))
            destination.Add(item);
        else
        {
            var targetIndex = direction == "up" ? index - 1 : index + 1;
            targetIndex = Math.Clamp(targetIndex, 0, source.Count);
            source.Insert(targetIndex, item);
        }

        LoadBottomBarLayoutPreset(new BottomBarLayoutPresetData
        {
            Name = BottomBarPresetNameTextBox.Text,
            LeftItems = [.. _bottomBarLeftItems.Select(entry => entry.Id)],
            CenterItems = [.. _bottomBarCenterItems.Select(entry => entry.Id)],
            RightItems = [.. _bottomBarRightItems.Select(entry => entry.Id)],
            Spacing = int.TryParse(BottomBarSpacingTextBox.Text, out var spacing) ? spacing : 4
        });
        if (destination.Count > 0)
        {
            var targetList = new[] { BottomBarLeftListBox, BottomBarCenterListBox, BottomBarRightListBox }
                .FirstOrDefault(list => ReferenceEquals(GetBottomBarCollection(list), destination));
        if (targetList is not null)
                targetList.SelectedItem = item;
        }
        PublishBottomBarLayoutPreview();
    }

    private void BottomBarLayoutList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;
        _bottomBarDragSource = listBox;
        _bottomBarDragStart = e.GetPosition(listBox);
    }

    private void BottomBarLayoutList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListBox listBox ||
            listBox.SelectedItem is not BottomBarLayoutEditorItem item ||
            _bottomBarDragSource != listBox)
            return;
        var point = e.GetPosition(listBox);
        if (Math.Abs(point.X - _bottomBarDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _bottomBarDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        DragDrop.DoDragDrop(listBox, item, DragDropEffects.Move);
        _bottomBarDragSource = null;
    }

    private static ListBoxItem? FindBottomBarItemContainer(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item)
                return item;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void BottomBarLayoutList_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(BottomBarLayoutEditorItem))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void BottomBarLayoutList_OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox targetList || _bottomBarDragSource is null ||
            !e.Data.GetDataPresent(typeof(BottomBarLayoutEditorItem)) ||
            e.Data.GetData(typeof(BottomBarLayoutEditorItem)) is not BottomBarLayoutEditorItem item)
            return;

        var sourceList = _bottomBarDragSource;
        var source = GetBottomBarCollection(sourceList);
        var target = GetBottomBarCollection(targetList);
        source.Remove(item);
        var index = targetList.Items.Count;
        var container = FindBottomBarItemContainer(targetList.InputHitTest(e.GetPosition(targetList)) as DependencyObject);
        if (container is not null)
        {
            var hitIndex = targetList.ItemContainerGenerator.IndexFromContainer(container);
            if (hitIndex >= 0)
                index = hitIndex;
        }
        target.Insert(Math.Clamp(index, 0, target.Count), item);
        targetList.SelectedItem = item;
        _bottomBarDragSource = null;
        e.Handled = true;
        PublishBottomBarLayoutPreview();
    }

    private void BuildFileAssociationChoices(
        IReadOnlyList<string>? selectedExtensions,
        IReadOnlyList<CustomFileAssociationData>? customTypes)
    {
        var selected = FileAssociationService.NormalizeExtensions(selectedExtensions);
        var useAllByDefault = selected.Length == 0;

        foreach (var type in FileAssociationService.SupportedFileTypes)
        {
            var checkBox = new CheckBox
            {
                Content = LocalizationService.Get(type.Label),
                Tag = type.Extension,
                IsChecked = useAllByDefault || selected.Contains(type.Extension, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = LocalizationService.Format("Associer les fichiers {0} à Fuze", type.Extension),
                Style = (Style)FindResource("SettingsToggleWithText")
            };
            (type.IsAudio ? FileAssociationAudioPanel : FileAssociationVideoPanel).Children.Add(checkBox);
        }

        _customFileAssociationTypes.Clear();
        _customFileAssociationTypes.AddRange(
            FileAssociationService.NormalizeCustomTypes(customTypes)
                .Select(type => new CustomFileAssociationData
                {
                    Title = type.Title,
                    Extension = type.Extension,
                    IsAudio = type.IsAudio,
                    Enabled = type.Enabled
                }));
        RefreshCustomFileAssociationChoices();
    }

    private IReadOnlyList<string> GetSelectedFileAssociationExtensions() =>
        // The built-in formats are no longer individual user choices. Keep the
        // complete supported set in the snapshot so older settings are upgraded
        // automatically when they are saved again.
        FileAssociationService.SupportedFileTypes
            .Select(type => type.Extension)
            .ToArray();

    private void UpdateFileAssociationChoicesState()
    {
        if (FileAssociationExtensionsPanel is not null)
            FileAssociationExtensionsPanel.IsEnabled = FileAssociationsEnabledCheckBox.IsChecked == true;
    }

    private void RefreshCustomFileAssociationChoices()
    {
        UpdateCustomFileAssociationEnabledStates();
        foreach (var row in _customFileAssociationRows.Values.ToArray())
        {
            if (row.Parent is Panel parent)
                parent.Children.Remove(row);
        }
        _customFileAssociationRows.Clear();

        foreach (var type in _customFileAssociationTypes)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var checkBox = new CheckBox
            {
                Content = $"{type.Title} ({type.Extension}) · {LocalizationService.Get(type.IsAudio ? "Audio" : "Vidéo")}",
                Tag = type.Extension,
                IsChecked = type.Enabled,
                ToolTip = LocalizationService.Format("Associer les fichiers {0} à Fuze", type.Extension),
                Style = (Style)FindResource("SettingsToggleWithText")
            };
            Grid.SetColumn(checkBox, 0);
            row.Children.Add(checkBox);

            var removeButton = new Button
            {
                Content = "×",
                Tag = type,
                Width = 27,
                Height = 27,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = LocalizationService.Get("Retirer ce type de fichier"),
                Style = (Style)FindResource("TextButton")
            };
            removeButton.Click += RemoveCustomFileAssociation_OnClick;
            Grid.SetColumn(removeButton, 1);
            row.Children.Add(removeButton);
            (type.IsAudio ? FileAssociationAudioPanel : FileAssociationVideoPanel).Children.Add(row);
            _customFileAssociationRows[type.Extension] = row;
        }
    }

    private void UpdateCustomFileAssociationEnabledStates()
    {
        foreach (var type in _customFileAssociationTypes)
        {
            if (_customFileAssociationRows.TryGetValue(type.Extension, out var row) &&
                row.Children.OfType<CheckBox>().FirstOrDefault() is { } checkBox)
                type.Enabled = checkBox.IsChecked == true;
        }
    }

    private void AddCustomFileAssociation_OnClick(object sender, RoutedEventArgs e)
    {
        var title = CustomFileTitleTextBox.Text.Trim();
        var extensionText = CustomFileExtensionTextBox.Text.Trim();
        var extension = extensionText.StartsWith('.') ? extensionText : $".{extensionText}";
        var isAudio = string.Equals(GetSelectedTag(CustomFileKindComboBox, "video"), "audio",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(title) ||
            FileAssociationService.NormalizeCustomTypes(
                [new CustomFileAssociationData { Title = title, Extension = extension, IsAudio = isAudio }]).Length == 0)
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez un titre et une extension valide, par exemple : MP3 et .mp3."),
                LocalizationService.Get("Format personnalisé"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FileAssociationService.SupportedFileTypes.Any(type =>
                string.Equals(type.Extension, extension, StringComparison.OrdinalIgnoreCase)) ||
            _customFileAssociationTypes.Any(type =>
                string.Equals(type.Extension, extension, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, LocalizationService.Get("Cette extension existe déjà dans la liste."),
                LocalizationService.Get("Format personnalisé"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _customFileAssociationTypes.Add(new CustomFileAssociationData
        {
            Title = title,
            Extension = extension.ToLowerInvariant(),
            IsAudio = isAudio
        });
        CustomFileTitleTextBox.Clear();
        CustomFileExtensionTextBox.Clear();
        SelectTaggedItem(CustomFileKindComboBox, "video", "video");
        RefreshCustomFileAssociationChoices();
    }

    private void RemoveCustomFileAssociation_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CustomFileAssociationData type })
        {
            _customFileAssociationTypes.Remove(type);
            RefreshCustomFileAssociationChoices();
        }
    }

    private void ExportSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizationService.Get("Exporter les paramètres Fuze"),
            Filter = "Configuration Fuze (*.json)|*.json|Tous les fichiers|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "fuze-parametres.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true)
            ExportSettingsRequested?.Invoke(dialog.FileName);
    }

    private void ImportSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("Importer les paramètres Fuze"),
            Filter = "Configuration Fuze (*.json)|*.json|Tous les fichiers|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            ImportSettingsRequested?.Invoke(dialog.FileName);
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RewindTextBox.Text, out var rewind) || rewind is < 1 or > 600 ||
            !int.TryParse(ForwardTextBox.Text, out var forward) || forward is < 1 or > 600)
        {
            MessageBox.Show(this, LocalizationService.Get("Entrez des valeurs comprises entre 1 et 600 secondes."),
                LocalizationService.Get("Sauts de lecture"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseRange(ResumePromptStartSkipPercentTextBox, 0, 100, out var resumePromptStartSkipPercent) ||
            !TryParseRange(ResumePromptEndSkipPercentTextBox, 0, 100, out var resumePromptEndSkipPercent))
        {
            MessageBox.Show(this,
                LocalizationService.Get(
                    "Entrez des pourcentages compris entre 0 et 100 pour ignorer la question de reprise."),
                LocalizationService.Get("Reprise de lecture"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseAutoHideDelay(TopBarAutoHideDelayTextBox, out var topBarAutoHideDelay) ||
            !TryParseAutoHideDelay(BottomBarAutoHideDelayTextBox, out var bottomBarAutoHideDelay) ||
            !TryParseAutoHideDelay(CursorAutoHideDelayTextBox, out var cursorAutoHideDelay) ||
            !TryParseAutoHideDelay(VolumeIndicatorHideDelayTextBox, out var volumeIndicatorHideDelay) ||
            !TryParseAutoHideDelay(VolumePopupHideDelayTextBox, out var volumePopupHideDelay))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez pour chaque barre et pour la souris un délai compris entre 0,1 et 10 secondes."),
                LocalizationService.Get("Disparition automatique"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseRange(PlaylistScrollSpeedTextBox, 1, 100, out var playlistScrollSpeed))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez une vitesse de défilement comprise entre 1 et 100."),
                LocalizationService.Get("File des médias"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseRange(RecentMediaRetentionDaysTextBox, 0, 3650, out var recentMediaRetentionDays))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez une durée comprise entre 0 et 3 650 jours. La valeur 0 conserve l’historique sans limite."),
                LocalizationService.Get("Conservation de l’historique"), MessageBoxButton.OK, MessageBoxImage.Warning);
            RecentMediaRetentionDaysTextBox.Focus();
            return;
        }

        if (!TryParseRange(RecentMediaFolderDepthTextBox, 0, 10, out var recentMediaFolderDepth))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez un nombre de dossiers compris entre 0 et 10."),
                LocalizationService.Get("Médias récents"), MessageBoxButton.OK, MessageBoxImage.Warning);
            RecentMediaFolderDepthTextBox.Focus();
            RecentMediaFolderDepthTextBox.SelectAll();
            return;
        }

        if (!TryParseRange(PlaylistFolderDepthTextBox, 0, 10, out var playlistFolderDepth))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez un nombre de dossiers compris entre 0 et 10."),
                LocalizationService.Get("File des médias"), MessageBoxButton.OK, MessageBoxImage.Warning);
            PlaylistFolderDepthTextBox.Focus();
            PlaylistFolderDepthTextBox.SelectAll();
            return;
        }

        if (!TryParseRange(CustomZoomPercentTextBox, 50, 1000, out var customZoomPercent))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez un zoom personnalisé compris entre 50 et 1 000 %."),
                LocalizationService.Get("Zoom vidéo"), MessageBoxButton.OK, MessageBoxImage.Warning);
            CustomZoomPercentTextBox.Focus();
            CustomZoomPercentTextBox.SelectAll();
            return;
        }

        if (!TryParseAspectRatio(CustomAspectRatioTextBox, out var customAspectRatio))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez un format d’image valide, par exemple 16:9 ou 21:9."),
                LocalizationService.Get("Format d’image"), MessageBoxButton.OK, MessageBoxImage.Warning);
            CustomAspectRatioTextBox.Focus();
            CustomAspectRatioTextBox.SelectAll();
            return;
        }

        if (!int.TryParse(AudioDelayTextBox.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var audioDelay) || audioDelay is < -30000 or > 30000)
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez un délai audio compris entre -30 000 et 30 000 ms."),
                LocalizationService.Get("Synchronisation audio"), MessageBoxButton.OK, MessageBoxImage.Warning);
            AudioDelayTextBox.Focus();
            AudioDelayTextBox.SelectAll();
            return;
        }

        if (!TryParseRange(StartupTitleDelayTextBox, 0, 30000, out var titleDelay) ||
            !TryParseRange(StartupTitleDurationTextBox, 250, 30000, out var titleDuration))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Le délai doit être compris entre 0 et 30 000 ms, et la durée entre 250 et 30 000 ms."),
                LocalizationService.Get("Titre au démarrage"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!CaptureActiveStyleDraft(true))
            return;

        var screenshotBaseDirectory = Environment.ExpandEnvironmentVariables(
            ScreenshotBaseDirectoryTextBox.Text.Trim());
        if (string.IsNullOrWhiteSpace(screenshotBaseDirectory))
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Choisissez un emplacement pour les captures d’écran."),
                LocalizationService.Get("Captures d’écran"), MessageBoxButton.OK, MessageBoxImage.Warning);
            ScreenshotBaseDirectoryTextBox.Focus();
            return;
        }
        try
        {
            screenshotBaseDirectory = Path.GetFullPath(screenshotBaseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Choisissez un emplacement valide pour les captures d’écran."),
                LocalizationService.Get("Captures d’écran"), MessageBoxButton.OK, MessageBoxImage.Warning);
            ScreenshotBaseDirectoryTextBox.Focus();
            return;
        }

        var screenshotFolderName = ScreenshotFolderNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(screenshotFolderName) ||
            screenshotFolderName is "." or ".." ||
            screenshotFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, LocalizationService.Get(
                    "Entrez un nom de sous-dossier valide pour les captures."),
                LocalizationService.Get("Captures d’écran"), MessageBoxButton.OK, MessageBoxImage.Warning);
            ScreenshotFolderNameTextBox.Focus();
            ScreenshotFolderNameTextBox.SelectAll();
            return;
        }

        var preferredAudioTitles = GetPriorityTitles(_audioPriorityItems);
        var preferredSubtitleTitles = GetPriorityTitles(_subtitlePriorityItems);
        var fileAssociationExtensions = GetSelectedFileAssociationExtensions();
        UpdateCustomFileAssociationEnabledStates();
        var customFileAssociationTypes = FileAssociationService.NormalizeCustomTypes(_customFileAssociationTypes);
        var fileAssociationsEnabled = FileAssociationsEnabledCheckBox.IsChecked == true;
        var volumeControlStyle = int.TryParse(
                GetSelectedTag(VolumeControlStyleComboBox, "3"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedVolumeControlStyle)
            ? Math.Clamp(parsedVolumeControlStyle, 0, 3)
            : 3;

        var activeBottomBarPreset = BottomBarInterfaceComboBox.SelectedItem as BottomBarLayoutPresetData
            ?? _bottomBarPresets.FirstOrDefault();

        RewindSeconds = rewind;
        ForwardSeconds = forward;
        PrioritizeChapters = PrioritizeChaptersCheckBox.IsChecked == true;
        PlayNextMediaAutomatically = PlayNextMediaCheckBox.IsChecked == true;
        PlaybackSettings = new PlaybackSettingsSnapshot(
            ResumePlaybackCheckBox.IsChecked == true,
            resumePromptStartSkipPercent,
            resumePromptEndSkipPercent,
            AutoPlayOnOpenCheckBox.IsChecked == true,
            ConfirmCloseCheckBox.IsChecked == true,
            BufferingEnabledCheckBox.IsChecked == true,
            PreventSleepDuringPlaybackCheckBox.IsChecked == true,
            RememberMediaSettingsCheckBox.IsChecked == true,
            recentMediaRetentionDays,
            recentMediaFolderDepth,
            playlistFolderDepth,
            RepeatPlaylistCheckBox.IsChecked == true,
            EnhancedPlaybackCheckBox.IsChecked == true,
            EnhancedFolderAdvanceCheckBox.IsChecked == true,
            EnhancedFolderShowNameCheckBox.IsChecked == true,
            ShowEnhancedUpcomingInPlaylistCheckBox.IsChecked == true,
            ShowEnhancedNextFolderInPlaylistCheckBox.IsChecked == true,
            fileAssociationsEnabled,
            fileAssociationExtensions,
            customFileAssociationTypes);
        InterfaceSettings = new InterfaceSettingsSnapshot(
            topBarAutoHideDelay,
            bottomBarAutoHideDelay,
            playlistScrollSpeed,
            volumeControlStyle,
            volumePopupHideDelay,
            volumeIndicatorHideDelay,
            HideInterfaceOnVideoStartCheckBox.IsChecked == true,
            ShowSynchronizationButtonCheckBox.IsChecked == true,
            ShowShuffleButtonCheckBox.IsChecked == true,
            ShowRepeatButtonCheckBox.IsChecked == true,
            ShowSpeedButtonCheckBox.IsChecked == true,
            ShowPlaylistButtonCheckBox.IsChecked == true,
            ShowAdditionalMediaInformationCheckBox.IsChecked == true,
            AutoCompactMissingBottomBarItemsCheckBox.IsChecked == true,
            ShufflePlaybackCheckBox.IsChecked == true,
            RepeatPlaybackCheckBox.IsChecked == true,
            ShowScreenshotButtonCheckBox.IsChecked == true,
            AdaptiveInterfaceScaleCheckBox.IsChecked == true,
            AutoHideCursorCheckBox.IsChecked == true,
            cursorAutoHideDelay,
            AlwaysOnTopCheckBox.IsChecked == true,
            ShowOsdCheckBox.IsChecked == true,
            true,
            DiagnosticLoggingCheckBox.IsChecked == true,
            DisableToolTipsCheckBox.IsChecked == true,
            GetSelectedTag(InterfaceLanguageComboBox, "en"),
            TogglePlaybackOnSingleClickCheckBox.IsChecked == true,
            ToggleFullscreenOnDoubleClickCheckBox.IsChecked == true,
            [.. _bottomBarPresets.Select(CloneBottomBarPreset)],
            activeBottomBarPreset?.Name ?? "Fuze — compacte",
            ShowChapterNameInSeekPreviewCheckBox.IsChecked == true,
            ShowVideoPanButtonCheckBox.IsChecked == true);
        ResetVolumeOnStartup = ResetVolumeCheckBox.IsChecked == true;
        StartupVolume = Math.Clamp((int)Math.Round(StartupVolumeSlider.Value), 0, 125);
        VideoSettings = new VideoSettingsSnapshot(
            StartVideoFullscreenCheckBox.IsChecked == true,
            GetSelectedTag(VideoDisplayComboBox, "auto"),
            GetSelectedTag(VideoOutputComboBox, "auto"),
            HardwareDecodingCheckBox.IsChecked == true,
            DeinterlacingCheckBox.IsChecked == true,
            GetSelectedTag(HdrModeComboBox, "auto"),
            screenshotBaseDirectory,
            screenshotFolderName,
            GetSelectedTag(ScreenshotFormatComboBox, "png"),
            GetSelectedTag(ScreenshotAffixModeComboBox, "prefix"),
            ScreenshotAffixTextBox.Text.Trim(),
            ScreenshotSequentialNumberingCheckBox.IsChecked == true,
            CopyScreenshotsToClipboardCheckBox.IsChecked == true,
            customZoomPercent,
            customAspectRatio);
        ShortcutSettings = new ShortcutSettingsSnapshot(
            _shortcutItems.ToDictionary(item => item.Id, item => item.EncodedGesture,
                StringComparer.OrdinalIgnoreCase),
            MouseWheelTimelineCheckBox.IsChecked == true,
            MouseWheelVolumeCheckBox.IsChecked == true,
            CenterWheelVolumeCheckBox.IsChecked == true,
            CenterWheelTimelineCheckBox.IsChecked == true && CenterWheelVolumeCheckBox.IsChecked != true,
            MouseWheelAudioTracksCheckBox.IsChecked == true,
            MouseWheelSubtitleTracksCheckBox.IsChecked == true,
            IgnoreKeyboardVolumeButtonsCheckBox.IsChecked == true);
        var selectedAudioDevice = GetSelectedTag(AudioDeviceComboBox, "auto");
        // Le mode adaptatif est un choix de sortie virtuel dans cette fenêtre;
        // il ne doit jamais être transmis au moteur comme un nom de périphérique.
        SelectedAudioDevice = string.Equals(selectedAudioDevice, AdaptiveAudioDeviceTag,
                StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : selectedAudioDevice;
        AudioPassthrough = AudioPassthroughCheckBox.IsChecked == true;
        AudioExclusive = AudioExclusiveCheckBox.IsChecked == true;
        DisableAudioByDefault = DisableAudioByDefaultCheckBox.IsChecked == true;
        AutoSelectPreferredAudio = AutoSelectPreferredAudioCheckBox.IsChecked == true;
        PreferredAudioProfile = AutoSelectPreferredAudio
            ? GetSelectedTag(PreferredAudioComboBox, "disabled")
            : "disabled";
        PreferredAudioTitlePriorities = preferredAudioTitles;
        AudioOutputMode = GetSelectedTag(AudioOutputModeComboBox, AudioOutputMode.Automatic);
        AudioTreatmentMode = GetSelectedTreatments();
        DefaultAudioDelayMilliseconds = audioDelay;
        AudioNormalization = AudioNormalizationCheckBox.IsChecked == true;
        AutoSwitchAudioDevice = AutoSwitchAudioDeviceCheckBox.IsChecked == true;
        AdaptiveAudioModeEnabled = IsAdaptiveAudioDeviceSelected();
        AdaptiveAudioDisplayMappings = _adaptiveAudioDeviceSelectors
            .Select(pair => new AdaptiveAudioDisplayMappingData
            {
                DisplayId = pair.Key,
                DisplayName = pair.Value.Tag as string ?? pair.Key,
                AudioDevice = GetSelectedTag(pair.Value, "auto")
            })
            .ToArray();
        SubtitleSettings = new SubtitleSettingsSnapshot(
            StartupTitleOverlayCheckBox.IsChecked == true,
            PreferOriginalTitleCheckBox.IsChecked == true,
            _titleStyle.Position,
            titleDelay,
            titleDuration,
            _titleStyle.Font,
            _titleStyle.FontSize,
            _titleStyle.TextColor,
            _titleStyle.BorderColor,
            _titleStyle.BorderSize,
            _titleStyle.Shadow,
            _titleStyle.MarginX,
            _titleStyle.MarginY,
            _titleStyle.ScaleWithWindow,
            AutoSelectPreferredSubtitleCheckBox.IsChecked == true,
            GetSelectedTag(PreferredSubtitleComboBox, "default"),
            preferredSubtitleTitles,
            PreferSdhSubtitlesCheckBox.IsChecked == true,
            DisableSubtitlesByDefaultCheckBox.IsChecked == true,
            AutoLoadExternalSubtitlesCheckBox.IsChecked == true,
            GetSelectedTag(SubtitleEncodingComboBox, "auto"),
            _subtitleStyle.Font,
            _subtitleStyle.FontSize,
            _subtitleStyle.TextColor,
            _subtitleStyle.BorderColor,
            _subtitleStyle.BorderSize,
            _subtitleStyle.Shadow,
            _subtitleStyle.ForcePosition,
            _subtitleStyle.Position,
            _subtitleStyle.MarginX,
            _subtitleStyle.MarginY,
            _subtitleStyle.ScaleWithWindow);
        DialogResult = true;
    }

    private void ResetDefaults_OnClick(object sender, RoutedEventArgs e)
    {
        ShowPriorityPrompt(
            "Tout réinitialiser",
            "Cette action est irréversible. Êtes-vous sûr de vouloir réinitialiser tous les paramètres de Fuze?",
            "Tout réinitialiser",
            ResetAllSettingsToDefaults,
            showCancel: true);
    }

    private void ResetShortcuts_OnClick(object sender, RoutedEventArgs e)
    {
        ShowPriorityPrompt(
            "Réinitialiser les raccourcis ?",
            "Cette action est irréversible. Êtes-vous sûr de vouloir réinitialiser tous les raccourcis de Fuze?",
            "Réinitialiser",
            ResetShortcutsToDefaults,
            showCancel: true);
    }

    private void ResetShortcutsToDefaults()
    {
        foreach (var shortcut in _shortcutItems)
            shortcut.EncodedGesture = shortcut.DefaultGesture;

        _shortcutView?.Refresh();
    }

    private void ResetAllSettingsToDefaults()
    {
        RewindTextBox.Text = "15";
        ForwardTextBox.Text = "30";
        PrioritizeChaptersCheckBox.IsChecked = true;
        PlayNextMediaCheckBox.IsChecked = true;
        EnhancedPlaybackCheckBox.IsChecked = true;
        EnhancedFolderAdvanceCheckBox.IsChecked = false;
        EnhancedFolderShowNameCheckBox.IsChecked = true;
        ShowEnhancedUpcomingInPlaylistCheckBox.IsChecked = true;
        ShowEnhancedNextFolderInPlaylistCheckBox.IsChecked = false;
        ResumePlaybackCheckBox.IsChecked = true;
        ResumePromptStartSkipPercentTextBox.Text = "5";
        ResumePromptEndSkipPercentTextBox.Text = "5";
        AutoPlayOnOpenCheckBox.IsChecked = true;
        ConfirmCloseCheckBox.IsChecked = false;
        BufferingEnabledCheckBox.IsChecked = true;
        PreventSleepDuringPlaybackCheckBox.IsChecked = true;
        RememberMediaSettingsCheckBox.IsChecked = false;
        RecentMediaRetentionDaysTextBox.Text = "0";
        RecentMediaFolderDepthTextBox.Text = "2";
        PlaylistFolderDepthTextBox.Text = "2";
        RepeatPlaylistCheckBox.IsChecked = false;
        FileAssociationsEnabledCheckBox.IsChecked = true;
        foreach (var checkBox in FileAssociationVideoPanel.Children.OfType<CheckBox>()
                     .Concat(FileAssociationAudioPanel.Children.OfType<CheckBox>()))
            checkBox.IsChecked = true;
        _customFileAssociationTypes.Clear();
        RefreshCustomFileAssociationChoices();
        ShufflePlaybackCheckBox.IsChecked = false;
        RepeatPlaybackCheckBox.IsChecked = false;
        ShowScreenshotButtonCheckBox.IsChecked = false;
        AdaptiveInterfaceScaleCheckBox.IsChecked = true;
        AutoHideCursorCheckBox.IsChecked = true;
        CursorAutoHideDelayTextBox.Text = "3";
        AlwaysOnTopCheckBox.IsChecked = true;
        ShowOsdCheckBox.IsChecked = true;
        DisableToolTipsCheckBox.IsChecked = false;
        ShowChapterNameInSeekPreviewCheckBox.IsChecked = true;
        SelectTaggedItem(InterfaceLanguageComboBox, "en", "en");
        TogglePlaybackOnSingleClickCheckBox.IsChecked = true;
        ToggleFullscreenOnDoubleClickCheckBox.IsChecked = true;
        DiagnosticLoggingCheckBox.IsChecked = false;
        TopBarAutoHideDelayTextBox.Text = "1,5";
        BottomBarAutoHideDelayTextBox.Text = "0,5";
        PlaylistScrollSpeedTextBox.Text = "20";
        VolumeIndicatorHideDelayTextBox.Text = "1";
        SelectTaggedItem(VolumeControlStyleComboBox, "3", "3");
        VolumePopupHideDelayTextBox.Text = "2";
        HideInterfaceOnVideoStartCheckBox.IsChecked = true;
        ShowSynchronizationButtonCheckBox.IsChecked = false;
        ShowShuffleButtonCheckBox.IsChecked = false;
        ShowRepeatButtonCheckBox.IsChecked = false;
        ShowSpeedButtonCheckBox.IsChecked = true;
        ShowPlaylistButtonCheckBox.IsChecked = true;
        ShowVideoPanButtonCheckBox.IsChecked = false;
        ShowAdditionalMediaInformationCheckBox.IsChecked = false;
        AutoCompactMissingBottomBarItemsCheckBox.IsChecked = true;
        _loadingBottomBarLayout = true;
        _bottomBarPresets.Clear();
        _bottomBarPresets.Add(CreateCompactBottomBarPreset());
        _bottomBarPresets.Add(CreateCinemaBottomBarPreset());
        _bottomBarPresets.Add(CreatePrimaryClassicBottomBarPreset());
        BottomBarPresetComboBox.SelectedItem = _bottomBarPresets[2];
        BottomBarInterfaceComboBox.SelectedItem = _bottomBarPresets[2];
        _loadingBottomBarLayout = false;
        LoadBottomBarLayoutPreset(_bottomBarPresets[2]);
        UpdateBottomBarInterfaceDeleteButtonState();
        StartVideoFullscreenCheckBox.IsChecked = true;
        SelectTaggedItem(VideoDisplayComboBox, "auto", "auto");
        SelectTaggedItem(VideoOutputComboBox, "auto", "auto");
        CustomZoomPercentTextBox.Text = "100";
        CustomAspectRatioTextBox.Text = "16:9";
        HardwareDecodingCheckBox.IsChecked = true;
        DeinterlacingCheckBox.IsChecked = false;
        SelectTaggedItem(HdrModeComboBox, "auto", "auto");
        ScreenshotBaseDirectoryTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        ScreenshotFolderNameTextBox.Text = "Fuze";
        SelectTaggedItem(ScreenshotFormatComboBox, "png", "png");
        SelectTaggedItem(ScreenshotAffixModeComboBox, "prefix", "prefix");
        ScreenshotAffixTextBox.Text = "Fuze";
        ScreenshotSequentialNumberingCheckBox.IsChecked = false;
        CopyScreenshotsToClipboardCheckBox.IsChecked = false;
        foreach (var shortcut in _shortcutItems)
            shortcut.EncodedGesture = shortcut.DefaultGesture;
        ShortcutSearchTextBox.Clear();
        MouseWheelTimelineCheckBox.IsChecked = true;
        MouseWheelVolumeCheckBox.IsChecked = true;
        CenterWheelVolumeCheckBox.IsChecked = true;
        CenterWheelTimelineCheckBox.IsChecked = false;
        MouseWheelAudioTracksCheckBox.IsChecked = true;
        MouseWheelSubtitleTracksCheckBox.IsChecked = true;
        IgnoreKeyboardVolumeButtonsCheckBox.IsChecked = false;
        ResetVolumeCheckBox.IsChecked = true;
        StartupVolumeSlider.Value = 100;
        SelectAudioDevice("auto");
        AudioPassthroughCheckBox.IsChecked = false;
        AudioExclusiveCheckBox.IsChecked = false;
        DisableAudioByDefaultCheckBox.IsChecked = false;
        AutoSelectPreferredAudioCheckBox.IsChecked = false;
        SelectTaggedItem(PreferredAudioComboBox, "disabled", "disabled");
        _audioPriorityItems.Clear();
        SelectTaggedItem(AudioOutputModeComboBox, AudioOutputMode.Automatic, AudioOutputMode.Automatic);
        NightModeCheckBox.IsChecked = false;
        DialogueBoostCheckBox.IsChecked = false;
        HeadphoneBinauralCheckBox.IsChecked = false;
        SurroundDownmixCheckBox.IsChecked = false;
        AudioDelayTextBox.Text = "0";
        AudioNormalizationCheckBox.IsChecked = false;
        AutoSwitchAudioDeviceCheckBox.IsChecked = false;
        foreach (var selector in _adaptiveAudioDeviceSelectors.Values)
            SelectTaggedItem(selector, "auto", "auto");
        UpdateAdaptiveAudioControls();
        StartupTitleOverlayCheckBox.IsChecked = true;
        PreferOriginalTitleCheckBox.IsChecked = false;
        StartupTitleDelayTextBox.Text = "250";
        StartupTitleDurationTextBox.Text = "3000";
        AutoSelectPreferredSubtitleCheckBox.IsChecked = false;
        SelectTaggedItem(PreferredSubtitleComboBox, "default", "default");
        _subtitlePriorityItems.Clear();
        PreferSdhSubtitlesCheckBox.IsChecked = false;
        DisableSubtitlesByDefaultCheckBox.IsChecked = false;
        AutoLoadExternalSubtitlesCheckBox.IsChecked = false;
        SelectTaggedItem(SubtitleEncodingComboBox, "auto", "auto");
        SetDraft(_titleStyle, "Arial", 42, "#FFFFFFFF", "#FF000000", 2.5,
            true, true, "top-center", 20, 36, true);
        SetDraft(_subtitleStyle, "Arial", 42, "#FFFFFFFF", "#FF000000", 2.5,
            true, false, "bottom-center", 20, 36, true);
        _editTarget = EditTarget.None;
        LoadStyleDraft(_subtitleStyle);
        UpdateStartupVolumeControls();
        UpdatePreferredAudioControls();
        UpdatePassthroughControls();
        UpdateStartupTitleControls();
        UpdatePreferredSubtitleControls();
        UpdateSubtitlePositionControls();
        UpdateEditTargetVisuals();
        UpdatePriorityCounts();
        UpdateSubtitlePreview();
    }

    private void CategoryList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowSelectedCategory();

    private void InterfaceLanguageComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingInterfaceLanguage ||
            InterfaceLanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string language })
            return;

        LocalizationService.SetLanguage(language);
        RefreshLocalizedSettingsContent();
    }

    private void RefreshLocalizedSettingsContent()
    {
        foreach (var shortcut in _shortcutItems)
            shortcut.RefreshLocalization();

        foreach (var item in _bottomBarLeftItems)
            item.RefreshLocalization();
        foreach (var item in _bottomBarCenterItems)
            item.RefreshLocalization();
        foreach (var item in _bottomBarRightItems)
            item.RefreshLocalization();
        foreach (var preset in _bottomBarPresets)
            preset.RefreshLocalizedDisplayName();

        BottomBarPresetComboBox?.Items.Refresh();
        BottomBarInterfaceComboBox?.Items.Refresh();
        _shortcutView?.Refresh();

        // Ces contrôles sont créés dynamiquement après le chargement XAML.
        // Reconstruire uniquement les lignes personnalisées suffit à mettre à
        // jour leurs libellés et info-bulles sans perdre les cases cochées.
        if (FileAssociationAudioPanel is not null && FileAssociationVideoPanel is not null)
            RefreshCustomFileAssociationChoices();

        LocalizationService.ApplyToWindow(this);
        UpdateStartupVolumeControls();
        UpdatePreferredAudioControls();
        UpdatePassthroughControls();
        UpdateStartupTitleControls();
        UpdatePreferredSubtitleControls();
        UpdateSubtitlePositionControls();
        UpdateEditTargetVisuals();
        UpdatePriorityCounts();
        UpdateSubtitlePreview();
    }

    public void SelectCategory(string categoryTag)
    {
        if (string.IsNullOrWhiteSpace(categoryTag))
            return;

        var item = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, categoryTag, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return;

        CategoryList.SelectedItem = item;
        item.Focus();
        ShowSelectedCategory();
    }

    private void SelectAudioDevice(string name) =>
        SelectTaggedItem(AudioDeviceComboBox,
            string.Equals(name, AdaptiveAudioDeviceTag, StringComparison.OrdinalIgnoreCase)
                ? AdaptiveAudioDeviceTag
                : name,
            "auto");

    private void MoveReorganizedSettingsSections()
    {
        MoveChildren(MediaCards, MediaContentPanel);
        MoveChildren(SystemCards, SystemContentPanel);
        MoveChildren(SystemMaintenanceCards, SystemContentPanel);
        MoveChildren(PreventSleepSettingBlock, EnergyContentPanel);
        MoveElement(PreparationStabilityCard, SystemPreparationHost);
        MoveElement(SystemLanguageCard, SystemLanguageHost);
        MoveChildren(HardwareDecodingSettingBlock, PerformanceContentPanel);
        MoveChildren(BufferingSettingBlock, PerformanceContentPanel);
        MoveElement(ChapterTimeBarCard, GeneralTimeBarHost);
    }

    private static void MoveChildren(Panel source, Panel target)
    {
        var children = source.Children.OfType<UIElement>().ToArray();
        foreach (var child in children)
        {
            source.Children.Remove(child);
            target.Children.Add(child);
        }
    }

    private static void MoveElement(FrameworkElement element, Panel target)
    {
        if (element.Parent is Panel source)
            source.Children.Remove(element);
        target.Children.Add(element);
    }

    private bool IsAdaptiveAudioDeviceSelected() =>
        string.Equals(GetSelectedTag(AudioDeviceComboBox, "auto"), AdaptiveAudioDeviceTag,
            StringComparison.OrdinalIgnoreCase);

    private void AddAdaptiveAudioDeviceOption()
    {
        if (AudioDeviceComboBox.Items.OfType<ComboBoxItem>().Any(item =>
                string.Equals(item.Tag as string, AdaptiveAudioDeviceTag,
                    StringComparison.OrdinalIgnoreCase)))
            return;

        AudioDeviceComboBox.Items.Add(new ComboBoxItem
        {
            Content = LocalizationService.Get("Mode audio adaptatif"),
            Tag = AdaptiveAudioDeviceTag,
            ToolTip = LocalizationService.Get("Utilise automatiquement le périphérique associé à l’écran actif")
        });
    }

    private void BuildAdaptiveAudioMappings(
        IReadOnlyList<VideoDisplayDescription> videoDisplays,
        IReadOnlyList<AudioDeviceDescription> audioDevices,
        IReadOnlyList<AdaptiveAudioDisplayMappingData> savedMappings)
    {
        _adaptiveAudioDeviceSelectors.Clear();
        AdaptiveAudioMappingsPanel.Children.Clear();
        var mappings = savedMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.DisplayId))
            .GroupBy(mapping => mapping.DisplayId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var displays = videoDisplays
            .Where(display => !display.Id.Equals("auto", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (displays.Length == 0)
        {
            AdaptiveAudioMappingsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("Aucun écran détecté."),
                Foreground = new SolidColorBrush(Color.FromRgb(142, 150, 161)),
                FontSize = 11.2
            });
            return;
        }

        foreach (var display in displays)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });

            var labelPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            labelPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(display.NumberLabel)
                    ? display.Description
                    : string.IsNullOrWhiteSpace(display.Details)
                        ? display.NumberLabel
                        : $"{display.NumberLabel} — {display.Details}",
                Foreground = new SolidColorBrush(Color.FromRgb(240, 241, 243)),
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var detail = display.FriendlyName;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                labelPanel.Children.Add(new TextBlock
                {
                    Text = detail,
                    Foreground = new SolidColorBrush(Color.FromRgb(127, 135, 146)),
                    FontSize = 10.5,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            row.Children.Add(labelPanel);

            var selector = new ComboBox
            {
                Style = (Style)FindResource("SettingsComboBox"),
                Tag = display.Description,
                MinWidth = 180
            };
            Grid.SetColumn(selector, 1);
            foreach (var device in audioDevices)
            {
                selector.Items.Add(new ComboBoxItem
                {
                    Content = device.Description,
                    Tag = device.Name,
                    ToolTip = device.Name.Equals("auto", StringComparison.OrdinalIgnoreCase)
                        ? LocalizationService.Get("Suit le périphérique sélectionné dans Windows")
                        : device.Name
                });
            }

            var savedDevice = mappings.TryGetValue(display.Id, out var mapping) &&
                              !string.IsNullOrWhiteSpace(mapping.AudioDevice)
                ? mapping.AudioDevice
                : "auto";
            if (!selector.Items.OfType<ComboBoxItem>().Any(item =>
                    string.Equals(item.Tag as string, savedDevice, StringComparison.OrdinalIgnoreCase)))
            {
                selector.Items.Add(new ComboBoxItem
                {
                    Content = string.Format(CultureInfo.InvariantCulture,
                        LocalizationService.Get("Indisponible — {0}"), savedDevice),
                    Tag = savedDevice,
                    ToolTip = LocalizationService.Get("Ce périphérique n’est pas actuellement connecté")
                });
            }

            SelectTaggedItem(selector, savedDevice, "auto");
            row.Children.Add(selector);
            AdaptiveAudioMappingsPanel.Children.Add(row);
            _adaptiveAudioDeviceSelectors[display.Id] = selector;
        }
    }

    private static void SelectTaggedItem<T>(ComboBox comboBox, T value, T fallback)
    {
        var selected = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value));
        selected ??= comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, fallback));
        comboBox.SelectedItem = selected ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static T GetSelectedTag<T>(ComboBox comboBox, T fallback) =>
        comboBox.SelectedItem is ComboBoxItem { Tag: T tag } ? tag : fallback;

    private void ShowSelectedCategory()
    {
        if (GeneralPanel is null || MediaPanel is null || InterfacePanel is null || AudioPanel is null ||
            VideoPanel is null || SubtitlesPanel is null || ShortcutsPanel is null || SystemPanel is null)
            return;

        var category = (CategoryList.SelectedItem as ListBoxItem)?.Tag as string ?? "General";
        GeneralPanel.Visibility = category == "General" ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = category == "Media" ? Visibility.Visible : Visibility.Collapsed;
        InterfacePanel.Visibility = category == "Interface" ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = category == "Audio" ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = category == "Video" ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesPanel.Visibility = category == "Subtitles" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = category == "Shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SystemPanel.Visibility = category == "System" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseScreenshotDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Environment.ExpandEnvironmentVariables(
            ScreenshotBaseDirectoryTextBox.Text.Trim());
        if (!Directory.Exists(initialDirectory))
            initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get("Choisir l’emplacement des captures d’écran"),
            Multiselect = false,
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog(this) == true)
            ScreenshotBaseDirectoryTextBox.Text = dialog.FolderName;
    }

    private void OpenDiagnosticLog_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(DiagnosticLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{DiagnosticLogPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this,
                LocalizationService.Format("Impossible d’ouvrir l’emplacement du journal : {0}", exception.Message),
                LocalizationService.Get("JOURNAL DE DIAGNOSTIC"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShortcutSearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _shortcutView?.Refresh();
        if (ShortcutEmptySearchText is not null && _shortcutView is not null)
            ShortcutEmptySearchText.Visibility = _shortcutView.IsEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ShortcutsPanel_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableHeight <= 0 || e.Delta == 0)
            return;

        // La liste des raccourcis désactive son propre défilement pour laisser
        // la page entière défiler. Certains contrôles marquent toutefois la
        // molette comme traitée avant que le ScrollViewer parent ne la reçoive.
        // On applique donc le déplacement ici, en phase Preview.
        var step = e.Delta / 120.0 * 48.0;
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(
            scrollViewer.VerticalOffset - step, 0, scrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private void ShortcutGestureButton_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutBindingItem shortcut })
            return;

        OpenShortcutCapture(shortcut);
        e.Handled = true;
    }

    private void ShortcutGestureButton_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Button { Tag: ShortcutBindingItem shortcut })
            return;

        shortcut.EncodedGesture = string.Empty;
        _shortcutView?.Refresh();
        e.Handled = true;
    }

    private void OpenShortcutCapture(ShortcutBindingItem shortcut)
    {
        _editingShortcut = shortcut;
        _capturedShortcutGesture = shortcut.EncodedGesture;
        ShortcutCaptureTitleText.Text = LocalizationService.Format("Modifier • {0}", shortcut.Name);
        ShortcutCaptureErrorText.Visibility = Visibility.Collapsed;
        UpdateShortcutCaptureText();
        ShortcutCaptureOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => Keyboard.Focus(ShortcutCaptureKeyBorder), DispatcherPriority.Input);
    }

    private void ShortcutCaptureKeyBorder_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _capturedShortcutGesture = string.Empty;
        }
        else if (!ShortcutCatalog.IsModifierKey(key) && key != Key.None)
        {
            const ModifierKeys supported = ModifierKeys.Control | ModifierKeys.Shift |
                                           ModifierKeys.Alt | ModifierKeys.Windows;
            _capturedShortcutGesture = ShortcutCatalog.Encode(key, Keyboard.Modifiers & supported);
        }

        ShortcutCaptureErrorText.Visibility = Visibility.Collapsed;
        UpdateShortcutCaptureText();
        e.Handled = true;
    }

    private void UpdateShortcutCaptureText()
    {
        ShortcutCaptureKeyText.Text = LocalizationService.Get(
            ShortcutCatalog.Format(_capturedShortcutGesture));
        ShortcutCaptureKeyText.Foreground = string.IsNullOrWhiteSpace(_capturedShortcutGesture)
            ? new SolidColorBrush(Color.FromRgb(142, 150, 161))
            : new SolidColorBrush(Color.FromRgb(244, 245, 247));
    }

    private void ShortcutCaptureApply_OnClick(object sender, RoutedEventArgs e)
    {
        if (_editingShortcut is null)
        {
            CloseShortcutCapture();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_capturedShortcutGesture))
        {
            var conflict = _shortcutItems.FirstOrDefault(item =>
                !ReferenceEquals(item, _editingShortcut) &&
                string.Equals(item.EncodedGesture, _capturedShortcutGesture, StringComparison.Ordinal));
            if (conflict is not null)
            {
                ShortcutCaptureErrorText.Text = LocalizationService.Format(
                    "Cette combinaison est déjà utilisée par « {0} ».", conflict.Name);
                ShortcutCaptureErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        _editingShortcut.EncodedGesture = _capturedShortcutGesture;
        _shortcutView?.Refresh();
        CloseShortcutCapture();
    }

    private void ShortcutCaptureCancel_OnClick(object sender, RoutedEventArgs e) => CloseShortcutCapture();

    private void CloseShortcutCapture()
    {
        ShortcutCaptureOverlay.Visibility = Visibility.Collapsed;
        _editingShortcut = null;
        _capturedShortcutGesture = string.Empty;
        ShortcutCaptureErrorText.Visibility = Visibility.Collapsed;
    }

    private void ResetVolumeCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdateStartupVolumeControls();

    private void AutoSelectPreferredAudioCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdatePreferredAudioControls();

    private void PreferredAudioComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePreferredAudioControls();

    private void DisableAudioByDefaultCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdatePreferredAudioControls();

    private void AudioPassthroughCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdatePassthroughControls();

    private void AudioDeviceComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioDeviceSelection ||
            AudioDeviceComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var selectedTag = item.Tag as string;
        if (string.Equals(selectedTag, AdaptiveAudioDeviceTag,
                StringComparison.OrdinalIgnoreCase))
        {
            UpdateAdaptiveAudioControls();
            return;
        }

        _audioDeviceBeforeAdaptive = string.IsNullOrWhiteSpace(selectedTag)
            ? "auto"
            : selectedTag;
        UpdateAdaptiveAudioControls();
    }

    private void CenterWheelVolumeCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (CenterWheelVolumeCheckBox.IsChecked == true)
            CenterWheelTimelineCheckBox.IsChecked = false;
    }

    private void CenterWheelTimelineCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (CenterWheelTimelineCheckBox.IsChecked == true)
            CenterWheelVolumeCheckBox.IsChecked = false;
    }

    private static void SettingsDialog_OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element)
            ToolTipService.SetIsEnabled(element, false);
    }

    private void StartupTitleOverlayCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdateStartupTitleControls();

    private void AutoSelectPreferredSubtitleCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdatePreferredSubtitleControls();

    private void PreferredSubtitleComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePreferredSubtitleControls();

    private void DisableSubtitlesByDefaultCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdatePreferredSubtitleControls();

    private void SubtitleForcePositionCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingStyleDraft)
            CaptureActiveStyleDraft(false);
        UpdateSubtitlePositionControls();
        UpdateSubtitlePreview();
    }

    private void SubtitlePreviewSetting_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SubtitleFontComboBox))
            UpdateFontSelectionPreview();
        if (!_loadingStyleDraft)
            CaptureActiveStyleDraft(false);
        UpdateSubtitlePreview();
    }

    private void UpdateFontSelectionPreview()
    {
        if (SubtitleFontComboBox is null)
            return;

        var fontName = GetSelectedTag(SubtitleFontComboBox, "Arial");
        SubtitleFontComboBox.FontFamily = new FontFamily(fontName);
    }

    private void SubtitlePreviewToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingStyleDraft)
            CaptureActiveStyleDraft(false);
        UpdateSubtitlePreview();
    }

    private void SubtitlePreviewSetting_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingStyleDraft)
            CaptureActiveStyleDraft(false);
        UpdateSubtitlePreview();
    }

    private void SubtitlePreviewSurface_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid surface)
            surface.Clip = new RectangleGeometry(
                new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height)), 8, 8);
    }

    private void TreatmentCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender == HeadphoneBinauralCheckBox && HeadphoneBinauralCheckBox.IsChecked == true)
            SurroundDownmixCheckBox.IsChecked = false;
        else if (sender == SurroundDownmixCheckBox && SurroundDownmixCheckBox.IsChecked == true)
            HeadphoneBinauralCheckBox.IsChecked = false;
    }

    private AudioTreatmentMode GetSelectedTreatments()
    {
        var mode = AudioTreatmentMode.None;
        if (NightModeCheckBox.IsChecked == true)
            mode |= AudioTreatmentMode.Night;
        if (DialogueBoostCheckBox.IsChecked == true)
            mode |= AudioTreatmentMode.DialogueBoost;
        if (HeadphoneBinauralCheckBox.IsChecked == true)
            mode |= AudioTreatmentMode.HeadphoneBinaural;
        if (SurroundDownmixCheckBox.IsChecked == true)
            mode |= AudioTreatmentMode.SurroundDownmix;
        return mode;
    }

    private void PopulateSubtitleChoices()
    {
        AddTaggedItems(SubtitlePositionComboBox, ScreenPositions());
        AddTaggedItems(PreferredSubtitleComboBox,
        [
            ("Choix du fichier", "default"),
            ("Français québécois (VFQ)", "vfq"),
            ("Français de France (VFF)", "vff"),
            ("Français", "fr"),
            ("Anglais", "en"),
            ("Japonais", "ja"),
            ("Piste forcée", "forced"),
            ("Mes priorités personnalisées", "custom")
        ]);
        AddTaggedItems(SubtitleEncodingComboBox,
        [
            ("Automatique (système)", "auto"),
            ("Unicode UTF-8", "utf-8"),
            ("Unicode UTF-16 LE", "utf-16le"),
            ("Unicode UTF-16 BE", "utf-16be"),
            ("Europe occidentale · Windows-1252", "windows-1252"),
            ("Europe centrale · Windows-1250", "windows-1250"),
            ("Cyrillique · Windows-1251", "windows-1251"),
            ("Grec · Windows-1253", "windows-1253"),
            ("Turc · Windows-1254", "windows-1254"),
            ("Hébreu · Windows-1255", "windows-1255"),
            ("Arabe · Windows-1256", "windows-1256"),
            ("Baltique · Windows-1257", "windows-1257"),
            ("Vietnamien · Windows-1258", "windows-1258"),
            ("Europe occidentale · ISO-8859-1", "iso-8859-1"),
            ("Europe centrale · ISO-8859-2", "iso-8859-2"),
            ("Cyrillique · ISO-8859-5", "iso-8859-5"),
            ("Grec · ISO-8859-7", "iso-8859-7"),
            ("Turc · ISO-8859-9", "iso-8859-9"),
            ("Europe occidentale · ISO-8859-15", "iso-8859-15"),
            ("Europe occidentale · IBM 850", "cp850"),
            ("Europe centrale · IBM 852", "cp852"),
            ("Cyrillique · IBM 866", "cp866"),
            ("Japonais · Shift-JIS", "shift-jis"),
            ("Japonais · EUC-JP", "euc-jp"),
            ("Chinois simplifié · GB18030", "gb18030"),
            ("Chinois traditionnel · Big5", "big5"),
            ("Coréen · EUC-KR", "euc-kr")
        ]);

        string[] fonts =
        [
            "Arial", "Arial Black", "Bahnschrift", "Baskerville Old Face", "Bell MT",
            "Berlin Sans FB", "Book Antiqua", "Bookman Old Style", "Bradley Hand ITC",
            "Britannic Bold", "Calibri", "Calibri Light", "Cambria", "Cambria Math", "Candara",
            "Century", "Century Gothic", "Comic Sans MS", "Consolas", "Constantia", "Corbel",
            "Courier New", "Ebrima", "Franklin Gothic Medium", "Gadugi", "Garamond", "Georgia",
            "Gill Sans MT", "Impact", "Javanese Text", "Leelawadee UI", "Lucida Console",
            "Lucida Sans Unicode", "Malgun Gothic", "Microsoft Himalaya", "Microsoft JhengHei",
            "Microsoft New Tai Lue", "Microsoft PhagsPa", "Microsoft Sans Serif", "Microsoft Tai Le",
            "Microsoft Uighur", "Microsoft YaHei", "Microsoft Yi Baiti", "Mongolian Baiti",
            "MS Gothic", "MS PGothic", "Palatino Linotype", "Segoe UI", "Segoe UI Black",
            "Segoe UI Symbol", "SimSun", "Sitka Text", "Sylfaen", "Tahoma", "Times New Roman",
            "Trebuchet MS", "Verdana", "Yu Gothic"
        ];
        AddFontItems(SubtitleFontComboBox, fonts);
        AddTaggedItems(SubtitleFontSizeComboBox,
            new[] { 16, 18, 20, 22, 24, 26, 28, 30, 32, 36, 42, 48, 54, 60, 72, 84, 96 }
                .Select(size => ($"{size} px", size)));
        AddTaggedItems(SubtitleTextColorComboBox,
        [
            ("Blanc", "#FFFFFFFF"), ("Jaune", "#FFFFFF00"), ("Cyan", "#FF00FFFF"),
            ("Vert clair", "#FF80FF80"), ("Orange", "#FFFFA040"), ("Rose", "#FFFF80C0"),
            ("Gris clair", "#FFD8D8D8"), ("Noir", "#FF000000")
        ]);
        AddTaggedItems(SubtitleBorderColorComboBox,
        [
            ("Noir", "#FF000000"), ("Gris foncé", "#FF303030"), ("Blanc", "#FFFFFFFF"),
            ("Bleu foncé", "#FF102040"), ("Rouge foncé", "#FF501010"),
            ("Transparent", "#00000000")
        ]);
        AddTaggedItems(SubtitleBorderSizeComboBox,
        [
            ("Aucun", 0d), ("Fin", 1d), ("Normal", 2.5d), ("Épais", 4d), ("Très épais", 6d)
        ]);
    }

    private static (string Label, string Tag)[] ScreenPositions() =>
    [
        ("Haut gauche", "top-left"), ("Haut centre", "top-center"), ("Haut droite", "top-right"),
        ("Centre gauche", "center-left"), ("Centre", "center-center"),
        ("Centre droite", "center-right"), ("Bas gauche", "bottom-left"),
        ("Bas centre", "bottom-center"), ("Bas droite", "bottom-right")
    ];

    private static void AddTaggedItems<T>(ComboBox comboBox, IEnumerable<(string Label, T Tag)> items)
    {
        foreach (var (label, tag) in items)
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Get(label),
                Tag = tag
            });
    }

    private static void AddFontItems(ComboBox comboBox, IEnumerable<string> fonts)
    {
        foreach (var font in fonts)
        {
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = font,
                Tag = font,
                FontFamily = new FontFamily(font),
                FontSize = 14,
                ToolTip = LocalizationService.Format("Aperçu de {0}", font)
            });
        }
    }

    private static string FormatAutoHideDelay(int milliseconds) =>
        (Math.Clamp(milliseconds, 100, 10000) / 1000d)
        .ToString("0.##", LocalizationService.CurrentLanguage == "fr"
            ? CultureInfo.GetCultureInfo("fr-CA")
            : CultureInfo.GetCultureInfo("en-US"));

    private static bool TryParseAutoHideDelay(TextBox textBox, out int milliseconds)
    {
        var normalized = textBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var seconds) && seconds is >= 0.1 and <= 10)
        {
            milliseconds = Math.Clamp((int)Math.Round(seconds * 1000d), 100, 10000);
            return true;
        }

        milliseconds = 0;
        textBox.Focus();
        textBox.SelectAll();
        return false;
    }

    private static bool TryParseRange(TextBox textBox, int minimum, int maximum, out int value)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum)
            return true;

        textBox.Focus();
        textBox.SelectAll();
        return false;
    }

    private static string NormalizeAspectRatio(string? value)
    {
        if (TryParseAspectRatio(value, out var normalized))
            return normalized;
        return "16:9";
    }

    private static bool TryParseAspectRatio(TextBox textBox, out string value) =>
        TryParseAspectRatio(textBox.Text, out value);

    private static bool TryParseAspectRatio(string? text, out string value)
    {
        value = "16:9";
        var parts = text?.Trim().Split(':', StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width is < 1 or > 10000 || height is < 1 or > 10000)
            return false;

        value = $"{width}:{height}";
        return true;
    }

    private static void AddPriorityItems(ObservableCollection<PriorityTitleItem> target,
        IEnumerable<string>? values)
    {
        foreach (var value in (values ?? [])
                     .Select(value => value?.Trim())
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .Take(50))
            target.Add(new PriorityTitleItem(value!));
    }

    private static string[] GetPriorityTitles(IEnumerable<PriorityTitleItem> items) => items
            .Select(item => item.Title?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(50)
            .Cast<string>()
            .ToArray();

    private void AddAudioPriority_OnClick(object sender, RoutedEventArgs e) =>
        AddPriority(_audioPriorityItems, PreferredAudioTitlesListBox);

    private void AddSubtitlePriority_OnClick(object sender, RoutedEventArgs e) =>
        AddPriority(_subtitlePriorityItems, PreferredSubtitleTitlesListBox);

    private void ClearAudioPriorities_OnClick(object sender, RoutedEventArgs e) =>
        ConfirmPriorityListClear(_audioPriorityItems, "audio");

    private void ClearSubtitlePriorities_OnClick(object sender, RoutedEventArgs e) =>
        ConfirmPriorityListClear(_subtitlePriorityItems, "de sous-titres");

    private void MergeSubtitlePrioritiesIntoAudio_OnClick(object sender, RoutedEventArgs e) =>
        MergePriorityLists(_subtitlePriorityItems, _audioPriorityItems,
            PreferredAudioTitlesListBox, "de sous-titres", "audio");

    private void MergeAudioPrioritiesIntoSubtitle_OnClick(object sender, RoutedEventArgs e) =>
        MergePriorityLists(_audioPriorityItems, _subtitlePriorityItems,
            PreferredSubtitleTitlesListBox, "audio", "de sous-titres");

    private void AddPriority(ObservableCollection<PriorityTitleItem> items, ListBox listBox)
    {
        if (items.Count >= 50)
            return;

        var item = new PriorityTitleItem(string.Empty);
        items.Add(item);
        UpdatePriorityCounts();
        Dispatcher.BeginInvoke(() =>
        {
            listBox.ScrollIntoView(item);
            listBox.UpdateLayout();
            if (listBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                return;

            var editor = FindVisualChild<TextBox>(container);
            editor?.Focus();
            editor?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void RemovePriority_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PriorityTitleItem item })
            return;

        if (!_audioPriorityItems.Remove(item))
            _subtitlePriorityItems.Remove(item);
        UpdatePriorityCounts();
    }

    private void ConfirmPriorityListClear(ObservableCollection<PriorityTitleItem> items,
        string listName)
    {
        if (items.Count == 0)
        {
            ShowPriorityPrompt(LocalizationService.Get("Liste déjà vide"),
                LocalizationService.Format("La liste {0} ne contient aucun titre à supprimer.", listName),
                "OK");
            return;
        }

        ShowPriorityPrompt(LocalizationService.Get("Supprimer toute la liste ?"),
            LocalizationService.Format("Êtes-vous sûr de vouloir supprimer les {0} titres de la liste {1} ?",
                items.Count, listName),
            LocalizationService.Get("Supprimer"), () =>
            {
                items.Clear();
                UpdatePriorityCounts();
            }, showCancel: true);
    }

    private void MergePriorityLists(IEnumerable<PriorityTitleItem> source,
        ObservableCollection<PriorityTitleItem> target, ListBox targetListBox,
        string sourceName, string targetName)
    {
        var existing = target
            .Select(item => item.Title?.Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Cast<string>()
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var additions = source
            .Select(item => item.Title?.Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Cast<string>()
            .Where(existing.Add)
            .ToArray();

        if (additions.Length == 0)
        {
            ShowPriorityPrompt(LocalizationService.Get("Rien à intégrer"),
                LocalizationService.Format(
                    "La liste {0} ne contient aucun nouveau titre à ajouter à la liste {1}.",
                    sourceName, targetName),
                "OK");
            return;
        }

        var resultingCount = target.Count + additions.Length;
        if (resultingCount > 50)
        {
            var excess = resultingCount - 50;
            ShowPriorityPrompt(LocalizationService.Get("Limite de 50 dépassée"),
                LocalizationService.Format(
                    "L’intégration porterait la liste {0} à {1} titres. Supprimez au moins {2} titre{3}, puis réessayez.",
                    targetName, resultingCount, excess, excess > 1 ? "s" : string.Empty),
                "OK");
            return;
        }

        foreach (var title in additions)
            target.Add(new PriorityTitleItem(title));

        UpdatePriorityCounts();
        var lastItem = target.Last();
        Dispatcher.BeginInvoke(() => targetListBox.ScrollIntoView(lastItem), DispatcherPriority.Input);
    }

    private void ShowPriorityPrompt(string title, string message, string confirmLabel,
        Action? confirmAction = null, bool showCancel = false)
    {
        _pendingPriorityPromptAction = confirmAction;
        PriorityPromptTitleText.Text = LocalizationService.Get(title);
        PriorityPromptMessageText.Text = LocalizationService.Get(message);
        PriorityPromptConfirmButton.Content = LocalizationService.Get(confirmLabel);
        PriorityPromptCancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        PriorityPromptOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => PriorityPromptConfirmButton.Focus(), DispatcherPriority.Input);
    }

    private void PriorityPromptCancel_OnClick(object sender, RoutedEventArgs e) =>
        ClosePriorityPrompt();

    private void PriorityPromptConfirm_OnClick(object sender, RoutedEventArgs e)
    {
        var action = _pendingPriorityPromptAction;
        ClosePriorityPrompt();
        action?.Invoke();
    }

    private void ClosePriorityPrompt()
    {
        _pendingPriorityPromptAction = null;
        PriorityPromptOverlay.Visibility = Visibility.Collapsed;
    }

    private void PriorityDragHandle_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedPriorityItem = (sender as FrameworkElement)?.DataContext as PriorityTitleItem;
        _priorityDragStart = e.GetPosition(this);
    }

    private void PriorityDragHandle_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedPriorityItem is null)
            return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _priorityDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _priorityDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _draggedPriorityItem;
        _draggedPriorityItem = null;
        item.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
        }
        finally
        {
            item.IsDragging = false;
        }
    }

    private void PriorityListBox_OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox ||
            e.Data.GetData(typeof(PriorityTitleItem)) is not PriorityTitleItem item ||
            !GetPriorityItems(listBox).Contains(item))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        AutoScrollPriorityList(listBox, e.GetPosition(listBox));
        MovePriorityItemAtPointer(listBox, item, e.GetPosition(listBox));
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void PriorityListBox_OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox ||
            e.Data.GetData(typeof(PriorityTitleItem)) is not PriorityTitleItem item)
            return;

        MovePriorityItemAtPointer(listBox, item, e.GetPosition(listBox));
        e.Handled = true;
    }

    private ObservableCollection<PriorityTitleItem> GetPriorityItems(ListBox listBox) =>
        ReferenceEquals(listBox, PreferredAudioTitlesListBox)
            ? _audioPriorityItems
            : _subtitlePriorityItems;

    private void MovePriorityItemAtPointer(ListBox listBox, PriorityTitleItem item, Point pointer)
    {
        var items = GetPriorityItems(listBox);
        var oldIndex = items.IndexOf(item);
        if (oldIndex < 0)
            return;

        var insertionIndex = items.Count;
        for (var index = 0; index < items.Count; index++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
                continue;

            var midpoint = container.TranslatePoint(
                new Point(0, container.ActualHeight / 2), listBox).Y;
            if (pointer.Y < midpoint)
            {
                insertionIndex = index;
                break;
            }
        }

        var targetIndex = insertionIndex;
        if (oldIndex < targetIndex)
            targetIndex--;
        targetIndex = Math.Clamp(targetIndex, 0, items.Count - 1);
        if (targetIndex == oldIndex)
            return;

        items.Move(oldIndex, targetIndex);
        listBox.UpdateLayout();
    }

    private static void AutoScrollPriorityList(ListBox listBox, Point pointer)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer is null)
            return;

        const double hotZone = 24;
        if (pointer.Y < hotZone)
            scrollViewer.LineUp();
        else if (pointer.Y > listBox.ActualHeight - hotZone)
            scrollViewer.LineDown();
    }

    private void UpdatePriorityCounts()
    {
        if (PreferredAudioPriorityCountText is not null)
            PreferredAudioPriorityCountText.Text =
                $"{_audioPriorityItems.Count} / 50 • priorité du haut vers le bas";
        if (PreferredSubtitlePriorityCountText is not null)
            PreferredSubtitlePriorityCountText.Text =
                $"{_subtitlePriorityItems.Count} / 50 • priorité du haut vers le bas";
        if (AddAudioPriorityButton is not null)
            AddAudioPriorityButton.IsEnabled = _audioPriorityItems.Count < 50;
        if (AddSubtitlePriorityButton is not null)
            AddSubtitlePriorityButton.IsEnabled = _subtitlePriorityItems.Count < 50;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }

        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void UpdateStartupVolumeControls()
    {
        if (StartupVolumeControls is null)
            return;

        var enabled = ResetVolumeCheckBox.IsChecked == true;
        StartupVolumeControls.IsEnabled = enabled;
        StartupVolumeControls.Opacity = enabled ? 1 : 0.42;
    }

    private void UpdateAdaptiveAudioControls()
    {
        if (AdaptiveAudioMappingsPanel is null)
            return;

        var enabled = IsAdaptiveAudioDeviceSelected();
        AdaptiveAudioMappingsPanel.IsEnabled = enabled;
        AdaptiveAudioMappingsPanel.Opacity = enabled ? 1 : 0.42;
    }

    private void UpdatePreferredAudioControls()
    {
        if (PreferredAudioComboBox is null || PreferredAudioCustomControls is null)
            return;

        var enabled = AutoSelectPreferredAudioCheckBox.IsChecked == true &&
                      DisableAudioByDefaultCheckBox.IsChecked != true;
        PreferredAudioComboBox.IsEnabled = enabled;
        PreferredAudioComboBox.Opacity = enabled ? 1 : 0.42;
        AutoSelectPreferredAudioCheckBox.IsEnabled = DisableAudioByDefaultCheckBox.IsChecked != true;
        AutoSelectPreferredAudioCheckBox.Opacity = AutoSelectPreferredAudioCheckBox.IsEnabled ? 1 : 0.42;
        var customEnabled = enabled && GetSelectedTag(PreferredAudioComboBox, "disabled") == "custom";
        PreferredAudioCustomControls.IsEnabled = customEnabled;
        PreferredAudioCustomControls.Visibility = customEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePassthroughControls()
    {
        if (DecodedAudioControls is null)
            return;

        var enabled = AudioPassthroughCheckBox.IsChecked != true;
        DecodedAudioControls.IsEnabled = enabled;
        DecodedAudioControls.Opacity = enabled ? 1 : 0.42;
    }

    private void UpdateStartupTitleControls()
    {
        if (StartupTitleControls is null)
            return;

        var enabled = StartupTitleOverlayCheckBox.IsChecked == true;
        StartupTitleControls.IsEnabled = enabled;
        StartupTitleControls.Opacity = enabled ? 1 : 0.42;
        UpdateSubtitlePreview();
    }

    private void UpdatePreferredSubtitleControls()
    {
        if (PreferredSubtitleComboBox is null || PreferredSubtitleCustomControls is null)
            return;

        var enabled = AutoSelectPreferredSubtitleCheckBox.IsChecked == true &&
                      DisableSubtitlesByDefaultCheckBox.IsChecked != true;
        PreferredSubtitleComboBox.IsEnabled = enabled;
        PreferredSubtitleComboBox.Opacity = enabled ? 1 : 0.42;
        AutoSelectPreferredSubtitleCheckBox.IsEnabled = DisableSubtitlesByDefaultCheckBox.IsChecked != true;
        AutoSelectPreferredSubtitleCheckBox.Opacity = AutoSelectPreferredSubtitleCheckBox.IsEnabled ? 1 : 0.42;
        var customEnabled = enabled && GetSelectedTag(PreferredSubtitleComboBox, "default") == "custom";
        PreferredSubtitleCustomControls.IsEnabled = customEnabled;
        PreferredSubtitleCustomControls.Visibility = customEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSubtitlePositionControls()
    {
        if (SubtitlePositionControls is null || SubtitleForcePositionCheckBox is null)
            return;

        var enabled = _editTarget == EditTarget.Title ||
                      _editTarget == EditTarget.Subtitle && SubtitleForcePositionCheckBox.IsChecked == true;
        SubtitlePositionControls.IsEnabled = enabled;
        SubtitlePositionControls.Opacity = enabled ? 1 : 0.42;
        UpdateSubtitlePreview();
    }

    private void ModifyTitle_OnClick(object sender, RoutedEventArgs e) =>
        SwitchEditTarget(EditTarget.Title);

    private void ModifySubtitle_OnClick(object sender, RoutedEventArgs e) =>
        SwitchEditTarget(EditTarget.Subtitle);

    private void SwitchEditTarget(EditTarget target)
    {
        if (_editTarget == target)
            return;

        if (!CaptureActiveStyleDraft(true))
            return;

        _editTarget = target;
        LoadStyleDraft(target == EditTarget.Title ? _titleStyle : _subtitleStyle);
        UpdateEditTargetVisuals();
        UpdateSubtitlePositionControls();
        UpdateSubtitlePreview();
    }

    private static void SetDraft(StyleDraft draft, string font, int fontSize,
        string textColor, string borderColor, double borderSize, bool shadow,
        bool forcePosition, string position, int marginX, int marginY,
        bool scaleWithWindow)
    {
        draft.Font = string.IsNullOrWhiteSpace(font) ? "Arial" : font;
        draft.FontSize = Math.Clamp(fontSize, 12, 120);
        draft.TextColor = string.IsNullOrWhiteSpace(textColor) ? "#FFFFFFFF" : textColor;
        draft.BorderColor = string.IsNullOrWhiteSpace(borderColor) ? "#FF000000" : borderColor;
        draft.BorderSize = Math.Clamp(borderSize, 0, 10);
        draft.Shadow = shadow;
        draft.ForcePosition = forcePosition;
        draft.Position = string.IsNullOrWhiteSpace(position) ? "bottom-center" : position;
        draft.MarginX = Math.Clamp(marginX, 0, 500);
        draft.MarginY = Math.Clamp(marginY, 0, 500);
        draft.ScaleWithWindow = scaleWithWindow;
    }

    private void LoadStyleDraft(StyleDraft draft)
    {
        _loadingStyleDraft = true;
        SelectTaggedItem(SubtitleFontComboBox, draft.Font, "Arial");
        SelectTaggedItem(SubtitleFontSizeComboBox, draft.FontSize, 42);
        SelectTaggedItem(SubtitleTextColorComboBox, draft.TextColor, "#FFFFFFFF");
        SelectTaggedItem(SubtitleBorderColorComboBox, draft.BorderColor, "#FF000000");
        SelectTaggedItem(SubtitleBorderSizeComboBox, draft.BorderSize, 2.5);
        SubtitleShadowCheckBox.IsChecked = draft.Shadow;
        SubtitleForcePositionCheckBox.IsChecked = draft.ForcePosition;
        SelectTaggedItem(SubtitlePositionComboBox, draft.Position,
            _editTarget == EditTarget.Title ? "top-center" : "bottom-center");
        SubtitleMarginXTextBox.Text = draft.MarginX.ToString(CultureInfo.InvariantCulture);
        SubtitleMarginYTextBox.Text = draft.MarginY.ToString(CultureInfo.InvariantCulture);
        ScaleTextToWindowCheckBox.IsChecked = draft.ScaleWithWindow;
        UpdateFontSelectionPreview();
        _loadingStyleDraft = false;
    }

    private bool CaptureActiveStyleDraft(bool showValidation)
    {
        if (_editTarget == EditTarget.None || _loadingStyleDraft)
            return true;

        if (!int.TryParse(SubtitleMarginXTextBox.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var marginX) || marginX is < 0 or > 500 ||
            !int.TryParse(SubtitleMarginYTextBox.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var marginY) || marginY is < 0 or > 500)
        {
            if (showValidation)
            {
                MessageBox.Show(this,
                    LocalizationService.Get("Les marges doivent être comprises entre 0 et 500 pixels."),
                    LocalizationService.Get("POSITION"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SubtitleMarginXTextBox.Focus();
                SubtitleMarginXTextBox.SelectAll();
            }

            return false;
        }

        var draft = _editTarget == EditTarget.Title ? _titleStyle : _subtitleStyle;
        draft.Font = GetSelectedTag(SubtitleFontComboBox, "Arial");
        draft.FontSize = GetSelectedTag(SubtitleFontSizeComboBox, 42);
        draft.TextColor = GetSelectedTag(SubtitleTextColorComboBox, "#FFFFFFFF");
        draft.BorderColor = GetSelectedTag(SubtitleBorderColorComboBox, "#FF000000");
        draft.BorderSize = GetSelectedTag(SubtitleBorderSizeComboBox, 2.5);
        draft.Shadow = SubtitleShadowCheckBox.IsChecked == true;
        draft.ForcePosition = _editTarget == EditTarget.Title ||
                              SubtitleForcePositionCheckBox.IsChecked == true;
        draft.Position = GetSelectedTag(SubtitlePositionComboBox,
            _editTarget == EditTarget.Title ? "top-center" : "bottom-center");
        draft.MarginX = marginX;
        draft.MarginY = marginY;
        draft.ScaleWithWindow = ScaleTextToWindowCheckBox.IsChecked == true;
        return true;
    }

    private void UpdateEditTargetVisuals()
    {
        if (AppearanceControls is null || PositionCardBorder is null ||
            ModifyTitleButton is null || ModifySubtitleButton is null ||
            EditTargetHintText is null || PositionSectionTitleText is null ||
            PositionSectionDescriptionText is null || SubtitleForcePositionCheckBox is null)
            return;

        var selected = _editTarget != EditTarget.None;
        AppearanceControls.IsEnabled = selected;
        AppearanceControls.Opacity = selected ? 1 : 0.38;
        PositionCardBorder.IsEnabled = selected;
        PositionCardBorder.Opacity = selected ? 1 : 0.46;
        ApplyTargetButtonState(ModifyTitleButton, _editTarget == EditTarget.Title);
        ApplyTargetButtonState(ModifySubtitleButton, _editTarget == EditTarget.Subtitle);

        if (_editTarget == EditTarget.Title)
        {
            EditTargetHintText.Text = LocalizationService.Get(
                "Vous modifiez uniquement l’apparence et la position du titre.");
            PositionSectionTitleText.Text = LocalizationService.Get("POSITION DU TITRE");
            PositionSectionDescriptionText.Text = LocalizationService.Get(
                "Détermine précisément où le titre de démarrage apparaît dans l’image.");
            SubtitleForcePositionCheckBox.Visibility = Visibility.Collapsed;
        }
        else if (_editTarget == EditTarget.Subtitle)
        {
            EditTargetHintText.Text = LocalizationService.Get(
                "Vous modifiez uniquement l’apparence et la position des sous-titres.");
            PositionSectionTitleText.Text = LocalizationService.Get(
                "POSITION FORCÉE DES SOUS-TITRES");
            PositionSectionDescriptionText.Text = LocalizationService.Get(
                "Ignore la position intégrée au fichier et applique les marges choisies.");
            SubtitleForcePositionCheckBox.Visibility = Visibility.Visible;
        }
        else
        {
            EditTargetHintText.Text = LocalizationService.Get(
                "Sélectionnez le titre ou les sous-titres pour activer les réglages.");
            PositionSectionTitleText.Text = LocalizationService.Get("POSITION");
            PositionSectionDescriptionText.Text = LocalizationService.Get(
                "Sélectionnez d’abord l’élément à modifier dans la section Apparence.");
            SubtitleForcePositionCheckBox.Visibility = Visibility.Collapsed;
        }
    }

    private static void ApplyTargetButtonState(Button button, bool selected)
    {
        if (selected)
        {
            button.Background = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0x7A, 0x45));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x70));
            button.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB1, 0x87));
        }
        else
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(BorderBrushProperty);
            button.ClearValue(ForegroundProperty);
        }
    }

    private void UpdateSubtitlePreview()
    {
        if (SubtitlePreviewSurface is null || SubtitlePreviewTextHost is null ||
            SubtitlePreviewText is null || SubtitlePreviewOutlineText is null ||
            SubtitlePreviewShadowText is null || SubtitlePreviewPositionLabel is null ||
            SubtitleFontComboBox is null || SubtitleFontSizeComboBox is null ||
            SubtitleTextColorComboBox is null || SubtitleBorderColorComboBox is null ||
            SubtitleBorderSizeComboBox is null || SubtitleShadowCheckBox is null ||
            SubtitlePositionComboBox is null)
            return;

        var editingTitle = _editTarget == EditTarget.Title;
        var text = _editTarget switch
        {
            EditTarget.Title => LocalizationService.Get("Titre de la vidéo"),
            EditTarget.Subtitle => LocalizationService.Get("Exemple de sous-titre"),
            _ => LocalizationService.Get("Choisissez un élément à modifier")
        };
        var position = editingTitle
            ? GetSelectedTag(SubtitlePositionComboBox, "top-center")
            : _editTarget == EditTarget.Subtitle && SubtitleForcePositionCheckBox.IsChecked == true
                ? GetSelectedTag(SubtitlePositionComboBox, "bottom-center")
                : _editTarget == EditTarget.None ? "center-center" : "bottom-center";
        var fontName = GetSelectedTag(SubtitleFontComboBox, "Arial");
        var fontSize = Math.Clamp(GetSelectedTag(SubtitleFontSizeComboBox, 42) * 0.68, 13, 54);
        var textBrush = ParseBrush(GetSelectedTag(SubtitleTextColorComboBox, "#FFFFFFFF"), Colors.White);
        var borderBrush = ParseBrush(GetSelectedTag(SubtitleBorderColorComboBox, "#FF000000"), Colors.Black);
        var borderSize = Math.Clamp(GetSelectedTag(SubtitleBorderSizeComboBox, 2.5), 0, 10);
        var family = new FontFamily(fontName);

        foreach (var textBlock in new[]
                 {
                     SubtitlePreviewShadowText, SubtitlePreviewOutlineText, SubtitlePreviewText
                 })
        {
            textBlock.Text = text;
            textBlock.FontFamily = family;
            textBlock.FontSize = fontSize;
            textBlock.FontWeight = FontWeights.Normal;
        }

        SubtitlePreviewText.Foreground = textBrush;
        SubtitlePreviewOutlineText.Foreground = borderBrush;
        SubtitlePreviewOutlineText.Visibility = borderSize > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubtitlePreviewOutlineText.Effect = borderBrush is SolidColorBrush outlineBrush && borderSize > 0
            ? new DropShadowEffect
            {
                Color = outlineBrush.Color,
                BlurRadius = Math.Max(1, borderSize * 2.2),
                ShadowDepth = 0,
                Opacity = outlineBrush.Opacity
            }
            : null;
        SubtitlePreviewShadowText.Foreground = borderBrush;
        SubtitlePreviewShadowText.Visibility = SubtitleShadowCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubtitlePreviewShadowText.RenderTransform = new TranslateTransform(2.4, 2.4);

        ApplyPreviewPosition(position);
        var positionLabel = PositionLabel(position);
        if (_editTarget == EditTarget.Subtitle && SubtitleForcePositionCheckBox.IsChecked != true)
            positionLabel = LocalizationService.Get("Bas centre (aperçu automatique)");
        SubtitlePreviewPositionLabel.Text = _editTarget switch
        {
            EditTarget.Title => LocalizationService.Format("Titre • {0}", positionLabel),
            EditTarget.Subtitle => LocalizationService.Format("Sous-titre • {0}", positionLabel),
            _ => LocalizationService.Get("Aucun élément sélectionné")
        };
    }

    private void ApplyPreviewPosition(string position)
    {
        var normalized = position?.Trim().ToLowerInvariant() ?? "bottom-center";
        SubtitlePreviewTextHost.HorizontalAlignment = normalized.EndsWith("left", StringComparison.Ordinal)
            ? HorizontalAlignment.Left
            : normalized.EndsWith("right", StringComparison.Ordinal)
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Center;
        SubtitlePreviewTextHost.VerticalAlignment = normalized.StartsWith("top", StringComparison.Ordinal)
            ? VerticalAlignment.Top
            : normalized.StartsWith("center", StringComparison.Ordinal)
                ? VerticalAlignment.Center
                : VerticalAlignment.Bottom;

        var marginX = int.TryParse(SubtitleMarginXTextBox?.Text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedX) ? Math.Clamp(parsedX * 0.12, 0, 62) : 2.4;
        var marginY = int.TryParse(SubtitleMarginYTextBox?.Text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedY) ? Math.Clamp(parsedY * 0.12, 0, 62) : 4.3;
        var left = SubtitlePreviewTextHost.HorizontalAlignment == HorizontalAlignment.Left ? marginX : 0;
        var right = SubtitlePreviewTextHost.HorizontalAlignment == HorizontalAlignment.Right ? marginX : 0;
        var top = SubtitlePreviewTextHost.VerticalAlignment == VerticalAlignment.Top ? marginY : 0;
        var bottom = SubtitlePreviewTextHost.VerticalAlignment == VerticalAlignment.Bottom ? marginY : 0;
        SubtitlePreviewTextHost.Margin = new Thickness(left + 14, top + 12, right + 14, bottom + 12);
    }

    private static Brush ParseBrush(string value, Color fallback)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(fallback);
        }
    }

    private static string PositionLabel(string position) => position switch
    {
        "top-left" => LocalizationService.Get("Haut gauche"),
        "top-center" => LocalizationService.Get("Haut centre"),
        "top-right" => LocalizationService.Get("Haut droite"),
        "center-left" => LocalizationService.Get("Centre gauche"),
        "center-center" => LocalizationService.Get("CENTRE"),
        "center-right" => LocalizationService.Get("Centre droite"),
        "bottom-left" => LocalizationService.Get("Bas gauche"),
        "bottom-right" => LocalizationService.Get("Bas droite"),
        _ => LocalizationService.Get("Bas centre")
    };

    private void SettingsDialog_OnClosed(object? sender, EventArgs e)
    {
        // Un changement de langue est aperçu immédiatement, mais il ne doit
        // pas rester actif si l’utilisateur ferme les paramètres avec Annuler.
        // L’import est l’exception : le profil importé contient précisément la
        // langue à conserver.
        if (!SettingsImported && DialogResult != true)
            LocalizationService.SetLanguage(_languageBeforeDialog);
    }

    private void StartupVolumeSlider_OnValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (StartupVolumeText is not null)
            StartupVolumeText.Text = $"{Math.Clamp((int)Math.Round(e.NewValue), 0, 125)} %";
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Le bouton a été relâché avant que Windows commence le déplacement.
        }
    }

    private void ResizeThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string edge } || WindowState != WindowState.Normal)
            return;

        var width = double.IsNaN(Width) ? ActualWidth : Width;
        var height = double.IsNaN(Height) ? ActualHeight : Height;

        if (edge.Contains("Left", StringComparison.Ordinal))
        {
            var delta = Math.Min(e.HorizontalChange, width - MinWidth);
            Width = Math.Max(MinWidth, width - delta);
            Left += delta;
        }
        else if (edge.Contains("Right", StringComparison.Ordinal))
        {
            Width = Math.Max(MinWidth, width + e.HorizontalChange);
        }

        if (edge.Contains("Top", StringComparison.Ordinal))
        {
            var delta = Math.Min(e.VerticalChange, height - MinHeight);
            Height = Math.Max(MinHeight, height - delta);
            Top += delta;
        }
        else if (edge.Contains("Bottom", StringComparison.Ordinal))
        {
            Height = Math.Max(MinHeight, height + e.VerticalChange);
        }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ShortcutCaptureOverlay.Visibility == Visibility.Visible)
        {
            var captureKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (captureKey == Key.Escape)
            {
                _capturedShortcutGesture = string.Empty;
            }
            else if (!ShortcutCatalog.IsModifierKey(captureKey) && captureKey != Key.None)
            {
                const ModifierKeys supported = ModifierKeys.Control | ModifierKeys.Shift |
                                               ModifierKeys.Alt | ModifierKeys.Windows;
                _capturedShortcutGesture = ShortcutCatalog.Encode(
                    captureKey, Keyboard.Modifiers & supported);
            }

            ShortcutCaptureErrorText.Visibility = Visibility.Collapsed;
            UpdateShortcutCaptureText();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && Keyboard.FocusedElement is Button
            {
                Tag: ShortcutBindingItem shortcut
            })
        {
            shortcut.EncodedGesture = string.Empty;
            _shortcutView?.Refresh();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
            return;

        if (PriorityPromptOverlay.Visibility == Visibility.Visible)
        {
            ClosePriorityPrompt();
            e.Handled = true;
            return;
        }

        DialogResult = false;
        e.Handled = true;
    }
}
