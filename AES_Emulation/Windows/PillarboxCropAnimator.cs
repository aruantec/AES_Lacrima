using System;

namespace AES_Emulation.Windows;

/// <summary>
/// Time-based crop easing aligned with <see cref="AES_Controls.Behaviors.SlideBehavior"/> (450ms cubic).
/// </summary>
internal static class PillarboxCropAnimator
{
    /// <summary>Matches SlideBehavior slide/opacity duration.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(450);

    /// <summary>Settings overlay close — accelerates into the settled state.</summary>
    public static double CubicEaseIn(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t;
    }

    /// <summary>Settings overlay open — decelerates into the settled state.</summary>
    public static double CubicEaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var inv = 1.0 - t;
        return 1.0 - inv * inv * inv;
    }

    public static int Lerp(int from, int to, double progress) =>
        (int)Math.Round(from + (to - from) * progress);

    /// <summary>
    /// Bars closing = crop insets grow (letterbox shrinks). Uses ease-in like overlay close.
    /// </summary>
    public static bool IsClosingBars(int fromLeft, int fromRight, int toLeft, int toRight) =>
        toLeft > fromLeft || toRight > fromRight;
}
