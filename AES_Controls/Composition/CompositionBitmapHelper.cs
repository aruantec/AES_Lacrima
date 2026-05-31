using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace AES_Controls.Composition;

internal static class CompositionBitmapHelper
{
    public const int FolderCoverMaxEdge = 256;

    public static SKImage? ToSkImage(Bitmap? bitmap, int maxEdge = 0)
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

        using var skBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = buffer)
            {
                Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
            }
        }

        if (maxEdge > 0 && Math.Max(w, h) > maxEdge)
        {
            float scale = maxEdge / (float)Math.Max(w, h);
            int tw = Math.Max(1, (int)(w * scale));
            int th = Math.Max(1, (int)(h * scale));
            using var resized = skBmp.Resize(new SKImageInfo(tw, th), SKFilterQuality.Medium);
            return resized == null ? SKImage.FromBitmap(skBmp) : SKImage.FromBitmap(resized);
        }

        return SKImage.FromBitmap(skBmp);
    }
}
