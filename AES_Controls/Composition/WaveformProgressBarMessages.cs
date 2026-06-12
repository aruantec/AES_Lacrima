using SkiaSharp;

namespace AES_Controls.Composition;

internal record WaveformDataMessage(float[] Samples);

internal record WaveformProgressMessage(double Progress, bool IsDragging);

internal record WaveformColorsMessage(
    SKColor Played,
    SKColor Unplayed,
    SKColor Indicator,
    SKColor Triangle,
    SKColor Text,
    SKColor Loading,
    SKColor Border,
    SKColor Background);

internal record WaveformLayoutMessage(
    float WaveformVerticalOffset,
    float BarGap,
    float BlockHeight,
    float VerticalGap,
    int VisualBarCount,
    float MarginLeft,
    float MarginRight,
    float MarginTop,
    float MarginBottom);

internal record WaveformStyleMessage(
    bool IsSymmetric,
    bool ShowReflection,
    bool IsTriangleUpwards,
    bool IsDigital,
    float TriangleOffset,
    float TriangleWidth,
    float TriangleHeight,
    float TextSize,
    float LoadingIndicatorSize,
    float BorderThickness);

internal record WaveformGradientMessage(SKColor[]? Colors, float[]? Offsets);

internal record WaveformHoverMessage(float? X, float? Y);

internal record WaveformRangeMessage(double Minimum, double Maximum);

internal record WaveformLoadingMessage(bool IsLoading, double Angle);
