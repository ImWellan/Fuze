using System.Globalization;
using System.Runtime.InteropServices;
using FusePlayer.Services;

namespace FusePlayer.Playback;

public enum PlaybackState
{
    NothingSpecial,
    Opening,
    Buffering,
    Playing,
    Paused,
    Stopped,
    Ended,
    Error
}

public enum MpvTrackType
{
    Video,
    Audio,
    Text,
    Unknown
}

public sealed record TrackDescription(int Id, string Name);

public sealed record ChapterDescription(long TimeOffset, string Name);

public sealed record AudioDeviceDescription(string Name, string Description);

public sealed record SubtitleRenderPreferences(
    string Encoding,
    string Font,
    int FontSize,
    string TextColor,
    string BorderColor,
    double BorderSize,
    bool Shadow,
    bool ForcePosition,
    string Position,
    int MarginX,
    int MarginY,
    bool ScaleWithWindow);

public sealed record TitleOverlayPreferences(
    string Position,
    string Font,
    int FontSize,
    string TextColor,
    string BorderColor,
    double BorderSize,
    bool Shadow,
    int MarginX,
    int MarginY,
    bool ScaleWithWindow);

public enum AudioOutputMode
{
    Automatic,
    Mono,
    Stereo,
    LeftChannel,
    RightChannel,
    ReversedStereo
}

[Flags]
public enum AudioTreatmentMode
{
    None = 0,
    Night = 1,
    DialogueBoost = 2,
    HeadphoneBinaural = 4,
    SurroundDownmix = 8
}

public sealed class MpvTrackData
{
    public MpvVideoTrackData Video { get; } = new();
    public MpvAudioTrackData Audio { get; } = new();
}

public sealed class MpvVideoTrackData
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint FrameRateNum { get; set; }
    public uint FrameRateDen { get; set; }
}

public sealed class MpvAudioTrackData
{
    public uint Channels { get; set; }
}

public sealed class MpvMediaTrack
{
    public int Id { get; init; }
    public MpvTrackType TrackType { get; init; }
    public string Codec { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string? Description { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public long Bitrate { get; init; }
    public MpvTrackData Data { get; init; } = new();
}

public sealed class MpvMedia(string location) : IDisposable
{
    public string Location { get; } = location;
    public MpvMediaTrack[] Tracks { get; internal set; } = [];

    public string CodecDescription(MpvTrackType trackType, string codec) => codec;

    public void Dispose()
    {
    }
}

public sealed class BufferingEventArgs(float cache) : EventArgs
{
    public float Cache { get; } = cache;
}

public sealed class MpvException(string message) : Exception(message);

/// <summary>
/// Thin managed owner for one libmpv client. The public surface is deliberately
/// small and tailored to Fuze; all decoding, timing and presentation remain in
/// libmpv.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private const ulong ObservePause = 1;
    private const ulong ObserveTracks = 2;
    private const ulong ObserveChapter = 3;
    private const ulong ObserveBuffering = 4;
    private const ulong ObserveIdle = 5;

    private readonly object _lifetimeLock = new();
    private IntPtr _handle;
    private IntPtr _windowHandle;
    private Thread? _eventThread;
    private bool _disposing;
    private bool _initialized;
    private bool _playPending;
    private bool _playPendingPaused;
    private string? _loadedLocation;
    private PlaybackState _state = PlaybackState.NothingSpecial;
    private int _volume = 100;
    private bool _mute;
    private float _rate = 1f;
    private string _aspectRatio = "16:9";
    private double _videoZoom;
    private double _videoPanX;
    private double _videoPanY;
    private string _videoOutput = "auto";
    private string _audioDevice = "auto";
    private AudioOutputMode _audioOutputMode = AudioOutputMode.Automatic;
    private AudioTreatmentMode _audioTreatmentMode = AudioTreatmentMode.None;
    private bool _audioPassthrough;
    private bool _audioExclusive;
    private bool _hardwareDecoding = true;
    private bool _deinterlacing;
    private string _hdrMode = "auto";
    private bool _bufferingEnabled = true;
    private bool _autoLoadExternalSubtitles;
    private bool _audioNormalization;
    private SubtitleRenderPreferences _subtitlePreferences = new(
        "auto", "Arial", 42, "#FFFFFFFF", "#FF000000", 2.5,
        true, false, "bottom-center", 20, 36, true);
    private long _videoSyncMilliseconds;
    private long _audioSyncMilliseconds;
    private long _subtitleSyncMilliseconds;
    private MpvMedia? _media;

    public event EventHandler? Opening;
    public event EventHandler<BufferingEventArgs>? Buffering;
    public event EventHandler? Playing;
    public event EventHandler? Paused;
    public event EventHandler? Stopped;
    public event EventHandler? EncounteredError;
    public event EventHandler? EndReached;
    public event EventHandler? ESAdded;
    public event EventHandler? ESDeleted;
    public event EventHandler? ESSelected;
    public event EventHandler? ChapterChanged;
    public event EventHandler? FileLoaded;
    public event EventHandler? VideoReconfigured;
    public event EventHandler? PlaybackRestarted;

    public MpvMedia? Media
    {
        get => _media;
        set
        {
            _media = value;
            if (value is null)
                _loadedLocation = null;
        }
    }

    public IntPtr Hwnd
    {
        get => _windowHandle;
        set
        {
            if (value != IntPtr.Zero)
                AttachWindow(value);
        }
    }

    public PlaybackState State => _state;
    public bool IsPlaying => _state is PlaybackState.Playing or PlaybackState.Buffering;

    public long Length => ToMilliseconds(GetDouble("duration"));
    public long Time => ToMilliseconds(GetDouble("time-pos"));
    public string? MetadataTitle
    {
        get
        {
            var title = GetProperty("metadata/by-key/title")?.Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;

            var count = Math.Max(0, GetInt32("metadata/list/count"));
            for (var index = 0; index < count; index++)
            {
                var key = GetProperty($"metadata/list/{index}/key");
                if (!string.Equals(key, "title", StringComparison.OrdinalIgnoreCase))
                    continue;

                title = GetProperty($"metadata/list/{index}/value")?.Trim();
                return string.IsNullOrWhiteSpace(title) ? null : title;
            }

            return null;
        }
    }

    public int Volume
    {
        get => (int)Math.Round(GetDouble("volume", _volume));
        set
        {
            _volume = Math.Clamp(value, 0, 200);
            SetProperty("volume", _volume.ToString(CultureInfo.InvariantCulture));
        }
    }

    public bool Mute
    {
        get => GetBoolean("mute", _mute);
        set
        {
            _mute = value;
            SetProperty("mute", value ? "yes" : "no");
        }
    }

    public string AspectRatio
    {
        get => _aspectRatio;
        set
        {
            SetVideoAspectRatio(value);
        }
    }

    // Kept for the existing presentation call site. libmpv owns the actual GPU
    // scale and uses the complete host surface.
    public float Scale { get; set; }

    public int ChapterCount => Math.Max(0, GetInt32("chapter-list/count"));
    public int Chapter => GetInt32("chapter", -1);

    public int AudioTrack => GetTrackId("aid");
    public int VideoTrack => GetTrackId("vid");
    public int Spu => GetTrackId("sid");
    public TrackDescription[] VideoTrackDescription => GetTrackDescriptions(MpvTrackType.Video, false);
    public TrackDescription[] AudioTrackDescription => GetTrackDescriptions(MpvTrackType.Audio, false);
    public TrackDescription[] SpuDescription => GetTrackDescriptions(MpvTrackType.Text, true);
    public int VideoTrackCount => GetTracks().Count(track => track.TrackType == MpvTrackType.Video);
    public int AudioTrackCount => GetTracks().Count(track => track.TrackType == MpvTrackType.Audio);
    public int SpuCount => GetTracks().Count(track => track.TrackType == MpvTrackType.Text) + 1;
    public string AudioDevice => GetProperty("audio-device") ?? _audioDevice;
    public AudioOutputMode AudioMode => _audioOutputMode;
    public AudioTreatmentMode AudioTreatment => _audioTreatmentMode;
    public bool AudioPassthrough => _audioPassthrough;
    public bool AudioExclusive => _audioExclusive;
    public string VideoOutput => _videoOutput;

    public bool ConfigureVideoOutput(string? output)
    {
        if (_initialized)
            return false;

        _videoOutput = NormalizeVideoOutput(output);
        return true;
    }

    public bool SetHardwareDecoding(bool enabled)
    {
        _hardwareDecoding = enabled;
        return !_initialized || SetProperty("hwdec", enabled ? "auto-safe" : "no");
    }

    public bool SetDeinterlacing(bool enabled)
    {
        _deinterlacing = enabled;
        return !_initialized || SetProperty("deinterlace", enabled ? "yes" : "no");
    }

    public bool SetHdrMode(string? mode)
    {
        _hdrMode = mode?.Trim().ToLowerInvariant() switch
        {
            "yes" or "on" or "enabled" => "yes",
            "no" or "off" or "disabled" => "no",
            _ => "auto"
        };

        if (!_initialized)
            return true;

        var hint = _hdrMode == "no" ? "no" : "yes";
        var toneMapping = _hdrMode == "no" ? "no" : "auto";
        return SetProperty("target-colorspace-hint", hint) &&
               SetProperty("tone-mapping", toneMapping);
    }

    public bool SetBufferingEnabled(bool enabled)
    {
        _bufferingEnabled = enabled;
        return !_initialized || SetProperty("cache", enabled ? "auto" : "no");
    }

    public bool SetExternalSubtitleAutoLoad(bool enabled)
    {
        _autoLoadExternalSubtitles = enabled;
        return !_initialized || SetProperty("sub-auto", enabled ? "fuzzy" : "no");
    }

    public bool SetAudioNormalization(bool enabled)
    {
        _audioNormalization = enabled;
        return !_initialized || ApplyAudioConfiguration();
    }

    public AudioDeviceDescription[] AudioDeviceDescriptions
    {
        get
        {
            var count = Math.Max(0, GetInt32("audio-device-list/count"));
            var devices = new List<AudioDeviceDescription>(count);
            for (var index = 0; index < count; index++)
            {
                var prefix = $"audio-device-list/{index}/";
                var name = GetProperty(prefix + "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var description = GetProperty(prefix + "description")?.Trim();
                devices.Add(new AudioDeviceDescription(name,
                    string.IsNullOrWhiteSpace(description) ? name : description));
            }

            return [.. devices];
        }
    }

    public void AttachWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || _disposing)
            return;

        lock (_lifetimeLock)
        {
            if (_initialized)
                return;

            _windowHandle = windowHandle;
            InitializeEngine();
        }
    }

    public bool Play() => PlayCore(startPaused: false);

    /// <summary>
    /// Charge le média sans laisser le son prendre de l'avance sur la première
    /// image. L'interface peut alors terminer son placement avant de relancer
    /// la lecture avec <see cref="SetPause"/>.
    /// </summary>
    public bool PlayPaused() => PlayCore(startPaused: true);

    public void RenderPausedFrame()
    {
        if (_initialized && !_disposing)
            Command("frame-step");
    }

    private bool PlayCore(bool startPaused)
    {
        if (_media is null || _disposing)
            return false;

        if (!_initialized)
        {
            _playPending = true;
            _playPendingPaused = startPaused;
            return true;
        }

        if (string.Equals(_loadedLocation, _media.Location, StringComparison.Ordinal))
        {
            SetPause(startPaused);
            return true;
        }

        _state = PlaybackState.Opening;
        Opening?.Invoke(this, EventArgs.Empty);
        // Régler la propriété avant loadfile sans annoncer Playing/Paused :
        // mpv n'a encore ni fichier chargé ni première image à ce moment-ci.
        SetProperty("pause", startPaused ? "yes" : "no");
        var result = Command("loadfile", _media.Location, "replace");
        if (result < 0)
        {
            _state = PlaybackState.Error;
            EncounteredError?.Invoke(this, EventArgs.Empty);
            return false;
        }

        _loadedLocation = _media.Location;
        ApplyRuntimePreferences();
        return true;
    }

    public void SetPause(bool paused)
    {
        if (!_initialized)
            return;

        SetProperty("pause", paused ? "yes" : "no");
        UpdatePauseState(paused);
    }

    public void Stop()
    {
        _playPending = false;
        _playPendingPaused = false;
        _loadedLocation = null;
        if (_initialized)
            Command("stop");
        SetState(PlaybackState.Stopped, Stopped);
    }

    public void SeekTo(TimeSpan position)
    {
        var seconds = Math.Max(0, position.TotalSeconds);
        Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute", "exact");
    }

    public bool SetRate(float rate)
    {
        _rate = Math.Clamp(rate, 0.01f, 100f);
        return SetProperty("speed", _rate.ToString(CultureInfo.InvariantCulture));
    }

    public bool SetAudioDevice(string name)
    {
        var requested = string.IsNullOrWhiteSpace(name) ? "auto" : name.Trim();
        var previous = _audioDevice;
        _audioDevice = requested;
        if (!_initialized)
            return true;
        if (SetProperty("audio-device", requested))
            return true;

        _audioDevice = previous;
        return false;
    }

    public bool SetVideoTrack(int id) =>
        SetProperty("vid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));

    public bool SetVideoAspectRatio(string value)
    {
        var requested = string.IsNullOrWhiteSpace(value) ? "no" : value.Trim();
        var previous = _aspectRatio;
        _aspectRatio = requested;
        if (!_initialized || SetProperty("video-aspect-override", requested))
            return true;

        _aspectRatio = previous;
        return false;
    }

    public bool SetVideoZoom(double value)
    {
        // libmpv exprime le zoom en paliers logarithmiques (2^zoom). La
        // limite précédente à 2 plafonnait donc toute valeur personnalisée
        // au-delà de 400 % : 500 % et 1 000 % finissaient avec le même zoom.
        // La valeur maximale de l'interface (1 000 %) correspond à log2(10).
        var requested = Math.Clamp(value, -2d, Math.Log(10d, 2d));
        var previous = _videoZoom;
        _videoZoom = requested;
        if (!_initialized || SetProperty("video-zoom", requested.ToString("0.######", CultureInfo.InvariantCulture)))
            return true;

        _videoZoom = previous;
        return false;
    }

    public bool SetVideoPan(double x, double y)
    {
        // Les valeurs de pan de libmpv sont exprimées en fractions de la
        // vidéo mise à l'échelle. Une plage généreuse permet de déplacer une
        // image fortement zoomée sans laisser entrer de valeurs aberrantes.
        var requestedX = Math.Clamp(x, -2d, 2d);
        var requestedY = Math.Clamp(y, -2d, 2d);
        var previousX = _videoPanX;
        var previousY = _videoPanY;
        _videoPanX = requestedX;
        _videoPanY = requestedY;
        if (!_initialized ||
            (SetProperty("video-pan-x", requestedX.ToString("0.######", CultureInfo.InvariantCulture)) &&
             SetProperty("video-pan-y", requestedY.ToString("0.######", CultureInfo.InvariantCulture))))
            return true;

        _videoPanX = previousX;
        _videoPanY = previousY;
        return false;
    }

    public bool SetAudioOutputMode(AudioOutputMode mode)
    {
        var previous = _audioOutputMode;
        _audioOutputMode = mode;
        if (!_initialized || ApplyAudioConfiguration())
            return true;

        _audioOutputMode = previous;
        ApplyAudioConfiguration();
        return false;
    }

    public bool SetAudioTreatmentMode(AudioTreatmentMode mode)
    {
        var previous = _audioTreatmentMode;
        _audioTreatmentMode = mode;
        if (!_initialized || ApplyAudioConfiguration())
            return true;

        _audioTreatmentMode = previous;
        ApplyAudioConfiguration();
        return false;
    }

    public bool SetAudioPassthrough(bool enabled)
    {
        var previous = _audioPassthrough;
        _audioPassthrough = enabled;
        if (!_initialized || ApplyAudioConfiguration())
            return true;

        _audioPassthrough = previous;
        ApplyAudioConfiguration();
        return false;
    }

    public bool SetAudioExclusive(bool enabled)
    {
        var previous = _audioExclusive;
        _audioExclusive = enabled;
        if (!_initialized || SetProperty("audio-exclusive", enabled ? "yes" : "no"))
            return true;

        _audioExclusive = previous;
        return false;
    }

    public bool SetTrackSynchronization(long videoMilliseconds, long audioMilliseconds,
        long subtitleMilliseconds)
    {
        var previous = (_videoSyncMilliseconds, _audioSyncMilliseconds, _subtitleSyncMilliseconds);
        _videoSyncMilliseconds = Math.Clamp(videoMilliseconds, -30000, 30000);
        _audioSyncMilliseconds = Math.Clamp(audioMilliseconds, -30000, 30000);
        _subtitleSyncMilliseconds = Math.Clamp(subtitleMilliseconds, -30000, 30000);
        if (!_initialized || ApplyTrackSynchronization())
            return true;

        (_videoSyncMilliseconds, _audioSyncMilliseconds, _subtitleSyncMilliseconds) = previous;
        ApplyTrackSynchronization();
        return false;
    }

    public bool SetAudioTrack(int id) => SetProperty("aid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));

    public bool SetSpu(int id) => SetProperty("sid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));

    public bool AddSubtitle(string path, bool select) =>
        Command("sub-add", path, select ? "select" : "auto") >= 0;

    public bool SetSubtitlePreferences(SubtitleRenderPreferences preferences)
    {
        _subtitlePreferences = preferences with
        {
            Encoding = string.IsNullOrWhiteSpace(preferences.Encoding) ? "auto" : preferences.Encoding.Trim(),
            Font = string.IsNullOrWhiteSpace(preferences.Font) ? "Arial" : preferences.Font.Trim(),
            FontSize = Math.Clamp(preferences.FontSize, 12, 120),
            TextColor = NormalizeMpvColor(preferences.TextColor, "#FFFFFFFF"),
            BorderColor = NormalizeMpvColor(preferences.BorderColor, "#FF000000"),
            BorderSize = Math.Clamp(preferences.BorderSize, 0, 10),
            Position = NormalizeScreenPosition(preferences.Position),
            MarginX = Math.Clamp(preferences.MarginX, 0, 500),
            MarginY = Math.Clamp(preferences.MarginY, 0, 500)
        };
        return !_initialized || ApplySubtitlePreferences();
    }

    public bool ShowText(string text, int durationMilliseconds, TitleOverlayPreferences preferences)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(text))
            return false;

        var (alignX, alignY) = GetMpvAlignment(preferences.Position);
        var applied = SetProperty("osd-align-x", alignX) &&
                      SetProperty("osd-align-y", alignY) &&
                      SetProperty("osd-font", string.IsNullOrWhiteSpace(preferences.Font)
                          ? "Arial"
                          : preferences.Font.Trim()) &&
                      SetProperty("osd-font-size", Math.Clamp(preferences.FontSize, 12, 120)
                          .ToString(CultureInfo.InvariantCulture)) &&
                      SetProperty("osd-scale-by-window", preferences.ScaleWithWindow ? "yes" : "no") &&
                      SetProperty("osd-color", NormalizeMpvColor(preferences.TextColor, "#FFFFFFFF")) &&
                      SetProperty("osd-border-color", NormalizeMpvColor(preferences.BorderColor, "#FF000000")) &&
                      SetProperty("osd-border-size", Math.Clamp(preferences.BorderSize, 0, 10)
                          .ToString("0.##", CultureInfo.InvariantCulture)) &&
                      SetProperty("osd-shadow-offset", preferences.Shadow ? "2" : "0") &&
                      SetProperty("osd-margin-x", Math.Clamp(preferences.MarginX, 0, 500)
                          .ToString(CultureInfo.InvariantCulture)) &&
                      SetProperty("osd-margin-y", Math.Clamp(preferences.MarginY, 0, 500)
                          .ToString(CultureInfo.InvariantCulture));
        return applied && Command("show-text", text, Math.Clamp(durationMilliseconds, 250, 30000)
            .ToString(CultureInfo.InvariantCulture), "0") >= 0;
    }

    public bool TakeSnapshot(string path) => Command("screenshot-to-file", path, "video") >= 0;

    public void NextChapter() => Command("add", "chapter", "1");
    public void PreviousChapter() => Command("add", "chapter", "-1");

    public ChapterDescription[] FullChapterDescriptions(int title)
    {
        var count = ChapterCount;
        var chapters = new List<ChapterDescription>(count);
        for (var index = 0; index < count; index++)
        {
            var seconds = GetDouble($"chapter-list/{index}/time");
            var name = GetProperty($"chapter-list/{index}/title");
            chapters.Add(new ChapterDescription(
                ToMilliseconds(seconds),
                string.IsNullOrWhiteSpace(name)
                    ? LocalizationService.Format("Chapitre {0}", index + 1)
                    : name));
        }

        return [.. chapters];
    }

    private void InitializeEngine()
    {
        _handle = MpvNative.mpv_create();
        if (_handle == IntPtr.Zero)
            throw new MpvException("libmpv n'a pas pu créer le moteur de lecture.");

        SetOption("config", "no", required: true);
        SetOption("terminal", "no", required: true);
        SetOption("input-default-bindings", "no", required: true);
        SetOption("input-vo-keyboard", "no", required: true);
        SetOption("osc", "no", required: true);
        SetOption("osd-level", "0", required: true);
        SetOption("idle", "yes", required: true);
        SetOption("wid", _windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture), required: true);

        // Ne pas créer une sortie vidéo vide au démarrage. Le comportement
        // mpv normal attend qu'une vidéo soit réellement chargée, ce qui évite
        // d'exposer un HWND noir avant la première image.
        SetOption("force-window", "no");
        SetOption("video-latency-hacks", "no");

        ConfigureVideoOutputOptions();
        SetOption("profile", "high-quality");
        SetOption("hwdec", _hardwareDecoding ? "auto-safe" : "no");
        SetOption("deinterlace", _deinterlacing ? "yes" : "no");
        SetOption("cache", _bufferingEnabled ? "auto" : "no");
        SetOption("target-colorspace-hint", "yes");
        SetOption("tone-mapping", _hdrMode == "no" ? "no" : "auto");
        SetOption("gamut-mapping-mode", "auto");
        SetOption("hdr-compute-peak", "auto");
        SetOption("dither-depth", "auto");
        SetOption("video-aspect-override", _aspectRatio);
        SetOption("keepaspect", "yes");
        SetOption("panscan", "1.0");
        SetOption("volume-max", "200");
        SetOption("sub-auto", _autoLoadExternalSubtitles ? "fuzzy" : "no");
        SetOption("audio-display", "no");

        var result = MpvNative.mpv_initialize(_handle);
        if (result < 0)
        {
            var message = ErrorMessage(result);
            MpvNative.mpv_terminate_destroy(_handle);
            _handle = IntPtr.Zero;
            throw new MpvException($"libmpv n'a pas pu démarrer : {message}");
        }

        _initialized = true;
        Observe(ObservePause, "pause");
        Observe(ObserveTracks, "track-list");
        Observe(ObserveChapter, "chapter");
        Observe(ObserveBuffering, "cache-buffering-state");
        Observe(ObserveIdle, "idle-active");

        ApplyRuntimePreferences();
        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "Fuze libmpv events"
        };
        _eventThread.Start();

        if (_playPending)
        {
            var startPaused = _playPendingPaused;
            _playPending = false;
            _playPendingPaused = false;
            PlayCore(startPaused);
        }
    }

    private void ConfigureVideoOutputOptions()
    {
        switch (_videoOutput)
        {
            case "d3d11":
                SetOption("vo", "gpu-next", required: true);
                SetOption("gpu-api", "d3d11");
                SetOption("gpu-context", "d3d11");
                break;
            case "d3d9":
                SetOption("vo", "gpu", required: true);
                SetOption("gpu-api", "opengl");
                SetOption("gpu-context", "angle");
                SetOption("angle-renderer", "d3d9");
                break;
            case "opengl":
                SetOption("vo", "gpu-next", required: true);
                SetOption("gpu-api", "opengl");
                SetOption("gpu-context", "win");
                break;
            case "software":
                SetOption("vo", "gpu", required: true);
                SetOption("gpu-api", "d3d11");
                SetOption("gpu-context", "d3d11");
                SetOption("d3d11-warp", "yes");
                break;
            default:
                SetOption("vo", "gpu-next", required: true);
                break;
        }
    }

    private static string NormalizeVideoOutput(string? output) => output?.Trim().ToLowerInvariant() switch
    {
        "d3d11" => "d3d11",
        "d3d9" => "d3d9",
        "opengl" => "opengl",
        "software" => "software",
        _ => "auto"
    };

    private void ApplyRuntimePreferences()
    {
        if (!_initialized)
            return;

        SetProperty("volume", _volume.ToString(CultureInfo.InvariantCulture));
        SetProperty("mute", _mute ? "yes" : "no");
        SetProperty("speed", _rate.ToString(CultureInfo.InvariantCulture));
        SetProperty("video-aspect-override", _aspectRatio);
        SetProperty("panscan", "1.0");
        SetProperty("video-zoom", _videoZoom.ToString("0.######", CultureInfo.InvariantCulture));
        SetProperty("video-pan-x", _videoPanX.ToString("0.######", CultureInfo.InvariantCulture));
        SetProperty("video-pan-y", _videoPanY.ToString("0.######", CultureInfo.InvariantCulture));
        SetProperty("hwdec", _hardwareDecoding ? "auto-safe" : "no");
        SetProperty("deinterlace", _deinterlacing ? "yes" : "no");
        SetProperty("cache", _bufferingEnabled ? "auto" : "no");
        SetProperty("sub-auto", _autoLoadExternalSubtitles ? "fuzzy" : "no");
        SetProperty("target-colorspace-hint", _hdrMode == "no" ? "no" : "yes");
        SetProperty("tone-mapping", _hdrMode == "no" ? "no" : "auto");
        SetProperty("audio-exclusive", _audioExclusive ? "yes" : "no");
        if (!SetProperty("audio-device", _audioDevice))
        {
            _audioDevice = "auto";
            SetProperty("audio-device", _audioDevice);
        }
        ApplyAudioConfiguration();
        ApplySubtitlePreferences();
        ApplyTrackSynchronization();
    }

    private bool ApplySubtitlePreferences()
    {
        if (!_initialized)
            return true;

        var preferences = _subtitlePreferences;
        var (alignX, alignY) = preferences.ForcePosition
            ? GetMpvAlignment(preferences.Position)
            : ("center", "bottom");
        var marginX = preferences.ForcePosition ? preferences.MarginX : 20;
        var marginY = preferences.ForcePosition ? preferences.MarginY : 22;
        return SetProperty("sub-codepage", preferences.Encoding) &&
               SetProperty("sub-font", preferences.Font) &&
               SetProperty("sub-font-size", preferences.FontSize.ToString(CultureInfo.InvariantCulture)) &&
               SetProperty("sub-scale-by-window", preferences.ScaleWithWindow ? "yes" : "no") &&
               SetProperty("sub-color", preferences.TextColor) &&
               SetProperty("sub-border-color", preferences.BorderColor) &&
               SetProperty("sub-border-size", preferences.BorderSize.ToString("0.##", CultureInfo.InvariantCulture)) &&
               SetProperty("sub-shadow-offset", preferences.Shadow ? "2" : "0") &&
               SetProperty("sub-align-x", alignX) &&
               SetProperty("sub-align-y", alignY) &&
               SetProperty("sub-margin-x", marginX.ToString(CultureInfo.InvariantCulture)) &&
               SetProperty("sub-margin-y", marginY.ToString(CultureInfo.InvariantCulture));
    }

    private static (string X, string Y) GetMpvAlignment(string? position)
    {
        var normalized = NormalizeScreenPosition(position);
        var parts = normalized.Split('-', 2);
        return parts.Length == 2
            ? (parts[1], parts[0])
            : ("center", "bottom");
    }

    private static string NormalizeScreenPosition(string? position) => position?.Trim().ToLowerInvariant() switch
    {
        "top-left" => "top-left",
        "top-center" => "top-center",
        "top-right" => "top-right",
        "center-left" => "center-left",
        "center" or "center-center" => "center-center",
        "center-right" => "center-right",
        "bottom-left" => "bottom-left",
        "bottom-right" => "bottom-right",
        _ => "bottom-center"
    };

    private static string NormalizeMpvColor(string? color, string fallback)
    {
        var value = color?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (value.Length == 7 && value[0] == '#')
            return "#FF" + value[1..].ToUpperInvariant();
        if (value.Length == 9 && value[0] == '#')
            return value.ToUpperInvariant();
        return fallback;
    }

    private bool ApplyTrackSynchronization()
    {
        if (!_initialized)
            return true;

        var audioDelay = (_audioSyncMilliseconds - _videoSyncMilliseconds) / 1000d;
        var subtitleDelay = (_subtitleSyncMilliseconds - _videoSyncMilliseconds) / 1000d;
        var audioApplied = SetProperty("audio-delay", audioDelay.ToString("0.###", CultureInfo.InvariantCulture));
        var subtitleApplied = SetProperty("sub-delay", subtitleDelay.ToString("0.###", CultureInfo.InvariantCulture));
        return audioApplied && subtitleApplied;
    }

    private bool ApplyAudioConfiguration()
    {
        if (!_initialized)
            return true;

        var passthroughCodecs = _audioPassthrough
            ? "ac3,eac3,dts,dts-hd,truehd"
            : string.Empty;
        var passthroughApplied = SetProperty("audio-spdif", passthroughCodecs);
        if (_audioPassthrough)
        {
            var rawChannelsApplied = SetProperty("audio-channels", "auto-safe");
            var rawFiltersApplied = SetProperty("af", string.Empty);
            return passthroughApplied && rawChannelsApplied && rawFiltersApplied;
        }

        var (channels, filter) = _audioOutputMode switch
        {
            AudioOutputMode.Mono => ("mono", string.Empty),
            AudioOutputMode.Stereo => ("stereo", string.Empty),
            AudioOutputMode.LeftChannel =>
                ("stereo", "lavfi=[pan=stereo|c0=c0|c1=c0]"),
            AudioOutputMode.RightChannel =>
                ("stereo", "lavfi=[pan=stereo|c0=c1|c1=c1]"),
            AudioOutputMode.ReversedStereo =>
                ("stereo", "lavfi=[pan=stereo|c0=c1|c1=c0]"),
            _ => ("auto-safe", string.Empty)
        };

        if (_audioOutputMode == AudioOutputMode.Automatic &&
            (_audioTreatmentMode & (AudioTreatmentMode.HeadphoneBinaural | AudioTreatmentMode.SurroundDownmix)) != 0)
            channels = "stereo";

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter))
            filters.Add(filter);
        if (_audioTreatmentMode.HasFlag(AudioTreatmentMode.Night))
            filters.Add("lavfi=[acompressor=threshold=0.125:ratio=4:attack=20:release=250:makeup=2]");
        if (_audioTreatmentMode.HasFlag(AudioTreatmentMode.DialogueBoost))
            filters.Add("lavfi=[equalizer=f=1700:t=q:w=1.1:g=3,equalizer=f=3200:t=q:w=1.2:g=2]");
        if ((_audioTreatmentMode & (AudioTreatmentMode.Night | AudioTreatmentMode.DialogueBoost)) != 0)
            filters.Add("lavfi=[alimiter=limit=0.95:attack=5:release=50]");
        if (_audioTreatmentMode.HasFlag(AudioTreatmentMode.HeadphoneBinaural))
            filters.Add("lavfi=[bs2b=profile=jmeier]");
        if (_audioNormalization)
            filters.Add("lavfi=[loudnorm=I=-16:TP=-1.5:LRA=11]");

        var channelsApplied = SetProperty("audio-channels", channels);
        var filtersApplied = SetProperty("af", string.Join(',', filters));
        return passthroughApplied && channelsApplied && filtersApplied;
    }

    private void SetOption(string name, string value, bool required = false)
    {
        var result = MpvNative.mpv_set_option_string(_handle, name, value);
        if (required && result < 0)
            throw new MpvException($"Option libmpv refusée ({name}) : {ErrorMessage(result)}");
    }

    private void Observe(ulong id, string property) =>
        MpvNative.mpv_observe_property(_handle, id, property, MpvNative.Format.None);

    private void EventLoop()
    {
        while (!_disposing && _handle != IntPtr.Zero)
        {
            var eventPointer = MpvNative.mpv_wait_event(_handle, 0.1);
            if (eventPointer == IntPtr.Zero)
                continue;

            var playerEvent = Marshal.PtrToStructure<MpvNative.Event>(eventPointer);
            if (playerEvent.EventId == MpvNative.EventId.None)
                continue;

            try
            {
                HandleEvent(playerEvent);
            }
            catch when (_disposing)
            {
                return;
            }
        }
    }

    private void HandleEvent(MpvNative.Event playerEvent)
    {
        switch (playerEvent.EventId)
        {
            case MpvNative.EventId.StartFile:
                _state = PlaybackState.Opening;
                break;
            case MpvNative.EventId.FileLoaded:
                RefreshMediaTracks();
                ESAdded?.Invoke(this, EventArgs.Empty);
                if (GetBoolean("pause", false))
                    UpdatePauseState(paused: true, force: true);
                FileLoaded?.Invoke(this, EventArgs.Empty);
                break;
            case MpvNative.EventId.EndFile:
                HandleEndFile(playerEvent.Data);
                break;
            case MpvNative.EventId.PropertyChange:
                HandlePropertyChange(playerEvent.ReplyUserdata);
                break;
            case MpvNative.EventId.VideoReconfig:
                VideoReconfigured?.Invoke(this, EventArgs.Empty);
                break;
            case MpvNative.EventId.PlaybackRestart:
                // mpv émet cet événement après avoir réinitialisé la lecture
                // (au démarrage et après une recherche). C'est le signal le
                // plus fiable pour synchroniser l'exposition de la surface
                // avec la première image, contrairement à VIDEO_RECONFIG qui
                // signifie seulement que la géométrie a changé.
                UpdatePauseState(GetBoolean("pause", false), force: true);
                PlaybackRestarted?.Invoke(this, EventArgs.Empty);
                break;
            case MpvNative.EventId.Shutdown:
                _disposing = true;
                break;
        }
    }

    private void HandleEndFile(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return;

        var end = Marshal.PtrToStructure<MpvNative.EndFileEvent>(data);
        switch (end.Reason)
        {
            case MpvNative.EndFileReason.Eof:
                SetState(PlaybackState.Ended, EndReached);
                break;
            case MpvNative.EndFileReason.Error:
                SetState(PlaybackState.Error, EncounteredError);
                break;
            case MpvNative.EndFileReason.Stop:
                SetState(PlaybackState.Stopped, Stopped);
                break;
        }
    }

    private void HandlePropertyChange(ulong observedProperty)
    {
        switch (observedProperty)
        {
            case ObservePause:
                UpdatePauseState(GetBoolean("pause", false));
                break;
            case ObserveTracks:
                RefreshMediaTracks();
                ESDeleted?.Invoke(this, EventArgs.Empty);
                ESAdded?.Invoke(this, EventArgs.Empty);
                ESSelected?.Invoke(this, EventArgs.Empty);
                break;
            case ObserveChapter:
                ChapterChanged?.Invoke(this, EventArgs.Empty);
                break;
            case ObserveBuffering:
                var cache = Math.Clamp(GetDouble("cache-buffering-state", 100), 0, 100);
                if (cache < 100)
                {
                    _state = PlaybackState.Buffering;
                    Buffering?.Invoke(this, new BufferingEventArgs((float)cache));
                }
                else if (!GetBoolean("pause", false) && !GetBoolean("idle-active", true))
                    SetState(PlaybackState.Playing, Playing);
                break;
            case ObserveIdle:
                if (GetBoolean("idle-active", false) && _state is not PlaybackState.Ended and not PlaybackState.Error)
                    _state = PlaybackState.Stopped;
                break;
        }
    }

    private void UpdatePauseState(bool paused, bool force = false)
    {
        if (GetBoolean("idle-active", false))
            return;

        // Une modification de la propriété pause envoyée avant loadfile peut
        // être observée pendant Opening. Ce n'est pas encore une lecture et
        // ne doit pas déclencher l'interface Playing avant la première image.
        if (!force && _state == PlaybackState.Opening)
            return;

        var next = paused ? PlaybackState.Paused : PlaybackState.Playing;
        if (!force && _state == next)
            return;

        SetState(next, paused ? Paused : Playing);
    }

    private void SetState(PlaybackState state, EventHandler? handler)
    {
        if (_state == state && state is not PlaybackState.Ended and not PlaybackState.Error)
            return;

        _state = state;
        handler?.Invoke(this, EventArgs.Empty);
    }

    private TrackDescription[] GetTrackDescriptions(MpvTrackType type, bool includeDisabled)
    {
        var descriptions = GetTracks()
            .Where(track => track.TrackType == type)
            .Select(track => new TrackDescription(track.Id, BuildTrackName(track)))
            .ToList();
        if (includeDisabled)
            descriptions.Insert(0, new TrackDescription(-1,
                LocalizationService.Get("Désactivés")));
        return [.. descriptions];
    }

    private static string BuildTrackName(MpvMediaTrack track)
    {
        var title = track.Description?.Trim();
        var language = track.Language?.Trim();
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(language))
            return $"{title} · {language}";
        if (!string.IsNullOrWhiteSpace(title))
            return title;
        if (!string.IsNullOrWhiteSpace(language))
            return language;
        return LocalizationService.Format("Piste {0}", track.Id);
    }

    private MpvMediaTrack[] GetTracks()
    {
        if (!_initialized)
            return [];

        var count = Math.Max(0, GetInt32("track-list/count"));
        var tracks = new List<MpvMediaTrack>(count);
        for (var index = 0; index < count; index++)
        {
            var prefix = $"track-list/{index}/";
            var type = GetProperty(prefix + "type") switch
            {
                "video" => MpvTrackType.Video,
                "audio" => MpvTrackType.Audio,
                "sub" => MpvTrackType.Text,
                _ => MpvTrackType.Unknown
            };
            var framesPerSecond = GetDouble(prefix + "demux-fps");
            tracks.Add(new MpvMediaTrack
            {
                Id = GetInt32(prefix + "id", index + 1),
                TrackType = type,
                Codec = GetProperty(prefix + "codec-desc") ?? GetProperty(prefix + "codec") ?? string.Empty,
                Language = GetProperty(prefix + "lang"),
                Description = GetProperty(prefix + "title"),
                IsDefault = GetBoolean(prefix + "default", false),
                IsForced = GetBoolean(prefix + "forced", false),
                Bitrate = Math.Max(0, (long)Math.Round(GetDouble(prefix + "demux-bitrate"))),
                Data = new MpvTrackData
                {
                    Video =
                    {
                        Width = ToUInt32(GetInt32(prefix + "demux-w")),
                        Height = ToUInt32(GetInt32(prefix + "demux-h")),
                        FrameRateNum = framesPerSecond > 0 ? (uint)Math.Round(framesPerSecond * 1000) : 0,
                        FrameRateDen = framesPerSecond > 0 ? 1000u : 0
                    },
                    Audio =
                    {
                        Channels = ToUInt32(GetInt32(prefix + "demux-channel-count"))
                    }
                }
            });
        }

        return [.. tracks];
    }

    private void RefreshMediaTracks()
    {
        if (_media is not null)
            _media.Tracks = GetTracks();
    }

    private int GetTrackId(string property)
    {
        var value = GetProperty(property);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1;
    }

    private int GetInt32(string property, int fallback = 0)
    {
        var value = GetProperty(property);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;
    }

    private double GetDouble(string property, double fallback = 0)
    {
        var value = GetProperty(property);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
               double.IsFinite(number)
            ? number
            : fallback;
    }

    private bool GetBoolean(string property, bool fallback)
    {
        var value = GetProperty(property);
        return value switch
        {
            "yes" or "true" => true,
            "no" or "false" => false,
            _ => fallback
        };
    }

    private string? GetProperty(string name)
    {
        if (!_initialized || _handle == IntPtr.Zero || _disposing)
            return null;

        var value = MpvNative.mpv_get_property_string(_handle, name);
        if (value == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            MpvNative.mpv_free(value);
        }
    }

    private bool SetProperty(string name, string value)
    {
        if (!_initialized || _handle == IntPtr.Zero || _disposing)
            return false;
        return MpvNative.mpv_set_property_string(_handle, name, value) >= 0;
    }

    private int Command(params string[] arguments)
    {
        if (!_initialized || _handle == IntPtr.Zero || _disposing || arguments.Length == 0)
            return -1;

        var strings = new IntPtr[arguments.Length];
        var array = IntPtr.Zero;
        try
        {
            for (var index = 0; index < arguments.Length; index++)
                strings[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);

            array = Marshal.AllocHGlobal(IntPtr.Size * (strings.Length + 1));
            for (var index = 0; index < strings.Length; index++)
                Marshal.WriteIntPtr(array, index * IntPtr.Size, strings[index]);
            Marshal.WriteIntPtr(array, strings.Length * IntPtr.Size, IntPtr.Zero);
            return MpvNative.mpv_command(_handle, array);
        }
        finally
        {
            if (array != IntPtr.Zero)
                Marshal.FreeHGlobal(array);
            foreach (var value in strings)
            {
                if (value != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(value);
            }
        }
    }

    private static string ErrorMessage(int error)
    {
        var pointer = MpvNative.mpv_error_string(error);
        return pointer == IntPtr.Zero ? $"erreur {error}" : Marshal.PtrToStringUTF8(pointer) ?? $"erreur {error}";
    }

    private static long ToMilliseconds(double seconds) =>
        !double.IsFinite(seconds) || seconds <= 0 ? 0 : (long)Math.Round(seconds * 1000d);

    private static uint ToUInt32(int value) => value <= 0 ? 0u : (uint)value;

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposing)
                return;
            _disposing = true;
        }

        if (_handle != IntPtr.Zero)
            MpvNative.mpv_wakeup(_handle);
        if (_eventThread is not null && _eventThread != Thread.CurrentThread)
            _eventThread.Join(TimeSpan.FromSeconds(2));

        lock (_lifetimeLock)
        {
            if (_handle != IntPtr.Zero)
            {
                MpvNative.mpv_terminate_destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _initialized = false;
        }
    }
}
