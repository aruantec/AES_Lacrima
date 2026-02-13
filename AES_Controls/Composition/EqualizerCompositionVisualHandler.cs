using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;
using System.Numerics;

namespace AES_Controls.Composition;

/// <summary>
/// Represents a single equalizer band with a gain value and optional
/// frequency label.
/// </summary>
internal record EqualizerBand(float Gain, string? Frequency);

/// <summary>
/// Message sent to the visual handler containing the list of bands to render.
/// </summary>
internal record EqualizerBandsMessage(IReadOnlyList<EqualizerBand> Bands);

/// <summary>
/// Message that carries a background color for the visual.
/// </summary>
internal record EqualizerBackgroundMessage(SKColor Color);

/// <summary>
/// Message that carries text styling information (color, size and margin).
/// </summary>
internal record EqualizerTextStyleMessage(SKColor Color, float FontSize, float Margin);

/// <summary>
/// Message that indicates which band is active (user interaction) and whether
/// the pointer is down.
/// </summary>
internal record EqualizerActiveBandMessage(int ActiveIndex, bool IsDown);

/// <summary>
/// Message that sets the render margin used by the visual.
/// </summary>
internal record EqualizerRenderMarginMessage(float Margin);

/// <summary>
/// Message that sets the label margins (left, top, right, bottom) used to
/// position left-side labels and frequency labels.
/// </summary>
internal record EqualizerLabelMarginMessage(float Left, float Top, float Right, float Bottom);

/// <summary>
/// Message that sets the explicit label gap between the left label block and
/// the sliders.
/// </summary>
internal record EqualizerLabelGapMessage(float Gap);

/// <summary>
/// Message that applies a global opacity multiplier (0..1) to the visual.
/// </summary>
internal record EqualizerGlobalOpacityMessage(float Opacity);

/// <summary>
/// Composition visual handler responsible for drawing the equalizer UI using
/// SkiaSharp. It receives messages from the control and invalidates the
/// composition surface when visual state changes.
/// </summary>
public class EqualizerCompositionVisualHandler : CompositionCustomVisualHandler
{
    private const float MinGain = -10f;
    private const float MaxGain = 10f;

    private Vector2 _visualSize;
    private List<EqualizerBand> _bands = new();
    private SKColor _backgroundColor = SKColors.Black;

    private int _activeBandIndex = -1;
    private bool _isPointerDown;

    private float _renderMargin = 0f;

    // Label margins and gap
    private float _labelMarginLeft = 8f;
    private float _labelMarginTop = 0f;
    private float _labelMarginRight = 0f;
    private float _labelMarginBottom = 0f;
    private float _labelGap = 40f;

    // Text margin (single value forwarded as Margin)
    private float _textMargin = 6f;
    private float _globalOpacity = 1f;

    // Paints
    private SKPaint? _trackPaint = new() { IsAntialias = true, StrokeWidth = 6f, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke, Color = new SKColor(90, 140, 200, 180) };
    private SKPaint? _trackFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(90, 140, 200, 90) };
    private SKPaint? _activePaint = new() { IsAntialias = true, StrokeWidth = 4f, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke, Color = new SKColor(100, 180, 255, 220) };
    private SKPaint? _knobPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(100, 180, 255, 255) };
    private SKPaint? _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(80, 150, 220, 80) };
    private SKPaint? _textPaint = new() { IsAntialias = true, TextSize = 12f, Color = new SKColor(210, 210, 210, 220), TextAlign = SKTextAlign.Center };
    private SKPaint? _knobGlowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 20f, Color = new SKColor(80, 170, 255, 90), StrokeCap = SKStrokeCap.Round };
    private SKPaint? _knobInnerGlowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10f, Color = new SKColor(120, 200, 255, 140), StrokeCap = SKStrokeCap.Round };

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
            case EqualizerBandsMessage b:
                _bands = b.Bands.ToList();
                Invalidate();
                return;
            case EqualizerBackgroundMessage bg:
                _backgroundColor = bg.Color;
                Invalidate();
                return;
            case EqualizerTextStyleMessage ts:
                if (_textPaint != null)
                {
                    _textPaint.Color = ts.Color;
                    _textPaint.TextSize = ts.FontSize;
                    _textMargin = ts.Margin;
                }

    /// <summary>
    /// Handles incoming messages sent from the control. Supported message types
    /// include size updates, band lists, styling messages and interaction
    /// state changes. Passing <c>null</c> triggers cleanup of native resources.
    /// </summary>
    /// <param name="message">The message object sent by the control.</param>
                Invalidate();
                return;
            case EqualizerActiveBandMessage ab:
                _activeBandIndex = ab.ActiveIndex;
                _isPointerDown = ab.IsDown;
                Invalidate();
                return;
            case EqualizerRenderMarginMessage rm:
                _renderMargin = rm.Margin;
                Invalidate();
                return;
            case EqualizerLabelMarginMessage lm:
                _labelMarginLeft = lm.Left;
                _labelMarginTop = lm.Top;
                _labelMarginRight = lm.Right;
                _labelMarginBottom = lm.Bottom;
                Invalidate();
                return;
            case EqualizerLabelGapMessage lg:
                _labelGap = lg.Gap;
                Invalidate();
                return;
            case EqualizerGlobalOpacityMessage go:
                _globalOpacity = Math.Clamp(go.Opacity, 0f, 1f);
                Invalidate();
                return;
        }

    /// <summary>
    /// Called to render the visual using the provided <see cref="ImmediateDrawingContext"/>.
    /// This method uses SkiaSharp to draw tracks, knobs, labels and fills for
    /// the equalizer bands.
    /// </summary>
    /// <param name="context">The immediate drawing context from the composition system.</param>
    }

    /// <summary>
    /// Releases native resources and disposes Skia paints used by the visual.
    /// </summary>

    public override void OnRender(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null || _trackPaint == null || _activePaint == null || _knobPaint == null || _fillPaint == null || _textPaint == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        if (_backgroundColor.Alpha > 0) canvas.Clear(_backgroundColor);
        // Apply global opacity (from control) by multiplying alpha on paints
        var globalAlpha = (byte)Math.Clamp((int)(_globalOpacity * 255f), 0, 255);
        // Update paint alpha channels
        if (_trackPaint != null) _trackPaint.Color = new SKColor(_trackPaint.Color.Red, _trackPaint.Color.Green, _trackPaint.Color.Blue, globalAlpha);
        if (_trackFillPaint != null) _trackFillPaint.Color = new SKColor(_trackFillPaint.Color.Red, _trackFillPaint.Color.Green, _trackFillPaint.Color.Blue, globalAlpha);
        if (_activePaint != null) _activePaint.Color = new SKColor(_activePaint.Color.Red, _activePaint.Color.Green, _activePaint.Color.Blue, globalAlpha);
        if (_knobPaint != null) _knobPaint.Color = new SKColor(_knobPaint.Color.Red, _knobPaint.Color.Green, _knobPaint.Color.Blue, globalAlpha);
        if (_fillPaint != null) _fillPaint.Color = new SKColor(_fillPaint.Color.Red, _fillPaint.Color.Green, _fillPaint.Color.Blue, (byte)(globalAlpha * 0.4f));
        if (_textPaint != null) _textPaint.Color = new SKColor(_textPaint.Color.Red, _textPaint.Color.Green, _textPaint.Color.Blue, globalAlpha);

        // Basic paddings (visual area in control space)
        float leftPadding = 20f + _renderMargin;
        float rightPadding = 20f + _renderMargin;
        float topPadding = 14f + _renderMargin;
        float bottomPadding = 26f + _renderMargin;

        float width = Math.Max(1f, _visualSize.X - _renderMargin * 2f);
        float height = Math.Max(1f, _visualSize.Y - _renderMargin * 2f);

        if (_bands.Count == 0 || _textPaint == null) return;

        // Measure left label block width (use left-aligned labels: 10dB, 0dB, -10dB)
        var leftLabels = new[] { "10dB", "0dB", "-10dB" };
        float maxLabelWidth = 0f;
        using var leftPaint = new SKPaint { IsAntialias = true, Color = _textPaint.Color, TextSize = _textPaint.TextSize, TextAlign = SKTextAlign.Right };
        foreach (var s in leftLabels)
        {
            var w = leftPaint.MeasureText(s);
            if (w > maxLabelWidth) maxLabelWidth = w;
        }

        // Left label block right edge X
        float labelBlockRightX = leftPadding + _labelMarginLeft + maxLabelWidth;
        // After label block add explicit gap
        float sliderStartX = labelBlockRightX + _labelGap;

        // Reserve space for bottom frequency labels: compute effective trackBottom above them
        float freqTextHeight = _textPaint.TextSize;
        float effectiveBottomPadding = bottomPadding + freqTextHeight + _textMargin;

        float trackTop = topPadding + _labelMarginTop;
        float trackBottom = Math.Max(trackTop + 10f, height - effectiveBottomPadding - _labelMarginBottom);

        int count = _bands.Count;
        // Measure maximum bottom frequency label width so we can reserve half of it on both sides
        float maxFreqLabelWidth = 0f;
        foreach (var b in _bands)
        {
            var f = b.Frequency ?? string.Empty;
            if (string.IsNullOrWhiteSpace(f)) continue;
            var w = _textPaint.MeasureText(f);
            if (w > maxFreqLabelWidth) maxFreqLabelWidth = w;
        }
        float maxFreqHalf = maxFreqLabelWidth * 0.5f;
        // Define slider area so first and last slider centers have room for half label width
        float leftSlotStart = sliderStartX + maxFreqHalf;
        float rightSlotEnd = width - rightPadding - maxFreqHalf;
        float availableWidth = Math.Max(1f, rightSlotEnd - leftSlotStart);
        float step = count > 1 ? availableWidth / (count - 1) : 0f;
        float knobRadius = Math.Clamp(step * 0.18f, 6f, 12f);

        var points = new SKPoint[count];
        for (int i = 0; i < count; i++)
        {
            float x = sliderStartX + step * i;
            float gain = Math.Clamp(_bands[i].Gain, MinGain, MaxGain);
            float t = (gain - MinGain) / (MaxGain - MinGain);
            float y = trackBottom - t * (trackBottom - trackTop);
            points[i] = new SKPoint(x, y);
        }

        // Draw grid fill and connecting line
        if (count > 1)
        {
            using var fillPath = new SKPath();
            fillPath.MoveTo(points[0]);
            for (int i = 1; i < points.Length; i++) fillPath.LineTo(points[i]);
            fillPath.LineTo(points[^1].X, trackBottom);
            fillPath.LineTo(points[0].X, trackBottom);
            fillPath.Close();
            canvas.DrawPath(fillPath, _fillPaint);

            using var linePath = new SKPath();
            linePath.MoveTo(points[0]);
            for (int i = 1; i < points.Length; i++) linePath.LineTo(points[i]);
            using var linePaint = new SKPaint { IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke, Color = new SKColor(120, 190, 255, 220) };
            canvas.DrawPath(linePath, linePaint);
        }

        // Draw tracks, knobs and bottom frequency texts
        for (int i = 0; i < count; i++)
        {
            var p = points[i];
            // Track
            canvas.DrawLine(p.X, trackTop, p.X, trackBottom, _trackPaint);
            // Bottom cap
            if (_trackFillPaint != null) canvas.DrawCircle(p.X, trackBottom, (_trackPaint?.StrokeWidth ?? 6f) / 2f, _trackFillPaint);
            // Active segment
            canvas.DrawLine(p.X, trackBottom, p.X, p.Y, _activePaint);
            // Glow when active
            if (_isPointerDown && i == _activeBandIndex)
            {
                if (_knobGlowPaint != null)
                {
                    try { _knobGlowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f); } catch { _knobGlowPaint.MaskFilter = null; }
                    canvas.DrawCircle(p.X, p.Y, knobRadius + 2f, _knobGlowPaint);
                }
                if (_knobInnerGlowPaint != null)
                {
                    try { _knobInnerGlowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f); } catch { _knobInnerGlowPaint.MaskFilter = null; }
                    canvas.DrawCircle(p.X, p.Y, knobRadius + 1f, _knobInnerGlowPaint);
                }
            }
            // Knob
            canvas.DrawCircle(p.X, p.Y, knobRadius, _knobPaint);

            // Bottom frequency label centered under slider
            var freq = _bands[i].Frequency ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(freq))
            {
                float freqX = p.X; // p.X was computed so it stays within leftSlotStart..rightSlotEnd
                float freqY = trackBottom + _textMargin + (_textPaint?.TextSize ?? 12f);
                canvas.DrawText(freq, freqX, freqY, _textPaint);
            }
        }

        if (_textPaint == null) return;
        // Draw left-side vertical labels forming the L
        // Draw left labels using a temporary right-aligned paint
        using (var lp = new SKPaint { IsAntialias = true, Color = _textPaint.Color, TextSize = _textPaint.TextSize, TextAlign = SKTextAlign.Right })
        {
            float baseX = leftPadding + _labelMarginLeft + maxLabelWidth; // right edge for right-aligned labels
            float halfText = lp.TextSize * 0.5f;
            float maxYpos = trackTop + halfText;
            float midYpos = (trackTop + trackBottom) * 0.5f + halfText * 0.2f;
            float minYpos = trackBottom + halfText;
            canvas.DrawText("10dB", baseX, maxYpos, lp);
            canvas.DrawText("0dB", baseX, midYpos, lp);
            canvas.DrawText("-10dB", baseX, minYpos, lp);
        }
    }

    private void Cleanup()
    {
        _bands.Clear();
        _trackPaint?.Dispose();
        _trackFillPaint?.Dispose();
        _activePaint?.Dispose();
        _knobPaint?.Dispose();
        _fillPaint?.Dispose();
        _textPaint?.Dispose();
        _knobGlowPaint?.Dispose();
        _knobInnerGlowPaint?.Dispose();
        _trackPaint = null;
        _trackFillPaint = null;
        _activePaint = null;
        _knobPaint = null;
        _fillPaint = null;
        _textPaint = null;
    }
}