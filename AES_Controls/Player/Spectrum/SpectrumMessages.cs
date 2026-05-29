using Avalonia.Collections;
using SkiaSharp;

namespace AES_Controls.Player.Spectrum;

internal sealed class SpectrumSourceMessage(AvaloniaList<double>? source)
{
    public AvaloniaList<double>? Source { get; } = source;
}

internal sealed record SpectrumConfigMessage(
    float BarWidth,
    float BarSpacing,
    float BlockHeight,
    float PeakThickness,
    bool UseDeltaTime,
    double AttackLerp,
    double ReleaseLerp,
    double PeakDecay,
    double PrePowAttackAlpha,
    double MaxRiseFraction,
    double MaxRiseAbsolute);

internal sealed record SpectrumGradientMessage(SKColor[] Colors);

internal sealed record SpectrumRuntimeMessage(bool IsVisible, bool IsPaused);

internal sealed record SpectrumOpacityMessage(float Opacity);

internal sealed record SpectrumWakeMessage;
