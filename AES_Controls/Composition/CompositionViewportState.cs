namespace AES_Controls.Composition;

/// <summary>
/// Shared scroll/selection motion flag so background cover work can yield while the user navigates.
/// </summary>
public static class CompositionViewportState
{
    private static int _motionDepth;
    private static int _visibleCenterIndex = -1;
    private static int[] _visibleIndices = Array.Empty<int>();

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

    public static IReadOnlyList<int> VisibleIndices => _visibleIndices;

    public static void SetVisibleIndices(IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
        {
            if (_visibleIndices.Length == 0)
                return;

            _visibleIndices = Array.Empty<int>();
            VisibleIndicesChanged?.Invoke(_visibleIndices);
            return;
        }

        if (_visibleIndices.Length == indices.Count)
        {
            bool same = true;
            for (int i = 0; i < indices.Count; i++)
            {
                if (_visibleIndices[i] != indices[i])
                {
                    same = false;
                    break;
                }
            }

            if (same)
                return;
        }

        var copy = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++)
            copy[i] = indices[i];

        _visibleIndices = copy;
        VisibleIndicesChanged?.Invoke(_visibleIndices);
    }

    public static event Action<int>? VisibleCenterIndexChanged;
    public static event Action<IReadOnlyList<int>>? VisibleIndicesChanged;

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
