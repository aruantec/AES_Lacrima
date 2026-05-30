using AES_Controls.Helpers;
using SkiaSharp;

namespace AES_Controls.Tests;

public sealed class CoverImageBarCropHelperTests
{
    [Fact]
    public void TryCrop_RemovesTopAndBottomBlackBars()
    {
        using var source = CreateBarredImage(240, 320, 48, 48, 0, 0, SKColors.Coral);
        using var cropped = CoverImageBarCropHelper.TryCrop(source, out bool didCrop);

        Assert.True(didCrop);
        Assert.NotNull(cropped);
        Assert.Equal(240, cropped!.Width);
        Assert.Equal(224, cropped.Height);
    }

    [Fact]
    public void TryCrop_RemovesTransparentBars()
    {
        using var source = CreateBarredImage(200, 260, 30, 30, 0, 0, SKColors.SkyBlue, barAlpha: 0);
        using var cropped = CoverImageBarCropHelper.TryCrop(source, out bool didCrop);

        Assert.True(didCrop);
        Assert.NotNull(cropped);
        Assert.Equal(200, cropped!.Width);
        Assert.Equal(200, cropped.Height);
    }

    [Fact]
    public void TryCrop_LeavesFullBleedImageUntouched()
    {
        using var source = CreateSolidImage(180, 180, SKColors.MediumSeaGreen);
        using var cropped = CoverImageBarCropHelper.TryCrop(source, out bool didCrop);

        Assert.False(didCrop);
        Assert.Null(cropped);
    }

    private static SKBitmap CreateSolidImage(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    private static SKBitmap CreateBarredImage(
        int width,
        int height,
        int topBar,
        int bottomBar,
        int leftBar,
        int rightBar,
        SKColor contentColor,
        byte barAlpha = 255)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0, 0, 0, barAlpha));

        var contentRect = new SKRect(
            leftBar,
            topBar,
            width - rightBar,
            height - bottomBar);
        using var paint = new SKPaint { Color = contentColor, IsAntialias = true };
        canvas.DrawRect(contentRect, paint);
        return bitmap;
    }
}
