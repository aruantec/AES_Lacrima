using System.Net.Http;
using AES_Code.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Shows how a wallpaper or letterbox bezel candidate will appear around arcade capture.
/// </summary>
public class CompositionWallpaperBezelPreviewControl : Border
{
    private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly Image _previewImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
    };

    private CancellationTokenSource? _loadCts;
    private int _loadGeneration;

    public static readonly StyledProperty<string?> ImageUrlProperty =
        AvaloniaProperty.Register<CompositionWallpaperBezelPreviewControl, string?>(nameof(ImageUrl));

    public static readonly StyledProperty<TagImageKind> PreviewKindProperty =
        AvaloniaProperty.Register<CompositionWallpaperBezelPreviewControl, TagImageKind>(
            nameof(PreviewKind),
            TagImageKind.BackCover);

    public string? ImageUrl
    {
        get => GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public TagImageKind PreviewKind
    {
        get => GetValue(PreviewKindProperty);
        set => SetValue(PreviewKindProperty, value);
    }

    public CompositionWallpaperBezelPreviewControl()
    {
        Background = new SolidColorBrush(Color.Parse("#14000000"));
        BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(12);
        ClipToBounds = true;
        Child = _previewImage;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ImageUrlProperty || change.Property == PreviewKindProperty)
            SchedulePreviewUpdate();
    }

    private void SchedulePreviewUpdate()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var generation = ++_loadGeneration;
        var ct = _loadCts.Token;
        var imageUrl = ImageUrl;
        var previewKind = PreviewKind;

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            _previewImage.Source = null;
            return;
        }

        _ = UpdatePreviewAsync(imageUrl, previewKind, generation, ct);
    }

    private async Task UpdatePreviewAsync(
        string imageUrl,
        TagImageKind previewKind,
        int generation,
        CancellationToken ct)
    {
        try
        {
            using var response = await ImageHttpClient.GetAsync(imageUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || generation != _loadGeneration)
                return;

            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return;

            var resolvedKind = previewKind;
            if (previewKind is TagImageKind.BackCover or TagImageKind.Wallpaper)
            {
                resolvedKind = InferPreviewKind(imageUrl, bitmap.Width, bitmap.Height, previewKind);
            }

            using var source = SKImage.FromBitmap(bitmap);
            using var rendered = CompositionWallpaperBezelPreviewRenderer.Render(source, resolvedKind);
            if (rendered == null || ct.IsCancellationRequested || generation != _loadGeneration)
                return;

            using var encoded = rendered.Encode(SKEncodedImageFormat.Png, 92);
            await using var stream = encoded.AsStream();
            var preview = new Bitmap(stream);

            if (ct.IsCancellationRequested || generation != _loadGeneration)
            {
                preview.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration)
                {
                    preview.Dispose();
                    return;
                }

                if (_previewImage.Source is IDisposable old)
                    old.Dispose();
                _previewImage.Source = preview;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (generation == _loadGeneration)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_previewImage.Source is IDisposable old)
                        old.Dispose();
                    _previewImage.Source = null;
                });
            }
        }
    }

    private static TagImageKind InferPreviewKind(string imageUrl, int width, int height, TagImageKind fallback)
    {
        var lower = imageUrl.ToLowerInvariant();
        if (lower.Contains("wallpaper", StringComparison.Ordinal))
            return TagImageKind.Wallpaper;
        if (lower.Contains("bezel", StringComparison.Ordinal)
            || lower.Contains("marquee", StringComparison.Ordinal)
            || lower.Contains("cabinet", StringComparison.Ordinal))
        {
            return TagImageKind.BackCover;
        }

        if (width > 0 && height > 0)
        {
            var aspect = width / (double)height;
            if (aspect >= 1.55)
                return TagImageKind.BackCover;
            if (aspect <= 0.85)
                return TagImageKind.Wallpaper;
        }

        return fallback;
    }
}
