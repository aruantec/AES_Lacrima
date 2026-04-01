using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using AES_Mpv.Player;
using System.Diagnostics;
using System.Globalization;

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
    private const int DisplayFpsUpdateInterval = 12;
    private const double MinMeasuredDisplayFps = 24.0;
    private const double MaxMeasuredDisplayFps = 240.0;

    private bool _initialized;
    private bool _hasRenderedOnceSincePause;
    private GlInterface? _glInterface;
    private long _lastRenderTimestamp;
    private double _smoothedDisplayFps;
    private int _framesSinceDisplayFpsUpdate;

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
        AvaloniaProperty.Register<VideoViewControl, double>(nameof(HeartbeatFps), 120.0);

    public double HeartbeatFps
    {
        get => GetValue(HeartbeatFpsProperty);
        set => SetValue(HeartbeatFpsProperty, value);
    }

    public static readonly StyledProperty<bool> UseCustomHeartbeatProperty =
        AvaloniaProperty.Register<VideoViewControl, bool>(nameof(UseCustomHeartbeat), true);

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

    public VideoViewControl()
    {
        _smoothedDisplayFps = HeartbeatFps;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hasRenderedOnceSincePause = false;
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
        _lastRenderTimestamp = 0;
        _smoothedDisplayFps = HeartbeatFps;
        _framesSinceDisplayFpsUpdate = 0;
    }

    private void InitializeMpvInternal()
    {
        if (Player == null || _glInterface == null) return;

        try
        {
            Player.Options.ResolveOpenGlAddress = GetProcAddressInternal;
            _smoothedDisplayFps = HeartbeatFps;

            Player.SetProperty("video-sync", UseCustomHeartbeat ? "display-resample" : "audio");
            Player.SetProperty("audio-pitch-correction", "yes");
            Player.SetProperty("hwdec", "auto-safe");
            Player.SetProperty("opengl-waitvsync", "no");
            Player.SetProperty("override-display-fps", "0");

            if (UseCustomHeartbeat)
            {
                Player.SetProperty("override-display-fps", HeartbeatFps.ToString(CultureInfo.InvariantCulture));
                Player.SetProperty("interpolation", "yes");
                Player.SetProperty("tscale", "oversample");
            }
            else
            {
                Player.SetProperty("interpolation", "no");
                Player.SetProperty("override-display-fps", "0");
            }

            Player.EnsureRenderContext();

            ApplyStretch(Stretch);
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

    private void ApplyStretch(Stretch stretch)
    {
        if (Player == null) return;
        switch (stretch)
        {
            case Stretch.None:
                Player.SetProperty("video-unscaled", "yes");
                Player.SetProperty("panscan", "0");
                break;
            case Stretch.Fill:
                Player.SetProperty("video-unscaled", "no");
                Player.SetProperty("keepaspect", "no");
                Player.SetProperty("panscan", "0");
                break;
            case Stretch.Uniform:
                Player.SetProperty("video-unscaled", "no");
                Player.SetProperty("keepaspect", "yes");
                Player.SetProperty("panscan", "0");
                break;
            case Stretch.UniformToFill:
                Player.SetProperty("video-unscaled", "no");
                Player.SetProperty("keepaspect", "yes");
                Player.SetProperty("panscan", "1.0");
                break;
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

    private void UpdateObservedDisplayFps()
    {
        if (!UseCustomHeartbeat || Player == null)
            return;

        long now = Stopwatch.GetTimestamp();
        long previous = _lastRenderTimestamp;
        _lastRenderTimestamp = now;

        if (previous == 0)
            return;

        double elapsedSeconds = (now - previous) / (double)Stopwatch.Frequency;
        if (elapsedSeconds <= 0)
            return;

        double observedFps = Math.Clamp(1.0 / elapsedSeconds, MinMeasuredDisplayFps, MaxMeasuredDisplayFps);
        _smoothedDisplayFps = _smoothedDisplayFps <= 0
            ? observedFps
            : (_smoothedDisplayFps * 0.85) + (observedFps * 0.15);

        _framesSinceDisplayFpsUpdate++;
        if (_framesSinceDisplayFpsUpdate < DisplayFpsUpdateInterval)
            return;

        _framesSinceDisplayFpsUpdate = 0;
        Player.SetProperty("override-display-fps", _smoothedDisplayFps.ToString("0.###", CultureInfo.InvariantCulture));
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (IsRenderingPaused && _hasRenderedOnceSincePause) return;

        if (!_initialized) InitializeMpvInternal();
        if (Player == null || !_initialized) return;

        var scale = VisualRoot?.RenderScaling ?? 1.0;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);

        if (width > 0 && height > 0)
        {
            gl.BindFramebuffer(0x8D40, fb);
            gl.Viewport(0, 0, width, height);
            Player.RenderToOpenGl(width, height, fb, flipY: 1);
            UpdateObservedDisplayFps();

            if (IsRenderingPaused) _hasRenderedOnceSincePause = true;
        }

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
                if (!IsRenderingPaused)
                    RequestNextFrameRendering();
            }
        }
        else if (change.Property == IsRenderingPausedProperty)
        {
            bool paused = change.GetNewValue<bool>();
            if (paused)
            {
                _hasRenderedOnceSincePause = false;
                RequestNextFrameRendering();
            }
            else
            {
                _hasRenderedOnceSincePause = false;
                RequestNextFrameRendering();
            }
        }
        else if (change.Property == StretchProperty)
        {
            ApplyStretch(change.GetNewValue<Stretch>());
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
            RequestNextFrameRendering();
        else if (change.Property == UseCustomHeartbeatProperty || change.Property == PlayerProperty)
        {
            _initialized = false;
            _hasRenderedOnceSincePause = false;
            _lastRenderTimestamp = 0;
            _smoothedDisplayFps = HeartbeatFps;
            _framesSinceDisplayFpsUpdate = 0;
            RequestNextFrameRendering();
        }
        else if (change.Property == HeartbeatFpsProperty)
        {
            _smoothedDisplayFps = change.GetNewValue<double>();
            if (UseCustomHeartbeat)
                Player?.SetProperty("override-display-fps", change.GetNewValue<double>().ToString(CultureInfo.InvariantCulture));
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _initialized = false;
        _hasRenderedOnceSincePause = false;
        _glInterface = null;
        _lastRenderTimestamp = 0;
        _framesSinceDisplayFpsUpdate = 0;
        base.OnOpenGlDeinit(gl);
    }
}
