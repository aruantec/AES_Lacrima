using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace AES_Controls.Composition;

internal static class CompositionSkiaTextHelper
{
    private static readonly string[] PreferredFamilies =
    [
        "Segoe UI",
        "Microsoft YaHei UI",
        "Yu Gothic UI",
        "Malgun Gothic",
        "Segoe UI Symbol"
    ];

    private static SKTypeface? _defaultTypeface;
    private static readonly ConcurrentDictionary<string, FontRun[]> RunCache = new();
    private static readonly ConcurrentDictionary<(string Text, int MaxWidthPx), string> TruncateCache = new();

    private readonly record struct FontRun(string Text, SKTypeface Typeface);

    public static void ConfigurePaint(SKPaint paint)
    {
        paint.Typeface = GetDefaultTypeface();
        paint.TextEncoding = SKTextEncoding.Utf16;
        paint.LcdRenderText = true;
    }

    public static float MeasureText(string text, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
            return 0f;

        float width = 0f;
        foreach (var run in GetRuns(text))
        {
            using var font = CreateFont(run.Typeface, paint.TextSize);
            width += font.MeasureText(run.Text);
        }

        return width;
    }

    public static void DrawText(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
            return;

        float cursorX = x;
        foreach (var run in GetRuns(text))
        {
            using var font = CreateFont(run.Typeface, paint.TextSize);
            canvas.DrawText(run.Text, cursorX, y, font, paint);
            cursorX += font.MeasureText(run.Text);
        }
    }

    public static string TruncateText(string text, float maxWidth, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        int maxWidthPx = Math.Max(1, (int)MathF.Round(maxWidth));
        var key = (text, maxWidthPx);
        return TruncateCache.GetOrAdd(key, static (k, state) =>
        {
            var (source, widthPx) = k;
            var p = (SKPaint)state!;
            return TruncateTextCore(source, widthPx, p);
        }, paint);
    }

    private static string TruncateTextCore(string text, float maxWidth, SKPaint paint)
    {
        if (MeasureText(text, paint) <= maxWidth)
            return text;

        const string ellipsis = "…";
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(rune);
            if (MeasureText(builder + ellipsis, paint) > maxWidth)
            {
                builder.Length -= rune.Utf16SequenceLength;
                break;
            }
        }

        return builder.Length == 0 ? ellipsis : builder + ellipsis;
    }

    private static FontRun[] GetRuns(string text) =>
        RunCache.GetOrAdd(text, static t => BuildFontRuns(t).ToArray());

    private static SKFont CreateFont(SKTypeface typeface, float textSize) =>
        new(typeface, textSize);

    private static SKTypeface GetDefaultTypeface()
    {
        if (_defaultTypeface != null)
            return _defaultTypeface;

        foreach (var family in PreferredFamilies)
        {
            var typeface = SKTypeface.FromFamilyName(family);
            if (typeface != null && !string.IsNullOrWhiteSpace(typeface.FamilyName))
            {
                _defaultTypeface = typeface;
                return typeface;
            }
        }

        _defaultTypeface = SKTypeface.Default;
        return _defaultTypeface;
    }

    private static IEnumerable<FontRun> BuildFontRuns(string text)
    {
        var fontManager = SKFontManager.Default;
        var buffer = new StringBuilder();
        SKTypeface? currentTypeface = null;

        foreach (var rune in text.EnumerateRunes())
        {
            var typeface = fontManager.MatchCharacter(
                PreferredFamilies[0],
                SKFontStyle.Normal,
                null,
                rune.Value) ?? GetDefaultTypeface();

            if (currentTypeface != null && !SameTypeface(currentTypeface, typeface))
            {
                yield return new FontRun(buffer.ToString(), currentTypeface);
                buffer.Clear();
            }

            currentTypeface = typeface;
            buffer.Append(rune);
        }

        if (buffer.Length > 0 && currentTypeface != null)
            yield return new FontRun(buffer.ToString(), currentTypeface);
    }

    private static bool SameTypeface(SKTypeface left, SKTypeface right) =>
        left.FamilyName == right.FamilyName && left.FontWeight == right.FontWeight;
}
