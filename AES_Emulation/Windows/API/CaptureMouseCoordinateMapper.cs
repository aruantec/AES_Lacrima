using System;
using Avalonia;
using Avalonia.Media;

namespace AES_Emulation.Windows.API;

/// <summary>
/// Maps pointer positions within a scaled capture presentation to emulator client coordinates.
/// Uses the same dest-rect proportions as rendering, then maps into the target client area.
/// </summary>
public static class CaptureMouseCoordinateMapper
{
    public static bool TryMapLocalToTargetClient(
        Visual visual,
        Point localPoint,
        Rect localDestRect,
        IntPtr targetHwnd,
        int frameWidth,
        int frameHeight,
        int frameCropLeft,
        int frameCropRight,
        int frameCropTop,
        int frameCropBottom,
        int clientAreaCropLeft,
        int clientAreaCropTop,
        int clientAreaCropRight,
        int clientAreaCropBottom,
        out int clientX,
        out int clientY)
    {
        clientX = 0;
        clientY = 0;

        if (visual == null || targetHwnd == IntPtr.Zero)
            return false;

        var viewSize = visual.Bounds.Size;
        if (viewSize.Width <= 0 || viewSize.Height <= 0 || localDestRect.Width <= 0 || localDestRect.Height <= 0)
            return false;

        var visLeft = Math.Max(0, localDestRect.X);
        var visTop = Math.Max(0, localDestRect.Y);
        var visRight = Math.Min(viewSize.Width, localDestRect.Right);
        var visBottom = Math.Min(viewSize.Height, localDestRect.Bottom);

        if (localPoint.X < visLeft || localPoint.X > visRight ||
            localPoint.Y < visTop || localPoint.Y > visBottom)
        {
            return false;
        }

        if (!Win32API.TryGetWindowClientSize(targetHwnd, out var clientWidth, out var clientHeight))
            return false;

        var effectiveFrameWidth = frameWidth > 0 ? frameWidth : clientWidth;
        var effectiveFrameHeight = frameHeight > 0 ? frameHeight : clientHeight;

        var scaleX = clientWidth / (double)Math.Max(1, effectiveFrameWidth);
        var scaleY = clientHeight / (double)Math.Max(1, effectiveFrameHeight);

        var contentLeft = clientAreaCropLeft + (int)Math.Round(frameCropLeft * scaleX);
        var contentTop = clientAreaCropTop + (int)Math.Round(frameCropTop * scaleY);
        var contentRight = clientAreaCropRight + (int)Math.Round(frameCropRight * scaleX);
        var contentBottom = clientAreaCropBottom + (int)Math.Round(frameCropBottom * scaleY);

        var contentWidth = Math.Max(1, clientWidth - contentLeft - contentRight);
        var contentHeight = Math.Max(1, clientHeight - contentTop - contentBottom);

        var u = (localPoint.X - localDestRect.X) / localDestRect.Width;
        var v = (localPoint.Y - localDestRect.Y) / localDestRect.Height;
        u = Math.Clamp(u, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);

        clientX = contentLeft + (int)Math.Round(u * contentWidth);
        clientY = contentTop + (int)Math.Round(v * contentHeight);
        clientX = Math.Clamp(clientX, 0, Math.Max(0, clientWidth - 1));
        clientY = Math.Clamp(clientY, 0, Math.Max(0, clientHeight - 1));
        return true;
    }

    public static Rect CalculateLocalDestRect(
        Size viewSize,
        Stretch stretch,
        int frameWidth,
        int frameHeight,
        int frameCropLeft,
        int frameCropRight,
        int frameCropTop,
        int frameCropBottom)
    {
        if (viewSize.Width <= 0 || viewSize.Height <= 0)
            return default;

        var effectiveFrameWidth = Math.Max(1, frameWidth - frameCropLeft - frameCropRight);
        var effectiveFrameHeight = Math.Max(1, frameHeight - frameCropTop - frameCropBottom);
        return CalculateAspectRect(
            (float)viewSize.Width,
            (float)viewSize.Height,
            effectiveFrameWidth,
            effectiveFrameHeight,
            stretch);
    }

    private static Rect CalculateAspectRect(float viewW, float viewH, float frameW, float frameH, Stretch stretch)
    {
        if (stretch == Stretch.Fill)
            return new Rect(0, 0, viewW, viewH);

        var viewAspect = viewW / viewH;
        var frameAspect = frameW / frameH;

        if (stretch == Stretch.Uniform)
        {
            if (frameAspect > viewAspect)
            {
                var h = viewW / frameAspect;
                return new Rect(0, (viewH - h) / 2, viewW, h);
            }

            var w = viewH * frameAspect;
            return new Rect((viewW - w) / 2, 0, w, viewH);
        }

        if (stretch == Stretch.UniformToFill)
        {
            if (frameAspect > viewAspect)
            {
                var w = viewH * frameAspect;
                return new Rect((viewW - w) / 2, 0, w, viewH);
            }

            var h = viewW / frameAspect;
            return new Rect(0, (viewH - h) / 2, viewW, h);
        }

        return new Rect((viewW - frameW) / 2, (viewH - frameH) / 2, frameW, frameH);
    }
}
