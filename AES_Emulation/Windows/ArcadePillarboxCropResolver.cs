using System;
using AES_Emulation.Services;

namespace AES_Emulation.Windows;

/// <summary>
/// Applies live pillarbox crop during play, or a user-locked crop stored in per-ROM metadata.
/// </summary>
internal sealed class ArcadePillarboxCropResolver
{
    private const double VisiblePillarScale = 0.5;

    private bool _isLocked;
    private int _lockedLeft;
    private int _lockedRight;
    private int _lockedFrameWidth;

    public bool IsLocked => _isLocked;

    public void SetLockedCrop(int left, int right, int frameWidth)
    {
        if (frameWidth <= 0 || (left <= 0 && right <= 0))
            return;

        _isLocked = true;
        _lockedLeft = left;
        _lockedRight = right;
        _lockedFrameWidth = frameWidth;
    }

    public void ClearLock()
    {
        _isLocked = false;
        _lockedLeft = 0;
        _lockedRight = 0;
        _lockedFrameWidth = 0;
    }

    public void Reset(string? romPath = null)
    {
        _isLocked = false;
        _lockedLeft = 0;
        _lockedRight = 0;
        _lockedFrameWidth = 0;

        if (ArcadePillarboxCropMetadataHelper.TryLoadLockedCrop(romPath, out var left, out var right, out var frameWidth))
        {
            _isLocked = true;
            _lockedLeft = left;
            _lockedRight = right;
            _lockedFrameWidth = frameWidth;
        }
    }

    public bool TryGetLockedCrop(int frameWidth, out int left, out int right)
    {
        left = right = 0;
        if (!_isLocked || frameWidth <= 0)
            return false;

        (left, right) = ScaleCrop(_lockedLeft, _lockedRight, _lockedFrameWidth, frameWidth);
        return left > 0 || right > 0;
    }

    public (int Left, int Right) Resolve(int frameWidth, int detectedLeft, int detectedRight)
    {
        if (frameWidth <= 0)
            return (0, 0);

        if (_isLocked && TryGetLockedCrop(frameWidth, out var lockedLeft, out var lockedRight))
            return (lockedLeft, lockedRight);

        var (left, right) = ApplySafetyMargin(detectedLeft, detectedRight, frameWidth);
        var contentWidth = frameWidth - left - right;
        if (contentWidth <= 0)
            return (0, 0);

        var (resolvedLeft, resolvedRight) = CreateSymmetricCrop(frameWidth, contentWidth);
        return RetainPartialPillars(detectedLeft, detectedRight, resolvedLeft, resolvedRight);
    }

    private static (int Left, int Right) ScaleCrop(int left, int right, int storedFrameWidth, int frameWidth)
    {
        if (storedFrameWidth <= 0)
            return (left, right);

        var scale = frameWidth / (double)storedFrameWidth;
        return ((int)Math.Round(left * scale), (int)Math.Round(right * scale));
    }

    private static (int Left, int Right) ApplySafetyMargin(int left, int right, int frameWidth)
    {
        if (left <= 0 && right <= 0)
            return (left, right);

        var margin = Math.Max(12, frameWidth / 90);
        return (Math.Max(0, left - margin), Math.Max(0, right - margin));
    }

    private static (int Left, int Right) RetainPartialPillars(
        int detectedLeft,
        int detectedRight,
        int resolvedLeft,
        int resolvedRight)
    {
        return (
            BlendCropInset(detectedLeft, resolvedLeft),
            BlendCropInset(detectedRight, resolvedRight));
    }

    private static int BlendCropInset(int detectedInset, int resolvedInset)
    {
        if (detectedInset <= 0)
            return resolvedInset;

        var currentVisible = Math.Max(0, detectedInset - resolvedInset);
        if (currentVisible <= 0)
            return resolvedInset;

        var targetVisible = (int)Math.Round(currentVisible * VisiblePillarScale);
        return detectedInset - targetVisible;
    }

    private static (int Left, int Right) CreateSymmetricCrop(int frameWidth, int contentWidth)
    {
        contentWidth = Math.Clamp(contentWidth, 1, frameWidth - 1);
        var totalCrop = frameWidth - contentWidth;
        var left = totalCrop / 2;
        return (left, totalCrop - left);
    }
}
