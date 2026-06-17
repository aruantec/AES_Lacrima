using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Shows a styled preview of a cover image as it will appear in carousel or grid mode.
/// </summary>
public class CompositionCoverPreviewControl : Border
{
    private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly Image _previewImage = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private CancellationTokenSource? _loadCts;
    private int _loadGeneration;

    public static readonly StyledProperty<string?> ImageUrlProperty =
        AvaloniaProperty.Register<CompositionCoverPreviewControl, string?>(nameof(ImageUrl));

    public static readonly StyledProperty<CoverLayoutMode> LayoutModeProperty =
        AvaloniaProperty.Register<CompositionCoverPreviewControl, CoverLayoutMode>(
            nameof(LayoutMode),
            CoverLayoutMode.Carousel);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CompositionCoverPreviewControl, string?>(nameof(Title));

    public string? ImageUrl
    {
        get => GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public CoverLayoutMode LayoutMode
    {
        get => GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public CompositionCoverPreviewControl()
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

        if (change.Property == ImageUrlProperty ||
            change.Property == LayoutModeProperty ||
            change.Property == TitleProperty)
        {
            SchedulePreviewUpdate();
        }
    }

    private void SchedulePreviewUpdate()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var generation = ++_loadGeneration;
        var ct = _loadCts.Token;
        var imageUrl = ImageUrl;
        var layoutMode = LayoutMode;
        var title = Title;

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            _previewImage.Source = null;
            return;
        }

        _ = UpdatePreviewAsync(imageUrl, layoutMode, title, generation, ct);
    }

    private async Task UpdatePreviewAsync(
        string imageUrl,
        CoverLayoutMode layoutMode,
        string? title,
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

            using var source = SKImage.FromBitmap(bitmap);
            using var rendered = CompositionCoverPreviewRenderer.Render(source, layoutMode, title);
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
}
