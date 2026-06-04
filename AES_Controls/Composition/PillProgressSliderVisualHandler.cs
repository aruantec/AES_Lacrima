using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;
using System.Numerics;

namespace AES_Controls.Composition
{
    public class PillProgressSliderVisualHandler : CompositionCustomVisualHandler
    {
        private Vector2 _visualSize;
        private double _progress;
        private float _trackHeight = 20f;
        private SKColor _borderColor = SKColors.White;
        private SKColor _fillColor = SKColor.Parse("#F5F5F5");
        private string _labelText = string.Empty;
        private float _labelFontSize;
        private SKColor _labelColor = SKColor.Parse("#E6FFFFFF");
        private SKColor _labelOverFillColor = SKColor.Parse("#1A1A1A");
        private bool _showPositionLabel;

        private readonly SKPaint _paint = new() { IsAntialias = true };
        private readonly SKFont _font = new();

        public override void OnMessage(object message)
        {
            switch (message)
            {
                case double d:
                    _progress = d;
                    Invalidate();
                    break;
                case InstantSliderPositionMessage isp:
                    _progress = isp.Value;
                    Invalidate();
                    break;
                case Vector2 size:
                    _visualSize = size;
                    Invalidate();
                    break;
                case PillTrackHeightMessage th:
                    _trackHeight = th.Value;
                    Invalidate();
                    break;
                case PillBorderColorMessage bc:
                    _borderColor = bc.Color;
                    Invalidate();
                    break;
                case PillFillColorMessage fc:
                    _fillColor = fc.Color;
                    Invalidate();
                    break;
                case PillLabelTextMessage lt:
                    _labelText = lt.Text ?? string.Empty;
                    Invalidate();
                    break;
                case PillLabelFontSizeMessage lfs:
                    _labelFontSize = lfs.Value;
                    Invalidate();
                    break;
                case PillLabelColorMessage lc:
                    _labelColor = lc.Color;
                    Invalidate();
                    break;
                case PillLabelOverFillColorMessage lof:
                    _labelOverFillColor = lof.Color;
                    Invalidate();
                    break;
                case PillShowPositionLabelMessage spl:
                    _showPositionLabel = spl.Value;
                    Invalidate();
                    break;
            }
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null)
                return;

            using var lease = leaseFeature.Lease();
            Draw(lease.SkCanvas);
        }

        private void Draw(SKCanvas canvas)
        {
            if (_visualSize.X <= 1f || _visualSize.Y <= 1f)
                return;

            const float horizPadding = 2f;
            const float borderWidth = 1.5f;

            float trackH = Math.Clamp(_trackHeight, 8f, Math.Max(8f, _visualSize.Y - 4f));
            float innerInset = trackH * 0.125f;
            float top = (_visualSize.Y - trackH) / 2f;
            var outer = new SKRect(horizPadding, top, _visualSize.X - horizPadding, top + trackH);
            float outerRadius = trackH / 2f;

            _paint.Style = SKPaintStyle.Stroke;
            _paint.StrokeWidth = borderWidth;
            _paint.Color = _borderColor;
            canvas.DrawRoundRect(outer, outerRadius, outerRadius, _paint);

            var inner = new SKRect(
                outer.Left + borderWidth / 2f + innerInset,
                outer.Top + borderWidth / 2f + innerInset,
                outer.Right - borderWidth / 2f - innerInset,
                outer.Bottom - borderWidth / 2f - innerInset);

            if (inner.Width <= 1f || inner.Height <= 1f)
                return;

            float pct = (float)Math.Clamp(_progress, 0.0, 1.0);
            float innerRadius = inner.Height / 2f;
            float fillWidth = pct <= 0f ? 0f : Math.Min(Math.Max(innerRadius * 2f, inner.Width * pct), inner.Width);
            var fillRect = new SKRect(inner.Left, inner.Top, inner.Left + fillWidth, inner.Bottom);

            if (fillWidth > 0f)
                DrawGlowFill(canvas, inner, fillRect, innerRadius);

            if (!_showPositionLabel || string.IsNullOrEmpty(_labelText))
                return;

            DrawLabel(canvas, inner, fillRect, innerRadius, fillWidth);
        }

        private void DrawGlowFill(SKCanvas canvas, SKRect inner, SKRect fillRect, float innerRadius)
        {
            canvas.Save();
            using (var innerClip = new SKPath())
            {
                innerClip.AddRoundRect(inner, innerRadius, innerRadius);
                canvas.ClipPath(innerClip, SKClipOperation.Intersect, antialias: true);
            }

            var glowRect = fillRect;
            glowRect.Inflate(innerRadius * 0.12f, innerRadius * 0.08f);

            _paint.Style = SKPaintStyle.Fill;
            _paint.Shader = null;
            _paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, innerRadius * 0.45f);
            _paint.Color = _fillColor.WithAlpha((byte)Math.Clamp(_fillColor.Alpha * 0.55f, 24, 140));
            canvas.DrawRoundRect(glowRect, innerRadius, innerRadius, _paint);
            _paint.MaskFilter = null;

            var topColor = _fillColor.WithAlpha((byte)Math.Clamp(_fillColor.Alpha + 30, 0, 220));
            var bottomColor = _fillColor.WithAlpha((byte)Math.Clamp(_fillColor.Alpha * 0.65f, 20, 180));
            using (var fillShader = SKShader.CreateLinearGradient(
                       new SKPoint(fillRect.Left, fillRect.Top),
                       new SKPoint(fillRect.Left, fillRect.Bottom),
                       new[] { topColor, bottomColor },
                       null,
                       SKShaderTileMode.Clamp))
            {
                _paint.Shader = fillShader;
                _paint.Color = SKColors.White;
                canvas.DrawRoundRect(fillRect, innerRadius, innerRadius, _paint);
            }

            _paint.Shader = null;
            canvas.Restore();
        }

        private void DrawLabel(SKCanvas canvas, SKRect inner, SKRect fillRect, float innerRadius, float fillWidth)
        {
            float fontSize = _labelFontSize > 0f ? _labelFontSize : inner.Height * 0.65f;
            _font.Size = fontSize;
            _font.Typeface = SKTypeface.Default;

            var textWidth = _font.MeasureText(_labelText);
            float textPad = Math.Max(8f, inner.Height * 0.14f);
            float x = inner.Right - textPad - textWidth;
            x = Math.Max(inner.Left + textPad, x);

            var textBounds = new SKRect();
            _font.MeasureText(_labelText, out textBounds);
            float y = inner.MidY - textBounds.MidY;

            _paint.Style = SKPaintStyle.Fill;
            _paint.Color = _labelColor;
            canvas.DrawText(_labelText, x, y, _font, _paint);

            if (fillWidth <= 0f)
                return;

            canvas.Save();
            using (var clipPath = new SKPath())
            {
                clipPath.AddRoundRect(fillRect, innerRadius, innerRadius);
                canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
            }
            _paint.Color = _labelOverFillColor;
            canvas.DrawText(_labelText, x, y, _font, _paint);
            canvas.Restore();
        }
    }
}
