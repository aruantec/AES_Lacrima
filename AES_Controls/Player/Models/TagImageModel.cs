using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Code.Models;

/// <summary>
/// Specifies the categorical intent of a tag image.
/// </summary>
public enum TagImageKind
{
    /// <summary>Front cover of the media or album.</summary>
    Cover,
    /// <summary>Back cover of the media or album.</summary>
    BackCover,
    /// <summary>A wallpaper suitable for desktop backgrounds.</summary>
    Wallpaper,
    /// <summary>A video or animated sequence used as a live wallpaper.</summary>
    LiveWallpaper,
    /// <summary>Image of the artist or band.</summary>
    Artist,
    /// <summary>Miscellaneous or unspecified image type.</summary>
    Other
}

/// <summary>
/// Represents embedded media artwork or metadata images, providing lazy-loaded bitmaps and previews.
/// </summary>
public partial class TagImageModel : ObservableObject, IDisposable
{
    private Bitmap? _cachedImage;
    /// <summary>
    /// Gets or sets the full-resolution bitmap of the inner data.
    /// Bitmaps are lazily created and cached. Returns null for LiveWallpaper or empty data.
    /// </summary>
    public Bitmap? Image
    {
        get
        {
            if (_cachedImage != null) return _cachedImage;
            if (Kind == TagImageKind.LiveWallpaper || Data == null || Data.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(Data);
                _cachedImage = new Bitmap(ms);
                return _cachedImage;
            }
            catch { return null; }
        }
        set => _cachedImage = value;
    }

    private Bitmap? _cachedPreview;
    /// <summary>
    /// Gets a downscaled preview version of the image (max width 250px).
    /// Prevents excessive memory overhead when displaying multiple thumbnails.
    /// </summary>
    public Bitmap? PreviewImage
    {
        get
        {
            if (_cachedPreview != null) return _cachedPreview;
            if (Kind == TagImageKind.LiveWallpaper || Data == null || Data.Length == 0) return null;

            try
            {
                using var ms = new MemoryStream(Data);
                using var original = new Bitmap(ms);
                var px = original.PixelSize;
                const int maxWidth = 250;

                if (px.Width <= maxWidth)
                {
                    _cachedPreview = new Bitmap(new MemoryStream(Data));
                    return _cachedPreview;
                }

                int targetW = maxWidth;
                int targetH = Math.Max(1, (int)Math.Round(px.Height * (targetW / (double)px.Width)));

                var target = new RenderTargetBitmap(new PixelSize(targetW, targetH), new Vector(96, 96));
                using (var ctx = target.CreateDrawingContext())
                {
                    ctx.DrawImage(original,
                        new Rect(0, 0, px.Width, px.Height),
                        new Rect(0, 0, targetW, targetH));
                }

                _cachedPreview = target;
                return _cachedPreview;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Action triggered when the image is requested to be deleted.
    /// </summary>
    public Action<TagImageModel>? OnDeleteImage { get; set; }

    /// <summary>
    /// Gets or sets the kind of image this represents.
    /// </summary>
    [ObservableProperty]
    private TagImageKind _kind;

    /// <summary>
    /// Gets or sets the raw binary data of the image.
    /// </summary>
    [ObservableProperty]
    private byte[] _data;

    /// <summary>
    /// Clears cached bitmaps when binary data changes.
    /// </summary>
    partial void OnDataChanged(byte[] value)
    {
        _cachedImage?.Dispose();
        _cachedImage = null;
        _cachedPreview?.Dispose();
        _cachedPreview = null;
    }

    /// <summary>
    /// Clears cached bitmaps when the image kind changes.
    /// </summary>
    partial void OnKindChanged(TagImageKind value)
    {
        _cachedImage?.Dispose();
        _cachedImage = null;
        _cachedPreview?.Dispose();
        _cachedPreview = null;
    }

    /// <summary>
    /// Gets or sets the MIME type (e.g., image/jpeg).
    /// </summary>
    [ObservableProperty]
    private string _mimeType;

    /// <summary>
    /// Gets or sets a description for the image.
    /// </summary>
    [ObservableProperty]
    private string _description;

    /// <summary>
    /// Returns all available <see cref="TagImageKind"/> values.
    /// </summary>
    public IEnumerable<TagImageKind> ImageKinds { get; } = Enum.GetValues<TagImageKind>();

    /// <summary>
    /// Initializes a new instance of the <see cref="TagImageModel"/> class.
    /// </summary>
    /// <param name="kind">The kind of image.</param>
    /// <param name="data">The raw binary data.</param>
    /// <param name="mimeType">The MIME type.</param>
    /// <param name="description">Optional description.</param>
    public TagImageModel(TagImageKind kind, byte[] data, string mimeType, string? description = null)
    {
        Kind = kind;
        Data = data;
        MimeType = mimeType;
        Description = description!;
    }

    /// <summary>
    /// Manually triggers a property change notification.
    /// </summary>
    /// <param name="propertyName">Name of the property.</param>
    public void RaisePropertyChanged(string propertyName)
    {
        OnPropertyChanged(propertyName);
    }

    /// <summary>
    /// Invokes the <see cref="OnDeleteImage"/> command.
    /// </summary>
    [RelayCommand]
    private void DeleteImage()
    {
        OnDeleteImage?.Invoke(this);
    }

    /// <summary>
    /// Disposes cached bitmaps.
    /// </summary>
    public void Dispose()
    {
        _cachedImage?.Dispose();
        _cachedImage = null;
        _cachedPreview?.Dispose();
        _cachedPreview = null;
    }
}