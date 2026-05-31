using System.Diagnostics;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

internal sealed record TitleLogoTextMessage(string Text);

internal sealed record TitleLogoAnimationMessage(bool IsEnabled);

internal sealed class TitleLogoIdleTickMessage
{
    public static TitleLogoIdleTickMessage Instance { get; } = new();
    private TitleLogoIdleTickMessage() { }
}

internal sealed class TitleLogoBurstTickMessage
{
    public static TitleLogoBurstTickMessage Instance { get; } = new();
    private TitleLogoBurstTickMessage() { }
}

/// <summary>
/// Low-overhead cyberpunk title renderer: idle timer updates with burst redraws only.
/// Does not use compositor animation frames to avoid competing with carousel scrolling.
/// </summary>
public sealed class TitleLogoCompositionVisualHandler : CompositionCustomVisualHandler
{
    private const string DefaultTitle = "AES LACRIMA";
    private const string GlitchCharset = "█▓▒░0123456789@#$%&*!?/\\|";

    private readonly struct GlitchSlice
    {
        public readonly float NormalizedY;
        public readonly float HeightFactor;
        public readonly float OffsetX;

        public GlitchSlice(float normalizedY, float heightFactor, float offsetX)
        {
            NormalizedY = normalizedY;
            HeightFactor = heightFactor;
            OffsetX = offsetX;
        }
    }

    private Vector2 _visualSize;
    private string _title = DefaultTitle;
    private bool _animationEnabled = true;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Random _random = new();

    private float _glitchOffsetX;
    private float _glitchOffsetY;
    private float _rgbSplit = 2.5f;
    private string _displayTitle = DefaultTitle;
    private int _glitchFramesRemaining;
    private int _cooldownTicks;
    private bool _flashActive;
    private GlitchSlice[] _slices = [];
    private int _idleRedrawCounter;

    internal bool IsGlitchBurstActive => _glitchFramesRemaining > 0;

    private SKTypeface? _typeface;
    private SKPaint? _shadowPaint;
    private SKPaint? _cyanPaint;
    private SKPaint? _magentaPaint;
    private SKPaint? _mainPaint;
    private SKPaint? _flashPaint;

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                Cleanup();
                return;
            case Vector2 size:
                _visualSize = size;
                Invalidate();
                return;
            case TitleLogoTextMessage text:
                _title = string.IsNullOrWhiteSpace(text.Text) ? DefaultTitle : text.Text;
                _displayTitle = _title;
                Invalidate();
                return;
            case TitleLogoAnimationMessage animation:
                _animationEnabled = animation.IsEnabled;
                Invalidate();
                return;
            case TitleLogoIdleTickMessage:
                if (_animationEnabled)
                    AdvanceIdleState();
                return;
            case TitleLogoBurstTickMessage:
                if (_animationEnabled && IsGlitchBurstActive)
                    AdvanceBurstFrame();
                return;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0)
            return;

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        EnsurePaints();

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        bool isGlitching = IsGlitchBurstActive;
        float pulse = isGlitching
            ? 1f
            : 0.82f + (0.18f * (float)Math.Sin(elapsed * 1.8));

        float width = _visualSize.X;
        float height = _visualSize.Y;
        float centerX = width / 2f + _glitchOffsetX;
        float textSize = Math.Clamp(Math.Min(width / 8.2f, height * 0.78f), 20f, 38f);

        ConfigureTextPaints(textSize, pulse);
        float baselineY = ComputeBaselineY(height) + _glitchOffsetY;

        float rgb = isGlitching ? _rgbSplit : 2.5f;

        DrawTitle(canvas, centerX + 1f, baselineY + 1f, _title, _shadowPaint);

        if (isGlitching)
        {
            if (_flashActive)
                DrawFlash(canvas, centerX, baselineY, textSize);

            DrawChannelSplit(canvas, centerX, baselineY, rgb);
            DrawGlitchSlices(canvas, centerX, baselineY, textSize);
            DrawGlitchCharacters(canvas, centerX, baselineY, _displayTitle, _mainPaint);
        }
        else
        {
            DrawTitle(canvas, centerX - rgb, baselineY, _displayTitle, _magentaPaint);
            DrawTitle(canvas, centerX + rgb, baselineY, _displayTitle, _cyanPaint);
            DrawTitle(canvas, centerX, baselineY, _displayTitle, _mainPaint);
        }
    }

    private void AdvanceIdleState()
    {
        if (IsGlitchBurstActive)
            return;

        if (_cooldownTicks > 0)
        {
            _cooldownTicks--;
            if (ShouldRedrawIdle())
                Invalidate();
            return;
        }

        StartGlitchBurst();
        Invalidate();
    }

    private void AdvanceBurstFrame()
    {
        _displayTitle = _random.NextDouble() < 0.55
            ? BuildCorruptedTitle(_random.Next(2, 6))
            : _title;

        _glitchFramesRemaining--;
        if (_glitchFramesRemaining <= 0)
        {
            ResetGlitch();
            _cooldownTicks = _random.Next(28, 55);
        }

        Invalidate();
    }

    private bool ShouldRedrawIdle()
    {
        _idleRedrawCounter++;
        return _idleRedrawCounter % 2 == 0;
    }

    private void DrawChannelSplit(SKCanvas canvas, float centerX, float baselineY, float rgb)
    {
        DrawTitle(canvas, centerX - rgb * 1.4f, baselineY - 1f, _displayTitle, _magentaPaint);
        DrawTitle(canvas, centerX + rgb * 1.4f, baselineY + 1f, _displayTitle, _cyanPaint);
        DrawTitle(canvas, centerX - rgb * 0.55f, baselineY, _displayTitle, _magentaPaint);
        DrawTitle(canvas, centerX + rgb * 0.55f, baselineY, _displayTitle, _cyanPaint);
    }

    private void DrawGlitchSlices(SKCanvas canvas, float centerX, float baselineY, float textSize)
    {
        if (_mainPaint == null || _slices.Length == 0)
            return;

        float textWidth = _mainPaint.MeasureText(_displayTitle);
        float left = centerX - (textWidth / 2f) - 6f;
        float right = centerX + (textWidth / 2f) + 6f;

        foreach (var slice in _slices)
        {
            float sliceTop = baselineY - (textSize * 0.62f) + (slice.NormalizedY * textSize * 0.72f);
            float sliceHeight = Math.Max(3f, textSize * slice.HeightFactor);

            canvas.Save();
            canvas.ClipRect(new SKRect(left, sliceTop, right, sliceTop + sliceHeight));
            DrawTitle(canvas, centerX + slice.OffsetX, baselineY, _displayTitle, _cyanPaint);
            DrawTitle(canvas, centerX + slice.OffsetX * 0.65f, baselineY, _displayTitle, _mainPaint);
            canvas.Restore();
        }
    }

    private void DrawGlitchCharacters(SKCanvas canvas, float centerX, float baselineY, string text, SKPaint? paint)
    {
        if (paint == null || string.IsNullOrEmpty(text))
            return;

        float totalWidth = paint.MeasureText(text);
        float x = centerX - (totalWidth / 2f);

        foreach (char c in text)
        {
            var glyph = c.ToString();
            float charWidth = paint.MeasureText(glyph);
            float jitterX = (float)(_random.NextDouble() * 5.0 - 2.5);
            float jitterY = (float)(_random.NextDouble() * 2.0 - 1.0);
            canvas.DrawText(glyph, x + jitterX, baselineY + jitterY, paint);
            x += charWidth;
        }
    }

    private void DrawFlash(SKCanvas canvas, float centerX, float baselineY, float textSize)
    {
        if (_flashPaint == null || _mainPaint == null)
            return;

        float textWidth = _mainPaint.MeasureText(_displayTitle);
        var rect = new SKRect(
            centerX - (textWidth / 2f) - 4f,
            baselineY - (textSize * 0.72f),
            centerX + (textWidth / 2f) + 4f,
            baselineY + (textSize * 0.18f));

        _flashPaint.Color = _random.Next(2) == 0
            ? new SKColor(0, 240, 255, 38)
            : new SKColor(255, 0, 170, 34);
        canvas.DrawRect(rect, _flashPaint);
    }

    private float ComputeBaselineY(float height)
    {
        if (_mainPaint == null)
            return height / 2f;

        _mainPaint.GetFontMetrics(out var metrics);
        return (height / 2f) - ((metrics.Ascent + metrics.Descent) / 2f);
    }

    private void DrawTitle(SKCanvas canvas, float x, float y, string text, SKPaint? paint)
    {
        if (paint == null || string.IsNullOrEmpty(text))
            return;

        canvas.DrawText(text, x, y, paint);
    }

    private void StartGlitchBurst()
    {
        _glitchFramesRemaining = _random.Next(3, 7);
        _glitchOffsetX = (float)(_random.NextDouble() * 16.0 - 8.0);
        _glitchOffsetY = (float)(_random.NextDouble() * 2.5 - 1.25);
        _rgbSplit = (float)(6.0 + _random.NextDouble() * 9.0);
        _flashActive = _random.NextDouble() < 0.65;
        _displayTitle = BuildCorruptedTitle(_random.Next(3, 7));

        int sliceCount = _random.Next(2, 4);
        _slices = new GlitchSlice[sliceCount];
        for (int i = 0; i < sliceCount; i++)
        {
            _slices[i] = new GlitchSlice(
                (float)_random.NextDouble(),
                0.1f + ((float)_random.NextDouble() * 0.16f),
                (float)(_random.NextDouble() * 22.0 - 11.0));
        }
    }

    private void ResetGlitch()
    {
        _displayTitle = _title;
        _glitchOffsetX = 0f;
        _glitchOffsetY = 0f;
        _rgbSplit = 2.5f;
        _flashActive = false;
        _slices = [];
    }

    private string BuildCorruptedTitle(int corruptCount)
    {
        var chars = _title.ToCharArray();
        for (int i = 0; i < corruptCount; i++)
        {
            int idx = _random.Next(chars.Length);
            if (chars[idx] == ' ')
                continue;

            chars[idx] = GlitchCharset[_random.Next(GlitchCharset.Length)];
        }

        return new string(chars);
    }

    private void EnsurePaints()
    {
        _typeface ??= SKTypeface.FromFamilyName(
            "Consolas",
            SKFontStyleWeight.Bold,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright)
            ?? SKTypeface.FromFamilyName(
                "Segoe UI",
                SKFontStyleWeight.Bold,
                SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright);

        _shadowPaint ??= new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 150),
            TextAlign = SKTextAlign.Center
        };

        _cyanPaint ??= new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 245, 255, 115),
            TextAlign = SKTextAlign.Center
        };

        _magentaPaint ??= new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 0, 180, 95),
            TextAlign = SKTextAlign.Center
        };

        _mainPaint ??= new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            TextAlign = SKTextAlign.Center
        };

        _flashPaint ??= new SKPaint
        {
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };
    }

    private void ConfigureTextPaints(float textSize, float pulse)
    {
        if (_typeface == null)
            return;

        if (_shadowPaint != null)
        {
            _shadowPaint.TextSize = textSize;
            _shadowPaint.Typeface = _typeface;
        }

        if (_cyanPaint != null)
        {
            _cyanPaint.TextSize = textSize;
            _cyanPaint.Typeface = _typeface;
            _cyanPaint.Color = new SKColor(0, 245, 255, (byte)(110 * pulse));
        }

        if (_magentaPaint != null)
        {
            _magentaPaint.TextSize = textSize;
            _magentaPaint.Typeface = _typeface;
            _magentaPaint.Color = new SKColor(255, 0, 180, (byte)(90 * pulse));
        }

        if (_mainPaint != null)
        {
            _mainPaint.TextSize = textSize;
            _mainPaint.Typeface = _typeface;
            _mainPaint.Color = new SKColor(
                (byte)(225 + (30 * pulse)),
                (byte)(245 + (10 * pulse)),
                255,
                255);
        }
    }

    private void Cleanup()
    {
        _shadowPaint?.Dispose();
        _cyanPaint?.Dispose();
        _magentaPaint?.Dispose();
        _mainPaint?.Dispose();
        _flashPaint?.Dispose();

        _shadowPaint = null;
        _cyanPaint = null;
        _magentaPaint = null;
        _mainPaint = null;
        _flashPaint = null;

        _typeface?.Dispose();
        _typeface = null;
    }
}
