using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace AES_Controls.Player.Models;

/// <summary>
/// Represents a media item (track) with metadata and cached images used by
/// the UI. The model exposes properties for serialization and runtime-only
/// properties for bitmaps and actions that should not be persisted.
/// </summary>
public partial class MediaItem : ObservableObject, IDisposable
{
    private Action<MediaItem>? _saveCoverBitmapAction;
    private Bitmap? _coverBitmap;
    private Bitmap? _wallpaperBitmap;
    private Bitmap? _screenshotBitmap;
    private (string, string)? _onlineUrls;

    // Persisted metadata
    private string? _fileName;

    [JsonPropertyName("FileName")]
    public string? FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    private string? _title;

    [JsonPropertyName("Title")]
    public string? Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string? _artist;

    [JsonPropertyName("Artist")]
    public string? Artist
    {
        get => _artist;
        set => SetProperty(ref _artist, value);
    }

    private string? _album;

    [JsonPropertyName("Album")]
    public string? Album
    {
        get => _album;
        set => SetProperty(ref _album, value);
    }

    private uint _track;

    [JsonPropertyName("Track")]
    public uint Track
    {
        get => _track;
        set => SetProperty(ref _track, value);
    }

    private uint _year;

    [JsonPropertyName("Year")]
    public uint Year
    {
        get => _year;
        set => SetProperty(ref _year, value);
    }

    private double _duration;

    [JsonPropertyName("Duration")]
    public double Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, value);
    }

    private string? _lyrics;

    [JsonPropertyName("Lyrics")]
    public string? Lyrics
    {
        get => _lyrics;
        set => SetProperty(ref _lyrics, value);
    }

    private string? _genre;

    [JsonPropertyName("Genre")]
    public string? Genre
    {
        get => _genre;
        set => SetProperty(ref _genre, value);
    }

    private string? _comment;

    [JsonPropertyName("Comment")]
    public string? Comment
    {
        get => _comment;
        set => SetProperty(ref _comment, value);
    }

    private double _replayGainTrackGain;

    [JsonPropertyName("ReplayGainTrackGain")]
    public double ReplayGainTrackGain
    {
        get => _replayGainTrackGain;
        set => SetProperty(ref _replayGainTrackGain, value);
    }

    private double _replayGainAlbumGain;

    [JsonPropertyName("ReplayGainAlbumGain")]
    public double ReplayGainAlbumGain
    {
        get => _replayGainAlbumGain;
        set => SetProperty(ref _replayGainAlbumGain, value);
    }

    private string? _localCoverPath;

    [JsonPropertyName("LocalCoverPath")]
    public string? LocalCoverPath
    {
        get => _localCoverPath;
        set => SetProperty(ref _localCoverPath, value);
    }

    // Runtime-only flags and resources (not serialized)
    private bool _isLoadingCover;
    private bool _coverFound;
    private bool _isRenaming;
    private bool _isNameInvalid;
    private string? _nameInvalidMessage;

    [JsonIgnore]
    public bool IsLoadingCover
    {
        get => _isLoadingCover;
        set => SetProperty(ref _isLoadingCover, value);
    }

    [JsonIgnore]
    public bool CoverFound
    {
        get => _coverFound;
        set => SetProperty(ref _coverFound, value);
    }

    [XmlIgnore]
    [JsonIgnore]
    public (string, string)? OnlineUrls
    {
        get => _onlineUrls;
        set => SetProperty(ref _onlineUrls, value);
    }

    [XmlIgnore]
    [JsonIgnore]
    public Bitmap? CoverBitmap
    {
        get => _coverBitmap;
        set => SetProperty(ref _coverBitmap, value);
    }

    private int _index;

    [XmlIgnore]
    [JsonIgnore]
    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    [XmlIgnore]
    [JsonIgnore]
    public Bitmap? ScreenshotBitmap
    {
        get => _screenshotBitmap;
        set
        {
            var old = _screenshotBitmap;
            if (SetProperty(ref _screenshotBitmap, value))
                old?.Dispose();
        }
    }

    [XmlIgnore]
    [JsonIgnore]
    public Bitmap? WallpaperBitmap
    {
        get => _wallpaperBitmap;
        set
        {
            var old = _wallpaperBitmap;
            if (SetProperty(ref _wallpaperBitmap, value))
                old?.Dispose();
        }
    }

    [XmlIgnore]
    [JsonIgnore]
    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetProperty(ref _isRenaming, value);
    }

    [XmlIgnore]
    [JsonIgnore]
    public bool IsNameInvalid
    {
        get => _isNameInvalid;
        set => SetProperty(ref _isNameInvalid, value);
    }

    [XmlIgnore]
    [JsonIgnore]
    public string? NameInvalidMessage
    {
        get => _nameInvalidMessage;
        set => SetProperty(ref _nameInvalidMessage, value);
    }

    [XmlIgnore]
    [JsonIgnore]
    public Action<MediaItem>? SaveCoverBitmapAction
    {
        get => _saveCoverBitmapAction;
        set => SetProperty(ref _saveCoverBitmapAction, value);
    }

    /// <summary>
    /// Disposes any native bitmap resources owned by this model.
    /// </summary>
    public void Dispose()
    {
        _coverBitmap?.Dispose();
        _coverBitmap = null;

        _wallpaperBitmap?.Dispose();
        _wallpaperBitmap = null;

        _screenshotBitmap?.Dispose();
        _screenshotBitmap = null;
    }

    [RelayCommand]
    private void SaveCoverBitmap()
    {
        SaveCoverBitmapAction?.Invoke(this);
        CoverFound = false;
    }

    [RelayCommand]
    private void Cancel()
    {
        CoverFound = false;
    }
}