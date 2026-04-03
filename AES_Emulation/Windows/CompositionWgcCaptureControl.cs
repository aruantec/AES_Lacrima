using AES_Controls.EmuGrabbing.ShaderHandling;
using AES_Emulation.Windows.API;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Numerics;

namespace AES_Emulation.Windows;

public class CompositionWgcCaptureControl : Control
{
    private CompositionCustomVisual? _visual;
    private nint _session = nint.Zero;
    private WindowHandler? _windowHandler;
    private nint _hostHandle = nint.Zero;

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

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, Stretch>(nameof(Stretch), Stretch.Uniform);

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

    public static readonly StyledProperty<bool> ShowStatisticsOverlayProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(ShowStatisticsOverlay), true);

    public bool ShowStatisticsOverlay
    {
        get => GetValue(ShowStatisticsOverlayProperty);
        set => SetValue(ShowStatisticsOverlayProperty, value);
    }

    public static readonly StyledProperty<bool> ShowFrametimeGraphProperty =
        AvaloniaProperty.Register<CompositionWgcCaptureControl, bool>(nameof(ShowFrametimeGraph), true);

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
        ClipToBounds = true;
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
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor != null)
        {
            _visual = compositor.CreateCustomVisual(new WgcCaptureVisualHandler());
            ElementComposition.SetElementChildVisual(this, _visual);

            UpdateHandlerSize();
            UpdateHandlerSession();
            UpdateHandlerSettings();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mw = desktop.MainWindow as TopLevel;
                if (mw != null && mw.TryGetPlatformHandle() is IPlatformHandle platform)
                {
                    _hostHandle = platform.Handle;
                    StartSession();
                }
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopSession();
        if (_visual != null)
        {
            _visual.SendHandlerMessage(null); // Cleanup signal
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }
    }

    private void StartSession()
    {
        if (TargetHwnd == IntPtr.Zero || _hostHandle == IntPtr.Zero) return;

        StopSession();

        // Prepare target window
        Win32API.RemoveWindowDecorations(TargetHwnd);
        Win32API.MoveAway(TargetHwnd);
        Win32API.SetWindowOpacity(TargetHwnd, 0);

        _windowHandler = new WindowHandler(10, 4, 4, 4, 4);
        _windowHandler.EnableRoundedCorners(44);
        _windowHandler.SetMoveToHost(false);
        _windowHandler.Start(_hostHandle, TargetHwnd);

        _session = WgcBridgeApi.CreateCaptureSession(TargetHwnd);
        if (_session != nint.Zero)
        {
            if (DisableDownscale)
                WgcBridgeApi.SetCaptureMaxResolution(_session, 0, 0);
            else
                WgcBridgeApi.SetCaptureMaxResolution(_session, 4096, 1080);

            UpdateHandlerSession();
            UpdateHandlerSettings();
        }
    }

    private void StopSession()
    {
        if (_windowHandler != null)
        {
            _windowHandler.Stop();
            _windowHandler.RestoreOriginalPosition();
            _windowHandler = null;

            Win32API.RestoreWindowDecorations(TargetHwnd);
            Win32API.SetWindowOpacity(TargetHwnd, 255);
        }

        if (_session != nint.Zero)
        {
            WgcBridgeApi.DestroyCaptureSession(_session);
            _session = nint.Zero;
            UpdateHandlerSession();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TargetHwndProperty)
        {
            if (_visual == null) return;
            //Start or stop session
            StartSession();
        }
        else if (change.Property == RequestStopSessionProperty)
        {
            try
            {
                var requested = change.NewValue is bool b && b;
                if (requested)
                {
                    // Immediately stop session while TargetHwnd still holds the handle
                    try
                    {
                        if (_windowHandler != null)
                        {
                            _windowHandler.Stop();
                            _windowHandler.RestoreOriginalPosition();
                            _windowHandler = null;
                            Win32API.RestoreWindowDecorations(TargetHwnd);
                            Win32API.SetWindowOpacity(TargetHwnd, 255);
                        }
                    }
                    catch { }
                    // Reset the request flag so subsequent clears don't re-trigger
                    SetValue(RequestStopSessionProperty, false);
                }
            }
            catch { }
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
        if (_visual != null)
        {
            var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
            _visual.Size = size;
            _visual.SendHandlerMessage(size);
        }
    }

    private void UpdateHandlerSession()
    {
        _visual?.SendHandlerMessage(new WgcSessionMessage
        {
            Session = _session,
            TargetHwnd = TargetHwnd,
            Owner = new WeakReference<CompositionWgcCaptureControl>(this)
        });
    }

    private void UpdateHandlerSettings()
    {
        _visual?.SendHandlerMessage(new WgcSettingsMessage
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
            EnableAutoCrop = EnableAutoCrop,
            OverlayPosition = new Vector2((float)OverlayPosition.X, (float)OverlayPosition.Y)
        });
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateHandlerSize();
    }
}

internal class WgcSessionMessage
{
    public nint Session;
    public nint TargetHwnd;
    public WeakReference<CompositionWgcCaptureControl>? Owner;
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

public class WgcCaptureVisualHandler : CompositionCustomVisualHandler
{
    private nint _session = nint.Zero;
    private nint _targetHwnd = nint.Zero;
    private WeakReference<CompositionWgcCaptureControl>? _ownerRef;
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
    private int _captureTextureId;
    private int _intermediateFbo;
    private int _intermediateTextureId;
    private int _texWidth, _texHeight;

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
            _lastNativeFrameCount = -1;
            if (_session != nint.Zero) RegisterForNextAnimationFrameUpdate();
        }
        else if (message is Vector2 size)
        {
            if (_visualSize != size)
            {
                _visualSize = size;
                _rectDirty = true;
                Invalidate();
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
            _enableAutoCrop = st.EnableAutoCrop;
            _overlayPosition = st.OverlayPosition;
            _settingsDirty = true;

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

            Invalidate();
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

        if (_paint != null)
        {
            _paint.ColorFilter?.Dispose();
            _paint.Dispose();
            _paint = null!;
        }
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
            catch { }
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        if (_session == nint.Zero) return;

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

            // Store for graph
            if (_showFrametimeGraph)
            {
                _frameTimes[_frameTimePtr] = (float)frameMs;
                _frameTimePtr = (_frameTimePtr + 1) % _frameTimes.Length;
            }
        }
        _lastFrameTicks = nowTicks;

        // Throttle UI property updates (approx 30 FPS)
        if ((double)(nowTicks - _lastUiUpdateTicks) / Stopwatch.Frequency >= 0.033)
        {
            _lastUiUpdateTicks = nowTicks;

            var fps = Math.Round(_smoothedFps, 1);
            var ft = Math.Round(_smoothedFrameTimeMs, 2);

            if ((fps != _lastSentFps || ft != _lastSentFt) && _ownerRef != null && _ownerRef.TryGetTarget(out var owner))
            {
                _lastSentFps = fps;
                _lastSentFt = ft;
                Dispatcher.UIThread.Post(() =>
                {
                    owner.Fps = fps;
                    owner.FrameTimeMs = ft;
                }, DispatcherPriority.Background);
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
            Invalidate();
        }

        RegisterForNextAnimationFrameUpdate();
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_session == nint.Zero || _visualSize.X < 1 || _visualSize.Y < 1) return;

        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        var grContext = lease.GrContext;

        EnsureGl(context, grContext);

        if (WgcBridgeApi.AcquireLatestFrame(_session, out IntPtr ptr, out nuint size, out int w, out int h))
        {
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

                    // Always use GL path if available for better performance
                    if (_gl != null)
                    {
                        RenderInternal(canvas, ptr, w, h, grContext);
                    }
                    else
                    {
                        RenderSimpleFallback(canvas, ptr, w, h);
                    }

                    // Render Statistics Overlay
                    if (_showStatisticsOverlay)
                    {
                        RenderOverlay(canvas);
                    }
                }
            }
            finally
            {
                WgcBridgeApi.ReleaseLatestFrame(_session);
            }
        }
    }

    private void RenderOverlay(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(220),
            IsAntialias = true,
            TextSize = 16,
            Typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        };

        float x = _overlayPosition.X;
        float y = _overlayPosition.Y;
        float lineH = 20;

        // Background box for readability
        using (var bgPaint = new SKPaint
        {
            Color = new SKColor(_overlayBackgroundColor.R, _overlayBackgroundColor.G, _overlayBackgroundColor.B, (byte)(_overlayOpacity * 255)),
            IsAntialias = true
        })
        {
            float boxW = 200;
            if (_showDetailedGpuInfo) boxW = 350;

            float boxH = lineH * 3 + 10;
            if (_showDetailedGpuInfo) boxH += lineH * 2;
            if (_showFrametimeGraph) boxH += 60;

            canvas.DrawRoundRect(x - 10, y - 20, boxW, boxH, 8, 8, bgPaint);
        }

        canvas.DrawText($"Backend: {_backendName}", x, y, paint);
        y += lineH;
        canvas.DrawText($"FPS: {Math.Round(_smoothedFps, 1)}", x, y, paint);
        y += lineH;
        canvas.DrawText($"VRR/VSync: {(_vrrActive ? "On" : "Off")}", x, y, paint);
        y += lineH;

        if (_showDetailedGpuInfo)
        {
            paint.TextSize = 14;
            canvas.DrawText($"GPU: {_gpuRenderer}", x, y, paint);
            y += lineH;
            canvas.DrawText($"Vendor: {_gpuVendor}", x, y, paint);
            y += lineH;
            paint.TextSize = 16;
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
        using var linePaint = new SKPaint
        {
            Color = SKColors.Cyan.WithAlpha(180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        using var gridPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(40),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f
        };

        // Grid lines (16.6ms, 33.3ms)
        float ms16 = y + h - (16.6f / 50f * h);
        float ms33 = y + h - (33.3f / 50f * h);
        canvas.DrawLine(x, ms16, x + w, ms16, gridPaint);
        canvas.DrawLine(x, ms33, x + w, ms33, gridPaint);

        using var path = new SKPath();
        float step = w / (_frameTimes.Length - 1);

        for (int i = 0; i < _frameTimes.Length; i++)
        {
            int idx = (_frameTimePtr + i) % _frameTimes.Length;
            float val = Math.Clamp(_frameTimes[idx], 0, 50); // cap at 50ms for display
            float py = y + h - (val / 50f * h);
            float px = x + i * step;

            if (i == 0) path.MoveTo(px, py);
            else path.LineTo(px, py);
        }

        canvas.DrawPath(path, linePaint);

        // Value label
        using var textPaint = new SKPaint
        {
            Color = SKColors.Cyan,
            TextSize = 12,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Consolas")
        };
        canvas.DrawText($"{Math.Round(_smoothedFrameTimeMs, 2)} ms", x + w - 50, y - 5, textPaint);
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

        // Use 15 rows for much better vertical coverage to hit small UI or centered floating text
        int[] rows = { h / 16, h / 8, 3 * h / 16, h / 4, 5 * h / 16, 3 * h / 8, 7 * h / 16, h / 2, 9 * h / 16, 5 * h / 8, 11 * h / 16, 3 * h / 4, 13 * h / 16, 7 * h / 8, 15 * h / 16 };
        int maxScan = w / 4;
        const int contentThreshold = 1; // Extremely sensitive to remove almost-black edges

        // Verify there is SOME content in sampled middle before trusting a crop (prevents cropping purely black screens)
        bool hasContent = false;
        int[] centerSampleX = { w / 2, w / 3, 2 * w / 3, w / 4, 3 * w / 4 };
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

        bool hasShader = !string.IsNullOrEmpty(_retroarchShaderFile);

        // Initialize or Update Pipeline if shader is present
        if (hasShader && _shaderPipeline == null)
        {
            try
            {
                _shaderPipeline = new SlangShaderPipeline(_gl);
                _shaderPipeline.LoadShaderPreset(_retroarchShaderFile);
            }
            catch { _shaderPipeline = null; }
        }

        // Ensure GL resources for the current frame size
        if (_captureTextureId == 0 || _texWidth != w || _texHeight != h)
        {
            InitializeGlResources(w, h);
        }

        // 1. Upload WGC frame to GPU
        _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _captureTextureId);
        var texSub = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, uint, int, IntPtr, void>)_gl.GetProcAddress("glTexSubImage2D");
        if (texSub != null)
            texSub(GlConsts.GL_TEXTURE_2D, 0, 0, 0, w, h, 0x80E1u, GlConsts.GL_UNSIGNED_BYTE, ptr); // 0x80E1 = GL_BGRA

        int finalTextureId = _captureTextureId;

        // 2. Apply Shader if active
        if (hasShader && _shaderPipeline != null && _shaderPipeline.HasActiveShader)
        {
            _shaderPipeline.Brightness = _brightness;
            _shaderPipeline.Saturation = _saturation;
            _shaderPipeline.ColorTint = new[] { _tint.R / 255f, _tint.G / 255f, _tint.B / 255f, _tint.A / 255f };
            _shaderPipeline.Process(_captureTextureId, w, h, _intermediateFbo, 0, 0, w, h);
            finalTextureId = _intermediateTextureId;
            grContext.ResetContext();
        }

        // 3. Draw directly from GPU texture
        var glInfo = new GRGlTextureInfo
        {
            Id = (uint)finalTextureId,
            Target = (uint)GlConsts.GL_TEXTURE_2D,
            Format = 0x8058 // GL_RGBA8
        };

        using var backendTexture = new GRBackendTexture(w, h, false, glInfo);
        using var skImage = SKImage.FromAdoptedTexture(grContext, backendTexture, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);

        if (skImage != null)
        {
            if (_settingsDirty && !hasShader) { UpdatePaint(); _settingsDirty = false; }
            SKRect srcRect = new SKRect(_cropLeft, 0, w - _cropRight, h);
            canvas.DrawImage(skImage, srcRect, _cachedDestRect, hasShader ? null : _paint);
        }
    }

    private void RenderSimpleFallback(SKCanvas canvas, IntPtr ptr, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var img = SKImage.FromPixels(info, ptr, w * 4);
        if (img != null)
        {
            if (_settingsDirty) { UpdatePaint(); _settingsDirty = false; }
            SKRect srcRect = new SKRect(_cropLeft, 0, w - _cropRight, h);
            canvas.DrawImage(img, srcRect, _cachedDestRect, _paint);
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
            (rAlpha + _saturation) * _brightness * (_tint.R / 255f), gAlpha * _brightness, bAlpha * _brightness, 0, 0,
            rAlpha * _brightness, (gAlpha + _saturation) * _brightness * (_tint.G / 255f), bAlpha * _brightness, 0, 0,
            rAlpha * _brightness, gAlpha * _brightness, (bAlpha + _saturation) * _brightness * (_tint.B / 255f), 0, 0,
            0, 0, 0, _tint.A / 255f, 0
        };

        _paint.ColorFilter = SKColorFilter.CreateColorMatrix(matrix);
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