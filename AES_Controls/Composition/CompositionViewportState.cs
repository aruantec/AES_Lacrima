namespace AES_Controls.Composition;

/// <summary>
/// Shared scroll/selection motion flag so background cover work can yield while the user navigates.
/// </summary>
public static class CompositionViewportState
{
    private static int _motionDepth;

    public static bool IsInMotion => _motionDepth > 0;

    public static int VisibleCenterIndex { get; set; } = -1;

    public static void EnterMotion()
    {
        if (_motionDepth++ == 0)
            MotionChanged?.Invoke(true);
    }

    public static void ExitMotion()
    {
        if (_motionDepth <= 0)
            return;

        if (--_motionDepth == 0)
            MotionChanged?.Invoke(false);
    }

    public static event Action<bool>? MotionChanged;
}
