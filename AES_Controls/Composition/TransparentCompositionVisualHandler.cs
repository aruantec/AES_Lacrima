using System.Numerics;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Composition visual handler that only clears the canvas to transparent.
/// </summary>
public class TransparentCompositionVisualHandler : CompositionCustomVisualHandler
{
    private Vector2 _visualSize;

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                return;
            case "invalidate":
                Invalidate();
                return;
            case Vector2 size:
                _visualSize = size;
                Invalidate();
                return;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0)
            return;

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        lease.SkCanvas.Clear(SKColors.Transparent);
    }
}
