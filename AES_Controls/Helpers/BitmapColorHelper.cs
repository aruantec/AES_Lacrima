using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AES_Controls.Helpers;

public class BitmapColorHelper
{
    public static Color GetDominantColor(Bitmap? bitmap)
    {
        var colors = GetDominantColors(bitmap, 1);
        return colors.Length > 0 ? colors[0] : Colors.Transparent;
    }

    /// <summary>
    /// Picks white or black text depending on which contrasts better with <paramref name="background"/>.
    /// </summary>
    public static Color GetReadableForeground(Color background, Color lightForeground = default, Color darkForeground = default)
    {
        if (lightForeground == default)
            lightForeground = Colors.White;
        if (darkForeground == default)
            darkForeground = Colors.Black;

        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        double luminance =
            0.2126 * Linearize(background.R) +
            0.7152 * Linearize(background.G) +
            0.0722 * Linearize(background.B);

        double contrastWithLight = (1.05) / (luminance + 0.05);
        double contrastWithDark = (luminance + 0.05) / 0.05;
        return contrastWithLight >= contrastWithDark ? lightForeground : darkForeground;
    }

    /// <summary>
    /// Extract up to <paramref name="maxColors"/> vivid, hue-distinct colors from a bitmap.
    /// </summary>
    public static Color[] GetDominantColors(Bitmap? bitmap, int maxColors = 3)
    {
        if (bitmap == null || maxColors <= 0) return [];

        try
        {
            if (!TrySampleBitmapPixels(bitmap, 32, out var pixels))
                return [];

            return PickDistinctVividColors(pixels, maxColors).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static unsafe bool TrySampleBitmapPixels(Bitmap bitmap, int sampleSize, out byte[] pixels)
    {
        pixels = [];
        var sourceSize = bitmap.Size;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0) return false;

        var size = new PixelSize(sampleSize, sampleSize);
        using var small = new RenderTargetBitmap(size);
        using (var ctx = small.CreateDrawingContext())
        {
            ctx.DrawImage(bitmap, new Rect(0, 0, sourceSize.Width, sourceSize.Height),
                new Rect(0, 0, size.Width, size.Height));
        }

        pixels = new byte[size.Width * size.Height * 4];
        fixed (byte* p = pixels)
        {
            small.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pixels.Length, size.Width * 4);
        }

        return true;
    }

    private static List<Color> PickDistinctVividColors(byte[] pixels, int maxColors)
    {
        var colorCounts = new Dictionary<uint, (int Count, uint SumR, uint SumG, uint SumB)>();
        bool sawAnyOpaque = false;
        bool sawAnyNonBlack = false;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];

            if (a >= 128) sawAnyOpaque = true;
            if (!(r < 20 && g < 20 && b < 20)) sawAnyNonBlack = true;

            if (a < 32) continue;
            if (r < 20 && g < 20 && b < 20) continue;

            uint binR = (uint)(r / 16) * 16;
            uint binG = (uint)(g / 16) * 16;
            uint binB = (uint)(b / 16) * 16;
            uint colorKey = (binR << 16) | (binG << 8) | binB;

            if (colorCounts.TryGetValue(colorKey, out var entry))
            {
                entry.Count++;
                entry.SumR += r;
                entry.SumG += g;
                entry.SumB += b;
                colorCounts[colorKey] = entry;
            }
            else
            {
                colorCounts[colorKey] = (1, r, g, b);
            }
        }

        if (colorCounts.Count == 0)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a <= 16) continue;
                uint key = ((uint)r << 16) | ((uint)g << 8) | b;
                if (colorCounts.TryGetValue(key, out var entry))
                {
                    entry.Count++;
                    entry.SumR += r;
                    entry.SumG += g;
                    entry.SumB += b;
                    colorCounts[key] = entry;
                }
                else
                {
                    colorCounts[key] = (1, r, g, b);
                }
            }
        }

        var topColors = colorCounts
            .Select(kv =>
            {
                var entry = kv.Value;
                byte avgR = (byte)(entry.SumR / entry.Count);
                byte avgG = (byte)(entry.SumG / entry.Count);
                byte avgB = (byte)(entry.SumB / entry.Count);
                var c = Color.FromRgb(avgR, avgG, avgB);
                int max = Math.Max(c.R, Math.Max(c.G, c.B));
                int min = Math.Min(c.R, Math.Min(c.G, c.B));
                float chroma = (max - min) / 255f;
                return new { Color = c, Score = entry.Count * (chroma * chroma) };
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var picks = new List<Color>();
        if (topColors.Count == 0 || (!sawAnyOpaque && !sawAnyNonBlack))
            return picks;

        picks.Add(OverdriveColor(topColors[0].Color));
        float primaryHue = GetHue(picks[0]);

        foreach (var item in topColors.Skip(1))
        {
            if (picks.Count >= maxColors) break;
            var cand = OverdriveColor(item.Color);
            float h = GetHue(cand);
            float diff = Math.Abs(h - primaryHue);
            if (diff > 3.0f) diff = 6.0f - diff;
            if (diff > 0.6f && !picks.Any(pc => Math.Abs(GetHue(pc) - h) < 0.35f))
                picks.Add(cand);
        }

        if (picks.Count < maxColors)
        {
            foreach (var item in topColors.Skip(1))
            {
                if (picks.Count >= maxColors) break;
                var cand = OverdriveColor(item.Color);
                if (!picks.Contains(cand)) picks.Add(cand);
            }
        }

        float hueStep = maxColors <= 1 ? 0f : 2.0f / maxColors;
        while (picks.Count < maxColors)
            picks.Add(ShiftHue(picks[0], hueStep * picks.Count));

        return picks;
    }

    private unsafe (Color primary, Color secondary) GetThemePalette(Bitmap? bitmap)
    {
        if (bitmap == null) return (Color.Parse("#FF004D"), Color.Parse("#00CCFF"));

        byte[] pixels;
        try
        {
            var sourceSize = bitmap.Size;
            if (sourceSize.Width <= 0 || sourceSize.Height <= 0) return (Color.Parse("#FF004D"), Color.Parse("#00CCFF"));

            var size = new PixelSize(32, 32);
            // Use RenderTargetBitmap for scaling as it's more robust than CreateScaledBitmap for various bitmap implementations
            using var small = new RenderTargetBitmap(size);
            using (var ctx = small.CreateDrawingContext())
            {
                ctx.DrawImage(bitmap, new Rect(0, 0, sourceSize.Width, sourceSize.Height), new Rect(0, 0, size.Width, size.Height));
            }

            pixels = new byte[size.Width * size.Height * 4];
            fixed (byte* p = pixels)
            {
                small.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pixels.Length, size.Width * 4);
            }
        }
        catch
        {
            return (Color.Parse("#FF004D"), Color.Parse("#00CCFF"));
        }

        // track counts and raw sums so we can compute an unbiased representative color
        var colorCounts = new Dictionary<uint, (int Count, uint SumR, uint SumG, uint SumB)>();
        bool sawAnyOpaque = false;
        bool sawAnyNonBlack = false;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];

            if (a >= 128) sawAnyOpaque = true;
            if (!(r < 20 && g < 20 && b < 20)) sawAnyNonBlack = true;

            if (a < 32) continue;
            if (r < 20 && g < 20 && b < 20) continue;

            uint binR = (uint)(r / 16) * 16;
            uint binG = (uint)(g / 16) * 16;
            uint binB = (uint)(b / 16) * 16;
            uint colorKey = (binR << 16) | (binG << 8) | binB;

            if (colorCounts.TryGetValue(colorKey, out var entry))
            {
                entry.Count++;
                entry.SumR += r;
                entry.SumG += g;
                entry.SumB += b;
                colorCounts[colorKey] = entry;
            }
            else
            {
                colorCounts[colorKey] = (1, r, g, b);
            }
        }

        if (colorCounts.Count == 0)
        {
            // second pass with minimal filtering
            for (var i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a <= 16) continue;
                uint key = ((uint)r << 16) | ((uint)g << 8) | b;
                if (colorCounts.TryGetValue(key, out var entry))
                {
                    entry.Count++;
                    entry.SumR += r;
                    entry.SumG += g;
                    entry.SumB += b;
                    colorCounts[key] = entry;
                }
                else
                {
                    colorCounts[key] = (1, r, g, b);
                }
            }
        }

        // Sort by frequency, but multiply by chroma to ensure we don't pick grays
        var topColors = colorCounts
            .Select(kv => {
                var entry = kv.Value;
                byte avgR = (byte)(entry.SumR / entry.Count);
                byte avgG = (byte)(entry.SumG / entry.Count);
                byte avgB = (byte)(entry.SumB / entry.Count);
                var c = Color.FromRgb(avgR, avgG, avgB);
                int max = Math.Max(c.R, Math.Max(c.G, c.B));
                int min = Math.Min(c.R, Math.Min(c.G, c.B));
                float chroma = (max - min) / 255f;
                return new { Color = c, Score = entry.Count * (chroma * chroma) }; // Heavy bias toward vivid colors
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (topColors.Count == 0 || (!sawAnyOpaque && !sawAnyNonBlack))
            return (Color.Parse("#FF004D"), Color.Parse("#00CCFF"));

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

    /// <summary>
    /// Build a horizontal gradient from up to <paramref name="maxColors"/> dominant cover colors.
    /// </summary>
    public static LinearGradientBrush GetDominantColorGradient(Bitmap? bitmap, int maxColors = 3)
    {
        var defaultColors = new[]
        {
            Color.Parse("#00CCFF"),
            Color.Parse("#3333FF"),
            Color.Parse("#CC00CC")
        };

        var picks = bitmap == null ? [] : GetDominantColors(bitmap, maxColors);
        if (picks.Length == 0)
            picks = defaultColors;

        var stops = new GradientStops();
        for (int i = 0; i < picks.Length; i++)
        {
            double offset = picks.Length == 1 ? 0.0 : i / (double)(picks.Length - 1);
            stops.Add(new GradientStop(picks[i], offset));
        }

        return new LinearGradientBrush
        {
            GradientStops = stops,
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative)
        };
    }

    public LinearGradientBrush GetColorGradient(Bitmap? bitmap) =>
        GetDominantColorGradient(bitmap, 3);

    // Replaces NormalizeBrightness to give that HDR "Pop"
    private static Color OverdriveColor(Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        if (max <= 0) return c;

        // Push the saturation and ensure it's not dim
        // Avoid blowing the channel all the way to 1.0; a little boost is enough
        float factor = 1.0f / max;
        if (factor > 1.2f) factor = 1.2f; // cap saturation increase at 20%
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

    private static Color ShiftHue(Color c, float deltaHue)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        float d = max - min;
        if (d < 0.001f)
            return OverdriveColor(Color.FromRgb((byte)((c.R + 48) % 256), (byte)((c.G + 24) % 256), c.B));

        float h = GetHue(c) + deltaHue;
        while (h >= 6f) h -= 6f;
        while (h < 0f) h += 6f;

        float s = d / max;
        float v = max;
        float cVal = v * s;
        float x = cVal * (1f - Math.Abs(h % 2f - 1f));
        float m = v - cVal;

        float rf, gf, bf;
        if (h < 1f) { rf = cVal; gf = x; bf = 0; }
        else if (h < 2f) { rf = x; gf = cVal; bf = 0; }
        else if (h < 3f) { rf = 0; gf = cVal; bf = x; }
        else if (h < 4f) { rf = 0; gf = x; bf = cVal; }
        else if (h < 5f) { rf = x; gf = 0; bf = cVal; }
        else { rf = cVal; gf = 0; bf = x; }

        return OverdriveColor(Color.FromRgb(
            (byte)Math.Clamp((rf + m) * 255f, 0, 255),
            (byte)Math.Clamp((gf + m) * 255f, 0, 255),
            (byte)Math.Clamp((bf + m) * 255f, 0, 255)));
    }
}
