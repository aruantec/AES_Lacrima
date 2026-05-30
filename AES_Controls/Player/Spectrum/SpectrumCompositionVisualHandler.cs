using System.Diagnostics;
using System.Numerics;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Player.Spectrum;

internal sealed class SpectrumCompositionVisualHandler : CompositionCustomVisualHandler
{
    private const int FixedRenderWidth = 960;
    private const int FixedRenderHeight = 192;
    private const int MaxBarCount = 192;
    private const float DefaultPeakThicknessPixels = 2.0f;

    private Vector2 _visualSize;
    private AvaloniaList<double>? _spectrumSource;
    private bool _isVisible = true;
    private bool _isPaused;
    private bool _animationLoopActive;
    private long _lastFrameTicks;

    private float _barWidth = 4f;
    private float _barSpacing = 2f;
    private float _blockHeight = 8f;
    private float _peakThickness = 2f;
    private bool _useDeltaTime = true;
    private double _attackLerp = 0.42;
    private double _releaseLerp = 0.38;
    private double _peakDecay = 0.85;
    private double _prePowAttackAlpha = 0.90;
    private double _maxRiseFraction = 0.55;
    private double _maxRiseAbsolute = 0.05;
    private float _globalOpacity = 1f;

    private SKColor[] _gradientColors =
    [
        new SKColor(0x00, 0xCC, 0xFF),
        new SKColor(0x33, 0x33, 0xFF),
        new SKColor(0xCC, 0x00, 0xCC),
        new SKColor(0xFF, 0x00, 0x4D),
        new SKColor(0xFF, 0xB3, 0x00)
    ];

    private double[] _displayedBarLevels = [];
    private double[] _peakLevels = [];
    private double[] _rawSmoothed = [];
    private float[] _spectrumSnapshot = [];
    private int[] _sampleMap = [];
    private double _globalMax = 1e-6;
    private bool _isFirstFrame = true;
    private int _spectrumCount;
    private int _sampleMapTargetCount = -1;
    private int _sampleMapSourceCount = -1;
    private bool _isAnimating = true;

    private SKSurface? _offscreenSurface;
    private SKImage? _frameImage;
    private SKPaint? _barPaint;
    private SKPaint? _peakPaint;
    private SKPaint? _blitPaint;

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                Cleanup();
                return;
            case Vector2 size:
                _visualSize = size;
                EnsureOffscreenSurface();
                UpdateAnimationLoopState();
                Invalidate();
                return;
            case SpectrumSourceMessage source:
                _spectrumSource = source.Source;
                _isFirstFrame = true;
                _isAnimating = true;
                UpdateAnimationLoopState();
                return;
            case SpectrumConfigMessage config:
                _barWidth = Math.Max(0f, config.BarWidth);
                _barSpacing = Math.Max(0f, config.BarSpacing);
                _blockHeight = Math.Max(1f, config.BlockHeight);
                _peakThickness = Math.Max(1f, config.PeakThickness);
                _useDeltaTime = config.UseDeltaTime;
                _attackLerp = config.AttackLerp;
                _releaseLerp = config.ReleaseLerp;
                _peakDecay = config.PeakDecay;
                _prePowAttackAlpha = config.PrePowAttackAlpha;
                _maxRiseFraction = config.MaxRiseFraction;
                _maxRiseAbsolute = config.MaxRiseAbsolute;
                _sampleMapTargetCount = -1;
                _isAnimating = true;
                UpdateAnimationLoopState();
                Invalidate();
                return;
            case SpectrumGradientMessage gradient:
                if (gradient.Colors.Length == 5)
                    _gradientColors = gradient.Colors;
                Invalidate();
                return;
            case SpectrumRuntimeMessage runtime:
                _isVisible = runtime.IsVisible;
                _isPaused = runtime.IsPaused;
                if (_isPaused || !_isVisible)
                {
                    _isAnimating = false;
                    _animationLoopActive = false;
                    return;
                }

                _isAnimating = true;
                UpdateAnimationLoopState();
                return;
            case SpectrumOpacityMessage opacity:
                _globalOpacity = Math.Clamp(opacity.Opacity, 0f, 1f);
                if (_blitPaint != null)
                    _blitPaint.Color = _blitPaint.Color.WithAlpha((byte)Math.Clamp((int)(255f * _globalOpacity), 0, 255));
                Invalidate();
                return;
            case SpectrumWakeMessage:
                if (_isPaused || !_isVisible)
                    return;

                _isAnimating = true;
                UpdateAnimationLoopState();
                return;
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        if (!_animationLoopActive)
            return;

        try
        {
            long nowTicks = Stopwatch.GetTimestamp();
            float delta = 1f / 120f;
            if (_lastFrameTicks != 0)
            {
                delta = (float)((double)(nowTicks - _lastFrameTicks) / Stopwatch.Frequency);
                if (delta <= 0f)
                    delta = 1f / 120f;
                if (delta > 0.1f)
                    delta = 0.1f;
            }

            _lastFrameTicks = nowTicks;

            SnapshotSpectrum();
            int targetCount = GetTargetBarCount();
            _isAnimating = UpdatePhysics(targetCount, delta);
            RenderBarsToOffscreen(targetCount);
            Invalidate();
        }
        finally
        {
            if (_animationLoopActive && _isVisible && !_isPaused)
                RegisterForNextAnimationFrameUpdate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0 || _frameImage == null || _blitPaint == null)
            return;

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        var dest = SKRect.Create(0, 0, _visualSize.X, _visualSize.Y);
        var source = SKRect.Create(0, 0, FixedRenderWidth, FixedRenderHeight);
        canvas.DrawImage(_frameImage, source, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), _blitPaint);
    }

    private void UpdateAnimationLoopState()
    {
        bool shouldRun = _isVisible && !_isPaused && _visualSize.X > 0 && _visualSize.Y > 0;
        if (shouldRun && !_animationLoopActive)
        {
            _animationLoopActive = true;
            _lastFrameTicks = 0;
            RegisterForNextAnimationFrameUpdate();
            return;
        }

        if (!shouldRun)
            _animationLoopActive = false;
    }

    private void EnsureOffscreenSurface()
    {
        if (_offscreenSurface != null)
            return;

        var info = new SKImageInfo(FixedRenderWidth, FixedRenderHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        _offscreenSurface = SKSurface.Create(info);
        _barPaint ??= new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        _peakPaint ??= new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(255, 255, 255, 242) };
        _blitPaint ??= new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Low };
    }

    private void RenderBarsToOffscreen(int targetCount)
    {
        if (_offscreenSurface == null || targetCount <= 0)
            return;

        var canvas = _offscreenSurface.Canvas;
        canvas.Clear(SKColors.Transparent);

        double denom = (_globalMax * 1.1) + 1e-9;
        float width = FixedRenderWidth;
        float height = FixedRenderHeight;
        float step = width / targetCount;

        for (int i = 0; i < targetCount; i++)
        {
            float x0 = (i * step) + (_barSpacing * 0.5f);
            float x1 = Math.Min(width, x0 + _barWidth);
            if (x1 <= x0)
                continue;

            float barNorm = (float)Math.Clamp(_displayedBarLevels[i] / denom, 0.0, 1.0);
            float peakNorm = i < _peakLevels.Length
                ? (float)Math.Clamp(_peakLevels[i] / denom, 0.0, 1.0)
                : barNorm;
            float barTop = height - (barNorm * height);
            float peakTop = height - (peakNorm * height);
            SKColor color = GetGradientColor(i, targetCount);

            DrawBar(canvas, x0, x1, barTop, height, color, height);

            float peakBottom = Math.Clamp(peakTop + _peakThickness, 0f, height);
            if (peakBottom > peakTop && _peakPaint != null)
            {
                _peakPaint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp((int)(242f * _globalOpacity), 0, 255));
                canvas.DrawRect(new SKRect(x0, Math.Clamp(peakTop, 0f, height), x1, peakBottom), _peakPaint);
            }
        }

        _frameImage?.Dispose();
        _frameImage = _offscreenSurface.Snapshot();
    }

    private void SnapshotSpectrum()
    {
        var spectrum = _spectrumSource;
        if (spectrum == null)
        {
            _spectrumCount = 0;
            return;
        }

        lock (spectrum)
        {
            int count = spectrum.Count;
            if (_spectrumSnapshot.Length < count)
                _spectrumSnapshot = new float[count];

            for (int i = 0; i < count; i++)
                _spectrumSnapshot[i] = (float)spectrum[i];

            _spectrumCount = count;
        }
    }

    private bool UpdatePhysics(int targetCount, float delta)
    {
        if (_displayedBarLevels.Length != targetCount)
        {
            _displayedBarLevels = new double[targetCount];
            _peakLevels = new double[targetCount];
            _rawSmoothed = new double[targetCount];
            _sampleMap = new int[targetCount];
            _sampleMapTargetCount = -1;
            _isFirstFrame = true;
        }

        if (_sampleMapTargetCount != targetCount || _sampleMapSourceCount != _spectrumCount)
        {
            if (_sampleMap.Length < targetCount)
                _sampleMap = new int[targetCount];

            int srcCount = _spectrumCount;
            for (int i = 0; i < targetCount; i++)
            {
                _sampleMap[i] = srcCount > 0
                    ? Math.Min(srcCount - 1, (int)((i / (double)targetCount) * srcCount))
                    : 0;
            }

            _sampleMapTargetCount = targetCount;
            _sampleMapSourceCount = srcCount;
        }

        float timeFactor = _useDeltaTime ? delta * 60f : 1.0f;
        double adjAttack = 1.0 - Math.Pow(1.0 - _attackLerp, timeFactor);
        double adjRelease = 1.0 - Math.Pow(1.0 - _releaseLerp, timeFactor);
        double adjPeakDecay = 1.0 - Math.Pow(1.0 - _peakDecay, timeFactor);

        double observedMax = 0.0;
        bool hasVisibleActivity = false;

        for (int i = 0; i < targetCount; i++)
        {
            double src = _spectrumCount > 0 ? _spectrumSnapshot[_sampleMap[i]] : 0.0;

            if (double.IsNaN(src) || double.IsInfinity(src) || src < 0.0001)
                src = 0.0;

            _rawSmoothed[i] += (src - _rawSmoothed[i]) * Math.Min(1.0, _prePowAttackAlpha * timeFactor);
            double target = _rawSmoothed[i];

            if (_isFirstFrame && target > 0)
            {
                _displayedBarLevels[i] = target;
                _peakLevels[i] = target;
            }
            else
            {
                double effectiveTarget = target > _displayedBarLevels[i]
                    ? Math.Min(target, _displayedBarLevels[i] + Math.Max(target * _maxRiseFraction, _maxRiseAbsolute) * timeFactor)
                    : target;

                double lerp = effectiveTarget < _displayedBarLevels[i] ? adjRelease : adjAttack;
                _displayedBarLevels[i] += (effectiveTarget - _displayedBarLevels[i]) * lerp;

                if (_displayedBarLevels[i] > _peakLevels[i])
                    _peakLevels[i] = _displayedBarLevels[i];
                else
                    _peakLevels[i] += (_displayedBarLevels[i] - _peakLevels[i]) * adjPeakDecay;
            }

            if (_displayedBarLevels[i] > observedMax)
                observedMax = _displayedBarLevels[i];

            if (_displayedBarLevels[i] > 0.001 || _peakLevels[i] > 0.001 || _rawSmoothed[i] > 0.001)
                hasVisibleActivity = true;
        }

        if (observedMax > 0.001)
        {
            if (_isFirstFrame)
            {
                _globalMax = observedMax;
                _isFirstFrame = false;
            }
            else
            {
                double lerpSpeed = observedMax > _globalMax ? 0.15 : 0.01;
                _globalMax += (observedMax - _globalMax) * Math.Min(1.0, lerpSpeed * timeFactor);
            }
        }

        if (_globalMax < 0.05)
            _globalMax = 0.05;

        return hasVisibleActivity || observedMax > 0.001 || _globalMax > 0.051;
    }

    private int GetTargetBarCount()
    {
        double step = Math.Max(1.0, _barWidth + _barSpacing);
        int rawCount = Math.Max(1, (int)(FixedRenderWidth / step));
        return Math.Min(MaxBarCount, rawCount);
    }

    private void DrawBar(SKCanvas canvas, float left, float right, float top, float bottom, SKColor baseColor, float totalHeight)
    {
        if (_barPaint == null || bottom <= top)
            return;

        float blockHeight = Math.Max(1f, _blockHeight);
        float gapHeight = blockHeight * 0.15f;
        float currentBottom = bottom;

        while (currentBottom > top)
        {
            float blockTop = Math.Max(top, currentBottom - blockHeight);
            float visibleBottom = Math.Max(blockTop, currentBottom - gapHeight);
            if (visibleBottom > blockTop)
            {
                float centerY = (blockTop + visibleBottom) * 0.5f;
                float v = Math.Clamp((totalHeight - centerY) / totalHeight, 0f, 1f);
                float alpha = 0.65f + (0.35f * v);
                _barPaint.Color = baseColor.WithAlpha((byte)Math.Clamp((int)(baseColor.Alpha * alpha * _globalOpacity), 0, 255));
                canvas.DrawRect(new SKRect(left, blockTop, right, visibleBottom), _barPaint);
            }

            currentBottom = blockTop;
        }
    }

    private SKColor GetGradientColor(int index, int count)
    {
        float u = count <= 1 ? 0f : index / (float)(count - 1);

        if (u < 0.25f)
            return Lerp(_gradientColors[0], _gradientColors[1], u / 0.25f);
        if (u < 0.5f)
            return Lerp(_gradientColors[1], _gradientColors[2], (u - 0.25f) / 0.25f);
        if (u < 0.75f)
            return Lerp(_gradientColors[2], _gradientColors[3], (u - 0.5f) / 0.25f);

        return Lerp(_gradientColors[3], _gradientColors[4], (u - 0.75f) / 0.25f);
    }

    private static SKColor Lerp(SKColor from, SKColor to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(from.Red + ((to.Red - from.Red) * t)),
            (byte)(from.Green + ((to.Green - from.Green) * t)),
            (byte)(from.Blue + ((to.Blue - from.Blue) * t)),
            (byte)(from.Alpha + ((to.Alpha - from.Alpha) * t)));
    }

    private void Cleanup()
    {
        _animationLoopActive = false;
        _spectrumSource = null;
        _displayedBarLevels = [];
        _peakLevels = [];
        _rawSmoothed = [];
        _spectrumSnapshot = [];
        _sampleMap = [];

        _frameImage?.Dispose();
        _frameImage = null;
        _offscreenSurface?.Dispose();
        _offscreenSurface = null;

        _barPaint?.Dispose();
        _peakPaint?.Dispose();
        _blitPaint?.Dispose();
        _barPaint = null;
        _peakPaint = null;
        _blitPaint = null;
    }
}
