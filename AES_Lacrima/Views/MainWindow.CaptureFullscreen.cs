using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AES_Lacrima.ViewModels;
using log4net;

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
    private static readonly ILog FSLog = LogManager.GetLogger(typeof(MainWindow).Assembly, "CaptureFullscreen");

    private double _captureRestoreWidth;
    private double _captureRestoreHeight;

    internal bool IsCapturePresentationFullscreen { get; private set; }

    public MainWindowCaptureFullscreenState EnterCaptureFullscreenMode(PixelRect screenBounds)
    {
        var mainTopBar = this.FindControl<Control>("MainTopBar");
        var particleLayer = this.FindControl<Control>("ParticleLayer");
        var shaderToyLayer = this.FindControl<Control>("ShaderToyLayer");
        var backgroundImageLayer = this.FindControl<Control>("BackgroundImageLayer");
        var edgeBorderLayer = this.FindControl<Control>("EdgeBorderLayer");

        if (DataContext is not MainWindowViewModel vm)
            return new MainWindowCaptureFullscreenState
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

        double snapshotWidth = !double.IsNaN(vm.WindowWidth) && vm.WindowWidth > 0
            ? vm.WindowWidth
            : Width;
        double snapshotHeight = !double.IsNaN(vm.WindowHeight) && vm.WindowHeight > 0
            ? vm.WindowHeight
            : Height;

        FSLog.Info($"[ENTER] Before: Width={Width}, Height={Height}, vm.W={vm.WindowWidth}, vm.H={vm.WindowHeight}, Snapshot=({snapshotWidth},{snapshotHeight}), Bounds={Bounds.Width}x{Bounds.Height}, ClientSize={ClientSize}");

        var state = new MainWindowCaptureFullscreenState
        {
            Position = Position,
            Size = new Size(snapshotWidth, snapshotHeight),
            MainBorderCornerRadius = MainBorder?.CornerRadius ?? default,
            MainBorderBorderThickness = MainBorder?.BorderThickness ?? default,
            MainTopBarVisible = mainTopBar?.IsVisible ?? true,
            ParticlesVisible = particleLayer?.IsVisible ?? true,
            ShaderToyVisible = shaderToyLayer?.IsVisible ?? true,
            BackgroundImageVisible = backgroundImageLayer?.IsVisible ?? true,
            EdgeBorderVisible = edgeBorderLayer?.IsVisible ?? true
        };

        _captureRestoreWidth = snapshotWidth;
        _captureRestoreHeight = snapshotHeight;

        IsCapturePresentationFullscreen = true;
        _ignoreSizeChange = true;

        ApplyCaptureFullscreenWindowBounds(vm, screenBounds);

        FSLog.Info($"[ENTER] After ApplyBounds: Width={Width}, Height={Height}, vm.W={vm.WindowWidth}, vm.H={vm.WindowHeight}");

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

        // 4. After layout settles, compensate for the platform frame offset.
        //    ExtendClientAreaToDecorationsHint causes Win32 to add an invisible
        //    frame when resizing — same as exit mode.
        var entryTargetWidth = vm.WindowWidth;
        var entryTargetHeight = vm.WindowHeight;
        Dispatcher.UIThread.Post(() =>
        {
            var widthOffset = Width - entryTargetWidth;
            var heightOffset = Height - entryTargetHeight;

            FSLog.Info($"[ENTER-POST] Detected offset: widthOffset={widthOffset}, heightOffset={heightOffset}, Width={Width}, Height={Height}, target=({entryTargetWidth},{entryTargetHeight})");

            if (Math.Abs(widthOffset) > 0.5 || Math.Abs(heightOffset) > 0.5)
            {
                var compensatedWidth = entryTargetWidth - widthOffset;
                var compensatedHeight = entryTargetHeight - heightOffset;
                Width = compensatedWidth;
                Height = compensatedHeight;

                FSLog.Info($"[ENTER-POST] After compensate: Width={Width}, Height={Height}");
            }

            Activate();
        }, DispatcherPriority.Loaded);

        return state;
    }

    public void ExitCaptureFullscreenMode(MainWindowCaptureFullscreenState state)
    {
        var mainTopBar = this.FindControl<Control>("MainTopBar");
        var particleLayer = this.FindControl<Control>("ParticleLayer");
        var shaderToyLayer = this.FindControl<Control>("ShaderToyLayer");
        var backgroundImageLayer = this.FindControl<Control>("BackgroundImageLayer");
        var edgeBorderLayer = this.FindControl<Control>("EdgeBorderLayer");

        _ignoreSizeChange = true;

        FSLog.Info($"[EXIT] Start: Width={Width}, Height={Height}, vm.W={(DataContext as MainWindowViewModel)?.WindowWidth}, vm.H={(DataContext as MainWindowViewModel)?.WindowHeight}, RestoreTarget=({_captureRestoreWidth},{_captureRestoreHeight})");

        // 1. Restore chrome FIRST (while still at fullscreen size). This ensures
        //    that when size is applied in step 2, chrome is already present —
        //    matching startup conditions exactly. If size is applied first and
        //    chrome is restored after, the platform/layout system adds chrome
        //    dimensions on top, causing incremental inflation.
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

        // 2. Restore position and size AFTER chrome is back in place.
        //    Chrome being present when size is applied matches the startup
        //    condition — avoids the platform adding chrome dimensions on top.
        Position = state.Position;

        var restoreWidth = _captureRestoreWidth > 0 && !double.IsNaN(_captureRestoreWidth)
            ? _captureRestoreWidth
            : state.Size.Width;
        var restoreHeight = _captureRestoreHeight > 0 && !double.IsNaN(_captureRestoreHeight)
            ? _captureRestoreHeight
            : state.Size.Height;

        FSLog.Info($"[EXIT] After chrome restore: Width={Width}, Height={Height}");

        if (DataContext is MainWindowViewModel vm)
        {
            vm.WindowWidth = restoreWidth;
            vm.WindowHeight = restoreHeight;
        }

        Width = restoreWidth;
        Height = restoreHeight;

        FSLog.Info($"[EXIT] After size restore: Width={Width}, Height={Height}, vm.W={(DataContext as MainWindowViewModel)?.WindowWidth}, vm.H={(DataContext as MainWindowViewModel)?.WindowHeight}");

        // 3. After layout settles, compensate for the platform frame offset.
        //    ExtendClientAreaToDecorationsHint causes Win32 to add an invisible
        //    frame when resizing an existing window. We detect the offset the
        //    platform added and subtract it so the final size matches our target.
        Dispatcher.UIThread.Post(() =>
        {
            IsCapturePresentationFullscreen = false;
            _ignoreSizeChange = true;

            var widthOffset = Width - restoreWidth;
            var heightOffset = Height - restoreHeight;

            FSLog.Info($"[EXIT-POST] Detected offset: widthOffset={widthOffset}, heightOffset={heightOffset}, Width={Width}, Height={Height}, target=({restoreWidth},{restoreHeight})");

            if (Math.Abs(widthOffset) > 0.5 || Math.Abs(heightOffset) > 0.5)
            {
                var compensatedWidth = restoreWidth - widthOffset;
                var compensatedHeight = restoreHeight - heightOffset;
                Width = compensatedWidth;
                Height = compensatedHeight;

                if (DataContext is MainWindowViewModel vmPost)
                {
                    vmPost.WindowWidth = restoreWidth;
                    vmPost.WindowHeight = restoreHeight;
                }

                FSLog.Info($"[EXIT-POST] After compensate: Width={Width}, Height={Height}, vm.W={(DataContext as MainWindowViewModel)?.WindowWidth}, vm.H={(DataContext as MainWindowViewModel)?.WindowHeight}");
            }

            _ignoreSizeChange = false;

            Activate();
        }, DispatcherPriority.Loaded);
    }

    internal void PreparePersistedWindowSizeForShutdown()
    {
        if (!IsCapturePresentationFullscreen)
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        if (_captureRestoreWidth > 0 && !double.IsNaN(_captureRestoreWidth))
            vm.WindowWidth = _captureRestoreWidth;
        if (_captureRestoreHeight > 0 && !double.IsNaN(_captureRestoreHeight))
            vm.WindowHeight = _captureRestoreHeight;
    }

    private void ApplyCaptureFullscreenWindowBounds(MainWindowViewModel vm, PixelRect screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            return;

        var renderScaling = Math.Max(0.0001, RenderScaling > 0 ? RenderScaling : 1.0);
        var fsWidth = Math.Ceiling(screenBounds.Width / renderScaling);
        var fsHeight = Math.Ceiling(screenBounds.Height / renderScaling);

        Position = screenBounds.Position;
        vm.WindowWidth = fsWidth;
        vm.WindowHeight = fsHeight;

        // Also set directly to guarantee resize when the TwoWay binding's
        // source-to-target propagation is suppressed by a prior local value.
        Width = fsWidth;
        Height = fsHeight;
    }
}
