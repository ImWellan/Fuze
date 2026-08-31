using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FusePlayer.Controls;
using FusePlayer.Models;
using FusePlayer.Playback;
using FusePlayer.Services;
using Microsoft.Win32;

namespace FusePlayer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".aac", ".ac3", ".aiff", ".ape", ".asf", ".avi", ".divx", ".dts", ".eac3",
        ".flac", ".flv", ".m2ts", ".m3u", ".m3u8", ".m4a", ".m4v", ".mka", ".mkv", ".mov",
        ".mp2", ".mp3", ".mp4", ".mpeg", ".mpg", ".mts", ".ogg", ".ogm", ".ogv", ".opus",
        ".pls", ".ts", ".vob", ".wav", ".webm", ".wma", ".wmv"
    };

    private static readonly HashSet<string> EnhancedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".asf", ".avi", ".divx", ".flv", ".m2ts", ".m4v", ".mkv", ".mov",
        ".mp4", ".mpeg", ".mpg", ".mts", ".ogm", ".ogv", ".ts", ".vob", ".webm", ".wmv"
    };

    private static readonly (float Rate, string Label)[] SpeedOptions =
    [
        (0.25f, "0,25×"), (0.5f, "0,50×"), (0.75f, "0,75×"), (0.9f, "0,90×"),
        (1f, "1,00×"), (1.25f, "1,25×"), (1.5f, "1,50×"), (2f, "2,00×"), (3f, "3,00×")
    ];

    private static readonly (AudioOutputMode Mode, string Label)[] AudioModeOptions =
    [
        (AudioOutputMode.Automatic, "Automatique (original)"),
        (AudioOutputMode.Mono, "Mono"),
        (AudioOutputMode.Stereo, "Stéréo"),
        (AudioOutputMode.LeftChannel, "Canal gauche"),
        (AudioOutputMode.RightChannel, "Canal droit"),
        (AudioOutputMode.ReversedStereo, "Stéréo inversée")
    ];

    private static readonly (AudioTreatmentMode Mode, string Label, string Description)[] AudioTreatmentOptions =
    [
        (AudioTreatmentMode.Night, "Mode nuit",
            "Réduit l’écart entre les sons faibles et les sons forts"),
        (AudioTreatmentMode.DialogueBoost, "Renforcement des dialogues",
            "Accentue les fréquences principales des voix"),
        (AudioTreatmentMode.HeadphoneBinaural, "Casque binaural",
            "Adapte le mélange stéréo pour une écoute plus naturelle au casque"),
        (AudioTreatmentMode.SurroundDownmix, "Conversion surround vers stéréo",
            "Mélange les canaux 5.1 ou 7.1 vers deux haut-parleurs")
    ];

    private static readonly (double Value, string Label)[] VideoZoomOptions =
    [
        (-1d, "50 %"), (-0.415037d, "75 %"), (0d, "100 %"),
        (0.321928d, "125 %"), (0.584963d, "150 %"), (1d, "200 %")
    ];

    // Paliers utilisés par le mode « déplacement de l’écran ». Ils restent
    // suffisamment fins aux petits zooms et évitent les sauts trop brusques
    // lorsque l’image est déjà fortement agrandie.
    private static readonly int[] VideoPanZoomSteps =
        [25, 50, 75, 100, 125, 150, 200, 250, 275, 300, 400, 500, 600, 700, 800, 900, 1000];

    private static readonly (string Value, string Label)[] VideoAspectOptions =
    [
        ("no", "Original"), ("16:9", "16:9"), ("4:3", "4:3"),
        ("185:100", "1,85:1"), ("235:100", "2,35:1"), ("21:9", "21:9"), ("1:1", "1:1")
    ];

    private readonly MpvPlayer _mediaPlayer;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _controlsHideTimer;
    private readonly DispatcherTimer _gearControlsHideTimer;
    private readonly DispatcherTimer _toolBarHideTimer;
    private readonly DispatcherTimer _videoLayoutTimer;
    private readonly DispatcherTimer _videoSurfaceRevealTimer;
    private readonly DispatcherTimer _startupPlaybackWatchdogTimer;
    private readonly DispatcherTimer _videoClickTimer;
    private readonly DispatcherTimer _seekCommitTimer;
    private readonly DispatcherTimer _activeZOrderTimer;
    private readonly DispatcherTimer _startupTitleTimer;
    private readonly DispatcherTimer _volumePopupHideTimer;
    private readonly DispatcherTimer _volumeIndicatorHideTimer;
    private readonly DispatcherTimer _resumeCheckpointTimer;
    private readonly SessionStore _sessionStore = new();
    private readonly MediaProbeService _mediaProbeService = new();
    private int _mediaInformationPreloadGeneration;
    private readonly List<string> _recentMedia = [];
    private readonly Dictionary<string, DateTime> _recentMediaLastOpenedUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaPlaybackPreferencesData> _mediaPlaybackPreferences =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Window> _auxiliaryDialogs = [];
    private ShortcutsDialog? _shortcutsDialog;
    private AboutDialog? _aboutDialog;
    private VideoZoomDialog? _videoZoomDialog;
    private VideoAspectRatioDialog? _videoAspectRatioDialog;

    private string MediaFileFilter =>
        $"{LocalizationService.Get("Médias")}|*.mkv;*.mp4;*.avi;*.mov;*.webm;*.wmv;*.m4v;*.ts;*.m2ts;*.mp3;*.flac;*.wav;*.aac;*.ogg;*.opus;*.m3u;*.m3u8|" +
        $"{LocalizationService.Get("Vidéos")}|*.mkv;*.mp4;*.avi;*.mov;*.webm;*.wmv;*.m4v;*.ts;*.m2ts|" +
        $"{LocalizationService.Get("Audio")}|*.mp3;*.flac;*.wav;*.aac;*.ogg;*.opus|{LocalizationService.Get("Tous les fichiers")}|*.*";
    private const string AdaptiveAudioDeviceMenuTag = "__adaptive_audio__";
    private static readonly string PlaylistsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Fuze", "Listes de lecture");

    private MpvMedia? _currentMedia;
    private int _currentIndex = -1;
    private bool _isSeeking;
    private bool _isFullscreen;
    private bool _playlistVisible;
    private bool _initialized;
    private bool _isClosing;
    private bool _isAdjustingVolume;
    private bool _isMuted;
    private bool _showTotalDuration;
    private bool _showTimelineMilliseconds;
    private const double VideoAspectRatio = 16d / 9d;
    private int _speedIndex = 4;
    private float _playbackRate = 1f;
    private string _selectedAudioDevice = "auto";
    private AudioOutputMode _audioOutputMode = AudioOutputMode.Automatic;
    private AudioTreatmentMode _audioTreatmentMode = AudioTreatmentMode.None;
    private bool _audioPassthrough;
    private bool _audioExclusive;
    private bool _disableAudioByDefault;
    private bool _autoSelectPreferredAudio;
    private string _preferredAudioProfile = "disabled";
    private string[] _preferredAudioTitlePriorities = [];
    private int _defaultAudioDelayMilliseconds;
    private bool _preferredAudioAppliedForCurrentMedia;
    private bool _startupTitleOverlayEnabled = true;
    private bool _preferOriginalTitleForStartup;
    private string _startupTitlePosition = "top-center";
    private int _startupTitleDelayMilliseconds = 250;
    private int _startupTitleDurationMilliseconds = 3000;
    private string _startupTitleFont = "Arial";
    private int _startupTitleFontSize = 42;
    private string _startupTitleTextColor = "#FFFFFFFF";
    private string _startupTitleBorderColor = "#FF000000";
    private double _startupTitleBorderSize = 2.5;
    private bool _startupTitleShadow = true;
    private int _startupTitleMarginX = 20;
    private int _startupTitleMarginY = 36;
    private bool _startupTitleScaleWithWindow = true;
    private bool _startupTitleShownForCurrentMedia;
    private string _pendingStartupTitle = string.Empty;
    private bool _preserveWindowPresentationForCurrentMedia;
    private bool _autoSelectPreferredSubtitle;
    private string _preferredSubtitleProfile = "default";
    private string[] _preferredSubtitleTitlePriorities = [];
    private bool _disableSubtitlesByDefault;
    private bool _autoLoadExternalSubtitles;
    private bool _preferredSubtitleAppliedForCurrentMedia;
    private bool _videoStartupPresentationAppliedForCurrentMedia;
    private bool _fixedVideoPresentationAppliedForCurrentMedia;
    private string _subtitleEncoding = "auto";
    private string _subtitleFont = "Arial";
    private int _subtitleFontSize = 42;
    private string _subtitleTextColor = "#FFFFFFFF";
    private string _subtitleBorderColor = "#FF000000";
    private double _subtitleBorderSize = 2.5;
    private bool _subtitleShadow = true;
    private bool _subtitleForcePosition;
    private string _subtitlePosition = "bottom-center";
    private int _subtitleMarginX = 20;
    private int _subtitleMarginY = 36;
    private bool _subtitleScaleWithWindow = true;
    private TrackSyncDialog? _trackSyncDialog;
    private long _videoSyncMilliseconds;
    private long _audioSyncMilliseconds;
    private long _subtitleSyncMilliseconds;
    private int _rewindSeconds = 15;
    private int _forwardSeconds = 30;
    private bool _prioritizeChapters = true;
    private bool _playNextMediaAutomatically = true;
    private bool _enhancedPlaybackEnabled = true;
    private bool _enhancedFolderAdvanceEnabled;
    private bool _enhancedFolderShowNameEnabled = true;
    private bool _showEnhancedUpcomingInPlaylist = true;
    private bool _showEnhancedNextFolderInPlaylist;
    private string? _enhancedFolderTitleLocation;
    private bool _enhancedPlaybackEligible;
    private bool _enhancedPreloadStarted;
    private string? _enhancedNextLocation;
    private int _enhancedPlaybackGeneration;
    private const long EnhancedPreloadLeadMilliseconds = 15_000;
    private bool _resumePlayback = true;
    private int _resumePromptStartSkipPercent = 5;
    private int _resumePromptEndSkipPercent = 5;
    private bool _autoPlayOnOpen = true;
    private bool _confirmClose;
    private bool _preventSleepDuringPlayback = true;
    private bool _rememberMediaSettings;
    private int _recentMediaRetentionDays;
    private int _recentMediaFolderDepth = 2;
    private int _playlistFolderDepth = 2;
    private bool _fileAssociationsEnabled;
    private string[] _fileAssociationExtensions = [];
    private CustomFileAssociationData[] _customFileAssociationTypes = [];
    private bool _shufflePlayback;
    private bool _repeatPlayback;
    private bool _repeatPlaylist;
    private readonly HashSet<int> _shufflePlayedIndices = [];
    private readonly Random _shuffleRandom = new();
    private string? _lastMediaLocation;
    private long _lastMediaPositionMilliseconds;
    private long _lastPersistedResumePositionMilliseconds = -1;
    private string? _lastPersistedResumeLocation;
    private long _pendingResumePositionMilliseconds;
    private long _pendingResumePromptPositionMilliseconds;
    private string? _pendingResumePromptLocation;
    private bool _pauseAfterOpeningForResumePrompt;
    private bool _hardwareDecoding = true;
    private bool _deinterlacing;
    private string _hdrMode = "auto";
    private bool _bufferingEnabled = true;
    private bool _audioNormalization;
    private bool _autoSwitchAudioDevice;
    private bool _adaptiveAudioModeEnabled;
    private List<AdaptiveAudioDisplayMappingData> _adaptiveAudioDisplayMappings = [];
    private string? _lastAdaptiveAudioDisplayId;
    private bool _adaptiveAudioUpdateQueued;
    private bool _preferSdhSubtitles;
    private bool _showScreenshotButton;
    private bool _showShuffleButton;
    private bool _showRepeatButton;
    private bool _showSpeedButton = true;
    private bool _showPlaylistButton = true;
    private bool _showVideoPanButton;
    private bool _showAdditionalMediaInformation;
    private List<BottomBarLayoutPresetData> _bottomBarLayoutPresets = [];
    private string _activeBottomBarLayoutPreset = "Fuze — classique";
    private bool _bottomBarLayoutInitialized;
    private bool _autoCompactMissingBottomBarItems = true;
    private bool _bottomBarLayoutPreviewActive;
    private bool _bottomBarLayoutPreviewOverlayWasVisible;
    private bool _bottomBarLayoutPreviewControlsWereVisible;
    private BottomBarLayoutDialog? _bottomBarLayoutEditorWindow;
    private BottomBarLayoutPresetData? _bottomBarLayoutEditorDraft;
    private BottomBarLayoutPresetData? _appliedBottomBarLayout;
    private FrameworkElement? _bottomBarLayoutDragElement;
    private BottomBarLayoutEditorItem? _bottomBarLayoutDragItem;
    private Point _bottomBarLayoutDragStart;
    private bool _bottomBarLayoutDragActive;
    private readonly HashSet<string> _bottomBarLayoutSelectedItemIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _bottomBarLayoutDragStartPositions =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _bottomBarLayoutSelectionAnchorId;
    private bool _bottomBarLayoutFreeDragMode;
    private string? _bottomBarLayoutPreviewTitle;
    private bool _bottomBarGuidePositionUpdateQueued;
    private double _effectiveBottomBarTitleWidth = double.NaN;
    // Référence de largeur observée pour la fenêtre courante. Elle s'agrandit
    // automatiquement lorsqu'une fenêtre est ouverte sur un grand écran afin
    // que la réduction commence dès le premier redimensionnement.
    private double _bottomBarResponsiveWindowReferenceWidth =
        BottomBarResponsiveReferenceWidth;
    private readonly Dictionary<string, FrameworkElement> _bottomBarLayoutElements =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _bottomBarLayoutItemBounds =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush BottomBarLayoutNormalBorderBrush =
        new(Color.FromArgb(210, 216, 219, 223));
    private static readonly SolidColorBrush BottomBarLayoutSelectedBorderBrush =
        new(Color.FromArgb(255, 255, 138, 66));
    private static readonly SolidColorBrush BottomBarLayoutNormalFillBrush =
        new(Color.FromArgb(22, 255, 138, 66));
    private static readonly SolidColorBrush BottomBarLayoutSelectedFillBrush =
        new(Color.FromArgb(48, 255, 138, 66));
    private bool _adaptiveInterfaceScale = true;
    private bool _autoHideCursor = true;
    private int _cursorAutoHideDelayMilliseconds = 3000;
    private bool _alwaysOnTop = true;
    private bool _showOsd = true;
    private bool _disableToolTips;
    private bool _showChapterNameInSeekPreview = true;
    private string _interfaceLanguage = "en";
    private bool _togglePlaybackOnSingleClick = true;
    private bool _toggleFullscreenOnDoubleClick = true;
    private bool _toolTipIsOpen;
    private bool _startupPlaybackGatePending;
    private bool _resumePlaybackAfterSurfaceReveal;
    private bool _videoSurfaceReady;
    private MpvMedia? _startupPlaybackPendingMedia;
    private long _startupPlaybackTraceStartedAt;
    private bool _playbackRestartedForCurrentMedia;
    private bool _discordActivityEnabled;
    private bool _diagnosticLoggingEnabled;
    private readonly DispatcherTimer _cursorHideTimer;
    private bool _cursorIsHidden;
    // Défaut lisible pour la barre supérieure; cette valeur reste configurable.
    private int _topBarAutoHideDelayMilliseconds = 1500;
    private int _bottomBarAutoHideDelayMilliseconds = 500;
    private int _playlistScrollSpeed = 20;
    private bool _suppressBottomRevealAfterToolBarPin;
    private int _volumeControlStyle;
    private int _volumePopupHideDelayMilliseconds = 2000;
    private int _volumeIndicatorHideDelayMilliseconds = 1000;
    private bool _volumePopupUsesIndicatorDelay;
    private bool _volumeOverlayFollowsControls;
    private bool _hideInterfaceOnVideoStart = true;
    private bool _showSynchronizationButton;
    private bool _startVideoFullscreen = true;
    private string _preferredVideoDisplay = "auto";
    private string _videoOutput = "auto";
    private string _screenshotBaseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    private string _screenshotFolderName = "Fuze";
    private string _screenshotFormat = "png";
    private string _screenshotAffixMode = "prefix";
    private string _screenshotAffixText = "Fuze";
    private bool _screenshotSequentialNumbering;
    private bool _copyScreenshotsToClipboard;
    private Dictionary<string, string> _keyboardShortcuts = ShortcutCatalog.CreateDefaults();
    private bool _mouseWheelTimelineEnabled = true;
    private bool _mouseWheelVolumeEnabled = true;
    private bool _centerWheelVolumeEnabled = true;
    private bool _centerWheelTimelineEnabled;
    private bool _mouseWheelAudioTracksEnabled = true;
    private bool _mouseWheelSubtitleTracksEnabled = true;
    private bool _ignoreKeyboardVolumeButtons;
    private bool _resetVolumeOnStartup = true;
    private int _startupVolume = 100;
    private bool _toolBarPinnedOpen;
    private bool _toolBarTemporarilyExpanded;
    private bool _settingsDialogOpening;
    private bool _fullscreenTransitionInProgress;
    private long _lastVideoClickTick = -1;
    private Point _lastVideoClickPosition;
    private long? _pendingSeekTarget;
    private long _seekDragOriginTime;
    private int? _requestedAudioTrackId;
    private int? _requestedVideoTrackId;
    private int? _requestedSubtitleTrackId;
    private MediaPlaybackPreferencesData? _rememberedMediaPreferencesForCurrentMedia;
    private bool _rememberedMediaTracksAppliedForCurrentMedia;
    private double _videoZoom;
    private double _videoPanX;
    private double _videoPanY;
    private bool _videoPanModeEnabled;
    private bool _videoPanDragging;
    private Point _videoPanDragStart;
    private double _videoPanDragStartX;
    private double _videoPanDragStartY;
    private int _customZoomPercent = 100;
    private string _customAspectRatio = "16:9";
    private string _videoAspectOverride = "16:9";
    private double _interfaceScale = 1;
    private bool _suppressToolBarActivation = true;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private bool _windowWasMaximizedBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen = ResizeMode.CanResize;
    private WindowPlacement _windowPlacementBeforeFullscreen;
    private bool _hasWindowPlacement;
    private IntPtr _windowHandle;
    private HwndSource? _windowSource;
    private readonly HashSet<int> _registeredWindowMoveHotkeys = [];
    private IntPtr _windowMoveKeyboardHook;
    private LowLevelKeyboardProc? _windowMoveKeyboardHookCallback;
    private HwndSource? _videoOverlaySource;
    private NativePoint _lastCursorPosition;
    private bool _hasLastCursorPosition;
    private long _lastCursorMovementTick;
    private bool _pointerWasInBottomZone;
    private bool _pointerWasInTopZone;
    private int _openContextMenuCount;
    private int _controlsAnimationVersion;
    private int _toolBarAnimationVersion;
    private IntPtr _videoBackgroundBrush;
    private Window? _videoOverlayWindow;
    private IntPtr _videoOverlayHandle;
    private IntPtr _activeToolTipHandle;
    private bool _activeTopmostApplied;
    private int _modalDialogDepth;
    private bool _restoreVideoOverlayAfterModalDialog;
    private bool _isLiveWindowResize;
    private bool _videoOverlayHiddenForResize;
    private bool _playbackBarsHiddenForResize;
    private Visibility _topBarVisibilityBeforeResize = Visibility.Collapsed;
    private Visibility _bottomBarVisibilityBeforeResize = Visibility.Collapsed;
    private bool _topBarHitTestBeforeResize;
    private bool _bottomBarHitTestBeforeResize;
    private bool _controlsHideTimerBeforeResize;
    private bool _gearControlsHideTimerBeforeResize;
    private bool _toolBarHideTimerBeforeResize;
    private bool _videoOverlayHiddenForDisplayChange;
    private bool _topBarHiddenForDisplayChange;
    private Visibility _topBarVisibilityBeforeDisplayChange = Visibility.Collapsed;
    private bool _topBarHitTestBeforeDisplayChange;
    private double _topBarOpacityBeforeDisplayChange = 1;
    private double _topBarTranslateBeforeDisplayChange;
    private bool _toolBarHideTimerBeforeDisplayChange;
    private bool _videoOverlayHiddenForStartup;
    private bool _startupWindowPresentationPending;
    private bool _startupWindowsCloaked;
    private bool _pendingPlaybackAfterWindowTransition;
    private int _displayTransitionGeneration;
    private double _liveResizeHorizontalInsets;
    private double _liveResizeVerticalInsets;
    private IntPtr[] _liveResizeVideoWindows = [];
    private NativeRect _lastNativeSizingBounds;
    private readonly HashSet<uint> _discordProcessIds = [];
    private readonly object _discordProcessLock = new();
    private long _nextDiscordProcessRefresh;
    private long _lastFuseForegroundTick;
    private WinEventCallback? _discordOverlayEventCallback;
    private GCHandle _discordOverlayCallbackHandle;
    private IntPtr _discordOverlayShowHook;
    private List<ChapterMarkerInfo> _chapterMarkers = [];
    private bool _chapterMarkersReady;
    private bool _chapterMarkerLoadInProgress;
    private int _chapterMarkerLoadAttempts;
    private int _chapterMediaGeneration;
    private long _nextChapterMarkerLoadTick;

    private sealed record ChapterMarkerInfo(int Index, long TimeOffset, string Name);

    private const double SeekTrackHorizontalInset = 8;

    private const int WmSize = 0x0005;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmHotKey = 0x0312;
    private const int WmSysCommand = 0x0112;
    private const int WmDisplayChange = 0x007E;
    private const int WmEraseBackground = 0x0014;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int ScSize = 0xF000;
    private const int SizeBottomLeft = 7;
    private const int SizeBottomRight = 8;
    private const int WmSizing = 0x0214;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;
    private const int WmControlColorStatic = 0x0138;
    private const uint EventObjectShow = 0x8002;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint MonitorDefaultToNearest = 2;
    private const int WhKeyboardLowLevel = 13;
    private const int LlKeyDown = 0x0100;
    private const int LlSysKeyDown = 0x0104;
    private const int LlKeyUp = 0x0101;
    private const int LlSysKeyUp = 0x0105;
    private const uint LlKbdInjected = 0x00000010;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsContinuous = 0x80000000;
    private const int WindowMoveHotkeyBaseId = 0x4F20;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkShift = 0x10;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const uint MonitorInfoPrimary = 1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoRedraw = 0x0008;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint SwpFrameChanged = 0x0020;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwFrame = 0x0400;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;
    private const int SwShowMaximized = 3;
    private const int SwShowNoActivate = 4;
    private const int SwRestore = 9;
    private const int SwHide = 0;
    private const int SizingLeft = 1;
    private const int SizingRight = 2;
    private const int SizingTop = 3;
    private const int SizingTopLeft = 4;
    private const int SizingTopRight = 5;
    private const int SizingBottom = 6;
    private const int SizingBottomLeft = 7;
    private const int SizingBottomRight = 8;
    private const uint GwChild = 5;
    private const int HitTopLeft = 13;
    private const int HitTopRight = 14;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;
    private const int HitTransparent = -1;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCloak = 13;
    private const int DwmDoNotRound = 1;
    private const int DwmBorderColorBlack = 0;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    private delegate bool MonitorEnumCallback(IntPtr monitor, IntPtr deviceContext,
        ref NativeRect monitorRectangle, IntPtr parameter);
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId,
        int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceKey;
    }

    private sealed record VideoMonitorInfo(string Id, NativeRect Monitor, NativeRect WorkArea,
        bool IsPrimary);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint executionState);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, [In] ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRectangle,
        MonitorEnumCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayAdapters(IntPtr deviceName, uint deviceIndex,
        ref DisplayDeviceInfo displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitorDevices(string deviceName, uint deviceIndex,
        ref DisplayDeviceInfo displayDevice, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr GetRelatedWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);


    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
        ref int attributeValue, int attributeSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle,
        IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback,
        IntPtr module, uint threadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr deviceContext, [In] ref NativeRect rectangle, IntPtr brush);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMinimum, uint eventMaximum, IntPtr eventHookModule,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint colorReference);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    public MainWindow()
    {
        DataContext = this;
        InitializeComponent();
        InitializeBottomBarLayoutHosts();
        // Une ouverture par association de fichier doit préparer la géométrie
        // finale avant de montrer la première image. La fenêtre principale
        // reste néanmoins visible pendant l'initialisation de mpv : la cacher
        // avec Opacity = 0 laissait Windows afficher un écran noir pendant le
        // chargement du média. Un écran de chargement Fuse est affiché dans la
        // surface vidéo à la place.
        _startupWindowPresentationPending = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(path => File.Exists(path) && IsUsableMediaLocation(path));
        // Ne jamais placer un panneau opaque devant mpv au démarrage. Cette
        // barrière artificielle était la source de l'écran noir perceptible.
        StartupLoadingOverlay.Visibility = Visibility.Collapsed;
        AddHandler(FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_OnElementLoaded), true);
        VideoOverlay.AddHandler(ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(VideoToolTip_OnOpening), true);
        VideoOverlay.AddHandler(ToolTipService.ToolTipClosingEvent,
            new ToolTipEventHandler(VideoToolTip_OnClosing), true);
        // La file est une collection observable, mais on fixe aussi la source
        // explicitement afin que le tiroir reste synchronisé après un changement
        // de fenêtre ou de mode plein écran.
        PlaylistList.ItemsSource = _displayedPlaylist;

        var restoredSession = _sessionStore.Load();

        VideoOverlay.AddHandler(DragDrop.DragEnterEvent,
            new DragEventHandler(Window_OnDragOver), true);
        VideoOverlay.AddHandler(DragDrop.DragOverEvent,
            new DragEventHandler(Window_OnDragOver), true);
        VideoOverlay.AddHandler(DragDrop.DropEvent,
            new DragEventHandler(Window_OnDrop), true);

        _mediaPlayer = new MpvPlayer();
        _mediaPlayer.ConfigureVideoOutput(restoredSession.VideoOutput);
        VideoView.HandleCreated += VideoView_OnHandleCreated;
        AttachVideoOutput();

        WirePlayerEvents();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _uiTimer.Tick += (_, _) =>
        {
            RefreshTimeline();
            TrackPointerProximity();
        };

        _resumeCheckpointTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Un point de reprise fréquent protège contre une fermeture
            // inattendue sans écrire le fichier de session à chaque image.
            Interval = TimeSpan.FromSeconds(2)
        };
        _resumeCheckpointTimer.Tick += (_, _) => PersistResumeCheckpoint();

        _playlistAutoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _playlistAutoScrollTimer.Tick += (_, _) =>
        {
            if (_playlistDragging)
            {
                ScrollPlaylistForDrag();
                MovePlaylistItemWithMouse(_playlistDragLastPoint);
            }
            else
                _playlistAutoScrollTimer.Stop();
        };

        _activeZOrderTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _activeZOrderTimer.Tick += (_, _) => UpdateActiveTopmostProtection();

        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.7)
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
        };

        _controlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_bottomBarAutoHideDelayMilliseconds)
        };
        _controlsHideTimer.Tick += (_, _) =>
        {
            if (_modalDialogDepth > 0 || _settingsDialogOpening || _toolTipIsOpen)
            {
                RestartControlsHideTimer();
                return;
            }

            if (_openContextMenuCount > 0 || _isSeeking || _isAdjustingVolume ||
                IsPointerOverPlaybackControl() ||
                (VolumePopup.Visibility == Visibility.Visible && IsPointerInsideElement(VolumePopup)) ||
                ControlsPanel.IsMouseCaptureWithin)
            {
                RestartControlsHideTimer();
                return;
            }

            HidePlaybackControls();
        };

        // Après activation de l'écrou, ce minuteur dédié replie le bas même
        // si le pointeur ou le focus reste sur le bouton de la barre supérieure.
        _gearControlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_bottomBarAutoHideDelayMilliseconds)
        };
        _gearControlsHideTimer.Tick += (_, _) =>
        {
            _gearControlsHideTimer.Stop();
            if (_modalDialogDepth > 0 || _settingsDialogOpening || _toolTipIsOpen)
                return;
            if (_toolBarPinnedOpen)
                HidePlaybackControls();
        };

        _toolBarHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_topBarAutoHideDelayMilliseconds)
        };
        _toolBarHideTimer.Tick += (_, _) =>
        {
            _toolBarHideTimer.Stop();
            // Une barre maintenue par l'écrou ne doit jamais être repliée par
            // le minuteur de survol. Le clic de maintien est volontairement
            // indépendant du délai normal de disparition.
            if (_toolBarPinnedOpen)
                return;
            if (_modalDialogDepth > 0 || _settingsDialogOpening || _toolTipIsOpen)
                return;
            if (IsPointerInsideElement(ToolBarHost) || MenuBar.IsKeyboardFocusWithin)
            {
                _toolBarHideTimer.Start();
                return;
            }

            CollapseToolBar();
        };

        _videoLayoutTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _videoLayoutTimer.Tick += (_, _) =>
        {
            _videoLayoutTimer.Stop();
            RefreshVideoLayout();
        };

        _videoSurfaceRevealTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // Le déclenchement vient de MPV_EVENT_PLAYBACK_RESTART.
            // Un seul passage par la file Render suffit ensuite à laisser WPF
            // appliquer la géométrie de la surface, sans délai fixe arbitraire.
            Interval = TimeSpan.FromMilliseconds(1)
        };
        _videoSurfaceRevealTimer.Tick += (_, _) =>
        {
            _videoSurfaceRevealTimer.Stop();
            if (_isClosing || _currentMedia is null)
                return;

            _videoSurfaceReady = true;
            _startupPlaybackWatchdogTimer?.Stop();
            var resumePlayback = _resumePlaybackAfterSurfaceReveal;
            _resumePlaybackAfterSurfaceReveal = false;
            _startupPlaybackPendingMedia = null;
            VideoView.Visibility = Visibility.Visible;
            if (VideoView.NativeHandle != IntPtr.Zero)
                ShowWindow(VideoView.NativeHandle, SwShowNoActivate);
            AttachVideoOutput();
            TraceStartupPlayback("première image révélée");
            // La présentation de démarrage (moniteur, interface discrète,
            // plein écran) se fait après que la surface native existe. Cela
            // évite de déplacer une fenêtre encore masquée et supprime le
            // flash observé lors du changement d'état initial.
            if (resumePlayback)
            {
                var preserveWindowPresentation = _preserveWindowPresentationForCurrentMedia;
                var needsWindowTransition = !preserveWindowPresentation &&
                    _startVideoFullscreen && (!_isFullscreen || _fullscreenTransitionInProgress);
                _pendingPlaybackAfterWindowTransition = needsWindowTransition;
                ApplyVideoStartupPresentation();

                if (!needsWindowTransition)
                {
                    RevealOverlayAfterStartup();
                    _mediaPlayer.SetPause(false);
                    HideStartupLoadingOverlay();
                }
            }
            else
            {
                RevealOverlayAfterStartup();
                RevealStartupWindowIfReady();
                HideStartupLoadingOverlay();
            }
        };

        _startupPlaybackWatchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Cette garde n'est pas le chemin normal. Elle empêche seulement
            // un fichier ou un pilote défectueux de laisser indéfiniment Fuse
            // sur « Ouverture de la vidéo… ».
            Interval = TimeSpan.FromSeconds(8)
        };
        _startupPlaybackWatchdogTimer.Tick += (_, _) =>
        {
            _startupPlaybackWatchdogTimer.Stop();
            if (_isClosing || _currentMedia is null || _videoSurfaceReady)
                return;

            _startupPlaybackGatePending = false;
            _resumePlaybackAfterSurfaceReveal = true;
            ScheduleVideoSurfaceReveal(force: true);
        };

        _seekCommitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _seekCommitTimer.Tick += (_, _) => CommitPendingSeek();

        _videoClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            // Le clic simple est volontairement différé : il ne doit jamais
            // partir avant de savoir s'il s'agit du premier clic d'un double
            // clic. 280 ms est suffisamment court pour Play/Pause et réduit
            // fortement les doubles clics involontaires.
            Interval = TimeSpan.FromMilliseconds(280)
        };
        _videoClickTimer.Tick += (_, _) =>
        {
            _videoClickTimer.Stop();
            _lastVideoClickTick = -1;
            if (_togglePlaybackOnSingleClick && _currentIndex >= 0 && !_fullscreenTransitionInProgress)
                TogglePlayback();
        };

        _startupTitleTimer = new DispatcherTimer(DispatcherPriority.Background);
        _startupTitleTimer.Tick += (_, _) =>
        {
            _startupTitleTimer.Stop();
            ShowStartupTitleOverlay();
        };

        _volumePopupHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_volumePopupHideDelayMilliseconds)
        };
        _volumePopupHideTimer.Tick += (_, _) =>
        {
            _volumePopupHideTimer.Stop();
            if (!IsPointerInsideElement(VolumePopup))
                HideVolumePopup();
        };

        _volumeIndicatorHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_volumeIndicatorHideDelayMilliseconds)
        };
        _volumeIndicatorHideTimer.Tick += (_, _) =>
        {
            _volumeIndicatorHideTimer.Stop();
            VolumeIndicatorOverlay.Visibility = Visibility.Collapsed;
        };

        _cursorHideTimer = new DispatcherTimer
        {
            // Le délai du curseur est indépendant de celui des deux barres.
            Interval = TimeSpan.FromMilliseconds(_cursorAutoHideDelayMilliseconds)
        };
        _cursorHideTimer.Tick += (_, _) =>
        {
            _cursorHideTimer.Stop();
            if (!_autoHideCursor || _isClosing || _modalDialogDepth > 0 ||
                IsPointerOverPlaybackControl() || IsPointerInsideElement(ToolBarHost))
                return;

            SetPlaybackCursorVisibility(false);
            _cursorIsHidden = true;
        };

        SourceInitialized += Window_OnSourceInitialized;
        Activated += (_, _) =>
        {
            // Un changement de focus ne modifie aucune géométrie. Recalculer
            // ici la surface native de mpv provoquait une recomposition D3D
            // (et donc un écran noir) à l'ouverture ou à la fermeture de
            // chaque fenêtre Fuse.
            UpdateActiveTopmostProtection();
        };
        Deactivated += (_, _) =>
        {
            Dispatcher.BeginInvoke(UpdateActiveTopmostProtection, DispatcherPriority.Background);
        };
        InputManager.Current.PreProcessInput += InputManager_OnPreProcessInput;

        RestoreSession(restoredSession);
        try
        {
            var processAge = DateTime.Now - Process.GetCurrentProcess().StartTime;
            WriteDiagnosticLog(LocalizationService.Format(
                "Démarrage application +{0:0} ms • interface initialisée",
                processAge.TotalMilliseconds));
        }
        catch
        {
            // Le diagnostic de performance ne doit jamais bloquer l'interface.
        }
        _initialized = true;
        LocalizationService.LanguageChanged += LocalizationService_OnLanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= LocalizationService_OnLanguageChanged;
        _mediaPlayer.Volume = GetEngineVolume((int)Math.Round(VolumeSlider.Value));
        _uiTimer.Start();
        _resumeCheckpointTimer.Start();
        _activeZOrderTimer.Start();

        Loaded += (_, _) =>
        {
            UpdateResponsiveInterfaceScale();
            if (!_startupWindowPresentationPending)
                FitNormalWindowToVideoAspect();
            // Créer l'overlay une seule fois avant que mpv démarre. Le cacher
            // puis le recréer au-dessus d'une surface D3D11 forçait DWM à
            // recomposer l'écran et produisait deux à trois secondes de noir.
            // Son contenu reste masqué pendant la transition initiale, mais
            // son HWND demeure vivant pour toute la session.
            if (_startupWindowPresentationPending)
            {
                _videoOverlayHiddenForStartup = true;
                HidePlaybackControlsImmediately();
                EmptyState.Visibility = Visibility.Collapsed;
            }
            ShowVideoOverlayWindow();
            UpdateVideoOverlayPresentationState();
            QueueAdaptiveAudioDeviceUpdate();

            if (!OpenCommandLineMedia())
            {
                _videoOverlayHiddenForStartup = false;
                UpdateVideoOverlayPresentationState();
                RevealPlaybackControls();
                RevealStartupWindowIfReady();
            }
        };
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_startupWindowPresentationPending || _startupPlaybackGatePending ||
                _resumePlaybackAfterSurfaceReveal)
                return;

            FitNormalWindowToVideoAspect();
            RefreshVideoLayout();
        }, DispatcherPriority.ContextIdle);
    }

    public ObservableCollection<PlaylistItem> Playlist { get; } = [];
    private readonly ObservableCollection<PlaylistItem> _displayedPlaylist = [];
    private string _playlistSearchQuery = string.Empty;
    private Point _playlistDragStartPoint;
    private Point _playlistDragLastPoint;
    private PlaylistItem? _playlistDragItem;
    private bool _playlistDragging;
    private readonly DispatcherTimer _playlistAutoScrollTimer;

    private void WirePlayerEvents()
    {
        _mediaPlayer.Opening += (_, _) => Dispatch(() => SetEngineState("OUVERTURE…", "#FFFFB55B"));
        _mediaPlayer.Buffering += (_, e) => Dispatch(() =>
            SetEngineState($"MISE EN MÉMOIRE {e.Cache:0}%", "#FFFFB55B"));
        _mediaPlayer.Playing += (_, _) => Dispatch(() =>
        {
            UpdateSystemPlaybackAwakeState(true);
            TraceStartupPlayback("lecture audio/vidéo démarrée");
            _mediaPlayer.Mute = _isMuted;
            // mpv signale la reconfiguration de la sortie vidéo séparément de
            // l'état Playing. On attend cet événement avant de révéler la
            // surface; pour l'audio seul, il n'y aura naturellement pas de
            // VIDEO_RECONFIG à attendre.
            if (_playbackRestartedForCurrentMedia || _mediaPlayer.VideoTrackCount <= 0)
                ScheduleVideoSurfaceReveal();
            if (_pauseAfterOpeningForResumePrompt &&
                _pendingResumePromptPositionMilliseconds > 0 &&
                !string.IsNullOrWhiteSpace(_pendingResumePromptLocation))
            {
                var promptPosition = _pendingResumePromptPositionMilliseconds;
                var promptLocation = _pendingResumePromptLocation;
                _pendingResumePromptPositionMilliseconds = 0;
                _pendingResumePromptLocation = null;
                _pauseAfterOpeningForResumePrompt = false;

                if (ShouldSkipResumePrompt(promptPosition))
                {
                    // Une position très proche du début ou de la fin ne mérite
                    // pas d'interrompre l'ouverture avec une question. Dans
                    // les deux cas, la lecture repart simplement du début.
                    _mediaPlayer.SetPause(!_autoPlayOnOpen || _resumePlaybackAfterSurfaceReveal);
                }
                else
                {
                    _mediaPlayer.SetPause(true);
                    // Replier les deux barres avant d'afficher la question de
                    // reprise évite qu'elles ne réapparaissent une image au
                    // centre de l'overlay lors de la fermeture du dialogue.
                    ApplyVideoStartInterfacePreference();
                    var resume = AskToResumeMedia(promptLocation, promptPosition);
                    if (resume)
                    {
                        _pendingResumePositionMilliseconds = promptPosition;
                        _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(promptPosition));
                    }

                    if (_autoPlayOnOpen && !_resumePlaybackAfterSurfaceReveal)
                        _mediaPlayer.SetPause(false);
                }
            }
            if (_pendingResumePositionMilliseconds > 0)
            {
                var resumePosition = _pendingResumePositionMilliseconds;
                _pendingResumePositionMilliseconds = 0;
                _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(resumePosition));
            }
            EmptyState.Visibility = Visibility.Collapsed;
            SetPlayPauseVisual(true);
            UpdateMuteVisual();
            UpdateTrackIndicators();
            if (!TryApplyRememberedMediaTrackSelections())
            {
                TryApplyPreferredAudioTrack();
                TryApplyPreferredSubtitleTrack();
            }
            ApplyVideoStartupPresentation();
            ScheduleStartupTitleOverlay();
            // Ces opérations peuvent redimensionner le HWND enfant et forcer
            // une nouvelle composition D3D. Elles sont nécessaires à la
            // première image d'un média, mais ne doivent jamais être rejouées
            // lors d'une simple sortie de pause : la surface déjà rendue
            // deviendrait alors noire pendant sa recomposition.
            if (!_startupPlaybackGatePending &&
                !_resumePlaybackAfterSurfaceReveal &&
                !_fixedVideoPresentationAppliedForCurrentMedia)
            {
                _fixedVideoPresentationAppliedForCurrentMedia = true;
                FitNormalWindowToVideoAspect();
                ApplyFixedVideoPresentation();
            }
            SetEngineState("EN LECTURE", "#FF45D483");
            EnsureChapterMarkers(_mediaPlayer.Length, true);
        });
        _mediaPlayer.FileLoaded += (_, _) => Dispatch(() =>
        {
            if (_isClosing || _currentMedia is null)
                return;

            TraceStartupPlayback("fichier chargé par mpv");

            if (_startupPlaybackGatePending)
            {
                _startupPlaybackGatePending = false;
                // Le média est chargé en pause. mpv produira naturellement
                // PLAYBACK_RESTART lorsque sa première image sera prête.
                _resumePlaybackAfterSurfaceReveal = true;
            }

            // Prépare l’analyse complète en arrière-plan après le chargement
            // du fichier. Le premier clic sur les informations détaillées
            // réutilisera alors le résultat sans retarder l’interface.
            QueueMediaInformationPreload(_currentMedia.Location);

            // Les médias purement audio n'auront pas de VIDEO_RECONFIG.
            if (_mediaPlayer.VideoTrackCount <= 0)
                ScheduleVideoSurfaceReveal();
        });
        _mediaPlayer.VideoReconfigured += (_, _) => Dispatch(() =>
        {
            if (_isClosing || _currentMedia is null)
                return;

            // VIDEO_RECONFIG confirme uniquement la géométrie de la sortie.
            // Il peut arriver avant le démarrage réel de la lecture. Ne jamais
            // s'en servir seul pour libérer l'audio ou retirer le panneau de
            // chargement au démarrage.
            if (_playbackRestartedForCurrentMedia)
                ScheduleVideoSurfaceReveal();
        });
        _mediaPlayer.Paused += (_, _) => Dispatch(() =>
        {
            UpdateSystemPlaybackAwakeState(false);
            SetPlayPauseVisual(false);
            SetEngineState("EN PAUSE", "#FFFFB55B");
        });
        _mediaPlayer.Stopped += (_, _) => Dispatch(() =>
        {
            UpdateSystemPlaybackAwakeState(false);
            SetPlayPauseVisual(false);
            SetEngineState("ARRÊTÉ", "#FF8C929F");
        });
        _mediaPlayer.EncounteredError += (_, _) => Dispatch(() =>
        {
            UpdateSystemPlaybackAwakeState(false);
            _startupPlaybackWatchdogTimer.Stop();
            _startupPlaybackPendingMedia = null;
            _startupPlaybackGatePending = false;
            _resumePlaybackAfterSurfaceReveal = false;
            SetPlayPauseVisual(false);
            SetEngineState("ERREUR DE LECTURE", "#FFFF5D73");
            HideStartupLoadingOverlay();
            RevealStartupWindowIfReady();
            ShowToast("Impossible de lire ce média");
        });
        _mediaPlayer.EndReached += (_, _) => Dispatch(HandlePlaybackEnded);
        _mediaPlayer.ESAdded += (_, _) => Dispatch(() =>
        {
            UpdateTrackIndicators();
            if (!TryApplyRememberedMediaTrackSelections())
            {
                TryApplyPreferredAudioTrack();
                TryApplyPreferredSubtitleTrack();
            }
        });
        _mediaPlayer.ESDeleted += (_, _) => Dispatch(UpdateTrackIndicators);
        _mediaPlayer.ESSelected += (_, _) => Dispatch(HandleTrackSelectionChanged);
        _mediaPlayer.ChapterChanged += (_, _) => Dispatch(() =>
            EnsureChapterMarkers(_mediaPlayer.Length, true));
        _mediaPlayer.PlaybackRestarted += (_, _) => Dispatch(() =>
        {
            if (_isClosing || _currentMedia is null)
                return;

            TraceStartupPlayback("première image signalée par mpv");
            _playbackRestartedForCurrentMedia = true;
            // MPV documente cet événement comme le point où une lecture ou
            // une recherche est réinitialisée. La surface peut être révélée
            // après ce signal, au prochain passage Render de WPF.
            if (!_startupPlaybackGatePending || _resumePlaybackAfterSurfaceReveal)
                ScheduleVideoSurfaceReveal();
        });
    }

    private void UpdateSystemPlaybackAwakeState(bool playing)
    {
        var state = EsContinuous;
        if (_preventSleepDuringPlayback && playing)
            state |= EsSystemRequired | EsDisplayRequired;
        SetThreadExecutionState(state);
    }

    private void VideoView_OnHandleCreated(object? sender, EventArgs e)
    {
        AttachVideoOutput();
    }

    private void AttachVideoOutput()
    {
        if (!_isClosing)
            VideoView.Attach(_mediaPlayer);
    }

    private void ApplyFixedVideoPresentation(Action? completed = null)
    {
        if (_isClosing)
            return;

        try
        {
            _mediaPlayer.SetVideoAspectRatio(_videoAspectOverride);
            _mediaPlayer.SetVideoZoom(_videoZoom);
            _mediaPlayer.Scale = 0;
        }
        catch (MpvException)
        {
            completed?.Invoke();
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing)
                return;

            AttachVideoOutput();
            AppShell.UpdateLayout();
            ResizeNativeVideoSurfaceToView();
            RefreshVideoLayout();
            completed?.Invoke();
        }, DispatcherPriority.Render);
    }

    private void TryStartPreparedPlayback()
    {
        var expectedMedia = _startupPlaybackPendingMedia;
        if (_isClosing || expectedMedia is null ||
            !ReferenceEquals(_currentMedia, expectedMedia))
            return;

        // Charger le fichier seulement après la dernière passe du plein écran
        // évite que mpv crée sa sortie dans un rectangle qui sera aussitôt
        // remplacé. Le bloc finally de la transition rappelle cette méthode.
        if (_fullscreenTransitionInProgress)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || !ReferenceEquals(_currentMedia, expectedMedia) ||
                !ReferenceEquals(_startupPlaybackPendingMedia, expectedMedia) ||
                _fullscreenTransitionInProgress)
                return;

            AttachVideoOutput();
            AppShell.UpdateLayout();
            ResizeNativeVideoSurfaceToView();
            AlignVideoOverlayWindow();
            _playbackRestartedForCurrentMedia = false;
            _startupPlaybackPendingMedia = null;
            _startupPlaybackWatchdogTimer.Stop();
            _startupPlaybackWatchdogTimer.Start();
            TraceStartupPlayback("chargement transmis à mpv");
            if (!_mediaPlayer.Play())
            {
                _startupPlaybackWatchdogTimer.Stop();
                HideStartupLoadingOverlay();
                ShowToast("La lecture n’a pas pu démarrer");
            }
        }, DispatcherPriority.Render);
    }

    private void ResizeNativeVideoSurfaceToView()
    {
        if (VideoView.NativeHandle == IntPtr.Zero || VideoView.ActualWidth < 1 || VideoView.ActualHeight < 1 ||
            PresentationSource.FromVisual(VideoView) is null)
            return;

        var start = VideoView.PointToScreen(new Point(0, 0));
        var end = VideoView.PointToScreen(new Point(VideoView.ActualWidth, VideoView.ActualHeight));
        ResizeNativeVideoSurface(
            Math.Max(1, (int)Math.Round(Math.Abs(end.X - start.X))),
            Math.Max(1, (int)Math.Round(Math.Abs(end.Y - start.Y))));
    }

    private void ResizeNativeVideoSurface(int width, int height)
    {
        if (VideoView.NativeHandle == IntPtr.Zero)
            return;

        SetWindowPos(VideoView.NativeHandle, IntPtr.Zero, 0, 0, Math.Max(1, width), Math.Max(1, height),
            SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
    }

    private void RestoreSession(PlayerSession session)
    {
        _resetVolumeOnStartup = session.ResetVolumeOnStartup;
        _startupVolume = Math.Clamp(session.StartupVolume, 0, 125);
        VolumeSlider.Value = _resetVolumeOnStartup
            ? _startupVolume
            : Math.Clamp(session.Volume, 0, 125);
        _playlistVisible = false;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        _rewindSeconds = Math.Clamp(session.RewindSeconds, 1, 600);
        _forwardSeconds = Math.Clamp(session.ForwardSeconds, 1, 600);
        _prioritizeChapters = session.PrioritizeChapters;
        _playNextMediaAutomatically = session.PlayNextMediaAutomatically;
        _enhancedPlaybackEnabled = session.EnhancedPlaybackEnabled;
        _enhancedFolderAdvanceEnabled = session.EnhancedFolderAdvanceEnabled;
        _enhancedFolderShowNameEnabled = session.EnhancedFolderShowNameEnabled;
        _showEnhancedUpcomingInPlaylist = session.ShowEnhancedUpcomingInPlaylist;
        _showEnhancedNextFolderInPlaylist = session.ShowEnhancedNextFolderInPlaylist;
        ResetEnhancedPlaybackState();
        _resumePlayback = session.ResumePlayback;
        _resumePromptStartSkipPercent = Math.Clamp(session.ResumePromptStartSkipPercent, 0, 100);
        _resumePromptEndSkipPercent = Math.Clamp(session.ResumePromptEndSkipPercent, 0, 100);
        _autoPlayOnOpen = session.AutoPlayOnOpen;
        _confirmClose = session.ConfirmClose;
        _preventSleepDuringPlayback = session.PreventSleepDuringPlayback;
        _rememberMediaSettings = session.RememberMediaSettings;
        _recentMediaRetentionDays = Math.Clamp(session.RecentMediaRetentionDays, 0, 3650);
        _recentMediaFolderDepth = Math.Clamp(session.RecentMediaFolderDepth, 0, 10);
        _playlistFolderDepth = Math.Clamp(session.PlaylistFolderDepth, 0, 10);
        _fileAssociationsEnabled = session.FileAssociationsEnabled;
        _fileAssociationExtensions = NormalizeFileAssociationExtensions(session.FileAssociationExtensions);
        _customFileAssociationTypes = FileAssociationService.NormalizeCustomTypes(session.CustomFileAssociationTypes);
        _shufflePlayback = session.ShufflePlayback;
        _repeatPlayback = session.RepeatPlayback;
        _repeatPlaylist = session.RepeatPlaylist;
        _shufflePlayedIndices.Clear();
        _lastMediaLocation = session.LastMediaLocation;
        _lastMediaPositionMilliseconds = Math.Max(0, session.LastMediaPositionMilliseconds);
        _hardwareDecoding = session.HardwareDecoding;
        _deinterlacing = session.Deinterlacing;
        _hdrMode = NormalizeHdrMode(session.HdrMode);
        _bufferingEnabled = session.BufferingEnabled;
        _audioNormalization = session.AudioNormalization;
        _autoSwitchAudioDevice = session.AutoSwitchAudioDevice;
        _adaptiveAudioModeEnabled = session.AdaptiveAudioModeEnabled;
        _adaptiveAudioDisplayMappings = (session.AdaptiveAudioDisplayMappings ?? [])
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.DisplayId))
            .GroupBy(mapping => mapping.DisplayId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Select(mapping => new AdaptiveAudioDisplayMappingData
            {
                DisplayId = mapping.DisplayId.Trim(),
                DisplayName = mapping.DisplayName?.Trim() ?? string.Empty,
                AudioDevice = string.IsNullOrWhiteSpace(mapping.AudioDevice)
                    ? "auto"
                    : mapping.AudioDevice.Trim()
            })
            .ToList();
        _preferSdhSubtitles = session.PreferSdhSubtitles;
        _showScreenshotButton = session.ShowScreenshotButton;
        _showShuffleButton = session.ShowShuffleButton;
        _showRepeatButton = session.ShowRepeatButton;
        _showSpeedButton = session.ShowSpeedButton;
        _showPlaylistButton = session.ShowPlaylistButton;
        _showVideoPanButton = session.ShowVideoPanButton;
        _showAdditionalMediaInformation = session.ShowAdditionalMediaInformation;
        _adaptiveInterfaceScale = session.AdaptiveInterfaceScale;
        _autoHideCursor = session.AutoHideCursor;
        _alwaysOnTop = session.AlwaysOnTop;
        _showOsd = session.ShowOsd;
        _disableToolTips = session.DisableToolTips;
        _showChapterNameInSeekPreview = session.ShowChapterNameInSeekPreview;
        _interfaceLanguage = NormalizeInterfaceLanguage(session.InterfaceLanguage);
        _togglePlaybackOnSingleClick = session.TogglePlaybackOnSingleClick;
        _toggleFullscreenOnDoubleClick = session.ToggleFullscreenOnDoubleClick;
        _discordActivityEnabled = session.DiscordActivityEnabled;
        _diagnosticLoggingEnabled = session.DiagnosticLoggingEnabled;
        _cursorAutoHideDelayMilliseconds = session.CursorAutoHideDelayMilliseconds <= 0
            ? 3000
            : Math.Clamp(session.CursorAutoHideDelayMilliseconds, 100, 10000);
        // 350 ms était le défaut d'une ancienne version et rendait la barre
        // supérieure pratiquement inutilisable. On migre uniquement cette
        // ancienne valeur implicite; une valeur choisie par l'utilisateur reste
        // inchangée. Le nouveau défaut est de 1,5 seconde.
        var topBarDelay = session.TopBarAutoHideDelayMilliseconds == 350
            ? 1500
            : session.TopBarAutoHideDelayMilliseconds;
        ApplyInterfaceSettings(new InterfaceSettingsSnapshot(
            topBarDelay,
            session.BottomBarAutoHideDelayMilliseconds,
            session.PlaylistScrollSpeed,
            session.VolumeControlStyle,
            session.VolumePopupHideDelayMilliseconds,
            session.VolumeIndicatorHideDelayMilliseconds,
            session.HideInterfaceOnVideoStart,
            session.ShowSynchronizationButton,
            session.ShowShuffleButton,
            session.ShowRepeatButton,
            session.ShowSpeedButton,
            session.ShowPlaylistButton,
            session.ShowAdditionalMediaInformation,
            session.AutoCompactMissingBottomBarItems,
            session.ShufflePlayback,
            session.RepeatPlayback,
            session.ShowScreenshotButton,
            session.AdaptiveInterfaceScale,
            session.AutoHideCursor,
            _cursorAutoHideDelayMilliseconds,
            session.AlwaysOnTop,
            session.ShowOsd,
            session.DiscordActivityEnabled,
            session.DiagnosticLoggingEnabled,
            session.DisableToolTips,
            NormalizeInterfaceLanguage(session.InterfaceLanguage),
            session.TogglePlaybackOnSingleClick,
            session.ToggleFullscreenOnDoubleClick,
            session.BottomBarLayoutPresets,
            session.ActiveBottomBarLayoutPreset,
            session.ShowChapterNameInSeekPreview,
            session.ShowVideoPanButton));
        _startVideoFullscreen = session.StartVideoFullscreen;
        _preferredVideoDisplay = string.IsNullOrWhiteSpace(session.PreferredVideoDisplay)
            ? "auto"
            : session.PreferredVideoDisplay;
        _videoOutput = NormalizeVideoOutputSetting(session.VideoOutput);
        _customZoomPercent = Math.Clamp(session.CustomZoomPercent, 50, 1000);
        _customAspectRatio = NormalizeCustomAspectRatio(session.CustomAspectRatio);
        _screenshotBaseDirectory = NormalizeScreenshotBaseDirectory(session.ScreenshotBaseDirectory);
        _screenshotFolderName = NormalizeScreenshotFolderName(session.ScreenshotFolderName);
        _screenshotFormat = NormalizeScreenshotFormat(session.ScreenshotFormat);
        _screenshotAffixMode = NormalizeScreenshotAffixMode(session.ScreenshotAffixMode);
        _screenshotAffixText = session.ScreenshotAffixText?.Trim() ?? string.Empty;
        _screenshotSequentialNumbering = session.ScreenshotSequentialNumbering;
        _copyScreenshotsToClipboard = session.CopyScreenshotsToClipboard;
        _keyboardShortcuts = ShortcutCatalog.Normalize(session.KeyboardShortcuts);
        _mouseWheelTimelineEnabled = session.MouseWheelTimelineEnabled;
        _mouseWheelVolumeEnabled = session.MouseWheelVolumeEnabled;
        _centerWheelVolumeEnabled = session.CenterWheelVolumeEnabled;
        _centerWheelTimelineEnabled = session.CenterWheelTimelineEnabled && !_centerWheelVolumeEnabled;
        _mouseWheelAudioTracksEnabled = session.MouseWheelAudioTracksEnabled;
        _mouseWheelSubtitleTracksEnabled = session.MouseWheelSubtitleTracksEnabled;
        _ignoreKeyboardVolumeButtons = session.IgnoreKeyboardVolumeButtons;
        _selectedAudioDevice = string.IsNullOrWhiteSpace(session.AudioDevice)
            ? "auto"
            : session.AudioDevice;
        if (_autoSwitchAudioDevice)
            _selectedAudioDevice = "auto";
        _audioOutputMode = Enum.IsDefined(typeof(AudioOutputMode), session.AudioOutputMode)
            ? (AudioOutputMode)session.AudioOutputMode
            : AudioOutputMode.Automatic;
        const AudioTreatmentMode supportedTreatments = AudioTreatmentMode.Night |
                                                        AudioTreatmentMode.DialogueBoost |
                                                        AudioTreatmentMode.HeadphoneBinaural |
                                                        AudioTreatmentMode.SurroundDownmix;
        _audioTreatmentMode = (AudioTreatmentMode)session.AudioTreatmentMode & supportedTreatments;
        if (_audioTreatmentMode.HasFlag(AudioTreatmentMode.HeadphoneBinaural) &&
            _audioTreatmentMode.HasFlag(AudioTreatmentMode.SurroundDownmix))
            _audioTreatmentMode &= ~AudioTreatmentMode.SurroundDownmix;
        _audioPassthrough = session.AudioPassthrough;
        _audioExclusive = session.AudioExclusive;
        _disableAudioByDefault = session.DisableAudioByDefault;
        _autoSelectPreferredAudio = session.AutoSelectPreferredAudio;
        _preferredAudioProfile = _autoSelectPreferredAudio
            ? NormalizePreferredAudioProfile(session.PreferredAudioProfile)
            : "disabled";
        _preferredAudioTitlePriorities = NormalizeTitlePriorities(session.PreferredAudioTitlePriorities);
        _defaultAudioDelayMilliseconds = Math.Clamp(session.DefaultAudioDelayMilliseconds, -30000, 30000);
        _startupTitleOverlayEnabled = session.StartupTitleOverlayEnabled;
        _preferOriginalTitleForStartup = session.PreferOriginalTitleForStartup;
        _startupTitlePosition = NormalizeScreenPosition(session.StartupTitlePosition, "top-center");
        _startupTitleDelayMilliseconds = Math.Clamp(session.StartupTitleDelayMilliseconds, 0, 30000);
        _startupTitleDurationMilliseconds = Math.Clamp(session.StartupTitleDurationMilliseconds, 250, 30000);
        _startupTitleFont = string.IsNullOrWhiteSpace(session.StartupTitleFont)
            ? "Arial"
            : session.StartupTitleFont;
        _startupTitleFontSize = Math.Clamp(session.StartupTitleFontSize, 12, 120);
        _startupTitleTextColor = NormalizeSubtitleColor(session.StartupTitleTextColor, "#FFFFFFFF");
        _startupTitleBorderColor = NormalizeSubtitleColor(session.StartupTitleBorderColor, "#FF000000");
        _startupTitleBorderSize = Math.Clamp(session.StartupTitleBorderSize, 0, 10);
        _startupTitleShadow = session.StartupTitleShadow;
        _startupTitleMarginX = Math.Clamp(session.StartupTitleMarginX, 0, 500);
        _startupTitleMarginY = Math.Clamp(session.StartupTitleMarginY, 0, 500);
        _startupTitleScaleWithWindow = session.StartupTitleScaleWithWindow;
        _autoSelectPreferredSubtitle = session.AutoSelectPreferredSubtitle;
        _preferredSubtitleProfile = NormalizePreferredSubtitleProfile(session.PreferredSubtitleProfile);
        _preferredSubtitleTitlePriorities = NormalizeTitlePriorities(session.PreferredSubtitleTitlePriorities);
        _disableSubtitlesByDefault = session.DisableSubtitlesByDefault;
        _autoLoadExternalSubtitles = session.AutoLoadExternalSubtitles;
        _subtitleEncoding = string.IsNullOrWhiteSpace(session.SubtitleEncoding) ? "auto" : session.SubtitleEncoding;
        _subtitleFont = string.IsNullOrWhiteSpace(session.SubtitleFont) ? "Arial" : session.SubtitleFont;
        _subtitleFontSize = Math.Clamp(session.SubtitleFontSize, 12, 120);
        _subtitleTextColor = NormalizeSubtitleColor(session.SubtitleTextColor, "#FFFFFFFF");
        _subtitleBorderColor = NormalizeSubtitleColor(session.SubtitleBorderColor, "#FF000000");
        _subtitleBorderSize = Math.Clamp(session.SubtitleBorderSize, 0, 10);
        _subtitleShadow = session.SubtitleShadow;
        _subtitleForcePosition = session.SubtitleForcePosition;
        _subtitlePosition = NormalizeScreenPosition(session.SubtitlePosition, "bottom-center");
        _subtitleMarginX = Math.Clamp(session.SubtitleMarginX, 0, 500);
        _subtitleMarginY = Math.Clamp(session.SubtitleMarginY, 0, 500);
        _subtitleScaleWithWindow = session.SubtitleScaleWithWindow;
        _mediaPlayer.SetAudioDevice(_selectedAudioDevice);
        _mediaPlayer.SetAudioOutputMode(_audioOutputMode);
        _mediaPlayer.SetAudioTreatmentMode(_audioTreatmentMode);
        _mediaPlayer.SetAudioPassthrough(_audioPassthrough);
        _mediaPlayer.SetAudioExclusive(_audioExclusive);
        _mediaPlayer.SetHardwareDecoding(_hardwareDecoding);
        _mediaPlayer.SetDeinterlacing(_deinterlacing);
        _mediaPlayer.SetHdrMode(_hdrMode);
        _mediaPlayer.SetBufferingEnabled(_bufferingEnabled);
        UpdateSystemPlaybackAwakeState(_mediaPlayer.IsPlaying);
        _mediaPlayer.SetExternalSubtitleAutoLoad(_autoLoadExternalSubtitles);
        _mediaPlayer.SetAudioNormalization(_audioNormalization);
        _mediaPlayer.SetSubtitlePreferences(BuildSubtitlePreferences());
        UpdateAudioMenuAvailability();
        _recentMedia.Clear();
        _recentMediaLastOpenedUtc.Clear();
        foreach (var pair in session.RecentMediaLastOpenedUtc ?? [])
        {
            if (IsUsableMediaLocation(pair.Key))
                _recentMediaLastOpenedUtc[pair.Key] = pair.Value.Kind == DateTimeKind.Utc
                    ? pair.Value
                    : pair.Value.ToUniversalTime();
        }
        _recentMedia.AddRange(session.RecentMedia
            .Where(IsUsableMediaLocation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10));
        foreach (var location in _recentMedia)
            _recentMediaLastOpenedUtc.TryAdd(location, DateTime.UtcNow);
        PruneRecentMediaByRetention();
        _mediaPlaybackPreferences.Clear();
        foreach (var pair in session.MediaPlaybackPreferences ?? [])
        {
            if (IsUsableMediaLocation(pair.Key) && pair.Value is not null)
                _mediaPlaybackPreferences[pair.Key] = pair.Value;
        }
        PruneMediaPlaybackPreferences();
        RestoreSavedPlaylist(session.Playlist);
        ApplyFileAssociations();
        UpdateSkipButtons();
        RefreshRecentMediaMenu();
        RefreshPlaylistCount();
        UpdatePlaybackModeButtons();
    }

    private void RestoreSavedPlaylist(IEnumerable<PlaylistItemData>? savedItems)
    {
        Playlist.Clear();
        _currentIndex = -1;
        foreach (var data in (savedItems ?? [])
                     .Where(item => !string.IsNullOrWhiteSpace(item.Location))
                     .Where(item => IsUsableMediaLocation(item.Location))
                     .GroupBy(item => item.Location, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var item = new PlaylistItem
            {
                Location = data.Location,
                Title = string.IsNullOrWhiteSpace(data.Title)
                    ? PlaylistItem.FromLocation(data.Location).Title
                    : data.Title,
                IsNetwork = data.IsNetwork,
                DurationMilliseconds = Math.Max(0, data.DurationMilliseconds),
                IsEnhancedQueued = data.IsEnhancedQueued,
                IsEnhancedFolderStart = data.IsEnhancedFolderStart,
                EnhancedFolderTitle = data.EnhancedFolderTitle ?? string.Empty,
                IsManualQueueItem = data.IsManualQueueItem
            };
            item.DisplayFolderDepth = _playlistFolderDepth;
            Playlist.Add(item);
        }
    }

    private void ApplyPlaylistFolderDepth()
    {
        foreach (var item in Playlist)
            item.DisplayFolderDepth = _playlistFolderDepth;
    }

    private bool OpenCommandLineMedia()
    {
        var files = Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(File.Exists)
            .ToArray();

        if (files.Length > 0)
        {
            OpenLocations(files);
            return true;
        }

        // Restaurer la file sauvegardée avant de retomber sur le seul dernier
        // fichier. Cela conserve l'ordre et les éléments ajoutés manuellement
        // après un redémarrage inattendu.
        if (Playlist.Count > 0 && IsUsableMediaLocation(_lastMediaLocation))
        {
            var savedPair = Playlist
                .Select((item, index) => (item, index))
                .FirstOrDefault(pair => string.Equals(pair.item.Location,
                    _lastMediaLocation, StringComparison.OrdinalIgnoreCase));
            if (savedPair.item is not null)
            {
                var savedIndex = savedPair.index;
                var canResume = _resumePlayback && _lastMediaPositionMilliseconds > 1000;
                _pendingResumePositionMilliseconds = 0;
                _pendingResumePromptPositionMilliseconds = canResume
                    ? Math.Max(0, _lastMediaPositionMilliseconds)
                    : 0;
                _pendingResumePromptLocation = canResume ? Playlist[savedIndex].Location : null;
                _pauseAfterOpeningForResumePrompt = canResume;
                PlayAt(savedIndex, canResume || _autoPlayOnOpen);
                if (_showEnhancedUpcomingInPlaylist)
                    EnsureEnhancedNextQueued();
                return true;
            }
        }

        if (_resumePlayback && IsUsableMediaLocation(_lastMediaLocation))
        {
            OpenLocations([_lastMediaLocation!]);
            return true;
        }

        return false;
    }

    private void OpenFilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("Ouvrir un média"),
            Multiselect = false,
            CheckFileExists = true,
            Filter = MediaFileFilter
        };

        if (ShowModalDialog(() => dialog.ShowDialog(DialogOwnerWindow)) == true)
            OpenLocations(dialog.FileNames);
    }

    private void AddFilesToPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("Ajouter des épisodes à la file"),
            Multiselect = true,
            CheckFileExists = true,
            Filter = MediaFileFilter
        };

        if (ShowModalDialog(() => dialog.ShowDialog(DialogOwnerWindow)) != true)
            return;

        var locations = dialog.FileNames
            .Select(value => value.Trim())
            .Where(IsUsableMediaLocation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(location => !Playlist.Any(item =>
                string.Equals(item.Location, location, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (locations.Length == 0)
        {
            ShowToast("Aucun nouvel épisode à ajouter");
            return;
        }

        foreach (var location in locations)
        {
            var item = PlaylistItem.FromLocation(location);
            item.DisplayFolderDepth = _playlistFolderDepth;
            item.IsManualQueueItem = true;
            Playlist.Add(item);
        }

        RefreshPlaylistCount();
        if (_currentIndex >= 0)
            SelectCurrentPlaylistItem();
        else if (Playlist.Count == locations.Length)
            PlayAt(0);

        ShowToast(LocalizationService.Format("Épisodes ajoutés à la file : {0}", locations.Length));
    }

    private void OpenMultipleFilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("Ouvrir plusieurs médias"),
            Multiselect = true,
            CheckFileExists = true,
            Filter = MediaFileFilter
        };

        if (ShowModalDialog(() => dialog.ShowDialog(DialogOwnerWindow)) == true)
            OpenLocations(dialog.FileNames);
    }

    private void OpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get("Ouvrir un dossier multimédia"),
            Multiselect = false
        };

        if (ShowModalDialog(() => dialog.ShowDialog(DialogOwnerWindow)) != true)
            return;

        OpenLocations(ExpandDroppedPaths([dialog.FolderName]));
    }

    private void OpenDiscButton_OnClick(object sender, RoutedEventArgs e)
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.CDRom && drive.IsReady)
            .ToArray();
        if (drives.Length == 0)
        {
            ShowToast("Aucun disque prêt à être lu");
            return;
        }

        var menu = new ContextMenu();
        foreach (var drive in drives)
        {
            var location = $"dvd:///{drive.RootDirectory.FullName.Replace('\\', '/')}";
            AddContextAction(menu,
                string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
                () => OpenLocations([location]));
        }

        menu.PlacementTarget = MenuBar;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RecentMediaMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e) =>
        RefreshRecentMediaMenu();

    private void RefreshRecentMediaMenu()
    {
        if (RecentMediaMenuItem is null)
            return;

        RecentMediaMenuItem.Items.Clear();
        if (_recentMedia.Count == 0)
        {
            RecentMediaMenuItem.Items.Add(new MenuItem
            {
                Header = LocalizationService.Get("Aucun média récent"),
                IsEnabled = false
            });
            return;
        }

        foreach (var location in _recentMedia)
        {
            var menuItem = new MenuItem
            {
                Header = GetRecentMediaDisplayName(location),
                ToolTip = location,
                Tag = location
            };
            menuItem.Click += RecentMediaItem_OnClick;
            RecentMediaMenuItem.Items.Add(menuItem);
        }

        RecentMediaMenuItem.Items.Add(new Separator());
        var clearItem = new MenuItem { Header = LocalizationService.Get("Effacer les médias récents") };
        clearItem.Click += (_, _) =>
        {
            _recentMedia.Clear();
            RefreshRecentMediaMenu();
            PersistSession();
            ShowToast(LocalizationService.Get("Médias récents effacés"));
        };
        RecentMediaMenuItem.Items.Add(clearItem);
    }

    private string GetRecentMediaDisplayName(string location)
    {
        if (!File.Exists(location))
            return PlaylistItem.FromLocation(location).Title;

        var title = Path.GetFileNameWithoutExtension(location);
        var parts = new List<string> { title };
        var directory = Directory.GetParent(location);
        var depth = Math.Clamp(_recentMediaFolderDepth, 0, 10);
        for (var index = 0; index < depth && directory is not null; index++)
        {
            if (!string.IsNullOrWhiteSpace(directory.Name))
                parts.Insert(0, directory.Name);
            directory = directory.Parent;
        }

        return string.Join(" › ", parts);
    }

    private void RecentMediaItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string location })
            OpenLocations([location]);
    }

    private void SavePlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Playlist.Count == 0)
        {
            ShowToast("La liste de lecture est vide");
            return;
        }

        Directory.CreateDirectory(PlaylistsDirectory);
        var dialog = new SaveFileDialog
        {
            Title = LocalizationService.Get("Enregistrer la liste de lecture"),
            Filter = $"{LocalizationService.Get("Liste M3U8")}|*.m3u8|{LocalizationService.Get("Liste M3U")}|*.m3u",
            DefaultExt = ".m3u8",
            AddExtension = true,
            FileName = LocalizationService.Get("Liste de lecture Fuze"),
            InitialDirectory = PlaylistsDirectory
        };

        if (ShowModalDialog(() => dialog.ShowDialog(DialogOwnerWindow)) != true)
            return;

        try
        {
            var lines = new List<string> { "#EXTM3U" };
            foreach (var item in Playlist)
            {
                var seconds = item.DurationMilliseconds > 0
                    ? item.DurationMilliseconds / 1000
                    : -1;
                lines.Add($"#EXTINF:{seconds},{item.Title}");
                lines.Add(item.Location);
            }

            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            ShowToast("Liste de lecture enregistrée");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowToast("Impossible d’enregistrer la liste de lecture");
        }
    }

    private void OpenPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(PlaylistsDirectory);
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("Ouvrir une liste de lecture"),
            Filter = $"{LocalizationService.Get("Listes de lecture")}|*.m3u8;*.m3u|{LocalizationService.Get("Liste M3U8")}|*.m3u8|{LocalizationService.Get("Liste M3U")}|*.m3u",
            Multiselect = false,
            CheckFileExists = true,
            InitialDirectory = PlaylistsDirectory
        };

        if (ShowModalDialog(() => dialog.ShowDialog(DialogOwnerWindow)) != true)
            return;

        try
        {
            var playlistDirectory = Path.GetDirectoryName(dialog.FileName) ?? PlaylistsDirectory;
            var locations = File.ReadLines(dialog.FileName)
                .Select(line => line.Trim().TrimStart('\uFEFF'))
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                .Select(line => ResolvePlaylistLocation(line, playlistDirectory))
                .Where(IsUsableMediaLocation)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (locations.Length == 0)
            {
                ShowToast("Cette liste ne contient aucun média accessible");
                return;
            }

            OpenLocations(locations);
            ShowToast(LocalizationService.Format("Liste ouverte · {0} média(s)", locations.Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowToast("Impossible d’ouvrir la liste de lecture");
        }
    }

    private static string ResolvePlaylistLocation(string location, string playlistDirectory)
    {
        if (Path.IsPathFullyQualified(location) ||
            Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile)
            return location;

        return Path.GetFullPath(Path.Combine(playlistDirectory, location));
    }

    private void CloseCurrentMediaButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0)
        {
            ShowToast("Aucun média ouvert");
            return;
        }

        SaveCurrentMediaPlaybackPreferences();
        ResetTrackSynchronizationForMediaChange();
        _startupTitleTimer.Stop();
        _mediaPlayer.Stop();
        _mediaPlayer.Media = null;
        _currentMedia?.Dispose();
        _currentMedia = null;
        _currentIndex = -1;
        ResetChapterMarkers();
        ResetNowPlaying();
        PlaylistList.SelectedItem = null;
        ShowToast("Média fermé");
    }

    private void QueueMediaInformationPreload(string mediaPath)
    {
        if (_isClosing || string.IsNullOrWhiteSpace(mediaPath))
            return;

        var generation = Volatile.Read(ref _mediaInformationPreloadGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                // Laisser la lecture initiale se stabiliser avant de lancer
                // ffprobe évite toute concurrence visible au démarrage.
                await Task.Delay(750).ConfigureAwait(false);
                if (_isClosing || Volatile.Read(ref _mediaInformationPreloadGeneration) != generation)
                    return;

                await _mediaProbeService.BuildInformationAsync(mediaPath).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or OperationCanceledException)
            {
                // Le préchargement est opportuniste : une erreur ne doit pas
                // perturber la lecture ni l’ouverture du média.
            }
        });
    }

    private async void MediaInformationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= Playlist.Count || _currentMedia is null)
        {
            ShowToast("Aucun média ouvert");
            return;
        }

        var item = Playlist[_currentIndex];
        ShowToast("Lecture des informations du média…");
        var detailedInformation = await _mediaProbeService.BuildInformationAsync(item.Location);
        if (_isClosing)
            return;
        var information = BuildCondensedMediaInformation(item, detailedInformation);
        var dialog = new MediaInfoDialog(item.Title, information, compact: true) { Owner = this };
        ShowAuxiliaryDialog(dialog);
    }

    private async void AdditionalMediaInformationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= Playlist.Count || _currentMedia is null)
        {
            ShowToast("Aucun média ouvert");
            return;
        }

        var item = Playlist[_currentIndex];
        ShowToast("Analyse complète du média…");
        var information = await _mediaProbeService.BuildInformationAsync(item.Location) ??
                          BuildMediaInformation(item);
        if (_isClosing)
            return;
        var dialog = new MediaInfoDialog(item.Title, information,
            "Informations supplémentaires sur le média") { Owner = this };
        ShowAuxiliaryDialog(dialog);
    }

    private void ShowAuxiliaryDialog(Window dialog, bool preserveVideoZOrder = false)
    {
        dialog.Owner = DialogOwnerWindow;
        // La couche vidéo et la fenêtre principale peuvent être dans la bande
        // Topmost. Une fenêtre auxiliaire non-Topmost se retrouve alors visible
        // mais sous la vidéo et ne reçoit plus les clics.
        dialog.Topmost = !preserveVideoZOrder;

        _auxiliaryDialogs.Add(dialog);
        dialog.Activated += (_, _) =>
        {
            if (!preserveVideoZOrder)
                BringAuxiliaryDialogAboveVideo(dialog);
        };
        dialog.Closed += (_, _) =>
        {
            _auxiliaryDialogs.Remove(dialog);
            if (!_isClosing)
            {
                Dispatcher.BeginInvoke(UpdateActiveTopmostProtection, DispatcherPriority.Background);
                ResumeDisplayGeometryTransitionAfterDialog();
            }
        };
        dialog.Show();
        dialog.Activate();
        if (!preserveVideoZOrder)
            BringAuxiliaryDialogAboveVideo(dialog);
    }

    private Window DialogOwnerWindow =>
        _videoOverlayWindow?.IsVisible == true ? _videoOverlayWindow : this;

    private void BringAuxiliaryDialogAboveVideo(Window dialog)
    {
        if (_isClosing || !dialog.IsVisible)
            return;

        var handle = new WindowInteropHelper(dialog).Handle;
        if (handle == IntPtr.Zero)
            return;

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        BringWindowToTop(handle);
        SetForegroundWindow(handle);
    }

    private string BuildCondensedMediaInformation(PlaylistItem item, string? detailedInformation)
    {
        if (!string.IsNullOrWhiteSpace(detailedInformation))
            return CondenseMediaInformation(detailedInformation);

        var builder = new StringBuilder();
        builder.AppendLine("GÉNÉRAL");
        builder.AppendLine($"{LocalizationService.Get("Titre")}        : {item.Title}");
        builder.AppendLine($"{LocalizationService.Get("Type")}         : {GetMediaFormat(item)}");
        if (!item.IsNetwork && File.Exists(item.Location))
            builder.AppendLine($"{LocalizationService.Get("Taille")}       : {FormatFileSize(new FileInfo(item.Location).Length)}");
        builder.AppendLine($"{LocalizationService.Get("Durée")}        : {FormatTime(Math.Max(0, _mediaPlayer.Length))}");

        try
        {
            var tracks = _currentMedia?.Tracks ?? [];
            var videoTracks = tracks.Where(track => track.TrackType == MpvTrackType.Video).ToArray();
            var audioTracks = tracks.Where(track => track.TrackType == MpvTrackType.Audio).ToArray();
            var subtitleTracks = tracks.Where(track => track.TrackType == MpvTrackType.Text).ToArray();
            var chapterCount = CurrentChapterCount();

            builder.AppendLine($"{LocalizationService.Get("Vidéo")}        : {FormatTrackCount(videoTracks.Length)}");
            builder.AppendLine($"{LocalizationService.Get("Audio")}        : {FormatTrackCount(audioTracks.Length)}");
            builder.AppendLine($"{LocalizationService.Get("Sous-titres")}  : {FormatTrackCount(subtitleTracks.Length)}");
            builder.AppendLine($"{LocalizationService.Get("Chapitres")}    : {FormatTrackCount(chapterCount)}");

            if (videoTracks.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"VIDÉO — {FormatTrackCount(videoTracks.Length)}");
                for (var index = 0; index < videoTracks.Length; index++)
                {
                    var track = videoTracks[index];
                    builder.AppendLine($"{index + 1}.");
                    var width = track.Data.Video.Width;
                    var height = track.Data.Video.Height;
                    if (width > 0 && height > 0)
                        builder.AppendLine($"   {LocalizationService.Get("Résolution")} : {width} × {height}");
                    var codec = _currentMedia?.CodecDescription(track.TrackType, track.Codec);
                    if (!string.IsNullOrWhiteSpace(codec))
                        builder.AppendLine($"   {LocalizationService.Get("Format")}     : {codec}");
                }
            }

            if (audioTracks.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"AUDIO — {FormatTrackCount(audioTracks.Length)}");
                for (var index = 0; index < audioTracks.Length; index++)
                {
                    var track = audioTracks[index];
                    builder.AppendLine($"{index + 1}.");
                    builder.AppendLine($"   {LocalizationService.Get("Langue")} : {FriendlyTrackLanguage(track.Language, track.Description)}");
                    if (!string.IsNullOrWhiteSpace(track.Description))
                        builder.AppendLine($"   {LocalizationService.Get("Nom")}    : {track.Description.Trim()}");
                    builder.AppendLine($"   {LocalizationService.Get("Son")}    : {FriendlyChannelLayout(track.Data.Audio.Channels)}");
                }
            }

            if (subtitleTracks.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"SOUS-TITRES — {FormatTrackCount(subtitleTracks.Length)}");
                for (var index = 0; index < subtitleTracks.Length; index++)
                {
                    var track = subtitleTracks[index];
                    builder.AppendLine($"{index + 1}.");
                    builder.AppendLine($"   {LocalizationService.Get("Langue")} : {FriendlyTrackLanguage(track.Language, track.Description)}");
                    if (!string.IsNullOrWhiteSpace(track.Description))
                        builder.AppendLine($"   {LocalizationService.Get("Nom")}    : {track.Description.Trim()}");
                }
            }

            if (chapterCount > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"CHAPITRES — {FormatTrackCount(chapterCount)}");
                for (var index = 0; index < chapterCount; index++)
                {
                    var chapter = index < _chapterMarkers.Count ? _chapterMarkers[index] : null;
                    builder.AppendLine($"{index + 1}.");
                    builder.AppendLine($"   {LocalizationService.Get("Titre")} : {chapter?.Name ?? $"{LocalizationService.Get("Chapitre")} {index + 1}"}");
                    if (chapter is not null)
                        builder.AppendLine($"   {LocalizationService.Get("Début")} : {FormatTime(chapter.TimeOffset)}");
                }
            }
        }
        catch (Exception exception) when (exception is MpvException or InvalidOperationException)
        {
            builder.AppendLine($"{LocalizationService.Get("Détails")}      : {LocalizationService.Get("En cours de lecture")}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string CondenseMediaInformation(string detailedInformation)
    {
        var sections = new Dictionary<string, (string Header, List<string> Lines)>(
            StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        foreach (var rawLine in detailedInformation.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = rawLine.Trim();
            var key = CompactSectionKey(trimmed);
            if (key is not null)
            {
                currentKey = key;
                sections[key] = (trimmed, []);
            }
            else if (currentKey is not null)
            {
                sections[currentKey].Lines.Add(rawLine);
            }
        }

        var builder = new StringBuilder();
        if (sections.TryGetValue("general", out var general))
        {
            builder.AppendLine("GÉNÉRAL");
            AppendCompactField(builder, general.Lines, "Titre");
            AppendCompactField(builder, general.Lines, "Conteneur", "Format");
            AppendCompactField(builder, general.Lines, "Taille");
            AppendCompactField(builder, general.Lines, "Durée");
            AppendCompactField(builder, general.Lines, "Vidéo");
            AppendCompactField(builder, general.Lines, "Audio");
            AppendCompactField(builder, general.Lines, "Sous-titres");
            AppendCompactField(builder, general.Lines, "Chapitres");
        }

        AppendCompactTrackSection(builder, sections, "video",
            ["Codec", "Titre", "Résolution", "Par défaut", "Forcée"]);
        AppendCompactTrackSection(builder, sections, "audio",
            ["Codec", "Titre", "Langue", "Par défaut", "Forcée", "Canaux"]);
        AppendCompactTrackSection(builder, sections, "subtitles",
            ["Codec", "Titre", "Langue", "Par défaut", "Forcée"]);
        AppendCompactTrackSection(builder, sections, "chapters", ["Titre", "De"]);
        return builder.ToString().TrimEnd();
    }

    private static string? CompactSectionKey(string line)
    {
        if (line.Equals("GÉNÉRAL", StringComparison.OrdinalIgnoreCase))
            return "general";
        var category = line.Split('—', 2)[0].Trim();
        return category.ToUpperInvariant() switch
        {
            "VIDÉO" => "video",
            "AUDIO" => "audio",
            "SOUS-TITRES" => "subtitles",
            "CHAPITRES" => "chapters",
            "BALISES" => "tags",
            _ => null
        };
    }

    private static void AppendCompactTrackSection(StringBuilder builder,
        IReadOnlyDictionary<string, (string Header, List<string> Lines)> sections,
        string key, string[] fields)
    {
        if (!sections.TryGetValue(key, out var section))
            return;

        var items = SplitInformationItems(section.Lines);
        if (items.Count == 0)
            return;

        builder.AppendLine();
        builder.AppendLine(section.Header);
        foreach (var item in items)
        {
            builder.AppendLine($"{item.Number}.");
            foreach (var field in fields)
                AppendCompactField(builder, item.Lines, field, indentation: "   ");
        }
    }

    private static List<(int Number, List<string> Lines)> SplitInformationItems(
        IEnumerable<string> lines)
    {
        var items = new List<(int Number, List<string> Lines)>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith('.') && int.TryParse(trimmed[..^1], out var number))
                items.Add((number, []));
            else if (items.Count > 0)
                items[^1].Lines.Add(line);
        }
        return items;
    }

    private static void AppendCompactField(StringBuilder builder, IEnumerable<string> lines,
        string sourceLabel, string? displayedLabel = null, string indentation = "")
    {
        var localizedSourceLabel = LocalizationService.Get(sourceLabel);
        var outputLabel = LocalizationService.Get(displayedLabel ?? sourceLabel);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon < 0)
                continue;

            var actualLabel = trimmed[..colon].Trim();
            if (!actualLabel.Equals(sourceLabel, StringComparison.OrdinalIgnoreCase) &&
                !actualLabel.Equals(localizedSourceLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            builder.AppendLine($"{indentation}{outputLabel} : {trimmed[(colon + 1)..].TrimStart()}");
            return;
        }
    }

    private int CurrentChapterCount()
    {
        var count = _chapterMarkers.Count;
        try
        {
            count = Math.Max(count, _mediaPlayer.ChapterCount);
        }
        catch (MpvException)
        {
            // Les marqueurs déjà chargés restent la meilleure valeur disponible.
        }
        return count;
    }

    private static string FormatTrackCount(int count) =>
        $"{count} {LocalizationService.Get(count > 1 ? "pistes" : "piste")}";

    private static string FriendlyChannelLayout(uint channels) => channels switch
    {
        1 => "Mono",
        2 => LocalizationService.Get("Stéréo"),
        6 => "5.1",
        8 => "7.1",
        0 => LocalizationService.Get("Non indiqué"),
        _ => $"{channels} {LocalizationService.Get("Canaux").ToLowerInvariant()}"
    };

    private static string FriendlyTrackLanguage(string? language, string? name)
    {
        var normalizedName = name ?? string.Empty;
        if (normalizedName.Contains("VFQ", StringComparison.OrdinalIgnoreCase))
            return $"{LocalizationService.Get("Français — Canada")} (fr-CA)";
        if (normalizedName.Contains("VFF", StringComparison.OrdinalIgnoreCase))
            return $"{LocalizationService.Get("Français — France")} (fr-FR)";

        var normalized = language?.Trim().Replace('_', '-').ToLowerInvariant();
        return normalized switch
        {
            "fr-ca" => $"{LocalizationService.Get("Français — Canada")} (fr-CA)",
            "fr-fr" => $"{LocalizationService.Get("Français — France")} (fr-FR)",
            "fre" or "fra" or "fr" or "french" => LocalizationService.Get("Français"),
            "en-us" => $"{LocalizationService.Get("Anglais — États-Unis")} (en-US)",
            "en-ca" => $"{LocalizationService.Get("Anglais — Canada")} (en-CA)",
            "eng" or "en" or "english" => LocalizationService.Get("Anglais"),
            "jpn" or "ja" or "japanese" => LocalizationService.Get("Japonais"),
            "spa" or "es" or "spanish" => LocalizationService.Get("Espagnol"),
            "deu" or "ger" or "de" or "german" => LocalizationService.Get("Allemand"),
            "ita" or "it" or "italian" => LocalizationService.Get("Italien"),
            "por" or "pt" or "portuguese" => LocalizationService.Get("Portugais"),
            null or "" or "und" => LocalizationService.Get("Non indiquée"),
            _ => language!.Trim()
        };
    }

    private string BuildMediaInformation(PlaylistItem item)
    {
        var currentMedia = _currentMedia;
        if (currentMedia is null)
            return LocalizationService.Get("Les informations du média ne sont plus disponibles.");

        var builder = new StringBuilder();
        builder.AppendLine($"{LocalizationService.Get("Emplacement")} : {item.Location}");
        builder.AppendLine($"{LocalizationService.Get("Format")}      : {GetMediaFormat(item)}");
        if (!item.IsNetwork && File.Exists(item.Location))
        {
            var size = new FileInfo(item.Location).Length;
            builder.AppendLine($"{LocalizationService.Get("Taille")}      : {FormatFileSize(size)}");
        }

        builder.AppendLine($"{LocalizationService.Get("Durée")}       : {FormatTime(Math.Max(0, _mediaPlayer.Length))}");
        try
        {
            var tracks = currentMedia.Tracks;
            var fallbackVideoTracks = tracks.Where(track => track.TrackType == MpvTrackType.Video).ToArray();
            var fallbackAudioTracks = tracks.Where(track => track.TrackType == MpvTrackType.Audio).ToArray();
            var fallbackSubtitleTracks = tracks.Where(track => track.TrackType == MpvTrackType.Text).ToArray();
            builder.AppendLine($"{LocalizationService.Get("Chapitres")}   : {FormatTrackCount(CurrentChapterCount())}");
            builder.AppendLine($"{LocalizationService.Get("Vidéo")}       : {FormatTrackCount(fallbackVideoTracks.Length)}");
            builder.AppendLine($"{LocalizationService.Get("Audio")}       : {FormatTrackCount(fallbackAudioTracks.Length)}");
            builder.AppendLine($"{LocalizationService.Get("Sous-titres")} : {FormatTrackCount(fallbackSubtitleTracks.Length)}");
            var videoTracks = tracks.Where(track => track.TrackType == MpvTrackType.Video).ToArray();
            if (videoTracks.Length > 0)
            {
                var video = videoTracks[0];
                var codec = currentMedia.CodecDescription(video.TrackType, video.Codec);
                var width = video.Data.Video.Width;
                var height = video.Data.Video.Height;
                var frameRate = video.Data.Video.FrameRateDen > 0
                    ? video.Data.Video.FrameRateNum / (double)video.Data.Video.FrameRateDen
                    : 0;
                builder.AppendLine();
                builder.AppendLine("VIDÉO");
                builder.AppendLine($"{LocalizationService.Get("Résolution")}   : {width} × {height}");
                builder.AppendLine($"{LocalizationService.Get("Codec")}        : {codec}");
                if (video.Bitrate > 0)
                    builder.AppendLine($"{LocalizationService.Get("Débit")}        : {video.Bitrate / 1000d:0} kb/s");
                if (frameRate > 0)
                    builder.AppendLine($"{LocalizationService.Get("Images/s")}     : {frameRate:0.###}");
            }

            var audioTracks = tracks.Where(track => track.TrackType == MpvTrackType.Audio).ToArray();
            if (audioTracks.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("AUDIO");
                for (var index = 0; index < audioTracks.Length; index++)
                {
                    var track = audioTracks[index];
                    var codec = currentMedia.CodecDescription(track.TrackType, track.Codec);
                    var language = FriendlyTrackLanguage(track.Language, track.Description);
                    var channels = FriendlyChannelLayout(track.Data.Audio.Channels);
                    builder.AppendLine($"{index + 1}. {codec} · {language} · {channels}");
                }
            }

            var subtitles = tracks.Where(track => track.TrackType == MpvTrackType.Text).ToArray();
            if (subtitles.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("SOUS-TITRES");
                for (var index = 0; index < subtitles.Length; index++)
                {
                    var track = subtitles[index];
                    var language = FriendlyTrackLanguage(track.Language, track.Description);
                    builder.AppendLine($"{index + 1}. {language}");
                }
            }
        }
        catch (Exception exception) when (exception is MpvException or InvalidOperationException)
        {
            builder.AppendLine();
            builder.AppendLine(LocalizationService.Get("Les détails des codecs ne sont pas encore disponibles."));
        }

        return builder.ToString().TrimEnd();
    }

    private static string GetMediaFormat(PlaylistItem item) => item.IsNetwork
        ? LocalizationService.Get("Flux réseau")
        : Path.GetExtension(item.Location).TrimStart('.').ToUpperInvariant() switch
        {
            "" => LocalizationService.Get("Média"),
            var extension => extension
        };

    private static string FormatFileSize(long bytes)
    {
        string[] units = LocalizationService.CurrentLanguage == "en"
            ? ["B", "KB", "MB", "GB", "TB"]
            : ["o", "Ko", "Mo", "Go", "To"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private void OpenLocations(IEnumerable<string> locations)
    {
        var usableLocations = locations
            .Select(value => value.Trim())
            .Where(IsUsableMediaLocation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (usableLocations.Length == 0)
        {
            ShowToast("Aucun média reconnu");
            return;
        }

        var localFiles = usableLocations.All(File.Exists);
        var sameDirectory = localFiles && usableLocations.Length > 0 &&
            usableLocations
                .Select(path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1;
        if (sameDirectory)
        {
            usableLocations = usableLocations
                .OrderBy(path => path, NaturalPathComparer.Instance)
                .ToArray();
        }

        _enhancedPlaybackEligible = _enhancedPlaybackEnabled && localFiles && sameDirectory &&
            usableLocations.All(path => EnhancedVideoExtensions.Contains(Path.GetExtension(path)));
        ResetEnhancedPlaybackState();
        _enhancedFolderTitleLocation = null;

        _playlistSearchQuery = string.Empty;
        PlaylistSearchTextBox.Clear();
        Playlist.Clear();
        _shufflePlayedIndices.Clear();
        foreach (var location in usableLocations)
        {
            var item = PlaylistItem.FromLocation(location);
            item.DisplayFolderDepth = _playlistFolderDepth;
            Playlist.Add(item);
        }

        RefreshPlaylistCount();
        var canResume = _resumePlayback && usableLocations.Length == 1 &&
            !string.IsNullOrWhiteSpace(_lastMediaLocation) &&
            string.Equals(usableLocations[0], _lastMediaLocation, StringComparison.OrdinalIgnoreCase) &&
            _lastMediaPositionMilliseconds > 1000;
        _pendingResumePositionMilliseconds = 0;
        _pendingResumePromptPositionMilliseconds = canResume
            ? Math.Max(0, _lastMediaPositionMilliseconds)
            : 0;
        _pendingResumePromptLocation = canResume ? usableLocations[0] : null;
        _pauseAfterOpeningForResumePrompt = canResume;
        PlayAt(0, canResume || _autoPlayOnOpen);
        if (_showEnhancedUpcomingInPlaylist)
            EnsureEnhancedNextQueued();
    }

    private bool AskToResumeMedia(string location, long positionMilliseconds)
    {
        var title = Path.GetFileNameWithoutExtension(location);
        if (string.IsNullOrWhiteSpace(title))
            title = "ce média";

        var dialog = new ResumePlaybackDialog(title, positionMilliseconds);
        // L'overlay vidéo est une fenêtre native auxiliaire qui peut être
        // cloquée ou neutralisée pendant le démarrage. Il ne doit jamais être
        // le propriétaire de la question de reprise: après sa fermeture, WPF
        // pourrait lui rendre le focus et laisser la couche vidéo intercepter
        // les clics. La fenêtre principale est le propriétaire stable lorsqu'il
        // est possible d'en définir un; avant son affichage, aucun propriétaire
        // n'est assigné (WPF lève une exception pour une fenêtre non affichée).
        var owner = IsLoaded && IsVisible ? this : null;
        if (owner is not null)
            dialog.Owner = owner;
        var result = ShowModalDialog(() => dialog.ShowDialog()) == true && dialog.Resume;

        // Une boîte modale ouverte au milieu de l'événement Playing peut
        // terminer avant le prochain passage de composition. Réactiver ici les
        // fenêtres et le hit-test évite de conserver un focus modal fantôme.
        if (!_isClosing)
        {
            IsEnabled = true;
            if (_videoOverlayWindow is not null)
                _videoOverlayWindow.IsEnabled = true;
            UpdateVideoOverlayPresentationState();
            Activate();
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || _currentMedia is null)
                    return;

                if (_videoSurfaceReady)
                    RevealOverlayAfterStartup();
                else
                    ScheduleVideoSurfaceReveal(force: true);
            }, DispatcherPriority.Render);
        }

        return result;
    }

    private bool AskToConfirmClose()
    {
        var dialog = new ConfirmCloseDialog
        {
            Owner = DialogOwnerWindow
        };
        return ShowModalDialog(() => dialog.ShowDialog()) == true && dialog.Confirmed;
    }

    private bool ShouldSkipResumePrompt(long positionMilliseconds)
    {
        var durationMilliseconds = _mediaPlayer.Length;
        if (durationMilliseconds <= 0)
            return false;

        var positionPercent = Math.Clamp(
            positionMilliseconds / (double)durationMilliseconds * 100d, 0d, 100d);
        return positionPercent <= Math.Clamp(_resumePromptStartSkipPercent, 0, 100) ||
               positionPercent >= 100d - Math.Clamp(_resumePromptEndSkipPercent, 0, 100);
    }

    private static bool IsUsableMediaLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return false;
        if (File.Exists(location))
            return true;
        return Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile;
    }

    private void ResetEnhancedPlaybackState()
    {
        _enhancedPreloadStarted = false;
        _enhancedNextLocation = null;
        unchecked
        {
            _enhancedPlaybackGeneration++;
        }
    }

    private void RefreshEnhancedPlaybackEligibility()
    {
        var locations = Playlist.Select(item => item.Location).ToArray();
        var localFiles = locations.Length > 0 && locations.All(File.Exists);
        var sameDirectory = localFiles && locations
            .Select(path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == 1;
        _enhancedPlaybackEligible = _enhancedPlaybackEnabled && localFiles && sameDirectory &&
            locations.All(path => EnhancedVideoExtensions.Contains(Path.GetExtension(path)));
        ResetEnhancedPlaybackStateForCurrentMedia();
    }

    private void ResetEnhancedPlaybackStateForCurrentMedia()
    {
        ResetEnhancedPlaybackState();
        if (CanUseEnhancedCurrentMedia())
            _enhancedNextLocation = ResolveEnhancedNextLocation();
    }

    private bool CanUseEnhancedCurrentMedia()
    {
        if (!_enhancedPlaybackEnabled || _currentIndex < 0 ||
            _currentIndex >= Playlist.Count)
            return false;

        var location = Playlist[_currentIndex].Location;
        return File.Exists(location) &&
               EnhancedVideoExtensions.Contains(Path.GetExtension(location));
    }

    private string? ResolveEnhancedNextLocation()
    {
        if (!CanUseEnhancedCurrentMedia())
            return null;

        var upcoming = ResolveEnhancedUpcomingLocations();
        if (upcoming.Length > 0)
            return upcoming[0];

        if (_currentIndex + 1 < Playlist.Count)
        {
            var queued = Playlist[_currentIndex + 1].Location;
            if (File.Exists(queued))
                return queued;
        }

        return null;
    }

    private string[] ResolveEnhancedUpcomingLocations()
    {
        if (!CanUseEnhancedCurrentMedia())
            return [];

        try
        {
            var currentLocation = Path.GetFullPath(Playlist[_currentIndex].Location);
            var directory = Path.GetDirectoryName(currentLocation);
            if (string.IsNullOrWhiteSpace(directory))
                return [];

            var files = GetEnhancedVideoFiles(directory, SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .ToArray();
            var currentFileIndex = Array.FindIndex(files, file =>
                string.Equals(file, currentLocation, StringComparison.OrdinalIgnoreCase));
            if (currentFileIndex < 0)
                return [];

            var upcoming = files
                .Skip(currentFileIndex + 1)
                .ToArray();
            if (_showEnhancedNextFolderInPlaylist && _enhancedFolderAdvanceEnabled)
                upcoming = upcoming
                    .Concat(ResolveEnhancedFolderPreviewLocations())
                    .ToArray();
            return upcoming;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private string[] ResolveEnhancedFolderPreviewLocations()
    {
        if (_currentIndex < 0 || _currentIndex >= Playlist.Count)
            return [];

        try
        {
            var currentLocation = Path.GetFullPath(Playlist[_currentIndex].Location);
            var currentDirectory = Path.GetDirectoryName(currentLocation);
            var parentDirectory = string.IsNullOrWhiteSpace(currentDirectory)
                ? null
                : Directory.GetParent(currentDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(currentDirectory) || string.IsNullOrWhiteSpace(parentDirectory))
                return [];

            var siblingDirectories = Directory.EnumerateDirectories(parentDirectory)
                .OrderBy(directory => directory, NaturalPathComparer.Instance)
                .ToArray();
            var currentDirectoryIndex = Array.FindIndex(siblingDirectories, directory =>
                string.Equals(Path.GetFullPath(directory), Path.GetFullPath(currentDirectory),
                    StringComparison.OrdinalIgnoreCase));
            if (currentDirectoryIndex < 0)
                return [];

            for (var index = currentDirectoryIndex + 1; index < siblingDirectories.Length; index++)
            {
                var nextFiles = GetEnhancedVideoFiles(siblingDirectories[index], SearchOption.AllDirectories);
                if (nextFiles.Length > 0)
                    return nextFiles.Select(Path.GetFullPath).ToArray();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            // L’aperçu du dossier suivant est facultatif et ne doit jamais
            // interrompre la lecture si un dossier est inaccessible.
        }

        return [];
    }

    private static string? FindNextSiblingLocation(string location)
    {
        if (!File.Exists(location))
            return null;

        try
        {
            var fullLocation = Path.GetFullPath(location);
            var directory = Path.GetDirectoryName(fullLocation);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;

            var files = GetEnhancedVideoFiles(directory);
            var currentIndex = Array.FindIndex(files, file =>
                string.Equals(Path.GetFullPath(file), fullLocation,
                    StringComparison.OrdinalIgnoreCase));
            return currentIndex >= 0 && currentIndex + 1 < files.Length
                ? files[currentIndex + 1]
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string[] GetEnhancedVideoFiles(string directory,
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", searchOption)
                .Where(file => EnhancedVideoExtensions.Contains(Path.GetExtension(file)))
                .OrderBy(file => file, NaturalPathComparer.Instance)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private string? ResolveEnhancedFolderNextLocation()
    {
        if (!_enhancedFolderAdvanceEnabled ||
            _currentIndex < 0 || _currentIndex >= Playlist.Count)
            return null;

        try
        {
            var currentLocation = Path.GetFullPath(Playlist[_currentIndex].Location);
            var currentDirectory = Path.GetDirectoryName(currentLocation);
            if (string.IsNullOrWhiteSpace(currentDirectory))
                return null;

            var currentFiles = GetEnhancedVideoFiles(currentDirectory, SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .ToArray();
            var currentFileIndex = Array.FindIndex(currentFiles, file =>
                string.Equals(file, currentLocation, StringComparison.OrdinalIgnoreCase));
            if (currentFiles.Length == 0 || currentFileIndex != currentFiles.Length - 1)
                return null;

            var parentDirectory = Directory.GetParent(currentDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(parentDirectory))
                return null;

            var siblingDirectories = Directory.EnumerateDirectories(parentDirectory)
                .OrderBy(directory => directory, NaturalPathComparer.Instance)
                .ToArray();
            var currentDirectoryIndex = Array.FindIndex(siblingDirectories, directory =>
                string.Equals(Path.GetFullPath(directory), Path.GetFullPath(currentDirectory),
                    StringComparison.OrdinalIgnoreCase));
            if (currentDirectoryIndex < 0)
                return null;

            for (var index = currentDirectoryIndex + 1; index < siblingDirectories.Length; index++)
            {
                var nextFiles = GetEnhancedVideoFiles(siblingDirectories[index], SearchOption.AllDirectories);
                if (nextFiles.Length > 0)
                    return nextFiles[0];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            // La navigation entre dossiers est optionnelle et ne doit jamais
            // interrompre la lecture si un dossier est inaccessible.
        }

        return null;
    }

    private void EnsureEnhancedNextQueued()
    {
        if (!CanUseEnhancedCurrentMedia())
            return;

        var upcoming = ResolveEnhancedUpcomingLocations();
        if (upcoming.Length == 0)
        {
            var fallback = ResolveEnhancedNextLocation();
            if (!string.IsNullOrWhiteSpace(fallback))
                upcoming = [fallback];
        }

        if (upcoming.Length == 0)
            return;

        _enhancedNextLocation = upcoming[0];
        var locationsToQueue = _showEnhancedUpcomingInPlaylist
            ? upcoming
            : upcoming.Take(1).ToArray();
        var inserted = false;
        var currentDirectory = Path.GetDirectoryName(
            Path.GetFullPath(Playlist[_currentIndex].Location));

        var currentFolderLocations = locationsToQueue
            .Where(location => !IsDifferentDirectory(location, currentDirectory))
            .ToArray();
        var nextFolderLocations = locationsToQueue
            .Where(location => IsDifferentDirectory(location, currentDirectory))
            .ToArray();

        var insertIndex = FindEnhancedFolderInsertionIndex(currentDirectory);
        foreach (var next in currentFolderLocations)
        {
            var existing = Playlist.FirstOrDefault(item =>
                string.Equals(item.Location, next, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                continue;

            var item = PlaylistItem.FromLocation(next);
            item.DisplayFolderDepth = _playlistFolderDepth;
            item.IsEnhancedQueued = true;
            Playlist.Insert(insertIndex++, item);
            inserted = true;
        }

        // Recalculate the insertion point after the current-folder items were
        // added, so a newly enabled next-folder option cannot jump ahead of
        // the remaining episodes of the current folder.
        insertIndex = FindEnhancedFolderInsertionIndex(currentDirectory);
        var folderBoundaryMarked = false;
        foreach (var next in nextFolderLocations)
        {
            var existing = Playlist.FirstOrDefault(item =>
                string.Equals(item.Location, next, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.IsManualQueueItem)
                    continue;
                existing.IsEnhancedQueued = true;
                if (!folderBoundaryMarked && IsDifferentDirectory(existing.Location, currentDirectory))
                {
                    MarkEnhancedFolderStart(existing, currentDirectory);
                    folderBoundaryMarked = true;
                }
                continue;
            }

            var item = PlaylistItem.FromLocation(next);
            item.DisplayFolderDepth = _playlistFolderDepth;
            item.IsEnhancedQueued = true;
            if (!folderBoundaryMarked && IsDifferentDirectory(next, currentDirectory))
            {
                MarkEnhancedFolderStart(item, currentDirectory);
                folderBoundaryMarked = true;
            }
            Playlist.Insert(insertIndex++, item);
            inserted = true;
        }

        if (inserted)
        {
            RefreshPlaylistCount();
            SelectCurrentPlaylistItem();
        }
    }

    private int FindEnhancedFolderInsertionIndex(string? currentDirectory)
    {
        var insertIndex = Math.Min(_currentIndex + 1, Playlist.Count);
        while (insertIndex < Playlist.Count &&
               !IsDifferentDirectory(Playlist[insertIndex].Location, currentDirectory))
        {
            insertIndex++;
        }

        return insertIndex;
    }

    private static bool IsDifferentDirectory(string location, string? referenceDirectory) =>
        !string.IsNullOrWhiteSpace(GetEnhancedFolderRoot(location, referenceDirectory)) &&
        !string.Equals(GetEnhancedFolderRoot(location, referenceDirectory),
            Path.GetFullPath(referenceDirectory!), StringComparison.OrdinalIgnoreCase);

    private void RemoveEnhancedNextFolderItems()
    {
        if (_currentIndex < 0 || _currentIndex >= Playlist.Count)
            return;

        var currentDirectory = Path.GetDirectoryName(
            Path.GetFullPath(Playlist[_currentIndex].Location));
        var changed = false;
        for (var index = Playlist.Count - 1; index >= 0; index--)
        {
            if (index == _currentIndex || !Playlist[index].IsEnhancedQueued ||
                !IsDifferentDirectory(Playlist[index].Location, currentDirectory))
                continue;

            Playlist.RemoveAt(index);
            if (index < _currentIndex)
                _currentIndex--;
            changed = true;
        }

        if (changed)
        {
            RefreshPlaylistCount();
            SelectCurrentPlaylistItem();
        }
    }

    private static string? GetEnhancedFolderRoot(string location, string? referenceDirectory)
    {
        if (string.IsNullOrWhiteSpace(referenceDirectory) || !File.Exists(location))
            return null;

        var currentDirectory = Path.GetFullPath(referenceDirectory);
        var parentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        var nextDirectory = Path.GetDirectoryName(Path.GetFullPath(location));
        if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(nextDirectory))
            return null;

        var folder = nextDirectory;
        while (true)
        {
            var parent = Directory.GetParent(folder)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                return null;
            if (string.Equals(parent, parentDirectory, StringComparison.OrdinalIgnoreCase))
                return folder;
            folder = parent;
        }
    }

    private static void MarkEnhancedFolderStart(PlaylistItem item, string? referenceDirectory)
    {
        var directory = GetEnhancedFolderRoot(item.Location, referenceDirectory) ??
                        Path.GetDirectoryName(item.Location);
        var title = string.IsNullOrWhiteSpace(directory)
            ? "Dossier suivant"
            : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        item.EnhancedFolderTitle = string.IsNullOrWhiteSpace(title)
            ? "Dossier suivant"
            : title;
        item.IsEnhancedFolderStart = true;
    }

    private void EnsureEnhancedFolderNextQueued()
    {
        var next = ResolveEnhancedFolderNextLocation();
        if (string.IsNullOrWhiteSpace(next))
            return;

        _enhancedNextLocation = next;
        if (_enhancedFolderShowNameEnabled)
            _enhancedFolderTitleLocation = next;
        if (Playlist.Any(item => string.Equals(item.Location, next,
                StringComparison.OrdinalIgnoreCase)))
            return;

        var insertIndex = Math.Min(_currentIndex + 1, Playlist.Count);
        var item = PlaylistItem.FromLocation(next);
        item.DisplayFolderDepth = _playlistFolderDepth;
        item.IsEnhancedQueued = true;
        Playlist.Insert(insertIndex, item);
        RefreshPlaylistCount();
        SelectCurrentPlaylistItem();
    }

    private void StartEnhancedPreload()
    {
        if (_enhancedPreloadStarted)
            return;

        _enhancedPreloadStarted = true;
        EnsureEnhancedNextQueued();
        if (_enhancedNextLocation is null)
            EnsureEnhancedFolderNextQueued();
        var next = _enhancedNextLocation;
        if (string.IsNullOrWhiteSpace(next) || !File.Exists(next))
            return;

        var generation = _enhancedPlaybackGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                await WarmEnhancedFileAsync(next).ConfigureAwait(false);
                if (generation == _enhancedPlaybackGeneration)
                    await _mediaProbeService.BuildInformationAsync(next).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidOperationException or OperationCanceledException)
            {
                // Le préchauffage est opportuniste : une erreur ne doit jamais
                // interrompre la lecture en cours.
            }
        });
    }

    private static async Task WarmEnhancedFileAsync(string location)
    {
        const int warmupBytes = 4 * 1024 * 1024;
        const int bufferSize = 1024 * 1024;
        await using var stream = new FileStream(location, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[bufferSize];
        var total = 0;
        while (total < warmupBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, warmupBytes - total))).ConfigureAwait(false);
            if (read <= 0)
                break;
            total += read;
        }
    }

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var left = Path.GetFileName(x);
            var right = Path.GetFileName(y);
            var result = CompareNatural(left, right);
            return result != 0 ? result :
                StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }

        private static int CompareNatural(string left, string right)
        {
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftDigit = char.IsDigit(left[leftIndex]);
                var rightDigit = char.IsDigit(right[rightIndex]);
                if (leftDigit && rightDigit)
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                        leftIndex++;
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                        rightIndex++;

                    var leftDigits = left[leftStart..leftIndex].TrimStart('0');
                    var rightDigits = right[rightStart..rightIndex].TrimStart('0');
                    if (leftDigits.Length != rightDigits.Length)
                        return leftDigits.Length.CompareTo(rightDigits.Length);
                    var numericResult = string.Compare(leftDigits, rightDigits,
                        StringComparison.Ordinal);
                    if (numericResult != 0)
                        return numericResult;
                    continue;
                }

                var result = char.ToUpperInvariant(left[leftIndex])
                    .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (result != 0)
                    return result;
                leftIndex++;
                rightIndex++;
            }

            return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        }
    }

    private void AddRecentMedia(string location)
    {
        _recentMedia.RemoveAll(item =>
            string.Equals(item, location, StringComparison.OrdinalIgnoreCase));
        _recentMedia.Insert(0, location);
        _recentMediaLastOpenedUtc[location] = DateTime.UtcNow;

        if (_recentMedia.Count > 10)
            _recentMedia.RemoveRange(10, _recentMedia.Count - 10);
        PruneRecentMediaByRetention();
        RefreshRecentMediaMenu();
        PersistSession();
    }

    private void PruneRecentMediaByRetention()
    {
        if (_recentMediaRetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_recentMediaRetentionDays);
            _recentMedia.RemoveAll(location =>
                _recentMediaLastOpenedUtc.TryGetValue(location, out var openedUtc) &&
                openedUtc < cutoff);
        }

        var retained = _recentMedia.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var location in _recentMediaLastOpenedUtc.Keys
                     .Where(location => !retained.Contains(location)).ToArray())
            _recentMediaLastOpenedUtc.Remove(location);
    }

    private void PruneMediaPlaybackPreferences()
    {
        if (_recentMediaRetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_recentMediaRetentionDays);
            foreach (var location in _mediaPlaybackPreferences
                         .Where(pair => pair.Value.UpdatedUtc < cutoff)
                         .Select(pair => pair.Key).ToArray())
                _mediaPlaybackPreferences.Remove(location);
        }

        foreach (var location in _mediaPlaybackPreferences
                     .OrderByDescending(pair => pair.Value.UpdatedUtc)
                     .Skip(500)
                     .Select(pair => pair.Key).ToArray())
            _mediaPlaybackPreferences.Remove(location);
    }

    private void PlayAt(int index, bool startPlayback = true, bool preserveWindowPresentation = false)
    {
        if (index < 0 || index >= Playlist.Count || _isClosing)
            return;

        Interlocked.Increment(ref _mediaInformationPreloadGeneration);
        SaveCurrentMediaPlaybackPreferences();

        // Un média ouvert par association de fichier peut transmettre le clic
        // de lancement au nouvel overlay. Ne jamais laisser ce clic devenir le
        // premier terme d'un double-clic dans le lecteur.
        _videoClickTimer.Stop();
        _lastVideoClickTick = -1;
        _preserveWindowPresentationForCurrentMedia = preserveWindowPresentation;
        _seekCommitTimer.Stop();
        _pendingSeekTarget = null;
        ResetTrackSynchronizationForMediaChange();
        ResetVideoPresentationForMediaChange();
        ResetChapterMarkers();
        _currentIndex = index;
        ResetEnhancedPlaybackStateForCurrentMedia();
        if (_shufflePlayback)
            _shufflePlayedIndices.Add(index);
        // mpv synchronise déjà le premier son avec la première image. Le
        // charger en pause derrière un écran opaque ajoutait un délai visible.
        _startupPlaybackGatePending = false;
        _startupPlaybackTraceStartedAt = Stopwatch.GetTimestamp();
        TraceStartupPlayback("ouverture demandée");
        _resumePlaybackAfterSurfaceReveal = false;
        _videoSurfaceReady = false;
        _startupPlaybackPendingMedia = null;
        _startupPlaybackWatchdogTimer.Stop();
        _playbackRestartedForCurrentMedia = false;
        _pendingPlaybackAfterWindowTransition = false;
        var requestStartupFullscreen = startPlayback && !preserveWindowPresentation &&
            _startVideoFullscreen && !_isFullscreen && !_fullscreenTransitionInProgress;
        _videoSurfaceRevealTimer.Stop();
        // Laisser le HWND vidéo présenté pendant le chargement permet à mpv
        // de rendre réellement sa première image en pause. La surface Fuse
        // opaque de la fenêtre d'overlay la couvre jusqu'à ce que cette image
        // soit prête, donc aucun noir natif n'est exposé.
        VideoView.Visibility = Visibility.Visible;
        if (VideoView.NativeHandle != IntPtr.Zero)
            ShowWindow(VideoView.NativeHandle, SwShowNoActivate);
        HideStartupLoadingOverlay();
        AttachVideoOutput();

        // La couche de commandes est déjà visible après l'affichage de la
        // fenêtre. Replier l'interface avant de charger le nouveau média
        // empêche la barre inférieure de se retrouver brièvement au centre
        // pendant que libmpv prépare sa première image. Un changement de
        // média qui conserve la présentation de la fenêtre garde, lui, son
        // état actuel.
        if (startPlayback && !preserveWindowPresentation)
        {
            ApplyVideoStartInterfacePreference();
            HidePlaybackControlsImmediately();
            _videoOverlayHiddenForStartup = true;
            UpdateVideoOverlayPresentationState();

        }

        var item = Playlist[index];
        if (!string.Equals(_enhancedFolderTitleLocation, item.Location,
                StringComparison.OrdinalIgnoreCase))
            _enhancedFolderTitleLocation = null;
        _lastMediaLocation = item.Location;
        _lastMediaPositionMilliseconds = 0;
        SelectCurrentPlaylistItem();

        _mediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = new MpvMedia(item.Location);
        _mediaPlayer.Media = _currentMedia;
        _preferredAudioAppliedForCurrentMedia = false;
        _preferredSubtitleAppliedForCurrentMedia = false;
        _videoStartupPresentationAppliedForCurrentMedia = false;
        _fixedVideoPresentationAppliedForCurrentMedia = false;
        _startupTitleShownForCurrentMedia = false;
        _pendingStartupTitle = item.Title;
        _startupTitleTimer.Stop();
        _requestedVideoTrackId = null;
        _requestedAudioTrackId = null;
        _requestedSubtitleTrackId = null;
        PrepareRememberedMediaPlaybackPreferences(item.Location);
        if (_disableAudioByDefault)
            _mediaPlayer.SetAudioTrack(-1);
        if (_disableSubtitlesByDefault)
            _mediaPlayer.SetSpu(-1);
        _isMuted = false;
        _mediaPlayer.Mute = false;
        _mediaPlayer.Volume = GetEngineVolume((int)Math.Round(VolumeSlider.Value));
        UpdateMuteVisual();
        AudioTrackCountText.Text = "0/0";
        SubtitleTrackCountText.Text = "0/0";

        NowPlayingTitle.Text = item.Title;
        NowPlayingDetail.Text = item.IsNetwork
            ? LocalizationService.Get("• FLUX RÉSEAU")
            : string.Empty;
        Title = $"Fuze — {item.Title}";
        WindowTitleText.Text = $"Fuze — {item.Title}";
        EmptyState.Visibility = Visibility.Collapsed;
        SeekSlider.Value = 0;
        UpdateTimelineText(0, item.DurationMilliseconds);

        if (requestStartupFullscreen)
        {
            // La surface de chargement opaque vit maintenant dans le HWND
            // d'overlay et couvre réellement mpv. On peut donc préparer la
            // géométrie plein écran avant la première image sans exposer une
            // surface noire. Le passage est placé à Render afin que WPF ait
            // d'abord composé la surface Fuse.
            _videoStartupPresentationAppliedForCurrentMedia = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || _isFullscreen || _fullscreenTransitionInProgress ||
                    _currentMedia is null)
                    return;

                MoveWindowToPreferredVideoDisplay();
                ToggleFullscreen();
            }, DispatcherPriority.Render);
        }

        if (!startPlayback)
        {
            SetPlayPauseVisual(false);
            SetEngineState("PRÊT", "#FF8C929F");
            AddRecentMedia(item.Location);
            ScheduleVideoSurfaceReveal();
            return;
        }

        // Stabiliser toute la géométrie avant même d'envoyer loadfile à mpv.
        // Ainsi sa première image naît directement dans la surface définitive,
        // comme dans le lecteur mpv autonome.
        _startupPlaybackPendingMedia = _currentMedia;
        _fixedVideoPresentationAppliedForCurrentMedia = true;
        if (!requestStartupFullscreen)
            FitNormalWindowToVideoAspect();
        ApplyFixedVideoPresentation(TryStartPreparedPlayback);
        AddRecentMedia(item.Location);
    }

    private void ScheduleVideoSurfaceReveal(bool force = false)
    {
        if (_isClosing || _currentMedia is null || _videoSurfaceReady)
            return;

        // file-loaded confirme le chargement, mais PLAYBACK_RESTART est le
        // signal mpv indiquant le démarrage effectif après ce chargement.
        if (!force && _resumePlaybackAfterSurfaceReveal &&
            _mediaPlayer.VideoTrackCount > 0 &&
            !_playbackRestartedForCurrentMedia)
        {
            return;
        }

        _videoSurfaceRevealTimer.Stop();
        _videoSurfaceRevealTimer.Start();
    }

    private void TogglePlayback()
    {
        if (_currentIndex < 0)
        {
            var index = GetSelectedPlaylistIndex();
            if (index < 0)
                index = 0;
            if (index < Playlist.Count)
                PlayAt(index);
            else
                OpenFilesButton_OnClick(this, new RoutedEventArgs());
            return;
        }

        CommitPendingSeek();
        AttachVideoOutput();

        var state = _mediaPlayer.State;
        if (state is PlaybackState.Playing or PlaybackState.Buffering)
            _mediaPlayer.SetPause(true);
        else if (state == PlaybackState.Paused)
            _mediaPlayer.SetPause(false);
        else
            _mediaPlayer.Play();
    }

    private void PlayButton_OnClick(object sender, RoutedEventArgs e) => TogglePlayback();

    private void ShuffleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _shufflePlayback = !_shufflePlayback;
        _shufflePlayedIndices.Clear();
        if (_shufflePlayback && _currentIndex >= 0)
            _shufflePlayedIndices.Add(_currentIndex);
        UpdatePlaybackModeButtons();
        PersistSession();
        ShowToast(_shufflePlayback ? "Lecture aléatoire activée" : "Lecture aléatoire désactivée");
    }

    private void RepeatButton_OnClick(object sender, RoutedEventArgs e)
    {
        _repeatPlayback = !_repeatPlayback;
        UpdatePlaybackModeButtons();
        PersistSession();
        ShowToast(_repeatPlayback
            ? "Répétition du média actuel activée"
            : "Répétition du média actuel désactivée");
    }

    private void UpdatePlaybackModeButtons()
    {
        if (ShuffleButton is null || RepeatButton is null)
            return;

        ShuffleButton.Visibility = _bottomBarLayoutPreviewActive || _showShuffleButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepeatButton.Visibility = _bottomBarLayoutPreviewActive || _showRepeatButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShuffleButton.Foreground = _shufflePlayback ? new SolidColorBrush(Color.FromRgb(255, 154, 72)) :
            new SolidColorBrush(Color.FromRgb(216, 219, 223));
        RepeatButton.Foreground = _repeatPlayback ? new SolidColorBrush(Color.FromRgb(255, 154, 72)) :
            new SolidColorBrush(Color.FromRgb(216, 219, 223));
    }

    private void SetPlayPauseVisual(bool isPlaying)
    {
        PlayGlyph.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StopMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        _seekCommitTimer.Stop();
        _pendingSeekTarget = null;
        _mediaPlayer.Stop();
        SeekSlider.Value = 0;
        UpdateTimelineText(0, _mediaPlayer.Length);
    }

    private void RewindButton_OnClick(object sender, RoutedEventArgs e) =>
        SeekRelative(-_rewindSeconds * 1000L);

    private void ForwardButton_OnClick(object sender, RoutedEventArgs e) =>
        SeekRelative(_forwardSeconds * 1000L);

    private void PreviousButton_OnClick(object sender, RoutedEventArgs e) => PlayPrevious();

    private void NextButton_OnClick(object sender, RoutedEventArgs e) => PlayNext();

    private void ChaptersMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e) =>
        RefreshChaptersMenu();

    private void RefreshChaptersMenu()
    {
        ChaptersMenuItem.Items.Clear();
        if (_currentIndex < 0)
        {
            AddChapterMenuPlaceholder("Aucun média ouvert");
            return;
        }

        EnsureChapterMarkers(_mediaPlayer.Length, true);
        if (!_chapterMarkersReady)
        {
            var chapterCount = 0;
            try
            {
                chapterCount = _mediaPlayer.ChapterCount;
            }
            catch (MpvException)
            {
                // La liste sera réessayée par le chargement asynchrone existant.
            }

            AddChapterMenuPlaceholder(chapterCount > 0
                ? "Chargement des chapitres…"
                : "Aucun chapitre");
            return;
        }

        if (_chapterMarkers.Count == 0)
        {
            AddChapterMenuPlaceholder("Aucun chapitre");
            return;
        }

        var currentTime = _pendingSeekTarget ?? Math.Max(0, _mediaPlayer.Time);
        var activeChapter = _chapterMarkers.LastOrDefault(chapter =>
            chapter.TimeOffset <= currentTime + 250);
        foreach (var chapter in _chapterMarkers)
        {
            var item = new MenuItem
            {
                Header = chapter.Name,
                InputGestureText = FormatTime(chapter.TimeOffset),
                IsCheckable = true,
                IsChecked = ReferenceEquals(chapter, activeChapter),
                Tag = chapter
            };
            item.Click += ChapterMenuItem_OnClick;
            ChaptersMenuItem.Items.Add(item);
        }
    }

    private void AddChapterMenuPlaceholder(string text) =>
        ChaptersMenuItem.Items.Add(new MenuItem
        {
            Header = LocalizationService.Get(text),
            IsEnabled = false
        });

    private void ChapterMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ChapterMarkerInfo chapter })
            return;

        QueueSeek(chapter.TimeOffset, true);
        ShowToast(chapter.Name);
    }

    private void GoToTimeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0)
        {
            ShowToast("Aucun média ouvert");
            return;
        }

        CommitPendingSeek();
        var currentTime = Math.Max(0, _mediaPlayer.Time);
        var dialog = new GoToTimeDialog(currentTime, Math.Max(0, _mediaPlayer.Length))
        {
            Owner = DialogOwnerWindow
        };
        var restorePinnedToolBar = _toolBarPinnedOpen;
        if (restorePinnedToolBar)
            _toolBarHideTimer.Stop();

        bool? settingsResult;
        try
        {
            settingsResult = ShowModalDialog(dialog.ShowDialog);
        }
        finally
        {
            if (restorePinnedToolBar && !_isClosing)
            {
                _toolBarPinnedOpen = true;
                _suppressToolBarActivation = false;
                ExpandToolBar(false);
                RestartToolBarHideTimer();
            }
        }

        if (settingsResult != true)
            return;

        QueueSeek(dialog.TargetMilliseconds, true);
        ShowToast(LocalizationService.Format("Position {0}", FormatTime(dialog.TargetMilliseconds, true)));
    }

    private void PreviousChapterMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EnsureChapterMarkers(_mediaPlayer.Length, true);
        if (!TryPlayPreviousChapter())
            ShowToast("Aucun chapitre précédent");
    }

    private void NextChapterMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        EnsureChapterMarkers(_mediaPlayer.Length, true);
        if (!TryPlayNextChapter())
            ShowToast("Aucun chapitre suivant");
    }

    private void PreviousMediaMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        PlayPreviousMedia();

    private void NextMediaMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        PlayNextMedia();

    private void UpdateSkipButtons()
    {
        RewindButton.ToolTip = string.Format(CultureInfo.InvariantCulture,
            LocalizationService.Get("Reculer de {0} secondes"), _rewindSeconds);
        ForwardButton.ToolTip = string.Format(CultureInfo.InvariantCulture,
            LocalizationService.Get("Avancer de {0} secondes"), _forwardSeconds);
        RewindMenuItem.Header = string.Format(CultureInfo.InvariantCulture,
            LocalizationService.Get("Reculer de {0} secondes"), _rewindSeconds);
        ForwardMenuItem.Header = string.Format(CultureInfo.InvariantCulture,
            LocalizationService.Get("Avancer de {0} secondes"), _forwardSeconds);
        UpdateNavigationToolTips();
    }

    private void UpdateNavigationToolTips()
    {
        var previousToolTip = _prioritizeChapters
            ? "Chapitre précédent, puis média précédent (P)"
            : "Média précédent (P)";
        var nextToolTip = _prioritizeChapters
            ? "Chapitre suivant, puis média suivant (N)"
            : "Média suivant (N)";

        PreviousButton.ToolTip = LocalizationService.Get(previousToolTip);
        NextButton.ToolTip = LocalizationService.Get(nextToolTip);
    }

    private void PlayPrevious()
    {
        if (Playlist.Count == 0)
            return;

        if (_prioritizeChapters)
        {
            EnsureChapterMarkers(_mediaPlayer.Length, true);
            if (TryPlayPreviousChapter())
                return;
        }

        PlayPreviousMedia();
    }

    private void PlayNext()
    {
        if (Playlist.Count == 0)
            return;

        if (_prioritizeChapters)
        {
            EnsureChapterMarkers(_mediaPlayer.Length, true);
            if (TryPlayNextChapter())
                return;
        }

        PlayNextMedia();
    }

    private void PlayPreviousMedia()
    {
        if (Playlist.Count == 0)
        {
            ShowToast("Aucun média précédent");
            return;
        }

        var previous = _currentIndex > 0 ? _currentIndex - 1 : Playlist.Count - 1;
        PlayAt(previous);
    }

    private void PlayNextMedia()
    {
        if (Playlist.Count == 0)
        {
            ShowToast("Aucun média suivant");
            return;
        }

        // Avec un seul fichier ouvert, l’épisode voisin n’est pas encore dans
        // la playlist. La lecture augmentée doit aussi fonctionner depuis le
        // bouton « média suivant », pas seulement à la fin du fichier.
        EnsureEnhancedNextQueued();
        var next = GetNextPlaylistIndex();
        if (next < 0 && _enhancedFolderAdvanceEnabled)
        {
            EnsureEnhancedFolderNextQueued();
            next = GetNextPlaylistIndex();
        }

        if (next >= 0)
            PlayAt(next);
        else
            ShowToast("Aucun média suivant");
    }

    private int GetNextPlaylistIndex()
    {
        if (Playlist.Count == 0)
            return -1;

        if (_shufflePlayback && Playlist.Count > 1)
        {
            if (_shufflePlayedIndices.Count >= Playlist.Count)
            {
                _shufflePlayedIndices.Clear();
                if (_currentIndex >= 0)
                    _shufflePlayedIndices.Add(_currentIndex);
            }

            var candidates = Enumerable.Range(0, Playlist.Count)
                .Where(index => index != _currentIndex && !_shufflePlayedIndices.Contains(index))
                .ToArray();
            if (candidates.Length > 0)
            {
                var selected = candidates[_shuffleRandom.Next(candidates.Length)];
                _shufflePlayedIndices.Add(selected);
                return selected;
            }

            // Sécurité pour une liste modifiée pendant un cycle aléatoire.
            _shufflePlayedIndices.Clear();
            if (_currentIndex >= 0)
                _shufflePlayedIndices.Add(_currentIndex);
            candidates = Enumerable.Range(0, Playlist.Count)
                .Where(index => index != _currentIndex)
                .ToArray();
            if (candidates.Length > 0)
            {
                var selected = candidates[_shuffleRandom.Next(candidates.Length)];
                _shufflePlayedIndices.Add(selected);
                return selected;
            }
        }

        var next = _currentIndex + 1;
        return next < Playlist.Count ? next : (_repeatPlaylist ? 0 : -1);
    }

    private void HandlePlaybackEnded()
    {
        UpdateSystemPlaybackAwakeState(false);
        if (_repeatPlayback && _currentIndex >= 0)
        {
            PlayAt(_currentIndex, true, preserveWindowPresentation: true);
            return;
        }

        if (!_playNextMediaAutomatically)
        {
            SetPlayPauseVisual(false);
            SetEngineState("TERMINÉ", "#FF8C929F");
            return;
        }

        EnsureEnhancedNextQueued();

        var next = GetNextPlaylistIndex();
        if (next < 0)
        {
            EnsureEnhancedFolderNextQueued();
            next = GetNextPlaylistIndex();
        }
        if (next >= 0)
            PlayAt(next, true, preserveWindowPresentation: true);
        else
        {
            SetPlayPauseVisual(false);
            SetEngineState("TERMINÉ", "#FF8C929F");
        }
    }

    private void RefreshTimeline()
    {
        if (_isClosing)
            return;

        var length = _mediaPlayer.Length;
        var time = _isSeeking
            ? _seekDragOriginTime
            : _pendingSeekTarget ?? _mediaPlayer.Time;

        if (!_isSeeking)
            SeekSlider.Value = length > 0 ? Math.Clamp(time / (double)length * 1000, 0, 1000) : 0;

        UpdateTimelineText(time, length);
        EnsureChapterMarkers(length);
        UpdateSeekSurface();

        if (_currentIndex >= 0 && _currentIndex < Playlist.Count && length > 0)
            Playlist[_currentIndex].DurationMilliseconds = length;

        if (CanUseEnhancedCurrentMedia() && _playNextMediaAutomatically &&
            !_repeatPlayback && length > 0 &&
            time >= Math.Max(0, length - EnhancedPreloadLeadMilliseconds))
        {
            StartEnhancedPreload();
        }
    }

    private void UpdateTimelineText(long elapsedMilliseconds, long durationMilliseconds)
    {
        if (_bottomBarLayoutPreviewActive)
        {
            // Dix chiffres par compteur (heures, minutes, secondes et
            // millisecondes) afin que l'éditeur montre leur largeur maximale.
            ElapsedText.Text = "100:00:00.000";
            DurationText.Text = "-100:00:00.000";
            return;
        }

        var elapsed = Math.Max(0, elapsedMilliseconds);
        var duration = Math.Max(0, durationMilliseconds);
        ElapsedText.Text = FormatTime(elapsed, _showTimelineMilliseconds);

        var rightTime = _showTotalDuration
            ? duration
            : Math.Max(0, duration - elapsed);
        var prefix = _showTotalDuration ? string.Empty : "-";
        DurationText.Text = $"{prefix}{FormatTime(rightTime, _showTimelineMilliseconds)}";
    }

    private void ElapsedText_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _showTimelineMilliseconds = !_showTimelineMilliseconds;
        UpdateNowPlayingTitleWidth();
        UpdateTimelineText(_pendingSeekTarget ?? _mediaPlayer.Time, _mediaPlayer.Length);
        ShowToast(_showTimelineMilliseconds
            ? "Millisecondes affichées"
            : "Millisecondes masquées");
        RestartControlsHideTimer();
        e.Handled = true;
    }

    private void UpdateNowPlayingTitleWidth()
    {
        // Le titre occupe désormais uniquement l'espace réel de la moitié gauche.
        // Le bloc central s'élargit de lui-même lorsque les millisecondes sont visibles.
        NowPlayingTitleHost.InvalidateMeasure();
    }

    private void NowPlayingTitle_OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock title || string.IsNullOrWhiteSpace(title.Text) ||
            title.ActualWidth <= 0)
        {
            e.Handled = true;
            return;
        }

        var typeface = new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch);
        var formatted = new FormattedText(
            title.Text,
            CultureInfo.CurrentUICulture,
            title.FlowDirection,
            typeface,
            title.FontSize,
            title.Foreground,
            VisualTreeHelper.GetDpi(title).PixelsPerDip);

        if (formatted.WidthIncludingTrailingWhitespace <= title.ActualWidth + 0.5)
            e.Handled = true;
    }

    private void DurationText_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _showTotalDuration = !_showTotalDuration;
        UpdateTimelineText(_pendingSeekTarget ?? _mediaPlayer.Time, _mediaPlayer.Length);
        ShowToast(_showTotalDuration
            ? "Durée totale"
            : "Temps restant");
        RestartControlsHideTimer();
        e.Handled = true;
    }

    private void SeekSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mediaPlayer.Length <= 0)
            return;

        _seekDragOriginTime = Math.Max(0, _pendingSeekTarget ?? _mediaPlayer.Time);
        _isSeeking = true;
        SeekSurface.CaptureMouse();
        UpdateSeekPreviewFromPointer(e, true);
        RevealPlaybackControls();
        e.Handled = true;
    }

    private void SeekSurface_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking)
            return;

        var ratio = UpdateSeekPreviewFromPointer(e, true);
        _isSeeking = false;
        SeekSurface.ReleaseMouseCapture();
        if (ratio is double targetRatio && _mediaPlayer.Length > 0)
            QueueSeek((long)(_mediaPlayer.Length * targetRatio), true);

        RestartControlsHideTimer();
        e.Handled = true;
    }

    private void SeekSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        RevealPlaybackControls();
        UpdateSeekPreviewFromPointer(e, _isSeeking && e.LeftButton == MouseButtonState.Pressed);
    }

    private void SeekSurface_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        if (!_mouseWheelTimelineEnabled)
        {
            e.Handled = true;
            return;
        }

        SeekRelative(e.Delta > 0
            ? _forwardSeconds * 1000L
            : -_rewindSeconds * 1000L);
        RestartControlsHideTimer();
        e.Handled = true;
    }

    private void SeekSurface_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isSeeking)
            SeekPreviewPopup.Visibility = Visibility.Collapsed;
    }

    private void SeekSurface_OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSeekSurface();

    private void SeekSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSeekSurface();
    }

    private double? UpdateSeekPreviewFromPointer(MouseEventArgs e, bool updateProgress = false)
    {
        if (_mediaPlayer.Length <= 0 || SeekSurface.ActualWidth <= 0)
        {
            SeekPreviewPopup.Visibility = Visibility.Collapsed;
            return null;
        }

        var x = Math.Clamp(e.GetPosition(SeekSurface).X, 0, SeekSurface.ActualWidth);
        var ratio = ShowSeekPreview(x);
        if (updateProgress)
            SeekSlider.Value = ratio * SeekSlider.Maximum;

        return ratio;
    }

    private double ShowSeekPreview(double x)
    {
        var trackWidth = Math.Max(0, SeekSurface.ActualWidth - SeekTrackHorizontalInset * 2);
        var ratio = trackWidth > 0 ? (x - SeekTrackHorizontalInset) / trackWidth : 0;
        ratio = Math.Clamp(ratio, 0, 1);
        var previewTime = Math.Clamp((long)(_mediaPlayer.Length * ratio), 0, _mediaPlayer.Length);
        var chapter = _chapterMarkers.LastOrDefault(candidate =>
            candidate.TimeOffset <= previewTime);
        SeekPreviewText.Text = chapter is null || !_showChapterNameInSeekPreview
            ? FormatTime(previewTime)
            : $"{chapter.Name} · {FormatTime(previewTime)}";
        SeekPreviewPopup.Visibility = Visibility.Visible;

        // La largeur dépend du titre du chapitre. Mesurer la bulle avant de la
        // positionner permet de la garder centrée tout en la maintenant dans
        // les limites de la barre lorsque le pointeur est près d'une extrémité.
        SeekPreviewPopup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bubbleWidth = Math.Max(76, SeekPreviewPopup.DesiredSize.Width);
        var availableWidth = Math.Max(0, SeekSurface.ActualWidth);
        Canvas.SetLeft(SeekPreviewPopup,
            Math.Clamp(x - bubbleWidth / 2, 0, Math.Max(0, availableWidth - bubbleWidth)));
        return ratio;
    }

    private void UpdateSeekSurface()
    {
        if (SeekSurface is null || SeekProgressFill is null || SeekPlayhead is null)
            return;

        var width = SeekSurface.ActualWidth;
        if (width <= 0)
            return;

        var ratio = SeekSlider.Maximum > 0
            ? Math.Clamp(SeekSlider.Value / SeekSlider.Maximum, 0, 1)
            : 0;
        var trackWidth = Math.Max(0, width - SeekTrackHorizontalInset * 2);
        SeekTrackBackground.Width = trackWidth;
        Canvas.SetLeft(SeekTrackBackground, SeekTrackHorizontalInset);
        Canvas.SetLeft(SeekProgressFill, SeekTrackHorizontalInset);
        var progress = trackWidth * ratio;
        SeekProgressFill.Width = progress <= 0
            ? 0
            : Math.Round(Math.Min(trackWidth, progress));
        var playheadLeft = Math.Clamp(
            SeekTrackHorizontalInset + progress - 1.5, 0, Math.Max(0, width - 3));
        // Aligner la petite barre sur les pixels physiques empêche
        // l’anticrénelage de changer d’une image à l’autre pendant la lecture.
        Canvas.SetLeft(SeekPlayhead, Math.Round(playheadLeft));
        UpdateChapterMarkerLayout(width, Math.Max(0, _mediaPlayer.Length));
    }

    private void ResetChapterMarkers()
    {
        _chapterMarkers = [];
        _chapterMarkersReady = false;
        _chapterMarkerLoadInProgress = false;
        _chapterMarkerLoadAttempts = 0;
        _chapterMediaGeneration++;
        _nextChapterMarkerLoadTick = 0;
        if (ChapterMarkersCanvas is not null)
            ChapterMarkersCanvas.Children.Clear();
    }

    private void EnsureChapterMarkers(long mediaLength, bool force = false)
    {
        if (_isClosing || _currentIndex < 0 || mediaLength <= 0 || _chapterMarkersReady ||
            _chapterMarkerLoadInProgress)
            return;

        var now = Environment.TickCount64;
        if (!force && now < _nextChapterMarkerLoadTick)
            return;

        _nextChapterMarkerLoadTick = now + 400;
        _chapterMarkerLoadAttempts++;

        int chapterCount;
        try
        {
            chapterCount = _mediaPlayer.ChapterCount;
        }
        catch (MpvException)
        {
            return;
        }

        if (chapterCount <= 0)
        {
            if (_chapterMarkerLoadAttempts >= 15)
            {
                _chapterMarkersReady = true;
                ChapterMarkersCanvas.Children.Clear();
            }
            return;
        }

        _chapterMarkerLoadInProgress = true;
        var generation = _chapterMediaGeneration;
        _ = Task.Run(() =>
        {
            ChapterDescription[] descriptions;
            try
            {
                descriptions = _mediaPlayer.FullChapterDescriptions(-1) ?? [];
            }
            catch (Exception exception) when (exception is MpvException or InvalidOperationException)
            {
                descriptions = [];
            }

            Dispatch(() => CompleteChapterMarkerLoad(
                generation, mediaLength, chapterCount, descriptions));
        });
    }

    private void CompleteChapterMarkerLoad(int generation, long mediaLength,
        int expectedChapterCount, ChapterDescription[] descriptions)
    {
        if (_isClosing || generation != _chapterMediaGeneration)
            return;

        _chapterMarkerLoadInProgress = false;
        // libmpv peut annoncer le bon total un peu avant que toutes les
        // descriptions soient disponibles. Ne jamais figer cette liste partielle.
        if (descriptions.Length < expectedChapterCount && _chapterMarkerLoadAttempts < 15)
            return;

        _chapterMarkers = descriptions
            .Select((chapter, index) => new ChapterMarkerInfo(
                index,
                NormalizeChapterOffset(chapter.TimeOffset, mediaLength),
                string.IsNullOrWhiteSpace(chapter.Name) ? $"Chapitre {index + 1}" : chapter.Name.Trim()))
            .Where(chapter => chapter.TimeOffset >= 0 && chapter.TimeOffset < mediaLength)
            .OrderBy(chapter => chapter.TimeOffset)
            .GroupBy(chapter => chapter.TimeOffset)
            .Select(group => group.First())
            .ToList();

        _chapterMarkersReady = true;
        RebuildChapterMarkers();
        if (ChaptersMenuItem.IsSubmenuOpen)
            RefreshChaptersMenu();
    }

    private static long NormalizeChapterOffset(long offset, long mediaLength)
    {
        if (offset <= 0 || mediaLength <= 0)
            return Math.Max(0, offset);

        // Certaines versions natives expriment ce champ en microsecondes,
        // tandis que la chronologie de MediaPlayer est en millisecondes.
        return offset > mediaLength * 20
            ? offset / 1000
            : offset;
    }

    private void RebuildChapterMarkers()
    {
        ChapterMarkersCanvas.Children.Clear();

        foreach (var chapter in _chapterMarkers)
        {
            var button = new Button
            {
                Style = (Style)FindResource("ChapterMarkerButton"),
                Tag = chapter
            };
            System.Windows.Automation.AutomationProperties.SetName(button,
                $"{chapter.Name}, {FormatTime(chapter.TimeOffset)}");
            button.Click += ChapterMarker_OnClick;
            ChapterMarkersCanvas.Children.Add(button);
        }

        UpdateChapterMarkerLayout(SeekSurface.ActualWidth, Math.Max(0, _mediaPlayer.Length));
    }

    private void UpdateChapterMarkerLayout(double width, long mediaLength)
    {
        if (width <= 0 || mediaLength <= 0 || ChapterMarkersCanvas.Children.Count == 0)
            return;

        foreach (var element in ChapterMarkersCanvas.Children.OfType<Button>())
        {
            if (element.Tag is not ChapterMarkerInfo chapter)
                continue;

            var trackWidth = Math.Max(0, width - SeekTrackHorizontalInset * 2);
            var center = SeekTrackHorizontalInset +
                         trackWidth * Math.Clamp(chapter.TimeOffset / (double)mediaLength, 0, 1);
            Canvas.SetLeft(element,
                Math.Clamp(center - element.Width / 2, 0, Math.Max(0, width - element.Width)));
            Canvas.SetTop(element, 0);
        }
    }

    private void ChapterMarker_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChapterMarkerInfo chapter })
            return;

        QueueSeek(chapter.TimeOffset, true);
        ShowToast(chapter.Name);
        RevealPlaybackControls();
        RestartControlsHideTimer();
        e.Handled = true;
    }

    private bool TryPlayNextChapter()
    {
        var currentTime = _pendingSeekTarget ?? Math.Max(0, _mediaPlayer.Time);
        var nextChapter = _chapterMarkers.FirstOrDefault(chapter =>
            chapter.TimeOffset > currentTime + 250);
        if (nextChapter is not null)
        {
            QueueSeek(nextChapter.TimeOffset, true);
            ShowToast(nextChapter.Name);
            return true;
        }

        try
        {
            var chapterCount = _mediaPlayer.ChapterCount;
            var currentChapter = _mediaPlayer.Chapter;
            if (_chapterMarkers.Count == 0 && chapterCount > 1 && currentChapter >= 0 &&
                currentChapter + 1 < chapterCount)
            {
                _mediaPlayer.NextChapter();
                ShowToast(LocalizationService.Format("Chapitre {0}", currentChapter + 2));
                return true;
            }
        }
        catch (MpvException)
        {
            return false;
        }

        return false;
    }

    private bool TryPlayPreviousChapter()
    {
        var currentTime = _pendingSeekTarget ?? Math.Max(0, _mediaPlayer.Time);
        if (_chapterMarkers.Count > 1)
        {
            var currentChapterIndex = -1;
            for (var index = 0; index < _chapterMarkers.Count; index++)
            {
                if (_chapterMarkers[index].TimeOffset <= currentTime + 250)
                    currentChapterIndex = index;
                else
                    break;
            }

            if (currentChapterIndex > 0)
            {
                var previousChapter = _chapterMarkers[currentChapterIndex - 1];
                QueueSeek(previousChapter.TimeOffset, true);
                ShowToast(previousChapter.Name);
                return true;
            }
        }

        try
        {
            var chapterCount = _mediaPlayer.ChapterCount;
            var currentChapter = _mediaPlayer.Chapter;
            if (_chapterMarkers.Count == 0 && chapterCount > 1 && currentChapter > 0)
            {
                _mediaPlayer.PreviousChapter();
                ShowToast(LocalizationService.Format("Chapitre {0}", currentChapter));
                return true;
            }
        }
        catch (MpvException)
        {
            return false;
        }

        return false;
    }

    private void SeekRelative(long milliseconds)
    {
        if (_currentIndex < 0)
            return;

        var current = _pendingSeekTarget ?? Math.Max(0, _mediaPlayer.Time);
        QueueSeek(current + milliseconds, false);
        ShowToast(milliseconds >= 0 ? $"+{milliseconds / 1000} s" : $"{milliseconds / 1000} s");
    }

    private void QueueSeek(long target, bool immediate)
    {
        var length = _mediaPlayer.Length;
        target = length > 0
            ? Math.Clamp(target, 0, length)
            : Math.Max(0, target);

        _pendingSeekTarget = target;
        ElapsedText.Text = FormatTime(target, _showTimelineMilliseconds);
        if (length > 0)
        {
            SeekSlider.Value = target / (double)length * SeekSlider.Maximum;
            UpdateTimelineText(target, length);
        }
        UpdateSeekSurface();

        _seekCommitTimer.Stop();
        if (immediate)
            CommitPendingSeek();
        else
            _seekCommitTimer.Start();
    }

    private void CommitPendingSeek()
    {
        _seekCommitTimer.Stop();
        if (_pendingSeekTarget is not long target || _currentIndex < 0)
            return;

        _pendingSeekTarget = null;
        AttachVideoOutput();
        _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(target));
    }

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var volume = (int)Math.Round(e.NewValue);
        if (VolumeText is not null)
            VolumeText.Text = $"{volume.ToString(CultureInfo.InvariantCulture)}%";
        if (VolumeInlineBarText is not null)
            VolumeInlineBarText.Text = $"{volume.ToString(CultureInfo.InvariantCulture)}%";
        if (VolumeFillClip is not null)
        {
            var fillWidth = volume <= 0
                ? 0
                : 2d + Math.Clamp(volume / 125d * 92d, 0, 92);
            VolumeFillClip.Rect = new Rect(0, 0, fillWidth, 26);
        }

        UpdateVolumeOverlayVisuals(volume);

        if (_initialized)
        {
            _mediaPlayer.Volume = GetEngineVolume(volume);
            if (volume > 0 && _isMuted)
            {
                _isMuted = false;
                _mediaPlayer.Mute = false;
            }
            UpdateMuteVisual();
        }
    }

    private void UpdateVolumeOverlayVisuals(int volume)
    {
        var clamped = Math.Clamp(volume, 0, 125);
        if (VolumePopupPercent is not null)
            VolumePopupPercent.Text = clamped.ToString(CultureInfo.InvariantCulture);

        if (VolumeIndicatorText is not null)
            VolumeIndicatorText.Text = $"{clamped.ToString(CultureInfo.InvariantCulture)} %";

        if (VolumeIndicatorFill is not null)
        {
            var trackWidth = VolumeIndicatorTrack is not null && VolumeIndicatorTrack.ActualWidth > 0
                ? VolumeIndicatorTrack.ActualWidth
                : 280;
            VolumeIndicatorFill.Width = trackWidth * clamped / 125d;
        }
    }

    private void ApplyVolumeControlStyle()
    {
        if (VolumeTriangleCanvas is null)
            return;

        var style = Math.Clamp(_volumeControlStyle, 0, 3);
        VolumeTriangleCanvas.Visibility = style == 0 ? Visibility.Visible : Visibility.Collapsed;
        VolumeInlineBarHost.Visibility = style == 3 ? Visibility.Visible : Visibility.Collapsed;
        VolumeCompactBarHost.Visibility = Visibility.Collapsed;
        VolumeControlHost.Visibility = style is 0 or 3 ? Visibility.Visible : Visibility.Collapsed;
        VolumeControlHost.Width = style == 3 ? 150 : 100;
        var floating = style == 2;
        // Le style flottant ne doit pas redimensionner la barre inférieure.
        // Les boutons gardent donc la même grille et les mêmes marges dans
        // les quatre variantes de volume.
        AudioTracksButton.Width = 54;
        SubtitleTracksButton.Width = 54;
        SpeedButton.Width = 52;
        OptionsBarPinButton.Width = 32;
        MuteButton.Width = 32;
        AudioTracksButton.Margin = new Thickness(4, 0, 4, 0);
        SubtitleTracksButton.Margin = new Thickness(4, 0, 4, 0);
        SpeedButton.Margin = new Thickness(4, 0, 4, 0);
        TrackSynchronizationButton.Margin = new Thickness(4, 0, 4, 0);
        OptionsBarPinButton.Margin = new Thickness(4, 0, 4, 0);
        MuteButton.Margin = new Thickness(4, 0, 4, 0);
        // La barre compacte affiche le pourcentage à son extrémité. Le
        // curseur flottant est toujours accompagné de son pourcentage.
        VolumeIndicatorText.Visibility = style == 1 ? Visibility.Visible : Visibility.Collapsed;
        VolumePopupPercent.Visibility = style == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (style != 1 || !_initialized)
        {
            _volumeIndicatorHideTimer.Stop();
            VolumeIndicatorOverlay.Visibility = Visibility.Collapsed;
            if (style != 1)
                _volumeOverlayFollowsControls = false;
        }

        if (!floating)
            HideVolumePopup();

        UpdateVolumeOverlayVisuals((int)Math.Round(VolumeSlider.Value));
        // La présence et la largeur de l'item volume changent avec le style.
        // Replacer la vraie barre immédiatement empêche l'ancien espace de
        // rester entre Muet et Plein écran.
        if (_bottomBarLayoutInitialized && !_bottomBarLayoutPreviewActive)
            PositionBottomBarFreeLayout();
    }

    private void ShowVolumeIndicator(bool fromBottomBar = false)
    {
        if (_volumeControlStyle != 1 || !_initialized)
            return;

        _volumeOverlayFollowsControls = fromBottomBar;
        // Le panneau est mesuré avant de calculer le remplissage, afin que la
        // barre orange corresponde toujours à sa largeur réelle.
        VolumeIndicatorOverlay.Visibility = Visibility.Visible;
        UpdateVolumeOverlayVisuals((int)Math.Round(VolumeSlider.Value));
        _volumeIndicatorHideTimer.Stop();
        _volumeIndicatorHideTimer.Interval = TimeSpan.FromMilliseconds(_volumeIndicatorHideDelayMilliseconds);
        _volumeIndicatorHideTimer.Start();
    }

    private void ShowVolumePopup(bool fromWheel = false, bool fromBottomBar = false)
    {
        if (_volumeControlStyle != 2)
            return;

        _volumePopupUsesIndicatorDelay = fromWheel;
        _volumeOverlayFollowsControls = fromBottomBar;
        UpdateVolumeOverlayVisuals((int)Math.Round(VolumeSlider.Value));
        VolumePopup.Visibility = Visibility.Visible;
        _volumePopupHideTimer.Stop();
        _volumePopupHideTimer.Interval = TimeSpan.FromMilliseconds(
            fromWheel ? _volumeIndicatorHideDelayMilliseconds : _volumePopupHideDelayMilliseconds);
        _volumePopupHideTimer.Start();
        RestartControlsHideTimer();
    }

    private void HideVolumePopup()
    {
        _volumePopupHideTimer.Stop();
        _volumePopupUsesIndicatorDelay = false;
        _volumeOverlayFollowsControls = false;
        if (VolumePopup is not null)
            VolumePopup.Visibility = Visibility.Collapsed;
    }

    private void ScheduleVolumePopupHide()
    {
        if (_volumeControlStyle != 2 || VolumePopup.Visibility != Visibility.Visible)
            return;

        _volumePopupHideTimer.Stop();
        _volumePopupHideTimer.Interval = TimeSpan.FromMilliseconds(
            _volumePopupUsesIndicatorDelay
                ? _volumeIndicatorHideDelayMilliseconds
                : _volumePopupHideDelayMilliseconds);
        _volumePopupHideTimer.Start();
    }

    private void VolumePopup_OnMouseEnter(object sender, MouseEventArgs e) =>
        _volumePopupHideTimer.Stop();

    private void VolumePopup_OnMouseLeave(object sender, MouseEventArgs e) =>
        ScheduleVolumePopupHide();

    private void MuteButton_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_volumeControlStyle == 2 && VolumePopup.Visibility == Visibility.Visible)
            _volumePopupHideTimer.Stop();
    }

    private void MuteButton_OnMouseLeave(object sender, MouseEventArgs e) =>
        ScheduleVolumePopupHide();

    private static int GetEngineVolume(int displayedVolume)
    {
        displayedVolume = Math.Clamp(displayedVolume, 0, 125);
        if (displayedVolume <= 100)
            return displayedVolume;

        // La plage visible 100–125 pilote toute la réserve d'amplification de libmpv.
        return 100 + (displayedVolume - 100) * 4;
    }

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 125);
        ShowToast(LocalizationService.Format("Volume {0} %", (int)VolumeSlider.Value));
    }

    private void VolumeUpMenuItem_OnClick(object sender, RoutedEventArgs e) => ChangeVolume(5);

    private void VolumeDownMenuItem_OnClick(object sender, RoutedEventArgs e) => ChangeVolume(-5);

    private void VolumeControl_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isAdjustingVolume = true;
        VolumeHitArea.CaptureMouse();
        SetVolumeFromPointer(e);
    }

    private void VolumeControl_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isAdjustingVolume && e.LeftButton == MouseButtonState.Pressed)
            SetVolumeFromPointer(e);
    }

    private void VolumeControl_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        if (!_mouseWheelVolumeEnabled)
        {
            e.Handled = true;
            return;
        }

        ChangeVolume(e.Delta > 0 ? 5 : -5);
        if (_volumeControlStyle == 1)
            ShowVolumeIndicator(fromBottomBar: true);
        else if (_volumeControlStyle == 2)
            ShowVolumePopup(fromWheel: true, fromBottomBar: true);
        RestartControlsHideTimer();
        e.Handled = true;
    }

    private void VolumeControl_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isAdjustingVolume)
            return;

        SetVolumeFromPointer(e);
        _isAdjustingVolume = false;
        VolumeHitArea.ReleaseMouseCapture();
    }

    private void SetVolumeFromPointer(MouseEventArgs e)
    {
        var x = e.GetPosition(VolumeHitArea).X;
        var volume = (int)Math.Round(Math.Clamp((x - 2d) / 92d * 125d, 0, 125));
        if ((int)Math.Round(VolumeSlider.Value) != volume)
            VolumeSlider.Value = volume;
    }

    private void MuteButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        _mediaPlayer.Mute = _isMuted;
        UpdateMuteVisual();
        ShowToast(_isMuted
            ? LocalizationService.Get("Son coupé")
            : LocalizationService.Format("Volume {0} %", (int)VolumeSlider.Value));
        e.Handled = true;
    }

    private void MuteButton_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_volumeControlStyle == 2)
            ShowVolumePopup(fromBottomBar: true);
        else
            OpenAudioDevicesContextMenu(MuteButton);

        e.Handled = true;
    }

    private void VolumeControl_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement target)
            OpenAudioDevicesContextMenu(target);

        e.Handled = true;
    }

    private void OpenAudioDevicesContextMenu(FrameworkElement target)
    {
        var menu = CreateFuzeContextMenu();
        var engineSelection = _mediaPlayer.AudioDevice;
        if (!string.IsNullOrWhiteSpace(engineSelection))
            _selectedAudioDevice = engineSelection;

        var adaptiveItem = new MenuItem
        {
            Header = LocalizationService.Get("Mode audio adaptatif"),
            ToolTip = LocalizationService.Get("Utilise automatiquement le périphérique associé à l’écran actif"),
            IsCheckable = true,
            IsChecked = _adaptiveAudioModeEnabled,
            Tag = AdaptiveAudioDeviceMenuTag
        };
        adaptiveItem.Click += AudioDeviceMenuItem_OnClick;
        menu.Items.Add(adaptiveItem);
        menu.Items.Add(new Separator());

        var devices = GetAvailableAudioDevices();
        foreach (var device in devices)
        {
            var item = new MenuItem
            {
                Header = device.Description,
                ToolTip = device.Name.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? LocalizationService.Get("Suit automatiquement le périphérique choisi dans Windows")
                    : device.Name,
                IsCheckable = true,
                IsChecked = !_adaptiveAudioModeEnabled &&
                            device.Name.Equals(_selectedAudioDevice, StringComparison.OrdinalIgnoreCase),
                Tag = device
            };
            item.Click += AudioDeviceMenuItem_OnClick;
            menu.Items.Add(item);
        }

        if (devices.Length == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = LocalizationService.Get("Aucun périphérique détecté"),
                IsEnabled = false
            });
        }

        OpenContextMenu(menu, target, PlacementMode.Top);
    }

    private void UpdateMuteVisual()
    {
        MuteButton.Content = _isMuted || VolumeSlider.Value <= 0 ? "\uE74F" : "\uE767";
        MuteButton.Foreground = _isMuted
            ? new SolidColorBrush(Color.FromRgb(255, 92, 92))
            : new SolidColorBrush(Color.FromRgb(255, 154, 72));
    }

    private void SpeedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("FuzeContextMenuStyle")
        };
        PopulatePlaybackSpeedMenu(menu);
        OpenContextMenu(menu, SpeedButton);
    }

    private void PlaybackSpeedMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e) =>
        PopulatePlaybackSpeedMenu(PlaybackSpeedMenuItem);

    private void PopulatePlaybackSpeedMenu(ItemsControl menu)
    {
        menu.Items.Clear();
        for (var index = 0; index < SpeedOptions.Length; index++)
        {
            var optionIndex = index;
            var item = new MenuItem
            {
                Header = LocalizationService.Get(SpeedOptions[index].Label),
                IsCheckable = true,
                IsChecked = index == _speedIndex
            };
            item.Click += (_, _) => ApplySpeed(optionIndex, true);
            menu.Items.Add(item);
        }

        var separator = new Separator();
        if (menu is ContextMenu)
            separator.Style = (Style)FindResource("FuzeContextMenuSeparatorStyle");
        menu.Items.Add(separator);
        var customItem = new MenuItem
        {
            Header = LocalizationService.Get("Définir une vitesse personnalisée…"),
            IsCheckable = true,
            IsChecked = _speedIndex < 0
        };
        customItem.Click += CustomPlaybackSpeedMenuItem_OnClick;
        menu.Items.Add(customItem);
    }

    private void ChangeSpeed(int direction)
    {
        if (direction == 0)
            return;

        if (_speedIndex >= 0)
        {
            ApplySpeed(Math.Clamp(_speedIndex + Math.Sign(direction), 0, SpeedOptions.Length - 1), true);
            return;
        }

        var optionIndex = direction > 0
            ? Array.FindIndex(SpeedOptions, option => option.Rate > _playbackRate + 0.0001f)
            : Array.FindLastIndex(SpeedOptions, option => option.Rate < _playbackRate - 0.0001f);
        if (optionIndex >= 0)
            ApplySpeed(optionIndex, true);
    }

    private void ApplySpeed(int index, bool notify)
    {
        _speedIndex = Math.Clamp(index, 0, SpeedOptions.Length - 1);
        ApplyPlaybackRate(SpeedOptions[_speedIndex].Rate, notify);
    }

    private void CustomPlaybackSpeedMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PlaybackSpeedDialog(_playbackRate) { Owner = DialogOwnerWindow };
        if (ShowModalDialog(dialog.ShowDialog) == true)
            ApplyPlaybackRate(dialog.Rate, true);
    }

    private void ApplyPlaybackRate(float rate, bool notify)
    {
        _playbackRate = Math.Clamp(rate, 0.05f, 10f);
        _speedIndex = Array.FindIndex(SpeedOptions,
            option => Math.Abs(option.Rate - _playbackRate) < 0.0001f);
        var label = FormatPlaybackRate(_playbackRate);
        _mediaPlayer.SetRate(_playbackRate);
        SpeedButton.Content = label;
        SaveCurrentMediaPlaybackPreferences();
        if (notify)
            ShowToast(LocalizationService.Format("Vitesse {0}", label));
    }

    private static string FormatPlaybackRate(float rate) =>
        $"{rate.ToString("0.00", LocalizationService.CurrentLanguage == "fr"
            ? CultureInfo.GetCultureInfo("fr-CA")
            : CultureInfo.GetCultureInfo("en-US"))}×";

    private void SettingsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        // ExecuteShortcut peut appeler ce gestionnaire avec un RoutedEventArgs
        // synthétique, sans RoutedEvent associé. Dans ce cas, WPF lève une
        // InvalidOperationException si l'on écrit directement Handled.
        if (e.RoutedEvent is not null)
            e.Handled = true;
        if (_settingsDialogOpening || _modalDialogDepth > 0 || _isClosing)
            return;

        // Un MenuItem garde la capture de la souris pendant son clic. Ouvrir
        // une fenêtre modale dans ce même événement peut la placer derrière
        // le menu ou la faire fermer immédiatement. On laisse d'abord WPF
        // fermer le menu, puis on ouvre la fenêtre au tour de dispatcher
        // suivant.
        _settingsDialogOpening = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _settingsDialogOpening = false;
            if (!_isClosing && _modalDialogDepth == 0)
                OpenSettingsDialog();
        }), DispatcherPriority.ContextIdle);
    }

    private void OpenSettingsDialog(string? initialCategory = null)
    {
        var engineAudioDevice = _mediaPlayer.AudioDevice;
        if (!string.IsNullOrWhiteSpace(engineAudioDevice))
            _selectedAudioDevice = engineAudioDevice;
        var originalBottomBarLayoutPresets = _bottomBarLayoutPresets
            .Select(CloneBottomBarLayout)
            .ToList();
        var originalActiveBottomBarLayoutPreset = _activeBottomBarLayoutPreset;

        var dialog = new SettingsDialog(
            _rewindSeconds,
            _forwardSeconds,
            _prioritizeChapters,
            _playNextMediaAutomatically,
            new PlaybackSettingsSnapshot(
                _resumePlayback,
                _resumePromptStartSkipPercent,
                _resumePromptEndSkipPercent,
                _autoPlayOnOpen,
                _confirmClose,
                _bufferingEnabled,
                _preventSleepDuringPlayback,
                _rememberMediaSettings,
                _recentMediaRetentionDays,
                _recentMediaFolderDepth,
                _playlistFolderDepth,
                _repeatPlaylist,
                _enhancedPlaybackEnabled,
                _enhancedFolderAdvanceEnabled,
                _enhancedFolderShowNameEnabled,
                _showEnhancedUpcomingInPlaylist,
                _showEnhancedNextFolderInPlaylist,
                _fileAssociationsEnabled,
                _fileAssociationExtensions,
                _customFileAssociationTypes),
            new InterfaceSettingsSnapshot(
                _topBarAutoHideDelayMilliseconds,
                _bottomBarAutoHideDelayMilliseconds,
                _playlistScrollSpeed,
                _volumeControlStyle,
                _volumePopupHideDelayMilliseconds,
                _volumeIndicatorHideDelayMilliseconds,
                _hideInterfaceOnVideoStart,
                _showSynchronizationButton,
                _showShuffleButton,
                _showRepeatButton,
                _showSpeedButton,
                _showPlaylistButton,
                _showAdditionalMediaInformation,
                _autoCompactMissingBottomBarItems,
                _shufflePlayback,
                _repeatPlayback,
                _showScreenshotButton,
                _adaptiveInterfaceScale,
                _autoHideCursor,
                _cursorAutoHideDelayMilliseconds,
                _alwaysOnTop,
                _showOsd,
                _discordActivityEnabled,
                _diagnosticLoggingEnabled,
                _disableToolTips,
                _interfaceLanguage,
                _togglePlaybackOnSingleClick,
                 _toggleFullscreenOnDoubleClick,
                _bottomBarLayoutPresets,
                _activeBottomBarLayoutPreset,
                _showChapterNameInSeekPreview,
                _showVideoPanButton),
            GetAvailableVideoDisplays(),
            new VideoSettingsSnapshot(
                _startVideoFullscreen,
                _preferredVideoDisplay,
                _videoOutput,
                _hardwareDecoding,
                _deinterlacing,
                _hdrMode,
                _screenshotBaseDirectory,
                _screenshotFolderName,
                _screenshotFormat,
                _screenshotAffixMode,
                _screenshotAffixText,
                _screenshotSequentialNumbering,
                _copyScreenshotsToClipboard,
                _customZoomPercent,
                _customAspectRatio),
            new ShortcutSettingsSnapshot(
                _keyboardShortcuts,
                _mouseWheelTimelineEnabled,
                _mouseWheelVolumeEnabled,
                _centerWheelVolumeEnabled,
                _centerWheelTimelineEnabled,
                _mouseWheelAudioTracksEnabled,
                _mouseWheelSubtitleTracksEnabled,
                _ignoreKeyboardVolumeButtons),
            _resetVolumeOnStartup,
            _startupVolume,
            GetAvailableAudioDevices(),
            _selectedAudioDevice,
            _audioPassthrough,
            _audioExclusive,
            _disableAudioByDefault,
            _autoSelectPreferredAudio,
            _preferredAudioProfile,
            _preferredAudioTitlePriorities,
            _audioOutputMode,
            _audioTreatmentMode,
            _defaultAudioDelayMilliseconds,
            _audioNormalization,
            _autoSwitchAudioDevice,
            _adaptiveAudioModeEnabled,
            _adaptiveAudioDisplayMappings,
            new SubtitleSettingsSnapshot(
                _startupTitleOverlayEnabled,
                _preferOriginalTitleForStartup,
                _startupTitlePosition,
                _startupTitleDelayMilliseconds,
                _startupTitleDurationMilliseconds,
                _startupTitleFont,
                _startupTitleFontSize,
                _startupTitleTextColor,
                _startupTitleBorderColor,
                _startupTitleBorderSize,
                _startupTitleShadow,
                _startupTitleMarginX,
                _startupTitleMarginY,
                _startupTitleScaleWithWindow,
                _autoSelectPreferredSubtitle,
                _preferredSubtitleProfile,
                _preferredSubtitleTitlePriorities,
                _preferSdhSubtitles,
                _disableSubtitlesByDefault,
                _autoLoadExternalSubtitles,
                _subtitleEncoding,
                _subtitleFont,
                _subtitleFontSize,
                _subtitleTextColor,
                _subtitleBorderColor,
                _subtitleBorderSize,
                _subtitleShadow,
                _subtitleForcePosition,
                _subtitlePosition,
                _subtitleMarginX,
                _subtitleMarginY,
                _subtitleScaleWithWindow))
        {
            Owner = DialogOwnerWindow
        };
        dialog.BottomBarLayoutPreviewChanged += ApplyBottomBarLayoutPreview;
        dialog.ExportSettingsRequested += path => ExportSettingsFromDialog(dialog, path);
        dialog.ImportSettingsRequested += path => ImportSettingsFromDialog(dialog, path);
        if (!string.IsNullOrWhiteSpace(initialCategory))
            dialog.SelectCategory(initialCategory);

        var restorePinnedToolBar = _toolBarPinnedOpen;
        var restoreTopBar = ToolBarHost.Visibility == Visibility.Visible;
        _toolBarHideTimer.Stop();
        _gearControlsHideTimer.Stop();

        bool? settingsResult;
        try
        {
            settingsResult = ShowModalDialog(dialog.ShowDialog);
        }
        finally
        {
            // L'état choisi avec l'écrou appartient à la fenêtre principale;
            // ouvrir ou fermer Paramètres ne doit jamais le réinitialiser.
            if (!_isClosing && (restorePinnedToolBar || restoreTopBar))
            {
                if (restorePinnedToolBar)
                {
                    _toolBarPinnedOpen = true;
                    _suppressToolBarActivation = false;
                    ExpandToolBar(false);
                }
                else if (CanRevealTopBarWithoutGear())
                {
                    ExpandToolBar(true);
                }

                RestartToolBarHideTimer();
            }
        }

        if (settingsResult != true)
        {
            if (dialog.SettingsImported)
            {
                ShowToast(LocalizationService.Get("Paramètres importés"));
                return;
            }

            // L’aperçu live ne doit pas modifier la session lorsque l’utilisateur
            // annule la fenêtre de paramètres.
            _bottomBarLayoutPresets = originalBottomBarLayoutPresets;
            _activeBottomBarLayoutPreset = originalActiveBottomBarLayoutPreset;
            ApplyBottomBarLayout();
            if (dialog.BottomBarLayoutEditorRequested)
            {
                // Le placement se fait sur la vraie barre du lecteur. Les
                // paramètres modaux se ferment d’abord pour rendre la fenêtre
                // principale interactive, puis l’éditeur flottant s’ouvre.
                var basePresetName = dialog.BottomBarLayoutEditorBasePresetName;
                Dispatcher.BeginInvoke(() => OpenBottomBarLayoutEditor(basePresetName),
                    DispatcherPriority.ContextIdle);
            }
            return;
        }

        _rewindSeconds = dialog.RewindSeconds;
        _forwardSeconds = dialog.ForwardSeconds;
        _prioritizeChapters = dialog.PrioritizeChapters;
        _playNextMediaAutomatically = dialog.PlayNextMediaAutomatically;
        _enhancedPlaybackEnabled = dialog.PlaybackSettings.EnhancedPlaybackEnabled;
        _enhancedFolderAdvanceEnabled = dialog.PlaybackSettings.EnhancedFolderAdvanceEnabled;
        _enhancedFolderShowNameEnabled = dialog.PlaybackSettings.EnhancedFolderShowNameEnabled;
        _showEnhancedUpcomingInPlaylist = dialog.PlaybackSettings.ShowEnhancedUpcomingInPlaylist;
        _showEnhancedNextFolderInPlaylist = dialog.PlaybackSettings.ShowEnhancedNextFolderInPlaylist;
        RefreshEnhancedPlaybackEligibility();
        if (!_showEnhancedNextFolderInPlaylist || !_enhancedFolderAdvanceEnabled)
            RemoveEnhancedNextFolderItems();
        if (_showEnhancedUpcomingInPlaylist)
            EnsureEnhancedNextQueued();
        else
            RefreshPlaylistCount();
        _resumePlayback = dialog.PlaybackSettings.ResumePlayback;
        _resumePromptStartSkipPercent = Math.Clamp(dialog.PlaybackSettings.ResumePromptStartSkipPercent, 0, 100);
        _resumePromptEndSkipPercent = Math.Clamp(dialog.PlaybackSettings.ResumePromptEndSkipPercent, 0, 100);
        _autoPlayOnOpen = dialog.PlaybackSettings.AutoPlayOnOpen;
        _confirmClose = dialog.PlaybackSettings.ConfirmClose;
        _bufferingEnabled = dialog.PlaybackSettings.BufferingEnabled;
        var rememberMediaSettingsWasEnabled = _rememberMediaSettings;
        _preventSleepDuringPlayback = dialog.PlaybackSettings.PreventSleepDuringPlayback;
        _rememberMediaSettings = dialog.PlaybackSettings.RememberMediaSettings;
        _recentMediaRetentionDays = Math.Clamp(dialog.PlaybackSettings.RecentMediaRetentionDays, 0, 3650);
        _recentMediaFolderDepth = Math.Clamp(dialog.PlaybackSettings.RecentMediaFolderDepth, 0, 10);
        _playlistFolderDepth = Math.Clamp(dialog.PlaybackSettings.PlaylistFolderDepth, 0, 10);
        ApplyPlaylistFolderDepth();
        if (_rememberMediaSettings && !rememberMediaSettingsWasEnabled)
            SaveCurrentMediaPlaybackPreferences();
        PruneRecentMediaByRetention();
        PruneMediaPlaybackPreferences();
        RefreshRecentMediaMenu();
        _repeatPlaylist = dialog.PlaybackSettings.RepeatPlaylist;
        var previousCustomFileAssociationExtensions = _customFileAssociationTypes
            .Select(type => type.Extension)
            .ToArray();
        _fileAssociationsEnabled = dialog.PlaybackSettings.FileAssociationsEnabled;
        _fileAssociationExtensions = NormalizeFileAssociationExtensions(
            dialog.PlaybackSettings.FileAssociationExtensions);
        _customFileAssociationTypes = FileAssociationService.NormalizeCustomTypes(
            dialog.PlaybackSettings.CustomFileAssociationTypes);
        ApplyFileAssociations(showToast: true,
            previousCustomFileAssociationExtensions: previousCustomFileAssociationExtensions);
        _shufflePlayback = dialog.InterfaceSettings.ShufflePlayback;
        _repeatPlayback = dialog.InterfaceSettings.RepeatPlayback;
        _showScreenshotButton = dialog.InterfaceSettings.ShowScreenshotButton;
        _showShuffleButton = dialog.InterfaceSettings.ShowShuffleButton;
        _showRepeatButton = dialog.InterfaceSettings.ShowRepeatButton;
        _showSpeedButton = dialog.InterfaceSettings.ShowSpeedButton;
        _showPlaylistButton = dialog.InterfaceSettings.ShowPlaylistButton;
        _showAdditionalMediaInformation = dialog.InterfaceSettings.ShowAdditionalMediaInformation;
        _adaptiveInterfaceScale = dialog.InterfaceSettings.AdaptiveInterfaceScale;
        _autoHideCursor = dialog.InterfaceSettings.AutoHideCursor;
        _alwaysOnTop = dialog.InterfaceSettings.AlwaysOnTop;
        _showOsd = dialog.InterfaceSettings.ShowOsd;
        _disableToolTips = dialog.InterfaceSettings.DisableToolTips;
        _showChapterNameInSeekPreview = dialog.InterfaceSettings.ShowChapterNameInSeekPreview;
        _interfaceLanguage = NormalizeInterfaceLanguage(dialog.InterfaceSettings.InterfaceLanguage);
        _togglePlaybackOnSingleClick = dialog.InterfaceSettings.TogglePlaybackOnSingleClick;
        _toggleFullscreenOnDoubleClick = dialog.InterfaceSettings.ToggleFullscreenOnDoubleClick;
        _discordActivityEnabled = dialog.InterfaceSettings.DiscordActivityEnabled;
        _diagnosticLoggingEnabled = dialog.InterfaceSettings.DiagnosticLoggingEnabled;
        UpdatePlaybackModeButtons();
        ApplyInterfaceSettings(dialog.InterfaceSettings);
        _resetVolumeOnStartup = dialog.ResetVolumeOnStartup;
        _startupVolume = dialog.StartupVolume;

        var video = dialog.VideoSettings;
        var videoOutputChanged = !string.Equals(video.VideoOutput, _videoOutput,
            StringComparison.OrdinalIgnoreCase);
        _startVideoFullscreen = video.StartFullscreen;
        _preferredVideoDisplay = video.PreferredDisplay;
        _videoOutput = NormalizeVideoOutputSetting(video.VideoOutput);
        _customZoomPercent = Math.Clamp(video.CustomZoomPercent, 50, 1000);
        _customAspectRatio = NormalizeCustomAspectRatio(video.CustomAspectRatio);
        _hardwareDecoding = video.HardwareDecoding;
        _deinterlacing = video.Deinterlacing;
        _hdrMode = NormalizeHdrMode(video.HdrMode);
        _mediaPlayer.SetHardwareDecoding(_hardwareDecoding);
        _mediaPlayer.SetDeinterlacing(_deinterlacing);
        _mediaPlayer.SetHdrMode(_hdrMode);
        _mediaPlayer.SetBufferingEnabled(_bufferingEnabled);
        UpdateSystemPlaybackAwakeState(_mediaPlayer.IsPlaying);
        _screenshotBaseDirectory = NormalizeScreenshotBaseDirectory(video.ScreenshotBaseDirectory);
        _screenshotFolderName = NormalizeScreenshotFolderName(video.ScreenshotFolderName);
        _screenshotFormat = NormalizeScreenshotFormat(video.ScreenshotFormat);
        _screenshotAffixMode = NormalizeScreenshotAffixMode(video.ScreenshotAffixMode);
        _screenshotAffixText = video.ScreenshotAffixText.Trim();
        _screenshotSequentialNumbering = video.ScreenshotSequentialNumbering;
        _copyScreenshotsToClipboard = video.CopyScreenshotsToClipboard;

        var shortcuts = dialog.ShortcutSettings;
        _keyboardShortcuts = ShortcutCatalog.Normalize(shortcuts.KeyboardShortcuts);
        _mouseWheelTimelineEnabled = shortcuts.MouseWheelTimelineEnabled;
        _mouseWheelVolumeEnabled = shortcuts.MouseWheelVolumeEnabled;
        _centerWheelVolumeEnabled = shortcuts.CenterWheelVolumeEnabled;
        _centerWheelTimelineEnabled = shortcuts.CenterWheelTimelineEnabled && !_centerWheelVolumeEnabled;
        _mouseWheelAudioTracksEnabled = shortcuts.MouseWheelAudioTracksEnabled;
        _mouseWheelSubtitleTracksEnabled = shortcuts.MouseWheelSubtitleTracksEnabled;
        _ignoreKeyboardVolumeButtons = shortcuts.IgnoreKeyboardVolumeButtons;

        var audioError = false;
        _autoSwitchAudioDevice = dialog.AutoSwitchAudioDevice;
        _adaptiveAudioModeEnabled = dialog.AdaptiveAudioModeEnabled;
        _adaptiveAudioDisplayMappings = dialog.AdaptiveAudioDisplayMappings
            .Select(mapping => new AdaptiveAudioDisplayMappingData
            {
                DisplayId = mapping.DisplayId,
                DisplayName = mapping.DisplayName,
                AudioDevice = mapping.AudioDevice
            })
            .ToList();
        _lastAdaptiveAudioDisplayId = null;
        // Le mode adaptatif est représenté par un choix virtuel dans les
        // paramètres. Il ne faut jamais envoyer cette valeur au moteur audio;
        // lorsque le mode est actif, la sélection par écran ci-dessous est
        // prioritaire. Un périphérique concret désactive déjà ce mode dans la
        // fenêtre de paramètres.
        var requestedAudioDevice = string.IsNullOrWhiteSpace(dialog.SelectedAudioDevice)
            ? "auto"
            : dialog.SelectedAudioDevice;
        if (!_adaptiveAudioModeEnabled &&
            !requestedAudioDevice.Equals(_selectedAudioDevice,
                StringComparison.OrdinalIgnoreCase))
        {
            if (_mediaPlayer.SetAudioDevice(requestedAudioDevice))
                _selectedAudioDevice = requestedAudioDevice;
            else
                audioError = true;
        }

        if (_autoSwitchAudioDevice && !_adaptiveAudioModeEnabled)
        {
            if (_mediaPlayer.SetAudioDevice("auto"))
                _selectedAudioDevice = "auto";
            else
                audioError = true;
        }

        if (dialog.AudioPassthrough != _audioPassthrough)
        {
            if (_mediaPlayer.SetAudioPassthrough(dialog.AudioPassthrough))
                _audioPassthrough = dialog.AudioPassthrough;
            else
                audioError = true;
        }

        if (dialog.AudioExclusive != _audioExclusive)
        {
            if (_mediaPlayer.SetAudioExclusive(dialog.AudioExclusive))
                _audioExclusive = dialog.AudioExclusive;
            else
                audioError = true;
        }

        if (dialog.AudioOutputMode != _audioOutputMode)
        {
            if (_mediaPlayer.SetAudioOutputMode(dialog.AudioOutputMode))
                _audioOutputMode = dialog.AudioOutputMode;
            else
                audioError = true;
        }

        if (dialog.AudioTreatmentMode != _audioTreatmentMode)
        {
            if (_mediaPlayer.SetAudioTreatmentMode(dialog.AudioTreatmentMode))
                _audioTreatmentMode = dialog.AudioTreatmentMode;
            else
                audioError = true;
        }

        _disableAudioByDefault = dialog.DisableAudioByDefault;
        _autoSelectPreferredAudio = dialog.AutoSelectPreferredAudio;
        _preferredAudioProfile = NormalizePreferredAudioProfile(dialog.PreferredAudioProfile);
        _preferredAudioTitlePriorities = NormalizeTitlePriorities(dialog.PreferredAudioTitlePriorities);
        _preferredAudioAppliedForCurrentMedia = false;
        _defaultAudioDelayMilliseconds = dialog.DefaultAudioDelayMilliseconds;
        _audioNormalization = dialog.AudioNormalization;
        if (!_mediaPlayer.SetAudioNormalization(_audioNormalization))
            audioError = true;
        if (_currentMedia is not null)
        {
            _audioSyncMilliseconds = _defaultAudioDelayMilliseconds;
            if (!_mediaPlayer.SetTrackSynchronization(_videoSyncMilliseconds,
                    _audioSyncMilliseconds, _subtitleSyncMilliseconds))
                audioError = true;
            TryApplyPreferredAudioTrack();
        }
        if (_adaptiveAudioModeEnabled && !ApplyAdaptiveAudioDeviceForCurrentDisplay(force: true))
            audioError = true;

        var subtitles = dialog.SubtitleSettings;
        _startupTitleOverlayEnabled = subtitles.StartupTitleOverlayEnabled;
        _preferOriginalTitleForStartup = subtitles.PreferOriginalTitleForStartup;
        _startupTitlePosition = NormalizeScreenPosition(subtitles.StartupTitlePosition, "top-center");
        _startupTitleDelayMilliseconds = subtitles.StartupTitleDelayMilliseconds;
        _startupTitleDurationMilliseconds = subtitles.StartupTitleDurationMilliseconds;
        _startupTitleFont = subtitles.StartupTitleFont;
        _startupTitleFontSize = subtitles.StartupTitleFontSize;
        _startupTitleTextColor = subtitles.StartupTitleTextColor;
        _startupTitleBorderColor = subtitles.StartupTitleBorderColor;
        _startupTitleBorderSize = subtitles.StartupTitleBorderSize;
        _startupTitleShadow = subtitles.StartupTitleShadow;
        _startupTitleMarginX = subtitles.StartupTitleMarginX;
        _startupTitleMarginY = subtitles.StartupTitleMarginY;
        _startupTitleScaleWithWindow = subtitles.StartupTitleScaleWithWindow;
        _autoSelectPreferredSubtitle = subtitles.AutoSelectPreferredSubtitle;
        _preferredSubtitleProfile = NormalizePreferredSubtitleProfile(subtitles.PreferredSubtitleProfile);
        _preferredSubtitleTitlePriorities = NormalizeTitlePriorities(subtitles.PreferredSubtitleTitlePriorities);
        _preferSdhSubtitles = subtitles.PreferSdhSubtitles;
        _disableSubtitlesByDefault = subtitles.DisableSubtitlesByDefault;
        _autoLoadExternalSubtitles = subtitles.AutoLoadExternalSubtitles;
        if (!_mediaPlayer.SetExternalSubtitleAutoLoad(_autoLoadExternalSubtitles))
            audioError = true;
        _preferredSubtitleAppliedForCurrentMedia = false;
        _subtitleEncoding = subtitles.SubtitleEncoding;
        _subtitleFont = subtitles.SubtitleFont;
        _subtitleFontSize = subtitles.SubtitleFontSize;
        _subtitleTextColor = subtitles.SubtitleTextColor;
        _subtitleBorderColor = subtitles.SubtitleBorderColor;
        _subtitleBorderSize = subtitles.SubtitleBorderSize;
        _subtitleShadow = subtitles.SubtitleShadow;
        _subtitleForcePosition = subtitles.SubtitleForcePosition;
        _subtitlePosition = NormalizeScreenPosition(subtitles.SubtitlePosition, "bottom-center");
        _subtitleMarginX = subtitles.SubtitleMarginX;
        _subtitleMarginY = subtitles.SubtitleMarginY;
        _subtitleScaleWithWindow = subtitles.SubtitleScaleWithWindow;
        if (!_mediaPlayer.SetSubtitlePreferences(BuildSubtitlePreferences()))
            audioError = true;
        if (_currentMedia is not null)
            TryApplyPreferredSubtitleTrack();

        UpdateSkipButtons();
        UpdateAudioMenuAvailability();
        PersistSession();
        ShowToast(audioError
            ? "Paramètres enregistrés • une option de lecture n’a pas pu être appliquée"
            : videoOutputChanged
                ? "Paramètres enregistrés • sortie vidéo appliquée au prochain démarrage"
                : "Paramètres enregistrés");
    }

    private void ExportSettingsFromDialog(SettingsDialog dialog, string path)
    {
        // Les réglages déjà enregistrés sont exportés; les modifications
        // temporaires du formulaire restent volontairement non appliquées
        // jusqu'au bouton Enregistrer.
        PersistSession();
        if (_sessionStore.TryExportSettings(path))
        {
            dialog.ShowTransferNotice("Paramètres exportés",
                "La configuration de Fuze a été exportée. La file et la reprise locale n'ont pas été incluses.");
        }
        else
        {
            dialog.ShowTransferNotice("Export impossible",
                "Fuze n'a pas pu écrire ce fichier. Vérifiez l'emplacement choisi.");
        }
    }

    private void ImportSettingsFromDialog(SettingsDialog dialog, string path)
    {
        if (!_sessionStore.TryImportSettings(path, out var imported))
        {
            dialog.ShowTransferNotice("Import impossible",
                "Ce fichier n'est pas une configuration Fuze valide ou ne peut pas être lu.");
            return;
        }

        // Un transfert de paramètres ne doit pas interrompre le média en
        // cours. On conserve donc la file, le média et la visibilité actuels
        // puis on applique uniquement la configuration importée.
        var currentQueue = Playlist.Select(item => new PlaylistItemData
        {
            Location = item.Location,
            Title = item.Title,
            IsNetwork = item.IsNetwork,
            DurationMilliseconds = item.DurationMilliseconds,
            IsEnhancedQueued = item.IsEnhancedQueued,
            IsEnhancedFolderStart = item.IsEnhancedFolderStart,
            EnhancedFolderTitle = item.EnhancedFolderTitle,
            IsManualQueueItem = item.IsManualQueueItem
        }).ToArray();
        var currentIndex = _currentIndex;
        var playlistVisible = _playlistVisible;
        var currentLocation = _currentMedia?.Location ?? _lastMediaLocation;
        var currentPosition = _currentMedia is not null
            ? Math.Max(0, _mediaPlayer.Time)
            : _lastMediaPositionMilliseconds;

        RestoreSession(imported);
        RestoreSavedPlaylist(currentQueue);
        var restoredCurrentPair = Playlist
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => string.Equals(pair.item.Location, currentLocation,
                StringComparison.OrdinalIgnoreCase));
        _currentIndex = currentIndex >= 0 && currentIndex < Playlist.Count
            ? currentIndex
            : restoredCurrentPair.item is not null ? restoredCurrentPair.index : -1;
        if (_currentIndex < 0 || _currentIndex >= Playlist.Count)
            _currentIndex = -1;
        _lastMediaLocation = currentLocation;
        _lastMediaPositionMilliseconds = currentPosition;
        _playlistVisible = playlistVisible;
        ApplyPlaylistVisibility();
        RefreshPlaylistCount();
        SelectCurrentPlaylistItem();
        PersistSession();
        dialog.SettingsImported = true;
        dialog.Close();
    }

    private void ShortcutsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_shortcutsDialog?.IsVisible == true)
        {
            _shortcutsDialog.Activate();
            BringAuxiliaryDialogAboveVideo(_shortcutsDialog);
            return;
        }

        var dialog = new ShortcutsDialog(_keyboardShortcuts, _rewindSeconds, _forwardSeconds)
        {
            Owner = this
        };
        _shortcutsDialog = dialog;
        dialog.ModifyRequested += (_, _) =>
            Dispatcher.BeginInvoke(() => OpenSettingsDialog("Shortcuts"), DispatcherPriority.ContextIdle);
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shortcutsDialog, dialog))
                _shortcutsDialog = null;
        };
        ShowAuxiliaryDialog(dialog);
    }

    private void AboutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_aboutDialog?.IsVisible == true)
        {
            _aboutDialog.Activate();
            BringAuxiliaryDialogAboveVideo(_aboutDialog);
            return;
        }

        var dialog = new AboutDialog
        {
            Owner = this
        };
        _aboutDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_aboutDialog, dialog))
                _aboutDialog = null;
        };
        ShowAuxiliaryDialog(dialog);
    }

    private void ReportProblemMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        OpenExternalHelpLink("https://github.com/ImWellan/Fuse-Player/issues",
            "la page de signalement GitHub");

    private void WebsiteMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        OpenExternalHelpLink("https://github.com/ImWellan/Fuse-Player",
            "le site web de Fuze");

    private void OpenExternalHelpLink(string url, string targetName)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            ShowToast(LocalizationService.Format("Impossible d’ouvrir {0}", targetName));
        }
    }

    private void TrackSynchronizationMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        OpenTrackSynchronization(TrackSyncTarget.All);

    private void AudioSynchronizationMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        OpenTrackSynchronization(TrackSyncTarget.Audio);

    private void VideoSynchronizationMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        OpenTrackSynchronization(TrackSyncTarget.Video);

    private void SubtitleSynchronizationMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        OpenTrackSynchronization(TrackSyncTarget.Subtitle);

    private void OpenTrackSynchronization(TrackSyncTarget target)
    {
        if (_currentIndex < 0 || _currentMedia is null)
        {
            ShowToast("Lancez d’abord un média");
            return;
        }

        if (_trackSyncDialog?.IsVisible == true && _trackSyncDialog.Target == target)
        {
            _trackSyncDialog.Activate();
            BringAuxiliaryDialogAboveVideo(_trackSyncDialog);
            return;
        }

        if (_trackSyncDialog?.IsVisible == true)
            _trackSyncDialog.Close();

        var videoTracks = _mediaPlayer.VideoTrackDescription;
        var audioTracks = _mediaPlayer.AudioTrackDescription;
        var subtitleTracks = _mediaPlayer.SpuDescription;
        var hasVideo = _mediaPlayer.VideoTrackCount > 0;
        var hasAudio = _mediaPlayer.AudioTrackCount > 0;
        var hasSubtitles = Math.Max(0, _mediaPlayer.SpuCount - 1) > 0;
        var synchronization = new TrackSynchronization(
            _videoSyncMilliseconds, _audioSyncMilliseconds, _subtitleSyncMilliseconds);

        var dialog = new TrackSyncDialog(
            synchronization,
            GetActiveTrackLabel(videoTracks, _mediaPlayer.VideoTrack, "Vidéo principale"),
            GetActiveTrackLabel(audioTracks, _mediaPlayer.AudioTrack, "Aucune piste sélectionnée"),
            GetActiveTrackLabel(subtitleTracks, _mediaPlayer.Spu, "Sous-titres désactivés"),
            hasVideo, hasAudio, hasSubtitles,
            target,
            ApplyTrackSynchronization)
        {
            Owner = this
        };
        _trackSyncDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trackSyncDialog, dialog))
                _trackSyncDialog = null;
        };
        ShowAuxiliaryDialog(dialog);
    }

    private void ResetTrackSynchronizationMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentMedia is null)
        {
            ShowToast(LocalizationService.Get("Aucun média ouvert"));
            return;
        }

        var reset = new TrackSynchronization(0, 0, 0);
        if (!ApplyTrackSynchronization(reset))
        {
            ShowToast(LocalizationService.Get("Impossible de réinitialiser la synchronisation"));
            return;
        }

        _trackSyncDialog?.SetSynchronization(reset);
        ShowToast(LocalizationService.Get("Synchronisation réinitialisée"));
    }

    private bool ApplyTrackSynchronization(TrackSynchronization synchronization)
    {
        if (_currentIndex < 0 || _currentMedia is null ||
            !_mediaPlayer.SetTrackSynchronization(
                synchronization.VideoMilliseconds,
                synchronization.AudioMilliseconds,
                synchronization.SubtitleMilliseconds))
            return false;

        _videoSyncMilliseconds = synchronization.VideoMilliseconds;
        _audioSyncMilliseconds = synchronization.AudioMilliseconds;
        _subtitleSyncMilliseconds = synchronization.SubtitleMilliseconds;
        SaveCurrentMediaPlaybackPreferences();
        return true;
    }

    private void ResetTrackSynchronizationForMediaChange()
    {
        _videoSyncMilliseconds = 0;
        _audioSyncMilliseconds = _defaultAudioDelayMilliseconds;
        _subtitleSyncMilliseconds = 0;
        _mediaPlayer.SetTrackSynchronization(0, _audioSyncMilliseconds, 0);
        if (_trackSyncDialog is not { } dialog)
            return;

        _trackSyncDialog = null;
        if (dialog.IsVisible)
            dialog.Close();
    }

    private void ResetVideoPresentationForMediaChange()
    {
        _videoZoom = 0;
        _videoPanX = 0;
        _videoPanY = 0;
        _videoAspectOverride = "16:9";
        _mediaPlayer.SetVideoZoom(_videoZoom);
        _mediaPlayer.SetVideoPan(_videoPanX, _videoPanY);
        _mediaPlayer.SetVideoAspectRatio(_videoAspectOverride);
    }

    private void PrepareRememberedMediaPlaybackPreferences(string location)
    {
        _rememberedMediaPreferencesForCurrentMedia = null;
        _rememberedMediaTracksAppliedForCurrentMedia = false;
        if (!_rememberMediaSettings ||
            !_mediaPlaybackPreferences.TryGetValue(location, out var preferences))
        {
            if (_rememberMediaSettings)
            {
                _playbackRate = 1f;
                _speedIndex = Array.FindIndex(SpeedOptions,
                    option => Math.Abs(option.Rate - _playbackRate) < 0.0001f);
                _mediaPlayer.SetRate(_playbackRate);
                SpeedButton.Content = FormatPlaybackRate(_playbackRate);
            }
            return;
        }

        _rememberedMediaPreferencesForCurrentMedia = preferences;
        _playbackRate = Math.Clamp(preferences.PlaybackRate, 0.05f, 10f);
        _speedIndex = Array.FindIndex(SpeedOptions,
            option => Math.Abs(option.Rate - _playbackRate) < 0.0001f);
        _mediaPlayer.SetRate(_playbackRate);
        SpeedButton.Content = FormatPlaybackRate(_playbackRate);
        _videoZoom = Math.Clamp(preferences.VideoZoom, -2d, Math.Log(10d, 2d));
        _mediaPlayer.SetVideoZoom(_videoZoom);
        _videoSyncMilliseconds = Math.Clamp(preferences.VideoSyncMilliseconds, -30000, 30000);
        _audioSyncMilliseconds = Math.Clamp(preferences.AudioSyncMilliseconds, -30000, 30000);
        _subtitleSyncMilliseconds = Math.Clamp(preferences.SubtitleSyncMilliseconds, -30000, 30000);
        _mediaPlayer.SetTrackSynchronization(_videoSyncMilliseconds,
            _audioSyncMilliseconds, _subtitleSyncMilliseconds);
    }

    private bool TryApplyRememberedMediaTrackSelections()
    {
        if (!_rememberMediaSettings || _rememberedMediaTracksAppliedForCurrentMedia ||
            _rememberedMediaPreferencesForCurrentMedia is not { } preferences ||
            _currentMedia is null)
            return false;

        if (preferences.VideoTrackId is int videoId && videoId >= 0 &&
            !_mediaPlayer.VideoTrackDescription.Any(track => track.Id == videoId))
            return false;
        if (preferences.AudioTrackId is int audioId && audioId >= 0 &&
            !_mediaPlayer.AudioTrackDescription.Any(track => track.Id == audioId))
            return false;
        if (preferences.SubtitleTrackId is int subtitleId && subtitleId >= 0 &&
            !_mediaPlayer.SpuDescription.Any(track => track.Id == subtitleId))
            return false;

        _rememberedMediaTracksAppliedForCurrentMedia = true;
        if (preferences.VideoTrackId.HasValue)
            RequestVideoTrack(preferences.VideoTrackId.Value);
        if (preferences.AudioTrackId.HasValue)
            RequestAudioTrack(preferences.AudioTrackId.Value);
        if (preferences.SubtitleTrackId.HasValue)
            RequestSubtitleTrack(preferences.SubtitleTrackId.Value);
        return true;
    }

    private void SaveCurrentMediaPlaybackPreferences()
    {
        if (!_rememberMediaSettings || _currentMedia is null ||
            string.IsNullOrWhiteSpace(_currentMedia.Location))
            return;

        var location = _currentMedia.Location;
        _mediaPlaybackPreferences[location] = new MediaPlaybackPreferencesData
        {
            VideoTrackId = _requestedVideoTrackId ?? _mediaPlayer.VideoTrack,
            AudioTrackId = _requestedAudioTrackId ?? _mediaPlayer.AudioTrack,
            SubtitleTrackId = _requestedSubtitleTrackId ?? _mediaPlayer.Spu,
            PlaybackRate = _playbackRate,
            VideoZoom = _videoZoom,
            VideoSyncMilliseconds = _videoSyncMilliseconds,
            AudioSyncMilliseconds = _audioSyncMilliseconds,
            SubtitleSyncMilliseconds = _subtitleSyncMilliseconds,
            UpdatedUtc = DateTime.UtcNow
        };
        _rememberedMediaPreferencesForCurrentMedia = _mediaPlaybackPreferences[location];
        PruneMediaPlaybackPreferences();
    }

    private static string GetActiveTrackLabel(TrackDescription[] tracks, int activeId, string fallback)
    {
        var active = tracks.FirstOrDefault(track => track.Id == activeId);
        if (active is not null && !string.IsNullOrWhiteSpace(active.Name))
            return active.Name;
        return tracks.FirstOrDefault(track => track.Id >= 0)?.Name ??
               LocalizationService.Get(fallback);
    }

    private void VideoTracksMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        VideoTracksMenuItem.Items.Clear();
        var tracks = GetSelectableTracks(
            _mediaPlayer.VideoTrackDescription, Math.Max(0, _mediaPlayer.VideoTrackCount));
        if (tracks.Length == 0)
        {
            VideoTracksMenuItem.Items.Add(new MenuItem
            {
                Header = LocalizationService.Get("Aucune piste vidéo"),
                IsEnabled = false
            });
            return;
        }

        var activeId = _requestedVideoTrackId ?? _mediaPlayer.VideoTrack;
        for (var index = 0; index < tracks.Length; index++)
        {
            var track = tracks[index];
            var item = new MenuItem
            {
                Header = FormatTrackChoice(track, index + 1),
                IsCheckable = true,
                IsChecked = track.Id == activeId,
                Tag = track
            };
            item.Click += VideoTrackMenuItem_OnClick;
            VideoTracksMenuItem.Items.Add(item);
        }
    }

    private void VideoTrackMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TrackDescription track })
            return;

        if (RequestVideoTrack(track.Id))
            ShowToast(LocalizationService.Format("Vidéo • {0}", track.Name));
    }

    private void SubtitleTracksMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        SubtitleTracksMenuItem.Items.Clear();
        var activeId = _requestedSubtitleTrackId ?? _mediaPlayer.Spu;
        var disabledItem = new MenuItem
        {
            Header = LocalizationService.Get("Désactivés"),
            IsCheckable = true,
            IsChecked = activeId < 0,
            Tag = new TrackDescription(-1, "Désactivés")
        };
        disabledItem.Click += SubtitleTrackMenuItem_OnClick;
        SubtitleTracksMenuItem.Items.Add(disabledItem);

        var tracks = _mediaPlayer.SpuDescription.Where(track => track.Id >= 0).ToArray();
        if (tracks.Length == 0)
            return;

        SubtitleTracksMenuItem.Items.Add(new Separator());
        for (var index = 0; index < tracks.Length; index++)
        {
            var track = tracks[index];
            var item = new MenuItem
            {
                Header = FormatTrackChoice(track, index + 1),
                IsCheckable = true,
                IsChecked = track.Id == activeId,
                Tag = track
            };
            item.Click += SubtitleTrackMenuItem_OnClick;
            SubtitleTracksMenuItem.Items.Add(item);
        }
    }

    private void SubtitleTrackMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TrackDescription track })
            return;

        if (RequestSubtitleTrack(track.Id))
            ShowToast(track.Id < 0
                ? LocalizationService.Get("Sous-titres désactivés")
                : string.Format(CultureInfo.InvariantCulture,
                    LocalizationService.Get("Sous-titres • {0}"), track.Name));
    }

    private void VideoZoomMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        VideoZoomMenuItem.Items.Clear();
        foreach (var (value, label) in VideoZoomOptions)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = Math.Abs(value - _videoZoom) < 0.0001,
                Tag = value
            };
            item.Click += VideoZoomChoice_OnClick;
            VideoZoomMenuItem.Items.Add(item);
        }

        VideoZoomMenuItem.Items.Add(new Separator());
        var customZoomPercent = Math.Clamp(_customZoomPercent, 50, 1000);
        var customItem = new MenuItem
        {
            Header = string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Get("Personnaliser… ({0} %)"), customZoomPercent),
            ToolTip = LocalizationService.Get("Ouvre une interface pour saisir une valeur de 50 à 1 000 %.")
        };
        customItem.Click += VideoZoomCustomizeMenuItem_OnClick;
        VideoZoomMenuItem.Items.Add(customItem);
    }

    private void VideoZoomCustomizeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_videoZoomDialog?.IsVisible == true)
        {
            _videoZoomDialog.Activate();
            BringAuxiliaryDialogAboveVideo(_videoZoomDialog);
            return;
        }

        var dialog = new VideoZoomDialog(_customZoomPercent) { Owner = this };
        _videoZoomDialog = dialog;
        dialog.Applied += (_, _) => ApplyCustomZoom(dialog.ZoomPercent);
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_videoZoomDialog, dialog))
                _videoZoomDialog = null;
        };
        ShowAuxiliaryDialog(dialog);
    }

    private void VideoZoomChoice_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: double zoom } || !EnsureVideoIsOpen())
            return;
        if (!_mediaPlayer.SetVideoZoom(zoom))
        {
            ShowToast("Impossible de modifier le zoom");
            return;
        }

        _videoZoom = zoom;
        SaveCurrentMediaPlaybackPreferences();
        var label = VideoZoomOptions.FirstOrDefault(option => Math.Abs(option.Value - zoom) < 0.0001).Label;
        if (string.IsNullOrWhiteSpace(label))
            label = LocalizationService.Format("Personnalisé ({0} %)",
                Math.Clamp(_customZoomPercent, 50, 1000));
        ShowToast(LocalizationService.Format("Zoom vidéo • {0}", label));
    }

    private static double ZoomPercentToMpv(int percent) =>
        Math.Log(Math.Clamp(percent, 25, 1000) / 100d, 2d);

    private void VideoAspectMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateVideoValueMenu(VideoAspectMenuItem, VideoAspectOptions, _videoAspectOverride,
            VideoAspectChoice_OnClick);
        VideoAspectMenuItem.Items.Add(new Separator());
        var customItem = new MenuItem
        {
            Header = LocalizationService.Format("Personnaliser… ({0})", _customAspectRatio),
            ToolTip = LocalizationService.Get("Ouvre une interface pour saisir la largeur et la hauteur du format.")
        };
        customItem.Click += VideoAspectCustomizeMenuItem_OnClick;
        VideoAspectMenuItem.Items.Add(customItem);
    }

    private void VideoAspectCustomizeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_videoAspectRatioDialog?.IsVisible == true)
        {
            _videoAspectRatioDialog.Activate();
            BringAuxiliaryDialogAboveVideo(_videoAspectRatioDialog);
            return;
        }

        var dialog = new VideoAspectRatioDialog(_customAspectRatio) { Owner = this };
        _videoAspectRatioDialog = dialog;
        dialog.Applied += (_, _) => ApplyCustomAspectRatio(dialog.AspectRatio);
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_videoAspectRatioDialog, dialog))
                _videoAspectRatioDialog = null;
        };
        ShowAuxiliaryDialog(dialog);
    }

    private void ApplyCustomZoom(int percent)
    {
        _customZoomPercent = Math.Clamp(percent, 50, 1000);
        var zoom = ZoomPercentToMpv(_customZoomPercent);
        if (EnsureVideoIsOpen() && _mediaPlayer.SetVideoZoom(zoom))
        {
            _videoZoom = zoom;
            SaveCurrentMediaPlaybackPreferences();
        }

        PersistSession();
        ShowToast(LocalizationService.Format("Zoom vidéo • Personnalisé ({0} %)", _customZoomPercent));
    }

    private void ApplyCustomAspectRatio(string aspectRatio)
    {
        _customAspectRatio = NormalizeCustomAspectRatio(aspectRatio);
        if (EnsureVideoIsOpen() && _mediaPlayer.SetVideoAspectRatio(_customAspectRatio))
        {
            _videoAspectOverride = _customAspectRatio;
            SaveCurrentMediaPlaybackPreferences();
        }

        PersistSession();
        ShowToast(LocalizationService.Format("Format d’image • Personnalisé ({0})", _customAspectRatio));
    }

    private void VideoAspectChoice_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string aspect } || !EnsureVideoIsOpen())
            return;
        if (!_mediaPlayer.SetVideoAspectRatio(aspect))
        {
            ShowToast("Impossible de modifier le format d’image");
            return;
        }

        _videoAspectOverride = aspect;
        var label = VideoAspectOptions.First(option => option.Value == aspect).Label;
        ShowToast(LocalizationService.Format("Format d’image • {0}", label));
    }

    private static void PopulateVideoValueMenu(MenuItem menu,
        IEnumerable<(string Value, string Label)> options, string selected,
        RoutedEventHandler clickHandler)
    {
        menu.Items.Clear();
        foreach (var (value, label) in options)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = value.Equals(selected, StringComparison.OrdinalIgnoreCase),
                Tag = value
            };
            item.Click += clickHandler;
            menu.Items.Add(item);
        }
    }

    private bool EnsureVideoIsOpen()
    {
        if (_currentIndex >= 0 && _currentMedia is not null && _mediaPlayer.VideoTrackCount > 0)
            return true;

        ShowToast("Aucune vidéo ouverte");
        return false;
    }

    private static string FormatTrackChoice(TrackDescription track, int position)
    {
        var name = track.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) ||
            name.Equals($"Piste {track.Id}", StringComparison.OrdinalIgnoreCase))
            return LocalizationService.Format("Piste {0}", position);
        return LocalizationService.Format("Piste {0}  ·  {1}", position, name);
    }

    private void AudioTracksMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        AudioTracksMenuItem.Items.Clear();
        var tracks = GetSelectableTracks(
            _mediaPlayer.AudioTrackDescription, Math.Max(0, _mediaPlayer.AudioTrackCount));
        if (tracks.Length == 0)
        {
            AudioTracksMenuItem.Items.Add(new MenuItem
            {
                Header = LocalizationService.Get("Aucune piste audio"),
                IsEnabled = false
            });
            return;
        }

        var activeId = _requestedAudioTrackId ?? _mediaPlayer.AudioTrack;
        for (var index = 0; index < tracks.Length; index++)
        {
            var track = tracks[index];
            var item = new MenuItem
            {
                Header = FormatTrackChoice(track, index + 1),
                IsCheckable = true,
                IsChecked = track.Id == activeId,
                Tag = track
            };
            item.Click += AudioTrackMenuItem_OnClick;
            AudioTracksMenuItem.Items.Add(item);
        }
    }

    private void AudioTrackMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TrackDescription track })
            return;

        if (RequestAudioTrack(track.Id))
            ShowToast(LocalizationService.Format("Audio • {0}", track.Name));
    }

    private void AudioDevicesMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e) =>
        RefreshAudioDevicesMenu();

    private void RefreshAudioDevicesMenu()
    {
        AudioDevicesMenuItem.Items.Clear();
        var devices = GetAvailableAudioDevices();
        var engineSelection = _mediaPlayer.AudioDevice;
        if (!string.IsNullOrWhiteSpace(engineSelection))
            _selectedAudioDevice = engineSelection;

        var adaptiveItem = new MenuItem
        {
            Header = LocalizationService.Get("Mode audio adaptatif"),
            ToolTip = LocalizationService.Get("Utilise automatiquement le périphérique associé à l’écran actif"),
            IsCheckable = true,
            IsChecked = _adaptiveAudioModeEnabled,
            Tag = AdaptiveAudioDeviceMenuTag
        };
        adaptiveItem.Click += AudioDeviceMenuItem_OnClick;
        AudioDevicesMenuItem.Items.Add(adaptiveItem);
        AudioDevicesMenuItem.Items.Add(new Separator());

        foreach (var device in devices)
            AddAudioDeviceMenuItem(device);

        if (devices.Length == 1)
        {
            AudioDevicesMenuItem.Items.Add(new Separator());
            AudioDevicesMenuItem.Items.Add(new MenuItem
            {
                Header = LocalizationService.Get("Aucun autre périphérique détecté"),
                IsEnabled = false
            });
        }
    }

    private AudioDeviceDescription[] GetAvailableAudioDevices()
    {
        var devices = _mediaPlayer.AudioDeviceDescriptions
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return
        [
            new AudioDeviceDescription("auto", LocalizationService.Get("Par défaut (Windows)")),
            .. devices.Where(device =>
                !device.Name.Equals("auto", StringComparison.OrdinalIgnoreCase))
        ];
    }

    private void AddAudioDeviceMenuItem(AudioDeviceDescription device)
    {
        var item = new MenuItem
        {
            Header = device.Description,
                ToolTip = device.Name.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? LocalizationService.Get("Suit automatiquement le périphérique choisi dans Windows")
                : device.Name,
            IsCheckable = true,
            IsChecked = !_adaptiveAudioModeEnabled &&
                        device.Name.Equals(_selectedAudioDevice, StringComparison.OrdinalIgnoreCase),
            Tag = device
        };
        item.Click += AudioDeviceMenuItem_OnClick;
        AudioDevicesMenuItem.Items.Add(item);
    }

    private void AudioDeviceMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
            return;

        if (string.Equals(item.Tag as string, AdaptiveAudioDeviceMenuTag,
                StringComparison.OrdinalIgnoreCase))
        {
            _adaptiveAudioModeEnabled = true;
            _lastAdaptiveAudioDisplayId = null;
            var applied = ApplyAdaptiveAudioDeviceForCurrentDisplay(force: true);
            PersistSession();
            ShowToast(applied
                ? LocalizationService.Get("Audio adaptatif activé")
                : LocalizationService.Get("Audio adaptatif activé — sera appliqué à l’écran actif"));
            return;
        }

        if (item.Tag is not AudioDeviceDescription device)
            return;

        if (!_mediaPlayer.SetAudioDevice(device.Name))
        {
            ShowToast(LocalizationService.Get("Impossible d’utiliser ce périphérique audio"));
            return;
        }

        _selectedAudioDevice = device.Name;
        if (_adaptiveAudioModeEnabled)
        {
            // Un choix explicite dans le menu de lecture doit avoir priorité
            // sur l’association automatique par écran. Les associations
            // restent enregistrées, mais le mode adaptatif est désactivé
            // jusqu’à ce que l’utilisateur le réactive dans les paramètres.
            _adaptiveAudioModeEnabled = false;
            _lastAdaptiveAudioDisplayId = null;
            ShowToast(string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Get("Sortie audio • {0} (mode adaptatif désactivé)"),
                device.Description));
        }
        else
        {
            ShowToast(string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Get("Sortie audio • {0}"), device.Description));
        }
        PersistSession();
    }

    private void AudioModesMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        AudioModesMenuItem.Items.Clear();
        foreach (var (mode, label) in AudioModeOptions)
        {
            var item = new MenuItem
            {
                Header = LocalizationService.Get(label),
                IsCheckable = true,
                IsChecked = mode == _audioOutputMode,
                Tag = mode
            };
            item.Click += AudioModeMenuItem_OnClick;
            AudioModesMenuItem.Items.Add(item);
        }
    }

    private void AudioModeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AudioOutputMode mode })
            return;

        if (!_mediaPlayer.SetAudioOutputMode(mode))
        {
            ShowToast(LocalizationService.Get("Impossible d’appliquer ce mode audio"));
            return;
        }

        _audioOutputMode = mode;
        var label = AudioModeOptions.First(option => option.Mode == mode).Label;
        PersistSession();
        ShowToast(string.Format(CultureInfo.InvariantCulture,
            LocalizationService.Get("Canaux audio • {0}"), LocalizationService.Get(label)));
    }

    private void AudioProcessingMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        AudioProcessingMenuItem.Items.Clear();
        for (var index = 0; index < AudioTreatmentOptions.Length; index++)
        {
            if (index == 2)
                AudioProcessingMenuItem.Items.Add(new Separator());

            var (mode, label, description) = AudioTreatmentOptions[index];
            var item = new MenuItem
            {
                Header = LocalizationService.Get(label),
                ToolTip = LocalizationService.Get(description),
                IsCheckable = true,
                IsChecked = _audioTreatmentMode.HasFlag(mode),
                Tag = mode
            };
            item.Click += AudioTreatmentMenuItem_OnClick;
            AudioProcessingMenuItem.Items.Add(item);
        }
    }

    private void AudioTreatmentMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AudioTreatmentMode mode } item)
            return;

        var requested = _audioTreatmentMode;
        if (item.IsChecked)
        {
            if (mode == AudioTreatmentMode.HeadphoneBinaural)
                requested &= ~AudioTreatmentMode.SurroundDownmix;
            else if (mode == AudioTreatmentMode.SurroundDownmix)
                requested &= ~AudioTreatmentMode.HeadphoneBinaural;
            requested |= mode;
        }
        else
        {
            requested &= ~mode;
        }

        if (!_mediaPlayer.SetAudioTreatmentMode(requested))
        {
            item.IsChecked = _audioTreatmentMode.HasFlag(mode);
            ShowToast(LocalizationService.Get("Impossible d’appliquer ce traitement audio"));
            return;
        }

        _audioTreatmentMode = requested;
        PersistSession();
        var label = AudioTreatmentOptions.First(option => option.Mode == mode).Label;
        ShowToast(LocalizationService.Format(item.IsChecked ? "{0} • activé" : "{0} • désactivé", label));
    }

    private void UpdateAudioMenuAvailability()
    {
        if (AudioModesMenuItem is null || AudioProcessingMenuItem is null)
            return;

        AudioModesMenuItem.IsEnabled = !_audioPassthrough;
        AudioProcessingMenuItem.IsEnabled = !_audioPassthrough;
        AudioModesMenuItem.ToolTip = _audioPassthrough
            ? LocalizationService.Get("Désactivez le passthrough pour modifier les canaux")
            : null;
        AudioProcessingMenuItem.ToolTip = _audioPassthrough
            ? LocalizationService.Get("Désactivez le passthrough pour utiliser les traitements audio")
            : null;
    }

    private void AudioTracksButton_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = CreateTracksMenu("Aucune piste audio", _mediaPlayer.AudioTrackDescription,
            _requestedAudioTrackId ?? _mediaPlayer.AudioTrack, RequestAudioTrack);
        OpenContextMenu(menu, AudioTracksButton);
    }

    private void AudioTracksButton_OnCycleClick(object sender, RoutedEventArgs e) => CycleAudioTrack(1);

    private void AudioTracksButton_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        if (!_mouseWheelAudioTracksEnabled)
        {
            e.Handled = true;
            return;
        }

        CycleAudioTrack(e.Delta > 0 ? -1 : 1);
        RestartControlsHideTimer();
        e.Handled = true;
    }

    private void AudioTracksButton_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        AudioTracksButton_OnClick(sender, e);
        e.Handled = true;
    }

    private void SubtitleTracksButton_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = CreateTracksMenu("Aucun sous-titre", _mediaPlayer.SpuDescription,
            _requestedSubtitleTrackId ?? _mediaPlayer.Spu, RequestSubtitleTrack);
        menu.Items.Add(CreateFuzeContextMenuSeparator());
        var add = new MenuItem { Header = LocalizationService.Get("Ajouter un fichier de sous-titres…") };
        add.Click += AddSubtitleButton_OnClick;
        menu.Items.Add(add);
        OpenContextMenu(menu, SubtitleTracksButton);
    }

    private void SubtitleTracksButton_OnCycleClick(object sender, RoutedEventArgs e) => CycleSubtitleTrack(1);

    private void SubtitleTracksButton_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        if (!_mouseWheelSubtitleTracksEnabled)
        {
            e.Handled = true;
            return;
        }

        CycleSubtitleTrack(e.Delta > 0 ? -1 : 1);
        RestartControlsHideTimer();
        e.Handled = true;
    }

    private void SubtitleTracksButton_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        SubtitleTracksButton_OnClick(sender, e);
        e.Handled = true;
    }

    private void UpdateTrackIndicators()
    {
        var audioActiveId = _requestedAudioTrackId ?? _mediaPlayer.AudioTrack;
        var subtitleActiveId = _requestedSubtitleTrackId ?? _mediaPlayer.Spu;
        var (audioCurrent, audioTotal) = GetTrackPosition(
            _mediaPlayer.AudioTrackDescription, audioActiveId,
            Math.Max(0, _mediaPlayer.AudioTrackCount));
        var (subtitleCurrent, subtitleTotal) = GetTrackPosition(
            _mediaPlayer.SpuDescription, subtitleActiveId,
            Math.Max(0, _mediaPlayer.SpuCount - 1));

        AudioTrackCountText.Text = $"{audioCurrent}/{audioTotal}";
        SubtitleTrackCountText.Text = $"{subtitleCurrent}/{subtitleTotal}";
    }

    private void HandleTrackSelectionChanged()
    {
        if (_requestedVideoTrackId == _mediaPlayer.VideoTrack)
            _requestedVideoTrackId = null;
        if (_requestedAudioTrackId == _mediaPlayer.AudioTrack)
            _requestedAudioTrackId = null;
        if (_requestedSubtitleTrackId == _mediaPlayer.Spu)
            _requestedSubtitleTrackId = null;

        UpdateTrackIndicators();
        SaveCurrentMediaPlaybackPreferences();
    }

    private void TryApplyPreferredAudioTrack()
    {
        if (_preferredAudioAppliedForCurrentMedia || _currentMedia is null)
            return;

        if (_disableAudioByDefault)
        {
            _preferredAudioAppliedForCurrentMedia = true;
            RequestAudioTrack(-1);
            return;
        }

        if (!_autoSelectPreferredAudio || _preferredAudioProfile == "disabled")
            return;

        var tracks = _currentMedia.Tracks
            .Where(track => track.TrackType == MpvTrackType.Audio)
            .ToArray();
        if (tracks.Length == 0)
            return;

        _preferredAudioAppliedForCurrentMedia = true;
        var match = tracks
            .Select((track, index) => new
            {
                Track = track,
                Index = index,
                Score = _preferredAudioProfile == "custom"
                    ? ScoreCustomTrackTitle(track, _preferredAudioTitlePriorities)
                    : ScorePreferredAudioTrack(track, _preferredAudioProfile)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
        if (match is not null && match.Track.Id != _mediaPlayer.AudioTrack)
            RequestAudioTrack(match.Track.Id);
    }

    private static int ScorePreferredAudioTrack(MpvMediaTrack track, string profile)
    {
        var language = NormalizeSearchText(track.Language);
        var title = NormalizeSearchText(track.Description);
        return profile switch
        {
            "vfq" when language is "fr-ca" or "fra-ca" or "fre-ca" => 120,
            "vfq" when title.Contains("vfq", StringComparison.Ordinal) ||
                       title.Contains("quebec", StringComparison.Ordinal) ||
                       title.Contains("canad", StringComparison.Ordinal) => 105,
            "vfq" when language.StartsWith("fr", StringComparison.Ordinal) => 35,
            "vff" when language is "fr-fr" or "fra-fr" or "fre-fr" => 120,
            "vff" when title.Contains("vff", StringComparison.Ordinal) ||
                       title.Contains("france", StringComparison.Ordinal) => 105,
            "vff" when language.StartsWith("fr", StringComparison.Ordinal) => 35,
            "vo" when title.Equals("vo", StringComparison.Ordinal) ||
                      title.Contains("original", StringComparison.Ordinal) ||
                      title.Contains("version originale", StringComparison.Ordinal) => 120,
            "fr" when language.StartsWith("fr", StringComparison.Ordinal) ||
                      language.StartsWith("fra", StringComparison.Ordinal) ||
                      language.StartsWith("fre", StringComparison.Ordinal) => 100,
            "en" when language.StartsWith("en", StringComparison.Ordinal) ||
                      language.StartsWith("eng", StringComparison.Ordinal) => 100,
            "ja" when language.StartsWith("ja", StringComparison.Ordinal) ||
                      language.StartsWith("jpn", StringComparison.Ordinal) => 100,
            _ => 0
        };
    }

    private static string NormalizePreferredAudioProfile(string? profile)
    {
        var normalized = profile?.Trim().ToLowerInvariant();
        return normalized is "vfq" or "vff" or "vo" or "fr" or "en" or "ja" or "custom"
            ? normalized
            : "disabled";
    }

    private void TryApplyPreferredSubtitleTrack()
    {
        if (_preferredSubtitleAppliedForCurrentMedia || _currentMedia is null)
            return;

        if (_disableSubtitlesByDefault)
        {
            _preferredSubtitleAppliedForCurrentMedia = true;
            RequestSubtitleTrack(-1);
            return;
        }

        if (!_autoSelectPreferredSubtitle || _preferredSubtitleProfile == "default")
            return;

        var tracks = _currentMedia.Tracks
            .Where(track => track.TrackType == MpvTrackType.Text)
            .ToArray();
        if (tracks.Length == 0)
            return;

        _preferredSubtitleAppliedForCurrentMedia = true;
        var match = tracks
            .Select((track, index) => new
            {
                Track = track,
                Index = index,
                Score = _preferredSubtitleProfile == "custom"
                    ? ScoreCustomTrackTitle(track, _preferredSubtitleTitlePriorities)
                    : ScorePreferredSubtitleTrack(track, _preferredSubtitleProfile) +
                      (_preferSdhSubtitles && IsSdhSubtitleTrack(track) ? 500 : 0)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
        if (match is not null && match.Track.Id != _mediaPlayer.Spu)
            RequestSubtitleTrack(match.Track.Id);
    }

    private static int ScorePreferredSubtitleTrack(MpvMediaTrack track, string profile)
    {
        var language = NormalizeSearchText(track.Language);
        var title = NormalizeSearchText(track.Description);
        var forcedBonus = track.IsForced || title.Contains("force", StringComparison.Ordinal) ? 15 : 0;
        return profile switch
        {
            "forced" when track.IsForced || title.Contains("force", StringComparison.Ordinal) => 150,
            "vfq" when language is "fr-ca" or "fra-ca" or "fre-ca" => 120 + forcedBonus,
            "vfq" when title.Contains("vfq", StringComparison.Ordinal) ||
                       title.Contains("quebec", StringComparison.Ordinal) ||
                       title.Contains("canad", StringComparison.Ordinal) => 105 + forcedBonus,
            "vfq" when language.StartsWith("fr", StringComparison.Ordinal) => 35 + forcedBonus,
            "vff" when language is "fr-fr" or "fra-fr" or "fre-fr" => 120 + forcedBonus,
            "vff" when title.Contains("vff", StringComparison.Ordinal) ||
                       title.Contains("france", StringComparison.Ordinal) => 105 + forcedBonus,
            "vff" when language.StartsWith("fr", StringComparison.Ordinal) => 35 + forcedBonus,
            "fr" when language.StartsWith("fr", StringComparison.Ordinal) ||
                      language.StartsWith("fra", StringComparison.Ordinal) ||
                      language.StartsWith("fre", StringComparison.Ordinal) => 100 + forcedBonus,
            "en" when language.StartsWith("en", StringComparison.Ordinal) ||
                      language.StartsWith("eng", StringComparison.Ordinal) => 100 + forcedBonus,
            "ja" when language.StartsWith("ja", StringComparison.Ordinal) ||
                      language.StartsWith("jpn", StringComparison.Ordinal) => 100 + forcedBonus,
            _ => 0
        };
    }

    private static bool IsSdhSubtitleTrack(MpvMediaTrack track)
    {
        var title = NormalizeSearchText(track.Description);
        return title.Contains("sdh", StringComparison.Ordinal) ||
               title.Contains("cc", StringComparison.Ordinal) ||
               title.Contains("malentendant", StringComparison.Ordinal) ||
               title.Contains("hearing", StringComparison.Ordinal);
    }

    private static string NormalizePreferredSubtitleProfile(string? profile)
    {
        var normalized = profile?.Trim().ToLowerInvariant();
        return normalized is "vfq" or "vff" or "fr" or "en" or "ja" or "forced" or "custom"
            ? normalized
            : "default";
    }

    private static int ScoreCustomTrackTitle(MpvMediaTrack track, IReadOnlyList<string> priorities)
    {
        var title = NormalizeSearchText(track.Description);
        if (title.Length == 0)
            return 0;

        for (var index = 0; index < priorities.Count; index++)
        {
            var wanted = NormalizeSearchText(priorities[index]);
            if (wanted.Length == 0 || !title.Contains(wanted, StringComparison.Ordinal))
                continue;

            return (priorities.Count - index) * 1000 +
                   (title.Equals(wanted, StringComparison.Ordinal) ? 100 : 50);
        }

        return 0;
    }

    private static string[] NormalizeTitlePriorities(IEnumerable<string>? priorities) =>
        (priorities ?? [])
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Take(50)
        .Cast<string>()
        .ToArray();

    private SubtitleRenderPreferences BuildSubtitlePreferences() => new(
        _subtitleEncoding,
        _subtitleFont,
        _subtitleFontSize,
        _subtitleTextColor,
        _subtitleBorderColor,
        _subtitleBorderSize,
        _subtitleShadow,
        _subtitleForcePosition,
        _subtitlePosition,
        _subtitleMarginX,
        _subtitleMarginY,
        _subtitleScaleWithWindow);

    private void ScheduleStartupTitleOverlay()
    {
        if (!_showOsd || !_startupTitleOverlayEnabled || _startupTitleShownForCurrentMedia ||
            _currentIndex < 0 || _currentIndex >= Playlist.Count)
            return;

        _startupTitleShownForCurrentMedia = true;
        _pendingStartupTitle = GetStartupTitleText();
        _startupTitleTimer.Stop();
        if (_startupTitleDelayMilliseconds == 0)
        {
            ShowStartupTitleOverlay();
            return;
        }

        _startupTitleTimer.Interval = TimeSpan.FromMilliseconds(_startupTitleDelayMilliseconds);
        _startupTitleTimer.Start();
    }

    private void ShowStartupTitleOverlay()
    {
        if (!_showOsd || !_startupTitleOverlayEnabled || string.IsNullOrWhiteSpace(_pendingStartupTitle) ||
            _currentIndex < 0)
            return;

        _mediaPlayer.ShowText(_pendingStartupTitle, _startupTitleDurationMilliseconds,
            new TitleOverlayPreferences(
                _startupTitlePosition,
                _startupTitleFont,
                _startupTitleFontSize,
                _startupTitleTextColor,
                _startupTitleBorderColor,
                _startupTitleBorderSize,
                _startupTitleShadow,
                _startupTitleMarginX,
                _startupTitleMarginY,
                _startupTitleScaleWithWindow));
    }

    private string GetStartupTitleText()
    {
        if (_currentIndex < 0 || _currentIndex >= Playlist.Count)
            return string.Empty;

        var fileTitle = Playlist[_currentIndex].Title;
        var title = fileTitle;
        if (_preferOriginalTitleForStartup)
        {
            var originalTitle = _mediaPlayer.MetadataTitle;
            if (!string.IsNullOrWhiteSpace(originalTitle))
                title = originalTitle;
        }

        if (_enhancedFolderShowNameEnabled &&
            string.Equals(_enhancedFolderTitleLocation, Playlist[_currentIndex].Location,
                StringComparison.OrdinalIgnoreCase))
        {
            _enhancedFolderTitleLocation = null;
            var folder = Path.GetFileName(Path.GetDirectoryName(Playlist[_currentIndex].Location)
                ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folder))
                return $"{folder} — {title}";
        }

        return title;
    }

    private static string NormalizeScreenPosition(string? position, string fallback) =>
        position?.Trim().ToLowerInvariant() switch
        {
            "top-left" => "top-left",
            "top-center" => "top-center",
            "top-right" => "top-right",
            "center-left" => "center-left",
            "center" or "center-center" => "center-center",
            "center-right" => "center-right",
            "bottom-left" => "bottom-left",
            "bottom-center" => "bottom-center",
            "bottom-right" => "bottom-right",
            _ => fallback
        };

    private static string NormalizeSubtitleColor(string? color, string fallback)
    {
        var value = color?.Trim();
        if (value is { Length: 9 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit))
            return value.ToUpperInvariant();
        return fallback;
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().ToLowerInvariant().Replace('_', '-').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static (int Current, int Total) GetTrackPosition(
        TrackDescription[]? descriptions, int activeId, int expectedCount)
    {
        var tracks = GetSelectableTracks(descriptions, expectedCount);
        var index = Array.FindIndex(tracks, track => track.Id == activeId);
        return (index >= 0 ? index + 1 : 0, tracks.Length);
    }

    private static TrackDescription[] GetSelectableTracks(
        TrackDescription[]? descriptions, int expectedCount)
    {
        if (descriptions is null || expectedCount <= 0)
            return [];

        var candidates = descriptions.Where(track => track.Id >= 0).ToArray();
        return candidates.Length > expectedCount
            ? candidates[^expectedCount..]
            : candidates;
    }

    private void CycleAudioTrack(int direction)
    {
        var tracks = GetSelectableTracks(
            _mediaPlayer.AudioTrackDescription, Math.Max(0, _mediaPlayer.AudioTrackCount));
        if (tracks.Length == 0)
        {
            ShowToast("Aucune piste audio");
            return;
        }

        var activeId = _requestedAudioTrackId ?? _mediaPlayer.AudioTrack;
        var current = Array.FindIndex(tracks, track => track.Id == activeId);
        var nextIndex = current < 0
            ? direction >= 0 ? 0 : tracks.Length - 1
            : (current + Math.Sign(direction) + tracks.Length) % tracks.Length;
        var next = tracks[nextIndex];
        if (RequestAudioTrack(next.Id))
            ShowToast(LocalizationService.Format("Audio • {0}", next.Name));
    }

    private void CycleSubtitleTrack(int direction)
    {
        var tracks = GetSelectableTracks(
            _mediaPlayer.SpuDescription, Math.Max(0, _mediaPlayer.SpuCount - 1));
        if (tracks.Length == 0)
        {
            ShowToast("Aucun sous-titre");
            return;
        }

        var activeId = _requestedSubtitleTrackId ?? _mediaPlayer.Spu;
        var current = Array.FindIndex(tracks, track => track.Id == activeId);
        var nextIndex = direction >= 0
            ? current + 1 < tracks.Length ? current + 1 : -1
            : current < 0 ? tracks.Length - 1 : current > 0 ? current - 1 : -1;

        if (nextIndex >= 0)
        {
            if (RequestSubtitleTrack(tracks[nextIndex].Id))
                ShowToast(LocalizationService.Format("Sous-titres • {0}", tracks[nextIndex].Name));
        }
        else
        {
            if (RequestSubtitleTrack(-1))
                ShowToast("Sous-titres désactivés");
        }
    }

    private bool RequestAudioTrack(int id)
    {
        _requestedAudioTrackId = id;
        UpdateTrackIndicators();
        if (_mediaPlayer.SetAudioTrack(id))
        {
            SaveCurrentMediaPlaybackPreferences();
            return true;
        }

        _requestedAudioTrackId = null;
        UpdateTrackIndicators();
        ShowToast("Impossible de changer la piste audio");
        return false;
    }

    private bool RequestVideoTrack(int id)
    {
        _requestedVideoTrackId = id;
        if (_mediaPlayer.SetVideoTrack(id))
        {
            SaveCurrentMediaPlaybackPreferences();
            return true;
        }

        _requestedVideoTrackId = null;
        ShowToast("Impossible de changer la piste vidéo");
        return false;
    }

    private bool RequestSubtitleTrack(int id)
    {
        _requestedSubtitleTrackId = id;
        UpdateTrackIndicators();
        if (_mediaPlayer.SetSpu(id))
        {
            SaveCurrentMediaPlaybackPreferences();
            return true;
        }

        _requestedSubtitleTrackId = null;
        UpdateTrackIndicators();
        ShowToast("Impossible de changer les sous-titres");
        return false;
    }

    private ContextMenu CreateTracksMenu(string emptyLabel, TrackDescription[]? tracks,
        int activeId, Func<int, bool> selectTrack)
    {
        var menu = CreateFuzeContextMenu();
        menu.MaxHeight = 420;

        if (tracks is null || tracks.Length == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = LocalizationService.Get(emptyLabel),
                IsEnabled = false
            });
            return menu;
        }

        foreach (var track in tracks)
        {
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(track.Name)
                    ? LocalizationService.Format("Piste {0}", track.Id)
                    : track.Name,
                IsCheckable = true,
                IsChecked = track.Id == activeId,
                Tag = track.Id
            };
            item.Click += (_, _) => selectTrack((int)item.Tag);
            menu.Items.Add(item);
        }

        return menu;
    }

    private ContextMenu CreateFuzeContextMenu() => new()
    {
        Style = (Style)FindResource("FuzeContextMenuStyle")
    };

    private Separator CreateFuzeContextMenuSeparator() => new()
    {
        Style = (Style)FindResource("FuzeContextMenuSeparatorStyle")
    };

    private void OpenContextMenu(ContextMenu menu, FrameworkElement target,
        PlacementMode placement = PlacementMode.Top)
    {
        if (menu.Style is null)
            menu.Style = (Style)FindResource("FuzeContextMenuStyle");

        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.Opened += ContextMenu_OnOpened;
        menu.Closed += ContextMenu_OnClosed;
        menu.IsOpen = true;
    }

    private void ContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        _openContextMenuCount++;
        _controlsHideTimer.Stop();
    }

    private void ContextMenu_OnClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Opened -= ContextMenu_OnOpened;
            menu.Closed -= ContextMenu_OnClosed;
        }

        _openContextMenuCount = Math.Max(0, _openContextMenuCount - 1);
        if (_openContextMenuCount == 0)
            RestartControlsHideTimer();
    }

    private void AddSubtitleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0)
        {
            ShowToast("Lancez d’abord une vidéo");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("Ajouter des sous-titres"),
            Filter = $"{LocalizationService.Get("Sous-titres")}|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx|{LocalizationService.Get("Tous les fichiers")}|*.*"
        };

        if (ShowModalDialog(() => dialog.ShowDialog(DialogOwnerWindow)) != true)
            return;

        var added = _mediaPlayer.AddSubtitle(dialog.FileName, true);
        ShowToast(added ? "Sous-titres ajoutés" : "Impossible d’ajouter ces sous-titres");
    }

    private void SnapshotButton_OnClick(object sender, RoutedEventArgs e) => TakeSnapshot();

    private void TakeSnapshot()
    {
        if (_currentIndex < 0)
        {
            ShowToast("Aucune image à capturer");
            return;
        }

        try
        {
            var directory = Path.Combine(_screenshotBaseDirectory, _screenshotFolderName);
            Directory.CreateDirectory(directory);
            var path = CreateSnapshotPath(directory);
            var success = _mediaPlayer.TakeSnapshot(path);
            if (!success)
                ShowToast("Échec de la capture");
            else if (_copyScreenshotsToClipboard)
                _ = CopySnapshotToClipboardAsync(path);
            else
                ShowToast(LocalizationService.Format("Capture enregistrée • {0}", Path.GetFileName(path)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               ArgumentException or NotSupportedException)
        {
            ShowToast("Impossible d’accéder au dossier de captures");
        }
    }

    private async Task CopySnapshotToClipboardAsync(string path)
    {
        for (var attempt = 0; attempt < 40 && !_isClosing; attempt++)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    await Task.Delay(50);
                    continue;
                }

                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                Clipboard.SetImage(image);
                ShowToast(LocalizationService.Format("Capture enregistrée et copiée • {0}", Path.GetFileName(path)));
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   COMException or NotSupportedException)
            {
                await Task.Delay(50);
            }
        }

        if (!_isClosing)
            ShowToast(LocalizationService.Format(
                "Capture enregistrée • copie impossible • {0}", Path.GetFileName(path)));
    }

    private string CreateSnapshotPath(string directory)
    {
        var extension = _screenshotFormat == "jpg" ? ".jpg" : ".png";
        var core = _screenshotSequentialNumbering
            ? GetNextSnapshotSequence(directory, extension).ToString("0000", CultureInfo.InvariantCulture)
            : DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        var affix = SanitizeFileNamePart(_screenshotAffixText);
        var stem = string.IsNullOrWhiteSpace(affix)
            ? core
            : _screenshotAffixMode == "suffix"
                ? $"{core}_{affix}"
                : $"{affix}_{core}";
        var path = Path.Combine(directory, stem + extension);
        if (!File.Exists(path))
            return path;

        for (var duplicate = 2; duplicate < 10000; duplicate++)
        {
            path = Path.Combine(directory, $"{stem}_{duplicate:00}{extension}");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    private int GetNextSnapshotSequence(string directory, string extension)
    {
        var affix = SanitizeFileNamePart(_screenshotAffixText);
        var prefix = _screenshotAffixMode == "prefix" && !string.IsNullOrWhiteSpace(affix)
            ? affix + "_"
            : string.Empty;
        var suffix = _screenshotAffixMode == "suffix" && !string.IsNullOrWhiteSpace(affix)
            ? "_" + affix
            : string.Empty;
        var maximum = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + extension,
                     SearchOption.TopDirectoryOnly))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var numberLength = stem.Length - prefix.Length - suffix.Length;
            if (numberLength <= 0)
                continue;
            var numberText = stem.Substring(prefix.Length, numberLength);
            if (int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                maximum = Math.Max(maximum, number);
        }

        return maximum == int.MaxValue ? 1 : maximum + 1;
    }

    private static string SanitizeFileNamePart(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            text = text.Replace(invalid, '_');
        return text.Trim(' ', '.');
    }

    private void PlaylistList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_playlistDragging && PlaylistList.SelectedItem is not null)
            PlaylistList.ScrollIntoView(PlaylistList.SelectedItem);
    }

    private void PlaylistList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _playlistDragStartPoint = e.GetPosition(PlaylistList);
        _playlistDragItem = GetPlaylistItemFromElement(e.OriginalSource as DependencyObject);
        if (_playlistDragItem?.IsManualQueueItem != true ||
            Playlist.IndexOf(_playlistDragItem) == _currentIndex)
            _playlistDragItem = null;
    }

    private void PlaylistList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_playlistDragging)
        {
            PersistSession();
            _playlistDragging = false;
            _playlistAutoScrollTimer.Stop();
            if (ReferenceEquals(Mouse.Captured, PlaylistList))
                Mouse.Capture(null);
        }
        _playlistDragItem = null;
    }

    private void PlaylistList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _playlistDragging = false;
            _playlistAutoScrollTimer.Stop();
            _playlistDragItem = null;
            return;
        }

        if (!_playlistDragging)
        {
            if (_playlistDragItem is null)
                return;

            var horizontal = Math.Abs(e.GetPosition(PlaylistList).X - _playlistDragStartPoint.X);
            var vertical = Math.Abs(e.GetPosition(PlaylistList).Y - _playlistDragStartPoint.Y);
            if (horizontal < SystemParameters.MinimumHorizontalDragDistance &&
                vertical < SystemParameters.MinimumVerticalDragDistance)
                return;

            _playlistDragging = true;
            Mouse.Capture(PlaylistList, CaptureMode.SubTree);
        }

        _playlistDragLastPoint = e.GetPosition(PlaylistList);
        MovePlaylistItemWithMouse(_playlistDragLastPoint);
        e.Handled = true;
    }

    private void MovePlaylistItemWithMouse(Point position)
    {
        var source = _playlistDragItem;
        if (source is null || !source.IsManualQueueItem)
            return;

        var sourceIndex = Playlist.IndexOf(source);
        if (sourceIndex < 0 || sourceIndex == _currentIndex)
            return;

        AutoScrollPlaylist(position);
        var targetIndex = GetPlaylistInsertionIndex(position);

        if (targetIndex > sourceIndex)
            targetIndex--;
        if (targetIndex == sourceIndex)
            return;

        var currentItem = _currentIndex >= 0 && _currentIndex < Playlist.Count
            ? Playlist[_currentIndex]
            : null;

        Playlist.RemoveAt(sourceIndex);
        targetIndex = Math.Clamp(targetIndex, 0, Playlist.Count);
        Playlist.Insert(targetIndex, source);
        _currentIndex = currentItem is null ? -1 : Playlist.IndexOf(currentItem);
        SynchronizeDisplayedPlaylistOrder();
        UpdatePlaylistCountText();
        PlaylistList.SelectedItem = source;
    }

    private void AutoScrollPlaylist(Point position)
    {
        var scrollViewer = FindScrollViewer(PlaylistList);
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
            return;

        var viewportY = position.Y;
        try
        {
            // La position de la souris est exprimée dans le ListBox, tandis
            // que le viewport peut avoir une marge interne différente. La
            // convertir dans le ScrollViewer rend les deux bords parfaitement
            // symétriques.
            viewportY = scrollViewer.PointFromScreen(PlaylistList.PointToScreen(position)).Y;
        }
        catch (InvalidOperationException)
        {
            // Pendant la virtualisation, le visuel peut momentanément ne pas
            // être relié à une source; la position du ListBox reste un bon
            // repli pour le prochain tick.
        }

        const double edge = 54;
        var activeEdge = Math.Min(edge, scrollViewer.ViewportHeight / 2d);
        var atEdge = viewportY < activeEdge ||
                     viewportY > scrollViewer.ViewportHeight - activeEdge;
        _playlistDragLastPoint = position;
        if (!atEdge)
        {
            _playlistAutoScrollTimer.Stop();
            return;
        }

        // Le tick applique le même déplacement dans les deux sens. La souris
        // ne détermine donc plus la vitesse en fonction de sa fréquence de
        // déplacement.
        if (!_playlistAutoScrollTimer.IsEnabled)
            _playlistAutoScrollTimer.Start();
    }

    private void ScrollPlaylistForDrag()
    {
        var scrollViewer = FindScrollViewer(PlaylistList);
        if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
            return;

        var viewportY = _playlistDragLastPoint.Y;
        try
        {
            viewportY = scrollViewer.PointFromScreen(
                PlaylistList.PointToScreen(_playlistDragLastPoint)).Y;
        }
        catch (InvalidOperationException)
        {
            // Voir le repli dans AutoScrollPlaylist.
        }

        const double edge = 54;
        var activeEdge = Math.Min(edge, scrollViewer.ViewportHeight / 2d);
        var direction = viewportY < activeEdge
            ? -1
            : viewportY > scrollViewer.ViewportHeight - activeEdge ? 1 : 0;
        if (direction == 0)
        {
            _playlistAutoScrollTimer.Stop();
            return;
        }

        // Le pas est réglable dans Paramètres > Interface > File des médias.
        // Il reste identique dans les deux directions pour un défilement
        // régulier en haut comme en bas de la liste.
        var step = Math.Clamp(_playlistScrollSpeed, 1, 100);
        var maximum = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(
            scrollViewer.VerticalOffset + direction * step, 0, maximum));
        PlaylistList.UpdateLayout();
    }

    private int GetPlaylistInsertionIndex(Point position)
    {
        var visibleRows = new List<(PlaylistItem Item, double Top, double Bottom)>();
        foreach (var item in _displayedPlaylist)
        {
            if (PlaylistList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container ||
                !container.IsVisible || container.ActualHeight <= 0)
                continue;

            try
            {
                var top = container.TranslatePoint(new Point(0, 0), PlaylistList).Y;
                visibleRows.Add((item, top, top + container.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                // La ligne peut être en cours de virtualisation pendant le
                // défilement; elle sera reprise au prochain mouvement.
            }
        }

        if (visibleRows.Count == 0)
            return position.Y <= 0 ? 0 : Playlist.Count;

        visibleRows.Sort((left, right) => left.Top.CompareTo(right.Top));
        var sourceIndex = _playlistDragItem is null ? -1 : Playlist.IndexOf(_playlistDragItem);
        foreach (var row in visibleRows)
        {
            var rowIndex = Playlist.IndexOf(row.Item);
            if (rowIndex < 0)
                continue;

            // Un quart de la ligne suffit pour franchir un élément. Le seuil
            // est orienté selon le sens du déplacement afin de conserver le
            // même ressenti en montant et en descendant dans la file.
            var rowHeight = row.Bottom - row.Top;
            var threshold = sourceIndex >= 0 && sourceIndex < rowIndex
                ? row.Top + rowHeight * 0.25
                : row.Bottom - rowHeight * 0.25;
            if (position.Y <= threshold)
                return Math.Max(0, rowIndex);
        }

        var lastIndex = Playlist.IndexOf(visibleRows[^1].Item);
        return lastIndex < 0 ? Playlist.Count : lastIndex + 1;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? element)
    {
        if (element is null)
            return null;
        if (element is ScrollViewer scrollViewer)
            return scrollViewer;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(element, index));
            if (result is not null)
                return result;
        }

        return null;
    }

    private PlaylistItem? GetPlaylistItemFromElement(DependencyObject? element)
    {
        if (element is null)
            return null;

        // ContainerFromElement peut échouer lorsqu'un TextBlock, une bordure
        // ou un bouton est la source exacte du clic. Remonter la hiérarchie
        // permet de retrouver la ligne de manière fiable dans tous les cas.
        for (var current = element; current is not null; current = GetParentElement(current))
        {
            if (current is FrameworkElement frameworkElement &&
                frameworkElement.DataContext is PlaylistItem dataItem)
                return dataItem;

            if (current is ListBoxItem listBoxItem && listBoxItem.Content is PlaylistItem item)
                return item;
        }

        return null;
    }

    private ListBoxItem? GetPlaylistContainerFromElement(DependencyObject? element)
    {
        for (var current = element; current is not null; current = GetParentElement(current))
        {
            if (current is ListBoxItem item)
                return item;
        }

        return null;
    }

    private static DependencyObject? GetParentElement(DependencyObject element) => element switch
    {
        Visual visual => VisualTreeHelper.GetParent(visual),
        Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
        FrameworkContentElement contentElement => contentElement.Parent,
        _ => null
    };

    private void PlaylistList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var index = GetSelectedPlaylistIndex();
        if (index >= 0)
            PlayAt(index);
    }

    private void PlaylistList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var index = GetSelectedPlaylistIndex();
        if (e.Key != Key.Delete || index < 0)
            return;

        RemovePlaylistItem(index);
        e.Handled = true;
    }

    private void PlaylistItemRemoveButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Le bouton de retrait ne doit jamais armer un glisser-déposer.
        _playlistDragItem = null;
        if (sender is Button { DataContext: PlaylistItem item })
        {
            var index = Playlist.IndexOf(item);
            if (index >= 0)
                RemovePlaylistItem(index);
        }

        e.Handled = true;
    }

    private int GetSelectedPlaylistIndex() => PlaylistList.SelectedItem is PlaylistItem item
        ? Playlist.IndexOf(item)
        : -1;

    private void RemovePlaylistItem(int index)
    {
        if (index < 0 || index >= Playlist.Count)
            return;

        if (index == _currentIndex || (_currentIndex < 0 && index == 0))
        {
            ShowToast("Le premier média ne peut pas être supprimé");
            return;
        }

        if (Playlist[index].IsEnhancedQueued)
        {
            ShowToast("Les médias de la lecture augmentée sont fixes");
            return;
        }

        var removingCurrent = index == _currentIndex;
        if (removingCurrent)
        {
            _mediaPlayer.Stop();
            _currentMedia?.Dispose();
            _currentMedia = null;
            _currentIndex = -1;
            ResetNowPlaying();
        }
        else if (index < _currentIndex)
        {
            _currentIndex--;
        }

        Playlist.RemoveAt(index);
        RefreshPlaylistCount();
        if (Playlist.Count > 0)
        {
            if (_currentIndex >= 0 && _currentIndex < Playlist.Count)
                SelectCurrentPlaylistItem();
            else
            {
                var selection = Playlist[Math.Min(index, Playlist.Count - 1)];
                PlaylistList.SelectedItem = selection;
            }
        }

        PersistSession();
    }

    private void ClearPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex >= 0 && _currentIndex < Playlist.Count)
        {
            var currentItem = Playlist[_currentIndex];
            for (var index = Playlist.Count - 1; index >= 0; index--)
            {
                if (index != _currentIndex && !Playlist[index].IsEnhancedQueued)
                {
                    Playlist.RemoveAt(index);
                    if (index < _currentIndex)
                        _currentIndex--;
                }
            }

            _currentIndex = Playlist.IndexOf(currentItem);
            SelectCurrentPlaylistItem();
            RefreshPlaylistCount();
            ShowToast("File vidée, médias de la lecture augmentée conservés");
            return;
        }

        for (var index = Playlist.Count - 1; index >= 0; index--)
        {
            if (!Playlist[index].IsEnhancedQueued)
                Playlist.RemoveAt(index);
        }
        _currentIndex = -1;
        RefreshPlaylistCount();
        ResetNowPlaying();
        ShowToast("File vidée, médias de la lecture augmentée conservés");
    }

    private void ResetNowPlaying()
    {
        _seekCommitTimer.Stop();
        _videoSurfaceRevealTimer.Stop();
        _startupPlaybackGatePending = false;
        _resumePlaybackAfterSurfaceReveal = false;
        _videoSurfaceReady = false;
        _startupPlaybackPendingMedia = null;
        _startupPlaybackWatchdogTimer.Stop();
        _playbackRestartedForCurrentMedia = false;
        _pendingPlaybackAfterWindowTransition = false;
        RevealOverlayAfterStartup();
        HideStartupLoadingOverlay();
        _pendingSeekTarget = null;
        _requestedAudioTrackId = null;
        _requestedSubtitleTrackId = null;
        VideoView.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        NowPlayingTitle.Text = LocalizationService.Get("Aucun média");
        NowPlayingDetail.Text = string.Empty;
        Title = "Fuze";
        WindowTitleText.Text = "Fuze";
        SeekSlider.Value = 0;
        UpdateTimelineText(0, 0);
        SetPlayPauseVisual(false);
        AudioTrackCountText.Text = "0/0";
        SubtitleTrackCountText.Text = "0/0";
        SetEngineState("MOTEUR PRÊT", "#FF45D483");
    }

    private void RefreshPlaylistCount()
    {
        RefreshDisplayedPlaylist();
        UpdatePlaylistCountText();
    }

    private void UpdatePlaylistCountText()
    {
        if (!string.IsNullOrWhiteSpace(_playlistSearchQuery))
        {
            var resultCount = _displayedPlaylist.Count;
            PlaylistCountText.Text = resultCount == 1
                ? LocalizationService.Format("1 résultat / {0}", Playlist.Count)
                : LocalizationService.Format("{0} résultats / {1}", resultCount, Playlist.Count);
            return;
        }

        // Les médias déjà parcourus restent conservés dans la file interne
        // (pour la navigation arrière), mais ne sont plus comptés comme des
        // éléments à venir dans le tiroir.
        var remainingCount = _currentIndex >= 0 && _currentIndex < Playlist.Count
            ? Playlist.Count - _currentIndex
            : Playlist.Count;
        PlaylistCountText.Text = remainingCount switch
        {
            0 => LocalizationService.Get("0 élément"),
            1 => LocalizationService.Get("1 élément"),
            _ => LocalizationService.Format("{0} éléments", remainingCount)
        };
    }

    private void RefreshDisplayedPlaylist()
    {
        if (_displayedPlaylist is null)
            return;

        var currentItem = _currentIndex >= 0 && _currentIndex < Playlist.Count
            ? Playlist[_currentIndex]
            : null;
        foreach (var item in Playlist)
            item.IsCurrent = ReferenceEquals(item, currentItem);

        var visibleItems = BuildDisplayedPlaylistItems();
        _displayedPlaylist.Clear();
        foreach (var item in visibleItems)
            _displayedPlaylist.Add(item);
    }

    private void PlaylistSearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _playlistSearchQuery = PlaylistSearchTextBox.Text.Trim();
        UpdatePlaylistSearchPlaceholder();
        RefreshDisplayedPlaylist();
        UpdatePlaylistCountText();
        if (_displayedPlaylist.Count > 0)
            PlaylistList.ScrollIntoView(_displayedPlaylist[0]);
    }

    private void PlaylistSearchTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdatePlaylistSearchPlaceholder();

    private void PlaylistSearchTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdatePlaylistSearchPlaceholder();

    private void UpdatePlaylistSearchPlaceholder()
    {
        if (PlaylistSearchPlaceholderText is null || PlaylistSearchTextBox is null)
            return;

        PlaylistSearchPlaceholderText.Visibility =
            string.IsNullOrWhiteSpace(PlaylistSearchTextBox.Text) &&
            !PlaylistSearchTextBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private List<PlaylistItem> BuildDisplayedPlaylistItems()
    {
        // Une recherche doit pouvoir retrouver n'importe quel élément de la
        // file, même lorsque l'aperçu des prochains médias est désactivé.
        if (!string.IsNullOrWhiteSpace(_playlistSearchQuery))
        {
            return Playlist
                .Where(item => item.Title.Contains(_playlistSearchQuery,
                                    StringComparison.CurrentCultureIgnoreCase) ||
                               item.Location.Contains(_playlistSearchQuery,
                                    StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }

        var visibleItems = new List<PlaylistItem>();
        if (_currentIndex >= 0 && _currentIndex < Playlist.Count)
        {
            visibleItems.Add(Playlist[_currentIndex]);
            var previewCount = _showEnhancedUpcomingInPlaylist
                ? int.MaxValue
                : 0;
            for (var index = _currentIndex + 1;
                 index < Playlist.Count && visibleItems.Count <= previewCount;
                 index++)
            {
                visibleItems.Add(Playlist[index]);
            }
        }
        else
        {
            visibleItems.AddRange(_showEnhancedUpcomingInPlaylist
                ? Playlist
                : Playlist.Take(1));
        }

        return visibleItems;
    }

    private void SynchronizeDisplayedPlaylistOrder()
    {
        var desired = BuildDisplayedPlaylistItems();
        if (_displayedPlaylist.Count != desired.Count)
        {
            RefreshDisplayedPlaylist();
            return;
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (ReferenceEquals(_displayedPlaylist[index], desired[index]))
                continue;

            var existingIndex = _displayedPlaylist.IndexOf(desired[index]);
            if (existingIndex < 0)
            {
                RefreshDisplayedPlaylist();
                return;
            }

            _displayedPlaylist.Move(existingIndex, index);
        }
    }

    private void SelectCurrentPlaylistItem()
    {
        // Réappliquer la préférence à chaque synchronisation de l'affichage.
        // Cela couvre aussi les éléments ajoutés automatiquement après le
        // chargement initial et évite qu'un premier élément conserve une
        // profondeur différente des suivants.
        ApplyPlaylistFolderDepth();

        if (_currentIndex < 0 || _currentIndex >= Playlist.Count)
        {
            PlaylistList.SelectedItem = null;
            return;
        }

        RefreshDisplayedPlaylist();
        UpdatePlaylistCountText();
        var item = Playlist[_currentIndex];
        PlaylistList.SelectedItem = item;
        PlaylistList.ScrollIntoView(item);
    }

    private void ControlActivationZone_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_suppressBottomRevealAfterToolBarPin)
            return;

        _suppressBottomRevealAfterToolBarPin = false;
        RevealPlaybackControls();
    }

    private void ControlActivationZone_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_suppressBottomRevealAfterToolBarPin)
            return;

        _suppressBottomRevealAfterToolBarPin = false;
        RevealPlaybackControls();
    }

    private void TrackPointerProximity()
    {
        if (_windowHandle == IntPtr.Zero || !IsVisible || WindowState == WindowState.Minimized ||
            !GetCursorPos(out var cursor) || PresentationSource.FromVisual(VideoViewport) is null)
            return;

        Point pointerInViewport;
        try
        {
            pointerInViewport = VideoViewport.PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var insideViewport = pointerInViewport.X >= 0 && pointerInViewport.X < VideoViewport.ActualWidth &&
                             pointerInViewport.Y >= 0 && pointerInViewport.Y < VideoViewport.ActualHeight;
        var moved = !_hasLastCursorPosition || cursor.X != _lastCursorPosition.X ||
                    cursor.Y != _lastCursorPosition.Y;
        if (moved)
        {
            _lastCursorMovementTick = Environment.TickCount64;
            if (_cursorIsHidden)
            {
                SetPlaybackCursorVisibility(true);
                _cursorIsHidden = false;
            }

            _cursorHideTimer.Stop();
            if (_autoHideCursor && insideViewport && _modalDialogDepth == 0)
                RestartCursorHideTimerWithRemaining();
        }
        else if (_autoHideCursor && !_cursorIsHidden && insideViewport &&
                 _modalDialogDepth == 0 && !_cursorHideTimer.IsEnabled &&
                 !IsPointerOverPlaybackControl() && !IsPointerInsideElement(ToolBarHost))
        {
            // Re-arm the timer if a native video/overlay transition consumed
            // the mouse-move notification while the pointer is stationary.
            RestartCursorHideTimerWithRemaining();
        }
        var bottomZoneHeight = Math.Max(44, 60 * _interfaceScale);
        const double toolZoneHeight = 38;
        var inBottomZone = insideViewport &&
                           pointerInViewport.Y >= VideoViewport.ActualHeight - bottomZoneHeight;
        // La barre supérieure est une surcouche de la vidéo, y compris en plein écran.
        // En mode fenêtré, la bande de titre fait aussi partie de la zone d'approche.
        var inTopZone = insideViewport && pointerInViewport.Y <= toolZoneHeight;
        if (!_isFullscreen && PresentationSource.FromVisual(AppShell) is not null)
        {
            try
            {
                var pointerInShell = AppShell.PointFromScreen(new Point(cursor.X, cursor.Y));
                var insideShell = pointerInShell.X >= 0 && pointerInShell.X < AppShell.ActualWidth &&
                                  pointerInShell.Y >= 0 && pointerInShell.Y < AppShell.ActualHeight;
                var titleHeight = Math.Max(0, TitleBar.ActualHeight);
                inTopZone |= insideShell && pointerInShell.Y <= titleHeight + toolZoneHeight;
            }
            catch (InvalidOperationException)
            {
                // La fenêtre peut être en transition de DPI ou de moniteur.
            }
        }
        var canRevealTopBarWithoutGear = CanRevealTopBarWithoutGear();

        if (!inBottomZone)
            _suppressBottomRevealAfterToolBarPin = false;

        if (inBottomZone && !_suppressBottomRevealAfterToolBarPin &&
            (moved || !_pointerWasInBottomZone))
            RevealPlaybackControls();

        var canRevealTopBar = canRevealTopBarWithoutGear || _toolBarPinnedOpen;
        if (canRevealTopBar && !_suppressToolBarActivation && inTopZone &&
            (moved || !_pointerWasInTopZone))
        {
            ExpandToolBar(true);
            RestartToolBarHideTimer();
        }

        if (!inTopZone && _pointerWasInTopZone)
            RestartToolBarHideTimer();

        TrackSeekPreview(cursor, moved);

        _lastCursorPosition = cursor;
        _hasLastCursorPosition = true;
        _pointerWasInBottomZone = inBottomZone;
        _pointerWasInTopZone = inTopZone;
    }

    private void TrackSeekPreview(NativePoint cursor, bool moved)
    {
        if (!IsLoaded || SeekSurface.ActualWidth <= 0 || _mediaPlayer.Length <= 0 ||
            PresentationSource.FromVisual(SeekSurface) is null)
            return;

        var origin = SeekSurface.PointToScreen(new Point(0, 0));
        var opposite = SeekSurface.PointToScreen(
            new Point(SeekSurface.ActualWidth, SeekSurface.ActualHeight));
        var left = Math.Min(origin.X, opposite.X);
        var right = Math.Max(origin.X, opposite.X);
        var top = Math.Min(origin.Y, opposite.Y);
        var bottom = Math.Max(origin.Y, opposite.Y);
        var screenWidth = right - left;
        var inside = screenWidth > 0 && cursor.X >= left && cursor.X <= right &&
                     cursor.Y >= top && cursor.Y <= bottom;

        if (inside && moved)
        {
            var x = Math.Clamp((cursor.X - left) / screenWidth * SeekSurface.ActualWidth,
                0, SeekSurface.ActualWidth);
            ShowSeekPreview(x);
        }
        else if (!inside && !_isSeeking)
        {
            SeekPreviewPopup.Visibility = Visibility.Collapsed;
        }
    }

    private void RevealPlaybackControls()
    {
        RestartControlsHideTimer();

        if (ControlsPanel.Visibility == Visibility.Visible && ControlsPanel.IsHitTestVisible)
            return;

        var animationVersion = ++_controlsAnimationVersion;
        var fromOpacity = ControlsPanel.Visibility == Visibility.Visible ? ControlsPanel.Opacity : 0;
        var fromOffset = ControlsPanel.Visibility == Visibility.Visible ? ControlsTranslate.Y : 7;

        ControlsPanel.Visibility = Visibility.Visible;
        ControlsPanel.IsHitTestVisible = true;
        AlignVideoOverlayWindow();

        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(105),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation
        {
            From = fromOffset,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(125),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (animationVersion != _controlsAnimationVersion)
                return;

            ControlsPanel.BeginAnimation(OpacityProperty, null);
            ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ControlsPanel.Opacity = 1;
            ControlsTranslate.Y = 0;
        };

        ControlsPanel.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void RestartControlsHideTimer()
    {
        if (_bottomBarLayoutPreviewActive)
        {
            _controlsHideTimer.Stop();
            return;
        }
        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void RestartToolBarHideTimer()
    {
        _toolBarHideTimer.Stop();
        _toolBarHideTimer.Start();
    }

    private void RestartCursorHideTimerWithRemaining()
    {
        _cursorHideTimer.Stop();
        if (!_autoHideCursor || _cursorIsHidden || _isClosing || _modalDialogDepth > 0 ||
            !IsPointerInsideElement(VideoViewport) ||
            IsPointerOverPlaybackControl() || IsPointerInsideElement(ToolBarHost))
            return;

        var hideDelay = _cursorAutoHideDelayMilliseconds;
        var elapsed = _lastCursorMovementTick > 0
            ? Math.Max(0, Environment.TickCount64 - _lastCursorMovementTick)
            : 0;
        var remaining = Math.Max(1, hideDelay - (int)Math.Min(int.MaxValue, elapsed));
        _cursorHideTimer.Interval = TimeSpan.FromMilliseconds(remaining);
        _cursorHideTimer.Start();
    }

    private void SetPlaybackCursorVisibility(bool visible)
    {
        var cursor = visible ? Cursors.Arrow : Cursors.None;
        VideoHwndHost.SetVideoCursorHidden(!visible);
        Cursor = cursor;
        VideoViewport.Cursor = cursor;
        VideoOverlay.Cursor = cursor;
        VideoView.Cursor = cursor;
        if (_videoOverlayWindow is not null)
            _videoOverlayWindow.Cursor = cursor;
    }

    private void MainWindow_OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (_disableToolTips && e.OriginalSource is DependencyObject element)
            ToolTipService.SetIsEnabled(element, false);
    }

    private void ShowStartupLoadingOverlay(string message)
    {
        if (_isClosing || StartupLoadingOverlay is null)
            return;

        StartupLoadingText.Text = LocalizationService.Get(
            string.IsNullOrWhiteSpace(message) ? "Ouverture de la vidéo…" : message);
        StartupLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideStartupLoadingOverlay()
    {
        if (StartupLoadingOverlay is not null)
            StartupLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void VideoToolTip_OnOpening(object sender, ToolTipEventArgs e)
    {
        SetToolTipVisibilityState(true);
    }

    private void VideoToolTip_OnClosing(object sender, ToolTipEventArgs e)
    {
        SetToolTipVisibilityState(false);
    }

    internal void SetToolTipVisibilityState(bool open)
    {
        _toolTipIsOpen = open;
        if (open)
        {
            _controlsHideTimer.Stop();
            _gearControlsHideTimer.Stop();
            _toolBarHideTimer.Stop();
        }
        else
        {
            RestartControlsHideTimer();
            RestartToolBarHideTimer();
        }
    }

    internal void SetActiveToolTipHandle(IntPtr handle)
    {
        _activeToolTipHandle = handle;
    }

    internal void ClearActiveToolTipHandle(IntPtr handle)
    {
        if (_activeToolTipHandle == handle)
            _activeToolTipHandle = IntPtr.Zero;
    }

    private void ApplyToolTipVisibility(bool enabled)
    {
        App.ToolTipsEnabled = enabled;
        // IsEnabled est héritée par les éléments enfants; appliquer aussi la
        // valeur aux surfaces séparées garantit que l'overlay vidéo et ses
        // fenêtres natives suivent le même réglage.
        ToolTipService.SetIsEnabled(this, enabled);
        ToolTipService.SetIsEnabled(VideoViewport, enabled);
        ToolTipService.SetIsEnabled(VideoOverlay, enabled);
        ToolTipService.SetIsEnabled(OverlayParking, enabled);
        ToolTipService.SetIsEnabled(ToolBarHost, enabled);
        ToolTipService.SetIsEnabled(ControlsPanel, enabled);
        if (_videoOverlayWindow is not null)
            ToolTipService.SetIsEnabled(_videoOverlayWindow, enabled);
    }

    private static BottomBarLayoutPresetData CreateCompactBottomBarLayout() => new()
    {
        Name = "Fuze — compacte",
        IsBuiltIn = true,
        LeftItems = ["playlist", "previous", "rewind", "play", "forward", "next", "title"],
        CenterItems = ["timeline"],
        RightItems = ["screenshot", "shuffle", "repeat", "audio", "subtitles", "speed", "sync", "pan", "gear", "mute", "volume", "fullscreen"],
        Spacing = 4,
        CenterBarOffset = 10
    };

    private static BottomBarLayoutPresetData CreateCinemaBottomBarLayout() => new()
    {
        Name = "Fuze — cinéma",
        IsBuiltIn = true,
        LeftItems = ["previous", "rewind", "play", "forward", "next"],
        CenterItems = ["title", "timeline"],
        RightItems = ["playlist", "audio", "subtitles", "mute", "volume", "pan", "fullscreen"],
        Spacing = 5,
        CenterBarOffset = 10
    };

    private static BottomBarLayoutPresetData CreateClassicBottomBarLayout() => new()
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

    private static BottomBarLayoutPresetData CreatePrimaryClassicBottomBarLayout()
    {
        var preset = CreateClassicBottomBarLayout();
        preset.Name = PrimaryClassicBottomBarLayoutName;
        preset.CenterLockedItemId = "play";
        return preset;
    }

    private static readonly HashSet<string> BottomBarLayoutItemIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "playlist", "previous", "rewind", "play", "forward", "next", "title", "timeline",
        "elapsed_time", "duration_time",
        "screenshot", "shuffle", "repeat", "audio", "subtitles", "speed", "sync", "pan", "gear",
        "mute", "volume", "fullscreen"
    };

    private const string PrimaryClassicBottomBarLayoutName = "Fuze — classique";
    // Largeur de référence de la barre inférieure. Le titre suit cette
    // proportion lorsque la fenêtre est réduite, sans modifier son texte.
    private const double BottomBarResponsiveReferenceWidth = 1280d;
    private const double BottomBarResponsiveMinimumWidth = 640d;
    private const double BottomBarTitleResizeCurve = 1.5d;
    private const double BottomBarTitleMinimumWidth = 48d;

    private static bool IsPrimaryClassicBottomBarLayoutName(string? name) =>
        string.Equals(name?.Trim(), "fuze new", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), "Fuse Classic 4", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), "fuse classique 4", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name?.Trim(), "fuze — classique 4", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuiltInBottomBarLayout(BottomBarLayoutPresetData preset) =>
        preset.IsBuiltIn ||
        string.Equals(preset.Name, PrimaryClassicBottomBarLayoutName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuze — compacte", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuze — cinéma", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Name, "Fuse classique", StringComparison.OrdinalIgnoreCase);

    private static BottomBarLayoutPresetData CloneBottomBarLayout(BottomBarLayoutPresetData source) => new()
    {
        Name = source.Name?.Trim() ?? string.Empty,
        IsBuiltIn = IsBuiltInBottomBarLayout(source),
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

    private static List<BottomBarLayoutPresetData> NormalizeBottomBarLayoutPresets(
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
                var preset = CloneBottomBarLayout(candidate);
                // Les deux modèles fournis par Fuze sont toujours recréés depuis
                // leurs définitions officielles. Une ancienne session ne peut
                // donc pas conserver une modification accidentelle de ceux-ci.
                if (IsPrimaryClassicBottomBarLayoutName(preset.Name))
                {
                    // Promouvoir la version 4 comme classique principale sans
                    // supprimer les autres mises en page personnalisées.
                    preset.Name = PrimaryClassicBottomBarLayoutName;
                    preset.IsBuiltIn = true;
                    var usedPrimary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var list in new[] { preset.LeftItems, preset.CenterItems, preset.RightItems })
                        list.RemoveAll(id => !BottomBarLayoutItemIds.Contains(id) || !usedPrimary.Add(id));
                    if (!usedPrimary.Contains("pan"))
                        preset.RightItems.Add("pan");
                    if (preset.HorizontalPositions.Count > 0 &&
                        !preset.HorizontalPositions.ContainsKey("pan"))
                        preset.HorizontalPositions["pan"] = 0.719d;
                    primaryClassic ??= preset;
                    continue;
                }
                if (IsBuiltInBottomBarLayout(preset))
                    continue;
                if (string.IsNullOrWhiteSpace(preset.Name) || result.Any(existing =>
                        string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var list in new[] { preset.LeftItems, preset.CenterItems, preset.RightItems })
                    list.RemoveAll(id => !BottomBarLayoutItemIds.Contains(id) || !used.Add(id));
                result.Add(preset);
            }
        }

        result.Insert(0, CreateCinemaBottomBarLayout());
        result.Insert(0, CreateCompactBottomBarLayout());
        result.Insert(2, primaryClassic ?? CreatePrimaryClassicBottomBarLayout());
        return result;
    }

    private static void RemoveBottomBarElementFromParent(FrameworkElement element)
    {
        if (element.Parent is Panel panel)
            panel.Children.Remove(element);
        else if (element.Parent is ContentControl contentControl &&
                 ReferenceEquals(contentControl.Content, element))
            contentControl.Content = null;
    }

    private void InitializeBottomBarLayoutHosts()
    {
        _bottomBarLayoutElements.Clear();
        _bottomBarLayoutElements["playlist"] = PlaylistButton;
        _bottomBarLayoutElements["previous"] = PreviousButton;
        _bottomBarLayoutElements["rewind"] = RewindButton;
        _bottomBarLayoutElements["play"] = PlayButton;
        _bottomBarLayoutElements["forward"] = ForwardButton;
        _bottomBarLayoutElements["next"] = NextButton;
        _bottomBarLayoutElements["title"] = NowPlayingTitleHost;
        _bottomBarLayoutElements["timeline"] = TimelineTextHost;
        _bottomBarLayoutElements["screenshot"] = ScreenshotButton;
        _bottomBarLayoutElements["shuffle"] = ShuffleButton;
        _bottomBarLayoutElements["repeat"] = RepeatButton;
        _bottomBarLayoutElements["audio"] = AudioTracksButton;
        _bottomBarLayoutElements["subtitles"] = SubtitleTracksButton;
        _bottomBarLayoutElements["speed"] = SpeedButton;
        _bottomBarLayoutElements["sync"] = TrackSynchronizationButton;
        _bottomBarLayoutElements["pan"] = VideoPanButton;
        _bottomBarLayoutElements["gear"] = OptionsBarPinButton;
        _bottomBarLayoutElements["mute"] = MuteButton;
        _bottomBarLayoutElements["volume"] = VolumeControlHost;
        _bottomBarLayoutElements["fullscreen"] = FullscreenButton;

        foreach (var element in _bottomBarLayoutElements.Values)
            RemoveBottomBarElementFromParent(element);

        _bottomBarLayoutElements["elapsed_time"] = ElapsedText;
        _bottomBarLayoutElements["duration_time"] = DurationText;

        NowPlayingTitleHost.Width = 230;
        DefaultBottomBarLayoutGrid.Visibility = Visibility.Collapsed;
        BottomBarCustomLayoutGrid.Visibility = Visibility.Visible;
        _bottomBarLayoutInitialized = true;
        _bottomBarLayoutPresets =
            [CreateCompactBottomBarLayout(), CreateCinemaBottomBarLayout(),
                CreatePrimaryClassicBottomBarLayout()];
        ApplyBottomBarLayout();
    }

    private void ApplyBottomBarLayout()
    {
        if (!_bottomBarLayoutInitialized)
            return;

        var preset = _bottomBarLayoutPresets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, _activeBottomBarLayoutPreset, StringComparison.OrdinalIgnoreCase))
            ?? _bottomBarLayoutPresets.FirstOrDefault() ?? CreateCompactBottomBarLayout();
        _activeBottomBarLayoutPreset = preset.Name;

        ApplyBottomBarLayout(preset);
    }

    private void ApplyBottomBarLayoutPreview(BottomBarLayoutPresetData preset)
    {
        if (_bottomBarLayoutInitialized)
        {
            ApplyBottomBarLayout(preset);
            KeepBottomBarLayoutSelectionValid(preset);
        }
    }

    private IReadOnlyList<string> GetBottomBarLayoutVisualOrder()
    {
        var preset = _bottomBarLayoutEditorDraft ?? _appliedBottomBarLayout;
        if (preset is null)
            return [];

        return EnumerateBottomBarLayoutIds(preset)
            .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) && element.IsVisible)
            .Select((id, sourceIndex) => new
            {
                Id = id,
                SourceIndex = sourceIndex,
                Position = preset.HorizontalPositions.TryGetValue(id, out var value) ? value : 0.5
            })
            .OrderBy(item => item.Position)
            .ThenBy(item => item.SourceIndex)
            .Select(item => item.Id)
            .ToArray();
    }

    private void SelectBottomBarLayoutItem(string id, bool extendSelection)
    {
        if (!extendSelection)
        {
            if (!_bottomBarLayoutSelectedItemIds.Contains(id))
            {
                _bottomBarLayoutSelectedItemIds.Clear();
                _bottomBarLayoutSelectedItemIds.Add(id);
            }
            _bottomBarLayoutSelectionAnchorId = id;
            UpdateBottomBarLayoutItemBounds();
            return;
        }

        var orderedIds = GetBottomBarLayoutVisualOrder();
        if (orderedIds.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(_bottomBarLayoutSelectionAnchorId) ||
            !_bottomBarLayoutSelectedItemIds.Contains(_bottomBarLayoutSelectionAnchorId))
        {
            _bottomBarLayoutSelectionAnchorId = id;
            _bottomBarLayoutSelectedItemIds.Clear();
            _bottomBarLayoutSelectedItemIds.Add(id);
            UpdateBottomBarLayoutItemBounds();
            return;
        }

        var anchorIndex = orderedIds.ToList().FindIndex(candidate =>
            string.Equals(candidate, _bottomBarLayoutSelectionAnchorId, StringComparison.OrdinalIgnoreCase));
        var clickedIndex = orderedIds.ToList().FindIndex(candidate =>
            string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
        if (anchorIndex < 0 || clickedIndex < 0)
            return;

        _bottomBarLayoutSelectedItemIds.Clear();
        for (var index = Math.Min(anchorIndex, clickedIndex);
             index <= Math.Max(anchorIndex, clickedIndex); index++)
            _bottomBarLayoutSelectedItemIds.Add(orderedIds[index]);
        UpdateBottomBarLayoutItemBounds();
    }

    private void ClearBottomBarLayoutSelection()
    {
        _bottomBarLayoutSelectedItemIds.Clear();
        _bottomBarLayoutSelectionAnchorId = null;
        UpdateBottomBarLayoutItemBounds();
    }

    private void KeepBottomBarLayoutSelectionValid(BottomBarLayoutPresetData preset)
    {
        var availableIds = EnumerateBottomBarLayoutIds(preset)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _bottomBarLayoutSelectedItemIds.RemoveWhere(id => !availableIds.Contains(id));
        if (_bottomBarLayoutSelectionAnchorId is not null &&
            !availableIds.Contains(_bottomBarLayoutSelectionAnchorId))
            _bottomBarLayoutSelectionAnchorId = null;
    }

    private FrameworkElement? FindBottomBarLayoutElementAt(Point position)
    {
        foreach (var element in _bottomBarLayoutElements.Values.Distinct())
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                continue;

            try
            {
                var topLeft = element.TranslatePoint(new Point(0, 0), ControlsContentGrid);
                // RenderSize est exprimé avant le LayoutTransform de la barre.
                // À 150 %, 200 % ou 300 %, l'utiliser directement donnait à
                // l'éditeur une zone de sélection plus grande que le bouton
                // réellement affiché. Les deux coins traduits appartiennent au
                // même repère que la souris et restent donc exacts à tout DPI.
                var bottomRight = element.TranslatePoint(
                    new Point(element.ActualWidth, element.ActualHeight), ControlsContentGrid);
                var bounds = new Rect(
                    new Point(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y)),
                    new Point(Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y)));
                if (bounds.Contains(position))
                    return element;
            }
            catch (InvalidOperationException)
            {
                // La barre peut être en cours de réorganisation; attendre le
                // prochain mouvement de souris dans ce cas.
            }
        }

        return null;
    }

    private void BottomBarLayoutGuide_OnPreviewMouseLeftButtonDown(object sender,
        MouseButtonEventArgs e)
    {
        if (!_bottomBarLayoutPreviewActive)
            return;

        // L’overlay est volontairement la surface supérieure. Même un clic
        // dans un espace vide doit rester dans l’éditeur et ne jamais activer
        // la commande située derrière.
        e.Handled = true;
        _bottomBarLayoutDragElement = null;
        _bottomBarLayoutDragItem = null;
        _bottomBarLayoutDragActive = false;
        BottomBarLayoutGuideOverlay.ReleaseMouseCapture();
        if (_bottomBarLayoutEditorWindow?.CanEditActivePreset != true)
            return;

        var extendSelection = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var element = FindBottomBarLayoutElementAt(e.GetPosition(ControlsContentGrid));
        if (element is null || !TryGetBottomBarLayoutItem(element, out var item))
        {
            if (!extendSelection)
                ClearBottomBarLayoutSelection();
            return;
        }

        SelectBottomBarLayoutItem(item.Id, extendSelection);
        // Maj+clic ne commence pas un glissement : la touche sert uniquement à
        // construire une plage continue. Le groupe reste sélectionné après le
        // relâchement de Maj et peut ensuite être déplacé d'un seul geste.
        if (extendSelection)
            return;

        _bottomBarLayoutDragElement = element;
        _bottomBarLayoutDragItem = item;
        // Toutes les coordonnées du déplacement sont exprimées dans le
        // Canvas réel. L'overlay de repères est centré avec une largeur et
        // des marges différentes; mélanger ses coordonnées avec celles du
        // Canvas faisait sauter le bouton puis tassait les autres commandes.
        _bottomBarLayoutDragStart = e.GetPosition(BottomBarFreeLayoutCanvas);
        BottomBarLayoutGuideOverlay.CaptureMouse();
    }

    private void BottomBarLayoutGuide_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_bottomBarLayoutPreviewActive)
            return;

        e.Handled = true;
        // Le survol de la surface d’édition garde la vraie barre visible;
        // aucune ancienne échéance de masquage ne doit la faire disparaître.
        RevealPlaybackControls();
        if (e.LeftButton != MouseButtonState.Pressed ||
            _bottomBarLayoutDragElement is null || _bottomBarLayoutDragItem is null)
            return;

        var point = e.GetPosition(BottomBarFreeLayoutCanvas);
        if (!_bottomBarLayoutDragActive &&
            Math.Abs(point.X - _bottomBarLayoutDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _bottomBarLayoutDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!_bottomBarLayoutDragActive)
        {
            BeginBottomBarLayoutDragHistory();
            _bottomBarLayoutDragActive = true;
        }
        UpdateBottomBarLayoutDrag(point);
    }

    private void BottomBarLayoutGuide_OnPreviewMouseLeftButtonUp(object sender,
        MouseButtonEventArgs e)
    {
        if (!_bottomBarLayoutPreviewActive)
            return;

        e.Handled = true;
        var completedDrag = _bottomBarLayoutDragActive && _bottomBarLayoutDragItem is not null;
        if (completedDrag)
        {
            UpdateBottomBarLayoutDrag(e.GetPosition(BottomBarFreeLayoutCanvas));
            CommitBottomBarLayoutDrag();
        }
        BottomBarLayoutGuideOverlay.ReleaseMouseCapture();
        _bottomBarLayoutDragElement = null;
        _bottomBarLayoutDragItem = null;
        _bottomBarLayoutDragActive = false;
        _bottomBarLayoutDragStartPositions.Clear();
        if (_bottomBarLayoutEditorDraft is not null)
        {
            _bottomBarLayoutEditorWindow?.UpdateFromExternal(_bottomBarLayoutEditorDraft);
            if (completedDrag)
                _bottomBarLayoutEditorWindow?.CommitExternalEdit();
            else
                _bottomBarLayoutEditorWindow?.CancelExternalEdit();
        }
    }

    private void OpenBottomBarLayoutEditor(string? basePresetName = null)
    {
        if (_isClosing || _bottomBarLayoutEditorWindow?.IsVisible == true)
            return;

        var active = _bottomBarLayoutPresets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name,
                string.IsNullOrWhiteSpace(basePresetName) ? _activeBottomBarLayoutPreset : basePresetName,
                StringComparison.OrdinalIgnoreCase))
            ?? _bottomBarLayoutPresets.FirstOrDefault();
        if (active is null)
            return;

        var originalPresets = _bottomBarLayoutPresets
            .Select(CloneBottomBarLayout)
            .ToList();
        var originalActiveName = _activeBottomBarLayoutPreset;
        var originalIntegratedButtons = (
            Screenshot: _showScreenshotButton,
            Shuffle: _showShuffleButton,
            Repeat: _showRepeatButton,
            Speed: _showSpeedButton,
            Playlist: _showPlaylistButton,
            Synchronization: _showSynchronizationButton,
            VideoPan: _showVideoPanButton,
            Gear: _hideInterfaceOnVideoStart);
        var editor = new BottomBarLayoutDialog(
            _bottomBarLayoutPresets.Select(CloneBottomBarLayout).ToArray(), active.Name)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Topmost = true,
            ShowActivated = true
        };

        _bottomBarLayoutEditorWindow = editor;
        _bottomBarLayoutEditorDraft = CloneBottomBarLayout(active);
        _bottomBarLayoutFreeDragMode = editor.FreeDragModeEnabled;
        editor.PreviewChanged += draft =>
        {
            _bottomBarLayoutEditorDraft = CloneBottomBarLayout(draft);
            ApplyBottomBarLayoutPreview(draft);
        };
        editor.EditingEnabledChanged += canEdit => SetBottomBarLayoutDragEnabled(canEdit);
        editor.FreeDragModeChanged += enabled => _bottomBarLayoutFreeDragMode = enabled;
        editor.ItemAdded += EnsureBottomBarLayoutItemVisible;
        editor.Closed += (_, _) =>
        {
            EndBottomBarLayoutPreview();
            if (editor.ResultAccepted)
            {
                _bottomBarLayoutPresets = editor.ResultPresets
                    .Select(CloneBottomBarLayout)
                    .ToList();
                _activeBottomBarLayoutPreset = editor.ActivePresetName;
                ApplyBottomBarLayout();
                ApplyIntegratedBottomBarButtonVisibility();
                PersistSession();
            }
            else
            {
                _bottomBarLayoutPresets = originalPresets;
                _activeBottomBarLayoutPreset = originalActiveName;
                _showScreenshotButton = originalIntegratedButtons.Screenshot;
                _showShuffleButton = originalIntegratedButtons.Shuffle;
                _showRepeatButton = originalIntegratedButtons.Repeat;
                _showSpeedButton = originalIntegratedButtons.Speed;
                _showPlaylistButton = originalIntegratedButtons.Playlist;
                _showSynchronizationButton = originalIntegratedButtons.Synchronization;
                _showVideoPanButton = originalIntegratedButtons.VideoPan;
                _hideInterfaceOnVideoStart = originalIntegratedButtons.Gear;
                ApplyBottomBarLayout();
                ApplyIntegratedBottomBarButtonVisibility();
            }

            _bottomBarLayoutEditorWindow = null;
            _bottomBarLayoutEditorDraft = null;
            _bottomBarLayoutFreeDragMode = false;
        };

        BeginBottomBarLayoutPreview();
        // Le modèle de départ est d’abord rendu avec sa disposition originale,
        // puis ses centres réels deviennent les positions continues de la copie.
        ApplyBottomBarLayoutPreview(active);
        SetBottomBarLayoutDragEnabled(editor.CanEditActivePreset);
        // Utilise le même chemin que les autres fenêtres Fuse : il désactive
        // temporairement le z-order de la fenêtre vidéo, place l’éditeur
        // au-dessus de celle-ci, puis lui donne réellement le focus.
        // L'éditeur est déjà Topmost et appartient à l'overlay. Ne pas
        // rétrograder puis remonter les deux HWND vidéo lors de son ouverture :
        // cette permutation d'ordre Z faisait reconstruire la surface D3D11
        // et produisait le passage noir observé au clic sur « Créer ».
        ShowAuxiliaryDialog(editor, preserveVideoZOrder: true);
        editor.Focus();
        // Le premier calcul doit attendre que la fenêtre et la vraie barre aient
        // terminé leur passe de mesure. Sinon ActualWidth/ActualHeight peuvent
        // encore correspondre à l'ancien modèle; le premier clic de l'utilisateur
        // déclenche alors un recalcul qui replace soudainement tous les items.
        Dispatcher.BeginInvoke(() =>
        {
            if (_bottomBarLayoutEditorWindow != editor || !editor.IsVisible ||
                _bottomBarLayoutEditorDraft is null)
                return;

            ControlsContentGrid.UpdateLayout();
            BottomBarCustomLayoutGrid.UpdateLayout();
            // Un modèle déjà enregistré possède ses coordonnées continues.
            // Les recompacter à chaque réouverture décalait différemment les
            // voisins gauche et droit du bouton central. On complète seulement
            // les positions manquantes, dans le repère exact du Canvas.
            SeedContinuousBottomBarPositions(_bottomBarLayoutEditorDraft, false);
            PersistCurrentBottomBarPositions(_bottomBarLayoutEditorDraft,
                Math.Max(1, BottomBarCustomLayoutGrid.ActualWidth));
            ApplyBottomBarLayoutPreview(_bottomBarLayoutEditorDraft);
            ControlsContentGrid.UpdateLayout();
            BottomBarCustomLayoutGrid.UpdateLayout();
            PositionBottomBarFreeLayout();
            editor.UpdateFromExternal(_bottomBarLayoutEditorDraft);
        }, DispatcherPriority.Render);
    }

    private void SetBottomBarLayoutDragEnabled(bool enabled)
    {
        foreach (var element in _bottomBarLayoutElements.Values.Distinct())
        {
            element.PreviewMouseLeftButtonDown -= BottomBarLayoutElement_OnPreviewMouseLeftButtonDown;
            element.PreviewMouseMove -= BottomBarLayoutElement_OnPreviewMouseMove;
            if (enabled)
            {
                element.PreviewMouseLeftButtonDown += BottomBarLayoutElement_OnPreviewMouseLeftButtonDown;
                element.PreviewMouseMove += BottomBarLayoutElement_OnPreviewMouseMove;
            }
        }
    }

    private void EnsureBottomBarLayoutItemVisible(string id)
    {
        switch (id)
        {
            case "screenshot":
                _showScreenshotButton = true;
                break;
            case "shuffle":
                _showShuffleButton = true;
                break;
            case "repeat":
                _showRepeatButton = true;
                break;
            case "speed":
                _showSpeedButton = true;
                break;
            case "playlist":
                _showPlaylistButton = true;
                break;
            case "sync":
                _showSynchronizationButton = true;
                break;
            case "pan":
                _showVideoPanButton = true;
                break;
            case "gear":
                _hideInterfaceOnVideoStart = true;
                break;
        }

        if (_bottomBarLayoutElements.TryGetValue(id, out var element))
            element.Visibility = Visibility.Visible;
        ApplyIntegratedBottomBarButtonVisibility();
    }

    private void ApplyIntegratedBottomBarButtonVisibility()
    {
        UpdatePlaybackModeButtons();
        ScreenshotButton.Visibility = _bottomBarLayoutPreviewActive || _showScreenshotButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        SpeedButton.Visibility = _bottomBarLayoutPreviewActive || _showSpeedButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlaylistButton.Visibility = _bottomBarLayoutPreviewActive || _showPlaylistButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        TrackSynchronizationButton.Visibility = _bottomBarLayoutPreviewActive || _showSynchronizationButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        VideoPanButton.Visibility = _bottomBarLayoutPreviewActive || _showVideoPanButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        OptionsBarPinButton.Visibility = _bottomBarLayoutPreviewActive || _hideInterfaceOnVideoStart
            ? Visibility.Visible
            : Visibility.Collapsed;
        PositionBottomBarFreeLayout();
    }

    private bool TryGetBottomBarLayoutItem(FrameworkElement element,
        out BottomBarLayoutEditorItem item)
    {
        var pair = _bottomBarLayoutElements.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Value, element));
        var id = pair.Key;
        if (string.IsNullOrWhiteSpace(id) || !BottomBarLayoutItemIds.Contains(id))
        {
            item = null!;
            return false;
        }

        item = new BottomBarLayoutEditorItem(id, id);
        return true;
    }

    private void BottomBarLayoutElement_OnPreviewMouseLeftButtonDown(object sender,
        MouseButtonEventArgs e)
    {
        if (!_bottomBarLayoutPreviewActive ||
            _bottomBarLayoutEditorWindow?.CanEditActivePreset != true ||
            sender is not FrameworkElement element ||
            !TryGetBottomBarLayoutItem(element, out var item))
            return;

        var extendSelection = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        SelectBottomBarLayoutItem(item.Id, extendSelection);
        if (extendSelection)
        {
            e.Handled = true;
            return;
        }
        _bottomBarLayoutDragElement = element;
        _bottomBarLayoutDragItem = item;
        _bottomBarLayoutDragStart = e.GetPosition(BottomBarFreeLayoutCanvas);
        _bottomBarLayoutDragActive = false;
    }

    private void BottomBarLayoutElement_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_bottomBarLayoutPreviewActive ||
            _bottomBarLayoutEditorWindow?.CanEditActivePreset != true ||
            e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement element ||
            !ReferenceEquals(element, _bottomBarLayoutDragElement) ||
            _bottomBarLayoutDragItem is null)
            return;
        var point = e.GetPosition(BottomBarFreeLayoutCanvas);
        if (!_bottomBarLayoutDragActive &&
            Math.Abs(point.X - _bottomBarLayoutDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _bottomBarLayoutDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (!_bottomBarLayoutDragActive)
        {
            BeginBottomBarLayoutDragHistory();
            _bottomBarLayoutDragActive = true;
        }
        UpdateBottomBarLayoutDrag(e.GetPosition(BottomBarFreeLayoutCanvas));
    }

    private void BottomBarLayout_OnDragOver(object sender, DragEventArgs e)
    {
        if (!_bottomBarLayoutPreviewActive ||
            _bottomBarLayoutEditorWindow?.CanEditActivePreset != true ||
            !e.Data.GetDataPresent(typeof(BottomBarLayoutEditorItem)))
            return;
        RevealPlaybackControls();
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void BottomBarLayout_OnDrop(object sender, DragEventArgs e)
    {
        if (!_bottomBarLayoutPreviewActive ||
            _bottomBarLayoutEditorWindow?.CanEditActivePreset != true ||
            !e.Data.GetDataPresent(typeof(BottomBarLayoutEditorItem)) ||
            e.Data.GetData(typeof(BottomBarLayoutEditorItem)) is not BottomBarLayoutEditorItem item)
            return;

        var draft = CloneBottomBarLayout(_bottomBarLayoutEditorDraft ??
            _bottomBarLayoutPresets.FirstOrDefault() ?? CreateCompactBottomBarLayout());
        var pointer = e.GetPosition(BottomBarFreeLayoutCanvas);
        var width = Math.Max(1, BottomBarFreeLayoutCanvas.ActualWidth);
        SeedContinuousBottomBarPositions(draft);
        draft.HorizontalPositions[item.Id] =
            ClampBottomBarLayoutCenter(item.Id, pointer.X, width) / width;
        PackAndPersistBottomBarPositions(draft, width, item.Id);

        _bottomBarLayoutEditorDraft = draft;
        ApplyBottomBarLayoutPreview(draft);
        _bottomBarLayoutEditorWindow?.UpdateFromExternal(draft);
        _bottomBarLayoutDragElement = null;
        _bottomBarLayoutDragItem = null;
        e.Handled = true;
    }

    /// <summary>
    /// Déplace un bouton directement sur le Canvas de la vraie barre. Le
    /// glisser-déposer WPF bloquait auparavant le thread d'interface pendant
    /// toute l'opération; cette mise à jour légère ne reconstruit que les
    /// coordonnées et garde le rendu fluide à chaque mouvement de souris.
    /// </summary>
    private void UpdateBottomBarLayoutDrag(Point point)
    {
        if (_bottomBarLayoutDragItem is null ||
            BottomBarFreeLayoutCanvas.Visibility != Visibility.Visible ||
            BottomBarFreeLayoutCanvas.ActualWidth <= 0)
            return;

        var draft = _bottomBarLayoutEditorDraft ??
                    _bottomBarLayoutPresets.FirstOrDefault() ?? CreateCompactBottomBarLayout();
        // Pendant un glissement, compléter seulement les coordonnées manquantes.
        // Le tassement est ensuite calculé autour de l'item tenu, jamais dans
        // une direction globale prédéfinie.
        SeedContinuousBottomBarPositions(draft, false);
        var width = Math.Max(1, BottomBarFreeLayoutCanvas.ActualWidth);
        if (_bottomBarLayoutDragStartPositions.Count == 0)
        {
            foreach (var pair in draft.HorizontalPositions)
                _bottomBarLayoutDragStartPositions[pair.Key] = pair.Value;
        }
        var movingIds = _bottomBarLayoutSelectedItemIds.Contains(_bottomBarLayoutDragItem.Id)
            ? GetBottomBarLayoutVisualOrder()
                .Where(id => _bottomBarLayoutSelectedItemIds.Contains(id) &&
                             _bottomBarLayoutDragStartPositions.ContainsKey(id))
                .ToArray()
            : [_bottomBarLayoutDragItem.Id];
        if (movingIds.Length == 0)
            movingIds = [_bottomBarLayoutDragItem.Id];

        var requestedDelta = point.X - _bottomBarLayoutDragStart.X;
        var groupLeft = movingIds.Min(id =>
            (_bottomBarLayoutDragStartPositions[id] * width) -
            (Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d));
        var groupRight = movingIds.Max(id =>
            (_bottomBarLayoutDragStartPositions[id] * width) +
            (Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d));
        var minimumDelta = -groupLeft;
        var maximumDelta = width - groupRight;
        var delta = minimumDelta <= maximumDelta
            ? Math.Clamp(requestedDelta, minimumDelta, maximumDelta)
            : 0d;
        foreach (var id in movingIds)
        {
            if (string.Equals(id, draft.CenterLockedItemId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var center = (_bottomBarLayoutDragStartPositions[id] * width) + delta;
            draft.HorizontalPositions[id] = ClampBottomBarLayoutCenter(id, center, width) / width;
        }
        if (!_bottomBarLayoutFreeDragMode)
            PushBottomBarLayoutItemsDuringDrag(draft, width, movingIds, delta);
        _bottomBarLayoutEditorDraft = draft;
        _appliedBottomBarLayout = CloneBottomBarLayout(draft);
        PositionBottomBarFreeLayout();
    }

    private void PushBottomBarLayoutItemsDuringDrag(
        BottomBarLayoutPresetData preset,
        double width,
        IReadOnlyCollection<string> movingIds,
        double delta)
    {
        if (movingIds.Count == 0 || Math.Abs(delta) < 0.01)
            return;

        var moving = movingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allIds = EnumerateBottomBarLayoutIds(preset)
            .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) && element.IsVisible)
            .ToArray();
        var spacing = Math.Clamp(preset.Spacing, 0, 24);
        var startCenters = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var currentCenters = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in allIds)
        {
            startCenters[id] = _bottomBarLayoutDragStartPositions.TryGetValue(id, out var start)
                ? start * width
                : 0.5 * width;
            currentCenters[id] = preset.HorizontalPositions.TryGetValue(id, out var current)
                ? ClampBottomBarLayoutCenter(id, current * width, width)
                : startCenters[id];
        }
        var movingStartLeft = movingIds.Min(id =>
            startCenters[id] - (Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d));
        var movingStartRight = movingIds.Max(id =>
            startCenters[id] + (Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d));
        var movingLeft = movingIds.Min(id =>
            currentCenters[id] - (Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d));
        var movingRight = movingIds.Max(id =>
            currentCenters[id] + (Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d));
        var lockedId = preset.CenterLockedItemId;
        var lockedCenter = !string.IsNullOrWhiteSpace(lockedId) &&
                           startCenters.ContainsKey(lockedId)
            ? GetCenterLockedBottomBarTarget(lockedId, width)
            : (double?)null;
        var movingCenter = (movingLeft + movingRight) / 2d;

        // Le bouton verrouillé sépare les deux côtés sans transmettre une
        // poussée au côté opposé. Une fois que le centre du bouton tenu a
        // réellement franchi le verrou, il peut pousser les commandes de son
        // nouveau côté. La règle est strictement symétrique gauche/droite.
        bool IsOnMovingSideOfCenterLock(string id)
        {
            if (lockedCenter is not double center ||
                string.Equals(id, lockedId, StringComparison.OrdinalIgnoreCase))
                return !string.Equals(id, lockedId, StringComparison.OrdinalIgnoreCase);

            if (movingCenter < center - 0.01)
                return startCenters[id] < center;
            if (movingCenter > center + 0.01)
                return startCenters[id] > center;
            return delta > 0 ? startCenters[id] > center : startCenters[id] < center;
        }

        if (delta > 0)
        {
            var plannedCenters = new List<(string Id, double Center, double HalfWidth)>();
            var plannedRight = movingRight;
            var blockedByEdge = false;
            foreach (var id in allIds
                         .Where(id => !moving.Contains(id) &&
                                      startCenters[id] > movingStartRight &&
                                      IsOnMovingSideOfCenterLock(id))
                         .OrderBy(id => startCenters[id]))
            {
                var halfWidth = Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d;
                var left = currentCenters[id] - halfWidth;
                if (left >= plannedRight + spacing)
                    break;
                var requestedCenter = plannedRight + spacing + halfWidth;
                if (requestedCenter > width - halfWidth)
                {
                    blockedByEdge = true;
                    break;
                }
                plannedCenters.Add((id, requestedCenter, halfWidth));
                plannedRight = requestedCenter + halfWidth;
            }

            // Ne jamais conserver une poussée partielle. Si le dernier bouton
            // de la chaîne atteint le bord, les premiers doivent eux aussi
            // rester à leur position précédente; sinon ils se compactent peu
            // à peu les uns sur les autres à chaque mouvement de souris.
            if (!blockedByEdge)
            {
                foreach (var (id, center, halfWidth) in plannedCenters)
                {
                    currentCenters[id] = center;
                    preset.HorizontalPositions[id] = center / width;
                    movingRight = Math.Max(movingRight, center + halfWidth);
                }
            }
        }
        else
        {
            var plannedCenters = new List<(string Id, double Center, double HalfWidth)>();
            var plannedLeft = movingLeft;
            var blockedByEdge = false;
            foreach (var id in allIds
                         .Where(id => !moving.Contains(id) &&
                                      startCenters[id] < movingStartLeft &&
                                      IsOnMovingSideOfCenterLock(id))
                         .OrderByDescending(id => startCenters[id]))
            {
                var halfWidth = Math.Min(width, GetBottomBarLayoutElementWidth(id)) / 2d;
                var right = currentCenters[id] + halfWidth;
                if (right <= plannedLeft - spacing)
                    break;
                var requestedCenter = plannedLeft - spacing - halfWidth;
                if (requestedCenter < halfWidth)
                {
                    blockedByEdge = true;
                    break;
                }
                plannedCenters.Add((id, requestedCenter, halfWidth));
                plannedLeft = requestedCenter - halfWidth;
            }

            if (!blockedByEdge)
            {
                foreach (var (id, center, halfWidth) in plannedCenters)
                {
                    currentCenters[id] = center;
                    preset.HorizontalPositions[id] = center / width;
                    movingLeft = Math.Min(movingLeft, center - halfWidth);
                }
            }
        }
    }

    private void BeginBottomBarLayoutDragHistory()
    {
        var snapshot = _bottomBarLayoutEditorDraft ?? _appliedBottomBarLayout;
        if (snapshot is null)
            return;

        SeedContinuousBottomBarPositions(snapshot, false);
        _bottomBarLayoutDragStartPositions.Clear();
        foreach (var pair in snapshot.HorizontalPositions)
            _bottomBarLayoutDragStartPositions[pair.Key] = pair.Value;
        _bottomBarLayoutEditorWindow?.BeginExternalEdit(CloneBottomBarLayout(snapshot));
    }

    private void CommitBottomBarLayoutDrag()
    {
        if (_bottomBarLayoutDragItem is null || _bottomBarLayoutEditorDraft is null ||
            BottomBarFreeLayoutCanvas.ActualWidth <= 0)
            return;

        var width = Math.Max(1, BottomBarFreeLayoutCanvas.ActualWidth);
        var movingIds = _bottomBarLayoutSelectedItemIds.Contains(_bottomBarLayoutDragItem.Id)
            ? GetBottomBarLayoutVisualOrder()
                .Where(id => _bottomBarLayoutSelectedItemIds.Contains(id))
                .ToArray()
            : [_bottomBarLayoutDragItem.Id];
        if (!ResolveBottomBarLayoutDropOverlap(
                _bottomBarLayoutEditorDraft, width, movingIds))
            PersistCurrentBottomBarPositions(_bottomBarLayoutEditorDraft, width);
        _appliedBottomBarLayout = CloneBottomBarLayout(_bottomBarLayoutEditorDraft);
        // Dès le relâchement, on quitte le rendu superposé et on affiche les
        // commandes repoussées de part et d'autre du point de dépôt.
        _bottomBarLayoutDragActive = false;
        PositionBottomBarFreeLayout();
        Dispatcher.BeginInvoke(() =>
        {
            PositionBottomBarFreeLayout();
            UpdateBottomBarLayoutItemBounds();
        }, DispatcherPriority.Render);
    }

    private bool ResolveBottomBarLayoutDropOverlap(
        BottomBarLayoutPresetData preset,
        double width,
        IReadOnlyCollection<string> movingIds)
    {
        width = Math.Max(1, width);
        var moving = movingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (moving.Count == 0)
            return false;

        var visibleIds = EnumerateBottomBarLayoutIds(preset)
            .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) &&
                         element.IsVisible)
            .ToArray();
        var centers = visibleIds.ToDictionary(
            id => id,
            id => ClampBottomBarLayoutCenter(
                id,
                (preset.HorizontalPositions.TryGetValue(id, out var position)
                    ? position
                    : 0.5) * width,
                width),
            StringComparer.OrdinalIgnoreCase);
        var widths = visibleIds.ToDictionary(
            id => id,
            id => Math.Min(width, GetBottomBarLayoutElementWidth(id)),
            StringComparer.OrdinalIgnoreCase);
        var spacing = Math.Clamp(preset.Spacing, 0, 24);

        var hasMovingOverlap = moving.Any(movingId =>
            centers.ContainsKey(movingId) && visibleIds.Any(otherId =>
                !moving.Contains(otherId) &&
                Math.Abs(centers[movingId] - centers[otherId]) <
                ((widths[movingId] + widths[otherId]) / 2d) + spacing - 0.5));
        if (!hasMovingOverlap)
            return false;

        var orderedMoving = moving
            .Where(centers.ContainsKey)
            .OrderBy(id => _bottomBarLayoutDragStartPositions.TryGetValue(id, out var start)
                ? start
                : centers[id] / width)
            .ToArray();
        if (orderedMoving.Length == 0)
            return false;

        var movingLeft = orderedMoving.Min(id => centers[id] - (widths[id] / 2d));
        var movingRight = orderedMoving.Max(id => centers[id] + (widths[id] / 2d));
        var movingCenter = (movingLeft + movingRight) / 2d;
        var stationary = visibleIds
            .Where(id => !moving.Contains(id))
            .OrderBy(id => centers[id])
            .ToList();

        // Le milieu du bouton survolé sert de seuil : avant son milieu, le
        // groupe est inséré à gauche; après son milieu, il est inséré à droite.
        var insertionIndex = stationary.Count(id => centers[id] < movingCenter);
        var order = stationary.Take(insertionIndex)
            .Concat(orderedMoving)
            .Concat(stationary.Skip(insertionIndex))
            .ToArray();

        var orderedWidths = order.Select(id => widths[id]).ToArray();
        var availableSpacing = order.Length > 1
            ? Math.Max(0d, (width - orderedWidths.Sum()) / (order.Length - 1d))
            : spacing;
        var effectiveSpacing = Math.Min(spacing, availableSpacing);
        var resolvedCenters = order.Select(id => centers[id]).ToArray();

        // Projeter uniquement les positions qui violent l'ordre choisi. Les
        // écarts déjà valides sont conservés, donc les amplitudes de la mise en
        // page ne sont pas compactées inutilement.
        for (var pass = 0; pass < 3; pass++)
        {
            resolvedCenters[0] = Math.Max(resolvedCenters[0], orderedWidths[0] / 2d);
            for (var index = 1; index < order.Length; index++)
            {
                var minimum = resolvedCenters[index - 1] +
                              (orderedWidths[index - 1] / 2d) + effectiveSpacing +
                              (orderedWidths[index] / 2d);
                resolvedCenters[index] = Math.Max(resolvedCenters[index], minimum);
            }

            resolvedCenters[^1] = Math.Min(
                resolvedCenters[^1], width - (orderedWidths[^1] / 2d));
            for (var index = order.Length - 2; index >= 0; index--)
            {
                var maximum = resolvedCenters[index + 1] -
                              (orderedWidths[index + 1] / 2d) - effectiveSpacing -
                              (orderedWidths[index] / 2d);
                resolvedCenters[index] = Math.Min(resolvedCenters[index], maximum);
            }
        }

        var resolved = order
            .Select((id, index) => (Id: id, Center: resolvedCenters[index]))
            .ToList();
        ApplyCenterLockedBottomBarItem(resolved, preset, width);
        foreach (var (id, center) in resolved)
            preset.HorizontalPositions[id] = Math.Clamp(center / width, 0d, 1d);
        return true;
    }

    private void PersistCurrentBottomBarPositions(BottomBarLayoutPresetData preset, double width)
    {
        var positions = EnumerateBottomBarLayoutIds(preset)
            .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) && element.IsVisible)
            .Select(id =>
            {
                var normalized = preset.HorizontalPositions.TryGetValue(id, out var value)
                    ? value
                    : 0.5;
                return (Id: id, Center: ClampBottomBarLayoutCenter(id, normalized * width, width));
            })
            .ToList();
        ApplyCenterLockedBottomBarItem(positions, preset, width);
        foreach (var (id, center) in positions)
            preset.HorizontalPositions[id] = Math.Clamp(center / width, 0d, 1d);
    }

    private bool HasOverlappingBottomBarLayoutItems(BottomBarLayoutPresetData preset, double width)
    {
        var positions = EnumerateBottomBarLayoutIds(preset)
            .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) && element.IsVisible)
            .Select(id =>
            {
                var normalized = preset.HorizontalPositions.TryGetValue(id, out var value)
                    ? value
                    : 0.5;
                return (Id: id, Center: ClampBottomBarLayoutCenter(id, normalized * width, width));
            })
            .OrderBy(item => item.Center)
            .ToArray();
        var spacing = Math.Clamp(preset.Spacing, 0, 24);
        for (var index = 1; index < positions.Length; index++)
        {
            var previous = positions[index - 1];
            var current = positions[index];
            var previousWidth = Math.Min(width, GetBottomBarLayoutElementWidth(previous.Id));
            var currentWidth = Math.Min(width, GetBottomBarLayoutElementWidth(current.Id));
            var gap = current.Center - (currentWidth / 2d) -
                      (previous.Center + (previousWidth / 2d));
            if (gap < spacing - 0.5)
                return true;
        }
        return false;
    }

    private void BeginBottomBarLayoutPreview()
    {
        _bottomBarLayoutPreviewTitle = NowPlayingTitle.Text;
        _bottomBarLayoutPreviewActive = true;
        // Dans l'éditeur, le volume reste toujours un item visible et
        // déplaçable, même lorsque le style de lecture utilise un indicateur
        // éphémère. En lecture normale, ces deux styles continuent à ne laisser
        // aucun espace entre Muet et Plein écran.
        VolumeControlHost.Width = _volumeControlStyle == 3 ? 150 : 100;
        VolumeControlHost.Visibility = Visibility.Visible;
        BottomBarLayoutVolumePlaceholder.Visibility = Visibility.Visible;
        _bottomBarLayoutPreviewOverlayWasVisible = _videoOverlayWindow?.IsVisible == true;
        _bottomBarLayoutPreviewControlsWereVisible =
            ControlsPanel.Visibility == Visibility.Visible && ControlsPanel.IsHitTestVisible;

        // L’éditeur dédié doit laisser la vraie ligne de commandes ouverte et
        // stable pendant toute la modification. Le minuteur normal est arrêté
        // jusqu’à la fermeture de cette fenêtre.
        ShowVideoOverlayWindow();
        RevealPlaybackControls();
        // Neutralise toute animation résiduelle et verrouille visuellement la
        // vraie barre pendant l’édition.
        ControlsPanel.BeginAnimation(OpacityProperty, null);
        ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ControlsPanel.Visibility = Visibility.Visible;
        ControlsPanel.IsHitTestVisible = true;
        ControlsPanel.Opacity = 1;
        ControlsTranslate.Y = 0;
        _controlsHideTimer.Stop();
        _gearControlsHideTimer.Stop();
        if (string.IsNullOrWhiteSpace(NowPlayingTitle.Text) ||
            string.Equals(NowPlayingTitle.Text, "Aucun média", StringComparison.OrdinalIgnoreCase))
            NowPlayingTitle.Text = LocalizationService.Get("Nom de la vidéo — aperçu de la mise en page");
        UpdateTimelineText(0, 0);
        ApplyIntegratedBottomBarButtonVisibility();
        ApplyBottomBarLayoutGuideSettings(_bottomBarLayoutEditorDraft ??
            _bottomBarLayoutPresets.FirstOrDefault() ?? CreateCompactBottomBarLayout());
        BottomBarLayoutGuideOverlay.Visibility = Visibility.Visible;
        QueueBottomBarGuidePositionUpdate();
    }

    private void EndBottomBarLayoutPreview()
    {
        if (!_bottomBarLayoutPreviewActive)
            return;

        _bottomBarLayoutPreviewActive = false;
        BottomBarLayoutVolumePlaceholder.Visibility = Visibility.Collapsed;
        ApplyVolumeControlStyle();
        if (_bottomBarLayoutPreviewTitle is not null)
            NowPlayingTitle.Text = _bottomBarLayoutPreviewTitle;
        _bottomBarLayoutPreviewTitle = null;
        UpdateTimelineText(_pendingSeekTarget ?? _mediaPlayer.Time, _mediaPlayer.Length);
        SetBottomBarLayoutDragEnabled(false);
        BottomBarLayoutGuideOverlay.ReleaseMouseCapture();
        _bottomBarLayoutDragElement = null;
        _bottomBarLayoutDragItem = null;
        _bottomBarLayoutDragActive = false;
        _bottomBarLayoutDragStartPositions.Clear();
        _bottomBarLayoutSelectedItemIds.Clear();
        _bottomBarLayoutSelectionAnchorId = null;
        BottomBarLayoutItemBoundsCanvas.Children.Clear();
        _bottomBarLayoutItemBounds.Clear();
        BottomBarLayoutGuideOverlay.Visibility = Visibility.Collapsed;
        _controlsHideTimer.Stop();
        _gearControlsHideTimer.Stop();
        if (!_bottomBarLayoutPreviewControlsWereVisible)
            HidePlaybackControlsImmediately();
        else
            RestartControlsHideTimer();

        // La fenêtre modale de paramètres avait masqué l’overlay de lecture;
        // on le remasque dès que l’éditeur dédié est fermé. La fenêtre des
        // paramètres pourra ensuite le restaurer normalement à sa fermeture.
        if (!_bottomBarLayoutPreviewOverlayWasVisible)
            _videoOverlayWindow?.Hide();
        _bottomBarLayoutPreviewOverlayWasVisible = false;
        _bottomBarLayoutPreviewControlsWereVisible = false;
    }

    private void ApplyBottomBarLayout(BottomBarLayoutPresetData preset)
    {
        if (!_bottomBarLayoutInitialized)
            return;

        ApplyBottomBarLayoutGuideSettings(preset);

        _appliedBottomBarLayout = CloneBottomBarLayout(preset);
        PrepareBottomBarTimelineComposition(preset.SplitTimeline);
        // Appliquer immédiatement la largeur adaptée. L'éditeur réutilise ce
        // même chemin à chaque aperçu; laisser ici la valeur brute faisait
        // brièvement, et parfois durablement, réapparaître le titre non réduit.
        _effectiveBottomBarTitleWidth = Math.Clamp(
            preset.TitleWidth * GetBottomBarHighDpiAdjustment(), 80, 800);
        NowPlayingTitleHost.Width = _effectiveBottomBarTitleWidth;
        BottomBarLeftHost.Children.Clear();
        BottomBarCenterHost.Children.Clear();
        BottomBarRightHost.Children.Clear();
        BottomBarFreeLayoutCanvas.Children.Clear();
        var spacing = Math.Clamp(preset.Spacing, 0, 24);
        var lists = new[]
        {
            ExpandBottomBarLayoutIds(preset.LeftItems, preset.SplitTimeline).ToList(),
            ExpandBottomBarLayoutIds(preset.CenterItems, preset.SplitTimeline).ToList(),
            ExpandBottomBarLayoutIds(preset.RightItems, preset.SplitTimeline).ToList()
        };
        if (preset.HorizontalPositions.Count > 0)
        {
            BottomBarLegacyLayoutGrid.Visibility = Visibility.Collapsed;
            BottomBarFreeLayoutCanvas.Visibility = Visibility.Visible;
            foreach (var id in EnumerateBottomBarLayoutIds(preset))
            {
                if (!_bottomBarLayoutElements.TryGetValue(id, out var element))
                    continue;
                RemoveBottomBarElementFromParent(element);
                element.Margin = new Thickness(0);
                BottomBarFreeLayoutCanvas.Children.Add(element);
            }
        }
        else
        {
            BottomBarLegacyLayoutGrid.Visibility = Visibility.Visible;
            BottomBarFreeLayoutCanvas.Visibility = Visibility.Collapsed;
            var hosts = new[] { BottomBarLeftHost, BottomBarCenterHost, BottomBarRightHost };
            for (var groupIndex = 0; groupIndex < hosts.Length; groupIndex++)
            {
                var host = hosts[groupIndex];
                foreach (var id in lists[groupIndex])
                {
                    if (!_bottomBarLayoutElements.TryGetValue(id, out var element))
                        continue;
                    RemoveBottomBarElementFromParent(element);
                    element.Margin = new Thickness(spacing / 2d, 0, spacing / 2d, 0);
                    host.Children.Add(element);
                }
            }
        }

        DefaultBottomBarLayoutGrid.Visibility = Visibility.Collapsed;
        BottomBarCustomLayoutGrid.Visibility = Visibility.Visible;
        if (_bottomBarLayoutPreviewActive)
        {
            foreach (var id in EnumerateBottomBarLayoutIds(preset))
            {
                if (_bottomBarLayoutElements.TryGetValue(id, out var element))
                    element.Visibility = Visibility.Visible;
            }
        }
        // Le bouton de déplacement reste une commande intégrée optionnelle :
        // il n'est visible que si le modèle le contient et si l'utilisateur
        // l'a activé dans « Boutons intégrés à la barre inférieure ».
        VideoPanButton.Visibility = EnumerateBottomBarLayoutIds(preset)
            .Contains("pan", StringComparer.OrdinalIgnoreCase) &&
            (_bottomBarLayoutPreviewActive || _showVideoPanButton)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateNowPlayingTitleWidth();
        UpdateResponsiveBottomBarTitleWidthForCurrentLayout();
        QueueResponsiveInterfaceScaleUpdate();
        Dispatcher.BeginInvoke(PositionBottomBarFreeLayout, DispatcherPriority.Render);
    }

    private void PrepareBottomBarTimelineComposition(bool splitTimeline)
    {
        RemoveBottomBarElementFromParent(TimelineTextHost);
        RemoveBottomBarElementFromParent(ElapsedText);
        RemoveBottomBarElementFromParent(DurationText);
        RemoveBottomBarElementFromParent(TimelineSeparatorText);
        TimelineTextHost.Children.Clear();

        if (splitTimeline)
        {
            TimelineSeparatorText.Visibility = Visibility.Collapsed;
            return;
        }

        TimelineSeparatorText.Visibility = Visibility.Visible;
        TimelineTextHost.Children.Add(ElapsedText);
        TimelineTextHost.Children.Add(TimelineSeparatorText);
        TimelineTextHost.Children.Add(DurationText);
    }

    private static IEnumerable<string> ExpandBottomBarLayoutIds(
        IEnumerable<string> source, bool splitTimeline)
    {
        foreach (var id in source)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (splitTimeline && string.Equals(id, "timeline", StringComparison.OrdinalIgnoreCase))
            {
                yield return "elapsed_time";
                yield return "duration_time";
            }
            else
            {
                yield return id;
            }
        }
    }

    private static IEnumerable<string> EnumerateBottomBarLayoutIds(BottomBarLayoutPresetData preset) =>
        ExpandBottomBarLayoutIds(
                preset.LeftItems.Concat(preset.CenterItems).Concat(preset.RightItems),
                preset.SplitTimeline)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private void SeedContinuousBottomBarPositions(BottomBarLayoutPresetData preset,
        bool packPositions = true)
    {
        var ids = EnumerateBottomBarLayoutIds(preset).ToArray();
        // Toutes les coordonnées libres sont relatives à ce même rectangle.
        // ControlsContentGrid est plus large puisqu'il inclut les marges de
        // BottomBarCustomLayoutGrid; mélanger les deux créait un décalage qui
        // devenait particulièrement visible autour du centre verrouillé.
        var width = Math.Max(1, BottomBarCustomLayoutGrid.ActualWidth > 0
            ? BottomBarCustomLayoutGrid.ActualWidth
            : ControlsContentGrid.ActualWidth);
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            if (preset.HorizontalPositions.ContainsKey(id))
                continue;

            var fallback = (index + 1d) / (ids.Length + 1d);
            if (!_bottomBarLayoutElements.TryGetValue(id, out var element) ||
                !element.IsVisible || element.ActualWidth <= 0)
            {
                preset.HorizontalPositions[id] = fallback;
                continue;
            }

            try
            {
                var left = element.TranslatePoint(new Point(0, 0), BottomBarCustomLayoutGrid).X;
                preset.HorizontalPositions[id] = Math.Clamp((left + (element.ActualWidth / 2d)) / width, 0d, 1d);
            }
            catch (InvalidOperationException)
            {
                preset.HorizontalPositions[id] = fallback;
            }
        }

        if (packPositions)
            PackAndPersistBottomBarPositions(preset, width);
    }

    private void PackAndPersistBottomBarPositions(BottomBarLayoutPresetData preset, double width,
        string? anchorId = null)
    {
        var positions = CalculatePackedBottomBarCenters(preset, width, anchorId);
        ApplyCenterLockedBottomBarItem(positions, preset, width);
        foreach (var (id, center) in positions)
            preset.HorizontalPositions[id] = Math.Clamp(center / Math.Max(1, width), 0d, 1d);
    }

    /// <summary>
    /// Maintient la commande choisie par l'utilisateur au centre exact de la
    /// barre. Les commandes voisines sont repoussées de chaque côté, sans
    /// déplacer le verrou lui-même. Le calcul est volontairement indépendant
    /// du mode de déplacement libre afin que le verrou reste effectif pendant
    /// l'édition et après l'enregistrement du modèle.
    /// </summary>
    private double GetCenterLockedBottomBarTarget(string id, double width)
    {
        width = Math.Max(1, width);
        // width correspond déjà à la surface intérieure du Canvas. Ajouter
        // encore les marges du conteneur déplacerait artificiellement le
        // verrou central vers la droite.
        var canvasCenter = width / 2d;
        try
        {
            var guideWidth = BottomBarLayoutVerticalGuides.ActualWidth;
            if (guideWidth > 0)
            {
                var guideCenter = BottomBarLayoutVerticalGuides.TranslatePoint(
                    new Point(guideWidth / 2d, 0), BottomBarFreeLayoutCanvas).X;
                if (double.IsFinite(guideCenter))
                    canvasCenter = guideCenter;
            }
        }
        catch (InvalidOperationException)
        {
            // Pendant une passe de mesure, le repère peut ne pas encore être
            // relié au Canvas. Le calcul basé sur les marges sert de repli.
        }
        return ClampBottomBarLayoutCenter(id, canvasCenter, width);
    }

    private void KeepCenterLockedBottomBarItemFixed(
        List<(string Id, double Center)> positions,
        BottomBarLayoutPresetData preset,
        double width)
    {
        var lockedId = preset.CenterLockedItemId;
        if (string.IsNullOrWhiteSpace(lockedId))
            return;
        var index = positions.FindIndex(item =>
            string.Equals(item.Id, lockedId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            positions[index] = (positions[index].Id,
                GetCenterLockedBottomBarTarget(lockedId, width));
    }

    private void ApplyCenterLockedBottomBarItem(
        List<(string Id, double Center)> positions,
        BottomBarLayoutPresetData preset,
        double width)
    {
        var lockedId = preset.CenterLockedItemId;
        if (string.IsNullOrWhiteSpace(lockedId) || positions.Count == 0)
            return;

        var ordered = positions
            .OrderBy(item => item.Center)
            .ToList();
        var lockedIndex = ordered.FindIndex(item =>
            string.Equals(item.Id, lockedId, StringComparison.OrdinalIgnoreCase));
        if (lockedIndex < 0)
            return;

        width = Math.Max(1, width);
        var widths = ordered
            .Select(item => Math.Min(width, GetBottomBarLayoutElementWidth(item.Id)))
            .ToArray();
        var configuredSpacing = Math.Clamp(preset.Spacing, 0, 24);
        var availableSpacing = ordered.Count > 1
            ? Math.Max(0, (width - widths.Sum()) / (ordered.Count - 1d))
            : configuredSpacing;
        var spacing = Math.Min(configuredSpacing, availableSpacing);
        var centers = ordered.Select(item => item.Center).ToArray();
        centers[lockedIndex] = GetCenterLockedBottomBarTarget(lockedId, width);

        for (var index = lockedIndex - 1; index >= 0; index--)
        {
            var maximum = centers[index + 1] - (widths[index + 1] / 2d) -
                          spacing - (widths[index] / 2d);
            centers[index] = Math.Min(centers[index], maximum);
        }
        for (var index = lockedIndex + 1; index < centers.Length; index++)
        {
            var minimum = centers[index - 1] + (widths[index - 1] / 2d) +
                          spacing + (widths[index] / 2d);
            centers[index] = Math.Max(centers[index], minimum);
        }

        // Recentrer chaque côté si sa première commande a touché un bord. Le
        // déplacement reste limité au côté concerné et le bouton verrouillé
        // conserve ainsi exactement la position 50 %.
        if (lockedIndex > 0 && centers[0] < widths[0] / 2d)
        {
            var delta = (widths[0] / 2d) - centers[0];
            for (var index = 0; index < lockedIndex; index++)
                centers[index] += delta;
        }
        if (lockedIndex < centers.Length - 1 &&
            centers[^1] > width - (widths[^1] / 2d))
        {
            var delta = (width - (widths[^1] / 2d)) - centers[^1];
            for (var index = lockedIndex + 1; index < centers.Length; index++)
                centers[index] += delta;
        }

        positions.Clear();
        for (var index = 0; index < ordered.Count; index++)
            positions.Add((ordered[index].Id, centers[index]));
    }

    private void ResolveRuntimeBottomBarOverlaps(
        List<(string Id, double Center)> positions,
        BottomBarLayoutPresetData preset,
        double width)
    {
        if (IsBottomBarLayoutEditing || positions.Count < 2)
            return;

        width = Math.Max(1, width);
        var ordered = positions.OrderBy(item => item.Center).ToArray();
        var centers = ordered.Select(item => item.Center).ToArray();
        var widths = ordered.Select(item =>
            Math.Min(width, GetBottomBarLayoutElementWidth(item.Id))).ToArray();
        var spacing = Math.Clamp(preset.Spacing, 0, 24);
        var anchorIndex = !string.IsNullOrWhiteSpace(preset.CenterLockedItemId)
            ? Array.FindIndex(ordered, item => string.Equals(item.Id,
                preset.CenterLockedItemId, StringComparison.OrdinalIgnoreCase))
            : -1;
        if (anchorIndex < 0)
            anchorIndex = Enumerable.Range(0, ordered.Length)
                .OrderBy(index => Math.Abs(centers[index] - width / 2d))
                .First();

        // Les coordonnées choisies restent inchangées si elles ne se touchent
        // pas. Une collision est repoussée depuis le centre vers l'extérieur,
        // ce qui empêche notamment les deux compteurs de temps de se superposer.
        for (var index = anchorIndex - 1; index >= 0; index--)
        {
            var maximum = centers[index + 1] - widths[index + 1] / 2d -
                          spacing - widths[index] / 2d;
            centers[index] = Math.Min(centers[index], maximum);
        }
        for (var index = anchorIndex + 1; index < centers.Length; index++)
        {
            var minimum = centers[index - 1] + widths[index - 1] / 2d +
                          spacing + widths[index] / 2d;
            centers[index] = Math.Max(centers[index], minimum);
        }

        positions.Clear();
        for (var index = 0; index < ordered.Length; index++)
            positions.Add((ordered[index].Id,
                ClampBottomBarLayoutCenter(ordered[index].Id, centers[index], width)));
    }

    private double GetBottomBarLayoutElementWidth(string id)
    {
        if (string.Equals(id, "volume", StringComparison.OrdinalIgnoreCase))
        {
            // L'éditeur doit conserver une vraie surface de sélection et de
            // déplacement pour le volume dans chacun des quatre styles.
            if (_bottomBarLayoutPreviewActive)
                return _volumeControlStyle == 3 ? 150d : 100d;

            // ActualWidth peut encore contenir la mesure de l'ancien style.
            // Utiliser les largeurs réelles garantit que la barre horizontale
            // ne passe jamais sous le bouton Plein écran.
            return _volumeControlStyle switch
            {
                0 => 100d,
                3 => 150d,
                _ => 0d
            };
        }
        if (string.Equals(id, "title", StringComparison.OrdinalIgnoreCase) &&
            double.IsFinite(_effectiveBottomBarTitleWidth))
            return _effectiveBottomBarTitleWidth;
        if (!_bottomBarLayoutElements.TryGetValue(id, out var element))
            return 34d;
        if (element.ActualWidth > 1)
            return element.ActualWidth;
        if (!double.IsNaN(element.Width) && element.Width > 1)
            return element.Width;
        return string.Equals(id, "title", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(_appliedBottomBarLayout?.TitleWidth ?? 230, 80, 800)
            : 34d;
    }

    private double ClampBottomBarLayoutCenter(string id, double requestedCenter, double availableWidth)
    {
        availableWidth = Math.Max(1, availableWidth);
        var elementWidth = Math.Min(availableWidth, GetBottomBarLayoutElementWidth(id));
        var halfWidth = elementWidth / 2d;
        return Math.Clamp(requestedCenter, halfWidth, availableWidth - halfWidth);
    }

    private List<(string Id, double Center)> CalculatePackedBottomBarCenters(
        BottomBarLayoutPresetData preset, double width, string? anchorId = null)
    {
        width = Math.Max(1, width);
        var keepDragOrder = !string.IsNullOrWhiteSpace(anchorId) &&
                            _bottomBarLayoutPreviewActive &&
                            _bottomBarLayoutDragActive &&
                            !_bottomBarLayoutFreeDragMode;
        var ids = EnumerateBottomBarLayoutIds(preset)
            .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) &&
                         element.Visibility == Visibility.Visible)
            .OrderBy(id => keepDragOrder &&
                           _bottomBarLayoutDragStartPositions.TryGetValue(id, out var dragPosition)
                ? dragPosition
                : preset.HorizontalPositions.TryGetValue(id, out var position) ? position : 0.5)
            .ToArray();
        if (ids.Length == 0)
            return [];

        var widths = ids.Select(id => Math.Min(width, GetBottomBarLayoutElementWidth(id))).ToArray();
        var configuredSpacing = Math.Clamp(preset.Spacing, 0, 24);
        var availableSpacing = ids.Length > 1
            ? Math.Max(0, (width - widths.Sum()) / (ids.Length - 1d))
            : configuredSpacing;
        // Si la fenêtre devient très étroite, l'espace est réduit avant de
        // permettre à deux commandes de se chevaucher. Dans les dimensions
        // normales, l'espacement choisi par l'utilisateur reste inchangé.
        var spacing = Math.Min(configuredSpacing, availableSpacing);
        var requestedCenters = ids.Select((id, index) =>
            ClampBottomBarLayoutCenter(id,
                (preset.HorizontalPositions.TryGetValue(id, out var position)
                    ? position
                    : (index + 1d) / (ids.Length + 1d)) * width,
                width)).ToArray();
        var centers = new double[ids.Length];
        var anchorIndex = string.IsNullOrWhiteSpace(anchorId)
            ? -1
            : Array.FindIndex(ids,
                id => string.Equals(id, anchorId, StringComparison.OrdinalIgnoreCase));
        if (anchorIndex >= 0)
        {
            Array.Copy(requestedCenters, centers, requestedCenters.Length);
            for (var index = anchorIndex - 1; index >= 0; index--)
            {
                var maximum = centers[index + 1] - (widths[index + 1] / 2d) -
                              spacing - (widths[index] / 2d);
                centers[index] = Math.Min(centers[index], maximum);
            }
            for (var index = anchorIndex + 1; index < centers.Length; index++)
            {
                var minimum = centers[index - 1] + (widths[index - 1] / 2d) +
                              spacing + (widths[index] / 2d);
                centers[index] = Math.Max(centers[index], minimum);
            }

            // Corriger les limites localement. Déplacer tout le groupe d'un
            // seul bloc pouvait repousser le bord opposé hors de la barre, où
            // les commandes finissaient ensuite toutes compactées au même X.
            centers[0] = Math.Max(centers[0], widths[0] / 2d);
            for (var index = 1; index < centers.Length; index++)
            {
                var minimum = centers[index - 1] + (widths[index - 1] / 2d) +
                              spacing + (widths[index] / 2d);
                centers[index] = Math.Max(centers[index], minimum);
            }
            centers[^1] = Math.Min(centers[^1], width - (widths[^1] / 2d));
            for (var index = centers.Length - 2; index >= 0; index--)
            {
                var maximum = centers[index + 1] - (widths[index + 1] / 2d) -
                              spacing - (widths[index] / 2d);
                centers[index] = Math.Min(centers[index], maximum);
            }

            return ids.Select((id, index) => (id, centers[index])).ToList();
        }

        // Hors déplacement avec ancre, l'espacement automatique représente
        // l'écart visuel choisi entre les commandes d'une même zone. Les
        // anciennes positions pouvaient conserver un ancien écart et rendre
        // les valeurs 0 à 3 pratiquement invisibles, alors que la valeur 4
        // semblait être un minimum. Recomposer les trois zones séparément
        // rend toute la plage effective sans déplacer un groupe dans la zone
        // voisine.
        if (CalculateRegionPackedBottomBarCenters(
                preset, width, ids, widths, spacing) is { } regionCenters)
            return regionCenters;

        for (var index = 0; index < ids.Length; index++)
        {
            var requested = requestedCenters[index];
            var minimum = index == 0
                ? widths[index] / 2d
                : centers[index - 1] + (widths[index - 1] / 2d) + spacing + (widths[index] / 2d);
            centers[index] = Math.Max(requested, minimum);
        }

        // Si la dernière commande dépasse le bord droit, corriger depuis la
        // droite vers la gauche. Déplacer toute la ligne d'un seul bloc vers
        // la gauche compactait visuellement les éléments de gauche, même si
        // leur propre zone disposait encore d'espace.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var index = centers.Length - 1; index >= 0; index--)
            {
                var maximum = index == centers.Length - 1
                    ? width - (widths[index] / 2d)
                    : centers[index + 1] - (widths[index + 1] / 2d) -
                      spacing - (widths[index] / 2d);
                centers[index] = Math.Min(centers[index], maximum);
            }

            // Revenir vers la droite seulement lorsque le premier élément a
            // dépassé le bord gauche. En temps normal, les positions de gauche
            // restent donc ancrées à leur demande initiale.
            for (var index = 0; index < centers.Length; index++)
            {
                var minimum = index == 0
                    ? widths[index] / 2d
                    : centers[index - 1] + (widths[index - 1] / 2d) +
                      spacing + (widths[index] / 2d);
                centers[index] = Math.Max(centers[index], minimum);
            }
        }

        return ids.Select((id, index) => (id, centers[index])).ToList();
    }

    private List<(string Id, double Center)>? CalculateRegionPackedBottomBarCenters(
        BottomBarLayoutPresetData preset, double width, string[] ids, double[] widths,
        double spacing)
    {
        var groups = new List<(int Index, double Position)>[3]
        {
            [], [], []
        };
        for (var index = 0; index < ids.Length; index++)
        {
            var position = preset.HorizontalPositions.TryGetValue(ids[index], out var value)
                ? value
                : (index + 1d) / (ids.Length + 1d);
            var groupIndex = position < (1d / 3d)
                ? 0
                : position <= (2d / 3d) ? 1 : 2;
            groups[groupIndex].Add((index, position));
        }

        var orderedGroups = groups
            .Select(group => group
                .OrderBy(item => item.Position)
                .ThenBy(item => item.Index)
                .ToArray())
            .ToArray();
        var groupWidths = orderedGroups
            .Select(group => group.Sum(item => widths[item.Index]) +
                             (spacing * Math.Max(0, group.Length - 1)))
            .ToArray();
        if (groupWidths.Sum() > width + 0.01)
            return null;

        // Les groupes gauche et droit restent ancrés à leurs bords. Le groupe
        // central utilise ensuite l'espace restant et demeure centré autant
        // que possible. Cette méthode ne suppose plus qu'un groupe tient dans
        // un tiers de la fenêtre : c'était la raison pour laquelle les icônes
        // autres que le titre gardaient leur ancien espacement.
        var starts = new double[3];
        starts[0] = 0;
        starts[2] = width - groupWidths[2];
        var leftEnd = orderedGroups[0].Length > 0 ? groupWidths[0] : 0;
        var rightStart = orderedGroups[2].Length > 0 ? starts[2] : width;
        if (orderedGroups[1].Length > 0)
        {
            var centeredStart = (width - groupWidths[1]) / 2d;
            var minimumStart = leftEnd;
            var maximumStart = rightStart - groupWidths[1];
            if (minimumStart > maximumStart + 0.01)
                return null;
            starts[1] = Math.Clamp(centeredStart, minimumStart, maximumStart);
        }

        var centers = new double[ids.Length];
        for (var groupIndex = 0; groupIndex < orderedGroups.Length; groupIndex++)
        {
            var start = starts[groupIndex];
            foreach (var item in orderedGroups[groupIndex])
            {
                centers[item.Index] = start + (widths[item.Index] / 2d);
                start += widths[item.Index] + spacing;
            }
        }

        return ids.Select((id, index) => (id, centers[index])).ToList();
    }

    private bool HasHiddenBottomBarLayoutItems(BottomBarLayoutPresetData preset) =>
        EnumerateBottomBarLayoutIds(preset).Any(id =>
            !_bottomBarLayoutElements.TryGetValue(id, out var element) ||
            element.Visibility != Visibility.Visible);

    private bool IsBottomBarLayoutEditing =>
        _bottomBarLayoutPreviewActive || _bottomBarLayoutEditorWindow?.IsVisible == true;

    private List<(string Id, double Center)> CalculateCompactedBottomBarCenters(
        BottomBarLayoutPresetData preset, double width)
    {
        // La compaction des éléments absents ne concerne que l'affichage
        // normal. L'éditeur doit toujours refléter les positions choisies,
        // même si le paramètre global est activé.
        if (IsBottomBarLayoutEditing)
            return CalculatePackedBottomBarCenters(preset, width);

        width = Math.Max(1, width);
        var ids = EnumerateBottomBarLayoutIds(preset).ToArray();
        var count = Math.Max(1, ids.Length);
        var spacing = Math.Clamp(preset.Spacing, 0, 24);
        var items = ids
            .Select((id, index) =>
            {
                var normalized = preset.HorizontalPositions.TryGetValue(id, out var value)
                    ? value
                    : (index + 1d) / (count + 1d);
                var visible = _bottomBarLayoutElements.TryGetValue(id, out var element) &&
                              element.Visibility == Visibility.Visible;
                return new
                {
                    Id = id,
                    SourceIndex = index,
                    Position = normalized,
                    Center = ClampBottomBarLayoutCenter(id, normalized * width, width),
                    Width = Math.Min(width, GetBottomBarLayoutElementWidth(id)),
                    Visible = visible
                };
            })
            .OrderBy(item => item.Position)
            .ThenBy(item => item.SourceIndex)
            .ToArray();
        var centers = items
            .Where(item => item.Visible)
            .ToDictionary(item => item.Id, item => item.Center,
                StringComparer.OrdinalIgnoreCase);

        // À gauche, un trou est fermé en plaçant le prochain bouton visible à
        // l'espacement exact du précédent. Le même déplacement est propagé à
        // la suite du groupe afin de préserver ses autres écarts personnalisés.
        var shift = 0d;
        var hasPrevious = false;
        var hiddenBeforeFirst = false;
        var hiddenSincePrevious = false;
        var previousCenter = 0d;
        var previousWidth = 0d;
        foreach (var item in items.Where(item => item.Position < (1d / 3d)))
        {
            if (!item.Visible)
            {
                if (hasPrevious)
                    hiddenSincePrevious = true;
                else
                    hiddenBeforeFirst = true;
                continue;
            }

            var center = item.Center + shift;
            if (!hasPrevious && hiddenBeforeFirst)
            {
                var desired = item.Width / 2d;
                shift += desired - center;
                center = desired;
            }
            else if (hasPrevious && hiddenSincePrevious)
            {
                var desired = previousCenter + (previousWidth / 2d) +
                              spacing + (item.Width / 2d);
                shift += desired - center;
                center = desired;
            }
            center = ClampBottomBarLayoutCenter(item.Id, center, width);
            centers[item.Id] = center;
            previousCenter = center;
            previousWidth = item.Width;
            hasPrevious = true;
            hiddenSincePrevious = false;
        }

        // À droite, la même règle est appliquée en miroir depuis le bord droit.
        shift = 0d;
        hasPrevious = false;
        hiddenBeforeFirst = false;
        hiddenSincePrevious = false;
        previousCenter = 0d;
        previousWidth = 0d;
        foreach (var item in items.Where(item => item.Position > (2d / 3d)).Reverse())
        {
            if (!item.Visible)
            {
                if (hasPrevious)
                    hiddenSincePrevious = true;
                else
                    hiddenBeforeFirst = true;
                continue;
            }

            var center = item.Center + shift;
            if (!hasPrevious && hiddenBeforeFirst)
            {
                var desired = width - item.Width / 2d;
                shift += desired - center;
                center = desired;
            }
            else if (hasPrevious && hiddenSincePrevious)
            {
                var desired = previousCenter - (previousWidth / 2d) -
                              spacing - (item.Width / 2d);
                shift += desired - center;
                center = desired;
            }
            center = ClampBottomBarLayoutCenter(item.Id, center, width);
            centers[item.Id] = center;
            previousCenter = center;
            previousWidth = item.Width;
            hasPrevious = true;
            hiddenSincePrevious = false;
        }

        // Le groupe central se referme vers son ancre. Le bouton verrouillé est
        // l'ancre naturelle; sinon la commande visible la plus proche de 50 %
        // remplit ce rôle. Rien n'est recomposé en une nouvelle rangée.
        var centerItems = items
            .Where(item => item.Position is >= (1d / 3d) and <= (2d / 3d))
            .ToArray();
        var anchor = centerItems.FirstOrDefault(item => item.Visible &&
            string.Equals(item.Id, preset.CenterLockedItemId,
                StringComparison.OrdinalIgnoreCase))
            ?? centerItems.Where(item => item.Visible)
                .OrderBy(item => Math.Abs(item.Position - 0.5d))
                .FirstOrDefault();
        if (anchor is not null)
        {
            shift = 0d;
            previousCenter = centers[anchor.Id];
            previousWidth = anchor.Width;
            hiddenSincePrevious = false;
            foreach (var item in centerItems.Where(item => item.Position < anchor.Position).Reverse())
            {
                if (!item.Visible)
                {
                    hiddenSincePrevious = true;
                    continue;
                }

                var center = item.Center + shift;
                if (hiddenSincePrevious)
                {
                    var desired = previousCenter - (previousWidth / 2d) -
                                  spacing - (item.Width / 2d);
                    shift += desired - center;
                    center = desired;
                }
                center = ClampBottomBarLayoutCenter(item.Id, center, width);
                centers[item.Id] = center;
                previousCenter = center;
                previousWidth = item.Width;
                hiddenSincePrevious = false;
            }

            shift = 0d;
            previousCenter = centers[anchor.Id];
            previousWidth = anchor.Width;
            hiddenSincePrevious = false;
            foreach (var item in centerItems.Where(item => item.Position > anchor.Position))
            {
                if (!item.Visible)
                {
                    hiddenSincePrevious = true;
                    continue;
                }

                var center = item.Center + shift;
                if (hiddenSincePrevious)
                {
                    var desired = previousCenter + (previousWidth / 2d) +
                                  spacing + (item.Width / 2d);
                    shift += desired - center;
                    center = desired;
                }
                center = ClampBottomBarLayoutCenter(item.Id, center, width);
                centers[item.Id] = center;
                previousCenter = center;
                previousWidth = item.Width;
                hiddenSincePrevious = false;
            }
        }

        return items.Where(item => item.Visible)
            .Select(item => (item.Id, centers[item.Id]))
            .ToList();
    }

    private void RemoveEphemeralVolumeGap(
        List<(string Id, double Center)> positions, BottomBarLayoutPresetData preset, double width)
    {
        // Le retrait de l'espace appartient uniquement à la barre en lecture.
        // Dans l'éditeur, l'item Volume doit garder sa place pour être manipulé.
        // Il fait aussi partie du mode « Compacter les boutons absents » : si
        // ce réglage est désactivé, chaque centre enregistré doit rester la
        // source de vérité, y compris les espaces volontairement dessinés.
        if (!_autoCompactMissingBottomBarItems ||
            _bottomBarLayoutPreviewActive ||
            _volumeControlStyle is not (1 or 2))
            return;

        var muteIndex = positions.FindIndex(item =>
            string.Equals(item.Id, "mute", StringComparison.OrdinalIgnoreCase));
        var fullscreenIndex = positions.FindIndex(item =>
            string.Equals(item.Id, "fullscreen", StringComparison.OrdinalIgnoreCase));
        if (muteIndex < 0 || fullscreenIndex < 0 ||
            positions[muteIndex].Center >= positions[fullscreenIndex].Center)
            return;

        var spacing = Math.Clamp(preset.Spacing, 0, 24);
        var muteWidth = Math.Min(width, GetBottomBarLayoutElementWidth("mute"));
        var fullscreenWidth = Math.Min(width, GetBottomBarLayoutElementWidth("fullscreen"));
        var fullscreenCenter = positions[fullscreenIndex].Center;
        var muteCenter = fullscreenCenter - (fullscreenWidth / 2d) - spacing - (muteWidth / 2d);
        positions[muteIndex] = (positions[muteIndex].Id,
            ClampBottomBarLayoutCenter("mute", muteCenter, width));
    }

    private void PositionBottomBarFreeLayout()
    {
        if (_appliedBottomBarLayout is null ||
            BottomBarFreeLayoutCanvas.Visibility != Visibility.Visible ||
            BottomBarFreeLayoutCanvas.ActualWidth <= 0)
            return;

        BottomBarFreeLayoutCanvas.UpdateLayout();
        UpdateResponsiveBottomBarTitleWidth(
            _appliedBottomBarLayout, BottomBarFreeLayoutCanvas.ActualWidth);
        // La largeur du titre peut avoir changé; refaire une mesure avant de
        // calculer les collisions garantit que les centres utilisent sa largeur
        // effective et ne tassent plus inutilement les éléments de gauche.
        BottomBarFreeLayoutCanvas.UpdateLayout();
        var height = Math.Max(1, BottomBarFreeLayoutCanvas.ActualHeight);
        // En mode édition, les coordonnées enregistrées sont la source de
        // vérité. Le calcul par zones (gauche/centre/droite), utile en lecture
        // normale, faisait revenir chaque commande vers un tiers de la barre
        // après un déplacement et donnait l'impression que le mode compaction
        // restait actif.
        var hasContinuousPositions = _appliedBottomBarLayout.HorizontalPositions.Count > 0;
        var compactHiddenItems = _autoCompactMissingBottomBarItems &&
                                 !IsBottomBarLayoutEditing &&
                                 HasHiddenBottomBarLayoutItems(_appliedBottomBarLayout);
        var useContinuousPositions = hasContinuousPositions && !compactHiddenItems;
        var positions = useContinuousPositions ||
                        (_bottomBarLayoutFreeDragMode && _bottomBarLayoutDragActive &&
                         _bottomBarLayoutDragItem is not null)
            ? EnumerateBottomBarLayoutIds(_appliedBottomBarLayout)
                .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) &&
                             element.Visibility == Visibility.Visible)
                .Select(id =>
                {
                    var normalized = _appliedBottomBarLayout.HorizontalPositions.TryGetValue(id, out var value)
                        ? value
                        : 0.5;
                    return (Id: id, Center: ClampBottomBarLayoutCenter(
                        id, normalized * BottomBarFreeLayoutCanvas.ActualWidth,
                        BottomBarFreeLayoutCanvas.ActualWidth));
                }).ToList()
            : compactHiddenItems
                ? CalculateCompactedBottomBarCenters(
                    _appliedBottomBarLayout, BottomBarFreeLayoutCanvas.ActualWidth)
                : CalculatePackedBottomBarCenters(
                    _appliedBottomBarLayout, BottomBarFreeLayoutCanvas.ActualWidth);
        RemoveEphemeralVolumeGap(
            positions, _appliedBottomBarLayout, BottomBarFreeLayoutCanvas.ActualWidth);
        if (IsBottomBarLayoutEditing && _bottomBarLayoutDragActive)
            KeepCenterLockedBottomBarItemFixed(
                positions, _appliedBottomBarLayout, BottomBarFreeLayoutCanvas.ActualWidth);
        else
            ApplyCenterLockedBottomBarItem(
                positions, _appliedBottomBarLayout, BottomBarFreeLayoutCanvas.ActualWidth);
        ResolveRuntimeBottomBarOverlaps(
            positions, _appliedBottomBarLayout, BottomBarFreeLayoutCanvas.ActualWidth);
        foreach (var (id, center) in positions)
        {
            if (!_bottomBarLayoutElements.TryGetValue(id, out var element) ||
                !BottomBarFreeLayoutCanvas.Children.Contains(element))
                continue;
            var maximumLeft = Math.Max(0, BottomBarFreeLayoutCanvas.ActualWidth - element.ActualWidth);
            Canvas.SetLeft(element, Math.Clamp(center - (element.ActualWidth / 2d), 0, maximumLeft));
            Canvas.SetTop(element, Math.Max(0, (height - element.ActualHeight) / 2d));
        }

        BottomBarFreeLayoutCanvas.UpdateLayout();
        UpdateBottomBarLayoutItemBounds();
        QueueBottomBarGuidePositionUpdate();
    }

    private void BottomBarFreeLayoutCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionBottomBarFreeLayout();

    private void UpdateResponsiveBottomBarTitleWidth(
        BottomBarLayoutPresetData preset, double width)
    {
        // Le titre est le seul item volontairement extensible de la barre. Sa
        // largeur visuelle doit suivre la taille de la fenêtre, et non rester
        // figée pendant que les autres commandes se resserrent. Le texte et la
        // largeur enregistrée dans le modèle restent inchangés.
        width = Math.Max(1d, width);
        var configuredWidth = Math.Clamp(
            preset.TitleWidth * GetBottomBarHighDpiAdjustment(), 80, 800);
        // Avec LayoutTransform, le Canvas peut conserver une largeur logique
        // proche de 1280 même lorsque la fenêtre affichée est beaucoup plus
        // petite. Utiliser la largeur de ControlsPanel pour la proportion,
        // tout en gardant `width` pour les collisions dans le Canvas, évite
        // que le titre reste visuellement trop long au redimensionnement.
        var displayedWidth = ControlsPanel.ActualWidth > 0
            ? ControlsPanel.ActualWidth
            : width;
        if (displayedWidth > _bottomBarResponsiveWindowReferenceWidth)
            _bottomBarResponsiveWindowReferenceWidth = displayedWidth;

        var referenceWidth = Math.Max(
            BottomBarResponsiveReferenceWidth,
            _bottomBarResponsiveWindowReferenceWidth);
        var minimumReferenceWidth = Math.Min(
            BottomBarResponsiveMinimumWidth,
            referenceWidth * 0.5d);
        // Une interpolation légèrement progressive évite que le titre reste
        // disproportionné dans la zone intermédiaire : les deux extrêmes
        // restent identiques (50 % au minimum, 100 % à la pleine largeur).
        var normalizedWindowWidth = Math.Clamp(
            (displayedWidth - minimumReferenceWidth) /
            (referenceWidth - minimumReferenceWidth),
            0d, 1d);
        var windowScale = 0.5d +
                          (0.5d * Math.Pow(normalizedWindowWidth,
                              BottomBarTitleResizeCurve));
        var proportionalWidth = configuredWidth * windowScale;
        _effectiveBottomBarTitleWidth = proportionalWidth;

        if (!_bottomBarLayoutElements.ContainsKey("title") ||
            !EnumerateBottomBarLayoutIds(preset).Any(id =>
                string.Equals(id, "title", StringComparison.OrdinalIgnoreCase)))
            return;

        var visibleIds = EnumerateBottomBarLayoutIds(preset)
            .Where(id => _bottomBarLayoutElements.TryGetValue(id, out var element) &&
                         element.Visibility == Visibility.Visible)
            .ToArray();
        if (!visibleIds.Contains("title", StringComparer.OrdinalIgnoreCase))
            return;

        var configuredSpacing = Math.Clamp(preset.Spacing, 0, 24);
        var otherWidth = visibleIds
            .Where(id => !string.Equals(id, "title", StringComparison.OrdinalIgnoreCase))
            .Sum(id => Math.Min(width, GetBottomBarLayoutElementWidth(id)));
        var gapCount = Math.Max(0, visibleIds.Length - 1);
        var availableForTitle = Math.Max(
            0d, width - otherWidth - (configuredSpacing * gapCount));
        // Lorsque l'espace devient limité, le titre se réduit encore jusqu'à
        // la plus petite largeur utile (ou jusqu'à zéro si les autres commandes
        // occupent déjà toute la barre), ce qui évite qu'il recouvre un bouton.
        var minimumWidth = Math.Min(
            Math.Min(BottomBarTitleMinimumWidth, proportionalWidth),
            availableForTitle);
        var effectiveWidth = Math.Clamp(
            Math.Min(proportionalWidth, availableForTitle),
            minimumWidth,
            Math.Max(minimumWidth, proportionalWidth));

        _effectiveBottomBarTitleWidth = effectiveWidth;
        NowPlayingTitleHost.Width = effectiveWidth;
    }

    private void UpdateResponsiveBottomBarTitleWidthForCurrentLayout()
    {
        if (_appliedBottomBarLayout is null)
            return;

        var width = BottomBarFreeLayoutCanvas.Visibility == Visibility.Visible &&
                    BottomBarFreeLayoutCanvas.ActualWidth > 0
            ? BottomBarFreeLayoutCanvas.ActualWidth
            : BottomBarCustomLayoutGrid.ActualWidth > 0
                ? BottomBarCustomLayoutGrid.ActualWidth
                : ControlsPanel.ActualWidth;
        if (width > 0)
            UpdateResponsiveBottomBarTitleWidth(_appliedBottomBarLayout, width);
    }

    private void ApplyBottomBarLayoutGuideSettings(BottomBarLayoutPresetData preset)
    {
        var offset = Math.Clamp(preset.CenterBarOffset, -34, 18);
        BottomBarLayoutCenterLine.Height = 2;
        BottomBarLayoutCenterLine.Margin = new Thickness(0, 34 + offset, 0, 0);
        BottomBarLayoutCenterLine.Visibility = preset.HideHorizontalCenterGuide
            ? Visibility.Collapsed
            : Visibility.Visible;
        BottomBarLayoutVerticalGuides.Visibility = preset.HideVerticalGuides
            ? Visibility.Collapsed
            : Visibility.Visible;
        DefaultBottomBarLayoutTranslateTransform.Y = offset;
        BottomBarCustomLayoutTranslateTransform.Y = offset;
        QueueBottomBarGuidePositionUpdate();
    }

    private void QueueBottomBarGuidePositionUpdate()
    {
        if (!_bottomBarLayoutPreviewActive || _bottomBarGuidePositionUpdateQueued)
            return;

        _bottomBarGuidePositionUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _bottomBarGuidePositionUpdateQueued = false;
            UpdateBottomBarGuidePosition();
        }, DispatcherPriority.Render);
    }

    private void UpdateBottomBarGuidePosition()
    {
        if (!_bottomBarLayoutPreviewActive ||
            BottomBarLayoutGuideOverlay.Visibility != Visibility.Visible ||
            BottomBarCustomLayoutGrid.Visibility != Visibility.Visible ||
            BottomBarCustomLayoutGrid.ActualHeight <= 0)
            return;

        try
        {
            // Le repère doit traverser le centre rendu de la rangée, pas une
            // coordonnée fixe calculée pour la barre de 52 px. TranslatePoint
            // tient compte du redimensionnement et du décalage choisi dans
            // l'éditeur, ce qui empêche la ligne de dériver vers le bas.
            var top = BottomBarCustomLayoutGrid.TranslatePoint(
                new Point(0, 0), BottomBarLayoutGuideOverlay).Y;
            var bottom = BottomBarCustomLayoutGrid.TranslatePoint(
                new Point(0, BottomBarCustomLayoutGrid.ActualHeight),
                BottomBarLayoutGuideOverlay).Y;
            var center = (top + bottom) / 2d;
            var lineHeight = BottomBarLayoutCenterLine.ActualHeight > 0
                ? BottomBarLayoutCenterLine.ActualHeight
                : 2d;
            BottomBarLayoutCenterLine.Margin = new Thickness(
                0, Math.Max(0, center - (lineHeight / 2d)), 0, 0);
            UpdateBottomBarLayoutItemBounds();
        }
        catch (InvalidOperationException)
        {
            // La hiérarchie visuelle peut être entre deux passes de composition
            // pendant un changement de mode. La prochaine passe de rendu remet
            // automatiquement le repère à jour.
        }
    }

    private void UpdateBottomBarLayoutItemBounds()
    {
        if (!_bottomBarLayoutPreviewActive)
            return;

        var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in EnumerateBottomBarLayoutIds(_appliedBottomBarLayout ??
                     _bottomBarLayoutEditorDraft ?? CreateCompactBottomBarLayout()))
        {
            if (!_bottomBarLayoutElements.TryGetValue(id, out var element) ||
                !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                continue;

            try
            {
                var topLeft = element.TranslatePoint(new Point(0, 0), BottomBarLayoutItemBoundsCanvas);
                var bottomRight = element.TranslatePoint(
                    new Point(element.ActualWidth, element.ActualHeight),
                    BottomBarLayoutItemBoundsCanvas);
                var left = Math.Min(topLeft.X, bottomRight.X);
                var top = Math.Min(topLeft.Y, bottomRight.Y);
                var boundsWidth = Math.Abs(bottomRight.X - topLeft.X);
                var boundsHeight = Math.Abs(bottomRight.Y - topLeft.Y);
                if (boundsWidth <= 0 || boundsHeight <= 0)
                    continue;

                visibleIds.Add(id);
                if (!_bottomBarLayoutItemBounds.TryGetValue(id, out var frame))
                {
                    frame = new Border
                    {
                        BorderBrush = BottomBarLayoutNormalBorderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Background = BottomBarLayoutNormalFillBrush,
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true
                    };
                    _bottomBarLayoutItemBounds[id] = frame;
                    BottomBarLayoutItemBoundsCanvas.Children.Add(frame);
                }

                var isSelected = _bottomBarLayoutSelectedItemIds.Contains(id);
                frame.BorderBrush = isSelected
                    ? BottomBarLayoutSelectedBorderBrush
                    : BottomBarLayoutNormalBorderBrush;
                frame.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
                frame.Background = isSelected
                    ? BottomBarLayoutSelectedFillBrush
                    : BottomBarLayoutNormalFillBrush;

                frame.Width = boundsWidth;
                frame.Height = boundsHeight;
                Canvas.SetLeft(frame, left);
                Canvas.SetTop(frame, top);
            }
            catch (InvalidOperationException)
            {
                // Une nouvelle passe de rendu recalculera le cadre si le bouton
                // change temporairement de parent pendant une réorganisation.
            }
        }

        foreach (var staleId in _bottomBarLayoutItemBounds.Keys
                     .Where(id => !visibleIds.Contains(id)).ToArray())
        {
            BottomBarLayoutItemBoundsCanvas.Children.Remove(_bottomBarLayoutItemBounds[staleId]);
            _bottomBarLayoutItemBounds.Remove(staleId);
        }
    }

    private void ApplyInterfaceSettings(InterfaceSettingsSnapshot settings)
    {
        _topBarAutoHideDelayMilliseconds = Math.Clamp(
            settings.TopBarAutoHideDelayMilliseconds, 100, 10000);
        _bottomBarAutoHideDelayMilliseconds = Math.Clamp(
            settings.BottomBarAutoHideDelayMilliseconds, 100, 10000);
        _playlistScrollSpeed = Math.Clamp(
            settings.PlaylistScrollSpeed <= 0 ? 20 : settings.PlaylistScrollSpeed, 1, 100);
        _bottomBarLayoutPresets = NormalizeBottomBarLayoutPresets(settings.BottomBarLayoutPresets);
        var requestedBottomBarLayoutName = string.IsNullOrWhiteSpace(settings.ActiveBottomBarLayoutPreset)
            ? null
            : IsPrimaryClassicBottomBarLayoutName(settings.ActiveBottomBarLayoutPreset)
                ? PrimaryClassicBottomBarLayoutName
                : string.Equals(settings.ActiveBottomBarLayoutPreset.Trim(), "Fuse classique",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Fuze — classique"
                    : settings.ActiveBottomBarLayoutPreset.Trim();
        _activeBottomBarLayoutPreset = _bottomBarLayoutPresets.FirstOrDefault(preset =>
                string.Equals(preset.Name, requestedBottomBarLayoutName,
                    StringComparison.OrdinalIgnoreCase))?.Name
            ?? _bottomBarLayoutPresets.FirstOrDefault(preset =>
                string.Equals(preset.Name, PrimaryClassicBottomBarLayoutName,
                    StringComparison.OrdinalIgnoreCase))?.Name
            ?? _bottomBarLayoutPresets.FirstOrDefault()?.Name
            ?? "Fuze — compacte";
        _autoCompactMissingBottomBarItems = settings.AutoCompactMissingBottomBarItems;
        _volumeControlStyle = Math.Clamp(settings.VolumeControlStyle, 0, 3);
        _volumePopupHideDelayMilliseconds = Math.Clamp(
            settings.VolumePopupHideDelayMilliseconds, 100, 10000);
        _volumeIndicatorHideDelayMilliseconds = Math.Clamp(
            settings.VolumeIndicatorHideDelayMilliseconds, 100, 10000);
        _hideInterfaceOnVideoStart = settings.HideInterfaceOnVideoStart;
        _showSynchronizationButton = settings.ShowSynchronizationButton;
        _showShuffleButton = settings.ShowShuffleButton;
        _showRepeatButton = settings.ShowRepeatButton;
        _showSpeedButton = settings.ShowSpeedButton;
        _showPlaylistButton = settings.ShowPlaylistButton;
        _showVideoPanButton = settings.ShowVideoPanButton;
        _showAdditionalMediaInformation = settings.ShowAdditionalMediaInformation;
        _showScreenshotButton = settings.ShowScreenshotButton;
        _adaptiveInterfaceScale = settings.AdaptiveInterfaceScale;
        _autoHideCursor = settings.AutoHideCursor;
        _cursorAutoHideDelayMilliseconds = Math.Clamp(
            settings.CursorAutoHideDelayMilliseconds <= 0 ? 3000 : settings.CursorAutoHideDelayMilliseconds,
            100, 10000);
        _alwaysOnTop = settings.AlwaysOnTop;
        _showOsd = settings.ShowOsd;
        _disableToolTips = settings.DisableToolTips;
        _showChapterNameInSeekPreview = settings.ShowChapterNameInSeekPreview;
        _interfaceLanguage = NormalizeInterfaceLanguage(settings.InterfaceLanguage);
        LocalizationService.SetLanguage(_interfaceLanguage);
        _togglePlaybackOnSingleClick = settings.TogglePlaybackOnSingleClick;
        _toggleFullscreenOnDoubleClick = settings.ToggleFullscreenOnDoubleClick;
        _discordActivityEnabled = settings.DiscordActivityEnabled;
        _diagnosticLoggingEnabled = settings.DiagnosticLoggingEnabled;
        ApplyToolTipVisibility(!_disableToolTips);
        UpdatePlaybackModeButtons();
        _toolBarHideTimer.Interval = TimeSpan.FromMilliseconds(_topBarAutoHideDelayMilliseconds);
        _controlsHideTimer.Interval = TimeSpan.FromMilliseconds(_bottomBarAutoHideDelayMilliseconds);
        _gearControlsHideTimer.Interval = TimeSpan.FromMilliseconds(_bottomBarAutoHideDelayMilliseconds);
        _cursorHideTimer.Interval = TimeSpan.FromMilliseconds(_cursorAutoHideDelayMilliseconds);
        _volumePopupHideTimer.Interval = TimeSpan.FromMilliseconds(_volumePopupHideDelayMilliseconds);
        _volumeIndicatorHideTimer.Interval = TimeSpan.FromMilliseconds(_volumeIndicatorHideDelayMilliseconds);
        ApplyVolumeControlStyle();
        ApplyBottomBarLayout();
        OptionsBarPinButton.Visibility = _hideInterfaceOnVideoStart
            ? Visibility.Visible
            : Visibility.Collapsed;
        TrackSynchronizationButton.Visibility = _showSynchronizationButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShuffleButton.Visibility = _showShuffleButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepeatButton.Visibility = _showRepeatButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        SpeedButton.Visibility = _showSpeedButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlaylistButton.Visibility = _showPlaylistButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScreenshotButton.Visibility = _showScreenshotButton
            ? Visibility.Visible
            : Visibility.Collapsed;
        MediaInformationMenuItem.Visibility = _showAdditionalMediaInformation
            ? Visibility.Collapsed
            : Visibility.Visible;
        AdditionalMediaInformationMenuItem.Visibility = _showAdditionalMediaInformation
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Les visibilités viennent de changer après l'application du modèle.
        // Recalculer maintenant referme réellement les trous dans la barre hors
        // de l'éditeur, au lieu de conserver les anciennes coordonnées.
        PositionBottomBarFreeLayout();
        if (!_autoHideCursor)
        {
            _cursorHideTimer.Stop();
            SetPlaybackCursorVisibility(true);
            _cursorIsHidden = false;
        }
        else
        {
            RestartCursorHideTimerWithRemaining();
        }
        QueueResponsiveInterfaceScaleUpdate();

        if (!_hideInterfaceOnVideoStart)
        {
            _suppressToolBarActivation = false;
        }

        // Les textes statiques sont traduits une seule fois après l'application
        // des réglages. Les contrôles dynamiques (titre, progression, pistes)
        // restent pilotés par le lecteur et ne sont jamais remplacés.
        LocalizationService.ApplyToWindow(this);
        LocalizationService.ApplyToMenuHierarchy(MenuBar);
    }

    private void LocalizationService_OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_isClosing || !_initialized || Dispatcher.HasShutdownStarted)
            return;

        // Le choix de langue peut être fait pendant que la fenêtre Paramètres
        // est ouverte. Reporter le rafraîchissement au prochain tour évite de
        // modifier l’arbre visuel pendant un événement SelectionChanged.
        Dispatcher.BeginInvoke(RefreshLocalizedMainWindow, DispatcherPriority.DataBind);
    }

    private void RefreshLocalizedMainWindow()
    {
        if (_isClosing)
            return;

        LocalizationService.ApplyToWindow(this);
        // WPF does not consistently expose closed MenuItem popups through
        // the logical/visual tree. Walk the menu item collections explicitly
        // so the top bar and every submenu branch change language immediately.
        LocalizationService.ApplyToMenuHierarchy(MenuBar);
        // The playback surface (including the top bar) is moved into its own
        // transparent overlay window after startup. It is no longer a child
        // of MainWindow at that point, so localize that HWND explicitly too.
        if (_videoOverlayWindow is not null)
            LocalizationService.ApplyToWindow(_videoOverlayWindow);
        LocalizationService.ApplyToMenuHierarchy(MenuBar);
        foreach (var dialog in _auxiliaryDialogs.Where(dialog => dialog.IsVisible).ToArray())
            LocalizationService.ApplyToWindow(dialog);
        _shortcutsDialog?.RefreshLocalizedContent();
        _trackSyncDialog?.RefreshLocalizedContent();
        UpdateSkipButtons();
        UpdateNavigationToolTips();
        UpdateAudioMenuAvailability();
        UpdatePlaybackModeButtons();
        UpdatePlaylistSearchPlaceholder();
        RefreshPlaylistCount();
        RefreshRecentMediaMenu();

        // Les sous-menus sont recréés à leur ouverture. Les reconstruire ici
        // aussi garantit que les branches déjà créées (même fermées) ne
        // conservent pas des libellés de l’ancienne langue. La passe dédiée
        // des MenuItems ci-dessus couvre ensuite les éléments statiques.
        var menuEventArgs = new RoutedEventArgs();
        RefreshChaptersMenu();
        VideoTracksMenuItem_OnSubmenuOpened(VideoTracksMenuItem, menuEventArgs);
        AudioTracksMenuItem_OnSubmenuOpened(AudioTracksMenuItem, menuEventArgs);
        SubtitleTracksMenuItem_OnSubmenuOpened(SubtitleTracksMenuItem, menuEventArgs);
        RefreshAudioDevicesMenu();
        AudioModesMenuItem_OnSubmenuOpened(AudioModesMenuItem, menuEventArgs);
        AudioProcessingMenuItem_OnSubmenuOpened(AudioProcessingMenuItem, menuEventArgs);
        PlaybackSpeedMenuItem_OnSubmenuOpened(PlaybackSpeedMenuItem, menuEventArgs);
        VideoZoomMenuItem_OnSubmenuOpened(VideoZoomMenuItem, menuEventArgs);
        VideoAspectMenuItem_OnSubmenuOpened(VideoAspectMenuItem, menuEventArgs);
        LocalizationService.ApplyToMenuHierarchy(MenuBar);
    }

    private bool CanRevealTopBarWithoutGear() =>
        !_hideInterfaceOnVideoStart || _currentMedia is null || _mediaPlayer.VideoTrackCount <= 0;

    private bool IsPointerOverPlaybackControl()
    {
        // Toute la surface de la barre est une zone active, y compris ses
        // espaces transparents entre les commandes. Cela évite de masquer le
        // curseur lorsque la souris reste immobile dans une partie vide.
        return IsPointerInsideElement(ControlsPanel);
    }

    private bool IsOptionsBarPinFocused()
    {
        for (var element = Keyboard.FocusedElement as DependencyObject;
             element is not null;
             element = GetEventParent(element))
        {
            if (ReferenceEquals(element, OptionsBarPinButton))
                return true;

            if (ReferenceEquals(element, ControlsPanel))
                break;
        }

        return false;
    }

    private bool IsPointerInsideElement(FrameworkElement element) =>
        TryGetPointerPosition(element, out _);

    private static bool TryGetPointerPosition(FrameworkElement element, out Point point)
    {
        point = default;
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
            !GetCursorPos(out var cursor))
        {
            return false;
        }

        try
        {
            point = element.PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth &&
               point.Y <= element.ActualHeight;
    }

    private void HidePlaybackControls()
    {
        if (_bottomBarLayoutPreviewActive)
        {
            // La fenêtre de mise en page affiche la vraie barre en continu.
            _controlsHideTimer.Stop();
            RevealPlaybackControls();
            return;
        }
        _controlsHideTimer.Stop();
        SeekPreviewPopup.Visibility = Visibility.Collapsed;
        if (_volumeOverlayFollowsControls)
        {
            if (_volumeControlStyle == 1)
            {
                _volumeIndicatorHideTimer.Stop();
                VolumeIndicatorOverlay.Visibility = Visibility.Collapsed;
                _volumeOverlayFollowsControls = false;
            }
            else if (_volumeControlStyle == 2)
            {
                HideVolumePopup();
            }
        }
        if (ControlsPanel.Visibility != Visibility.Visible || !ControlsPanel.IsHitTestVisible)
        {
            RestartCursorHideTimerWithRemaining();
            return;
        }

        var animationVersion = ++_controlsAnimationVersion;
        ControlsPanel.IsHitTestVisible = false;

        var fade = new DoubleAnimation
        {
            From = ControlsPanel.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(95),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        var slide = new DoubleAnimation
        {
            From = ControlsTranslate.Y,
            To = 5,
            Duration = TimeSpan.FromMilliseconds(105),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (animationVersion != _controlsAnimationVersion || ControlsPanel.IsHitTestVisible)
                return;

            ControlsPanel.BeginAnimation(OpacityProperty, null);
            ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ControlsPanel.Opacity = 0;
            ControlsTranslate.Y = 5;
            ControlsPanel.Visibility = Visibility.Collapsed;
            AlignVideoOverlayWindow();
            RestartCursorHideTimerWithRemaining();
        };

        ControlsPanel.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void HidePlaybackControlsImmediately()
    {
        if (_bottomBarLayoutPreviewActive)
        {
            RevealPlaybackControls();
            return;
        }
        _controlsHideTimer.Stop();
        SeekPreviewPopup.Visibility = Visibility.Collapsed;
        _controlsAnimationVersion++;
        ControlsPanel.BeginAnimation(OpacityProperty, null);
        ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ControlsPanel.IsHitTestVisible = false;
        ControlsPanel.Opacity = 0;
        ControlsTranslate.Y = 5;
        ControlsPanel.Visibility = Visibility.Collapsed;
    }

    private void ToolBarToggleButton_OnClick(object sender, RoutedEventArgs e) =>
        SetToolBarPinned(!_toolBarPinnedOpen);

    private void OptionsBarPinButton_OnClick(object sender, RoutedEventArgs e) =>
        SetToolBarPinned(!_toolBarPinnedOpen);

    private void SetToolBarPinned(bool pinned)
    {
        _toolBarPinnedOpen = pinned;
        _suppressBottomRevealAfterToolBarPin = pinned;
        _toolBarTemporarilyExpanded = false;
        OptionsBarPinButton.Foreground = pinned ? Brushes.White :
            new SolidColorBrush(Color.FromRgb(0xD8, 0xDB, 0xDF));
        OptionsBarPinButton.ToolTip = LocalizationService.Get(pinned
            ? "Masquer la barre des options"
            : "Afficher et garder la barre des options ouverte");

        if (!pinned)
        {
            // Le bouton est un mode de maintien, pas un bouton de suppression.
            // En le désactivant, on laisse la barre visible le temps normal
            // puis elle se replie; elle ne disparaît plus instantanément ni ne
            // perd sa zone d'approche.
            _suppressToolBarActivation = false;
            ExpandToolBar(true);
            RestartToolBarHideTimer();
        }
        else
        {
            _suppressToolBarActivation = false;
            ExpandToolBar(false);
            // En mode maintien, le minuteur du haut est arrêté. Il sera
            // relancé uniquement lorsque l'utilisateur recliquera la flèche.
            _toolBarHideTimer.Stop();
            // Le bas est piloté par son minuteur normal. Un second minuteur
            // lancé par l'écrou provoquait deux replis concurrents et un
            // clignotement perceptible du bouton lorsque le pointeur restait
            // dessus.
            _gearControlsHideTimer.Stop();
        }

        if (!pinned)
            _gearControlsHideTimer.Stop();

        // Le haut et le bas ont des minuteries indépendantes. Le clic sur
        // l'écrou ne doit donc pas maintenir la barre inférieure ouverte.
        RestartControlsHideTimer();
    }

    private void ToolBarHost_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_toolBarPinnedOpen)
        {
            _toolBarHideTimer.Stop();
            return;
        }

        if (CanRevealTopBarWithoutGear())
        {
            ExpandToolBar(true);
            RestartToolBarHideTimer();
        }
    }

    private void ToolBarHost_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_toolBarPinnedOpen)
        {
            _toolBarHideTimer.Stop();
            return;
        }

        if (MenuBar.Visibility != Visibility.Visible && CanRevealTopBarWithoutGear())
        {
            ExpandToolBar(true);
            RestartToolBarHideTimer();
        }
    }

    private void ToolBarHost_OnMouseLeave(object sender, MouseEventArgs e)
    {
        RestartToolBarHideTimer();
    }

    private void ToolBarActivationZone_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_suppressToolBarActivation && !_toolBarPinnedOpen && CanRevealTopBarWithoutGear())
            ExpandToolBar(true);
    }

    private void ToolBarActivationZone_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_suppressToolBarActivation && !_toolBarPinnedOpen && CanRevealTopBarWithoutGear())
            ExpandToolBar(true);
    }

    private void ToolBarActivationZone_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (CanRevealTopBarWithoutGear())
            _suppressToolBarActivation = false;
    }

    private void ExpandToolBar(bool temporary)
    {
        _toolBarTemporarilyExpanded = temporary;
        ToolBarToggleButton.Width = 32 * _interfaceScale;
        ToolBarToggleButton.Height = 33 * _interfaceScale;
        ToolBarToggleButton.FontSize = 9 * _interfaceScale;
        // En mode survol, la flèche vers le haut indique que la barre est
        // déployée. Une fois verrouillée par le clic, elle pointe vers le bas
        // pour signaler que le verrouillage peut être retiré.
        ToolBarToggleButton.Content = _toolBarPinnedOpen ? "▼" : "▲";
        ToolBarToggleButton.ToolTip = LocalizationService.Get(_toolBarPinnedOpen
            ? "Replier la barre des outils"
            : "Garder la barre des outils ouverte");

        if (ToolBarHost.Visibility == Visibility.Visible && ToolBarHost.IsHitTestVisible)
            return;

        var animationVersion = ++_toolBarAnimationVersion;
        var fromOpacity = ToolBarHost.Visibility == Visibility.Visible ? ToolBarHost.Opacity : 0;
        var fromOffset = ToolBarHost.Visibility == Visibility.Visible ? ToolBarTranslate.Y : -7;

        ToolBarHost.Visibility = Visibility.Visible;
        ToolBarHost.Height = 33 * _interfaceScale;
        ToolBarHost.IsHitTestVisible = true;
        ToolBarHost.Background = new SolidColorBrush(Color.FromArgb(0xA6, 0x24, 0x27, 0x2B));
        MenuBar.Visibility = Visibility.Visible;
        MenuBar.IsHitTestVisible = true;

        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(115),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation
        {
            From = fromOffset,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(135),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (animationVersion != _toolBarAnimationVersion)
                return;

            ToolBarHost.BeginAnimation(OpacityProperty, null);
            ToolBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ToolBarHost.Opacity = 1;
            ToolBarTranslate.Y = 0;
        };

        ToolBarHost.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        ToolBarTranslate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void CollapseToolBar()
    {
        _toolBarTemporarilyExpanded = false;
        ToolBarToggleButton.Content = "▼";
        ToolBarToggleButton.ToolTip = LocalizationService.Get("Afficher la barre des outils");

        if (ToolBarHost.Visibility != Visibility.Visible)
        {
            RestartCursorHideTimerWithRemaining();
            return;
        }

        var animationVersion = ++_toolBarAnimationVersion;
        ToolBarHost.IsHitTestVisible = false;
        MenuBar.IsHitTestVisible = false;

        if (_isFullscreen)
        {
            CompleteToolBarCollapse(animationVersion);
            return;
        }

        var fade = new DoubleAnimation
        {
            From = ToolBarHost.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(55),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        var slide = new DoubleAnimation
        {
            From = ToolBarTranslate.Y,
            To = -5,
            Duration = TimeSpan.FromMilliseconds(65),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => CompleteToolBarCollapse(animationVersion);

        ToolBarHost.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        ToolBarTranslate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void CompleteToolBarCollapse(int animationVersion)
    {
        if (animationVersion != _toolBarAnimationVersion || ToolBarHost.IsHitTestVisible)
            return;

        ToolBarHost.BeginAnimation(OpacityProperty, null);
        ToolBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ToolBarHost.Opacity = 0;
        ToolBarTranslate.Y = -5;
        ToolBarHost.Background = Brushes.Transparent;
        ToolBarHost.Height = 0;
        ToolBarHost.Visibility = Visibility.Collapsed;
        MenuBar.Visibility = Visibility.Collapsed;
        MenuBar.IsHitTestVisible = true;
        RestartCursorHideTimerWithRemaining();
    }

    private void PlaylistToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _playlistVisible = !_playlistVisible;
        ApplyPlaylistVisibility();
    }

    private void ApplyPlaylistVisibility()
    {
        var canShow = _playlistVisible;
        PlaylistPanel.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FullscreenButton_OnClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_fullscreenTransitionInProgress || _isClosing)
            return;

        // Masquer l'overlay avant toute modification de taille/position. Le
        // double-clic peut changer de moniteur ou de DPI; laisser la barre
        // maintenue visible pendant ce passage permet à DWM de la composer
        // brièvement dans l'écran voisin.
        BeginDisplayGeometryTransition();
        _fullscreenTransitionInProgress = true;
        _videoClickTimer.Stop();
        _lastVideoClickTick = -1;
        Mouse.Capture(null);
        _isSeeking = false;
        _isAdjustingVolume = false;
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            AppShell.Margin = new Thickness(0);
            _windowStateBeforeFullscreen = WindowState;
            _windowWasMaximizedBeforeFullscreen = WindowState == WindowState.Maximized ||
                                                  (_windowHandle != IntPtr.Zero && IsZoomed(_windowHandle));
            _resizeModeBeforeFullscreen = ResizeMode;
            _hasWindowPlacement = false;
            if (_windowHandle != IntPtr.Zero)
            {
                _windowPlacementBeforeFullscreen = new WindowPlacement
                {
                    Length = (uint)Marshal.SizeOf<WindowPlacement>()
                };
                _hasWindowPlacement = GetWindowPlacement(_windowHandle, ref _windowPlacementBeforeFullscreen);
            }

            TitleRow.Height = new GridLength(0);
            TitleBar.Visibility = Visibility.Collapsed;
            MenuRow.Height = new GridLength(0);
            // Appliquer immédiatement la nouvelle grille avant le déplacement
            // natif : cela évite que le premier passage sur un autre DPI ne
            // conserve les dimensions de la petite fenêtre.
            AppShell.UpdateLayout();
            // La barre est restaurée par FinishDisplayGeometryTransition,
            // une fois l'overlay aligné sur la nouvelle géométrie.
            if (!_topBarHiddenForDisplayChange)
            {
                if (_toolBarPinnedOpen)
                    ExpandToolBar(false);
                else
                    CollapseToolBar();
            }
            ApplyPlaylistVisibility();
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            if (_windowHandle != IntPtr.Zero && IsZoomed(_windowHandle))
                ShowWindow(_windowHandle, SwRestore);

            if (_windowHandle != IntPtr.Zero)
            {
                var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
                var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var bounds = monitorInfo.Monitor;
                    // Le plein écran automatique doit recouvrir aussi la barre
                    // des tâches. IntPtr.Zero conserve la fenêtre sous la
                    // bande topmost du shell Windows; HWND_TOPMOST donne le
                    // même résultat que le plein écran demandé manuellement.
                    SetWindowPos(_windowHandle, HwndTopmost, bounds.Left, bounds.Top,
                        bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                        SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
                }
                else
                {
                    WindowState = WindowState.Maximized;
                }
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            UpdateActiveTopmostProtection();
            ShowToast("Plein écran • Échap pour quitter");
        }
        else
        {
            TitleRow.Height = new GridLength(28);
            TitleBar.Visibility = Visibility.Visible;
            ResizeMode = _resizeModeBeforeFullscreen;
            WindowState = WindowState.Normal;
            AppShell.UpdateLayout();

            if (_windowHandle != IntPtr.Zero && _hasWindowPlacement)
            {
                _windowPlacementBeforeFullscreen.Length = (uint)Marshal.SizeOf<WindowPlacement>();
                SetWindowPlacement(_windowHandle, ref _windowPlacementBeforeFullscreen);
            }

            // La sortie de plein écran rend à Windows son ordre normal, sauf
            // si l'utilisateur a explicitement demandé « Toujours au-dessus ».
            if (!_alwaysOnTop && _windowHandle != IntPtr.Zero)
                SetWindowPos(_windowHandle, HwndNotTopmost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);

            WindowState = _windowWasMaximizedBeforeFullscreen
                ? WindowState.Maximized
                : _windowStateBeforeFullscreen;
            if (_windowWasMaximizedBeforeFullscreen && _windowHandle != IntPtr.Zero)
                ShowWindow(_windowHandle, SwShowMaximized);

            if (!_topBarHiddenForDisplayChange)
            {
                if (_toolBarPinnedOpen)
                    ExpandToolBar(false);
                else
                    CollapseToolBar();
            }
            ApplyPlaylistVisibility();
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing)
                return;

            try
            {
                if (!_isFullscreen && _windowWasMaximizedBeforeFullscreen)
                {
                    WindowState = WindowState.Maximized;
                    if (_windowHandle != IntPtr.Zero)
                        ShowWindow(_windowHandle, SwShowMaximized);
                }
                UpdateMaximizedWorkAreaInsets();
                UpdateResponsiveInterfaceScale();
                QueueResponsiveInterfaceScaleUpdate();
                RefreshVideoLayout();
                AttachVideoOutput();
                // Un passage plein écran préparé avant la première image ne
                // doit pas retirer la surface de chargement. Elle reste en
                // place jusqu'au signal de reprise de mpv; autrement le HWND
                // vidéo encore vide est exposé pendant plusieurs secondes.
                if (_videoSurfaceReady)
                    RevealOverlayAfterStartup();
                BringFullscreenWindowsAboveTaskbar();
                if (_pendingPlaybackAfterWindowTransition)
                {
                    _pendingPlaybackAfterWindowTransition = false;
                    _mediaPlayer.SetPause(false);
                }
            }
            finally
            {
                _fullscreenTransitionInProgress = false;
                TryStartPreparedPlayback();
                // Si la transition plein écran se termine avant le passage
                // Render qui révèle la surface, celui-ci peut avoir différé
                // la fin de l'écran de chargement. Rejouer la garde ici évite
                // de conserver le panneau temporaire après une ouverture
                // rapide, sans jamais masquer la fenêtre avant que mpv ait
                // fourni sa première image.
                if (_videoSurfaceReady)
                {
                    RevealStartupWindowIfReady();
                    if (!_startupWindowPresentationPending)
                        HideStartupLoadingOverlay();
                }
            }
        }, DispatcherPriority.Render);
    }

    private void RevealOverlayAfterStartup()
    {
        if (!_videoOverlayHiddenForStartup)
            return;

        // Le HWND de l'overlay est créé une seule fois au chargement et reste
        // visible. Seul son contenu est réactivé ici; aucune nouvelle fenêtre
        // transparente n'est injectée au-dessus de la surface D3D11 de mpv.
        _videoOverlayHiddenForStartup = false;
        _videoOverlayHiddenForDisplayChange = false;
        if (_videoOverlayWindow?.IsVisible != true || _videoOverlayHandle == IntPtr.Zero)
        {
            ShowVideoOverlayWindow();
            UpdateVideoOverlayPresentationState();
            RevealStartupWindowIfReady();
            return;
        }

        AlignVideoOverlayWindow();
        UpdateVideoOverlayPresentationState();
        RevealStartupWindowIfReady();
    }

    private void RevealStartupWindowIfReady()
    {
        if (!_startupWindowPresentationPending || _isClosing ||
            (_startVideoFullscreen && !_isFullscreen && _fullscreenTransitionInProgress))
            return;

        // Le cloaking DWM garde les HWND composés et rendables pendant que mpv
        // prépare sa première image, sans exposer leur fond noir à l'écran.
        // Les deux fenêtres sont dévoilées seulement une fois leur géométrie
        // finale prête, ce qui évite le flash noir d'une ouverture par fichier.
        HideStartupLoadingOverlay();
        SetWindowCloaked(_videoOverlayHandle, false);
        SetWindowCloaked(_windowHandle, false);
        _startupWindowsCloaked = false;
        _startupWindowPresentationPending = false;
    }

    private static bool SetWindowCloaked(IntPtr window, bool cloaked)
    {
        if (window == IntPtr.Zero)
            return false;

        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(window, DwmCloak, ref value, sizeof(int)) >= 0;
    }

    private void VideoView_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsFreeVideoArea(e.OriginalSource as DependencyObject))
        {
            _videoClickTimer.Stop();
            _lastVideoClickTick = -1;
            return;
        }

        if (_fullscreenTransitionInProgress)
        {
            e.Handled = true;
            return;
        }

        if (_videoPanModeEnabled)
        {
            if (_currentIndex >= 0)
                BeginVideoPanDrag(e);
            // En mode déplacement, un clic isolé ne doit jamais déclencher
            // Play/Pause et ne doit pas armer la détection du double-clic.
            e.Handled = true;
            return;
        }

        var now = Environment.TickCount64;
        var position = e.GetPosition(VideoOverlay);
        var doubleClickTime = GetVideoDoubleClickDelayMilliseconds();
        const double horizontalTolerance = 7d;
        const double verticalTolerance = 7d;
        var isDoubleClick = _lastVideoClickTick >= 0 &&
                            now - _lastVideoClickTick <= doubleClickTime &&
                            Math.Abs(position.X - _lastVideoClickPosition.X) <= horizontalTolerance &&
                            Math.Abs(position.Y - _lastVideoClickPosition.Y) <= verticalTolerance;

        if (!_toggleFullscreenOnDoubleClick)
        {
            // Sans action de double-clic, le clic simple ne doit pas attendre
            // la fenêtre de détection ni être interprété comme un plein écran.
            _videoClickTimer.Stop();
            _lastVideoClickTick = -1;
            if (_togglePlaybackOnSingleClick)
                TogglePlayback();
            e.Handled = true;
            return;
        }

        if (isDoubleClick)
        {
            _videoClickTimer.Stop();
            _lastVideoClickTick = -1;
            ToggleFullscreen();
        }
        else
        {
            // Deux clics proches dans le temps, mais éloignés dans l'image,
            // ne sont pas un double-clic. On valide donc le premier clic puis
            // on arme le second séparément.
            if (_togglePlaybackOnSingleClick &&
                _lastVideoClickTick >= 0 && _videoClickTimer.IsEnabled && _currentIndex >= 0)
                TogglePlayback();

            _lastVideoClickTick = now;
            _lastVideoClickPosition = position;
            _videoClickTimer.Stop();
            _videoClickTimer.Interval = TimeSpan.FromMilliseconds(doubleClickTime);
            _videoClickTimer.Start();
        }

        e.Handled = true;
    }

    private static int GetVideoDoubleClickDelayMilliseconds()
    {
        // Respecte une préférence Windows plus rapide sans laisser le délai
        // global de 500 ms rendre Play/Pause lourd. Une valeur système hors
        // norme est ramenée à une plage utilisable.
        var systemDelay = (int)GetDoubleClickTime();
        return Math.Clamp(Math.Min(systemDelay, 280), 160, 280);
    }

    private void VideoOverlay_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_videoPanDragging)
            return;

        EndVideoPanDrag();
        e.Handled = true;
    }

    private void VideoOverlay_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_videoPanDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(VideoOverlay);
        var zoomScale = Math.Clamp(Math.Pow(2d, _videoZoom), 0.25d, 10d);
        var scaledWidth = Math.Max(1d, VideoOverlay.ActualWidth) * zoomScale;
        var scaledHeight = Math.Max(1d, VideoOverlay.ActualHeight) * zoomScale;
        var panX = _videoPanDragStartX + (point.X - _videoPanDragStart.X) / scaledWidth;
        var panY = _videoPanDragStartY + (point.Y - _videoPanDragStart.Y) / scaledHeight;
        if (_mediaPlayer.SetVideoPan(panX, panY))
        {
            _videoPanX = Math.Clamp(panX, -2d, 2d);
            _videoPanY = Math.Clamp(panY, -2d, 2d);
            SaveCurrentMediaPlaybackPreferences();
        }

        e.Handled = true;
    }

    private void BeginVideoPanDrag(MouseButtonEventArgs e)
    {
        _videoClickTimer.Stop();
        _lastVideoClickTick = -1;
        _videoPanDragging = true;
        _videoPanDragStart = e.GetPosition(VideoOverlay);
        _videoPanDragStartX = _videoPanX;
        _videoPanDragStartY = _videoPanY;
        VideoOverlay.CaptureMouse();
    }

    private void EndVideoPanDrag()
    {
        if (ReferenceEquals(Mouse.Captured, VideoOverlay))
            VideoOverlay.ReleaseMouseCapture();
        _videoPanDragging = false;
    }

    private void ResetVideoPanPosition()
    {
        if (_currentIndex < 0 || !_mediaPlayer.SetVideoPan(0, 0))
            return;

        _videoPanX = 0;
        _videoPanY = 0;
        ShowToast("Image recentrée");
    }

    private void VideoPanButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetVideoPanMode(!_videoPanModeEnabled);
        // L’état actif reste affiché dans l’indicateur supérieur; éviter un
        // second toast au même emplacement lors de l’activation.
        if (!_videoPanModeEnabled)
            ShowToast("Déplacement de l’écran désactivé");
        // Le raccourci clavier appelle aussi cette méthode avec un RoutedEventArgs
        // synthétique, qui ne possède aucun RoutedEvent. Définir Handled dans ce
        // cas provoque une InvalidOperationException et ferme Fuse.
        if (e.RoutedEvent is not null)
            e.Handled = true;
    }

    private void SetVideoPanMode(bool enabled)
    {
        if (_videoPanModeEnabled == enabled)
            return;

        _videoPanModeEnabled = enabled;
        if (!enabled)
            EndVideoPanDrag();
        else
        {
            _videoClickTimer.Stop();
            _lastVideoClickTick = -1;
        }

        VideoOverlay.Cursor = enabled ? Cursors.SizeAll : Cursors.Arrow;
        VideoPanModeIndicator.Visibility = enabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        VideoPanButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(255, 154, 72))
                                             : new SolidColorBrush(Color.FromRgb(216, 219, 223));
        VideoPanButton.ToolTip = LocalizationService.Get(enabled
            ? "Déplacement de l’écran activé : glissez l’image • molette : zoom • clic droit : recentrer"
            : "Déplacement de l’écran (G)");
    }

    private void AdjustVideoPanModeZoom(int direction)
    {
        if (_currentIndex < 0 || direction == 0)
            return;

        var currentPercent = Math.Pow(2d, _videoZoom) * 100d;
        var targetIndex = -1;
        if (direction > 0)
        {
            for (var index = 0; index < VideoPanZoomSteps.Length; index++)
            {
                if (VideoPanZoomSteps[index] > currentPercent + 0.01d)
                {
                    targetIndex = index;
                    break;
                }
            }
        }
        else
        {
            for (var index = VideoPanZoomSteps.Length - 1; index >= 0; index--)
            {
                if (VideoPanZoomSteps[index] < currentPercent - 0.01d)
                {
                    targetIndex = index;
                    break;
                }
            }
        }

        targetIndex = targetIndex < 0
            ? direction > 0 ? VideoPanZoomSteps.Length - 1 : 0
            : targetIndex;
        var targetPercent = VideoPanZoomSteps[targetIndex];
        var targetZoom = ZoomPercentToMpv(targetPercent);
        if (!_mediaPlayer.SetVideoZoom(targetZoom))
            return;

        _videoZoom = targetZoom;
        SaveCurrentMediaPlaybackPreferences();
        PersistSession();
        ShowToast(LocalizationService.Format("Zoom vidéo • {0} %", targetPercent));
    }

    private void VideoOverlay_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || _currentIndex < 0 ||
            !IsFreeVideoArea(e.OriginalSource as DependencyObject))
            return;

        if (_videoPanModeEnabled)
        {
            AdjustVideoPanModeZoom(e.Delta > 0 ? 1 : -1);
            RestartControlsHideTimer();
            e.Handled = true;
            return;
        }

        if (_centerWheelVolumeEnabled)
        {
            ChangeVolume(e.Delta > 0 ? 5 : -5);
            if (_volumeControlStyle == 1)
                ShowVolumeIndicator();
            else if (_volumeControlStyle == 2)
                ShowVolumePopup(fromWheel: true);
        }
        else if (_centerWheelTimelineEnabled)
        {
            SeekRelative(e.Delta > 0
                ? _forwardSeconds * 1000L
                : -_rewindSeconds * 1000L);
            RevealPlaybackControls();
        }
        else
        {
            return;
        }

        RestartControlsHideTimer();
        e.Handled = true;
    }

    private bool IsFreeVideoArea(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetEventParent(current))
        {
            if (ReferenceEquals(current, ToolBarHost) ||
                ReferenceEquals(current, ControlsPanel) ||
                ReferenceEquals(current, VolumePopup) ||
                ReferenceEquals(current, VolumeIndicatorOverlay) ||
                ReferenceEquals(current, EmptyState) ||
                ReferenceEquals(current, PlaylistPanel))
            {
                return false;
            }

            if (ReferenceEquals(current, VideoOverlay))
                return true;
        }

        return false;
    }

    private static DependencyObject? GetEventParent(DependencyObject element)
    {
        return element is Visual
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
    }

    private void VideoView_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsFreeVideoArea(e.OriginalSource as DependencyObject))
            return;

        if (_videoPanModeEnabled)
        {
            ResetVideoPanPosition();
            e.Handled = true;
            return;
        }

        var menu = CreateFuzeContextMenu();
        AddContextAction(menu, _mediaPlayer.IsPlaying ? "Pause" : "Lecture", TogglePlayback);
        AddContextAction(menu, "Précédent", PlayPrevious);
        AddContextAction(menu, "Suivant", PlayNext);
        menu.Items.Add(CreateFuzeContextMenuSeparator());
        AddContextAction(menu, "Capture d’écran", TakeSnapshot);
        AddContextAction(menu, _isFullscreen ? "Quitter le plein écran" : "Plein écran", ToggleFullscreen);
        OpenContextMenu(menu, VideoOverlay, PlacementMode.MousePoint);
        e.Handled = true;
    }

    private void AddContextAction(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = LocalizationService.Get(label) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        var media = ExpandDroppedPaths(paths).ToArray();
        if (media.Length == 0)
        {
            ShowToast("Aucun média reconnu");
            return;
        }

        OpenLocations(media);
    }

    private static IEnumerable<string> ExpandDroppedPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => MediaExtensions.Contains(Path.GetExtension(file)))
                    .OrderBy(file => file, NaturalPathComparer.Instance)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        const ModifierKeys supported = ModifierKeys.Control | ModifierKeys.Shift |
                                       ModifierKeys.Alt | ModifierKeys.Windows;
        var modifiers = Keyboard.Modifiers & supported;
        var windowsShiftPressed = modifiers == (ModifierKeys.Windows | ModifierKeys.Shift) ||
                                  (modifiers == ModifierKeys.Shift && IsWindowsShiftPressed());
        if (windowsShiftPressed &&
            key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (MoveWindowToAdjacentMonitor(key))
                e.Handled = true;
            return;
        }

        if (key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (key is Key.VolumeUp or Key.VolumeDown or Key.VolumeMute)
        {
            if (_ignoreKeyboardVolumeButtons)
                return;

            if (key == Key.VolumeUp)
                ChangeVolume(5);
            else if (key == Key.VolumeDown)
                ChangeVolume(-5);
            else
                MuteButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        var command = ShortcutCatalog.Definitions.FirstOrDefault(definition =>
            _keyboardShortcuts.TryGetValue(definition.Id, out var encoded) &&
            ShortcutCatalog.TryDecode(encoded, out var shortcutKey, out var shortcutModifiers) &&
            shortcutKey == key && shortcutModifiers == modifiers);
        if (command is null)
            return;

        ExecuteShortcut(command.Id);
        e.Handled = true;
    }

    private void ExecuteShortcut(string commandId)
    {
        switch (commandId)
        {
            case "open-file":
                OpenFilesButton_OnClick(this, new RoutedEventArgs());
                break;
            case "open-multiple":
                OpenMultipleFilesButton_OnClick(this, new RoutedEventArgs());
                break;
            case "open-settings":
                SettingsMenuItem_OnClick(this, new RoutedEventArgs());
                break;
            case "play-pause":
            case "play-pause-secondary":
                TogglePlayback();
                break;
            case "seek-back":
            case "seek-back-secondary":
                SeekRelative(-_rewindSeconds * 1000L);
                break;
            case "seek-forward":
            case "seek-forward-secondary":
                SeekRelative(_forwardSeconds * 1000L);
                break;
            case "volume-up":
                ChangeVolume(5);
                break;
            case "volume-down":
                ChangeVolume(-5);
                break;
            case "mute":
                MuteButton_OnClick(this, new RoutedEventArgs());
                break;
            case "fullscreen":
                ToggleFullscreen();
                break;
            case "playlist":
                PlaylistToggleButton_OnClick(this, new RoutedEventArgs());
                break;
            case "shuffle":
                ShuffleButton_OnClick(this, new RoutedEventArgs());
                break;
            case "repeat":
                RepeatButton_OnClick(this, new RoutedEventArgs());
                break;
            case "audio-track":
                CycleAudioTrack(1);
                break;
            case "subtitle-track":
                CycleSubtitleTrack(1);
                break;
            case "speed-menu":
                SpeedButton_OnClick(this, new RoutedEventArgs());
                break;
            case "track-sync":
                TrackSynchronizationMenuItem_OnClick(this, new RoutedEventArgs());
                break;
            case "video-pan":
                VideoPanButton_OnClick(this, new RoutedEventArgs());
                break;
            case "options-bar":
                OptionsBarPinButton_OnClick(this, new RoutedEventArgs());
                break;
            case "next":
                PlayNext();
                break;
            case "previous":
                PlayPrevious();
                break;
            case "snapshot":
                TakeSnapshot();
                break;
            case "speed-down":
                ChangeSpeed(-1);
                break;
            case "speed-up":
                ChangeSpeed(1);
                break;
        }
    }

    private void InputManager_OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (_isClosing || e.StagingItem.Input is not KeyEventArgs keyEvent ||
            keyEvent.RoutedEvent != Keyboard.PreviewKeyDownEvent || keyEvent.Handled)
            return;

        var focusedWindow = Keyboard.FocusedElement is DependencyObject focusedElement
            ? GetWindow(focusedElement)
            : null;
        if (focusedWindow != this && focusedWindow != _videoOverlayWindow)
            return;

        Window_OnPreviewKeyDown(this, keyEvent);
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        if (_windowHandle == IntPtr.Zero)
            return;

        ApplyNativeWindowAppearance();
        CenterStartupWindowOnPointerMonitor();
        if (_startupWindowPresentationPending)
            _startupWindowsCloaked = SetWindowCloaked(_windowHandle, true);
        _videoBackgroundBrush = CreateSolidBrush(0x00000000);
        InitializeVideoOverlayWindow();

        _windowSource = HwndSource.FromHwnd(_windowHandle);
        if (_windowSource is not null)
        {
            if (_windowSource.CompositionTarget is not null)
                _windowSource.CompositionTarget.BackgroundColor = Colors.Black;
            _windowSource.AddHook(WindowProcedure);
        }

        RegisterWindowMoveHotkeys();
        InstallWindowMoveKeyboardHook();

        InstallDiscordOverlayProtection();
    }

    private void RegisterWindowMoveHotkeys()
    {
        if (_windowHandle == IntPtr.Zero)
            return;

        var directions = new[] { VkLeft, VkUp, VkRight, VkDown };
        for (var index = 0; index < directions.Length; index++)
        {
            var id = WindowMoveHotkeyBaseId + index;
            if (RegisterHotKey(_windowHandle, id, ModWindows | ModShift,
                    (uint)directions[index]))
                _registeredWindowMoveHotkeys.Add(id);
        }
    }

    private void UnregisterWindowMoveHotkeys()
    {
        if (_windowHandle == IntPtr.Zero)
            return;

        foreach (var id in _registeredWindowMoveHotkeys)
            UnregisterHotKey(_windowHandle, id);
        _registeredWindowMoveHotkeys.Clear();
    }

    private void InstallWindowMoveKeyboardHook()
    {
        if (_windowMoveKeyboardHook != IntPtr.Zero)
            return;

        _windowMoveKeyboardHookCallback = WindowMoveKeyboardHook;
        // WH_KEYBOARD_LL doit recevoir un handle de module lorsque le hook est
        // installé pour tous les threads. Avec un handle nul, Windows peut
        // refuser silencieusement le hook dans une publication autonome .NET,
        // ce qui rend Win+Maj+flèche inopérant en plein écran.
        var module = GetModuleHandle(null);
        _windowMoveKeyboardHook = SetWindowsHookEx(
            WhKeyboardLowLevel, _windowMoveKeyboardHookCallback, module, 0);
    }

    private void UninstallWindowMoveKeyboardHook()
    {
        if (_windowMoveKeyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_windowMoveKeyboardHook);
            _windowMoveKeyboardHook = IntPtr.Zero;
        }

        _windowMoveKeyboardHookCallback = null;
    }

    private IntPtr WindowMoveKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam.ToInt32() is LlKeyDown or LlSysKeyDown) &&
            lParam != IntPtr.Zero)
        {
            var data = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
            var virtualKey = (int)data.VirtualKeyCode;
            if ((data.Flags & LlKbdInjected) == 0 &&
                IsWindowsShiftPressed() &&
                TryGetVirtualKeyDirection(virtualKey, out var direction) &&
                IsFuzeForegroundWindow())
            {
                if (Dispatcher.CheckAccess())
                {
                    // Le hook bas niveau est installé depuis le thread WPF.
                    // Déplacer immédiatement la fenêtre évite de laisser le
                    // shell traiter la combinaison avant le Dispatcher. Si le
                    // déplacement est impossible, on laisse passer la touche
                    // afin de ne pas la bloquer inutilement.
                    if (!_isClosing && MoveWindowToAdjacentMonitor(direction))
                        return new IntPtr(1);
                }
                else
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!_isClosing)
                            MoveWindowToAdjacentMonitor(direction);
                    }, DispatcherPriority.Input);
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_windowMoveKeyboardHook, code, wParam, lParam);
    }

    private bool IsFuzeForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground == _windowHandle || foreground == _videoOverlayHandle)
            return true;

        // En plein écran, le focus peut être placé sur la surface native mpv
        // (ou sur un autre HWND enfant de Fuze) plutôt que sur les deux HWND
        // WPF principaux. Ils appartiennent néanmoins au même processus.
        if (foreground == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void InitializeVideoOverlayWindow()
    {
        if (_videoOverlayWindow is not null || _isClosing)
            return;

        OverlayParking.Children.Remove(VideoOverlay);
        OverlayParking.Visibility = Visibility.Collapsed;

        _videoOverlayWindow = new Window
        {
            Title = string.Empty,
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left,
            Top = Top,
            Width = Math.Max(1, VideoViewport.ActualWidth),
            Height = Math.Max(1, VideoViewport.ActualHeight),
            Content = VideoOverlay
        };
        _videoOverlayWindow.SourceInitialized += VideoOverlayWindow_OnSourceInitialized;
        _videoOverlayWindow.DpiChanged += VideoOverlayWindow_OnDpiChanged;
        if (IsLoaded)
            Dispatcher.BeginInvoke(ShowVideoOverlayWindow, DispatcherPriority.Loaded);
    }

    private void ApplyNativeWindowAppearance()
    {
        if (_windowHandle == IntPtr.Zero)
            return;

        var cornerPreference = DwmDoNotRound;
        DwmSetWindowAttribute(_windowHandle, DwmWindowCornerPreference,
            ref cornerPreference, sizeof(int));
        var borderColor = DwmBorderColorBlack;
        DwmSetWindowAttribute(_windowHandle, DwmBorderColor,
            ref borderColor, sizeof(int));
    }

    private void CenterStartupWindowOnPointerMonitor()
    {
        if (_windowHandle == IntPtr.Zero || !GetCursorPos(out var cursorPosition))
            return;

        var monitor = MonitorFromPoint(cursorPosition, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo) ||
            !GetWindowRect(_windowHandle, out var currentBounds))
            return;

        var workArea = monitorInfo.WorkArea;
        var workWidth = Math.Max(1, workArea.Right - workArea.Left);
        var workHeight = Math.Max(1, workArea.Bottom - workArea.Top);
        var currentWidth = Math.Max(1, currentBounds.Right - currentBounds.Left);
        var currentHeight = Math.Max(1, currentBounds.Bottom - currentBounds.Top);
        var width = Math.Min(currentWidth, workWidth);
        var height = Math.Min(currentHeight, workHeight);
        var left = workArea.Left + (workWidth - width) / 2;
        var top = workArea.Top + (workHeight - height) / 2;

        SetWindowPos(_windowHandle, IntPtr.Zero, left, top, width, height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static IReadOnlyList<VideoMonitorInfo> GetVideoMonitors()
    {
        var monitors = new List<VideoMonitorInfo>();

        bool CollectMonitor(IntPtr monitor, IntPtr deviceContext,
            ref NativeRect monitorRectangle, IntPtr parameter)
        {
            var info = new MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                Device = string.Empty
            };
            if (!GetMonitorInfoEx(monitor, ref info))
                return true;

            var id = info.Device?.Trim();
            if (string.IsNullOrWhiteSpace(id))
                id = $"monitor:{monitor.ToInt64():X}";
            monitors.Add(new VideoMonitorInfo(id, info.Monitor, info.WorkArea,
                (info.Flags & MonitorInfoPrimary) != 0));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectMonitor, IntPtr.Zero);
        return monitors
            .OrderBy(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool MoveWindowToAdjacentMonitor(Key direction)
    {
        if (_windowHandle == IntPtr.Zero ||
            !GetWindowRect(_windowHandle, out var windowBounds))
            return false;

        var monitors = GetVideoMonitors();
        if (monitors.Count < 2)
            return false;

        var windowCenterX = (windowBounds.Left + windowBounds.Right) / 2d;
        var windowCenterY = (windowBounds.Top + windowBounds.Bottom) / 2d;
        var current = monitors.FirstOrDefault(monitor =>
            windowCenterX >= monitor.Monitor.Left && windowCenterX < monitor.Monitor.Right &&
            windowCenterY >= monitor.Monitor.Top && windowCenterY < monitor.Monitor.Bottom);
        if (current is null)
            return false;

        var currentCenterX = (current.Monitor.Left + current.Monitor.Right) / 2d;
        var currentCenterY = (current.Monitor.Top + current.Monitor.Bottom) / 2d;
        var candidates = monitors.Where(monitor => !ReferenceEquals(monitor, current));
        candidates = direction switch
        {
            Key.Left => candidates.Where(monitor =>
                (monitor.Monitor.Left + monitor.Monitor.Right) / 2d < currentCenterX),
            Key.Right => candidates.Where(monitor =>
                (monitor.Monitor.Left + monitor.Monitor.Right) / 2d > currentCenterX),
            Key.Up => candidates.Where(monitor =>
                (monitor.Monitor.Top + monitor.Monitor.Bottom) / 2d < currentCenterY),
            Key.Down => candidates.Where(monitor =>
                (monitor.Monitor.Top + monitor.Monitor.Bottom) / 2d > currentCenterY),
            _ => []
        };

        var target = candidates
            .OrderBy(monitor =>
            {
                var centerX = (monitor.Monitor.Left + monitor.Monitor.Right) / 2d;
                var centerY = (monitor.Monitor.Top + monitor.Monitor.Bottom) / 2d;
                var primary = direction is Key.Left or Key.Right
                    ? Math.Abs(centerX - currentCenterX)
                    : Math.Abs(centerY - currentCenterY);
                var secondary = direction is Key.Left or Key.Right
                    ? Math.Abs(centerY - currentCenterY)
                    : Math.Abs(centerX - currentCenterX);
                return primary + secondary * 0.25;
            })
            .FirstOrDefault();
        // S'il n'y a pas d'écran strictement dans la direction demandée
        // (configuration verticale, écran décalé ou extrémité de la rangée),
        // choisir le moniteur voisin le plus proche permet au raccourci de
        // rester utile au lieu de ne fonctionner qu'une seule fois.
        if (target is null)
        {
            target = monitors
                .Where(monitor => !ReferenceEquals(monitor, current))
                .OrderBy(monitor =>
                {
                    var centerX = (monitor.Monitor.Left + monitor.Monitor.Right) / 2d;
                    var centerY = (monitor.Monitor.Top + monitor.Monitor.Bottom) / 2d;
                    return Math.Abs(centerX - currentCenterX) +
                           Math.Abs(centerY - currentCenterY);
                })
                .FirstOrDefault();
        }
        if (target is null)
            return false;

        if (_isFullscreen)
        {
            var bounds = target.Monitor;
            SetWindowPos(_windowHandle, HwndTopmost, bounds.Left, bounds.Top,
                Math.Max(1, bounds.Right - bounds.Left),
                Math.Max(1, bounds.Bottom - bounds.Top),
                SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
            AppShell.UpdateLayout();
            _videoOverlayWindow?.UpdateLayout();
            AlignVideoOverlayWindow();
            BringFullscreenWindowsAboveTaskbar();
            ScheduleVideoLayoutRefresh();
            QueueAdaptiveAudioDeviceUpdate();
            return true;
        }

        var wasMaximized = WindowState == WindowState.Maximized;
        if (wasMaximized)
            WindowState = WindowState.Normal;

        if (!GetWindowRect(_windowHandle, out windowBounds))
            return false;

        var width = Math.Min(Math.Max(1, windowBounds.Right - windowBounds.Left),
            Math.Max(1, target.WorkArea.Right - target.WorkArea.Left));
        var height = Math.Min(Math.Max(1, windowBounds.Bottom - windowBounds.Top),
            Math.Max(1, target.WorkArea.Bottom - target.WorkArea.Top));
        var left = target.WorkArea.Left +
                   (target.WorkArea.Right - target.WorkArea.Left - width) / 2;
        var top = target.WorkArea.Top +
                  (target.WorkArea.Bottom - target.WorkArea.Top - height) / 2;
        SetWindowPos(_windowHandle, IntPtr.Zero, left, top, width, height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);

        if (wasMaximized)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isClosing && !_isFullscreen)
                    WindowState = WindowState.Maximized;
            }, DispatcherPriority.Render);
        }

        AlignVideoOverlayWindow();
        ScheduleVideoLayoutRefresh();
        QueueAdaptiveAudioDeviceUpdate();
        return true;
    }

    private static VideoDisplayDescription[] GetAvailableVideoDisplays()
    {
        var displays = new List<VideoDisplayDescription>
        {
            new("auto", LocalizationService.Get("Automatique (écran actuel)"))
        };
        var monitors = GetVideoMonitors();
        var friendlyNames = GetMonitorFriendlyNamesByGdiName();
        for (var index = 0; index < monitors.Count; index++)
        {
            var monitor = monitors[index];
            var width = Math.Max(1, monitor.Monitor.Right - monitor.Monitor.Left);
            var height = Math.Max(1, monitor.Monitor.Bottom - monitor.Monitor.Top);
            var primary = monitor.IsPrimary
                ? $" • {LocalizationService.Get("principal")}"
                : string.Empty;
            var friendlyName = friendlyNames.GetValueOrDefault(monitor.Id);
            var numberLabel = $"{LocalizationService.Get("Écran")} {index + 1}";
            var details = $"{width} × {height}{primary}";
            var nameLine = string.IsNullOrWhiteSpace(friendlyName)
                ? string.Empty
                : $" • {friendlyName}";
            displays.Add(new VideoDisplayDescription(monitor.Id,
                $"{numberLabel} — {details}{nameLine}",
                numberLabel,
                friendlyName ?? string.Empty,
                details));
        }

        return [.. displays];
    }

    private static Dictionary<string, string> GetMonitorFriendlyNamesByGdiName()
    {
        const uint getDeviceInterfaceName = 1;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = new DisplayDeviceInfo
            {
                Size = (uint)Marshal.SizeOf<DisplayDeviceInfo>()
            };
            if (!EnumDisplayAdapters(IntPtr.Zero, adapterIndex, ref adapter, 0))
                break;

            var adapterName = adapter.DeviceName?.Trim();
            if (string.IsNullOrWhiteSpace(adapterName))
                continue;

            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                var monitor = new DisplayDeviceInfo
                {
                    Size = (uint)Marshal.SizeOf<DisplayDeviceInfo>()
                };
                if (!EnumDisplayMonitorDevices(adapterName, monitorIndex, ref monitor,
                        getDeviceInterfaceName))
                    break;

                var friendlyName = TryReadMonitorNameFromEdid(monitor.DeviceId);
                if (string.IsNullOrWhiteSpace(friendlyName))
                    friendlyName = monitor.DeviceString?.Trim();
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    result[adapterName] = friendlyName;
                    break;
                }
            }
        }

        return result;
    }

    private static string? TryReadMonitorNameFromEdid(string? deviceInterfaceId)
    {
        if (string.IsNullOrWhiteSpace(deviceInterfaceId))
            return null;

        var parts = deviceInterfaceId.Split('#');
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[1]) ||
            string.IsNullOrWhiteSpace(parts[2]))
            return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 72)
                return null;

            for (var offset = 54; offset + 18 <= edid.Length && offset < 126; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 ||
                    edid[offset + 3] != 0xFC)
                    continue;

                var name = Encoding.ASCII.GetString(edid, offset + 5, 13)
                    .Trim('\0', '\n', '\r', ' ');
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException)
        {
            // Le nom convivial est un enrichissement visuel. L'identifiant et
            // la résolution restent disponibles si Windows refuse l'EDID.
        }

        return null;
    }

    private string? GetCurrentVideoDisplayId()
    {
        if (_windowHandle == IntPtr.Zero)
            return null;

        var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return null;

        var info = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            Device = string.Empty
        };
        if (!GetMonitorInfoEx(monitor, ref info))
            return null;

        var id = info.Device?.Trim();
        return string.IsNullOrWhiteSpace(id) ? $"monitor:{monitor.ToInt64():X}" : id;
    }

    private void QueueAdaptiveAudioDeviceUpdate()
    {
        if (!_adaptiveAudioModeEnabled || _adaptiveAudioUpdateQueued || _isClosing)
            return;

        _adaptiveAudioUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _adaptiveAudioUpdateQueued = false;
            if (!_isClosing)
                ApplyAdaptiveAudioDeviceForCurrentDisplay();
        }, DispatcherPriority.Background);
    }

    private bool ApplyAdaptiveAudioDeviceForCurrentDisplay(bool force = false, bool showToast = false)
    {
        if (!_adaptiveAudioModeEnabled)
        {
            _lastAdaptiveAudioDisplayId = null;
            return true;
        }

        var displayId = GetCurrentVideoDisplayId();
        if (string.IsNullOrWhiteSpace(displayId))
            return false;

        var mapping = _adaptiveAudioDisplayMappings.LastOrDefault(candidate =>
            string.Equals(candidate.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
        var deviceName = string.IsNullOrWhiteSpace(mapping?.AudioDevice)
            ? "auto"
            : mapping.AudioDevice.Trim();
        if (!force && string.Equals(_lastAdaptiveAudioDisplayId, displayId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_selectedAudioDevice, deviceName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!_mediaPlayer.SetAudioDevice(deviceName))
            return false;

        _selectedAudioDevice = deviceName;
        _lastAdaptiveAudioDisplayId = displayId;
        if (showToast)
        {
            var description = GetAvailableAudioDevices().FirstOrDefault(device =>
                string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase))?.Description
                              ?? deviceName;
            ShowToast(LocalizationService.Format("Audio adaptatif • {0}", description));
        }

        return true;
    }

    private void UpdateAdaptiveAudioMappingForCurrentDisplay(string deviceName)
    {
        if (!_adaptiveAudioModeEnabled)
            return;

        var displayId = GetCurrentVideoDisplayId();
        if (string.IsNullOrWhiteSpace(displayId))
            return;

        var displayName = GetAvailableVideoDisplays().FirstOrDefault(display =>
            string.Equals(display.Id, displayId, StringComparison.OrdinalIgnoreCase))?.Description
                          ?? displayId;
        var mapping = _adaptiveAudioDisplayMappings.LastOrDefault(candidate =>
            string.Equals(candidate.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            mapping = new AdaptiveAudioDisplayMappingData { DisplayId = displayId };
            _adaptiveAudioDisplayMappings.Add(mapping);
        }

        mapping.DisplayName = displayName;
        mapping.AudioDevice = string.IsNullOrWhiteSpace(deviceName) ? "auto" : deviceName;
        _lastAdaptiveAudioDisplayId = displayId;
    }

    private static string NormalizeVideoOutputSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "d3d11" => "d3d11",
        "d3d9" => "d3d9",
        "opengl" => "opengl",
        "software" => "software",
        _ => "auto"
    };

    private static string NormalizeCustomAspectRatio(string? value)
    {
        var parts = value?.Trim().Split(':', StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) &&
            width is >= 1 and <= 10000 && height is >= 1 and <= 10000)
            return $"{width}:{height}";

        return "16:9";
    }

    private static string NormalizeInterfaceLanguage(string? value) =>
        string.Equals(value?.Trim(), "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    private static string[] NormalizeFileAssociationExtensions(IEnumerable<string>? extensions)
    {
        var normalized = FileAssociationService.NormalizeExtensions(extensions);
        return normalized.Length > 0
            ? normalized
            : FileAssociationService.SupportedFileTypes.Select(type => type.Extension).ToArray();
    }

    private bool ApplyFileAssociations(
        bool showToast = false,
        IEnumerable<string>? previousCustomFileAssociationExtensions = null)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !(string.Equals(Path.GetFileName(executablePath), "Fuze.exe", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(Path.GetFileName(executablePath), "Fuse.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var publishedExecutable = Path.Combine(AppContext.BaseDirectory, "Fuse.exe");
            if (!File.Exists(publishedExecutable))
                publishedExecutable = Path.Combine(AppContext.BaseDirectory, "Fuze.exe");
            if (File.Exists(publishedExecutable))
                executablePath = publishedExecutable;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            if (showToast)
                ShowToast("Le chemin de Fuze est introuvable pour l’association des fichiers");
            return false;
        }

        if (FileAssociationService.TryApply(
                _fileAssociationsEnabled,
                _fileAssociationExtensions,
                executablePath,
                _customFileAssociationTypes,
                previousCustomFileAssociationExtensions,
                out var error))
            return true;

        if (showToast)
            ShowToast(LocalizationService.Format("Association des fichiers impossible : {0}", error));
        return false;
    }

    private static string NormalizeHdrMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "on" or "enabled" => "yes",
        "no" or "off" or "disabled" => "no",
        _ => "auto"
    };

    private static string NormalizeScreenshotBaseDirectory(string? value)
    {
        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return fallback;
        }
    }

    private static string NormalizeScreenshotFolderName(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
               name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? "Fuze"
            : name;
    }

    private static string NormalizeScreenshotFormat(string? value) =>
        string.Equals(value?.Trim(), "jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

    private static string NormalizeScreenshotAffixMode(string? value) =>
        string.Equals(value?.Trim(), "suffix", StringComparison.OrdinalIgnoreCase) ? "suffix" : "prefix";

    private void ApplyVideoStartupPresentation()
    {
        if (_videoStartupPresentationAppliedForCurrentMedia || _currentMedia is null ||
            _mediaPlayer.VideoTrackCount <= 0 || !_videoSurfaceReady)
            return;

        _videoStartupPresentationAppliedForCurrentMedia = true;
        var preserveWindowPresentation = _preserveWindowPresentationForCurrentMedia;
        _preserveWindowPresentationForCurrentMedia = false;
        if (preserveWindowPresentation)
            return;

        ApplyVideoStartInterfacePreference();
        MoveWindowToPreferredVideoDisplay();
        if (!_startVideoFullscreen || _isFullscreen)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosing && !_isFullscreen && !_fullscreenTransitionInProgress &&
                _currentMedia is not null)
                ToggleFullscreen();
        }, DispatcherPriority.Render);
    }

    private void ApplyVideoStartInterfacePreference()
    {
        // Les deux modes commencent avec les barres repliées. Le réglage
        // détermine uniquement la façon de révéler la barre supérieure :
        // l'écrou en mode discret, le survol en mode normal.
        SetToolBarPinned(false);
        HidePlaybackControls();
        _suppressToolBarActivation = _hideInterfaceOnVideoStart;
    }

    private void MoveWindowToPreferredVideoDisplay()
    {
        if (_windowHandle == IntPtr.Zero ||
            string.Equals(_preferredVideoDisplay, "auto", StringComparison.OrdinalIgnoreCase))
            return;

        var target = GetVideoMonitors().FirstOrDefault(monitor =>
            string.Equals(monitor.Id, _preferredVideoDisplay, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            _preferredVideoDisplay = "auto";
            return;
        }

        if (_isFullscreen)
        {
            var monitorBounds = target.Monitor;
            SetWindowPos(_windowHandle, IntPtr.Zero, monitorBounds.Left, monitorBounds.Top,
                monitorBounds.Right - monitorBounds.Left, monitorBounds.Bottom - monitorBounds.Top,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
            ScheduleVideoLayoutRefresh();
            QueueAdaptiveAudioDeviceUpdate();
            return;
        }

        var restoreMaximized = WindowState == WindowState.Maximized && !_startVideoFullscreen;
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;

        if (!GetWindowRect(_windowHandle, out var currentBounds))
            return;

        var workArea = target.WorkArea;
        var workWidth = Math.Max(1, workArea.Right - workArea.Left);
        var workHeight = Math.Max(1, workArea.Bottom - workArea.Top);
        var width = Math.Min(Math.Max(1, currentBounds.Right - currentBounds.Left), workWidth);
        var height = Math.Min(Math.Max(1, currentBounds.Bottom - currentBounds.Top), workHeight);
        var left = workArea.Left + (workWidth - width) / 2;
        var top = workArea.Top + (workHeight - height) / 2;
        SetWindowPos(_windowHandle, IntPtr.Zero, left, top, width, height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

        if (restoreMaximized)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isClosing && !_isFullscreen)
                    WindowState = WindowState.Maximized;
            }, DispatcherPriority.Render);
        }
        QueueAdaptiveAudioDeviceUpdate();
    }

    private void VideoOverlayWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_videoOverlayWindow is null)
            return;

        _videoOverlayHandle = new WindowInteropHelper(_videoOverlayWindow).Handle;
        if (_videoOverlayHandle == IntPtr.Zero)
            return;

        if (_startupWindowPresentationPending)
        {
            SetWindowCloaked(_videoOverlayHandle, true);
            _startupWindowsCloaked = true;
        }

        _videoOverlaySource = HwndSource.FromHwnd(_videoOverlayHandle);
        if (_videoOverlaySource?.CompositionTarget is HwndTarget target)
            target.RenderMode = RenderMode.Default;
        _videoOverlaySource?.AddHook(VideoOverlayWindowProcedure);
    }

    private void VideoOverlayWindow_OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        VideoOverlay.InvalidateMeasure();
        VideoOverlay.InvalidateArrange();
        VideoOverlay.InvalidateVisual();
        // Les commandes appartiennent à cette fenêtre d'overlay. Un changement
        // de moniteur ou d'échelle peut donc toucher l'overlay avant la fenêtre
        // principale; recalculer ici évite de conserver les dimensions de
        // l'ancien DPI dans l'éditeur de la barre.
        QueueResponsiveInterfaceScaleUpdate();
        BeginDisplayGeometryTransition();
        ScheduleVideoLayoutRefresh();
        QueueAdaptiveAudioDeviceUpdate();
    }

    private void ShowVideoOverlayWindow()
    {
        if (_videoOverlayWindow is null || _isClosing || _videoOverlayWindow.IsVisible)
            return;

        _videoOverlayWindow.Owner = this;
        UpdateVideoOverlayPresentationState();
        _videoOverlayWindow.Show();
        AlignVideoOverlayWindow();
        Dispatcher.BeginInvoke(() =>
        {
            if (_videoOverlayWindow is null || !_videoOverlayWindow.IsVisible || _isClosing)
                return;

            VideoOverlay.InvalidateMeasure();
            VideoOverlay.InvalidateArrange();
            VideoOverlay.InvalidateVisual();
            _videoOverlayWindow.UpdateLayout();
            AlignVideoOverlayWindow();
        }, DispatcherPriority.Render);
    }

    private void UpdateVideoOverlayPresentationState()
    {
        var suppressed = _videoOverlayHiddenForStartup || _modalDialogDepth > 0;
        // Ne jamais masquer visuellement la grande fenêtre transparente. Son
        // passage Hidden -> Visible force le compositeur DWM à reconstruire la
        // surface D3D11 de mpv et provoque précisément les deux à trois
        // secondes de noir observées après « Créer une nouvelle interface »
        // et après le chargement d'une vidéo. Les panneaux individuels sont
        // déjà repliés; il suffit donc de neutraliser temporairement les clics.
        VideoOverlay.Visibility = Visibility.Visible;
        VideoOverlay.IsHitTestVisible = !suppressed;
    }

    private void BringFullscreenWindowsAboveTaskbar()
    {
        if (!_isFullscreen || _isClosing || _windowHandle == IntPtr.Zero ||
            _modalDialogDepth > 0 || _settingsDialogOpening ||
            _auxiliaryDialogs.Any(dialog => dialog.IsVisible) ||
            IsWindowsScreenCaptureForeground())
            return;

        // Pendant l'ouverture automatique, WPF peut remettre l'ordre Z de la
        // fenêtre principale après le premier layout. Cette remise en avant
        // tardive garantit que la barre des tâches est bien recouverte.
        const uint staticFlags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder;
        if (IsZoomed(_windowHandle))
            ShowWindow(_windowHandle, SwRestore);
        var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            var bounds = monitorInfo.Monitor;
            SetWindowPos(_windowHandle, HwndTopmost, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
        }
        else
        {
            SetWindowPos(_windowHandle, HwndTopmost, 0, 0, 0, 0, staticFlags);
        }
        if (_videoOverlayHandle != IntPtr.Zero && IsWindowVisible(_videoOverlayHandle))
            SetWindowPos(_videoOverlayHandle, HwndTopmost, 0, 0, 0, 0, staticFlags);
    }

    private void BeginDisplayGeometryTransition()
    {
        if (_isClosing || !IsLoaded || _isLiveWindowResize)
            return;

        var generation = ++_displayTransitionGeneration;
        if (!_videoOverlayHiddenForStartup)
            HideTopBarForDisplayChange();
        // La fenêtre WPF transparente ne doit pas être redimensionnée pendant
        // que son propriétaire change de DPI/moniteur : sinon DWM conserve une
        // image composite périmée sous forme de traits. L'image mpv reste, elle,
        // visible; seule la couche de commandes est masquée une frame.
        if (!_videoOverlayHiddenForDisplayChange && _videoOverlayHandle != IntPtr.Zero &&
            IsWindowVisible(_videoOverlayHandle))
        {
            _videoOverlayHiddenForDisplayChange = true;
            ShowWindow(_videoOverlayHandle, SwHide);
        }

        Dispatcher.BeginInvoke(() => CompleteDisplayGeometryTransition(generation),
            DispatcherPriority.ContextIdle);
    }

    private void HideTopBarForDisplayChange()
    {
        if (_topBarHiddenForDisplayChange)
            return;

        _topBarHiddenForDisplayChange = true;
        _topBarVisibilityBeforeDisplayChange = ToolBarHost.Visibility;
        _topBarHitTestBeforeDisplayChange = ToolBarHost.IsHitTestVisible;
        _topBarOpacityBeforeDisplayChange = ToolBarHost.Opacity;
        _topBarTranslateBeforeDisplayChange = ToolBarTranslate.Y;
        _toolBarHideTimerBeforeDisplayChange = _toolBarHideTimer.IsEnabled;

        // Stopper l'animation évite qu'un callback de fondu ne rende la barre
        // visible pendant que la fenêtre passe d'un écran à l'autre.
        _toolBarAnimationVersion++;
        ToolBarHost.BeginAnimation(OpacityProperty, null);
        ToolBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _toolBarHideTimer.Stop();
        ToolBarHost.IsHitTestVisible = false;
        ToolBarHost.Visibility = Visibility.Hidden;
    }

    private void RestoreTopBarAfterDisplayChange()
    {
        if (!_topBarHiddenForDisplayChange)
            return;

        ToolBarHost.BeginAnimation(OpacityProperty, null);
        ToolBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ToolBarHost.Opacity = _topBarOpacityBeforeDisplayChange;
        ToolBarTranslate.Y = _topBarTranslateBeforeDisplayChange;
        ToolBarHost.Visibility = _topBarVisibilityBeforeDisplayChange;
        ToolBarHost.IsHitTestVisible = _topBarHitTestBeforeDisplayChange;
        _topBarHiddenForDisplayChange = false;

        if (_toolBarHideTimerBeforeDisplayChange)
            _toolBarHideTimer.Start();
    }

    private void CompleteDisplayGeometryTransition(int generation)
    {
        if (_isClosing || generation != _displayTransitionGeneration)
            return;

        UpdateMaximizedWorkAreaInsets();
        UpdateResponsiveInterfaceScale();
        AppShell.InvalidateMeasure();
        AppShell.InvalidateArrange();
        VideoView.InvalidateMeasure();
        VideoView.InvalidateArrange();
        AppShell.UpdateLayout();
        ResizeNativeVideoSurfaceToView();
        AttachVideoOutput();
        RequestVideoSurfaceRedraw();
        _videoOverlayWindow?.UpdateLayout();
        AlignVideoOverlayWindow();
        BringFullscreenWindowsAboveTaskbar();

        // Attendre le prochain passage de composition avant de révéler la
        // fenêtre transparente. La révéler dès ContextIdle permet à DWM de
        // recomposer une ancienne image pendant le changement de moniteur,
        // ce qui produit les traits visibles sur certains couples de DPI.
        EventHandler? finishAfterRender = null;
        finishAfterRender = (_, _) =>
        {
            CompositionTarget.Rendering -= finishAfterRender;
            FinishDisplayGeometryTransition(generation);
        };
        CompositionTarget.Rendering += finishAfterRender;
    }

    private void FinishDisplayGeometryTransition(int generation)
    {
        if (_isClosing || generation != _displayTransitionGeneration)
            return;

        // Une boîte de paramètres ou d'information doit rester au-dessus de
        // l'overlay vidéo. Le callback de composition peut arriver pendant sa
        // boucle modale; dans ce cas on laisse la couche masquée et on reprend
        // le dernier passage lorsqu'elle est fermée.
        if (_modalDialogDepth > 0 || _settingsDialogOpening ||
            _auxiliaryDialogs.Any(dialog => dialog.IsVisible))
            return;

        // Le même overlay peut être masqué par la barrière de démarrage. Ne
        // pas le révéler au milieu du passage petite fenêtre -> plein écran :
        // cela faisait apparaître les commandes au centre pendant l'ouverture.
        if (_videoOverlayHiddenForStartup)
            return;

        _videoOverlayWindow?.UpdateLayout();
        AlignVideoOverlayWindow();
        RequestVideoSurfaceRedraw();

        if (_videoOverlayHiddenForDisplayChange && _videoOverlayHandle != IntPtr.Zero)
        {
            // Restaurer le contenu avant de révéler le HWND déjà repositionné.
            // Ainsi, la barre maintenue ne peut pas être peinte brièvement avec
            // les anciennes coordonnées sur l'écran voisin.
            RestoreTopBarAfterDisplayChange();
            ShowWindow(_videoOverlayHandle, SwShowNoActivate);
            _videoOverlayHiddenForDisplayChange = false;
        }
        else
        {
            RestoreTopBarAfterDisplayChange();
        }

        BringFullscreenWindowsAboveTaskbar();
    }

    private void ResumeDisplayGeometryTransitionAfterDialog()
    {
        if (_isClosing || !_videoOverlayHiddenForDisplayChange ||
            _modalDialogDepth > 0 || _settingsDialogOpening ||
            _auxiliaryDialogs.Any(dialog => dialog.IsVisible))
            return;

        Dispatcher.BeginInvoke(() =>
            FinishDisplayGeometryTransition(_displayTransitionGeneration),
            DispatcherPriority.Render);
    }

    private void RequestVideoSurfaceRedraw()
    {
        var current = VideoView.NativeHandle;
        if (current == IntPtr.Zero)
            return;

        // libmpv utilise la fenêtre native passée par « wid ». Invalider la
        // chaîne parent/enfants après un changement de DPI/moniteur force
        // Windows à délivrer le WM_PAINT/WM_SIZE au chemin vidéo au lieu de
        // conserver des pixels de l'ancien écran dans le cache DWM.
        const uint flags = RdwInvalidate | RdwFrame | RdwAllChildren | RdwUpdateNow;
        for (var depth = 0; current != IntPtr.Zero && depth < 4; depth++)
        {
            RedrawWindow(current, IntPtr.Zero, IntPtr.Zero, flags);
            current = GetRelatedWindow(current, GwChild);
        }
    }

    private T ShowModalDialog<T>(Func<T> showDialog)
    {
        var isOutermostDialog = _modalDialogDepth++ == 0;
        if (isOutermostDialog)
        {
            _restoreVideoOverlayAfterModalDialog = _videoOverlayWindow?.IsVisible == true;
            // Garder le HWND transparent vivant évite une nouvelle
            // composition DWM à la fermeture des paramètres. Son contenu est
            // neutralisé et le dialogue, propriétaire de l'overlay, demeure
            // au-dessus sans faire descendre/remonter la pile vidéo.
            UpdateVideoOverlayPresentationState();
        }

        try
        {
            return showDialog();
        }
        finally
        {
            _modalDialogDepth = Math.Max(0, _modalDialogDepth - 1);
            if (_modalDialogDepth == 0)
            {
                var restoreOverlay = _restoreVideoOverlayAfterModalDialog;
                _restoreVideoOverlayAfterModalDialog = false;
                if (restoreOverlay && !_isClosing)
                    UpdateVideoOverlayPresentationState();

                if (!_isClosing)
                {
                    Dispatcher.BeginInvoke(UpdateActiveTopmostProtection, DispatcherPriority.Background);
                    ResumeDisplayGeometryTransitionAfterDialog();
                }
            }
        }
    }

    private void AlignVideoOverlayWindow()
    {
        if (_videoOverlayWindow is null || _isClosing || _isLiveWindowResize || !IsLoaded ||
            WindowState == WindowState.Minimized || VideoViewport.ActualWidth < 1 || VideoViewport.ActualHeight < 1)
            return;

        if (PresentationSource.FromVisual(VideoViewport) is null)
            return;

        var startDevice = VideoViewport.PointToScreen(new Point(0, 0));
        var endDevice = VideoViewport.PointToScreen(new Point(VideoViewport.ActualWidth, VideoViewport.ActualHeight));
        var left = (int)Math.Round(Math.Min(startDevice.X, endDevice.X));
        var top = (int)Math.Round(Math.Min(startDevice.Y, endDevice.Y));
        var width = Math.Max(1, (int)Math.Round(Math.Abs(endDevice.X - startDevice.X)));
        var height = Math.Max(1, (int)Math.Round(Math.Abs(endDevice.Y - startDevice.Y)));

        if (_videoOverlayWindow.Topmost)
            _videoOverlayWindow.Topmost = false;

        if (!_isFullscreen && WindowState == WindowState.Maximized && _windowHandle != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
            {
                height = Math.Max(1, Math.Min(top + height, monitorInfo.WorkArea.Bottom) - top);
            }
        }

        if (_videoOverlayHandle == IntPtr.Zero)
            _videoOverlayHandle = new WindowInteropHelper(_videoOverlayWindow).Handle;

        if (_videoOverlayHandle != IntPtr.Zero)
        {
            var flags = SwpNoActivate | SwpNoOwnerZOrder;
            if (!IsActive)
                flags |= SwpNoZOrder;
            SetWindowPos(_videoOverlayHandle, IntPtr.Zero, left, top, width, height,
                flags);
        }
    }

    private IntPtr VideoOverlayWindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        // En plein écran, le HWND de l'overlay reçoit parfois le clavier à la
        // place de la fenêtre principale. Relayer aussi Win+Maj+flèche ici
        // évite que le raccourci cesse de fonctionner après le passage en
        // plein écran.
        if (!handled && message is WmKeyDown or WmSysKeyDown &&
            (lParam.ToInt64() & (1L << 30)) == 0 &&
            IsWindowsShiftPressed() &&
            TryGetVirtualKeyDirection(wParam.ToInt32(), out var overlayDirection) &&
            MoveWindowToAdjacentMonitor(overlayDirection))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (message == WmNcHitTest &&
            (_videoOverlayHiddenForStartup || _modalDialogDepth > 0))
        {
            // Le HWND reste présent pour que DWM ne reconstruise pas la pile
            // vidéo. Lorsque son contenu est masqué, les clics doivent malgré
            // tout atteindre la boîte Fuse ou la fenêtre principale dessous.
            handled = true;
            return new IntPtr(HitTransparent);
        }

        if (message == WmNcHitTest && !_isClosing && !_isFullscreen &&
            WindowState == WindowState.Normal && GetCursorPos(out var cursor) &&
            GetWindowRect(_windowHandle != IntPtr.Zero ? _windowHandle : window, out var bounds))
        {
            // Utiliser le cadre de la fenêtre principale, pas celui de
            // l'overlay : leurs rectangles peuvent différer d'un pixel ou
            // plus après une conversion DPI WPF -> Win32.
            var bottomCornerSize = GetPhysicalCornerSize(
                _windowHandle != IntPtr.Zero ? _windowHandle : window, 7);
            var inBottomBand = cursor.Y >= bounds.Bottom - bottomCornerSize && cursor.Y < bounds.Bottom;
            var inLeftCorner = cursor.X >= bounds.Left && cursor.X < bounds.Left + bottomCornerSize;
            var inRightCorner = cursor.X >= bounds.Right - bottomCornerSize && cursor.X < bounds.Right;
            if (inBottomBand && (inLeftCorner || inRightCorner))
            {
                // L'overlay est une fenêtre séparée au-dessus du diffuseur.
                // Renvoyer HTTRANSPARENT permet à Windows de refaire le
                // WM_NCHITTEST sur la fenêtre principale, qui connaît le vrai
                // cadre à redimensionner. Un relais de clic ici était ignoré
                // par WindowChrome dans certains modes de fenêtre.
                handled = true;
                return new IntPtr(HitTransparent);
            }
        }

        if (message == WmNcLeftButtonDown && _windowHandle != IntPtr.Zero &&
            wParam.ToInt32() is HitBottomLeft or HitBottomRight)
        {
            // La couche vidéo est un HWND séparé et WindowChrome ne traite pas
            // toujours un WM_NCLBUTTONDOWN relayé depuis celui-ci. Déclencher
            // directement le redimensionnement système rend les deux coins
            // inférieurs fiables, même lorsque l'overlay est au-dessus du média.
            ReleaseCapture();
            var sizingEdge = wParam.ToInt32() == HitBottomLeft
                ? SizeBottomLeft
                : SizeBottomRight;
            SendMessage(_windowHandle, WmSysCommand,
                new IntPtr(ScSize | sizingEdge), IntPtr.Zero);
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private static int GetPhysicalCornerSize(IntPtr window, int logicalPixels)
    {
        var dpi = window == IntPtr.Zero ? 96u : GetDpiForWindow(window);
        if (dpi == 0)
            dpi = 96;

        return Math.Max(logicalPixels, (int)Math.Ceiling(logicalPixels * dpi / 96d));
    }

    private void UpdateActiveTopmostProtection()
    {
        // Capture d’écran de Windows doit pouvoir afficher son voile et ses
        // contrôles au-dessus de Fuse, même lorsque « Toujours au-dessus » est
        // activé. La suspension est temporaire; le prochain retour du focus
        // rétablit automatiquement l’ordre Z habituel.
        if (IsWindowsScreenCaptureForeground())
        {
            ReleaseActiveTopmost();
            return;
        }

        // Une fenêtre Fuse possédée par l'overlay est déjà au-dessus de la
        // vidéo. Ne jamais modifier l'ordre Z de la fenêtre principale et de
        // l'overlay pendant son ouverture ou sa fermeture : cette permutation
        // force DWM/libmpv à reconstruire la sortie vidéo.
        if (_modalDialogDepth > 0 || _settingsDialogOpening ||
            _auxiliaryDialogs.Any(dialog => dialog.IsVisible))
            return;

        if ((_alwaysOnTop && _modalDialogDepth == 0) || ShouldProtectAgainstDiscordOverlay())
        {
            if (ShouldProtectAgainstDiscordOverlay())
                HideDiscordOverlayWindows();
            MaintainActiveTopmost();
        }
        else
            ReleaseActiveTopmost();
    }

    private bool IsWindowsScreenCaptureForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _windowHandle ||
            foreground == _videoOverlayHandle)
            return false;

        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId)
            return false;

        string processName = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // La fenêtre peut disparaître entre GetForegroundWindow et la
            // résolution du processus; le titre et la classe restent un
            // second moyen de reconnaître l’outil natif.
        }
        catch (InvalidOperationException)
        {
            // Même cas lors d’une fermeture concurrente du processus.
        }

        if (processName.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ScreenSketch", StringComparison.OrdinalIgnoreCase))
            return true;

        var title = new StringBuilder(256);
        GetWindowText(foreground, title, title.Capacity);
        var titleText = title.ToString();
        if (titleText.Contains("Snipping Tool", StringComparison.OrdinalIgnoreCase) ||
            titleText.Contains("Screen snip", StringComparison.OrdinalIgnoreCase) ||
            titleText.Contains("Capture d’écran", StringComparison.OrdinalIgnoreCase) ||
            titleText.Contains("Outil Capture", StringComparison.OrdinalIgnoreCase))
            return true;

        var className = new StringBuilder(256);
        GetClassName(foreground, className, className.Capacity);
        var classText = className.ToString();
        return classText.Contains("ScreenClipping", StringComparison.OrdinalIgnoreCase) ||
               classText.Contains("SnippingTool", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldProtectAgainstDiscordOverlay()
    {
        if (_isClosing || _modalDialogDepth > 0 || _windowHandle == IntPtr.Zero)
            return false;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foregroundProcessId == (uint)Environment.ProcessId)
        {
            _lastFuseForegroundTick = Environment.TickCount64;
            // Une fenetre auxiliaire de Fuze (informations, reglages ou dialogue
            // systeme) doit rester au-dessus de la surface video et de ses commandes.
            return foreground == _windowHandle || foreground == _videoOverlayHandle;
        }

        return IsDiscordOverlayWindow(foreground) &&
               IsDiscordOverlayAssociatedWithFuse(foreground) &&
               Environment.TickCount64 - _lastFuseForegroundTick <= 1200;
    }

    private void MaintainActiveTopmost()
    {
        if (_isClosing || _windowHandle == IntPtr.Zero)
            return;

        if (_activeTopmostApplied)
            return;

        const uint flags = SwpNoMove | SwpNoSize | SwpNoRedraw | SwpNoActivate;

        DemoteDiscordOverlayWindows(flags);

        // Comme mpv avec --ontop : la vidéo passe d'abord dans la bande Topmost,
        // puis la couche de commandes est replacée juste au-dessus. La protection
        // est retirée dès que Fuse n'est plus l'application active.
        SetWindowPos(_windowHandle, HwndTopmost, 0, 0, 0, 0, flags);
        if (_videoOverlayHandle != IntPtr.Zero)
            SetWindowPos(_videoOverlayHandle, HwndTopmost, 0, 0, 0, 0, flags);
        foreach (var dialog in _auxiliaryDialogs.Where(dialog => dialog.IsVisible))
        {
            var dialogHandle = new WindowInteropHelper(dialog).Handle;
            if (dialogHandle != IntPtr.Zero)
                SetWindowPos(dialogHandle, HwndTopmost, 0, 0, 0, 0, flags);
        }
        if (_activeToolTipHandle != IntPtr.Zero)
            SetWindowPos(_activeToolTipHandle, HwndTopmost, 0, 0, 0, 0, flags);

        _activeTopmostApplied = true;
    }

    private void ReleaseActiveTopmost()
    {
        if (!_activeTopmostApplied && _windowHandle == IntPtr.Zero && _videoOverlayHandle == IntPtr.Zero &&
            _activeToolTipHandle == IntPtr.Zero)
            return;

        const uint flags = SwpNoMove | SwpNoSize | SwpNoRedraw | SwpNoActivate | SwpNoOwnerZOrder;
        foreach (var dialog in _auxiliaryDialogs.Where(dialog => dialog.IsVisible))
        {
            var dialogHandle = new WindowInteropHelper(dialog).Handle;
            if (dialogHandle != IntPtr.Zero)
                SetWindowPos(dialogHandle, HwndNotTopmost, 0, 0, 0, 0, flags);
        }
        if (_activeToolTipHandle != IntPtr.Zero)
            SetWindowPos(_activeToolTipHandle, HwndNotTopmost, 0, 0, 0, 0, flags);
        if (_videoOverlayHandle != IntPtr.Zero)
            SetWindowPos(_videoOverlayHandle, HwndNotTopmost, 0, 0, 0, 0, flags);
        if (_windowHandle != IntPtr.Zero)
            SetWindowPos(_windowHandle, HwndNotTopmost, 0, 0, 0, 0, flags);

        _activeTopmostApplied = false;
    }

    private void DemoteDiscordOverlayWindows(uint flags)
    {
        RefreshDiscordProcessIds();
        if (_discordProcessIds.Count == 0)
            return;

        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && IsDiscordOverlayWindow(window) &&
                IsDiscordOverlayAssociatedWithFuse(window))
                SetWindowPos(window, HwndNotTopmost, 0, 0, 0, 0, flags);
            return true;
        }, IntPtr.Zero);
    }

    private void InstallDiscordOverlayProtection()
    {
        if (_discordOverlayShowHook != IntPtr.Zero || _isClosing)
            return;

        _discordOverlayEventCallback = DiscordOverlayWindowShown;
        _discordOverlayCallbackHandle = GCHandle.Alloc(_discordOverlayEventCallback);
        _discordOverlayShowHook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero,
            _discordOverlayEventCallback, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        if (_discordOverlayShowHook == IntPtr.Zero)
        {
            _discordOverlayCallbackHandle.Free();
            _discordOverlayEventCallback = null;
        }
    }

    private void DiscordOverlayWindowShown(IntPtr hook, uint eventType, IntPtr window, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        if (_isClosing || eventType != EventObjectShow || objectId != ObjectIdWindow ||
            window == IntPtr.Zero)
            return;

        if (!IsDiscordOverlayWindow(window) || !IsDiscordOverlayAssociatedWithFuse(window))
            return;

        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foregroundProcessId != (uint)Environment.ProcessId &&
            Environment.TickCount64 - _lastFuseForegroundTick > 1200)
            return;

        ShowWindowAsync(window, SwHide);
    }

    private void HideDiscordOverlayWindows()
    {
        RefreshDiscordProcessIds();
        lock (_discordProcessLock)
        {
            if (_discordProcessIds.Count == 0)
                return;
        }

        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && IsDiscordOverlayWindow(window) &&
                IsDiscordOverlayAssociatedWithFuse(window))
                ShowWindowAsync(window, SwHide);
            return true;
        }, IntPtr.Zero);
    }

    private bool IsDiscordOverlayAssociatedWithFuse(IntPtr window)
    {
        var fuseWindow = _videoOverlayHandle != IntPtr.Zero && IsWindowVisible(_videoOverlayHandle)
            ? _videoOverlayHandle
            : _windowHandle;
        if (fuseWindow == IntPtr.Zero || !GetWindowRect(window, out var overlayBounds) ||
            !GetWindowRect(fuseWindow, out var fuseBounds))
            return false;

        var intersectionWidth = Math.Max(0,
            Math.Min(overlayBounds.Right, fuseBounds.Right) - Math.Max(overlayBounds.Left, fuseBounds.Left));
        var intersectionHeight = Math.Max(0,
            Math.Min(overlayBounds.Bottom, fuseBounds.Bottom) - Math.Max(overlayBounds.Top, fuseBounds.Top));
        var overlayArea = Math.Max(0L, (long)(overlayBounds.Right - overlayBounds.Left) *
                                           (overlayBounds.Bottom - overlayBounds.Top));
        var fuseArea = Math.Max(0L, (long)(fuseBounds.Right - fuseBounds.Left) *
                                        (fuseBounds.Bottom - fuseBounds.Top));
        if (overlayArea == 0 || fuseArea == 0)
            return false;

        var sharedArea = (long)intersectionWidth * intersectionHeight;
        return sharedArea >= Math.Min(overlayArea, fuseArea) * 0.65;
    }

    private bool IsDiscordOverlayWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;

        RefreshDiscordProcessIds();
        GetWindowThreadProcessId(window, out var processId);
        lock (_discordProcessLock)
        {
            if (!_discordProcessIds.Contains(processId))
                return false;
        }

        var title = new StringBuilder(128);
        GetWindowText(window, title, title.Capacity);
        var className = new StringBuilder(128);
        GetClassName(window, className, className.Capacity);
        return (title.ToString().StartsWith("Discord Overlay", StringComparison.OrdinalIgnoreCase) &&
                className.ToString() == "Chrome_WidgetWin_1") ||
               className.ToString() == "DiscordDesktopOverlayInputTrap";
    }

    private void RefreshDiscordProcessIds()
    {
        var now = Environment.TickCount64;
        lock (_discordProcessLock)
        {
            if (now < _nextDiscordProcessRefresh && _discordProcessIds.Count > 0)
                return;
        }

        _nextDiscordProcessRefresh = now + 2000;
        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("Discord"))
        {
            try
            {
                processIds.Add((uint)process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }

        lock (_discordProcessLock)
        {
            _discordProcessIds.Clear();
            _discordProcessIds.UnionWith(processIds);
        }
    }

#if false
    // Ancien chemin WPF conservé temporairement pour référence, mais retiré de la compilation.
    // Le redimensionnement actif passe désormais entièrement par la boucle native Windows.
    private void ResizeGrip_OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (_isClosing || _isFullscreen || WindowState != WindowState.Normal ||
            _windowHandle == IntPtr.Zero || sender is not Thumb thumb ||
            !Enum.TryParse(thumb.Tag?.ToString(), out ResizeCorner corner) ||
            corner == ResizeCorner.None ||
            !GetWindowRect(_windowHandle, out _customResizeStartBounds) ||
            !GetCursorPos(out _customResizeStartCursor))
        {
            return;
        }

        _customResizeCorner = corner;
        _lastCustomResizeApplyTimestamp = 0;
        _pendingCustomResizeBounds = _customResizeStartBounds;
        _lastAppliedCustomResizeBounds = _customResizeStartBounds;
        _hasPendingCustomResize = false;
        _hasCustomResizeWorkArea = false;

        var monitor = MonitorFromPoint(_customResizeStartCursor, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            _customResizeWorkArea = monitorInfo.WorkArea;
            _hasCustomResizeWorkArea = true;
        }

        CaptureLiveResizeMetrics();
        CaptureCustomResizeVideoWindows();
        _isLiveWindowResize = true;
        _videoLayoutTimer.Stop();
        if (!_customResizeRenderingSubscribed)
        {
            CompositionTarget.Rendering += CustomResize_OnRendering;
            _customResizeRenderingSubscribed = true;
        }

        e.Handled = true;
    }

    private void ResizeGrip_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_customResizeCorner == ResizeCorner.None || !GetCursorPos(out var cursor))
            return;

        QueueCustomResize(cursor);
        TryApplyPendingCustomResize();
        e.Handled = true;
    }

    private void CaptureLiveResizeMetrics()
    {
        GetVideoWindowInsets(out _liveResizeHorizontalInsets, out _liveResizeVerticalInsets);
        var dpi = VisualTreeHelper.GetDpi(this);
        _liveResizeDpiScaleX = dpi.DpiScaleX;
        _liveResizeDpiScaleY = dpi.DpiScaleY;
    }

    private void CaptureCustomResizeVideoWindows()
    {
        var windows = new List<IntPtr>(3);
        var current = VideoView.NativeHandle;
        for (var depth = 0; current != IntPtr.Zero && depth < 3; depth++)
        {
            windows.Add(current);
            current = GetRelatedWindow(current, GwChild);
        }

        _customResizeVideoWindows = windows.ToArray();
    }

    private void QueueCustomResize(NativePoint cursor)
    {
        var startWidth = Math.Max(1, _customResizeStartBounds.Right - _customResizeStartBounds.Left);
        var horizontalMovement = cursor.X - _customResizeStartCursor.X;
        var movingLeft = _customResizeCorner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft;
        var movingTop = _customResizeCorner is ResizeCorner.TopLeft or ResizeCorner.TopRight;
        var requestedWidth = movingLeft
            ? startWidth - horizontalMovement
            : startWidth + horizontalMovement;

        var horizontalInsets = _liveResizeHorizontalInsets;
        var verticalInsets = _liveResizeVerticalInsets;
        var minimumWidth = Math.Max(MinWidth * _liveResizeDpiScaleX,
            Math.Max(1d, (MinHeight * _liveResizeDpiScaleY - verticalInsets) * VideoAspectRatio + horizontalInsets));
        var maximumWidth = double.MaxValue;
        if (_hasCustomResizeWorkArea)
        {
            var workWidth = Math.Max(1, _customResizeWorkArea.Right - _customResizeWorkArea.Left);
            var workHeight = Math.Max(1, _customResizeWorkArea.Bottom - _customResizeWorkArea.Top);
            var widthAllowedByHeight = Math.Max(1d, workHeight - verticalInsets) * VideoAspectRatio +
                                       horizontalInsets;
            maximumWidth = Math.Max(1d, Math.Min(workWidth, widthAllowedByHeight));
            minimumWidth = Math.Min(minimumWidth, maximumWidth);
        }

        var width = Math.Max(1, (int)Math.Round(Math.Clamp(requestedWidth, minimumWidth, maximumWidth)));
        var videoWidth = Math.Max(1d, width - horizontalInsets);
        var height = Math.Max(1, (int)Math.Round(videoWidth / VideoAspectRatio + verticalInsets));

        var bounds = _customResizeStartBounds;
        if (movingLeft)
            bounds.Left = bounds.Right - width;
        else
            bounds.Right = bounds.Left + width;

        if (movingTop)
            bounds.Top = bounds.Bottom - height;
        else
            bounds.Bottom = bounds.Top + height;

        _pendingCustomResizeBounds = bounds;
        _hasPendingCustomResize = true;
    }

    private void CustomResize_OnRendering(object? sender, EventArgs e)
    {
        if (_customResizeCorner != ResizeCorner.None && GetCursorPos(out var cursor))
            QueueCustomResize(cursor);
        TryApplyPendingCustomResize();
    }

    private void TryApplyPendingCustomResize()
    {
        if (!_hasPendingCustomResize)
            return;

        var now = Stopwatch.GetTimestamp();
        if (_lastCustomResizeApplyTimestamp != 0 &&
            now - _lastCustomResizeApplyTimestamp < CustomResizeMinimumApplyTicks)
        {
            return;
        }

        _lastCustomResizeApplyTimestamp = now;
        ApplyPendingCustomResize();
    }

    private void ApplyPendingCustomResize()
    {
        if (!_hasPendingCustomResize || _customResizeCorner == ResizeCorner.None || _windowHandle == IntPtr.Zero)
            return;

        var bounds = _pendingCustomResizeBounds;
        _hasPendingCustomResize = false;
        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var videoWidth = Math.Max(1, width - (int)Math.Round(_liveResizeHorizontalInsets));
        var videoHeight = Math.Max(1, height - (int)Math.Round(_liveResizeVerticalInsets));
        var titleInset = Math.Max(0, (int)Math.Round(_liveResizeVerticalInsets));
        const uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging;
        var previousWidth = Math.Max(1,
            _lastAppliedCustomResizeBounds.Right - _lastAppliedCustomResizeBounds.Left);
        var growing = width >= previousWidth;

        if (growing)
            ResizeCustomVideoWindows(videoWidth, videoHeight, titleInset, flags);

        ResizeCustomOuterWindows(bounds, width, height, flags);

        if (!growing)
            ResizeCustomVideoWindows(videoWidth, videoHeight, titleInset, flags);

        _lastAppliedCustomResizeBounds = bounds;
    }

    private void ResizeCustomOuterWindows(NativeRect bounds, int width, int height, uint flags)
    {
        if (TryGetCustomVideoOverlayBounds(bounds, out var overlayBounds))
        {
            var deferred = BeginDeferWindowPos(2);
            if (deferred != IntPtr.Zero)
            {
                deferred = DeferWindowPos(deferred, _windowHandle, IntPtr.Zero,
                    bounds.Left, bounds.Top, width, height, flags);
                if (deferred != IntPtr.Zero)
                {
                    deferred = DeferWindowPos(deferred, _videoOverlayHandle, IntPtr.Zero,
                        overlayBounds.Left, overlayBounds.Top,
                        Math.Max(1, overlayBounds.Right - overlayBounds.Left),
                        Math.Max(1, overlayBounds.Bottom - overlayBounds.Top), flags);
                }

                if (deferred != IntPtr.Zero && EndDeferWindowPos(deferred))
                    return;
            }
        }

        SetWindowPos(_windowHandle, IntPtr.Zero, bounds.Left, bounds.Top, width, height, flags);
        AlignVideoOverlayDuringCustomResize(bounds);
    }

    private void ResizeCustomVideoWindows(int width, int height, int titleInset, uint flags)
    {
        for (var index = 0; index < _customResizeVideoWindows.Length; index++)
        {
            SetWindowPos(_customResizeVideoWindows[index], IntPtr.Zero,
                0, index == 0 ? titleInset : 0, width, height, flags);
        }
    }

    private bool TryGetCustomVideoOverlayBounds(NativeRect windowBounds, out NativeRect overlayBounds)
    {
        overlayBounds = default;
        if (_videoOverlayHandle == IntPtr.Zero || _videoOverlayWindow is null || !_videoOverlayWindow.IsVisible)
            return false;

        var titleInset = Math.Max(0, (int)Math.Round(_liveResizeVerticalInsets));
        overlayBounds = new NativeRect
        {
            Left = windowBounds.Left,
            Top = windowBounds.Top + titleInset,
            Right = windowBounds.Right,
            Bottom = windowBounds.Bottom
        };
        return true;
    }

    private void AlignVideoOverlayDuringCustomResize(NativeRect windowBounds)
    {
        if (!TryGetCustomVideoOverlayBounds(windowBounds, out var overlayBounds))
            return;

        SetWindowPos(_videoOverlayHandle, IntPtr.Zero, overlayBounds.Left, overlayBounds.Top,
            Math.Max(1, overlayBounds.Right - overlayBounds.Left),
            Math.Max(1, overlayBounds.Bottom - overlayBounds.Top),
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
    }

    private void ResizeGrip_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_customResizeCorner == ResizeCorner.None)
            return;

        if (GetCursorPos(out var cursor))
            QueueCustomResize(cursor);
        ApplyPendingCustomResize();

        _customResizeCorner = ResizeCorner.None;
        _hasPendingCustomResize = false;
        _hasCustomResizeWorkArea = false;
        _customResizeVideoWindows = [];
        if (_customResizeRenderingSubscribed)
        {
            CompositionTarget.Rendering -= CustomResize_OnRendering;
            _customResizeRenderingSubscribed = false;
        }

        _isLiveWindowResize = false;
        UpdateResponsiveInterfaceScale();
        AlignVideoOverlayWindow();
        ScheduleVideoLayoutRefresh();
        QueueAdaptiveAudioDeviceUpdate();
        e.Handled = true;
    }

#endif

    private void CaptureLiveResizeMetrics()
    {
        GetVideoWindowInsets(out _liveResizeHorizontalInsets, out _liveResizeVerticalInsets);
    }

    private void CaptureLiveResizeVideoWindows()
    {
        var windows = new List<IntPtr>(3);
        var current = VideoView.NativeHandle;
        for (var depth = 0; current != IntPtr.Zero && depth < 3; depth++)
        {
            windows.Add(current);
            current = GetRelatedWindow(current, GwChild);
        }

        _liveResizeVideoWindows = windows.ToArray();
    }

    private void ResizeLiveVideoWindows(NativeRect windowBounds)
    {
        if (_liveResizeVideoWindows.Length == 0)
            return;

        var width = Math.Max(1,
            windowBounds.Right - windowBounds.Left - (int)Math.Round(_liveResizeHorizontalInsets));
        var height = Math.Max(1,
            windowBounds.Bottom - windowBounds.Top - (int)Math.Round(_liveResizeVerticalInsets));
        var titleInset = Math.Max(0, (int)Math.Round(_liveResizeVerticalInsets));
        const uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging;
        for (var index = 0; index < _liveResizeVideoWindows.Length; index++)
        {
            SetWindowPos(_liveResizeVideoWindows[index], IntPtr.Zero,
                0, index == 0 ? titleInset : 0, width, height, flags);
        }
    }

    private void PrepareLiveVideoForSizing(IntPtr rectanglePointer)
    {
        if (rectanglePointer == IntPtr.Zero)
            return;

        var targetBounds = Marshal.PtrToStructure<NativeRect>(rectanglePointer);
        var previousWidth = Math.Max(1, _lastNativeSizingBounds.Right - _lastNativeSizingBounds.Left);
        var targetWidth = Math.Max(1, targetBounds.Right - targetBounds.Left);
        if (targetWidth >= previousWidth)
            ResizeLiveVideoWindows(targetBounds);
        _lastNativeSizingBounds = targetBounds;
    }

    private void FitNormalWindowToVideoAspect()
    {
        if (_isClosing || _isFullscreen || WindowState != WindowState.Normal ||
            _windowHandle == IntPtr.Zero || !GetWindowRect(_windowHandle, out var windowBounds))
            return;

        GetVideoWindowInsets(out var horizontalInsets, out var verticalInsets);
        var currentWidth = Math.Max(1, windowBounds.Right - windowBounds.Left);
        var videoWidth = Math.Max(1d, currentWidth - horizontalInsets);
        var width = videoWidth + horizontalInsets;
        var height = videoWidth / VideoAspectRatio + verticalInsets;

        var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            var workWidth = Math.Max(1, monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left);
            var workHeight = Math.Max(1, monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top);
            if (height > workHeight)
            {
                var availableVideoHeight = Math.Max(1d, workHeight - verticalInsets);
                height = availableVideoHeight + verticalInsets;
                width = availableVideoHeight * VideoAspectRatio + horizontalInsets;
            }
            if (width > workWidth)
            {
                var availableVideoWidth = Math.Max(1d, workWidth - horizontalInsets);
                width = availableVideoWidth + horizontalInsets;
                height = availableVideoWidth / VideoAspectRatio + verticalInsets;
            }
        }

        var targetWidth = Math.Max(1, (int)Math.Round(width));
        var targetHeight = Math.Max(1, (int)Math.Round(height));
        var centerX = (windowBounds.Left + windowBounds.Right) / 2;
        var centerY = (windowBounds.Top + windowBounds.Bottom) / 2;
        var left = centerX - targetWidth / 2;
        var top = centerY - targetHeight / 2;

        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            left = Math.Clamp(left, monitorInfo.WorkArea.Left,
                Math.Max(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Right - targetWidth));
            top = Math.Clamp(top, monitorInfo.WorkArea.Top,
                Math.Max(monitorInfo.WorkArea.Top, monitorInfo.WorkArea.Bottom - targetHeight));
        }

        SetWindowPos(_windowHandle, IntPtr.Zero, left, top, targetWidth, targetHeight,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private void GetVideoWindowInsets(out double horizontalInsets, out double verticalInsets)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        horizontalInsets = 0;
        verticalInsets = (_isFullscreen ? 0 : TitleRow.ActualHeight) * dpi.DpiScaleY;

        if (ActualWidth < 1 || ActualHeight < 1 || VideoViewport.ActualWidth < 1 || VideoViewport.ActualHeight < 1)
            return;

        horizontalInsets = Math.Max(0, ActualWidth - VideoViewport.ActualWidth) * dpi.DpiScaleX;
        verticalInsets = Math.Max(0, ActualHeight - VideoViewport.ActualHeight) * dpi.DpiScaleY;
    }

    private bool ConstrainSizingRectangle(IntPtr sizingEdge, IntPtr rectanglePointer)
    {
        if (_isFullscreen || WindowState != WindowState.Normal ||
            rectanglePointer == IntPtr.Zero || _windowHandle == IntPtr.Zero)
            return false;

        var edge = sizingEdge.ToInt32();
        if (edge is < SizingLeft or > SizingBottomRight)
            return false;

        var bounds = Marshal.PtrToStructure<NativeRect>(rectanglePointer);
        var horizontalInsets = _isLiveWindowResize ? _liveResizeHorizontalInsets : 0;
        var verticalInsets = _isLiveWindowResize ? _liveResizeVerticalInsets : 0;
        if (!_isLiveWindowResize)
            GetVideoWindowInsets(out horizontalInsets, out verticalInsets);
        var candidateWidth = Math.Max(1d, bounds.Right - bounds.Left);
        var candidateHeight = Math.Max(1d, bounds.Bottom - bounds.Top);
        var candidateVideoWidth = Math.Max(1d, candidateWidth - horizontalInsets);
        var candidateVideoHeight = Math.Max(1d, candidateHeight - verticalInsets);

        var movingLeft = edge is SizingLeft or SizingTopLeft or SizingBottomLeft;
        var movingRight = edge is SizingRight or SizingTopRight or SizingBottomRight;
        var movingTop = edge is SizingTop or SizingTopLeft or SizingTopRight;
        var movingBottom = edge is SizingBottom or SizingBottomLeft or SizingBottomRight;
        // Un coin suit toujours la largeur : aucun basculement de calcul pendant le glissement.
        var widthDriven = edge is not SizingTop and not SizingBottom;

        double targetWidth;
        double targetHeight;
        if (widthDriven)
        {
            targetWidth = candidateVideoWidth + horizontalInsets;
            targetHeight = candidateVideoWidth / VideoAspectRatio + verticalInsets;
        }
        else
        {
            targetHeight = candidateVideoHeight + verticalInsets;
            targetWidth = candidateVideoHeight * VideoAspectRatio + horizontalInsets;
        }

        var pixelWidth = Math.Max(1, (int)Math.Round(targetWidth));
        var pixelHeight = Math.Max(1, (int)Math.Round(targetHeight));
        if (movingLeft)
            bounds.Left = bounds.Right - pixelWidth;
        else if (movingRight)
            bounds.Right = bounds.Left + pixelWidth;
        else
        {
            var centerX = (bounds.Left + bounds.Right) / 2;
            bounds.Left = centerX - pixelWidth / 2;
            bounds.Right = bounds.Left + pixelWidth;
        }

        if (movingTop)
            bounds.Top = bounds.Bottom - pixelHeight;
        else if (movingBottom)
            bounds.Bottom = bounds.Top + pixelHeight;
        else
        {
            var centerY = (bounds.Top + bounds.Bottom) / 2;
            bounds.Top = centerY - pixelHeight / 2;
            bounds.Bottom = bounds.Top + pixelHeight;
        }

        Marshal.StructureToPtr(bounds, rectanglePointer, false);
        return true;
    }

    private void HidePlaybackBarsForResize()
    {
        if (_playbackBarsHiddenForResize)
            return;

        _playbackBarsHiddenForResize = true;
        _topBarVisibilityBeforeResize = ToolBarHost.Visibility;
        _bottomBarVisibilityBeforeResize = ControlsPanel.Visibility;
        _topBarHitTestBeforeResize = ToolBarHost.IsHitTestVisible;
        _bottomBarHitTestBeforeResize = ControlsPanel.IsHitTestVisible;
        _controlsHideTimerBeforeResize = _controlsHideTimer.IsEnabled;
        _gearControlsHideTimerBeforeResize = _gearControlsHideTimer.IsEnabled;
        _toolBarHideTimerBeforeResize = _toolBarHideTimer.IsEnabled;

        // Stop the independent timers while Windows owns the live resize loop.
        // Otherwise one of them can complete a fade halfway through the resize
        // and restore only one of the two bars.
        _controlsHideTimer.Stop();
        _gearControlsHideTimer.Stop();
        _toolBarHideTimer.Stop();

        // Hidden preserves each row's measured size, preventing the video and
        // overlay from jumping as the native window is resized. The exact
        // visibility and hit-test state are restored when the loop ends.
        if (ToolBarHost.Visibility == Visibility.Visible)
            ToolBarHost.Visibility = Visibility.Hidden;
        if (ControlsPanel.Visibility == Visibility.Visible)
            ControlsPanel.Visibility = Visibility.Hidden;
        ToolBarHost.IsHitTestVisible = false;
        ControlsPanel.IsHitTestVisible = false;
    }

    private void RestorePlaybackBarsAfterResize()
    {
        if (!_playbackBarsHiddenForResize)
            return;

        ToolBarHost.Visibility = _topBarVisibilityBeforeResize;
        ControlsPanel.Visibility = _bottomBarVisibilityBeforeResize;
        ToolBarHost.IsHitTestVisible = _topBarHitTestBeforeResize;
        ControlsPanel.IsHitTestVisible = _bottomBarHitTestBeforeResize;
        _playbackBarsHiddenForResize = false;

        if (_controlsHideTimerBeforeResize)
            _controlsHideTimer.Start();
        if (_gearControlsHideTimerBeforeResize)
            _gearControlsHideTimer.Start();
        if (_toolBarHideTimerBeforeResize)
            _toolBarHideTimer.Start();
    }

    private void BeginNativeLiveResize()
    {
        if (!_isLiveWindowResize)
        {
            _isLiveWindowResize = true;
            CaptureLiveResizeMetrics();
            CaptureLiveResizeVideoWindows();
            GetWindowRect(_windowHandle, out _lastNativeSizingBounds);
        }

        HidePlaybackBarsForResize();

        _videoLayoutTimer.Stop();
        if (!_videoOverlayHiddenForResize && _videoOverlayHandle != IntPtr.Zero &&
            IsWindowVisible(_videoOverlayHandle))
        {
            _videoOverlayHiddenForResize = true;
            ShowWindow(_videoOverlayHandle, SwHide);
        }

    }

    private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && IsFuzeForegroundWindow() &&
            TryGetRegisteredWindowMoveDirection(wParam.ToInt32(), out var hotKeyDirection))
        {
            if (MoveWindowToAdjacentMonitor(hotKeyDirection))
                handled = true;
            return IntPtr.Zero;
        }

        // Si Windows n'autorise pas l'enregistrement du raccourci global,
        // traiter aussi le message clavier natif lorsque la combinaison est
        // encore livrée à la fenêtre. Le bit 30 évite de déplacer la fenêtre
        // plusieurs fois lorsqu'une touche reste enfoncée.
        if (!handled && message is WmKeyDown or WmSysKeyDown &&
            (lParam.ToInt64() & (1L << 30)) == 0 &&
            IsWindowsShiftPressed() &&
            TryGetVirtualKeyDirection(wParam.ToInt32(), out var keyDirection) &&
            MoveWindowToAdjacentMonitor(keyDirection))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            // WM_DISPLAYCHANGE couvre les changements de résolution/ratio
            // qui ne déclenchent pas toujours DpiChanged dans WPF.
            if (message == WmDisplayChange && VideoView.NativeHandle != IntPtr.Zero)
                PostMessage(VideoView.NativeHandle, message, wParam, lParam);
            BeginDisplayGeometryTransition();
            ScheduleVideoLayoutRefresh();
        }

        if (message == WmNcHitTest && !_isClosing && !_isFullscreen &&
            WindowState == WindowState.Normal && GetCursorPos(out var hitCursor) &&
            GetWindowRect(window, out var hitBounds))
        {
            var topCornerSize = GetPhysicalCornerSize(window, 4);
            var bottomCornerSize = GetPhysicalCornerSize(window, 7);
            var inTopBand = hitCursor.Y >= hitBounds.Top &&
                            hitCursor.Y < hitBounds.Top + topCornerSize;
            var inBottomBand = hitCursor.Y >= hitBounds.Bottom - bottomCornerSize &&
                               hitCursor.Y < hitBounds.Bottom;
            var inTopLeft = inTopBand && hitCursor.X >= hitBounds.Left &&
                            hitCursor.X < hitBounds.Left + topCornerSize;
            var inTopRight = inTopBand && hitCursor.X >= hitBounds.Right - topCornerSize &&
                             hitCursor.X < hitBounds.Right;
            var inBottomLeft = inBottomBand && hitCursor.X >= hitBounds.Left &&
                               hitCursor.X < hitBounds.Left + bottomCornerSize;
            var inBottomRight = inBottomBand && hitCursor.X >= hitBounds.Right - bottomCornerSize &&
                                hitCursor.X < hitBounds.Right;
            if (inTopLeft || inTopRight || inBottomLeft || inBottomRight)
            {
                handled = true;
                return new IntPtr(inTopLeft ? HitTopLeft :
                    inTopRight ? HitTopRight :
                    inBottomLeft ? HitBottomLeft : HitBottomRight);
            }
        }

        if (message == WmEnterSizeMove)
            BeginNativeLiveResize();

        if (message == WmSizing)
        {
            // Certaines routes WindowChrome omettent WM_ENTERSIZEMOVE. WM_SIZING est
            // donc aussi une garantie que la couche transparente ne reste pas visible
            // une image derriere la nouvelle geometrie de la fenetre principale.
            BeginNativeLiveResize();
            if (ConstrainSizingRectangle(wParam, lParam))
            {
                // En croissance, la surface vidéo prend sa nouvelle taille avant
                // que Windows expose la portion agrandie de la fenêtre.
                PrepareLiveVideoForSizing(lParam);
                handled = true;
                return new IntPtr(1);
            }
        }

        if (message == WmSize && _isLiveWindowResize && _liveResizeVideoWindows.Length > 0 &&
            GetWindowRect(window, out var resizedBounds))
        {
            // En réduction, le parent coupe d'abord l'ancienne grande surface puis
            // celle-ci rejoint immédiatement la géométrie réellement appliquée.
            ResizeLiveVideoWindows(resizedBounds);
            _lastNativeSizingBounds = resizedBounds;
        }

        if (message == WmExitSizeMove)
        {
            _isLiveWindowResize = false;
            Dispatcher.BeginInvoke(() =>
            {
                AppShell.InvalidateMeasure();
                AppShell.InvalidateArrange();
                VideoView.InvalidateMeasure();
                VideoView.InvalidateArrange();
                AppShell.UpdateLayout();
                AlignVideoOverlayWindow();
                _videoOverlayWindow?.UpdateLayout();
                UpdateResponsiveInterfaceScale();
                _videoOverlayWindow?.UpdateLayout();
                AlignVideoOverlayWindow();
                if (_currentIndex >= 0)
                    ResizeNativeVideoSurfaceToView();
                _liveResizeVideoWindows = [];
                if (_videoOverlayHiddenForResize && _videoOverlayHandle != IntPtr.Zero)
                    ShowWindow(_videoOverlayHandle, SwShowNoActivate);
                _videoOverlayHiddenForResize = false;
                RestorePlaybackBarsAfterResize();
                ScheduleVideoLayoutRefresh();
            }, DispatcherPriority.Render);
        }

        return IntPtr.Zero;
    }

    private static bool IsWindowsShiftPressed() =>
        GetAsyncKeyState(VkShift) < 0 &&
        (GetAsyncKeyState(VkLeftWindows) < 0 || GetAsyncKeyState(VkRightWindows) < 0);

    private static bool TryGetVirtualKeyDirection(int virtualKey, out Key direction)
    {
        direction = virtualKey switch
        {
            VkLeft => Key.Left,
            VkUp => Key.Up,
            VkRight => Key.Right,
            VkDown => Key.Down,
            _ => Key.None
        };
        return direction != Key.None;
    }

    private static bool TryGetRegisteredWindowMoveDirection(int hotKeyId, out Key direction)
    {
        direction = hotKeyId - WindowMoveHotkeyBaseId switch
        {
            0 => Key.Left,
            1 => Key.Up,
            2 => Key.Right,
            3 => Key.Down,
            _ => Key.None
        };
        return direction != Key.None;
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Même pendant un redimensionnement en direct, la zone du titre doit
        // suivre la largeur disponible au lieu de recouvrir les commandes.
        UpdateResponsiveBottomBarTitleWidthForCurrentLayout();
        if (_isLiveWindowResize)
            return;

        UpdateResponsiveInterfaceScale();
        QueueResponsiveInterfaceScaleUpdate();
        QueueBottomBarGuidePositionUpdate();
        ScheduleVideoLayoutRefresh();
    }

    private void ControlsPanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isClosing)
            return;

        // Le panneau peut être redimensionné après la fenêtre (notamment avec
        // l'overlay de lecture). Recalculer ici garantit que la largeur du nom
        // suit la surface réellement affichée, même si l'événement de fenêtre
        // est arrivé avant la nouvelle passe de layout WPF.
        UpdateResponsiveBottomBarTitleWidthForCurrentLayout();
        if (BottomBarFreeLayoutCanvas.Visibility == Visibility.Visible)
            PositionBottomBarFreeLayout();
    }

    private void Window_OnLocationChanged(object? sender, EventArgs e)
    {
        if (_isLiveWindowResize)
            return;

        UpdateMaximizedWorkAreaInsets();
        AlignVideoOverlayWindow();
        if (_isFullscreen)
            BringFullscreenWindowsAboveTaskbar();
        ScheduleVideoLayoutRefresh();
        QueueAdaptiveAudioDeviceUpdate();
    }

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        UpdateMaximizedWorkAreaInsets();
        UpdateResponsiveInterfaceScale();
        QueueResponsiveInterfaceScaleUpdate();
        ScheduleVideoLayoutRefresh();
        QueueAdaptiveAudioDeviceUpdate();
    }

    private void Window_OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        UpdateMaximizedWorkAreaInsets();
        UpdateResponsiveInterfaceScale();
        QueueResponsiveInterfaceScaleUpdate();
        BeginDisplayGeometryTransition();
        ScheduleVideoLayoutRefresh();
    }

    private void QueueResponsiveInterfaceScaleUpdate()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosing)
                UpdateResponsiveInterfaceScale();
        }, DispatcherPriority.Render);
    }

    private double GetBottomBarHighDpiAdjustment()
    {
        var controlsDpi = VisualTreeHelper.GetDpi(ControlsContentGrid);
        var dpiScaleX = controlsDpi.DpiScaleX > 0 ? controlsDpi.DpiScaleX : 1d;
        return dpiScaleX <= 1.5d
            ? 1d
            : Math.Clamp(1d - ((dpiScaleX - 1.5d) * 0.1d), 0.85d, 1d);
    }

    private void UpdateResponsiveInterfaceScale()
    {
        var width = VideoViewport.ActualWidth > 0
            ? VideoViewport.ActualWidth
            : ActualWidth > 0
                ? ActualWidth
                : VideoOverlay.ActualWidth;
        if (width <= 0)
            return;

        var scale = _adaptiveInterfaceScale
            ? Math.Clamp(width / 1280d, 0.5, 1.2d)
            : 1d;

        // Windows fournit déjà à WPF une surface exprimée dans le repère du
        // moniteur courant. Diviser directement par le facteur DPI rendrait les
        // commandes minuscules à 300 %. On applique seulement une réduction
        // progressive au-dessus de 150 % : 100–150 % restent inchangés et
        // 300 % termine 15 % plus petit, dans l'éditeur comme en lecture.
        var highDpiAdjustment = GetBottomBarHighDpiAdjustment();
        var bottomBarScale = Math.Min(scale, 1d) * highDpiAdjustment;

        var interfaceScaleChanged = Math.Abs(scale - _interfaceScale) >= 0.005;
        var bottomBarScaleChanged =
            Math.Abs(bottomBarScale - ControlsScaleTransform.ScaleX) >= 0.005;
        if (!interfaceScaleChanged && !bottomBarScaleChanged)
        {
            // Même si le facteur final est identique, le Canvas a pu changer de
            // largeur logique après un changement de DPI. Ses positions sont
            // normalisées, mais elles doivent être projetées de nouveau.
            PositionBottomBarFreeLayout();
            UpdateBottomBarLayoutItemBounds();
            QueueBottomBarGuidePositionUpdate();
            return;
        }

        _interfaceScale = scale;
        ControlsScaleTransform.ScaleX = bottomBarScale;
        ControlsScaleTransform.ScaleY = bottomBarScale;
        TopMenuScaleTransform.ScaleX = scale;
        TopMenuScaleTransform.ScaleY = scale;
        ControlActivationZone.Height = 60 * scale;
        ToolBarToggleButton.Width = 32 * scale;
        ToolBarToggleButton.Height = 33 * scale;
        ToolBarToggleButton.FontSize = 9 * scale;

        if (ToolBarHost.Visibility == Visibility.Visible)
            ToolBarHost.Height = 33 * scale;

        ControlsLayoutRoot.InvalidateMeasure();
        ControlsLayoutRoot.InvalidateArrange();
        UpdateResponsiveBottomBarTitleWidthForCurrentLayout();
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing)
                return;
            PositionBottomBarFreeLayout();
            UpdateBottomBarLayoutItemBounds();
        }, DispatcherPriority.Render);
        QueueBottomBarGuidePositionUpdate();
    }

    private void UpdateMaximizedWorkAreaInsets()
    {
        if (_isFullscreen || WindowState != WindowState.Maximized || _windowHandle == IntPtr.Zero ||
            !GetWindowRect(_windowHandle, out var windowBounds))
        {
            if (AppShell.Margin != new Thickness(0))
                AppShell.Margin = new Thickness(0);
            return;
        }

        var monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var margin = new Thickness(
            Math.Max(0, monitorInfo.WorkArea.Left - windowBounds.Left) / dpi.DpiScaleX,
            Math.Max(0, monitorInfo.WorkArea.Top - windowBounds.Top) / dpi.DpiScaleY,
            Math.Max(0, windowBounds.Right - monitorInfo.WorkArea.Right) / dpi.DpiScaleX,
            Math.Max(0, windowBounds.Bottom - monitorInfo.WorkArea.Bottom) / dpi.DpiScaleY);

        if (Math.Abs(AppShell.Margin.Left - margin.Left) > 0.25 ||
            Math.Abs(AppShell.Margin.Top - margin.Top) > 0.25 ||
            Math.Abs(AppShell.Margin.Right - margin.Right) > 0.25 ||
            Math.Abs(AppShell.Margin.Bottom - margin.Bottom) > 0.25)
        {
            AppShell.Margin = margin;
        }
    }

    private void ScheduleVideoLayoutRefresh()
    {
        if (_isClosing || !IsLoaded)
            return;

        if (!_videoLayoutTimer.IsEnabled)
            _videoLayoutTimer.Start();
    }

    private void RefreshVideoLayout()
    {
        if (_isClosing || !IsLoaded)
            return;

        UpdateMaximizedWorkAreaInsets();
        AppShell.InvalidateMeasure();
        AppShell.InvalidateArrange();
        VideoView.InvalidateMeasure();
        VideoView.InvalidateArrange();
        VideoView.InvalidateVisual();
        VideoOverlay.InvalidateVisual();
        AppShell.UpdateLayout();
        ResizeNativeVideoSurfaceToView();
        AlignVideoOverlayWindow();
        BringFullscreenWindowsAboveTaskbar();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button)
            return;

        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private static void SetEngineState(string text, string color)
    {
        // L’état du moteur reste interne afin de garder la barre de titre épurée.
    }

    private void ShowToast(string message)
    {
        WriteDiagnosticLog(message);
        if (!_showOsd)
            return;

        ToastText.Text = LocalizationService.Get(message);
        Toast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void WriteDiagnosticLog(string message)
    {
        if (!_diagnosticLoggingEnabled || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            // Les événements sont écrits dans la langue active au moment où
            // ils sont produits. Cela ne retraduit pas la boîte de diagnostic
            // et ne modifie pas les anciennes entrées déjà enregistrées.
            var localizedMessage = LocalizationService.Get(message);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fuze");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "diagnostic.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.WriteAllText(path, string.Empty);
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {localizedMessage}{Environment.NewLine}");
        }
        catch
        {
            // Le journal ne doit jamais interrompre la lecture.
        }
    }

    private void TraceStartupPlayback(string stage)
    {
        if (!_diagnosticLoggingEnabled || _startupPlaybackTraceStartedAt == 0 ||
            string.IsNullOrWhiteSpace(stage))
            return;

        var elapsedMilliseconds = Stopwatch.GetElapsedTime(
            _startupPlaybackTraceStartedAt).TotalMilliseconds;
        WriteDiagnosticLog(LocalizationService.Format(
            "Démarrage vidéo +{0:0} ms • {1}",
            elapsedMilliseconds,
            LocalizationService.Get(stage)));
    }

    private void Dispatch(Action action)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(action);
    }

    private static string FormatTime(long milliseconds, bool showMilliseconds = false)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        var value = time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
        return showMilliseconds
            ? $"{value}.{time.Milliseconds:000}"
            : value;
    }

    private void PersistResumeCheckpoint()
    {
        if (_isClosing || _currentMedia is null || _currentIndex < 0 ||
            string.IsNullOrWhiteSpace(_currentMedia.Location))
            return;

        // Pendant le chargement, mpv expose brièvement 0 ms et une durée nulle.
        // Ne pas écraser ainsi le point de reprise qui doit encore être proposé.
        if ((_pauseAfterOpeningForResumePrompt && _pendingResumePromptPositionMilliseconds > 0) ||
            (_mediaPlayer.Length <= 0 && _mediaPlayer.Time <= 0))
            return;

        var location = _currentMedia.Location;
        var position = Math.Max(0, _mediaPlayer.Time);
        // Éviter les écritures inutiles lorsque la lecture est en pause ou
        // lorsque mpv n'a pas encore avancé depuis le dernier point.
        if (string.Equals(_lastPersistedResumeLocation, location,
                StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(position - _lastPersistedResumePositionMilliseconds) < 500)
            return;

        _lastMediaLocation = location;
        _lastMediaPositionMilliseconds = position;
        _lastPersistedResumeLocation = location;
        _lastPersistedResumePositionMilliseconds = position;
        PersistSession();
    }

    private void PersistSession()
    {
        SaveCurrentMediaPlaybackPreferences();
        PruneRecentMediaByRetention();
        PruneMediaPlaybackPreferences();
        var session = new PlayerSession
        {
            Volume = Math.Clamp((int)Math.Round(VolumeSlider.Value), 0, 125),
            ResetVolumeOnStartup = _resetVolumeOnStartup,
            StartupVolume = _startupVolume,
            SelectedIndex = -1,
            PlaylistVisible = false,
            RewindSeconds = _rewindSeconds,
            ForwardSeconds = _forwardSeconds,
            PrioritizeChapters = _prioritizeChapters,
            PlayNextMediaAutomatically = _playNextMediaAutomatically,
            EnhancedPlaybackEnabled = _enhancedPlaybackEnabled,
            EnhancedFolderAdvanceEnabled = _enhancedFolderAdvanceEnabled,
            EnhancedFolderShowNameEnabled = _enhancedFolderShowNameEnabled,
            ShowEnhancedUpcomingInPlaylist = _showEnhancedUpcomingInPlaylist,
            ShowEnhancedNextFolderInPlaylist = _showEnhancedNextFolderInPlaylist,
            ResumePlayback = _resumePlayback,
            ResumePromptStartSkipPercent = _resumePromptStartSkipPercent,
            ResumePromptEndSkipPercent = _resumePromptEndSkipPercent,
            AutoPlayOnOpen = _autoPlayOnOpen,
            ConfirmClose = _confirmClose,
            PreventSleepDuringPlayback = _preventSleepDuringPlayback,
            RememberMediaSettings = _rememberMediaSettings,
            RecentMediaRetentionDays = _recentMediaRetentionDays,
            RecentMediaFolderDepth = _recentMediaFolderDepth,
            PlaylistFolderDepth = _playlistFolderDepth,
            FileAssociationsEnabled = _fileAssociationsEnabled,
            FileAssociationExtensions = [.. _fileAssociationExtensions],
            CustomFileAssociationTypes = [.. _customFileAssociationTypes.Select(type => new CustomFileAssociationData
            {
                Title = type.Title,
                Extension = type.Extension,
                IsAudio = type.IsAudio,
                Enabled = type.Enabled
            })],
            ShufflePlayback = _shufflePlayback,
            RepeatPlayback = _repeatPlayback,
            RepeatPlaylist = _repeatPlaylist,
            LastMediaLocation = _currentIndex >= 0 && _currentIndex < Playlist.Count
                ? Playlist[_currentIndex].Location
                : _lastMediaLocation,
            LastMediaPositionMilliseconds = _currentMedia is not null
                ? Math.Max(0, _mediaPlayer.Time)
                : _lastMediaPositionMilliseconds,
            HardwareDecoding = _hardwareDecoding,
            Deinterlacing = _deinterlacing,
            HdrMode = _hdrMode,
            BufferingEnabled = _bufferingEnabled,
            AudioNormalization = _audioNormalization,
            AutoSwitchAudioDevice = _autoSwitchAudioDevice,
            AdaptiveAudioModeEnabled = _adaptiveAudioModeEnabled,
            AdaptiveAudioDisplayMappings = [.. _adaptiveAudioDisplayMappings.Select(mapping =>
                new AdaptiveAudioDisplayMappingData
                {
                    DisplayId = mapping.DisplayId,
                    DisplayName = mapping.DisplayName,
                    AudioDevice = mapping.AudioDevice
                })],
            PreferSdhSubtitles = _preferSdhSubtitles,
            ShowScreenshotButton = _showScreenshotButton,
            ShowShuffleButton = _showShuffleButton,
            ShowRepeatButton = _showRepeatButton,
            ShowSpeedButton = _showSpeedButton,
            ShowPlaylistButton = _showPlaylistButton,
            ShowVideoPanButton = _showVideoPanButton,
            ShowAdditionalMediaInformation = _showAdditionalMediaInformation,
            AdaptiveInterfaceScale = _adaptiveInterfaceScale,
            AutoHideCursor = _autoHideCursor,
            CursorAutoHideDelayMilliseconds = _cursorAutoHideDelayMilliseconds,
            AlwaysOnTop = _alwaysOnTop,
            ShowOsd = _showOsd,
            InterfaceLanguage = _interfaceLanguage,
            DisableToolTips = _disableToolTips,
            ShowChapterNameInSeekPreview = _showChapterNameInSeekPreview,
            TogglePlaybackOnSingleClick = _togglePlaybackOnSingleClick,
            ToggleFullscreenOnDoubleClick = _toggleFullscreenOnDoubleClick,
            DiscordActivityEnabled = _discordActivityEnabled,
            DiagnosticLoggingEnabled = _diagnosticLoggingEnabled,
            TopBarAutoHideDelayMilliseconds = _topBarAutoHideDelayMilliseconds,
            BottomBarAutoHideDelayMilliseconds = _bottomBarAutoHideDelayMilliseconds,
            PlaylistScrollSpeed = _playlistScrollSpeed,
            AutoCompactMissingBottomBarItems = _autoCompactMissingBottomBarItems,
            BottomBarLayoutPresets = [.. _bottomBarLayoutPresets.Select(CloneBottomBarLayout)],
            ActiveBottomBarLayoutPreset = _activeBottomBarLayoutPreset,
            VolumeControlStyle = _volumeControlStyle,
            VolumePopupHideDelayMilliseconds = _volumePopupHideDelayMilliseconds,
            VolumeIndicatorHideDelayMilliseconds = _volumeIndicatorHideDelayMilliseconds,
            HideInterfaceOnVideoStart = _hideInterfaceOnVideoStart,
            ShowSynchronizationButton = _showSynchronizationButton,
            StartVideoFullscreen = _startVideoFullscreen,
            PreferredVideoDisplay = _preferredVideoDisplay,
            VideoOutput = _videoOutput,
            CustomZoomPercent = _customZoomPercent,
            CustomAspectRatio = _customAspectRatio,
            ScreenshotBaseDirectory = _screenshotBaseDirectory,
            ScreenshotFolderName = _screenshotFolderName,
            ScreenshotFormat = _screenshotFormat,
            ScreenshotAffixMode = _screenshotAffixMode,
            ScreenshotAffixText = _screenshotAffixText,
            ScreenshotSequentialNumbering = _screenshotSequentialNumbering,
            CopyScreenshotsToClipboard = _copyScreenshotsToClipboard,
            KeyboardShortcuts = new Dictionary<string, string>(_keyboardShortcuts,
                StringComparer.OrdinalIgnoreCase),
            MouseWheelTimelineEnabled = _mouseWheelTimelineEnabled,
            MouseWheelVolumeEnabled = _mouseWheelVolumeEnabled,
            CenterWheelVolumeEnabled = _centerWheelVolumeEnabled,
            CenterWheelTimelineEnabled = _centerWheelTimelineEnabled,
            MouseWheelAudioTracksEnabled = _mouseWheelAudioTracksEnabled,
            MouseWheelSubtitleTracksEnabled = _mouseWheelSubtitleTracksEnabled,
            IgnoreKeyboardVolumeButtons = _ignoreKeyboardVolumeButtons,
            AudioDevice = _selectedAudioDevice,
            AudioOutputMode = (int)_audioOutputMode,
            AudioTreatmentMode = (int)_audioTreatmentMode,
            AudioPassthrough = _audioPassthrough,
            AudioExclusive = _audioExclusive,
            DisableAudioByDefault = _disableAudioByDefault,
            AutoSelectPreferredAudio = _autoSelectPreferredAudio,
            PreferredAudioProfile = _preferredAudioProfile,
            PreferredAudioTitlePriorities = [.. _preferredAudioTitlePriorities],
            DefaultAudioDelayMilliseconds = _defaultAudioDelayMilliseconds,
            StartupTitleOverlayEnabled = _startupTitleOverlayEnabled,
            PreferOriginalTitleForStartup = _preferOriginalTitleForStartup,
            StartupTitlePosition = _startupTitlePosition,
            StartupTitleDelayMilliseconds = _startupTitleDelayMilliseconds,
            StartupTitleDurationMilliseconds = _startupTitleDurationMilliseconds,
            StartupTitleFont = _startupTitleFont,
            StartupTitleFontSize = _startupTitleFontSize,
            StartupTitleTextColor = _startupTitleTextColor,
            StartupTitleBorderColor = _startupTitleBorderColor,
            StartupTitleBorderSize = _startupTitleBorderSize,
            StartupTitleShadow = _startupTitleShadow,
            StartupTitleMarginX = _startupTitleMarginX,
            StartupTitleMarginY = _startupTitleMarginY,
            StartupTitleScaleWithWindow = _startupTitleScaleWithWindow,
            AutoSelectPreferredSubtitle = _autoSelectPreferredSubtitle,
            AutoLoadExternalSubtitles = _autoLoadExternalSubtitles,
            PreferredSubtitleProfile = _preferredSubtitleProfile,
            PreferredSubtitleTitlePriorities = [.. _preferredSubtitleTitlePriorities],
            DisableSubtitlesByDefault = _disableSubtitlesByDefault,
            SubtitleEncoding = _subtitleEncoding,
            SubtitleFont = _subtitleFont,
            SubtitleFontSize = _subtitleFontSize,
            SubtitleTextColor = _subtitleTextColor,
            SubtitleBorderColor = _subtitleBorderColor,
            SubtitleBorderSize = _subtitleBorderSize,
            SubtitleShadow = _subtitleShadow,
            SubtitleForcePosition = _subtitleForcePosition,
            SubtitlePosition = _subtitlePosition,
            SubtitleMarginX = _subtitleMarginX,
            SubtitleMarginY = _subtitleMarginY,
            SubtitleScaleWithWindow = _subtitleScaleWithWindow,
            RecentMedia = [.. _recentMedia],
            RecentMediaLastOpenedUtc = new Dictionary<string, DateTime>(
                _recentMediaLastOpenedUtc, StringComparer.OrdinalIgnoreCase),
            MediaPlaybackPreferences = _mediaPlaybackPreferences.ToDictionary(
                pair => pair.Key,
                pair => new MediaPlaybackPreferencesData
                {
                    VideoTrackId = pair.Value.VideoTrackId,
                    AudioTrackId = pair.Value.AudioTrackId,
                    SubtitleTrackId = pair.Value.SubtitleTrackId,
                    PlaybackRate = pair.Value.PlaybackRate,
                    VideoZoom = pair.Value.VideoZoom,
                    VideoSyncMilliseconds = pair.Value.VideoSyncMilliseconds,
                    AudioSyncMilliseconds = pair.Value.AudioSyncMilliseconds,
                    SubtitleSyncMilliseconds = pair.Value.SubtitleSyncMilliseconds,
                    UpdatedUtc = pair.Value.UpdatedUtc
                }, StringComparer.OrdinalIgnoreCase),
            Playlist = [.. Playlist.Select(item => new PlaylistItemData
            {
                Location = item.Location,
                Title = item.Title,
                IsNetwork = item.IsNetwork,
                DurationMilliseconds = Math.Max(0, item.DurationMilliseconds),
                IsEnhancedQueued = item.IsEnhancedQueued,
                IsEnhancedFolderStart = item.IsEnhancedFolderStart,
                EnhancedFolderTitle = item.EnhancedFolderTitle,
                IsManualQueueItem = item.IsManualQueueItem
            })]
        };
        _sessionStore.Save(session);
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_isClosing && _confirmClose)
        {
            if (!AskToConfirmClose())
            {
                e.Cancel = true;
                return;
            }
        }

        _isClosing = true;
        _uiTimer.Stop();
        _toastTimer.Stop();
        _controlsHideTimer.Stop();
        _toolBarHideTimer.Stop();
        _videoLayoutTimer.Stop();
        _videoClickTimer.Stop();
        _startupTitleTimer.Stop();
        _resumeCheckpointTimer.Stop();
        _seekCommitTimer.Stop();
        _cursorHideTimer.Stop();
        _activeZOrderTimer.Stop();
        _playlistAutoScrollTimer.Stop();
        UninstallWindowMoveKeyboardHook();
        UnregisterWindowMoveHotkeys();
        ReleaseActiveTopmost();
        if (_discordOverlayShowHook != IntPtr.Zero)
        {
            UnhookWinEvent(_discordOverlayShowHook);
            _discordOverlayShowHook = IntPtr.Zero;
        }
        if (_discordOverlayCallbackHandle.IsAllocated)
            _discordOverlayCallbackHandle.Free();
        _discordOverlayEventCallback = null;
        InputManager.Current.PreProcessInput -= InputManager_OnPreProcessInput;

        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowProcedure);
            _windowSource = null;
        }

        if (_videoOverlaySource is not null)
        {
            _videoOverlaySource.RemoveHook(VideoOverlayWindowProcedure);
            _videoOverlaySource = null;
        }

        UpdateSystemPlaybackAwakeState(false);
        PersistSession();

        VideoView.HandleCreated -= VideoView_OnHandleCreated;
        _mediaPlayer.Stop();
        VideoView.Dispose();
        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();

        if (_videoOverlayWindow is not null)
        {
            var overlayWindow = _videoOverlayWindow;
            _videoOverlayWindow = null;
            _videoOverlayHandle = IntPtr.Zero;
            overlayWindow.Content = null;
            overlayWindow.Close();
        }

        if (_videoBackgroundBrush != IntPtr.Zero)
        {
            DeleteObject(_videoBackgroundBrush);
            _videoBackgroundBrush = IntPtr.Zero;
        }
    }

}
