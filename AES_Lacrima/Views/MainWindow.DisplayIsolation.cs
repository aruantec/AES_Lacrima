using System;
using System.Linq;
using AES_Controls.Composition;
using AES_Controls.GL;
using AES_Emulation.Windows.API;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AES_Lacrima.Views;

/// <summary>
/// Pins the main window layout while a fullscreen Steam game may change the desktop display mode.
/// </summary>
public partial class MainWindow
{
    private sealed class DesktopDisplayIsolationState
    {
        public required PixelPoint Position { get; init; }
        public required Size LogicalSize { get; init; }
        public required WindowState WindowState { get; init; }
        public required PixelRect PrimaryWorkingArea { get; init; }
        public required PixelRect PrimaryBounds { get; init; }
        public required double RenderScaling { get; init; }
    }

    private DesktopDisplayIsolationState? _desktopDisplayIsolation;
    private DispatcherTimer? _displayIsolationTimer;

    internal void BeginDesktopDisplayIsolation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var primary = Screens.Primary;
        _desktopDisplayIsolation = new DesktopDisplayIsolationState
        {
            Position = Position,
            LogicalSize = new Size(
                !double.IsNaN(Width) && Width > 0 ? Width : Bounds.Width,
                !double.IsNaN(Height) && Height > 0 ? Height : Bounds.Height),
            WindowState = WindowState,
            PrimaryWorkingArea = primary?.WorkingArea ?? default,
            PrimaryBounds = primary?.Bounds ?? default,
            RenderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0
        };

        FSLog.Info(
            $"[DISPLAY-ISOLATION] Begin position={_desktopDisplayIsolation.Position}, " +
            $"size={_desktopDisplayIsolation.LogicalSize.Width}x{_desktopDisplayIsolation.LogicalSize.Height}, " +
            $"primary={_desktopDisplayIsolation.PrimaryBounds.Width}x{_desktopDisplayIsolation.PrimaryBounds.Height}, " +
            $"scale={_desktopDisplayIsolation.RenderScaling:0.###}.");

        _displayIsolationTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _displayIsolationTimer.Tick -= OnDisplayIsolationTimerTick;
        _displayIsolationTimer.Tick += OnDisplayIsolationTimerTick;
        _displayIsolationTimer.Start();
    }

    internal void EndDesktopDisplayIsolation()
    {
        if (_desktopDisplayIsolation == null)
            return;

        FSLog.Info("[DISPLAY-ISOLATION] End.");
        _displayIsolationTimer?.Stop();
        _desktopDisplayIsolation = null;
        RecoverFromDisplayModeChange();
    }

    internal bool TryGetDesktopIsolationPrimaryBounds(out PixelRect bounds)
    {
        if (_desktopDisplayIsolation is { PrimaryBounds.Width: > 0, PrimaryBounds.Height: > 0 } state)
        {
            bounds = state.PrimaryBounds;
            return true;
        }

        bounds = default;
        return false;
    }

    private void OnDisplayIsolationTimerTick(object? sender, EventArgs e)
    {
        if (_desktopDisplayIsolation is not { } state)
            return;

        var primary = Screens.Primary;
        var boundsChanged = primary != null && !primary.Bounds.Equals(state.PrimaryBounds);
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? state.RenderScaling;
        var scaleChanged = Math.Abs(scale - state.RenderScaling) > 0.05;

        if (boundsChanged || scaleChanged)
        {
            FSLog.Info(
                $"[DISPLAY-ISOLATION] Desktop mode drift detected. " +
                $"boundsChanged={boundsChanged}, scaleChanged={scaleChanged} ({state.RenderScaling:0.###}->{scale:0.###}).");
            RecoverFromDisplayModeChange();
            return;
        }

        MaintainDesktopDisplayIsolation();
    }

    internal void RecoverFromDisplayModeChange()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Win32API.TryRestoreDesktopDisplayMode();
        MaintainDesktopDisplayIsolation();

        _lastRenderScale = double.NaN;
        _ignoreSizeChange = true;
        try
        {
            InvalidateVisual();
            InvalidateMeasure();
            InvalidateArrange();

            this.FindControl<Control>("MainTopBar")?.InvalidateVisual();
            this.FindControl<GlShaderToyControl>("ShaderToyLayer")?.InvalidateVisual();
            this.FindControl<Control>("ParticleLayer")?.InvalidateVisual();
            this.FindControl<Control>("BackgroundImageLayer")?.InvalidateVisual();

            foreach (var child in this.GetVisualDescendants().OfType<Control>())
            {
                if (child is CompositionAlbumRowControl or Navigation.EmulationListView)
                    InvalidateCompositionSubtree(child);
            }
        }
        finally
        {
            Dispatcher.UIThread.Post(() => _ignoreSizeChange = false, DispatcherPriority.Background);
        }
    }

    private static void InvalidateCompositionSubtree(Control root)
    {
        root.InvalidateVisual();
        root.InvalidateMeasure();
        root.InvalidateArrange();

        if (root is CompositionAlbumRowControl albumRow)
            albumRow.RefreshAllTileCovers();

        if (root is Navigation.EmulationListView emulationList)
            emulationList.RefreshAlbumTileCovers();

        foreach (var child in root.GetVisualChildren().OfType<Control>())
            InvalidateCompositionSubtree(child);
    }

    private void MaintainDesktopDisplayIsolation()
    {
        if (_desktopDisplayIsolation is not { } state || IsCapturePresentationFullscreen)
            return;

        var widthDrift = Math.Abs(Width - state.LogicalSize.Width);
        var heightDrift = Math.Abs(Height - state.LogicalSize.Height);
        var positionDrift = Math.Abs(Position.X - state.Position.X) + Math.Abs(Position.Y - state.Position.Y);
        var stateDrift = WindowState != state.WindowState && WindowState != WindowState.Normal;

        if (widthDrift < 2 && heightDrift < 2 && positionDrift < 2 && !stateDrift)
            return;

        if (DataContext is ViewModels.MainWindowViewModel vm && vm.HasUserResizedWindow)
        {
            _desktopDisplayIsolation = new DesktopDisplayIsolationState
            {
                Position = Position,
                LogicalSize = new Size(Width, Height),
                WindowState = WindowState,
                PrimaryWorkingArea = state.PrimaryWorkingArea,
                PrimaryBounds = state.PrimaryBounds,
                RenderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? state.RenderScaling
            };
            vm.WindowWidth = Width;
            vm.WindowHeight = Height;
            return;
        }

        FSLog.Info(
            $"[DISPLAY-ISOLATION] Restoring layout after display change. " +
            $"drift=({widthDrift:0},{heightDrift:0}) posDrift={positionDrift} state={WindowState}->{state.WindowState}.");

        _ignoreSizeChange = true;
        try
        {
            if (stateDrift)
                WindowState = state.WindowState;

            Position = state.Position;
            Width = state.LogicalSize.Width;
            Height = state.LogicalSize.Height;

            if (DataContext is ViewModels.MainWindowViewModel windowVm)
            {
                windowVm.WindowWidth = state.LogicalSize.Width;
                windowVm.WindowHeight = state.LogicalSize.Height;
            }
        }
        finally
        {
            Dispatcher.UIThread.Post(() => _ignoreSizeChange = false, DispatcherPriority.Background);
        }
    }
}
