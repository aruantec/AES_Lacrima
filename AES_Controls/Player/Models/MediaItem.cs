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
    private (string, string)? _onlineUrls;
    
    private string? _fileName;
    [JsonPropertyName("FileName")]
    /// <summary>
    /// The file name or path of the media item.
    /// </summary>
    public string? FileName { get => _fileName; set => SetProperty(ref _fileName, value); }

    private string? _title;
    [JsonPropertyName("Title")]
    /// <summary>
    /// The display title of the media item.
    /// </summary>
    public string? Title { get => _title; set => SetProperty(ref _title, value); }

    private string? _artist;
    [JsonPropertyName("Artist")]
    /// <summary>
    /// The artist name associated with the media item.
    /// </summary>
    public string? Artist { get => _artist; set => SetProperty(ref _artist, value); }

    private string? _album;
    [JsonPropertyName("Album")]
    /// <summary>
    /// The album name for the media item.
    /// </summary>
    public string? Album { get => _album; set => SetProperty(ref _album, value); }

    private uint _track;
    [JsonPropertyName("Track")]
    /// <summary>
    /// The track number within the album.
    /// </summary>
    public uint Track { get => _track; set => SetProperty(ref _track, value); }

    private uint _year;
    [JsonPropertyName("Year")]
    /// <summary>
    /// The year associated with the media item (e.g., release year).
    /// </summary>
    public uint Year { get => _year; set => SetProperty(ref _year, value); }

    private string? _lyrics;
    [JsonPropertyName("Lyrics")]
    /// <summary>
    /// Optional lyrics for the media item.
    /// </summary>
    public string? Lyrics { get => _lyrics; set => SetProperty(ref _lyrics, value); }

    private string? _genre;
    [JsonPropertyName("Genre")]
    /// <summary>
    /// The genre of the media item.
    /// </summary>
    public string? Genre { get => _genre; set => SetProperty(ref _genre, value); }

    private string? _comment;
    [JsonPropertyName("Comment")]
    /// <summary>
    /// Free-form comment or description for the media item.
    /// </summary>
    public string? Comment { get => _comment; set => SetProperty(ref _comment, value); }

    private string? _localCoverPath;
    [JsonPropertyName("LocalCoverPath")]
    /// <summary>
    /// Path to a locally stored cover image for the media item.
    /// </summary>
    public string? LocalCoverPath { get => _localCoverPath; set => SetProperty(ref _localCoverPath, value); }

    private bool _isLoadingCover;
    [JsonIgnore]
    /// <summary>
    /// True while the cover image is being loaded asynchronously.
    /// This property is not serialized.
    /// </summary>
    public bool IsLoadingCover { get => _isLoadingCover; set => SetProperty(ref _isLoadingCover, value); }

    private bool _coverFound;
    [JsonIgnore]
    /// <summary>
    /// Indicates whether a cover image was found (used to show save/cancel
    /// UI). This property is not serialized.
    /// </summary>
    public bool CoverFound 
    {
        get => _coverFound;
        set => SetProperty(ref _coverFound, value);
    }
    
    [XmlIgnore]
    [JsonIgnore]
    /// <summary>
    /// Optional online resource URLs (for example cover and wallpaper URLs).
    /// This property is ignored for XML/JSON serialization.
    /// </summary>
    public (string, string)? OnlineUrls
    {
        get => _onlineUrls;
        set => SetProperty(ref _onlineUrls, value);
    }
    
    [XmlIgnore]
    [JsonIgnore]
    /// <summary>
    /// Runtime-only bitmap for the item's cover image. Setting a new bitmap
    /// disposes the previous one to free native resources. Not serialized.
    /// </summary>
    public Bitmap? CoverBitmap
    {
        get => _coverBitmap;
        set => SetProperty(ref _coverBitmap, value);
    }

    private Bitmap? _screenshotBitmap;
    [XmlIgnore]
    [JsonIgnore]
    /// <summary>
    /// Runtime-only bitmap used for screenshots or thumbnails. Not serialized.
    /// </summary>
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
    /// <summary>
    /// Runtime-only bitmap for a wallpaper image related to the media item.
    /// Setting a new value disposes the previous bitmap. Not serialized.
    /// </summary>
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

    private bool _isRenaming;
    [XmlIgnore]
    [JsonIgnore]
    /// <summary>
    /// Indicates whether the item name is currently being edited in the UI.
    /// </summary>
    public bool IsRenaming { get => _isRenaming; set => SetProperty(ref _isRenaming, value); }

    private bool _isNameInvalid;
    [XmlIgnore]
    [JsonIgnore]
    /// <summary>
    /// Indicates whether the current name is invalid (e.g., a duplicate).
    /// </summary>
    public bool IsNameInvalid { get => _isNameInvalid; set => SetProperty(ref _isNameInvalid, value); }

    private string? _nameInvalidMessage;
    [XmlIgnore]
    [JsonIgnore]
    /// <summary>
    /// Error message to display when the name is invalid.
    /// </summary>
    public string? NameInvalidMessage { get => _nameInvalidMessage; set => SetProperty(ref _nameInvalidMessage, value); }

    [XmlIgnore]
    [JsonIgnore]
    /// <summary>
    /// Optional action that is invoked to persist the cover bitmap for this
    /// media item (for example saving to disk). Not serialized.
    /// </summary>
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
    /// <summary>
    /// Command invoked to persist the current cover bitmap using the
    /// <see cref="SaveCoverBitmapAction"/>, then clears the found state.
    /// </summary>
    private void SaveCoverBitmap()
    {
        SaveCoverBitmapAction?.Invoke(this);
        CoverFound = false;
    }

    [RelayCommand]
    /// <summary>
    /// Command invoked to cancel saving a discovered cover image and hide
    /// the cover found UI.
    /// </summary>
    private void Cancel()
    {
        CoverFound = false;
    }
}