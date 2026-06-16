using AES_Controls.Composition;
using SkiaSharp;

namespace AES_Controls.Tests;

public sealed class CompositionCardDisplayBakerTests
{
    [Fact]
    public void Bake_ProducesLargerThanSourceThumbnail()
    {
        using var source = CreateSolidImage(128, 128);
        using var baked = CompositionCardDisplayBaker.Bake(source);

        Assert.True(baked.Width >= CompositionCardDisplayBaker.BakedCardWidth - 1);
        Assert.True(baked.Height > source.Height);
    }

    [Fact]
    public void Bake_WithTitle_ChangesPixelsInTitleArea()
    {
        using var source = CreateSolidImage(128, 128);
        using var withoutTitle = CompositionCardDisplayBaker.Bake(source);
        using var withTitle = CompositionCardDisplayBaker.Bake(source, "Test Album Title");

        Assert.Equal(withoutTitle.Width, withTitle.Width);
        Assert.Equal(withoutTitle.Height, withTitle.Height);

        float titleTop = withTitle.Height * CompositionCardDisplayBaker.TitleAreaRatio;
        var withoutPixel = withoutTitle.PeekPixels().GetPixelColor(20, (int)(titleTop + 10));
        var withPixel = withTitle.PeekPixels().GetPixelColor(20, (int)(titleTop + 10));
        Assert.NotEqual(withoutPixel, withPixel);
    }

    private static SKImage CreateSolidImage(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.SteelBlue);
        return surface.Snapshot();
    }
}
