using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace AES_Controls.Composition;

/// <summary>
/// A minimal composition control that clears its drawing surface to transparent.
/// </summary>
public class TransparentComposition : Control
{
    private CompositionCustomVisual? _visual;

    public TransparentComposition()
    {
        ClipToBounds = false;
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _visual = compositor.CreateCustomVisual(new TransparentCompositionVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        UpdateVisualSize();
        _visual.SendHandlerMessage("invalidate");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_visual == null)
            return;

        _visual.SendHandlerMessage(null!);
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateVisualSize();
    }

    private void UpdateVisualSize()
    {
        if (_visual == null)
            return;

        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.Size = size;
        _visual.SendHandlerMessage(size);
    }
}
