using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AES_Controls.Helpers;

/// <summary>
/// Safely releases Avalonia bitmaps that may still be referenced by Image controls.
/// </summary>
public static class BitmapLifecycleHelper
{
    /// <summary>
    /// Disposes bitmap instances after the current UI layout/render pass so Image controls
    /// are not left referencing disposed sources mid-frame.
    /// </summary>
    public static void DisposeAfterRenderPass(params Bitmap?[] bitmaps)
    {
        if (bitmaps.Length == 0)
            return;

        var hasBitmap = false;
        foreach (var bitmap in bitmaps)
        {
            if (bitmap != null)
            {
                hasBitmap = true;
                break;
            }
        }

        if (!hasBitmap)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            ScheduleDispose(bitmaps);
        else
            Dispatcher.UIThread.Post(() => ScheduleDispose(bitmaps));
    }

    private static void ScheduleDispose(Bitmap?[] bitmaps)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var bitmap in bitmaps)
            {
                if (bitmap == null)
                    continue;

                try
                {
                    bitmap.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, DispatcherPriority.Background);
    }
}
