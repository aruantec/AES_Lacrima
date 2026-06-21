namespace AES_Controls.Composition;

/// <summary>
/// Shared scroll/selection motion flag so background cover work can yield while the user navigates.
/// </summary>
public static class CompositionViewportState
{
    private static int _motionDepth;
    private static int _visibleCenterIndex = -1;

    public static bool IsInMotion => _motionDepth > 0;

    public static int VisibleCenterIndex
    {
        get => _visibleCenterIndex;
        set
        {
            if (_visibleCenterIndex == value)
                return;

            _visibleCenterIndex = value;
            VisibleCenterIndexChanged?.Invoke(value);
        }
    }

    public static event Action<int>? VisibleCenterIndexChanged;

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
