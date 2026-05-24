using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AES_Lacrima.Views;

/// <summary>
/// Saved main-window chrome while emulator capture is in fullscreen presentation mode.
/// </summary>
public sealed class MainWindowCaptureFullscreenState
{
    public required PixelPoint Position { get; init; }
    public required Size Size { get; init; }
    public required CornerRadius MainBorderCornerRadius { get; init; }
    public required Thickness MainBorderBorderThickness { get; init; }
    public required bool MainTopBarVisible { get; init; }
    public required bool ParticlesVisible { get; init; }
    public required bool ShaderToyVisible { get; init; }
    public required bool BackgroundImageVisible { get; init; }
    public required bool EdgeBorderVisible { get; init; }
}

public partial class MainWindow
{
    public MainWindowCaptureFullscreenState EnterCaptureFullscreenMode(PixelRect screenBounds)
    {
        var mainTopBar = this.FindControl<Control>("MainTopBar");
        var particleLayer = this.FindControl<Control>("ParticleLayer");
        var shaderToyLayer = this.FindControl<Control>("ShaderToyLayer");
        var backgroundImageLayer = this.FindControl<Control>("BackgroundImageLayer");
        var edgeBorderLayer = this.FindControl<Control>("EdgeBorderLayer");

        var state = new MainWindowCaptureFullscreenState
        {
            Position = Position,
            Size = new Size(Width, Height),
            MainBorderCornerRadius = MainBorder?.CornerRadius ?? default,
            MainBorderBorderThickness = MainBorder?.BorderThickness ?? default,
            MainTopBarVisible = mainTopBar?.IsVisible ?? true,
            ParticlesVisible = particleLayer?.IsVisible ?? true,
            ShaderToyVisible = shaderToyLayer?.IsVisible ?? true,
            BackgroundImageVisible = backgroundImageLayer?.IsVisible ?? true,
            EdgeBorderVisible = edgeBorderLayer?.IsVisible ?? true
        };

        ApplyCaptureFullscreenWindowBounds(screenBounds);

        if (mainTopBar != null)
            mainTopBar.IsVisible = false;
        if (particleLayer != null)
            particleLayer.IsVisible = false;
        if (shaderToyLayer != null)
            shaderToyLayer.IsVisible = false;
        if (backgroundImageLayer != null)
            backgroundImageLayer.IsVisible = false;
        if (edgeBorderLayer != null)
            edgeBorderLayer.IsVisible = false;

        if (MainBorder != null)
        {
            MainBorder.CornerRadius = default;
            MainBorder.BorderThickness = default;
        }

        return state;
    }

    public void ExitCaptureFullscreenMode(MainWindowCaptureFullscreenState state)
    {
        var mainTopBar = this.FindControl<Control>("MainTopBar");
        var particleLayer = this.FindControl<Control>("ParticleLayer");
        var shaderToyLayer = this.FindControl<Control>("ShaderToyLayer");
        var backgroundImageLayer = this.FindControl<Control>("BackgroundImageLayer");
        var edgeBorderLayer = this.FindControl<Control>("EdgeBorderLayer");

        Position = state.Position;
        Width = state.Size.Width;
        Height = state.Size.Height;

        if (mainTopBar != null)
            mainTopBar.IsVisible = state.MainTopBarVisible;
        if (particleLayer != null)
            particleLayer.IsVisible = state.ParticlesVisible;
        if (shaderToyLayer != null)
            shaderToyLayer.IsVisible = state.ShaderToyVisible;
        if (backgroundImageLayer != null)
            backgroundImageLayer.IsVisible = state.BackgroundImageVisible;
        if (edgeBorderLayer != null)
            edgeBorderLayer.IsVisible = state.EdgeBorderVisible;

        if (MainBorder != null)
        {
            MainBorder.CornerRadius = state.MainBorderCornerRadius;
            MainBorder.BorderThickness = state.MainBorderBorderThickness;
        }
    }

    private void ApplyCaptureFullscreenWindowBounds(PixelRect screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            return;

        var renderScaling = Math.Max(0.0001, RenderScaling > 0 ? RenderScaling : 1.0);
        Position = screenBounds.Position;
        Width = Math.Ceiling(screenBounds.Width / renderScaling);
        Height = Math.Ceiling(screenBounds.Height / renderScaling);
    }
}
