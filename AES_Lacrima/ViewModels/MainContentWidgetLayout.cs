using System;
using AES_Controls.Composition;
using Avalonia;

namespace AES_Lacrima.ViewModels;

/// <summary>
/// Default home-screen widget layout expressed as fractions of the widget panel size.
/// Reference capture: 1092×769 layout space at ScaleFactor 2 (window 1092×720).
/// </summary>
internal static class MainContentWidgetLayout
{
    public const double ReferenceContainerWidth = 1092;
    public const double ReferenceContainerHeight = 769;

    // Clock — small corner margin, square face ~17% of panel width.
    public const double ClockMarginXRatio = 12 / ReferenceContainerWidth;
    public const double ClockMarginYRatio = 12 / ReferenceContainerHeight;
    public const double ClockSizeRatio = 184.84 / ReferenceContainerWidth;

    // Turntable — disc center aligns with panel center (ShaderToy origin).
    public const double PlayerWidthRatio = 354.51 / ReferenceContainerWidth;
    public const double PlayerHeightRatio = 372.04 / ReferenceContainerHeight;

    // Player info — full width strip above the bottom menu.
    public const double PlayerInfoHeightRatio = 116.94 / ReferenceContainerHeight;

    public static void Apply(
        MainContentViewModel target,
        double containerWidth,
        double containerHeight,
        double mainMenuHeight)
    {
        if (containerWidth <= 0 || containerHeight <= 0)
            return;

        var clockSize = Math.Max(120, containerWidth * ClockSizeRatio);
        target.ClockWidth = clockSize;
        target.ClockHeight = clockSize;
        target.ClockLeft = Math.Max(0, containerWidth * ClockMarginXRatio);
        target.ClockTop = Math.Max(0, containerHeight * ClockMarginYRatio);

        target.PlayerWidth = Math.Max(180, containerWidth * PlayerWidthRatio);
        target.PlayerHeight = Math.Max(200, containerHeight * PlayerHeightRatio);

        var discCenter = PlayerCompositionControl.GetDiscCenterInBounds(
            new Size(target.PlayerWidth, target.PlayerHeight));
        target.PlayerLeft = (containerWidth * 0.5) - discCenter.X;
        target.PlayerTop = (containerHeight * 0.5) - discCenter.Y;

        var playerInfoHeight = Math.Max(80, containerHeight * PlayerInfoHeightRatio);
        target.PlayerInfoLeft = 0;
        target.PlayerInfoWidth = containerWidth;
        target.PlayerInfoHeight = playerInfoHeight;
        target.PlayerInfoTop = containerHeight - mainMenuHeight - playerInfoHeight;
    }

    /// <summary>
    /// Converts legacy absolute settings (possibly saved at ScaleFactor render scale) into layout-space values.
    /// </summary>
    public static void NormalizeLegacyAbsoluteValues(
        ref double playerInfoLeft,
        ref double playerInfoTop,
        ref double playerInfoWidth,
        ref double playerInfoHeight,
        ref double clockLeft,
        ref double clockTop,
        ref double clockWidth,
        ref double clockHeight,
        ref double playerLeft,
        ref double playerTop,
        ref double playerWidth,
        ref double playerHeight,
        double scaleFactor,
        double windowWidth)
    {
        if (scaleFactor <= 1.01 || windowWidth <= 0)
            return;

        static bool LooksRenderScaled(double value, double windowLogicalSize, double scaleFactor)
            => value > windowLogicalSize * 1.15 && value <= windowLogicalSize * scaleFactor * 1.15;

        if (LooksRenderScaled(playerInfoWidth, windowWidth, scaleFactor))
        {
            playerInfoLeft /= scaleFactor;
            playerInfoTop /= scaleFactor;
            playerInfoWidth /= scaleFactor;
            playerInfoHeight /= scaleFactor;
            clockLeft /= scaleFactor;
            clockTop /= scaleFactor;
            clockWidth /= scaleFactor;
            clockHeight /= scaleFactor;
            playerLeft /= scaleFactor;
            playerTop /= scaleFactor;
            playerWidth /= scaleFactor;
            playerHeight /= scaleFactor;
        }
    }
}
