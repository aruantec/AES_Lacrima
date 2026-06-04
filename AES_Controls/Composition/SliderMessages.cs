using SkiaSharp;

namespace AES_Controls.Composition
{
    // Shared messages for slider visuals. Kept separate so carousel remains untouched.
    internal record InstantSliderPositionMessage(double Value);
    internal record PlayedAreaBrushMessage(SKColor Color);
    internal record SliderSmallThumbMessage(bool IsSmall);
    internal record PillBorderColorMessage(SKColor Color);
    internal record PillFillColorMessage(SKColor Color);
    internal record PillTrackHeightMessage(float Value);
    internal record PillLabelTextMessage(string Text);
    internal record PillLabelFontSizeMessage(float Value);
    internal record PillLabelColorMessage(SKColor Color);
    internal record PillLabelOverFillColorMessage(SKColor Color);
    internal record PillShowPositionLabelMessage(bool Value);
}
