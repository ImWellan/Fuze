using System.Text.Json.Serialization;
using System.ComponentModel;
using FusePlayer.Services;

namespace FusePlayer.Models;

/// <summary>
/// Layout sauvegardé de la barre de commandes inférieure. Les identifiants
/// correspondent aux contrôles de la fenêtre principale; les listes décrivent
/// les trois zones de la ligne (gauche, centre et droite).
/// </summary>
public sealed class BottomBarLayoutPresetData : INotifyPropertyChanged
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal))
                return;

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    [JsonIgnore]
    public string DisplayName => LocalizationService.Get(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshLocalizedDisplayName() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
    public bool IsBuiltIn { get; set; }
    public List<string> LeftItems { get; set; } = [];
    public List<string> CenterItems { get; set; } = [];
    public List<string> RightItems { get; set; } = [];
    // Position horizontale normalisée du centre de chaque commande (0 à 1).
    // Une collection vide conserve la disposition historique gauche/centre/droite.
    public Dictionary<string, double> HorizontalPositions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int Spacing { get; set; } = 4;
    // Largeur réservée au nom du média dans la barre inférieure.
    public int TitleWidth { get; set; } = 230;
    // Décalage vertical du repère central par rapport à sa position de base.
    // 10 px est la position verticale initiale; une valeur négative remonte
    // la ligne par rapport à sa position de base.
    public int CenterBarOffset { get; set; } = 10;
    // Masquage des repères verticaux 25 %, 50 % et 75 % dans l’éditeur.
    // Désactivé par défaut pour conserver les repères visibles.
    public bool HideVerticalGuides { get; set; }
    // Masquage de la barre horizontale centrale dans l’éditeur.
    // Désactivé par défaut pour conserver la ligne centrale visible.
    public bool HideHorizontalCenterGuide { get; set; }
    // Sépare le temps écoulé et le temps restant en deux commandes déplaçables.
    // Désactivé par défaut : le compteur demeure un seul bloc avec séparateur.
    public bool SplitTimeline { get; set; }
    // Identifiant de la commande qui reste centrée dans l'éditeur. Lorsque la
    // valeur est nulle, les commandes suivent leur position habituelle.
    public string? CenterLockedItemId { get; set; }
}

public sealed class PlayerSession
{
    public List<PlaylistItemData> Playlist { get; init; } = [];
    public int Volume { get; set; } = 100;
    public bool ResetVolumeOnStartup { get; set; } = true;
    public int StartupVolume { get; set; } = 100;
    public int SelectedIndex { get; set; } = -1;
    public bool PlaylistVisible { get; set; }
    public int RewindSeconds { get; set; } = 15;
    public int ForwardSeconds { get; set; } = 30;
    public bool PrioritizeChapters { get; set; } = true;
    public bool PlayNextMediaAutomatically { get; set; } = true;
    public bool EnhancedPlaybackEnabled { get; set; } = true;
    public bool EnhancedFolderAdvanceEnabled { get; set; }
    public bool EnhancedFolderShowNameEnabled { get; set; } = true;
    public bool ShowEnhancedUpcomingInPlaylist { get; set; } = true;
    public bool ShowEnhancedNextFolderInPlaylist { get; set; }
    public bool ResumePlayback { get; set; } = true;
    public int ResumePromptStartSkipPercent { get; set; } = 5;
    public int ResumePromptEndSkipPercent { get; set; } = 5;
    public bool AutoPlayOnOpen { get; set; } = true;
    public bool ConfirmClose { get; set; }
    public bool PreventSleepDuringPlayback { get; set; } = true;
    public bool RememberMediaSettings { get; set; }
    // 0 conserve l'historique sans limite de temps.
    public int RecentMediaRetentionDays { get; set; }
    // Nombre de dossiers parents affichés dans le menu des médias récents.
    // Deux correspond à l'affichage historique (grand-parent, parent, média).
    public int RecentMediaFolderDepth { get; set; } = 2;
    // Nombre de dossiers parents affichés dans chaque chemin de la file.
    // Deux conserve le rendu historique; 0 n'affiche que le nom du média.
    public int PlaylistFolderDepth { get; set; } = 2;
    public bool FileAssociationsEnabled { get; set; } = true;
    public List<string> FileAssociationExtensions { get; set; } = [];
    public List<CustomFileAssociationData> CustomFileAssociationTypes { get; set; } = [];
    public bool ShufflePlayback { get; set; }
    public bool RepeatPlayback { get; set; }
    public bool RepeatPlaylist { get; set; }
    public string? LastMediaLocation { get; set; }
    public long LastMediaPositionMilliseconds { get; set; }
    public bool HardwareDecoding { get; set; } = true;
    public bool Deinterlacing { get; set; }
    public string HdrMode { get; set; } = "auto";
    public bool BufferingEnabled { get; set; } = true;
    public bool AudioNormalization { get; set; }
    public bool AutoSwitchAudioDevice { get; set; }
    public bool AdaptiveAudioModeEnabled { get; set; }
    public List<AdaptiveAudioDisplayMappingData> AdaptiveAudioDisplayMappings { get; set; } = [];
    public bool PreferSdhSubtitles { get; set; }
    public bool ShowScreenshotButton { get; set; }
    public bool AdaptiveInterfaceScale { get; set; } = true;
    public bool AutoHideCursor { get; set; } = true;
    public int CursorAutoHideDelayMilliseconds { get; set; } = 3000;
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowOsd { get; set; } = true;
    // Langue de l’interface persistée entre les sessions (en ou fr).
    public string InterfaceLanguage { get; set; } = "en";
    public bool DisableToolTips { get; set; }
    public bool ShowChapterNameInSeekPreview { get; set; } = true;
    public bool TogglePlaybackOnSingleClick { get; set; } = true;
    public bool ToggleFullscreenOnDoubleClick { get; set; } = true;
    // Compatibilité avec les sessions précédentes : ce réglage contrôle
    // désormais la protection contre la superposition Discord.
    public bool DiscordActivityEnabled { get; set; } = true;
    public bool DiagnosticLoggingEnabled { get; set; }
    public int TopBarAutoHideDelayMilliseconds { get; set; } = 1500;
    public int BottomBarAutoHideDelayMilliseconds { get; set; } = 500;
    // Vitesse de défilement automatique de la file pendant un glisser-déposer.
    // La valeur est volontairement exprimée comme une vitesse simple dans
    // l'interface; l'implémentation la convertit en déplacement par tick.
    public int PlaylistScrollSpeed { get; set; } = 20;
    public bool AutoCompactMissingBottomBarItems { get; set; } = true;
    public List<BottomBarLayoutPresetData> BottomBarLayoutPresets { get; set; } = [];
    public string ActiveBottomBarLayoutPreset { get; set; } = "Fuze — classique";
    public int VolumeControlStyle { get; set; } = 3;
    public int VolumePopupHideDelayMilliseconds { get; set; } = 2000;
    public int VolumeIndicatorHideDelayMilliseconds { get; set; } = 1000;
    public bool HideInterfaceOnVideoStart { get; set; } = true;
    public bool ShowSynchronizationButton { get; set; }
    public bool ShowShuffleButton { get; set; }
    public bool ShowRepeatButton { get; set; }
    public bool ShowSpeedButton { get; set; } = true;
    public bool ShowPlaylistButton { get; set; } = true;
    public bool ShowVideoPanButton { get; set; } = false;
    public bool ShowAdditionalMediaInformation { get; set; }
    public bool StartVideoFullscreen { get; set; } = true;
    public string PreferredVideoDisplay { get; set; } = "auto";
    public string VideoOutput { get; set; } = "auto";
    public int CustomZoomPercent { get; set; } = 100;
    public string CustomAspectRatio { get; set; } = "16:9";
    public string ScreenshotBaseDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public string ScreenshotFolderName { get; set; } = "Fuze";
    public string ScreenshotFormat { get; set; } = "png";
    public string ScreenshotAffixMode { get; set; } = "prefix";
    public string ScreenshotAffixText { get; set; } = "Fuze";
    public bool ScreenshotSequentialNumbering { get; set; }
    public bool CopyScreenshotsToClipboard { get; set; }
    public Dictionary<string, string> KeyboardShortcuts { get; set; } = ShortcutCatalog.CreateDefaults();
    public bool MouseWheelTimelineEnabled { get; set; } = true;
    public bool MouseWheelVolumeEnabled { get; set; } = true;
    public bool CenterWheelVolumeEnabled { get; set; } = true;
    public bool CenterWheelTimelineEnabled { get; set; }
    public bool MouseWheelAudioTracksEnabled { get; set; } = true;
    public bool MouseWheelSubtitleTracksEnabled { get; set; } = true;
    public bool IgnoreKeyboardVolumeButtons { get; set; }
    public string AudioDevice { get; set; } = "auto";
    public int AudioOutputMode { get; set; }
    public int AudioTreatmentMode { get; set; }
    public bool AudioPassthrough { get; set; }
    public bool AudioExclusive { get; set; }
    public bool DisableAudioByDefault { get; set; }
    public bool AutoSelectPreferredAudio { get; set; }
    public string PreferredAudioProfile { get; set; } = "disabled";
    public List<string> PreferredAudioTitlePriorities { get; set; } = [];
    public int DefaultAudioDelayMilliseconds { get; set; }
    public bool StartupTitleOverlayEnabled { get; set; } = true;
    public bool PreferOriginalTitleForStartup { get; set; }
    public string StartupTitlePosition { get; set; } = "top-center";
    public int StartupTitleDelayMilliseconds { get; set; } = 250;
    public int StartupTitleDurationMilliseconds { get; set; } = 3000;
    public string StartupTitleFont { get; set; } = "Arial";
    public int StartupTitleFontSize { get; set; } = 42;
    public string StartupTitleTextColor { get; set; } = "#FFFFFFFF";
    public string StartupTitleBorderColor { get; set; } = "#FF000000";
    public double StartupTitleBorderSize { get; set; } = 2.5;
    public bool StartupTitleShadow { get; set; } = true;
    public int StartupTitleMarginX { get; set; } = 20;
    public int StartupTitleMarginY { get; set; } = 36;
    public bool StartupTitleScaleWithWindow { get; set; } = true;
    public bool AutoSelectPreferredSubtitle { get; set; }
    public bool AutoLoadExternalSubtitles { get; set; }
    public string PreferredSubtitleProfile { get; set; } = "default";
    public List<string> PreferredSubtitleTitlePriorities { get; set; } = [];
    public bool DisableSubtitlesByDefault { get; set; }
    public string SubtitleEncoding { get; set; } = "auto";
    public string SubtitleFont { get; set; } = "Arial";
    public int SubtitleFontSize { get; set; } = 42;
    public string SubtitleTextColor { get; set; } = "#FFFFFFFF";
    public string SubtitleBorderColor { get; set; } = "#FF000000";
    public double SubtitleBorderSize { get; set; } = 2.5;
    public bool SubtitleShadow { get; set; } = true;
    public bool SubtitleForcePosition { get; set; }
    public string SubtitlePosition { get; set; } = "bottom-center";
    public int SubtitleMarginX { get; set; } = 20;
    public int SubtitleMarginY { get; set; } = 36;
    public bool SubtitleScaleWithWindow { get; set; } = true;
    public List<string> RecentMedia { get; init; } = [];
    public Dictionary<string, DateTime> RecentMediaLastOpenedUtc { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaPlaybackPreferencesData> MediaPlaybackPreferences { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Périphérique audio associé à un moniteur pour le mode audio adaptatif.
/// L'identifiant du moniteur correspond au nom Windows (par exemple
/// \\.\DISPLAY1) et l'identifiant audio à celui exposé par mpv.
/// </summary>
public sealed class AdaptiveAudioDisplayMappingData
{
    public string DisplayId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AudioDevice { get; set; } = "auto";
}

public sealed class MediaPlaybackPreferencesData
{
    public int? VideoTrackId { get; set; }
    public int? AudioTrackId { get; set; }
    public int? SubtitleTrackId { get; set; }
    public float PlaybackRate { get; set; } = 1f;
    public double VideoZoom { get; set; }
    public long VideoSyncMilliseconds { get; set; }
    public long AudioSyncMilliseconds { get; set; }
    public long SubtitleSyncMilliseconds { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PlaylistItemData
{
    public required string Location { get; init; }
    public required string Title { get; init; }
    public bool IsNetwork { get; init; }
    public long DurationMilliseconds { get; init; }
    // Ces indicateurs permettent de restaurer la file telle qu'elle était,
    // y compris les éléments ajoutés par la lecture augmentée.
    public bool IsEnhancedQueued { get; init; }
    public bool IsEnhancedFolderStart { get; init; }
    public string EnhancedFolderTitle { get; init; } = string.Empty;
    public bool IsManualQueueItem { get; init; }
}

public sealed class CustomFileAssociationData
{
    public string Title { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public bool IsAudio { get; set; } = true;
    public bool Enabled { get; set; } = true;
}
