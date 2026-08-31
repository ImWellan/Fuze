using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FusePlayer.Models;

public sealed class PlaylistItem : INotifyPropertyChanged
{
    private long _durationMilliseconds;
    private bool _isEnhancedFolderStart;
    private string _enhancedFolderTitle = string.Empty;
    private bool _isEnhancedQueued;
    private bool _isCurrent;
    private bool _isManualQueueItem;
    private int _displayFolderDepth = 2;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Location { get; init; }
    public required string Title { get; init; }
    public bool IsNetwork { get; init; }

    public bool IsEnhancedFolderStart
    {
        get => _isEnhancedFolderStart;
        set
        {
            if (_isEnhancedFolderStart == value)
                return;
            _isEnhancedFolderStart = value;
            OnPropertyChanged();
        }
    }

    public string EnhancedFolderTitle
    {
        get => _enhancedFolderTitle;
        set
        {
            if (string.Equals(_enhancedFolderTitle, value, StringComparison.Ordinal))
                return;
            _enhancedFolderTitle = value;
            OnPropertyChanged();
        }
    }

    public bool IsEnhancedQueued
    {
        get => _isEnhancedQueued;
        set
        {
            if (_isEnhancedQueued == value)
                return;
            _isEnhancedQueued = value;
            OnPropertyChanged();
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public bool IsManualQueueItem
    {
        get => _isManualQueueItem;
        set
        {
            if (_isManualQueueItem == value)
                return;
            _isManualQueueItem = value;
            OnPropertyChanged();
        }
    }

    public long DurationMilliseconds
    {
        get => _durationMilliseconds;
        set
        {
            if (_durationMilliseconds == value)
                return;

            _durationMilliseconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationDisplay));
        }
    }

    public string DurationDisplay => DurationMilliseconds > 0
        ? FormatTime(DurationMilliseconds)
        : "—";

    public string DisplayLocation => CompactLocation(Location, DisplayFolderDepth);

    /// <summary>Nombre de dossiers parents visibles dans la file.</summary>
    public int DisplayFolderDepth
    {
        get => _displayFolderDepth;
        set
        {
            var normalized = Math.Clamp(value, 0, 10);
            if (_displayFolderDepth == normalized)
                return;

            _displayFolderDepth = normalized;
            OnPropertyChanged(nameof(DisplayLocation));
        }
    }

    public string Kind => IsNetwork
        ? "FLUX"
        : Path.GetExtension(Location).TrimStart('.').ToUpperInvariant() switch
        {
            "" => "MÉDIA",
            var extension => extension
        };

    public static PlaylistItem FromLocation(string location)
    {
        var isNetwork = Uri.TryCreate(location, UriKind.Absolute, out var uri)
                        && !uri.IsFile;
        var title = isNetwork
            ? uri!.Host
            : Path.GetFileNameWithoutExtension(location);

        return new PlaylistItem
        {
            Location = location,
            Title = string.IsNullOrWhiteSpace(title) ? location : title,
            IsNetwork = isNetwork
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string CompactLocation(string location, int folderDepth)
    {
        if (string.IsNullOrWhiteSpace(location))
            return location;

        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile)
            return location;

        try
        {
            var fullPath = Path.GetFullPath(location);
            var fileName = Path.GetFileName(fullPath);
            if (folderDepth <= 0)
                return fileName;

            // Parcourir les parents directement garantit que la valeur du
            // réglage correspond bien au nombre de dossiers affichés, y
            // compris pour le premier élément de la file.
            var parts = new List<string> { fileName };
            var directory = Directory.GetParent(fullPath);
            for (var index = 0; index < Math.Clamp(folderDepth, 0, 10) &&
                                  directory is not null; index++)
            {
                if (!string.IsNullOrWhiteSpace(directory.Name))
                    parts.Insert(0, directory.Name);
                directory = directory.Parent;
            }

            return string.Join(Path.DirectorySeparatorChar, parts);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                          PathTooLongException)
        {
            return location;
        }
    }
}
