using System;
using System.Linq;
using AES_Controls.Composition;
using AES_Controls.Widgets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AES_Controls.GL;

/// <summary>
/// Maps the player vinyl disc bounds into <see cref="GlShaderToyControl"/> fragment coordinates.
/// </summary>
public static class ShaderDiscOccluderSync
{
    public static void ApplyFromVisualRoot(GlShaderToyControl shader)
    {
        if (shader == null)
            return;

        var root = TopLevel.GetTopLevel(shader) as Visual;
        Apply(shader, root != null ? FindPlayerWidget(root) : null);
    }

    public static void Apply(GlShaderToyControl shader, WidgetControl? playerWidget)
    {
        if (shader == null)
            return;

        if (playerWidget == null || !playerWidget.IsVisible || playerWidget.Bounds.Width <= 0 || playerWidget.Bounds.Height <= 0)
        {
            shader.SetDiscOccluder(0f, 0f, 0f, false);
            return;
        }

        var disc = PlayerCompositionArmMetrics.GetDiscLayout(playerWidget.Bounds.Size);
        var transform = playerWidget.TransformToVisual(shader);
        if (transform == null)
        {
            shader.SetDiscOccluder(0f, 0f, 0f, false);
            return;
        }

        var center = transform.Value.Transform(disc.Center);
        var edge = transform.Value.Transform(new Point(disc.Center.X + disc.RingRadius, disc.Center.Y));
        var radius = Math.Sqrt(
            (edge.X - center.X) * (edge.X - center.X) +
            (edge.Y - center.Y) * (edge.Y - center.Y));

        var renderScale = TopLevel.GetTopLevel(shader)?.RenderScaling ?? 1.0;
        var fragHeight = shader.Bounds.Height * renderScale;
        shader.SetDiscOccluder(
            (float)(center.X * renderScale),
            (float)(fragHeight - center.Y * renderScale),
            (float)(radius * renderScale),
            true);
    }

    public static WidgetControl? FindPlayerWidget(Visual root)
    {
        foreach (var widget in root.GetVisualDescendants().OfType<WidgetControl>())
        {
            if (widget.Name == "Player" || widget.WidgetSettingsKey == "Player")
                return widget;
        }

        return null;
    }
}
