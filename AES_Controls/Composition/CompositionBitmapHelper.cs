using System.Buffers;
using System.Runtime.InteropServices;
using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace AES_Controls.Composition;

internal static class CompositionBitmapHelper
{
    public const int FolderCoverMaxEdge = 256;

    public static SKImage? ToSkImage(Bitmap? bitmap, int maxEdge = 0)
    {
        using var skBmp = CopyToSkBitmap(bitmap);
        if (skBmp == null)
            return null;

        if (maxEdge <= 0)
            return SKImage.FromBitmap(skBmp.Copy() ?? skBmp);

        return ResizeSkBitmap(skBmp, maxEdge);
    }

    /// <summary>
    /// Converts a cover bitmap to Skia, removing uniform letterbox/pillarbox bars before resize.
    /// </summary>
    public static SKImage? ToCoverSkImage(Bitmap? bitmap, int maxEdge, Action<SKBitmap>? onCropped = null) =>
        CreateCoverSkImage(CopyToSkBitmap(bitmap), maxEdge, onCropped, disposeSource: true);

    /// <summary>
    /// Copies Avalonia bitmap pixels on the UI thread, then crops and resizes on a worker thread.
    /// </summary>
    public static async Task<SKImage?> ToCoverSkImageAsync(
        Bitmap? bitmap,
        int maxEdge,
        Action<SKBitmap>? onCropped = null,
        CancellationToken cancellationToken = default)
    {
        if (bitmap == null)
            return null;

        PixelSize size;
        try
        {
            size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0 || bitmap.Format == null)
                return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        int w = size.Width;
        int h = size.Height;
        int stride = w * 4;
        int bufferSize = h * stride;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            bool copied;
            if (Dispatcher.UIThread.CheckAccess())
            {
                copied = TryCopyBitmapPixels(bitmap, size, buffer, bufferSize, stride);
            }
            else
            {
                var priority = CompositionViewportState.IsInMotion
                    ? DispatcherPriority.Background
                    : DispatcherPriority.Normal;
                copied = await Dispatcher.UIThread.InvokeAsync(
                    () => TryCopyBitmapPixels(bitmap, size, buffer, bufferSize, stride),
                    priority,
                    cancellationToken);
            }

            if (!copied)
                return null;

            return await Task.Run(() =>
            {
                using var skBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                unsafe
                {
                    fixed (byte* p = buffer)
                        Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
                }

                return CreateCoverSkImage(skBmp, maxEdge, onCropped, disposeSource: true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static SKImage? CreateCoverSkImage(
        SKBitmap? source,
        int maxEdge,
        Action<SKBitmap>? onCropped = null,
        bool disposeSource = false)
    {
        if (source == null)
            return null;

        SKBitmap? cropped = null;
        var working = source;
        try
        {
            cropped = CoverImageBarCropHelper.TryCrop(source, out bool didCrop);
            if (cropped != null)
            {
                working = cropped;
                if (didCrop)
                    onCropped?.Invoke(cropped);
            }

            if (maxEdge <= 0)
                return SKImage.FromBitmap(working.Copy() ?? working);

            int targetW = maxEdge;
            int targetH = maxEdge;
            if (working.Width > working.Height)
                targetH = Math.Max(1, (int)(maxEdge * (double)working.Height / working.Width));
            else
                targetW = Math.Max(1, (int)(maxEdge * (double)working.Width / working.Height));

            if (working.Width <= maxEdge && working.Height <= maxEdge)
                return SKImage.FromBitmap(working.Copy() ?? working);

            using var resized = working.Resize(new SKImageInfo(targetW, targetH), SKFilterQuality.Medium);
            return resized != null ? SKImage.FromBitmap(resized) : SKImage.FromBitmap(working.Copy() ?? working);
        }
        finally
        {
            if (cropped != null && !ReferenceEquals(cropped, source))
                cropped.Dispose();

            if (disposeSource)
                source.Dispose();
        }
    }

    private static bool TryCopyBitmapPixels(Bitmap bitmap, PixelSize size, byte[] buffer, int bufferSize, int stride)
    {
        try
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bufferSize, stride);
                return true;
            }
            finally
            {
                handle.Free();
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static SKBitmap? CopyToSkBitmap(Bitmap? bitmap)
    {
        if (bitmap == null)
            return null;

        PixelSize size;
        try
        {
            size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0 || bitmap.Format == null)
                return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        int w = size.Width;
        int h = size.Height;
        int stride = w * 4;
        int bufferSize = h * stride;
        byte[] buffer = new byte[bufferSize];

        try
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bufferSize, stride);
            }
            finally
            {
                handle.Free();
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch
        {
            return null;
        }

        var skBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = buffer)
            {
                Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
            }
        }

        return skBmp;
    }

    private static SKImage? ResizeSkBitmap(SKBitmap skBmp, int maxEdge)
    {
        int w = skBmp.Width;
        int h = skBmp.Height;
        float scale = maxEdge / (float)Math.Max(w, h);
        int tw = Math.Max(1, (int)(w * scale));
        int th = Math.Max(1, (int)(h * scale));
        using var resized = skBmp.Resize(new SKImageInfo(tw, th), SKFilterQuality.Medium);
        return resized == null ? SKImage.FromBitmap(skBmp.Copy() ?? skBmp) : SKImage.FromBitmap(resized);
    }
}
