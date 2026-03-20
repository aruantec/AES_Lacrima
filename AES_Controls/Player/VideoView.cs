using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using LibMPVSharp;
using System.Diagnostics;
using Avalonia.Threading;
using System.Globalization;

namespace AES_Controls.Player;

public enum VideoFlip
{
    None,
    Horizontal,
    Vertical,
    Both
}

public class VideoView : OpenGlControlBase
{
    private bool _initialized;
    private bool _hasRenderedOnceSincePause;
    private GlInterface? _glInterface;
    private DispatcherTimer? _uiHeartbeat;

    public static readonly StyledProperty<MPVMediaPlayer?> PlayerProperty =
        AvaloniaProperty.Register<VideoView, MPVMediaPlayer?>(nameof(Player));

    public MPVMediaPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public static readonly StyledProperty<bool> IsRenderingPausedProperty =
        AvaloniaProperty.Register<VideoView, bool>(nameof(IsRenderingPaused));

    public bool IsRenderingPaused
    {
        get => GetValue(IsRenderingPausedProperty);
        set => SetValue(IsRenderingPausedProperty, value);
    }

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoView, Stretch>(nameof(Stretch), Stretch.Uniform);

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public static readonly StyledProperty<int> RotationProperty =
        AvaloniaProperty.Register<VideoView, int>(nameof(Rotation));

    public int Rotation
    {
        get => GetValue(RotationProperty);
        set => SetValue(RotationProperty, value);
    }

    public static readonly StyledProperty<VideoFlip> FlipProperty =
        AvaloniaProperty.Register<VideoView, VideoFlip>(nameof(Flip));

    public VideoFlip Flip
    {
        get => GetValue(FlipProperty);
        set => SetValue(FlipProperty, value);
    }

    public static readonly StyledProperty<double> HeartbeatFpsProperty =
        AvaloniaProperty.Register<VideoView, double>(nameof(HeartbeatFps), 60.0);

    public double HeartbeatFps
    {
        get => GetValue(HeartbeatFpsProperty);
        set => SetValue(HeartbeatFpsProperty, value);
    }

    public static readonly StyledProperty<bool> UseCustomHeartbeatProperty =
        AvaloniaProperty.Register<VideoView, bool>(nameof(UseCustomHeartbeat));

    public bool UseCustomHeartbeat
    {
        get => GetValue(UseCustomHeartbeatProperty);
        set => SetValue(UseCustomHeartbeatProperty, value);
    }

    public static readonly StyledProperty<double> AudioSyncOffsetProperty =
        AvaloniaProperty.Register<VideoView, double>(nameof(AudioSyncOffset));

    public double AudioSyncOffset
    {
        get => GetValue(AudioSyncOffsetProperty);
        set => SetValue(AudioSyncOffsetProperty, value);
    }

    public VideoView()
    {
        _uiHeartbeat = new DispatcherTimer(
            CalculateInterval(HeartbeatFps),
            DispatcherPriority.Render,
            (_, _) => { if (!IsRenderingPaused) RequestNextFrameRendering(); });
    }

    private TimeSpan CalculateInterval(double fps)
    {
        if (fps <= 0) fps = 60.0;
        return TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond / fps));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hasRenderedOnceSincePause = false;
        if (UseCustomHeartbeat && !IsRenderingPaused) _uiHeartbeat?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _uiHeartbeat?.Stop();
    }

    private IntPtr GetProcAddressInternal(IntPtr ctx, string name)
        => _glInterface?.GetProcAddress(name) ?? IntPtr.Zero;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _glInterface = gl;
        _initialized = false;
        _hasRenderedOnceSincePause = false;
    }

    private void InitializeMpvInternal()
    {
        if (Player == null || _glInterface == null) return;

        try
        {
            Player.Options.GetProcAddress = GetProcAddressInternal;

            Player.SetProperty("video-sync", UseCustomHeartbeat ? "display-resample" : "audio");
            Player.SetProperty("audio-pitch-correction", "yes");
            Player.SetProperty("hwdec", "auto-safe");
            Player.SetProperty("opengl-waitvsync", "no");

            if (UseCustomHeartbeat)
            {
                Player.SetProperty("override-display-fps", HeartbeatFps.ToString(CultureInfo.InvariantCulture));
                Player.SetProperty("interpolation", "yes");
                Player.SetProperty("tscale", "oversample");
                if (!IsRenderingPaused) _uiHeartbeat?.Start();
            }
            else
            {
                Player.SetProperty("interpolation", "no");
                Player.SetProperty("override-display-fps", "0");
                _uiHeartbeat?.Stop();
            }

            Player.EnsureRenderContextCreated();

            ApplyStretch(Stretch);
            ApplyRotation(Rotation);
            ApplyFlip(Flip);
            ApplyAudioOffset(AudioSyncOffset);

            _initialized = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VideoView Init Error: {ex.Message}");
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
            Player.OpenGLRender(width, height, fb, flipY: 1);

            if (IsRenderingPaused) _hasRenderedOnceSincePause = true;
        }

        if (!UseCustomHeartbeat && !IsRenderingPaused)
        {
            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Input);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsRenderingPausedProperty)
        {
            bool paused = change.GetNewValue<bool>();
            if (paused)
            {
                _uiHeartbeat?.Stop();
            }
            else
            {
                _hasRenderedOnceSincePause = false;
                if (UseCustomHeartbeat) _uiHeartbeat?.Start();
                RequestNextFrameRendering();
            }
        }
        else if (change.Property == StretchProperty)
            ApplyStretch(change.GetNewValue<Stretch>());
        else if (change.Property == RotationProperty)
            ApplyRotation(change.GetNewValue<int>());
        else if (change.Property == FlipProperty)
            ApplyFlip(change.GetNewValue<VideoFlip>());
        else if (change.Property == AudioSyncOffsetProperty)
            ApplyAudioOffset(change.GetNewValue<double>());
        else if (change.Property == UseCustomHeartbeatProperty || change.Property == PlayerProperty)
        {
            _initialized = false;
            _hasRenderedOnceSincePause = false;
            if (!IsRenderingPaused) RequestNextFrameRendering();
        }
        else if (change.Property == HeartbeatFpsProperty)
        {
            var val = change.GetNewValue<double>();
            if (_uiHeartbeat != null)
                _uiHeartbeat.Interval = CalculateInterval(val);

            if (UseCustomHeartbeat)
                Player?.SetProperty("override-display-fps", val.ToString(CultureInfo.InvariantCulture));
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _initialized = false;
        _hasRenderedOnceSincePause = false;
        base.OnOpenGlDeinit(gl);
    }
}