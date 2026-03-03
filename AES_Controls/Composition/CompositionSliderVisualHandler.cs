using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using log4net;
using SkiaSharp;
using System.Diagnostics;
using System.Numerics;

namespace AES_Controls.Composition
{
    // A compact visual handler that draws a slider sized to the visual bounds.
    // This is intentionally separate from CompositionCarouselVisualHandler
    // so the carousel visual can remain untouched.
    public class CompositionSliderVisualHandler : CompositionCustomVisualHandler
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(CompositionSliderVisualHandler));

        private Vector2 _visualSize;
        private double _currentIndex = 0.0; // normalized 0..1
        private double _targetIndex = 0.0;
        private double _velocity = 0.0;
        private long _lastTicks = 0;

        private float _sliderTrackHeight = 4.0f;
        // vertical offset is unused because slider always fills bounds

        private readonly SKPaint _paint = new() { IsAntialias = true };
        private SKShader? _trackShader;
        private SKShader? _thumbShader;
        private float _lastSliderW;
        private bool _isPressed;
    private SKColor _playedColor = SKColors.Transparent;
    private bool _smallThumb = false;

        private readonly SKColor _trackTop = SKColor.Parse("#3A3A3A");
        private readonly SKColor _trackBottom = SKColor.Parse("#151515");
        private readonly SKColor _thumbTop = SKColors.White;
        private readonly SKColor _thumbBottom = SKColor.Parse("#EDEDED");

        public override void OnMessage(object message)
        {
            if (message is double d)
            {
                _targetIndex = d;
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
            }
            else if (message is InstantSliderPositionMessage isp)
            {
                _currentIndex = isp.Value;
                _targetIndex = isp.Value;
                _velocity = 0;
                _lastTicks = 0;
                Invalidate();
            }
            else if (message is Vector2 size)
            {
                _visualSize = size;
                Invalidate();
            }
            else if (message is SliderTrackHeightMessage sth)
            {
                _sliderTrackHeight = (float)sth.Value;
                ClearShaders();
                Invalidate();
            }
            else if (message is SliderPressedMessage sp)
            {
                _isPressed = sp.IsPressed;
                Invalidate();
            }
            else if (message is SliderSmallThumbMessage ssm)
            {
                _smallThumb = ssm.IsSmall;
                Invalidate();
            }
            else if (message is PlayedAreaBrushMessage pab)
            {
                _playedColor = pab.Color;
                Invalidate();
            }
        }

        private void ClearShaders()
        {
            _trackShader?.Dispose(); _trackShader = null;
            _thumbShader?.Dispose(); _thumbShader = null;
        }

        public override void OnAnimationFrameUpdate()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastTicks == 0) _lastTicks = now;
            double dt = (double)(now - _lastTicks) / Stopwatch.Frequency;
            _lastTicks = now;
            if (dt > 0.1) dt = 0.1;

            double dist = _targetIndex - _currentIndex;
            double stiffness = 45.0;
            double damping = 2.0 * Math.Sqrt(stiffness) * 1.15;
            _velocity += (dist * stiffness - _velocity * damping) * dt;
            _currentIndex += _velocity * dt;

            // Tighten the snap threshold and ensure we always invalidate if anything changed or velocity is present
            if (Math.Abs(dist) < 0.0001 && Math.Abs(_velocity) < 0.001)
            {
                _currentIndex = _targetIndex;
                _velocity = 0;
            }

            // Always invalidate if we are still far enough or if we have velocity. 
            // Crucially, if we are NOT at target, we should stay in the loop.
            if (Math.Abs(_targetIndex - _currentIndex) > 1e-8 || Math.Abs(_velocity) > 1e-8)
            {
                RegisterForNextAnimationFrameUpdate();
                Invalidate();
            }
            else
            {
                _currentIndex = _targetIndex;
                _velocity = 0;
                _lastTicks = 0;
                Invalidate(); // Ensure final position is drawn
            }
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            Draw(canvas);
        }

        private void Draw(SKCanvas canvas)
        {
            if (_visualSize.X <= 0 || _visualSize.Y <= 0) return;

            // Fill the available height (leave small padding)
            float horizPadding = Math.Clamp(_visualSize.X * 0.02f, 2f, 16f);
            float sliderW = Math.Max(0f, _visualSize.X - horizPadding * 2f);
            float sliderH = Math.Max(12f, _visualSize.Y - 2f);
            float top = Math.Max(0f, (_visualSize.Y - sliderH) / 2f);
            SKRect bounds = new SKRect(horizPadding, top, horizPadding + sliderW, top + sliderH);

            if (sliderW != _lastSliderW) { ClearShaders(); _lastSliderW = sliderW; }

            // Track
            // Respect incoming SliderTrackHeight more directly but ensure it fits inside
            // the available visual height. Previously this was clamped relative to
            // sliderH * 0.7 which prevented larger values (e.g. 15) from taking effect
            // for small control heights. Use sliderH - 4 as a safe maximum so the
            // track can fill most of the control when requested.
            float maxTrackH = Math.Max(1f, sliderH - 4f);
            float trackH = Math.Min(_sliderTrackHeight, maxTrackH);
            float trackY = bounds.MidY;
            SKRect trackRect = new SKRect(bounds.Left, trackY - trackH / 2f, bounds.Right, trackY + trackH / 2f);
            if (_trackShader == null)
            {
                // Use same base colors as the carousel handler for identical appearance
                var c1 = SKColor.Parse("#444444").WithAlpha(240);
                var c2 = SKColor.Parse("#777777").WithAlpha(240);
                _trackShader = SKShader.CreateLinearGradient(new SKPoint(trackRect.Left, trackRect.Top), new SKPoint(trackRect.Left, trackRect.Bottom), new[] { c1, c2 }, null, SKShaderTileMode.Clamp);
            }

            // Prepare thumb geometry early so the played area can be drawn behind the thumb.
            float thumbH = Math.Max(8f, sliderH * 0.6f);
            float thumbW = Math.Max(12f, thumbH * 2.0f * 1.3f);
            if (_smallThumb) thumbW *= 0.5f;
            float pct = (float)Math.Clamp(_currentIndex, 0.0, 1.0);
            float thumbX = bounds.Left + (thumbW / 2f) + pct * (bounds.Width - thumbW);
            SKRect thumbRect = new SKRect(thumbX - thumbW / 2f, trackY - thumbH / 2f, thumbX + thumbW / 2f, trackY + thumbH / 2f);

            // Subtle shadow under thumb
            _paint.Style = SKPaintStyle.Fill; _paint.Color = SKColors.Black.WithAlpha(30); var shadow = thumbRect; shadow.Offset(0, thumbH * 0.06f); canvas.DrawRoundRect(shadow, thumbH * 0.5f, thumbH * 0.5f, _paint);

            // Glow when pressed (draw behind the thumb with blur)
            if (_isPressed)
            {
                // Match carousel: white glow behind thumb using blur filter
                _paint.Style = SKPaintStyle.Fill;
                _paint.Color = SKColors.White.WithAlpha(120);
                _paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5);
                var glow = thumbRect; glow.Inflate(4, 4);
                canvas.DrawRoundRect(glow, 10, 10, _paint);
                _paint.MaskFilter = null;
            }

            // Draw track base with shader
            _paint.Style = SKPaintStyle.Fill; _paint.Shader = _trackShader; _paint.Color = SKColors.White; canvas.DrawRoundRect(trackRect, trackH / 2f, trackH / 2f, _paint); _paint.Shader = null;
            // subtle top highlight
            _paint.Style = SKPaintStyle.Fill; _paint.Color = SKColors.White.WithAlpha(0x1E); // ~12%
            var topHighlight = new SKRect(trackRect.Left + 1, trackRect.Top + 1, trackRect.Right - 1, trackRect.Top + trackH * 0.36f);
            canvas.DrawRoundRect(topHighlight, trackH * 0.36f, trackH * 0.36f, _paint);
            // subtle bottom overlay to deepen track center
            _paint.Color = SKColors.Black.WithAlpha(0x12); // ~7%
            var bottomOverlay = new SKRect(trackRect.Left + 1, trackRect.Bottom - trackH * 0.32f, trackRect.Right - 1, trackRect.Bottom - 1);
            canvas.DrawRoundRect(bottomOverlay, trackH * 0.32f, trackH * 0.32f, _paint);

            // Played area: draw from left edge of track up to current thumb center using provided color (behind thumb)
            if (_playedColor.Alpha != 0)
            {
                float playedRight = thumbX; // center of thumb
                var playedRect = new SKRect(trackRect.Left, trackRect.Top, Math.Min(playedRight, trackRect.Right), trackRect.Bottom);
                if (playedRect.Width > 1f)
                {
                    _paint.Style = SKPaintStyle.Fill;
                    _paint.Shader = null;
                    _paint.Color = _playedColor;
                    canvas.DrawRoundRect(playedRect, trackH / 2f, trackH / 2f, _paint);
                }
            }

            // Outer rim: light then dark for definition
            _paint.Style = SKPaintStyle.Stroke;
            _paint.StrokeWidth = Math.Max(1f, trackH * 0.08f);
            _paint.Color = SKColors.White.WithAlpha(0x20);
            canvas.DrawRoundRect(trackRect, trackH / 2f, trackH / 2f, _paint);
            _paint.StrokeWidth = Math.Max(1f, trackH * 0.045f);
            _paint.Color = SKColors.Black.WithAlpha(0x66);
            canvas.DrawRoundRect(trackRect, trackH / 2f, trackH / 2f, _paint);

            // Thumb fill (solid white)
            _paint.Style = SKPaintStyle.Fill;
            _paint.Shader = null;
            _paint.Color = SKColors.White;
            canvas.DrawRoundRect(thumbRect, thumbH * 0.5f, thumbH * 0.5f, _paint);

            // Gloss cap (subtle)
            _paint.Style = SKPaintStyle.Fill; _paint.Color = SKColors.White.WithAlpha(0xE6); var hl = new SKRect(thumbRect.Left + 2, thumbRect.Top + 2, thumbRect.Right - 2, thumbRect.Top + thumbH * 0.42f); canvas.DrawRoundRect(hl, thumbH * 0.42f, thumbH * 0.42f, _paint);

            // Outline
            _paint.Style = SKPaintStyle.Stroke; _paint.StrokeWidth = Math.Max(1f, thumbH * 0.06f); _paint.Color = SKColors.Black.WithAlpha(0x88); canvas.DrawRoundRect(thumbRect, thumbH * 0.5f, thumbH * 0.5f, _paint);
        }
    }
}
