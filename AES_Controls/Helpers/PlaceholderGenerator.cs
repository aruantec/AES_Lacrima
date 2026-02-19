using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AES_Controls.Helpers
{
    /// <summary>
    /// Provides methods to generate placeholder images for media items.
    /// </summary>
    public static class PlaceholderGenerator
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Bitmap> _cache = new();

        /// <summary>
        /// Generates a default cover bitmap with a musical note icon.
        /// </summary>
        /// <param name="width">The width of the generated bitmap.</param>
        /// <param name="height">The height of the generated bitmap.</param>
        /// <returns>A new <see cref="Bitmap"/> containing the placeholder graphic.</returns>
        public static Bitmap GenerateMusicPlaceholder(int width = 400, int height = 400)
        {
            if (_cache.TryGetValue((width, height), out var cached)) return cached;

            var size = new PixelSize(width, height);
            var renderTarget = new RenderTargetBitmap(size, new Vector(96, 96));

            using (var context = renderTarget.CreateDrawingContext())
            {
                // Background Radial Gradient
                var brush = new RadialGradientBrush
                {
                    Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
                    GradientStops =
                    [
                        new GradientStop(Color.Parse("#E0E0E0"), 0),
                        new GradientStop(Color.Parse("#A0A0A0"), 1)
                    ]
                };
                context.DrawRectangle(brush, null, new Rect(0, 0, width, height));

                // Musical Note icon (Double eighth note)
                var noteBrush = new SolidColorBrush(Color.Parse("#2D2D2D"));
                
                // Scale factors based on original 400x400 design
                double sw = width / 400.0;
                double sh = height / 400.0;
                
                var noteWidth = 200.0 * sw;
                var noteLeft = 110.0 * sw;
                var noteXOffset = (width - noteWidth) / 2.0 - noteLeft;

                // Note heads (slightly tilted ellipses)
                context.DrawEllipse(noteBrush, null, new Rect((110 * sw) + noteXOffset, 260 * sh, 80 * sw, 60 * sh));
                context.DrawEllipse(noteBrush, null, new Rect((230 * sw) + noteXOffset, 240 * sh, 80 * sw, 60 * sh));

                // Stems
                context.DrawRectangle(noteBrush, null, new Rect((175 * sw) + noteXOffset, 110 * sh, 15 * sw, 170 * sh));
                context.DrawRectangle(noteBrush, null, new Rect((295 * sw) + noteXOffset, 90 * sh, 15 * sw, 170 * sh));

                // Beam (tilted rectangle using geometry)
                var stream = new StreamGeometry();
                using (var ctx = stream.Open())
                {
                    ctx.BeginFigure(new Point((175 * sw) + noteXOffset, 110 * sh), true);
                    ctx.LineTo(new Point((310 * sw) + noteXOffset, 90 * sh));
                    ctx.LineTo(new Point((310 * sw) + noteXOffset, 140 * sh));
                    ctx.LineTo(new Point((175 * sw) + noteXOffset, 160 * sh));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(noteBrush, null, stream);
            }

            _cache[(width, height)] = renderTarget;
            return renderTarget;
        }
    }
}
