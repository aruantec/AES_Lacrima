using AES_Controls.EmuGrabbing.ShaderHandling;
using AES_Emulation.Windows.API;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using log4net;
using AES_Core.Logging;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.Windows;

public class CompositionWgcCaptureControl : Control
{
    private static readonly ILog Log = LogHelper.For<CompositionWgcCaptureControl>();
    private static bool IsWindowsPlatform => OperatingSystem.IsWindows();
    private CompositionCustomVisual? _visual;
    private WgcCaptureVisualHandler? _handler;
    private DispatcherTimer? _fallbackRenderTimer;
    private nint _session = nint.Zero;
    private WindowHandler? _windowHandler;
    private nint _hostHandle = nint.Zero;
    private nint _activeTargetHwnd = nint.Zero;
    private bool _isAttachedToVisualTree;
    private bool _isStoppingSession;
    private bool _useOwnerRenderFallback;
    private bool _loggedFallbackRenderPath;
    private CancellationTokenSource? _sessionStartCts;
    private const int CaptureReadyTimeoutMs = 5000;

    private bool _isDraggingOverlay;
    private Point _dragStart;

    public static readonly StyledProperty<double> OverlayXProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(OverlayX), 20);

    public double OverlayX
    {
        get => GetValue(OverlayXProperty);
        set => SetValue(OverlayXProperty, value);
    }

    public static readonly StyledProperty<double> OverlayYProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(OverlayY), 30);

    public double OverlayY
    {
        get => GetValue(OverlayYProperty);
        set => SetValue(OverlayYProperty, value);
    }

    public Point OverlayPosition
    {
        get => new Point(OverlayX, OverlayY);
        set
        {
            OverlayX = value.X;
            OverlayY = value.Y;
        }
    }

    #region Styled Properties
    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, IntPtr>(nameof(TargetHwnd));

    public IntPtr TargetHwnd
    {
        get => GetValue(TargetHwndProperty);
        set => SetValue(TargetHwndProperty, value);
    }

    public static readonly StyledProperty<bool> IsCaptureInitializingProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(IsCaptureInitializing), false);

    public bool IsCaptureInitializing
    {
        get => GetValue(IsCaptureInitializingProperty);
        set => SetValue(IsCaptureInitializingProperty, value);
    }

    public static readonly StyledProperty<int> CaptureSessionStartDelayMsProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, int>(nameof(CaptureSessionStartDelayMs), 3000);

    public int CaptureSessionStartDelayMs
    {
        get => GetValue(CaptureSessionStartDelayMsProperty);
        set => SetValue(CaptureSessionStartDelayMsProperty, value);
    }

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public static readonly StyledProperty<string?> RetroarchShaderFileProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, string?>(nameof(RetroarchShaderFile), null);

    public string? RetroarchShaderFile
    {
        get => GetValue(RetroarchShaderFileProperty);
        set => SetValue(RetroarchShaderFileProperty, value);
    }

    public static readonly StyledProperty<bool> DisableDownscaleProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(DisableDownscale), false);

    public bool DisableDownscale
    {
        get => GetValue(DisableDownscaleProperty);
        set => SetValue(DisableDownscaleProperty, value);
    }

    public static readonly StyledProperty<double> FpsSmoothingFactorProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(FpsSmoothingFactor), 0.85);

    public double FpsSmoothingFactor
    {
        get => GetValue(FpsSmoothingFactorProperty);
        set => SetValue(FpsSmoothingFactorProperty, value);
    }

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(DisableVSync), true);

    public bool DisableVSync
    {
        get => GetValue(DisableVSyncProperty);
        set => SetValue(DisableVSyncProperty, value);
    }

    public static readonly StyledProperty<double> FpsProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(Fps), 0.0);

    public double Fps
    {
        get => GetValue(FpsProperty);
        set => SetValue(FpsProperty, value);
    }

    public static readonly StyledProperty<double> FrameTimeMsProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(FrameTimeMs), 0.0);

    public double FrameTimeMs
    {
        get => GetValue(FrameTimeMsProperty);
        set => SetValue(FrameTimeMsProperty, value);
    }

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(Brightness), 1.0);

    public double Brightness
    {
        get => GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(Saturation), 1.0);

    public double Saturation
    {
        get => GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, Color>(nameof(ColorTint), Colors.White);

    public Color ColorTint
    {
        get => GetValue(ColorTintProperty);
        set => SetValue(ColorTintProperty, value);
    }

    public static readonly StyledProperty<bool> ForceUseTargetClientSizeProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(ForceUseTargetClientSize), false);

    public bool ForceUseTargetClientSize
    {
        get => GetValue(ForceUseTargetClientSizeProperty);
        set => SetValue(ForceUseTargetClientSizeProperty, value);
    }

    public static readonly StyledProperty<bool> UseHostWindowCaptureProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(UseHostWindowCapture), false);

    public bool UseHostWindowCapture
    {
        get => GetValue(UseHostWindowCaptureProperty);
        set => SetValue(UseHostWindowCaptureProperty, value);
    }

    public static readonly StyledProperty<bool> ShowStatisticsOverlayProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(ShowStatisticsOverlay), false);

    public bool ShowStatisticsOverlay
    {
        get => GetValue(ShowStatisticsOverlayProperty);
        set => SetValue(ShowStatisticsOverlayProperty, value);
    }

    public static readonly StyledProperty<bool> ShowFrametimeGraphProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(ShowFrametimeGraph), false);

    public bool ShowFrametimeGraph
    {
        get => GetValue(ShowFrametimeGraphProperty);
        set => SetValue(ShowFrametimeGraphProperty, value);
    }

    public static readonly StyledProperty<bool> ShowDetailedGpuInfoProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(ShowDetailedGpuInfo), false);

    public bool ShowDetailedGpuInfo
    {
        get => GetValue(ShowDetailedGpuInfoProperty);
        set => SetValue(ShowDetailedGpuInfoProperty, value);
    }

    public static readonly StyledProperty<double> OverlayOpacityProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, double>(nameof(OverlayOpacity), 0.55);

    public double OverlayOpacity
    {
        get => GetValue(OverlayOpacityProperty);
        set => SetValue(OverlayOpacityProperty, value);
    }

    public static readonly StyledProperty<Color> OverlayBackgroundColorProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, Color>(nameof(OverlayBackgroundColor), Colors.Black);

    public Color OverlayBackgroundColor
    {
        get => GetValue(OverlayBackgroundColorProperty);
        set => SetValue(OverlayBackgroundColorProperty, value);
    }

    public static readonly StyledProperty<bool> EnableAutoCropProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(EnableAutoCrop), false);

    public bool EnableAutoCrop
    {
        get => GetValue(EnableAutoCropProperty);
        set => SetValue(EnableAutoCropProperty, value);
    }

    // New: request the control to stop the active capture session immediately.
    // Set this to true (from UI or view-model) before clearing TargetHwnd to ensure
    // StopSession runs while the handle is still available.
    public static readonly StyledProperty<bool> RequestStopSessionProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(RequestStopSession), false);

    public bool RequestStopSession
    {
        get => GetValue(RequestStopSessionProperty);
        set => SetValue(RequestStopSessionProperty, value);
    }
    #endregion

    public CompositionWgcCaptureControl()
    {
        PreserveAotDependencies();
        ClipToBounds = true;
        LogInfo(
            $"CompositionWgcCaptureControl constructed. " +
#if NATIVE_AOT
            "nativeAot=true."
#else
            "nativeAot=false."
#endif
        );
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WgcCaptureVisualHandler))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WgcCaptureDrawOperation))]
    private static void PreserveAotDependencies()
    {
    }

    private static void LogDebugOnce(ref bool flag, string message)
    {
        if (flag)
            return;

        flag = true;
        Log.Debug(message);
        Debug.WriteLine(message);
        Trace.WriteLine(message);
    }

    private static void LogInfo(string message)
    {
        Log.Info(message);
        Debug.WriteLine(message);
        Trace.WriteLine(message);
    }

    private static void LogError(string message, Exception ex)
    {
        Log.Error(message, ex);
        Debug.WriteLine($"{message} {ex}");
        Trace.WriteLine($"{message} {ex}");
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        // Check if we are interacting with the overlay area (approx 200x110 box)
        float boxW = ShowDetailedGpuInfo ? 350 : 200;
        float boxH = 70;
        if (ShowDetailedGpuInfo) boxH += 40;
        if (ShowFrametimeGraph) boxH += 60;

        if (ShowStatisticsOverlay &&
            pos.X >= OverlayPosition.X - 10 && pos.X <= OverlayPosition.X + boxW - 10 &&
            pos.Y >= OverlayPosition.Y - 20 && pos.Y <= OverlayPosition.Y + boxH - 20)
        {
            _isDraggingOverlay = true;
            _dragStart = pos;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isDraggingOverlay)
        {
            var pos = e.GetPosition(this);
            var delta = pos - _dragStart;
            _dragStart = pos;

            var newPos = OverlayPosition + delta;

            // Constrain and magnetic snapping
            float boxW = ShowDetailedGpuInfo ? 350 : 200;
            float boxH = 70;
            if (ShowDetailedGpuInfo) boxH += 40;
            if (ShowFrametimeGraph) boxH += 60;

            float magnetRadius = 30; // Closer range for "magnetic" pull

            double x = newPos.X;
            double y = newPos.Y;

            // Snap to Left
            if (x < magnetRadius) x = 20;
            // Snap to Right
            else if (x + boxW > Bounds.Width - magnetRadius) x = Bounds.Width - boxW - 20;

            // Snap to Top
            if (y < magnetRadius + 20) y = 30;
            // Snap to Bottom
            else if (y + boxH > Bounds.Height - magnetRadius) y = Bounds.Height - boxH - 20;

            // Hard clamp so it never leaves screen
            x = Math.Clamp(x, 5, Math.Max(5, Bounds.Width - boxW - 5));
            y = Math.Clamp(y, 25, Math.Max(25, Bounds.Height - boxH - 5));

            OverlayPosition = new Point(x, y);

            UpdateHandlerSettings();
            e.Handled = true;
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isDraggingOverlay)
        {
            _isDraggingOverlay = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        _handler ??= new WgcCaptureVisualHandler();

        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        _useOwnerRenderFallback = compositor == null;
        _loggedFallbackRenderPath = false;

        if (IsWindowsPlatform)
        {
            LogInfo(
                $"CompositionWgcCaptureControl attached. compositor={(compositor != null ? "available" : "null")}, " +
                $"dynamicCodeSupported={RuntimeFeature.IsDynamicCodeSupported}, usingOwnerFallback={_useOwnerRenderFallback}.");
            LogInfo(WgcBridgeApi.GetDiagnostics());

            if (!_useOwnerRenderFallback && compositor != null)
            {
                try
                {
                    _visual = compositor.CreateCustomVisual(_handler);
                    ElementComposition.SetElementChildVisual(this, _visual);
                    LogInfo("CompositionWgcCaptureControl created composition custom visual successfully.");
                }
                catch (Exception ex)
                {
                    _useOwnerRenderFallback = true;
                    _visual = null;
                    LogError("CompositionWgcCaptureControl failed to create composition custom visual. Falling back to owner rendering.", ex);
                }
            }

            TryResolveHostHandle();
            StartSession();
        }
        else
        {
            _useOwnerRenderFallback = true;
            _visual = null;
        }

        UpdateHandlerSize();
        UpdateHandlerSession();
        UpdateHandlerSettings();
        UpdateFallbackRenderLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttachedToVisualTree = false;
        if (IsWindowsPlatform)
        {
            LogInfo("CompositionWgcCaptureControl detached from visual tree.");
            StopSession();
        }

        if (_visual != null)
        {
            _visual.SendHandlerMessage(null!); // Cleanup signal
            ElementComposition.SetElementChildVisual(this, null!);
            _visual = null;
        }
        else
        {
            _handler?.OnMessage(null);
        }

        _fallbackRenderTimer?.Stop();
    }

    private void StartSession()
    {
        if (!IsWindowsPlatform)
            return;

        StopSession();

        var nextTargetHwnd = TargetHwnd;
        if (nextTargetHwnd == IntPtr.Zero)
        {
            LogInfo("CompositionWgcCaptureControl StartSession skipped because TargetHwnd is zero.");
            return;
        }

        IsCaptureInitializing = true;

        if (_hostHandle == IntPtr.Zero && !TryResolveHostHandle())
        {
            LogInfo("CompositionWgcCaptureControl StartSession could not resolve host handle.");
            IsCaptureInitializing = false;
            return;
        }

        _sessionStartCts?.Cancel();
        _sessionStartCts?.Dispose();
        _sessionStartCts = new CancellationTokenSource();
        var sessionStartToken = _sessionStartCts.Token;
        _ = StartSessionDelayedAsync(nextTargetHwnd, sessionStartToken);
    }

    private async Task StartSessionDelayedAsync(nint nextTargetHwnd, CancellationToken cancellationToken)
    {
        if (!IsWindowsPlatform)
            return;

        try
        {
            await Task.Delay(CaptureSessionStartDelayMs, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            IsCaptureInitializing = false;
            return;
        }

        if (cancellationToken.IsCancellationRequested || TargetHwnd != nextTargetHwnd)
        {
            IsCaptureInitializing = false;
            return;
        }

        _windowHandler = new WindowHandler(10, 4, 4, 4, 4);
        _windowHandler.EnableRoundedCorners(44);
        _windowHandler.SetMoveToHost(false);
        _windowHandler.Start(_hostHandle, nextTargetHwnd);

        _session = await CreateCaptureSessionWithRetryAsync(nextTargetHwnd, cancellationToken).ConfigureAwait(true);
        if (_session != nint.Zero)
        {
            try
            {
                Win32API.RemoveWindowDecorations(nextTargetHwnd);
                Win32API.MoveAway(nextTargetHwnd, false);
                Win32API.SetWindowOpacity(nextTargetHwnd, 0);
            }
            catch (Exception ex)
            {
                LogInfo($"CompositionWgcCaptureControl could not fully hide/decorate target hwnd 0x{nextTargetHwnd.ToInt64():X} after session creation: {ex.Message}");
            }

            _activeTargetHwnd = nextTargetHwnd;
            LogInfo(
                $"CompositionWgcCaptureControl capture session created. session=0x{_session.ToInt64():X}, " +
                $"target=0x{nextTargetHwnd.ToInt64():X}, useOwnerFallback={_useOwnerRenderFallback}.");
            if (DisableDownscale)
                WgcBridgeApi.SetCaptureMaxResolution(_session, 0, 0);
            else
                WgcBridgeApi.SetCaptureMaxResolution(_session, 4096, 1080);

            UpdateHandlerSession();
            UpdateHandlerSettings();
            UpdateFallbackRenderLoop();

            var captureReady = await WaitForCaptureReadyAsync(cancellationToken).ConfigureAwait(true);
            if (!captureReady)
            {
                LogInfo($"CompositionWgcCaptureControl capture session did not receive a frame within {CaptureReadyTimeoutMs} ms.");
            }

            IsCaptureInitializing = false;
        }
        else
        {
            if (_windowHandler != null)
            {
                _windowHandler.Stop();
                _windowHandler = null;
            }

            Win32API.RestoreWindowDecorations(nextTargetHwnd);
            Win32API.SetWindowOpacity(nextTargetHwnd, 255);
            LogInfo($"CompositionWgcCaptureControl failed to create capture session for hwnd 0x{nextTargetHwnd.ToInt64():X}.");
            IsCaptureInitializing = false;
        }
    }

    private static async Task<nint> CreateCaptureSessionWithRetryAsync(nint targetHwnd, CancellationToken cancellationToken)
    {
        const int maxAttempts = 6;
        const int retryDelayMs = 300;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = WgcBridgeApi.CreateCaptureSession(targetHwnd);
            if (session != nint.Zero)
                return session;

            if (attempt < maxAttempts)
                await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(true);
        }

        return nint.Zero;
    }

    private void StopSession()
    {
        if (!IsWindowsPlatform)
            return;

        if (_isStoppingSession)
            return;

        if (_session == IntPtr.Zero && _windowHandler == null && _activeTargetHwnd == IntPtr.Zero)
            return;

        _isStoppingSession = true;

        try
        {
        LogInfo(
            $"CompositionWgcCaptureControl StopSession. session=0x{_session.ToInt64():X}, " +
            $"activeTarget=0x{_activeTargetHwnd.ToInt64():X}.");
        _sessionStartCts?.Cancel();
        _sessionStartCts?.Dispose();
        _sessionStartCts = null;

        var previousTargetHwnd = _activeTargetHwnd;
        var canRestoreTargetWindow = previousTargetHwnd != IntPtr.Zero && IsWindow(previousTargetHwnd);

        if (_windowHandler != null)
        {
            _windowHandler.Stop();
            if (canRestoreTargetWindow)
                _windowHandler.RestoreOriginalPosition();
            _windowHandler = null;

            if (canRestoreTargetWindow)
            {
                Win32API.RestoreWindowDecorations(previousTargetHwnd);
                Win32API.SetWindowOpacity(previousTargetHwnd, 255);
            }
        }

        var sessionToDestroy = _session;
        _session = nint.Zero;

        if (sessionToDestroy != nint.Zero)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    DestroyCaptureSessionSafely(sessionToDestroy);
                }
                catch (Exception ex)
                {
                    LogError($"CompositionWgcCaptureControl failed to destroy capture session 0x{sessionToDestroy.ToInt64():X}.", ex);
                }
            });
        }

        _activeTargetHwnd = IntPtr.Zero;
        IsCaptureInitializing = false;
        UpdateHandlerSession();
        UpdateFallbackRenderLoop();
        }
        finally
        {
            _isStoppingSession = false;
        }
    }

    private static void DestroyCaptureSessionSafely(nint session)
    {
        if (session == nint.Zero)
            return;

        // Give render/release callbacks a brief window to flush before destroying the native session.
        const int maxWaitIterations = 60;
        const int waitDelayMs = 16;

        for (var i = 0; i < maxWaitIterations; i++)
        {
            try
            {
                var readers = WgcBridgeApi.GetReaderCount(session);
                if (readers <= 0)
                    break;
            }
            catch
            {
                // Ignore reader count probe failures; destruction will still be attempted.
                break;
            }

            Thread.Sleep(waitDelayMs);
        }

        WgcBridgeApi.DestroyCaptureSession(session);
    }

    private async Task<bool> WaitForCaptureReadyAsync(CancellationToken cancellationToken)
    {
        if (!IsWindowsPlatform || _session == nint.Zero)
            return false;

        var start = Stopwatch.GetTimestamp();
        var timeoutTicks = CaptureReadyTimeoutMs * Stopwatch.Frequency / 1000;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (WgcBridgeApi.GetCaptureStatus(_session) > 0)
                return true;

            if (WgcBridgeApi.PeekLatestFrame(_session, out int peekW, out int peekH, out nuint requiredSize) && peekW > 0 && peekH > 0)
                return true;

            if ((Stopwatch.GetTimestamp() - start) > timeoutTicks)
                break;

            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }

        return false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TargetHwndProperty)
        {
            if (_visual == null && _handler == null)
                return;

            if (IsWindowsPlatform)
                StartSession();
        }
        else if (change.Property == RequestStopSessionProperty)
        {
            try
            {
                var requested = change.NewValue is bool b && b;
                if (requested && IsWindowsPlatform)
                {
                    LogInfo("CompositionWgcCaptureControl RequestStopSession triggered.");
                    StopSession();
                    SetValue(RequestStopSessionProperty, false);
                }
            }
            catch (Exception ex)
            {
                LogError("CompositionWgcCaptureControl RequestStopSession handler failed.", ex);
            }
        }
        else if (change.Property == StretchProperty ||
                 change.Property == BrightnessProperty ||
                 change.Property == SaturationProperty ||
                 change.Property == ColorTintProperty ||
                 change.Property == RetroarchShaderFileProperty ||
                 change.Property == FpsSmoothingFactorProperty ||
                 change.Property == DisableVSyncProperty ||
                 change.Property == ShowStatisticsOverlayProperty ||
                 change.Property == ShowFrametimeGraphProperty ||
                 change.Property == ShowDetailedGpuInfoProperty ||
                 change.Property == OverlayOpacityProperty ||
                 change.Property == OverlayBackgroundColorProperty ||
                 change.Property == EnableAutoCropProperty ||
                 change.Property == OverlayXProperty ||
                 change.Property == OverlayYProperty)
        {
            UpdateHandlerSettings();
        }
        else if (change.Property == UseHostWindowCaptureProperty)
        {
            if (IsWindowsPlatform)
                StartSession();
        }
        else if (change.Property == DisableDownscaleProperty)
        {
            if (_session != nint.Zero)
            {
                if (DisableDownscale) WgcBridgeApi.SetCaptureMaxResolution(_session, 0, 0);
                else WgcBridgeApi.SetCaptureMaxResolution(_session, 4096, 1080);
            }
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateHandlerSize();
        }
    }

    private void UpdateHandlerSize()
    {
        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        if (_visual != null)
        {
            _visual.Size = size;
        }

        SendHandlerMessage(size);
    }

    private void UpdateHandlerSession()
    {
        if (!IsWindowsPlatform)
            return;

        LogInfo(
            $"CompositionWgcCaptureControl UpdateHandlerSession: session=0x{_session.ToInt64():X}, " +
            $"target=0x{_activeTargetHwnd.ToInt64():X}, useOwnerInvalidation={_useOwnerRenderFallback}.");
        SendHandlerMessage(new WgcSessionMessage
        {
            Session = _session,
            TargetHwnd = _activeTargetHwnd,
            Owner = new WeakReference<CompositionWgcCaptureControl>(this),
            UseOwnerInvalidation = _useOwnerRenderFallback
        });
    }

    private bool TryResolveHostHandle()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.TryGetPlatformHandle() is not IPlatformHandle platform || platform.Handle == IntPtr.Zero)
        {
            LogInfo("CompositionWgcCaptureControl TryResolveHostHandle failed because platform handle was unavailable.");
            return false;
        }

        _hostHandle = platform.Handle;
        LogInfo($"CompositionWgcCaptureControl resolved host handle 0x{_hostHandle.ToInt64():X}.");
        return true;
    }

    private void UpdateHandlerSettings()
    {
        if (!IsWindowsPlatform)
            return;

        var effectiveEnableAutoCrop = EnableAutoCrop && !ForceUseTargetClientSize;

        SendHandlerMessage(new WgcSettingsMessage
        {
            Stretch = Stretch,
            Brightness = (float)Brightness,
            Saturation = (float)Saturation,
            Tint = ColorTint,
            ForceUseTargetClientSize = ForceUseTargetClientSize,
            RetroarchShaderFile = RetroarchShaderFile,
            FpsSmoothingFactor = FpsSmoothingFactor,
            DisableVSync = DisableVSync,
            ShowStatisticsOverlay = ShowStatisticsOverlay,
            ShowFrametimeGraph = ShowFrametimeGraph,
            ShowDetailedGpuInfo = ShowDetailedGpuInfo,
            OverlayOpacity = (float)OverlayOpacity,
            OverlayBackgroundColor = OverlayBackgroundColor,
            EnableAutoCrop = effectiveEnableAutoCrop,
            OverlayPosition = new Vector2((float)OverlayPosition.X, (float)OverlayPosition.Y)
        });
    }

    private void SendHandlerMessage(object? message)
    {
        if (!IsWindowsPlatform)
            return;

        if (_visual != null)
        {
            _visual.SendHandlerMessage(message!);
            return;
        }

        _handler?.OnMessage(message!);
    }

    private void UpdateFallbackRenderLoop()
    {
        if (!_useOwnerRenderFallback || !_isAttachedToVisualTree || _session == IntPtr.Zero || _handler == null)
        {
            _fallbackRenderTimer?.Stop();
            return;
        }

        LogDebugOnce(ref _loggedFallbackRenderPath, "CompositionWgcCaptureControl is using the owner-render fallback path.");
        EnsureFallbackRenderTimer();
        if (_fallbackRenderTimer != null && !_fallbackRenderTimer.IsEnabled)
            _fallbackRenderTimer.Start();

        InvalidateVisual();
    }

    private void EnsureFallbackRenderTimer()
    {
        if (_fallbackRenderTimer != null)
            return;

        _fallbackRenderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _fallbackRenderTimer.Tick += (_, _) =>
        {
            if (!_useOwnerRenderFallback || !_isAttachedToVisualTree || _handler == null || _session == IntPtr.Zero)
                return;

            _handler.OnAnimationFrameUpdate();
            InvalidateVisual();
        };
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_useOwnerRenderFallback && _handler != null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            context.Custom(new WgcCaptureDrawOperation(new Rect(Bounds.Size), _handler));
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateHandlerSize();
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
}

internal class WgcSessionMessage
{
    public nint Session;
    public nint TargetHwnd;
    public WeakReference<CompositionWgcCaptureControl>? Owner;
    public bool UseOwnerInvalidation;
}

internal class WgcSettingsMessage
{
    public Stretch Stretch;
    public float Brightness;
    public float Saturation;
    public Color Tint;
    public bool ForceUseTargetClientSize;
    public string? RetroarchShaderFile;
    public double FpsSmoothingFactor;
    public bool DisableVSync;
    public bool ShowStatisticsOverlay;
    public bool ShowFrametimeGraph;
    public bool ShowDetailedGpuInfo;
    public float OverlayOpacity;
    public Color OverlayBackgroundColor;
    public bool EnableAutoCrop;
    public Vector2 OverlayPosition;
}

internal sealed class WgcCaptureDrawOperation(Rect bounds, WgcCaptureVisualHandler handler) : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => Bounds.Contains(p);

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        handler.OnRender(context);
    }

    public bool Equals(ICustomDrawOperation? other) => false;
}

public class WgcCaptureVisualHandler : CompositionCustomVisualHandler
{
    private static readonly ILog Log = LogManager.GetLogger(
        typeof(WgcCaptureVisualHandler).Assembly,
        typeof(WgcCaptureVisualHandler).FullName ?? nameof(WgcCaptureVisualHandler));
    private nint _session = nint.Zero;
    private nint _targetHwnd = nint.Zero;
    private WeakReference<CompositionWgcCaptureControl>? _ownerRef;
    private bool _useOwnerInvalidation;
    private bool _forceUseTargetClientSize = false;
    private int _lastCropX, _lastCropY, _lastCropW, _lastCropH;

    // FPS Tracking
    private int _lastNativeFrameCount = -1;
    private long _lastFrameTicks = 0;
    private double _smoothedFps = 0.0;
    private double _smoothedFrameTimeMs = 0.0;
    private double _fpsSmoothing = 0.85;
    private bool _vrrActive = false;
    private long _lastUiUpdateTicks = 0;
    private double _lastSentFps = -1;
    private double _lastSentFt = -1;

    // Auto Crop
    private bool _enableAutoCrop = false;
    private int _cropLeft = 0;
    private int _cropRight = 0;
    private int _consecutiveBlackFrames = 0;

    // Statistics Overlay
    private bool _showStatisticsOverlay = true;
    private bool _showFrametimeGraph = true;
    private bool _showDetailedGpuInfo = false;
    private float _overlayOpacity = 0.55f;
    private Color _overlayBackgroundColor = Colors.Black;
    private Vector2 _overlayPosition = new Vector2(20, 30);

    private string _backendName = "Software";
    private string _gpuRenderer = "Unknown";
    private string _gpuVendor = "Unknown";
    private readonly float[] _frameTimes = new float[120];
    private int _frameTimePtr = 0;
    private SKTypeface? _overlayTypefaceBold;
    private SKTypeface? _overlayTypefaceRegular;
    private SKPaint? _overlayTextPaint;
    private SKPaint? _overlayDetailTextPaint;
    private SKPaint? _overlayBackgroundPaint;
    private SKPaint? _overlayLinePaint;
    private SKPaint? _overlayGridPaint;
    private SKPaint? _overlayGraphTextPaint;
    private SKPath? _overlayGraphPath;

    private Vector2 _visualSize;
    private SKRect _cachedDestRect;
    private bool _rectDirty = true;
    private Stretch _stretch = Stretch.Uniform;
    private float _brightness = 1.0f;
    private float _saturation = 1.0f;
    private Color _tint = Colors.White;
    private SKPaint _paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
    private bool _settingsDirty = true;

    // Shader Pipeline
    private SlangShaderPipeline? _shaderPipeline;
    private string? _retroarchShaderFile;
    private GlInterface? _gl;
    private IntPtr _glTexSubImage2DPtr = IntPtr.Zero;
    private int _captureTextureId;
    private int _intermediateFbo;
    private int _intermediateTextureId;
    private int _texWidth, _texHeight;
    private IntPtr _frameCopyBuffer = IntPtr.Zero;
    private nuint _frameCopyBufferSize;
    private bool _loggedSessionMessage;
    private bool _loggedRenderEntry;
    private bool _loggedLeaseMissing;
    private bool _loggedGlDiscovery;
    private bool _loggedAcquireLatestFrame;
    private bool _loggedCopyLatestFrame;
    private bool _loggedNoFrameAvailable;
    private bool _loggedGlRenderPath;
    private bool _loggedSimpleRenderPath;
    // Per-session flag: set when GL render fails so we fall back to CPU Skia for the remainder of the session
    private bool _glRenderFailed;
    private int _ownerInvalidateQueued;
    private int _ownerStatsUpdateQueued;

    private static void LogDebugOnce(ref bool flag, string message)
    {
        if (flag)
            return;

        flag = true;
        Log.Debug(message);
        Debug.WriteLine(message);
        Trace.WriteLine(message);
    }

    private static void LogWarnOnce(ref bool flag, string message)
    {
        if (flag)
            return;

        flag = true;
        Log.Warn(message);
        Debug.WriteLine(message);
        Trace.WriteLine(message);
    }

    public override void OnMessage(object? message)
    {
        if (message == null)
        {
            Cleanup();
            return;
        }

        if (message is WgcSessionMessage sm)
        {
            _session = sm.Session;
            _targetHwnd = sm.TargetHwnd;
            _ownerRef = sm.Owner;
            _useOwnerInvalidation = sm.UseOwnerInvalidation;
            _lastNativeFrameCount = -1;
            _ownerInvalidateQueued = 0;
            _ownerStatsUpdateQueued = 0;
            _loggedRenderEntry = false;
            _loggedLeaseMissing = false;
            _loggedGlDiscovery = false;
            _loggedAcquireLatestFrame = false;
            _loggedCopyLatestFrame = false;
            _loggedNoFrameAvailable = false;
            _loggedGlRenderPath = false;
            _loggedSimpleRenderPath = false;
            _glRenderFailed = false;
            LogDebugOnce(
                ref _loggedSessionMessage,
                $"WgcCaptureVisualHandler received session message. session=0x{_session.ToInt64():X}, " +
                $"target=0x{_targetHwnd.ToInt64():X}, useOwnerInvalidation={_useOwnerInvalidation}.");
            if (_session != nint.Zero && !_useOwnerInvalidation)
                RegisterForNextAnimationFrameUpdate();
            RequestRender();
        }
        else if (message is Vector2 size)
        {
            if (_visualSize != size)
            {
                _visualSize = size;
                _rectDirty = true;
                RequestRender();
            }
        }
        else if (message is WgcSettingsMessage st)
        {
            if (_stretch != st.Stretch)
            {
                _stretch = st.Stretch;
                _rectDirty = true;
            }

            _brightness = st.Brightness;
            _saturation = st.Saturation;
            _tint = st.Tint;
            _forceUseTargetClientSize = st.ForceUseTargetClientSize;
            _fpsSmoothing = st.FpsSmoothingFactor;
            _showStatisticsOverlay = st.ShowStatisticsOverlay;
            _showFrametimeGraph = st.ShowFrametimeGraph;
            _showDetailedGpuInfo = st.ShowDetailedGpuInfo;
            _overlayOpacity = st.OverlayOpacity;
            _overlayBackgroundColor = st.OverlayBackgroundColor;
            _enableAutoCrop = st.EnableAutoCrop && !st.ForceUseTargetClientSize;
            _overlayPosition = st.OverlayPosition;
            _settingsDirty = true;

            if (!_enableAutoCrop && (_cropLeft != 0 || _cropRight != 0))
            {
                _cropLeft = 0;
                _cropRight = 0;
                _rectDirty = true;
            }

            if (_retroarchShaderFile != st.RetroarchShaderFile)
            {
                _retroarchShaderFile = st.RetroarchShaderFile;
                // Pipeline will be re-initialized in OnRender when _gl is available
                _shaderPipeline?.Dispose();
                _shaderPipeline = null;
            }

            if (_session != nint.Zero && _vrrActive != st.DisableVSync)
            {
                _vrrActive = st.DisableVSync;
                WgcBridgeApi.SetVrrEnabled(_session, _vrrActive);
            }

            RequestRender();
        }
    }

    private void Cleanup()
    {
        _session = nint.Zero;
        _shaderPipeline?.Dispose();
        _shaderPipeline = null;

        if (_gl != null && _gl.ContextInfo != null)
        {
            if (_captureTextureId != 0) _gl.DeleteTexture(_captureTextureId);
            if (_intermediateTextureId != 0) _gl.DeleteTexture(_intermediateTextureId);
            if (_intermediateFbo != 0) _gl.DeleteFramebuffer(_intermediateFbo);
            _captureTextureId = 0;
            _intermediateTextureId = 0;
            _intermediateFbo = 0;
        }
        _glTexSubImage2DPtr = IntPtr.Zero;
        _ownerInvalidateQueued = 0;
        _ownerStatsUpdateQueued = 0;

        if (_frameCopyBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameCopyBuffer);
            _frameCopyBuffer = IntPtr.Zero;
            _frameCopyBufferSize = 0;
        }

        if (_paint != null)
        {
            _paint.ColorFilter?.Dispose();
            _paint.Dispose();
            _paint = null!;
        }

        _overlayGraphPath?.Dispose();
        _overlayGraphPath = null;
        _overlayTextPaint?.Dispose();
        _overlayTextPaint = null;
        _overlayDetailTextPaint?.Dispose();
        _overlayDetailTextPaint = null;
        _overlayBackgroundPaint?.Dispose();
        _overlayBackgroundPaint = null;
        _overlayLinePaint?.Dispose();
        _overlayLinePaint = null;
        _overlayGridPaint?.Dispose();
        _overlayGridPaint = null;
        _overlayGraphTextPaint?.Dispose();
        _overlayGraphTextPaint = null;
        _overlayTypefaceBold?.Dispose();
        _overlayTypefaceBold = null;
        _overlayTypefaceRegular?.Dispose();
        _overlayTypefaceRegular = null;
    }

    private void EnsureGl(ImmediateDrawingContext context, GRContext? grContext)
    {
        if (_gl != null) return;

        // Try multiple ways to get GL interface
        var glContext = context.TryGetFeature<IGlContext>();
        if (glContext != null)
        {
            _gl = glContext.GlInterface;
        }

        if (_gl == null)
        {
            var graphicsContext = context.TryGetFeature<IPlatformGraphicsContext>();
            if (graphicsContext != null)
            {
                _gl = graphicsContext.TryGetFeature<IGlContext>()?.GlInterface;
            }
        }

        // If we still don't have _gl but have a grContext, it's definitely hardware accelerated
        if (grContext != null)
        {
            _backendName = grContext.Backend.ToString();
            if (_gl == null)
            {
                Win32API.GetPrimaryGpuInfo(out _gpuRenderer, out _gpuVendor);
                if (_gpuRenderer == "Unknown") _gpuRenderer = "Skia Accelerated";
            }
        }

        if (_gl != null)
        {
            _backendName = "OpenGL";
            if (_glTexSubImage2DPtr == IntPtr.Zero)
                _glTexSubImage2DPtr = _gl.GetProcAddress("glTexSubImage2D");
            try
            {
                _gpuRenderer = _gl.GetString(0x1F01) ?? _gpuRenderer; // GL_RENDERER
                _gpuVendor = _gl.GetString(0x1F00) ?? _gpuVendor;     // GL_VENDOR

                var version = _gl.GetString(0x1F02); // GL_VERSION
                if (!string.IsNullOrEmpty(version))
                {
                    if (version.Contains("ES")) _backendName = "OpenGL ES";
                    else _backendName = "OpenGL " + version.Split(' ')[0];
                }
            }
            catch (Exception ex)
            {
                LogWarnOnce(ref _loggedGlDiscovery, $"WgcCaptureVisualHandler failed while querying GL strings: {ex}");
            }
        }

        LogDebugOnce(
            ref _loggedGlDiscovery,
            $"WgcCaptureVisualHandler EnsureGl: backend={_backendName}, grContext={(grContext != null ? "available" : "null")}, " +
            $"glInterface={(_gl != null ? "available" : "null")}, renderer={_gpuRenderer}, vendor={_gpuVendor}.");
    }

    public override void OnAnimationFrameUpdate()
    {
        if (_session == nint.Zero)
            return;

        try
        {
            // Perform per-frame updates for FPS tracking
            var nowTicks = Stopwatch.GetTimestamp();
            if (_lastFrameTicks != 0)
            {
                double dt = (double)(nowTicks - _lastFrameTicks) / Stopwatch.Frequency;
                double instantFps = dt > 0 ? (1.0 / dt) : 0.0;
                double frameMs = dt * 1000.0;

                double s = Math.Clamp(_fpsSmoothing, 0.0, 0.999);
                _smoothedFps = (_smoothedFps <= 0.0) ? instantFps : (_smoothedFps * s) + (instantFps * (1.0 - s));
                _smoothedFrameTimeMs = (_smoothedFrameTimeMs <= 0.0) ? frameMs : (_smoothedFrameTimeMs * s) + (frameMs * (1.0 - s));

                if (_showFrametimeGraph)
                {
                    _frameTimes[_frameTimePtr] = (float)frameMs;
                    _frameTimePtr = (_frameTimePtr + 1) % _frameTimes.Length;
                }
            }
            _lastFrameTicks = nowTicks;

            if ((double)(nowTicks - _lastUiUpdateTicks) / Stopwatch.Frequency >= 0.1)
            {
                _lastUiUpdateTicks = nowTicks;

                var fps = Math.Round(_smoothedFps, 1);
                var ft = Math.Round(_smoothedFrameTimeMs, 2);

                if ((fps != _lastSentFps || ft != _lastSentFt) && _ownerRef != null && _ownerRef.TryGetTarget(out var owner))
                {
                    _lastSentFps = fps;
                    _lastSentFt = ft;

                    if (Interlocked.Exchange(ref _ownerStatsUpdateQueued, 1) == 0)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                owner.Fps = fps;
                                owner.FrameTimeMs = ft;
                            }
                            finally
                            {
                                Volatile.Write(ref _ownerStatsUpdateQueued, 0);
                            }
                        }, DispatcherPriority.Background);
                    }
                }
            }

            if (_forceUseTargetClientSize && _targetHwnd != IntPtr.Zero)
            {
                if (Win32API.GetClientAreaOffsets(_targetHwnd, out int cx, out int cy, out int cw, out int ch))
                {
                    if (cx != _lastCropX || cy != _lastCropY || cw != _lastCropW || ch != _lastCropH)
                    {
                        WgcBridgeApi.SetCaptureCropRect(_session, cx, cy, cw, ch);
                        _lastCropX = cx; _lastCropY = cy; _lastCropW = cw; _lastCropH = ch;
                    }
                }
            }

            int nativeCount = WgcBridgeApi.GetCaptureStatus(_session);
            if (nativeCount != _lastNativeFrameCount)
            {
                _lastNativeFrameCount = nativeCount;
            }

            // Under NativeAOT the composition custom visual can stall if we only redraw on
            // capture-status transitions, so keep the visual invalidating while a session is active.
            if (!_useOwnerInvalidation)
                RequestRender();
        }
        catch (Exception ex)
        {
            Log.Error("WgcCaptureVisualHandler OnAnimationFrameUpdate failed.", ex);
            Debug.WriteLine($"WgcCaptureVisualHandler OnAnimationFrameUpdate failed. {ex}");
            Trace.WriteLine($"WgcCaptureVisualHandler OnAnimationFrameUpdate failed. {ex}");
        }
        finally
        {
            if (!_useOwnerInvalidation && _session != nint.Zero)
                RegisterForNextAnimationFrameUpdate();
        }
    }

    private void RequestRender()
    {
        if (_useOwnerInvalidation)
        {
            if (_ownerRef?.TryGetTarget(out var owner) == true)
            {
                if (Interlocked.Exchange(ref _ownerInvalidateQueued, 1) == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            owner.InvalidateVisual();
                        }
                        finally
                        {
                            Volatile.Write(ref _ownerInvalidateQueued, 0);
                        }
                    }, DispatcherPriority.Render);
                }
            }
            return;
        }

        Invalidate();
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X < 1 || _visualSize.Y < 1)
            return;

        try
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null)
            {
                LogWarnOnce(ref _loggedLeaseMissing, "WgcCaptureVisualHandler OnRender could not get ISkiaSharpApiLeaseFeature.");
                return;
            }

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            var grContext = lease.GrContext;

            // Always clear the target first.
            // This ensures the previous captured frame is not left behind when the session is stopped.
            canvas.Clear(SKColors.Black);

            var activeSession = _session;
            if (activeSession == nint.Zero)
                return;

            LogDebugOnce(
                ref _loggedRenderEntry,
                $"WgcCaptureVisualHandler OnRender entered. session=0x{activeSession.ToInt64():X}, size={_visualSize.X}x{_visualSize.Y}, " +
                $"ownerInvalidation={_useOwnerInvalidation}.");

            EnsureGl(context, grContext);

        if (WgcBridgeApi.AcquireLatestFrame(activeSession, out IntPtr ptr, out nuint size, out int w, out int h))
            {
                LogDebugOnce(
                    ref _loggedAcquireLatestFrame,
                    $"WgcCaptureVisualHandler acquired latest frame directly. size={w}x{h}, bytes={size}, ptr={(ptr != IntPtr.Zero ? "valid" : "null")}.");
                try
                {
                    if (w > 0 && h > 0 && ptr != IntPtr.Zero)
                    {
                        AutoDetectPillarboxes(ptr, w, h);

                        if (_rectDirty || _texWidth != w || _texHeight != h)
                        {
                            _cachedDestRect = CalculateAspectRect(_visualSize.X, _visualSize.Y, w - _cropLeft - _cropRight, h);
                            _rectDirty = false;
                        }

                        if (_gl != null && !_glRenderFailed)
                        {
                            try
                            {
                                LogDebugOnce(ref _loggedGlRenderPath, "WgcCaptureVisualHandler is rendering through the GL path.");
                                RenderInternal(canvas, ptr, w, h, grContext);
                            }
                            catch (Exception glEx)
                            {
                                _glRenderFailed = true;
                                Log.Warn($"WgcCaptureVisualHandler GL render failed, falling back to CPU Skia path for this session. {glEx}");
                                RenderSimpleFallback(canvas, ptr, w, h);
                            }
                        }
                        else
                        {
                            LogDebugOnce(ref _loggedSimpleRenderPath, "WgcCaptureVisualHandler is rendering through the CPU Skia path.");
                            RenderSimpleFallback(canvas, ptr, w, h);
                        }

                        if (_showStatisticsOverlay)
                        {
                            RenderOverlay(canvas);
                        }
                    }
                }
                finally
                {
                    WgcBridgeApi.ReleaseLatestFrame(activeSession);
                }
            }
            else if (TryCopyLatestFrame(activeSession, out ptr, out w, out h))
            {
                LogDebugOnce(
                    ref _loggedCopyLatestFrame,
                    $"WgcCaptureVisualHandler acquired latest frame through copy fallback. size={w}x{h}, ptr={(ptr != IntPtr.Zero ? "valid" : "null")}.");
                if (w > 0 && h > 0 && ptr != IntPtr.Zero)
                {
                    AutoDetectPillarboxes(ptr, w, h);

                    if (_rectDirty || _texWidth != w || _texHeight != h)
                    {
                        _cachedDestRect = CalculateAspectRect(_visualSize.X, _visualSize.Y, w - _cropLeft - _cropRight, h);
                        _rectDirty = false;
                    }

                    if (_gl != null && !_glRenderFailed)
                    {
                        try
                        {
                            LogDebugOnce(ref _loggedGlRenderPath, "WgcCaptureVisualHandler is rendering through the GL path.");
                            RenderInternal(canvas, ptr, w, h, grContext);
                        }
                        catch (Exception glEx)
                        {
                            _glRenderFailed = true;
                            Log.Warn($"WgcCaptureVisualHandler GL render failed, falling back to CPU Skia path for this session. {glEx}");
                            RenderSimpleFallback(canvas, ptr, w, h);
                        }
                    }
                    else
                    {
                        LogDebugOnce(ref _loggedSimpleRenderPath, "WgcCaptureVisualHandler is rendering through the CPU Skia path.");
                        RenderSimpleFallback(canvas, ptr, w, h);
                    }

                    if (_showStatisticsOverlay)
                        RenderOverlay(canvas);
                }
            }
            else
            {
                LogWarnOnce(ref _loggedNoFrameAvailable, "WgcCaptureVisualHandler could not acquire any frame in OnRender.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("WgcCaptureVisualHandler OnRender failed.", ex);
            Debug.WriteLine($"WgcCaptureVisualHandler OnRender failed. {ex}");
            Trace.WriteLine($"WgcCaptureVisualHandler OnRender failed. {ex}");
        }
    }

    private bool TryCopyLatestFrame(nint session, out IntPtr ptr, out int width, out int height)
    {
        ptr = IntPtr.Zero;
        width = 0;
        height = 0;

        if (session == IntPtr.Zero)
            return false;

        if (!WgcBridgeApi.PeekLatestFrame(session, out int peekWidth, out int peekHeight, out nuint requiredSize) ||
            peekWidth <= 0 ||
            peekHeight <= 0 ||
            requiredSize == 0)
        {
            return false;
        }

        EnsureFrameCopyBuffer(requiredSize);
        if (_frameCopyBuffer == IntPtr.Zero)
            return false;

        if (!WgcBridgeApi.GetLatestFrame(session, _frameCopyBuffer, _frameCopyBufferSize, out width, out height) ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        ptr = _frameCopyBuffer;
        return true;
    }

    private void EnsureFrameCopyBuffer(nuint requiredSize)
    {
        if (requiredSize == 0)
            return;

        if (_frameCopyBuffer != IntPtr.Zero && _frameCopyBufferSize >= requiredSize)
            return;

        if (_frameCopyBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameCopyBuffer);
            _frameCopyBuffer = IntPtr.Zero;
            _frameCopyBufferSize = 0;
        }

        try
        {
            _frameCopyBuffer = Marshal.AllocHGlobal(checked((nint)requiredSize));
            _frameCopyBufferSize = requiredSize;
        }
        catch (Exception ex)
        {
            LogWarnOnce(ref _loggedCopyLatestFrame, $"WgcCaptureVisualHandler failed to allocate frame copy buffer ({requiredSize} bytes): {ex}");
            _frameCopyBuffer = IntPtr.Zero;
            _frameCopyBufferSize = 0;
        }
    }

    private void RenderOverlay(SKCanvas canvas)
    {
        EnsureOverlayResources();
        if (_overlayTextPaint == null ||
            _overlayDetailTextPaint == null ||
            _overlayBackgroundPaint == null)
            return;

        _overlayBackgroundPaint.Color = new SKColor(
            _overlayBackgroundColor.R,
            _overlayBackgroundColor.G,
            _overlayBackgroundColor.B,
            (byte)(_overlayOpacity * 255));

        byte textAlpha = (byte)Math.Clamp(_overlayOpacity * 1.5f * 255, 0, 255);
        _overlayTextPaint.Color = SKColors.White.WithAlpha(textAlpha);
        _overlayDetailTextPaint.Color = SKColors.White.WithAlpha(textAlpha);

        float x = _overlayPosition.X;
        float y = _overlayPosition.Y;
        float lineH = 20;

        // Background box for readability
        float boxW = _showDetailedGpuInfo ? 350 : 200;
        float boxH = lineH * 3 + 10;
        if (_showDetailedGpuInfo) boxH += lineH * 2;
        if (_showFrametimeGraph) boxH += 60;
        canvas.DrawRoundRect(x - 10, y - 20, boxW, boxH, 8, 8, _overlayBackgroundPaint);

        canvas.DrawText($"Backend: {_backendName}", x, y, _overlayTextPaint);
        y += lineH;
        canvas.DrawText($"FPS: {Math.Round(_smoothedFps, 1)}", x, y, _overlayTextPaint);
        y += lineH;
        canvas.DrawText($"VRR/VSync: {(_vrrActive ? "On" : "Off")}", x, y, _overlayTextPaint);
        y += lineH;

        if (_showDetailedGpuInfo)
        {
            canvas.DrawText($"GPU: {_gpuRenderer}", x, y, _overlayDetailTextPaint);
            y += lineH;
            canvas.DrawText($"Vendor: {_gpuVendor}", x, y, _overlayDetailTextPaint);
            y += lineH;
        }

        if (_showFrametimeGraph)
        {
            y += 5;
            float graphW = _showDetailedGpuInfo ? 330 : 180;
            RenderFrametimeGraph(canvas, x, y, graphW, 40);
        }
    }

    private void RenderFrametimeGraph(SKCanvas canvas, float x, float y, float w, float h)
    {
        EnsureOverlayResources();
        if (_overlayLinePaint == null ||
            _overlayGridPaint == null ||
            _overlayGraphTextPaint == null ||
            _overlayGraphPath == null)
            return;

        // Grid lines (16.6ms, 33.3ms)
        float ms16 = y + h - (16.6f / 50f * h);
        float ms33 = y + h - (33.3f / 50f * h);
        canvas.DrawLine(x, ms16, x + w, ms16, _overlayGridPaint);
        canvas.DrawLine(x, ms33, x + w, ms33, _overlayGridPaint);

        _overlayGraphPath.Reset();
        float step = w / (_frameTimes.Length - 1);

        for (int i = 0; i < _frameTimes.Length; i++)
        {
            int idx = (_frameTimePtr + i) % _frameTimes.Length;
            float val = Math.Clamp(_frameTimes[idx], 0, 50); // cap at 50ms for display
            float py = y + h - (val / 50f * h);
            float px = x + i * step;

            if (i == 0) _overlayGraphPath.MoveTo(px, py);
            else _overlayGraphPath.LineTo(px, py);
        }

        canvas.DrawPath(_overlayGraphPath, _overlayLinePaint);
        canvas.DrawText($"{Math.Round(_smoothedFrameTimeMs, 2)} ms", x + w - 50, y - 5, _overlayGraphTextPaint);
    }

    private unsafe void AutoDetectPillarboxes(IntPtr ptr, int w, int h)
    {
        if (!_enableAutoCrop || w < 100 || h < 100)
        {
            if (_cropLeft != 0 || _cropRight != 0) { _cropLeft = 0; _cropRight = 0; _rectDirty = true; }
            return;
        }

        byte* pixels = (byte*)ptr.ToPointer();
        int stride = w * 4;

        // Use stackalloc to avoid per-frame heap allocations.
        Span<int> rows = stackalloc int[15]
        {
            h / 16, h / 8, 3 * h / 16, h / 4, 5 * h / 16,
            3 * h / 8, 7 * h / 16, h / 2, 9 * h / 16, 5 * h / 8,
            11 * h / 16, 3 * h / 4, 13 * h / 16, 7 * h / 8, 15 * h / 16
        };
        int maxScan = w / 4;
        const int contentThreshold = 1; // Extremely sensitive to remove almost-black edges

        // Verify there is SOME content in sampled middle before trusting a crop (prevents cropping purely black screens)
        bool hasContent = false;
        Span<int> centerSampleX = stackalloc int[5] { w / 2, w / 3, 2 * w / 3, w / 4, 3 * w / 4 };
        foreach (int x in centerSampleX)
        {
            foreach (int r in rows)
            {
                byte* p = pixels + (r * stride) + (x * 4);
                if (p[0] > 3 || p[1] > 3 || p[2] > 3) { hasContent = true; break; }
            }
            if (hasContent) break;
        }
        if (!hasContent) return;

        int detectedLeft = 0;
        for (int x = 0; x < maxScan; x++)
        {
            bool hasContentInCol = false;
            foreach (int r in rows)
            {
                byte* p = pixels + (r * stride) + (x * 4);
                if (p[0] > contentThreshold || p[1] > contentThreshold || p[2] > contentThreshold) { hasContentInCol = true; break; }
            }
            if (hasContentInCol)
            {
                // Minimized margin to 1 pixel for a cleaner cut
                detectedLeft = Math.Max(0, x - 1);
                break;
            }
        }

        int detectedRight = 0;
        for (int x = w - 1; x > w - 1 - maxScan; x--)
        {
            bool hasContentInCol = false;
            foreach (int r in rows)
            {
                byte* p = pixels + (r * stride) + (x * 4);
                if (p[0] > contentThreshold || p[1] > contentThreshold || p[2] > contentThreshold) { hasContentInCol = true; break; }
            }
            if (hasContentInCol)
            {
                // Minimized margin
                detectedRight = Math.Max(0, (w - 1 - x) - 1);
                break;
            }
        }

        bool changed = false;
        // Fast shrink (instantly recover content if non-black is found inside existing crop)
        if (detectedLeft < _cropLeft) { _cropLeft = detectedLeft; changed = true; _consecutiveBlackFrames = 0; }
        if (detectedRight < _cropRight) { _cropRight = detectedRight; changed = true; _consecutiveBlackFrames = 0; }

        // Slow expand (wait for consistency before removing bars)
        if (detectedLeft > _cropLeft || detectedRight > _cropRight)
        {
            _consecutiveBlackFrames++;
            if (_consecutiveBlackFrames > 30) // Reduced from 120 (~0.5sec at 60fps) for faster reaction
            {
                _cropLeft = detectedLeft;
                _cropRight = detectedRight;
                _consecutiveBlackFrames = 0;
                changed = true;
            }
        }
        else _consecutiveBlackFrames = 0;

        if (changed) _rectDirty = true;
    }


    private unsafe void RenderInternal(SKCanvas canvas, IntPtr ptr, int w, int h, GRContext? grContext)
    {
        if (_gl == null || grContext == null) return;

        var shaderFile = _retroarchShaderFile;
        bool hasShader = !string.IsNullOrEmpty(shaderFile);

        // Initialize or Update Pipeline if shader is present
        if (hasShader && _shaderPipeline == null)
        {
            try
            {
                _shaderPipeline = new SlangShaderPipeline(_gl);
                _shaderPipeline.LoadShaderPreset(shaderFile!);
            }
            catch (Exception ex)
            {
                LogWarnOnce(ref _loggedGlRenderPath, $"WgcCaptureVisualHandler failed to initialize Slang shader pipeline: {ex}");
                _shaderPipeline = null;
            }
        }

        // Ensure GL resources for the current frame size
        if (_captureTextureId == 0 || _texWidth != w || _texHeight != h)
        {
            InitializeGlResources(w, h);
        }

        // 1. Upload WGC frame to GPU
        _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _captureTextureId);
        if (_glTexSubImage2DPtr == IntPtr.Zero)
            _glTexSubImage2DPtr = _gl.GetProcAddress("glTexSubImage2D");

        if (_glTexSubImage2DPtr != IntPtr.Zero)
        {
            var texSub = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, uint, int, IntPtr, void>)_glTexSubImage2DPtr;
            texSub(GlConsts.GL_TEXTURE_2D, 0, 0, 0, w, h, 0x80E1u, GlConsts.GL_UNSIGNED_BYTE, ptr); // 0x80E1 = GL_BGRA
        }

        int finalTextureId = _captureTextureId;

        // 2. Apply Shader if active
        if (hasShader && _shaderPipeline != null && _shaderPipeline.HasActiveShader)
        {
            _shaderPipeline.Brightness = _brightness;
            _shaderPipeline.Saturation = _saturation;
            _shaderPipeline.ColorTint = new[] { _tint.R / 255f, _tint.G / 255f, _tint.B / 255f, _tint.A / 255f };
            _shaderPipeline.Process(_captureTextureId, w, h, _intermediateFbo, 0, 0, w, h);
            finalTextureId = _intermediateTextureId;
        }

        // Tell Skia its cached GL state is stale after our raw GL calls (glBindTexture, glTexSubImage2D,
        // and any shader draw calls made outside of Skia's knowledge).
        // This MUST be called before any subsequent Skia API use of the GrContext.
        grContext.ResetContext();

        // 3. Draw directly from GPU texture.
        // Use FromTexture (borrow) instead of FromAdoptedTexture (take ownership).
        // FromAdoptedTexture would cause Skia to call glDeleteTextures on _captureTextureId
        // when the SKImage is disposed, corrupting the texture ID we reuse every frame.
        var glInfo = new GRGlTextureInfo
        {
            Id = (uint)finalTextureId,
            Target = (uint)GlConsts.GL_TEXTURE_2D,
            Format = 0x8058 // GL_RGBA8
        };

        using var backendTexture = new GRBackendTexture(w, h, false, glInfo);
        using var skImage = SKImage.FromTexture(grContext, backendTexture, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);

        if (skImage != null)
        {
            if (_settingsDirty && !hasShader) { UpdatePaint(); _settingsDirty = false; }
            SKRect srcRect = new SKRect(_cropLeft, 0, w - _cropRight, h);
            canvas.DrawImage(skImage, srcRect, _cachedDestRect, hasShader ? null : _paint);
        }
    }

    private void RenderSimpleFallback(SKCanvas canvas, IntPtr ptr, int w, int h)
    {
        if (w <= 0 || h <= 0 || ptr == IntPtr.Zero)
            return;

        int cropLeft = Math.Max(0, _cropLeft);
        int cropRight = Math.Max(0, _cropRight);
        if (cropLeft + cropRight >= w)
            return;

        SKRect srcRect = new SKRect(cropLeft, 0, w - cropRight, h);
        if (srcRect.Width <= 0 || srcRect.Height <= 0)
            return;

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

        // Prefer a full SKImage copy path for safety when rendering through Skia.
        // This avoids lifetime issues and invalid pointer access inside DrawBitmap.
        using var pixmap = new SKPixmap(info, ptr);
        using var image = SKImage.FromPixels(pixmap);
        if (image == null)
            return;

        if (_settingsDirty) { UpdatePaint(); _settingsDirty = false; }

        try
        {
            canvas.DrawImage(image, srcRect, _cachedDestRect, _paint);
        }
        catch (Exception ex)
        {
            Log.Error("WgcCaptureVisualHandler RenderSimpleFallback.DrawImage failed.", ex);
        }
    }

    private void InitializeGlResources(int w, int h)
    {
        if (_gl == null) return;

        if (_captureTextureId != 0) _gl.DeleteTexture(_captureTextureId);
        _captureTextureId = _gl.GenTexture();
        _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _captureTextureId);
        _gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        _gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
        _gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA, w, h, 0, 0x80E1, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);

        if (_intermediateTextureId != 0) _gl.DeleteTexture(_intermediateTextureId);
        if (_intermediateFbo != 0) _gl.DeleteFramebuffer(_intermediateFbo);

        _intermediateFbo = _gl.GenFramebuffer();
        _intermediateTextureId = _gl.GenTexture();
        _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _intermediateTextureId);
        _gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA, w, h, 0, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
        _gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        _gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        _gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, _intermediateFbo);
        _gl.FramebufferTexture2D(GlConsts.GL_FRAMEBUFFER, GlConsts.GL_COLOR_ATTACHMENT0, GlConsts.GL_TEXTURE_2D, _intermediateTextureId, 0);

        _texWidth = w; _texHeight = h;
    }

    private void UpdatePaint()
    {
        if (_paint == null) return;

        // Dispose old filter to avoid memory leaks
        _paint.ColorFilter?.Dispose();

        // Combine Brightness, Saturation and Tint into a single ColorMatrix
        // Luma-preserving saturation matrix
        float rWeight = 0.299f;
        float gWeight = 0.587f;
        float bWeight = 0.114f;

        float oneMinusSat = 1.0f - _saturation;
        float rAlpha = oneMinusSat * rWeight;
        float gAlpha = oneMinusSat * gWeight;
        float bAlpha = oneMinusSat * bWeight;

        float[] matrix = new float[]
        {
            (rAlpha + _saturation) * _brightness * (_tint.R / 255f), gAlpha * _brightness * (_tint.G / 255f), bAlpha * _brightness * (_tint.B / 255f), 0, 0,
            rAlpha * _brightness * (_tint.R / 255f), (gAlpha + _saturation) * _brightness * (_tint.G / 255f), bAlpha * _brightness * (_tint.B / 255f), 0, 0,
            rAlpha * _brightness * (_tint.R / 255f), gAlpha * _brightness * (_tint.G / 255f), (bAlpha + _saturation) * _brightness * (_tint.B / 255f), 0, 0,
            0, 0, 0, _tint.A / 255f, 0
        };

        _paint.ColorFilter = SKColorFilter.CreateColorMatrix(matrix);
    }

    private void EnsureOverlayResources()
    {
        _overlayTypefaceBold ??= SKTypeface.FromFamilyName(
            "Consolas",
            SKFontStyleWeight.Bold,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        _overlayTypefaceRegular ??= SKTypeface.FromFamilyName("Consolas");

        _overlayTextPaint ??= new SKPaint
        {
            Color = SKColors.White.WithAlpha(220),
            IsAntialias = true,
            TextSize = 16,
            Typeface = _overlayTypefaceBold
        };

        _overlayDetailTextPaint ??= new SKPaint
        {
            Color = SKColors.White.WithAlpha(220),
            IsAntialias = true,
            TextSize = 14,
            Typeface = _overlayTypefaceBold
        };

        _overlayBackgroundPaint ??= new SKPaint
        {
            IsAntialias = true
        };

        _overlayLinePaint ??= new SKPaint
        {
            Color = SKColors.Cyan.WithAlpha(180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        _overlayGridPaint ??= new SKPaint
        {
            Color = SKColors.White.WithAlpha(40),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        };

        _overlayGraphTextPaint ??= new SKPaint
        {
            Color = SKColors.Cyan,
            TextSize = 12,
            IsAntialias = true,
            Typeface = _overlayTypefaceRegular
        };

        _overlayGraphPath ??= new SKPath();
    }

    private SKRect CalculateAspectRect(float viewW, float viewH, float frameW, float frameH)
    {
        if (_stretch == Stretch.Fill) return new SKRect(0, 0, viewW, viewH);

        float viewAspect = viewW / viewH;
        float frameAspect = frameW / frameH;

        if (_stretch == Stretch.Uniform)
        {
            if (frameAspect > viewAspect)
            {
                float h = viewW / frameAspect;
                return new SKRect(0, (viewH - h) / 2, viewW, (viewH + h) / 2);
            }
            else
            {
                float w = viewH * frameAspect;
                return new SKRect((viewW - w) / 2, 0, (viewW + w) / 2, viewH);
            }
        }
        else if (_stretch == Stretch.UniformToFill)
        {
            if (frameAspect > viewAspect)
            {
                float w = viewH * frameAspect;
                return new SKRect((viewW - w) / 2, 0, (viewW + w) / 2, viewH);
            }
            else
            {
                float h = viewW / frameAspect;
                return new SKRect(0, (viewH - h) / 2, viewW, (viewH + h) / 2);
            }
        }

        return new SKRect((viewW - frameW) / 2, (viewH - frameH) / 2, (viewW + frameW) / 2, (viewH + frameH) / 2);
    }
}
