using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer;

public readonly record struct TrackSynchronization(
    long VideoMilliseconds,
    long AudioMilliseconds,
    long SubtitleMilliseconds);

public enum TrackSyncTarget
{
    All,
    Video,
    Audio,
    Subtitle
}

public partial class TrackSyncDialog : Window
{
    private readonly Func<TrackSynchronization, bool> _apply;
    private readonly bool _hasVideo;
    private readonly bool _hasAudio;
    private readonly bool _hasSubtitles;
    private readonly string _videoTrack;
    private readonly string _audioTrack;
    private readonly string _subtitleTrack;
    private TrackSynchronization _synchronization;
    private bool _updatingControls;

    public TrackSyncDialog(TrackSynchronization synchronization,
        string videoTrack, string audioTrack, string subtitleTrack,
        bool hasVideo, bool hasAudio, bool hasSubtitles,
        TrackSyncTarget target,
        Func<TrackSynchronization, bool> apply)
    {
        InitializeComponent();
        LocalizationService.ApplyToWindow(this);
        _apply = apply;
        _hasVideo = hasVideo;
        _hasAudio = hasAudio;
        _hasSubtitles = hasSubtitles;
        _videoTrack = videoTrack;
        _audioTrack = audioTrack;
        _subtitleTrack = subtitleTrack;
        Target = target;

        ConfigureTrack(VideoSection, VideoTrackText, videoTrack, hasVideo);
        ConfigureTrack(AudioSection, AudioTrackText, audioTrack, hasAudio);
        ConfigureTrack(SubtitleSection, SubtitleTrackText, subtitleTrack, hasSubtitles);

        foreach (var textBox in new[] { VideoValueTextBox, AudioValueTextBox, SubtitleValueTextBox })
            DataObject.AddPastingHandler(textBox, ValueTextBox_OnPaste);

        ConfigureTarget();
        SetSynchronization(synchronization);
        Loaded += (_, _) => FocusTargetEditor();
    }

    public void RefreshLocalizedContent()
    {
        LocalizationService.ApplyToWindow(this);
        ConfigureTrack(VideoSection, VideoTrackText, _videoTrack, _hasVideo);
        ConfigureTrack(AudioSection, AudioTrackText, _audioTrack, _hasAudio);
        ConfigureTrack(SubtitleSection, SubtitleTrackText, _subtitleTrack, _hasSubtitles);
        ConfigureTarget();
        RenderSynchronization();
    }

    public TrackSyncTarget Target { get; }

    public void SetSynchronization(TrackSynchronization synchronization)
    {
        _synchronization = Clamp(synchronization);
        RenderSynchronization();
    }

    private static void ConfigureTrack(Border section, TextBlock label, string track, bool available)
    {
        label.Text = available ? track : LocalizationService.Get("Aucune piste disponible");
        label.ToolTip = label.Text;
        section.IsEnabled = available;
        section.Opacity = available ? 1 : 0.46;
    }

    private void ConfigureTarget()
    {
        if (Target == TrackSyncTarget.All)
            return;

        VideoRow.Height = Target == TrackSyncTarget.Video ? new GridLength(116) : new GridLength(0);
        AudioRow.Height = Target == TrackSyncTarget.Audio ? new GridLength(116) : new GridLength(0);
        SubtitleRow.Height = Target == TrackSyncTarget.Subtitle ? new GridLength(116) : new GridLength(0);
        VideoSection.Visibility = Target == TrackSyncTarget.Video ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = Target == TrackSyncTarget.Audio ? Visibility.Visible : Visibility.Collapsed;
        SubtitleSection.Visibility = Target == TrackSyncTarget.Subtitle ? Visibility.Visible : Visibility.Collapsed;
        ResetAllButton.Visibility = Visibility.Collapsed;
        Height = 338;

        var label = Target switch
        {
            TrackSyncTarget.Video => LocalizationService.Get("Vidéo").ToLowerInvariant(),
            TrackSyncTarget.Audio => LocalizationService.Get("Audio").ToLowerInvariant(),
            _ => LocalizationService.Get("des sous-titres")
        };
        DialogTitleText.Text = LocalizationService.Format("Synchronisation {0}", label);
        Title = DialogTitleText.Text;
        IntroText.Text = LocalizationService.Format(
            "Ajustement {0} en temps réel pendant la lecture", label);
    }

    private void FocusTargetEditor()
    {
        var editor = Target switch
        {
            TrackSyncTarget.Video => VideoValueTextBox,
            TrackSyncTarget.Audio => AudioValueTextBox,
            TrackSyncTarget.Subtitle => SubtitleValueTextBox,
            _ => null
        };
        if (editor is null || !editor.IsEnabled)
            return;

        editor.Focus();
        editor.SelectAll();
    }

    private void OffsetSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || sender is not Slider { Tag: string kind })
            return;

        ApplyOffset(kind, (long)Math.Round(e.NewValue));
    }

    private void StepButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        var parts = tag.Split(':', 2);
        if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var delta))
            return;

        ApplyOffset(parts[0], GetOffset(parts[0]) + delta);
    }

    private void ResetRowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string kind })
            ApplyOffset(kind, 0);
    }

    private void ResetAll_OnClick(object sender, RoutedEventArgs e) =>
        TryApply(new TrackSynchronization(0, 0, 0));

    private void ApplyOffset(string kind, long value)
    {
        value = Math.Clamp(value, -30000, 30000);
        var candidate = kind switch
        {
            "Video" => _synchronization with { VideoMilliseconds = value },
            "Audio" => _synchronization with { AudioMilliseconds = value },
            "Subtitle" => _synchronization with { SubtitleMilliseconds = value },
            _ => _synchronization
        };
        TryApply(candidate);
    }

    private void TryApply(TrackSynchronization candidate)
    {
        candidate = Clamp(candidate);
        if (_apply(candidate))
        {
            _synchronization = candidate;
            RenderSynchronization();
        }
        else
        {
            RenderSynchronization();
            EffectiveDelayText.Text = LocalizationService.Get("Impossible d’appliquer ce décalage");
            EffectiveDelayText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 107, 115));
        }
    }

    private void RenderSynchronization()
    {
        _updatingControls = true;
        VideoSlider.Value = _synchronization.VideoMilliseconds;
        AudioSlider.Value = _synchronization.AudioMilliseconds;
        SubtitleSlider.Value = _synchronization.SubtitleMilliseconds;
        VideoValueTextBox.Text = _synchronization.VideoMilliseconds.ToString(CultureInfo.InvariantCulture);
        AudioValueTextBox.Text = _synchronization.AudioMilliseconds.ToString(CultureInfo.InvariantCulture);
        SubtitleValueTextBox.Text = _synchronization.SubtitleMilliseconds.ToString(CultureInfo.InvariantCulture);
        _updatingControls = false;

        var audioRelative = _synchronization.AudioMilliseconds - _synchronization.VideoMilliseconds;
        var subtitleRelative = _synchronization.SubtitleMilliseconds - _synchronization.VideoMilliseconds;
        var audioText = _hasAudio ? FormatDelay(audioRelative) : LocalizationService.Get("indisponible");
        var subtitleText = _hasSubtitles ? FormatDelay(subtitleRelative) : LocalizationService.Get("indisponible");
        EffectiveDelayText.Text = Target switch
        {
            TrackSyncTarget.Audio => LocalizationService.Format(
                "Audio par rapport à la vidéo  •  {0}", audioText),
            TrackSyncTarget.Subtitle => LocalizationService.Format(
                "Sous-titres par rapport à la vidéo  •  {0}", subtitleText),
            _ => LocalizationService.Format(
                "Par rapport à la vidéo  •  Audio {0}  •  Sous-titres {1}",
                audioText, subtitleText)
        };
        EffectiveDelayText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(196, 201, 208));
    }

    private long GetOffset(string kind) => kind switch
    {
        "Video" => _synchronization.VideoMilliseconds,
        "Audio" => _synchronization.AudioMilliseconds,
        "Subtitle" => _synchronization.SubtitleMilliseconds,
        _ => 0
    };

    private void ValueTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitTextBox(sender as TextBox);

    private void ValueTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitTextBox(sender as TextBox);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitTextBox(TextBox? textBox)
    {
        if (_updatingControls || textBox?.Tag is not string kind)
            return;

        if (long.TryParse(textBox.Text.Trim(), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var value))
            ApplyOffset(kind, value);
        else
            RenderSynchronization();
    }

    private void ValueTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character) && character is not '-' and not '+');
    }

    private static void ValueTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text ||
            !long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out _))
            e.CancelCommand();
    }

    private static TrackSynchronization Clamp(TrackSynchronization value) => new(
        Math.Clamp(value.VideoMilliseconds, -30000, 30000),
        Math.Clamp(value.AudioMilliseconds, -30000, 30000),
        Math.Clamp(value.SubtitleMilliseconds, -30000, 30000));

    private static string FormatDelay(long milliseconds) =>
        $"{milliseconds / 1000d:+0.000;-0.000;0.000} s";

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Close();
        e.Handled = true;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        DragMove();
        e.Handled = true;
    }
}
