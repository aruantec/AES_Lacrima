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
    /// Extract up to five visually distinct colors from the provided bitmap and
    /// return them as a horizontal linear gradient brush. Attempts to pick
    /// different hues when possible; falls back to a default palette when no
    /// usable pixels are found.
    /// </summary>
    public unsafe LinearGradientBrush GetColorGradient(Bitmap bitmap)
    {
        // Default palette (matches existing default gradient in other controls)
        var defaultColors = new[] {
            Color.Parse("#00CCFF"), Color.Parse("#3333FF"), Color.Parse("#CC00CC"), Color.Parse("#FF004D"), Color.Parse("#FFB300")
        };

        if (bitmap == null)
        {
            var stopsFallback = new GradientStops();
            for (int i = 0; i < defaultColors.Length; i++)
            {
                double offset = defaultColors.Length == 1 ? 0.0 : i / (double)(defaultColors.Length - 1);
                stopsFallback.Add(new GradientStop(defaultColors[i], offset));
            }
            return new LinearGradientBrush
            {
                GradientStops = stopsFallback,
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative)
            };
        }

        var size = new PixelSize(48, 48);
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

        // store count plus sums so we can recover a more accurate representative color later
        var colorCounts = new Dictionary<uint, (int Count, uint SumR, uint SumG, uint SumB)>();
        bool sawAnyOpaque = false;
        bool sawAnyNonBlack = false;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];

            if (a >= 128) sawAnyOpaque = true;
            if (!(r < 20 && g < 20 && b < 20)) sawAnyNonBlack = true;

            // initial filtering: ignore almost-transparent pixels and true black
            if (a < 32) continue;
            if (r < 20 && g < 20 && b < 20) continue;

            uint binR = (uint)(r / 16) * 16;
            uint binG = (uint)(g / 16) * 16;
            uint binB = (uint)(b / 16) * 16;
            uint key = (binR << 16) | (binG << 8) | binB;

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

        // if we ended up with nothing useful, relax the filtering and count every non‑transparent pixel
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
            .Select(kv => {
                // recover an average color from the original samples in this bin
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

        List<Color> picks = new();
        
        // If no opaque or non‑black pixels were seen, it's probably a fully-transparent or all-black
        // image; fall back immediately so that defaultColors are used instead of garbage.
        if (topColors.Count == 0 || (!sawAnyOpaque && !sawAnyNonBlack))
        {
            picks.AddRange(defaultColors);
        }
        else
        {
            // pick most frequent/vivid as first
            picks.Add(OverdriveColor(topColors[0].Color));
            float primaryHue = GetHue(picks[0]);

            // try to pick distinct hues for the remaining slots
            foreach (var item in topColors.Skip(1))
            {
                if (picks.Count >= 5) break;
                var cand = OverdriveColor(item.Color);
                float h = GetHue(cand);
                float diff = Math.Abs(h - primaryHue);
                if (diff > 3.0f) diff = 6.0f - diff;
                if (diff > 0.6f && !picks.Any(pc => Math.Abs(GetHue(pc) - h) < 0.35f))
                {
                    picks.Add(cand);
                }
            }

            // If we still don't have enough, relax criteria and add next best
            if (picks.Count < 5)
            {
                foreach (var item in topColors.Skip(1))
                {
                    if (picks.Count >= 5) break;
                    var cand = OverdriveColor(item.Color);
                    if (!picks.Contains(cand)) picks.Add(cand);
                }
            }

            // Fill remaining slots with variations of primary if needed
            while (picks.Count < 5)
            {
                picks.Add(picks.Count > 0 ? picks[0] : defaultColors[picks.Count]);
            }
        }

        // Create gradient stops evenly spaced
        var stops = new GradientStops();
        for (int i = 0; i < Math.Min(5, picks.Count); i++)
        {
            double offset = (picks.Count == 1) ? 0.0 : i / (double)(picks.Count - 1);
            stops.Add(new GradientStop(picks[i], offset));
        }

        return new LinearGradientBrush
        {
            GradientStops = stops,
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative)
        };
    }

    // Replaces NormalizeBrightness to give that HDR "Pop"
    private Color OverdriveColor(Color c)
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
}
