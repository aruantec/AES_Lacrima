using System;

namespace AES_Emulation.Services;

public static class GameplayRecordingResolution
{
    /// <summary>
    /// Scales content to fit the resolution cap (up or down), preserving aspect ratio with even dimensions.
    /// </summary>
    public static (int Width, int Height) FitEvenDimensions(int width, int height, GameplayRecordingResolutionCap cap)
    {
        width = Math.Max(2, width & ~1);
        height = Math.Max(2, height & ~1);
        if (cap == GameplayRecordingResolutionCap.Native)
            return (width, height);

        // Standard presets (720p, 1080p, …) cap the shorter dimension — e.g. 1080p → 1920×1080 for 16:9.
        var capEdge = (int)cap;
        var shortEdge = Math.Min(width, height);
        if (shortEdge < 2)
            return (width, height);

        var scale = capEdge / (double)shortEdge;
        var outW = Math.Max(2, ((int)Math.Round(width * scale)) & ~1);
        var outH = Math.Max(2, ((int)Math.Round(outW * height / (double)width)) & ~1);
        return (outW, outH);
    }

    public static string GetDisplayLabel(GameplayRecordingResolutionCap cap) => cap switch
    {
        GameplayRecordingResolutionCap.Native => "Native (no cap)",
        GameplayRecordingResolutionCap.P384 => "384p",
        GameplayRecordingResolutionCap.P720 => "720p",
        GameplayRecordingResolutionCap.P1080 => "1080p",
        GameplayRecordingResolutionCap.P1440 => "1440p",
        GameplayRecordingResolutionCap.P2160 => "4K",
        _ => cap.ToString()
    };
}
