using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AES_Controls.Helpers;

public class BitmapColorHelper
{
    public static unsafe Color GetDominantColor(Bitmap bitmap)
    {
        if (bitmap == null) return Colors.Transparent;

        var size = new PixelSize(32, 32);
        // Use RenderTargetBitmap for scaling as it's more robust than CreateScaledBitmap for various bitmap implementations
        using var small = new RenderTargetBitmap(size);
        using (var ctx = small.CreateDrawingContext())
        {
            ctx.DrawImage(bitmap, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), new Rect(0, 0, size.Width, size.Height));
        }

        var pixels = new byte[size.Width * size.Height * 4];

        fixed (byte* p = pixels)
        {
            small.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pixels.Length, size.Width * 4);
        }

        long r = 0, g = 0, b = 0;
        var totalPixels = size.Width * size.Height;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Bitmap.CopyPixels usually returns BGRA
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
        }

        return Color.FromUInt32(0xFF000000 |
              (uint)(r / totalPixels) << 16 |
              (uint)(g / totalPixels) << 8 |
              (uint)(b / totalPixels));
    }

    private unsafe (Color primary, Color secondary) GetThemePalette(Bitmap bitmap)
    {
        if (bitmap == null) return (Color.Parse("#FF004D"), Color.Parse("#00CCFF"));

        var size = new PixelSize(32, 32);
        // Use RenderTargetBitmap for scaling as it's more robust than CreateScaledBitmap for various bitmap implementations
        using var small = new RenderTargetBitmap(size);
        using (var ctx = small.CreateDrawingContext())
        {
            ctx.DrawImage(bitmap, new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), new Rect(0, 0, size.Width, size.Height));
        }

        var pixels = new byte[size.Width * size.Height * 4];

        fixed (byte* p = pixels)
        {
            small.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pixels.Length, size.Width * 4);
        }

        // Key Change: Use a Dictionary to count occurrences of similar colors
        var colorCounts = new Dictionary<uint, int>();

        for (var i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];

            if (a < 128 || (r < 20 && g < 20 && b < 20)) continue;

            // "Posterize" the color to group similar shades together (bins of 16)
            uint binR = (uint)(r / 16) * 16;
            uint binG = (uint)(g / 16) * 16;
            uint binB = (uint)(b / 16) * 16;
            uint colorKey = (binR << 16) | (binG << 8) | binB;

            if (colorCounts.ContainsKey(colorKey)) colorCounts[colorKey]++;
            else colorCounts[colorKey] = 1;
        }

        // Sort by frequency, but multiply by chroma to ensure we don't pick grays
        var topColors = colorCounts
            .Select(kv => {
                var c = Color.FromUInt32(0xFF000000 | kv.Key);
                int max = Math.Max(c.R, Math.Max(c.G, c.B));
                int min = Math.Min(c.R, Math.Min(c.G, c.B));
                float chroma = (max - min) / 255f;
                return new { Color = c, Score = kv.Value * (chroma * chroma) }; // Heavy bias toward vivid colors
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (topColors.Count == 0) return (Color.Parse("#FF004D"), Color.Parse("#00CCFF"));

        Color primary = OverdriveColor(topColors[0].Color);

        // For secondary, find the most frequent color that isn't the same hue as primary
        float p_hue = GetHue(primary);
        var secondaryObj = topColors.Skip(1).FirstOrDefault(c => {
            float diff = Math.Abs(GetHue(c.Color) - p_hue);
            if (diff > 3.0f) diff = 6.0f - diff;
            return diff > 0.8f; // Look for a distinct second color
        });

        Color secondary = secondaryObj != null ? OverdriveColor(secondaryObj.Color) : primary;

        return (primary, secondary);
    }

    // Replaces NormalizeBrightness to give that HDR "Pop"
    private Color OverdriveColor(Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        if (max <= 0) return c;

        // Push the saturation and ensure it's not dim
        float factor = 1.0f / max;
        return Color.FromUInt32(0xFF000000 |
            (uint)Math.Clamp(r * factor * 255, 0, 255) << 16 |
            (uint)Math.Clamp(g * factor * 255, 0, 255) << 8 |
            (uint)Math.Clamp(b * factor * 255, 0, 255));
    }

    // Internal helper for Hue calculation (returns 0 to 6)
    private static float GetHue(Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (Math.Abs(max - min) < 0) return 0;
        float hue = (Math.Abs(max - r) < 0) ? (g - b) / (max - min) : (Math.Abs(max - g) < 0) ? 2f + (b - r) / (max - min) : 4f + (r - g) / (max - min);
        return hue < 0 ? hue + 6f : hue;
    }
}
