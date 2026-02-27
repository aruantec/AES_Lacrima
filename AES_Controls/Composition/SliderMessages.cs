using SkiaSharp;

namespace AES_Controls.Composition
{
    // Shared messages for slider visuals. Kept separate so carousel remains untouched.
    internal record InstantSliderPositionMessage(double Value);
    internal record PlayedAreaBrushMessage(SKColor Color);
}
