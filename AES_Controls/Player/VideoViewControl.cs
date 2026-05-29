using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using AES_Mpv.Player;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AES_Controls.Player;

public enum VideoFlip
{
    None,
    Horizontal,
    Vertical,
    Both
}

public class VideoViewControl : OpenGlControlBase
{
    private const double DefaultHeartbeatFps = 60.0;
    private static readonly TimeSpan ViewportExpansionDelay = TimeSpan.FromSeconds(2);

    private const int FixedRenderWidth = 1920;
    private const int FixedRenderHeight = 1080;

    private const int GlReadFramebuffer = 0x8CA8;
    private const int GlDrawFramebuffer = 0x8CA9;

    private bool _initialized;
    private bool _hasRenderedOnceSincePause;
    private GlInterface? _glInterface;
    private double _videoAspectRatio;
    private double _expandedViewportWidth;
    private long _viewportExpansionHoldUntilTicks;

    private int _offscreenFboId = -1;
    private int _offscreenTextureId = -1;
    private GlBlitFramebufferDelegate? _glBlitFramebuffer;

    public static readonly StyledProperty<AesMpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<VideoViewControl, AesMpvPlayer?>(nameof(Player));

    public AesMpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public static readonly StyledProperty<bool> IsRenderingPausedProperty =
        AvaloniaProperty.Register<VideoViewControl, bool>(nameof(IsRenderingPaused));

    public bool IsRenderingPaused
    {
        get => GetValue(IsRenderingPausedProperty);
        set => SetValue(IsRenderingPausedProperty, value);
    }

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoViewControl, Stretch>(nameof(Stretch), Stretch.Uniform);

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public static readonly StyledProperty<int> RotationProperty =
        AvaloniaProperty.Register<VideoViewControl, int>(nameof(Rotation));

    public int Rotation
    {
        get => GetValue(RotationProperty);
        set => SetValue(RotationProperty, value);
    }

    public static readonly StyledProperty<VideoFlip> FlipProperty =
        AvaloniaProperty.Register<VideoViewControl, VideoFlip>(nameof(Flip));

    public VideoFlip Flip
    {
        get => GetValue(FlipProperty);
        set => SetValue(FlipProperty, value);
    }

    public static readonly StyledProperty<double> HeartbeatFpsProperty =
        AvaloniaProperty.Register<VideoViewControl, double>(nameof(HeartbeatFps), DefaultHeartbeatFps);

    public double HeartbeatFps
    {
        get => GetValue(HeartbeatFpsProperty);
        set => SetValue(HeartbeatFpsProperty, value);
    }

    public static readonly StyledProperty<bool> UseCustomHeartbeatProperty =
        AvaloniaProperty.Register<VideoViewControl, bool>(nameof(UseCustomHeartbeat), false);

    public bool UseCustomHeartbeat
    {
        get => GetValue(UseCustomHeartbeatProperty);
        set => SetValue(UseCustomHeartbeatProperty, value);
    }

    public static readonly StyledProperty<double> AudioSyncOffsetProperty =
        AvaloniaProperty.Register<VideoViewControl, double>(nameof(AudioSyncOffset));

    public double AudioSyncOffset
    {
        get => GetValue(AudioSyncOffsetProperty);
        set => SetValue(AudioSyncOffsetProperty, value);
    }

    public static readonly DirectProperty<VideoViewControl, double> ExpandedViewportWidthProperty =
        AvaloniaProperty.RegisterDirect<VideoViewControl, double>(
            nameof(ExpandedViewportWidth),
            o => o.ExpandedViewportWidth);

    public static readonly StyledProperty<double> ReferenceViewportWidthProperty =
        AvaloniaProperty.Register<VideoViewControl, double>(nameof(ReferenceViewportWidth));

    public double ReferenceViewportWidth
    {
        get => GetValue(ReferenceViewportWidthProperty);
        set => SetValue(ReferenceViewportWidthProperty, value);
    }

    public static readonly StyledProperty<double> ReferenceViewportHeightProperty =
        AvaloniaProperty.Register<VideoViewControl, double>(nameof(ReferenceViewportHeight));

    public double ReferenceViewportHeight
    {
        get => GetValue(ReferenceViewportHeightProperty);
        set => SetValue(ReferenceViewportHeightProperty, value);
    }

    public double ExpandedViewportWidth
    {
        get => _expandedViewportWidth;
        private set => SetAndRaise(ExpandedViewportWidthProperty, ref _expandedViewportWidth, value);
    }

    public VideoViewControl()
    {
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GlBlitFramebufferDelegate(
        int srcX0, int srcY0, int srcX1, int srcY1,
        int dstX0, int dstY0, int dstX1, int dstY1,
        int mask, int filter);

    private static double GetEffectiveHeartbeatFps(double heartbeatFps)
        => heartbeatFps > 0 ? heartbeatFps : DefaultHeartbeatFps;

    private void ResetViewportSizing()
    {
        _videoAspectRatio = 0;
        _viewportExpansionHoldUntilTicks = 0;
        UpdateExpandedViewportWidth();
    }

    private void HoldViewportExpansion()
    {
        _viewportExpansionHoldUntilTicks = Stopwatch.GetTimestamp() +
            (long)(ViewportExpansionDelay.TotalSeconds * Stopwatch.Frequency);
        UpdateExpandedViewportWidth();
    }

    private void UpdateExpandedViewportWidth()
    {
        double baseWidth = ReferenceViewportWidth > 0
            ? ReferenceViewportWidth
            : Bounds.Width;
        double baseHeight = ReferenceViewportHeight > 0
            ? ReferenceViewportHeight
            : Bounds.Height;

        double targetWidth = baseWidth > 0
            ? Math.Round(baseWidth, 2)
            : (Bounds.Width > 0 ? Math.Round(Bounds.Width, 2) : 0);

        if (Stopwatch.GetTimestamp() >= _viewportExpansionHoldUntilTicks &&
            _videoAspectRatio > 0 &&
            baseHeight > 0)
        {
            double videoWidth = Math.Round(baseHeight * _videoAspectRatio, 2);
            targetWidth = Math.Max(targetWidth, videoWidth);
        }

        ExpandedViewportWidth = targetWidth;
    }

    private void RefreshVideoAspectRatio()
    {
        if (Player == null) return;

        double aspect = 0;
        try { aspect = Player.GetDoubleProperty("video-out-params/aspect"); }
        catch
        {
            try { aspect = Player.GetDoubleProperty("video-params/aspect"); }
            catch { return; }
        }

        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect)) return;
        if (Math.Abs(aspect - _videoAspectRatio) < 0.01) return;

        _videoAspectRatio = aspect;
        UpdateExpandedViewportWidth();
    }

    private void CreateOffscreenResources()
    {
        if (_glInterface == null) return;
        var gl = _glInterface;

        CleanupOffscreenResources();

        _offscreenTextureId = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _offscreenTextureId);
        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA,
            FixedRenderWidth, FixedRenderHeight, 0,
            GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        _offscreenFboId = gl.GenFramebuffer();
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, _offscreenFboId);
        gl.FramebufferTexture2D(GlConsts.GL_FRAMEBUFFER, GlConsts.GL_COLOR_ATTACHMENT0,
            GlConsts.GL_TEXTURE_2D, _offscreenTextureId, 0);
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, 0);
    }

    private void CleanupOffscreenResources()
    {
        if (_glInterface == null) return;
        var gl = _glInterface;
        if (_offscreenFboId >= 0)
        {
            gl.DeleteFramebuffer(_offscreenFboId);
            _offscreenFboId = -1;
        }
        if (_offscreenTextureId >= 0)
        {
            gl.DeleteTexture(_offscreenTextureId);
            _offscreenTextureId = -1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hasRenderedOnceSincePause = false;
        ResetViewportSizing();
        HoldViewportExpansion();
        if (!IsRenderingPaused)
            RequestNextFrameRendering();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
    }

    private IntPtr GetProcAddressInternal(IntPtr ctx, string name)
        => _glInterface?.GetProcAddress(name) ?? IntPtr.Zero;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _glInterface = gl;
        _initialized = false;
        _hasRenderedOnceSincePause = false;
        ResetViewportSizing();
        HoldViewportExpansion();

        var blitPtr = gl.GetProcAddress("glBlitFramebuffer");
        if (blitPtr != IntPtr.Zero)
            _glBlitFramebuffer = Marshal.GetDelegateForFunctionPointer<GlBlitFramebufferDelegate>(blitPtr);

        CreateOffscreenResources();
    }

    private void InitializeMpvInternal()
    {
        if (Player == null || _glInterface == null) return;

        try
        {
            Player.Options.ResolveOpenGlAddress = GetProcAddressInternal;
            double heartbeatFps = GetEffectiveHeartbeatFps(HeartbeatFps);

            Player.SetProperty("video-sync", UseCustomHeartbeat ? "display-resample" : "audio");
            Player.SetProperty("audio-pitch-correction", "yes");
            Player.SetProperty("hwdec", "auto-safe");
            Player.SetProperty("opengl-waitvsync", "no");
            Player.SetProperty("override-display-fps", "0");

            if (UseCustomHeartbeat)
            {
                Player.SetProperty("override-display-fps", heartbeatFps.ToString(CultureInfo.InvariantCulture));
                Player.SetProperty("interpolation", "yes");
                Player.SetProperty("tscale", "oversample");
            }
            else
            {
                Player.SetProperty("interpolation", "no");
                Player.SetProperty("override-display-fps", "0");
            }

            Player.EnsureRenderContext();

            // Always render Uniform into the fixed-size off-screen FBO
            Player.SetProperty("video-unscaled", "no");
            Player.SetProperty("keepaspect", "yes");
            Player.SetProperty("panscan", "0");

            ApplyRotation(Rotation);
            ApplyFlip(Flip);
            ApplyAudioOffset(AudioSyncOffset);

            _initialized = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VideoViewControl Init Error: {ex.Message}");
        }
    }

    private void ApplyRotation(int degrees)
        => Player?.SetProperty("video-rotate", degrees.ToString(CultureInfo.InvariantCulture));

    private void ApplyFlip(VideoFlip flip)
    {
        if (Player == null) return;
        string filter = flip switch
        {
            VideoFlip.Horizontal => "hflip",
            VideoFlip.Vertical => "vflip",
            VideoFlip.Both => "hflip,vflip",
            _ => ""
        };
        Player.SetProperty("vf", filter);
    }

    private void ApplyAudioOffset(double ms)
    {
        double seconds = ms / 1000.0;
        Player?.SetProperty("audio-delay", seconds.ToString(CultureInfo.InvariantCulture));
    }

    private static void GetBlitDestRect(int fbWidth, int fbHeight, Stretch stretch,
        out int dstX, out int dstY, out int dstW, out int dstH)
    {
        const float srcAspect = (float)FixedRenderWidth / FixedRenderHeight;
        float dstAspect = (float)fbWidth / fbHeight;

        switch (stretch)
        {
            case Stretch.Fill:
                dstX = 0; dstY = 0;
                dstW = fbWidth; dstH = fbHeight;
                break;

            case Stretch.UniformToFill:
                if (dstAspect > srcAspect)
                {
                    dstW = fbWidth;
                    dstH = (int)(fbWidth / srcAspect);
                    dstX = 0;
                    dstY = (fbHeight - dstH) / 2;
                }
                else
                {
                    dstH = fbHeight;
                    dstW = (int)(fbHeight * srcAspect);
                    dstX = (fbWidth - dstW) / 2;
                    dstY = 0;
                }
                break;

            default:
                if (dstAspect > srcAspect)
                {
                    dstH = fbHeight;
                    dstW = (int)(fbHeight * srcAspect);
                    dstX = (fbWidth - dstW) / 2;
                    dstY = 0;
                }
                else
                {
                    dstW = fbWidth;
                    dstH = (int)(fbWidth / srcAspect);
                    dstX = 0;
                    dstY = (fbHeight - dstH) / 2;
                }
                break;
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (IsRenderingPaused && _hasRenderedOnceSincePause) return;

        if (!_initialized) InitializeMpvInternal();
        if (Player == null || !_initialized) return;

        if (_offscreenFboId < 0 || _offscreenTextureId < 0)
            CreateOffscreenResources();

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int fbWidth = Math.Max(1, (int)(Bounds.Width * scale));
        int fbHeight = Math.Max(1, (int)(Bounds.Height * scale));

        // Step 1: Render mpv to off-screen FBO at fixed resolution
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, _offscreenFboId);
        gl.Viewport(0, 0, FixedRenderWidth, FixedRenderHeight);
        Player.RenderToOpenGl(FixedRenderWidth, FixedRenderHeight, _offscreenFboId, flipY: 1);

        // Step 2: Blit off-screen FBO to main framebuffer with scaling
        if (_glBlitFramebuffer != null && fbWidth > 0 && fbHeight > 0)
        {
            GetBlitDestRect(fbWidth, fbHeight, Stretch, out int dstX, out int dstY, out int dstW, out int dstH);

            gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
            gl.Viewport(0, 0, fbWidth, fbHeight);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

            gl.BindFramebuffer(GlReadFramebuffer, _offscreenFboId);
            gl.BindFramebuffer(GlDrawFramebuffer, fb);
            _glBlitFramebuffer(0, 0, FixedRenderWidth, FixedRenderHeight,
                dstX, dstY, dstX + dstW, dstY + dstH,
                GlConsts.GL_COLOR_BUFFER_BIT, GlConsts.GL_LINEAR);

            gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        }
        else
        {
            gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
            gl.Viewport(0, 0, fbWidth, fbHeight);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
            Player.RenderToOpenGl(fbWidth, fbHeight, fb, flipY: 1);
        }

        RefreshVideoAspectRatio();
        UpdateExpandedViewportWidth();

        if (IsRenderingPaused) _hasRenderedOnceSincePause = true;

        if (!IsRenderingPaused && IsVisible)
            RequestNextFrameRendering();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            if (change.GetNewValue<bool>())
            {
                _hasRenderedOnceSincePause = false;
                ResetViewportSizing();
                HoldViewportExpansion();
                if (!IsRenderingPaused)
                    RequestNextFrameRendering();
            }
            else
            {
                ResetViewportSizing();
            }
        }
        else if (change.Property == IsRenderingPausedProperty)
        {
            bool paused = change.GetNewValue<bool>();
            if (paused)
            {
                _hasRenderedOnceSincePause = false;
                ResetViewportSizing();
                RequestNextFrameRendering();
            }
            else
            {
                _hasRenderedOnceSincePause = false;
                ResetViewportSizing();
                HoldViewportExpansion();
                RequestNextFrameRendering();
            }
        }
        else if (change.Property == StretchProperty)
        {
            RequestNextFrameRendering();
        }
        else if (change.Property == RotationProperty)
        {
            ApplyRotation(change.GetNewValue<int>());
            RequestNextFrameRendering();
        }
        else if (change.Property == FlipProperty)
        {
            ApplyFlip(change.GetNewValue<VideoFlip>());
            RequestNextFrameRendering();
        }
        else if (change.Property == AudioSyncOffsetProperty)
            ApplyAudioOffset(change.GetNewValue<double>());
        else if (change.Property == BoundsProperty)
        {
            UpdateExpandedViewportWidth();
            RequestNextFrameRendering();
        }
        else if (change.Property == ReferenceViewportWidthProperty || change.Property == ReferenceViewportHeightProperty)
        {
            ResetViewportSizing();
            HoldViewportExpansion();
            RequestNextFrameRendering();
        }
        else if (change.Property == UseCustomHeartbeatProperty || change.Property == PlayerProperty)
        {
            _initialized = false;
            _hasRenderedOnceSincePause = false;
            ResetViewportSizing();
            HoldViewportExpansion();
            RequestNextFrameRendering();
        }
        else if (change.Property == HeartbeatFpsProperty)
        {
            double heartbeatFps = GetEffectiveHeartbeatFps(change.GetNewValue<double>());
            if (UseCustomHeartbeat)
                Player?.SetProperty("override-display-fps", heartbeatFps.ToString(CultureInfo.InvariantCulture));
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _initialized = false;
        _hasRenderedOnceSincePause = false;
        CleanupOffscreenResources();
        _glInterface = null;
        _glBlitFramebuffer = null;
        ResetViewportSizing();
        base.OnOpenGlDeinit(gl);
    }
}
