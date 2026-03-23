using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using System.Buffers;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using log4net;

namespace AES_Controls.GL;

public unsafe class GlRadialSpectrumControl : OpenGlControlBase, IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(GlRadialSpectrumControl));
    private const int GL_DYNAMIC_DRAW = 0x88E8;
    private const int GL_SRC_ALPHA = 0x0302;
    private const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const int GL_BLEND = 0x0BE2;
    private const double MinAdaptiveFrameIntervalMs = 1000.0 / 120.0;
    private const double MaxAdaptiveFrameIntervalMs = 1000.0 / 30.0;
    private const double AdaptiveIntervalToleranceMs = 0.25;

    #region Styled Properties
    public static readonly StyledProperty<AvaloniaList<double>> SpectrumProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, AvaloniaList<double>>(nameof(Spectrum));
    public static readonly StyledProperty<Bitmap?> CoverProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, Bitmap?>(nameof(Cover));
    public static readonly StyledProperty<Stretch> CoverStretchProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, Stretch>(nameof(CoverStretch), Stretch.UniformToFill);
    public static readonly StyledProperty<bool> RotateProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, bool>(nameof(Rotate));
    public static readonly StyledProperty<double> BarHeightPercentProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(BarHeightPercent), 125.0);
    public static readonly StyledProperty<double> BarOpacityProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(BarOpacity), 1.0);
    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(BarWidth), 0.025);
    public static readonly StyledProperty<double> BarBlurProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(BarBlur), 0.2);
    public static readonly StyledProperty<Color> RingColorProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, Color>(nameof(RingColor), Color.Parse("#F0F0FF"));
    public static readonly StyledProperty<Color> InnerCircleColorProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, Color>(nameof(InnerCircleColor), Colors.Transparent);
    public static readonly StyledProperty<double> GlowLineThicknessProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(GlowLineThickness), 3.0);
    public static readonly StyledProperty<double> GlowMarginProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(GlowMargin), 0.06);
    public static readonly StyledProperty<double> PushDistanceMultiplierProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(PushDistanceMultiplier), 8.0);
    public static readonly StyledProperty<double> CoverOpacityProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(CoverOpacity), 1.0);
    public static readonly StyledProperty<LinearGradientBrush?> BarGradientProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, LinearGradientBrush?>(nameof(BarGradient), new LinearGradientBrush { GradientStops = [new GradientStop(Color.Parse("#00CCFF"), 0.0), new GradientStop(Color.Parse("#3333FF"), 0.25), new GradientStop(Color.Parse("#CC00CC"), 0.5), new GradientStop(Color.Parse("#FF004D"), 0.75), new GradientStop(Color.Parse("#FFB300"), 1.0)] });
    public static readonly StyledProperty<int> SampleCountProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, int>(nameof(SampleCount), 128);
    public static readonly StyledProperty<double> AttackLerpProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(AttackLerp), 0.42);
    public static readonly StyledProperty<double> ReleaseLerpProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(ReleaseLerp), 0.38);
    public static readonly StyledProperty<double> PeakDecayProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(PeakDecay), 0.85);
    public static readonly StyledProperty<double> PrePowAttackAlphaProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(PrePowAttackAlpha), 0.90);
    public static readonly StyledProperty<double> MaxRiseFractionProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(MaxRiseFraction), 0.55);
    public static readonly StyledProperty<double> MaxRiseAbsoluteProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, double>(nameof(MaxRiseAbsolute), 0.05);
    public static readonly StyledProperty<bool> UseGlowLineProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, bool>(nameof(UseGlowLine), true);
    public static readonly StyledProperty<bool> UseVerticalBlendProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, bool>(nameof(UseVerticalBlend), true);
    public static readonly StyledProperty<bool> InvertBlendProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, bool>(nameof(InvertBlend));
    public static readonly StyledProperty<int> GlowModeProperty = AvaloniaProperty.Register<GlRadialSpectrumControl, int>(nameof(GlowMode), 2);
    
    // New property for optional delta-time
    public static readonly StyledProperty<bool> UseDeltaTimeProperty = 
        AvaloniaProperty.Register<GlRadialSpectrumControl, bool>(nameof(UseDeltaTime), true);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<GlRadialSpectrumControl, bool>(nameof(DisableVSync), true);

    public AvaloniaList<double> Spectrum { get => GetValue(SpectrumProperty); set => SetValue(SpectrumProperty, value); }
    public Bitmap? Cover { get => GetValue(CoverProperty); set => SetValue(CoverProperty, value); }
    public Stretch CoverStretch { get => GetValue(CoverStretchProperty); set => SetValue(CoverStretchProperty, value); }
    public bool Rotate { get => GetValue(RotateProperty); set => SetValue(RotateProperty, value); }
    public double BarHeightPercent { get => GetValue(BarHeightPercentProperty); set => SetValue(BarHeightPercentProperty, value); }
    public double BarOpacity { get => GetValue(BarOpacityProperty); set => SetValue(BarOpacityProperty, value); }
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public double BarBlur { get => GetValue(BarBlurProperty); set => SetValue(BarBlurProperty, value); }
    public Color RingColor { get => GetValue(RingColorProperty); set => SetValue(RingColorProperty, value); }
    public Color InnerCircleColor { get => GetValue(InnerCircleColorProperty); set => SetValue(InnerCircleColorProperty, value); }
    public double GlowLineThickness { get => GetValue(GlowLineThicknessProperty); set => SetValue(GlowLineThicknessProperty, value); }
    public double GlowMargin { get => GetValue(GlowMarginProperty); set => SetValue(GlowMarginProperty, value); }
    public double PushDistanceMultiplier { get => GetValue(PushDistanceMultiplierProperty); set => SetValue(PushDistanceMultiplierProperty, value); }
    public double CoverOpacity { get => GetValue(CoverOpacityProperty); set => SetValue(CoverOpacityProperty, value); }
    public LinearGradientBrush? BarGradient { get => GetValue(BarGradientProperty); set => SetValue(BarGradientProperty, value); }
    public int SampleCount { get => GetValue(SampleCountProperty); set => SetValue(SampleCountProperty, value); }
    public double AttackLerp { get => GetValue(AttackLerpProperty); set => SetValue(AttackLerpProperty, value); }
    public double ReleaseLerp { get => GetValue(ReleaseLerpProperty); set => SetValue(ReleaseLerpProperty, value); }
    public double PeakDecay { get => GetValue(PeakDecayProperty); set => SetValue(PeakDecayProperty, value); }
    public double PrePowAttackAlpha { get => GetValue(PrePowAttackAlphaProperty); set => SetValue(PrePowAttackAlphaProperty, value); }
    public double MaxRiseFraction { get => GetValue(MaxRiseFractionProperty); set => SetValue(MaxRiseFractionProperty, value); }
    public double MaxRiseAbsolute { get => GetValue(MaxRiseAbsoluteProperty); set => SetValue(MaxRiseAbsoluteProperty, value); }
    public bool UseGlowLine { get => GetValue(UseGlowLineProperty); set => SetValue(UseGlowLineProperty, value); }
    public bool UseVerticalBlend { get => GetValue(UseVerticalBlendProperty); set => SetValue(UseVerticalBlendProperty, value); }
    public bool InvertBlend { get => GetValue(InvertBlendProperty); set => SetValue(InvertBlendProperty, value); }
    public int GlowMode { get => GetValue(GlowModeProperty); set => SetValue(GlowModeProperty, value); }
    public bool UseDeltaTime { get => GetValue(UseDeltaTimeProperty); set => SetValue(UseDeltaTimeProperty, value); }
    public bool DisableVSync { get => GetValue(DisableVSyncProperty); set => SetValue(DisableVSyncProperty, value); }
    #endregion

    private int _barProgram, _coverProgram, _vbo, _vao, _texVbo, _texture;
    private bool _textureDirty;
    private float[] _glVertices = [];
    private float[] _glowLineVertices = [];
    private float[] _spectrumSnapshot = [];
    private int[] _sampleMap = [];
    private readonly float[] _gradientColors = new float[15];
    private double[] _displayedBarLevels = [];
    private double[] _glowLevels = [];
    private double[] _rawSmoothed = [];
    private double _globalMax = 1e-6;
    private float _rotationAngle;
    private bool _isEs;
    private bool _gradientDirty = true;
    private bool _isAnimating;
    private int _spectrumCount;
    private int _sampleMapTargetCount = -1;
    private int _sampleMapSourceCount = -1;
    private int _barVertexFloatCount;
    private int _glowVertexFloatCount;
    private int _vertexBufferCapacityBytes;
    private DispatcherTimer? _renderTimer;
    private double _targetFrameIntervalMs = MinAdaptiveFrameIntervalMs;
    private double _averageRenderDurationMs = MinAdaptiveFrameIntervalMs * 0.5;
    private double _averageFrameDurationMs = MinAdaptiveFrameIntervalMs;
    private int _overBudgetFrames;
    private int _underBudgetFrames;

    // Precomputed trigonometric values (Cos, Sin) per bar
    private float[] _precomputedCos = [];
    private float[] _precomputedSin = [];
    private int _lastSampleCount = -1;

    private readonly Stopwatch _st = Stopwatch.StartNew();
    private double _lastTicks;
    private bool _vsyncDisabled = false;
    private int _pendingRedraw;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int eglSwapIntervalDel(IntPtr dpy, int interval);
    private eglSwapIntervalDel? _eglSwapInterval;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglSwapIntervalEXTDel(int interval);
    private wglSwapIntervalEXTDel? _wglSwapIntervalEXT;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr eglGetCurrentDisplayDel();
    private eglGetCurrentDisplayDel? _eglGetCurrentDisplay;

    private readonly List<IDisposable> _propertySubscriptions = new();
    private INotifyCollectionChanged? _spectrumCollectionRef;
    private NotifyCollectionChangedEventHandler? _spectrumCollectionHandler;

    private int _barAspectLoc = -1;
    private int _barOpacityLoc = -1;
    private int _barBlurLoc = -1;
    private int _barUseVerticalBlendLoc = -1;
    private int _barInvertBlendLoc = -1;
    private int _barGlowModeLoc = -1;
    private readonly int[] _barColorLocs = new int[5];
    private int _coverAspectLoc = -1;
    private int _coverAngleLoc = -1;
    private int _coverUvScaleLoc = -1;
    private int _coverRingColorLoc = -1;
    private int _coverInnerColorLoc = -1;
    private int _coverOpacityLoc = -1;
    private int _coverHasTextureLoc = -1;

    private delegate* unmanaged[Stdcall]<int, int, void> _glBlendFunc;
    private delegate* unmanaged[Stdcall]<int, float, void> _glUniform1F;
    private delegate* unmanaged[Stdcall]<int, int, void> _glUniform1I;
    private delegate* unmanaged[Stdcall]<int, float, float, void> _glUniform2F;
    private delegate* unmanaged[Stdcall]<int, float, float, float, void> _glUniform3F;
    private delegate* unmanaged[Stdcall]<int, float, float, float, float, void> _glUniform4F;
    private delegate* unmanaged[Stdcall]<int, nint, nint, nint, void> _glBufferSubData;

    public GlRadialSpectrumControl()
    {
        _propertySubscriptions.Add(this.GetObservable(SpectrumProperty).Subscribe(new SimpleObserver<AvaloniaList<double>>(OnSpectrumChanged)));
        _propertySubscriptions.Add(this.GetObservable(CoverProperty).Subscribe(new SimpleObserver<Bitmap?>(_ =>
        {
            _textureDirty = true;
            RequestRedraw();
        })));

        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(MinAdaptiveFrameIntervalMs), DispatcherPriority.Render, (_, _) =>
        {
            if (!IsVisible || (Volatile.Read(ref _pendingRedraw) == 0 && !_isAnimating))
                return;

            Interlocked.Exchange(ref _pendingRedraw, 0);
            RequestNextFrameRendering();
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BarGradientProperty)
        {
            _gradientDirty = true;
            RequestRedraw();
        }
        else if (change.Property != IsVisibleProperty && change.Property != SpectrumProperty && change.Property != CoverProperty)
        {
            RequestRedraw();
        }

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            if (_isAnimating || Volatile.Read(ref _pendingRedraw) != 0)
                _renderTimer?.Start();
            RequestNextFrameRendering();
        }
        else if (change.Property == IsVisibleProperty)
        {
            _renderTimer?.Stop();
        }
    }

    private void OnSpectrumChanged(AvaloniaList<double> col)
    {
        // Unsubscribe previous collection handler
        try
        {
            if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null)
                _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler;
        }
        catch (Exception ex) { Log.Warn("Error unsubscribing from spectrum collection", ex); }
        _spectrumCollectionRef = null;
        _spectrumCollectionHandler = null;

        if (col is INotifyCollectionChanged notify)
        {
            _spectrumCollectionRef = notify;
            // reuse the same handler instance to avoid per-subscription allocations
            _spectrumCollectionHandler = OnSpectrumCollectionChanged;
            notify.CollectionChanged += _spectrumCollectionHandler;
        }
        RequestRedraw();
    }

    private void OnSpectrumCollectionChanged(object? s, NotifyCollectionChangedEventArgs e) => RequestRedraw();

    private void RequestRedraw()
    {
        _isAnimating = true;
        Interlocked.Exchange(ref _pendingRedraw, 1);
        if (IsVisible)
            _renderTimer?.Start();
    }

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        var shaderInfo = GlHelper.GetShaderVersion(gl);
        var shaderVersion = shaderInfo.Item1;
        _isEs = shaderInfo.Item2;

        try
        {
            IntPtr pWglSwap = gl.GetProcAddress("wglSwapIntervalEXT");
            if (pWglSwap != IntPtr.Zero) _wglSwapIntervalEXT = Marshal.GetDelegateForFunctionPointer<wglSwapIntervalEXTDel>(pWglSwap);
            IntPtr pEglSwap = gl.GetProcAddress("eglSwapInterval");
            if (pEglSwap != IntPtr.Zero) _eglSwapInterval = Marshal.GetDelegateForFunctionPointer<eglSwapIntervalDel>(pEglSwap);
            IntPtr pGetDisplay = gl.GetProcAddress("eglGetCurrentDisplay");
            if (pGetDisplay != IntPtr.Zero) _eglGetCurrentDisplay = Marshal.GetDelegateForFunctionPointer<eglGetCurrentDisplayDel>(pGetDisplay);
        }
        catch (Exception ex) { Log.Warn("Error getting OpenGL swap interval functions", ex); }

        string barVertexShader = $@"{shaderVersion}
            layout(location = 0) in vec2 a_pos; 
            layout(location = 1) in vec2 a_uv; 
            layout(location = 2) in vec2 a_quadUv;
            uniform float u_aspect; 
            out float v_u; 
            out float v_v; 
            out vec2 v_quadUv;
            void main() {{ 
                v_u = a_uv.x; 
                v_v = a_uv.y; 
                v_quadUv = a_quadUv; 
                gl_Position = vec4(a_pos.x / u_aspect, a_pos.y, 0.0, 1.0); 
            }}";

        string barFragmentShader = $@"{shaderVersion}
            {(_isEs ? "precision mediump float;" : "")}
            in float v_u; 
            in float v_v; 
            in vec2 v_quadUv;
            uniform vec3 u_col0, u_col1, u_col2, u_col3, u_col4;
            uniform float u_opacity, u_blur;
            uniform int u_useVerticalBlend, u_invertBlend, u_glowMode;
            out vec4 fragColor;
            void main() {{
                vec3 color;
                if (v_u < 0.25) color = mix(u_col0, u_col1, v_u/0.25);
                else if (v_u < 0.5) color = mix(u_col1, u_col2, (v_u-0.25)/0.25);
                else if (v_u < 0.75) color = mix(u_col2, u_col3, (v_u-0.5)/0.25);
                else color = mix(u_col3, u_col4, (v_u-0.75)/0.25);
                
                if (u_glowMode == -1) {{ 
                    fragColor = vec4(color * 2.0, u_opacity); 
                    return; 
                }}
                
                float distSide = abs(v_quadUv.x);
                float sideAlpha = 1.0 - smoothstep(1.0 - u_blur, 1.0, distSide);
                float vBlend = (u_invertBlend == 1) ? (1.0 - v_v) : v_v;
                float barAlpha = sideAlpha;
                
                if (u_useVerticalBlend == 1) barAlpha *= pow(max(0.0, vBlend), 1.8);
                barAlpha *= (1.0 - smoothstep(0.99, 1.0, v_v));
                
                fragColor = vec4(color, barAlpha * u_opacity);
            }}";

        string coverVertexShader = $@"{shaderVersion}
            layout(location = 0) in vec2 a_pos; 
            layout(location = 1) in vec2 a_uv;
            uniform float u_aspect, u_angle; 
            uniform vec2 u_uvScale; 
            out vec2 v_texUv; 
            out vec2 v_shapeUv;
            void main() {{
                float rad = radians(u_angle);
                float c = cos(rad), s = sin(rad); 
                mat2 rot = mat2(c, -s, s, c);
                v_texUv = (a_uv - 0.5) * u_uvScale + 0.5; 
                v_shapeUv = a_uv;
                gl_Position = vec4((rot * a_pos).x / u_aspect, (rot * a_pos).y, 0.0, 1.0);
            }}";

        string swizzleLine = _isEs ? "" : "tex = vec4(tex.b, tex.g, tex.r, tex.a);";
        string coverFragmentShader = $@"{shaderVersion}
            {(_isEs ? "precision mediump float;" : "")}
            in vec2 v_texUv; 
            in vec2 v_shapeUv; 
            uniform sampler2D u_tex; 
            uniform vec3 u_ringCol; 
            uniform vec4 u_innerCol;
            uniform float u_coverOpacity, u_hasTexture; 
            out vec4 fragColor;
            void main() {{
                float d = distance(v_shapeUv, vec2(0.5)); 
                if (d > 0.5) discard;
                
                vec4 baseCol = u_innerCol;
                if (u_hasTexture > 0.5) {{
                    vec4 tex = texture(u_tex, v_texUv);
                    {swizzleLine}
                    vec4 coverLayer = vec4(tex.rgb, tex.a * u_coverOpacity);
                    baseCol = vec4(mix(baseCol.rgb, coverLayer.rgb, coverLayer.a), max(baseCol.a, coverLayer.a));
                }}

                float ringEdge = smoothstep(0.5, 0.47, d);
                fragColor = mix(vec4(u_ringCol + 0.3, 1.0), baseCol, ringEdge);
                fragColor.a *= smoothstep(0.5, 0.485, d);
            }}";

        _barProgram = CreateProgram(gl, barVertexShader, barFragmentShader);
        _coverProgram = CreateProgram(gl, coverVertexShader, coverFragmentShader);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _texVbo = gl.GenBuffer();
        _texture = gl.GenTexture();

        _glBlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
        _glUniform1F = (delegate* unmanaged[Stdcall]<int, float, void>)gl.GetProcAddress("glUniform1f");
        _glUniform1I = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glUniform1i");
        _glUniform2F = (delegate* unmanaged[Stdcall]<int, float, float, void>)gl.GetProcAddress("glUniform2f");
        _glUniform3F = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)gl.GetProcAddress("glUniform3f");
        _glUniform4F = (delegate* unmanaged[Stdcall]<int, float, float, float, float, void>)gl.GetProcAddress("glUniform4f");
        _glBufferSubData = (delegate* unmanaged[Stdcall]<int, nint, nint, nint, void>)gl.GetProcAddress("glBufferSubData");

        _barAspectLoc = GetUniformLocation(gl, _barProgram, "u_aspect");
        _barOpacityLoc = GetUniformLocation(gl, _barProgram, "u_opacity");
        _barBlurLoc = GetUniformLocation(gl, _barProgram, "u_blur");
        _barUseVerticalBlendLoc = GetUniformLocation(gl, _barProgram, "u_useVerticalBlend");
        _barInvertBlendLoc = GetUniformLocation(gl, _barProgram, "u_invertBlend");
        _barGlowModeLoc = GetUniformLocation(gl, _barProgram, "u_glowMode");
        for (int i = 0; i < _barColorLocs.Length; i++)
            _barColorLocs[i] = GetUniformLocation(gl, _barProgram, $"u_col{i}");

        _coverAspectLoc = GetUniformLocation(gl, _coverProgram, "u_aspect");
        _coverAngleLoc = GetUniformLocation(gl, _coverProgram, "u_angle");
        _coverUvScaleLoc = GetUniformLocation(gl, _coverProgram, "u_uvScale");
        _coverRingColorLoc = GetUniformLocation(gl, _coverProgram, "u_ringCol");
        _coverInnerColorLoc = GetUniformLocation(gl, _coverProgram, "u_innerCol");
        _coverOpacityLoc = GetUniformLocation(gl, _coverProgram, "u_coverOpacity");
        _coverHasTextureLoc = GetUniformLocation(gl, _coverProgram, "u_hasTexture");

        float[] quad = [-0.72f, 0.72f, 0, 0, -0.72f, -0.72f, 0, 1, 0.72f, 0.72f, 1, 0, 0.72f, -0.72f, 1, 1];
        gl.BindBuffer(0x8892, _texVbo);
        fixed (float* p = quad)
        {
            gl.BufferData(0x8892, quad.Length * 4, (nint)p, 0x88E4);
        }

        _gradientDirty = true;
        _textureDirty = true;
        _vsyncDisabled = false;
        _lastTicks = _st.Elapsed.TotalSeconds;
        ResetAdaptiveFramePacing();
        _isAnimating = true;
        _renderTimer?.Start();
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!IsVisible)  return;
        double renderStartMs = _st.Elapsed.TotalMilliseconds;
        
        if (DisableVSync && !_vsyncDisabled)
        {
            try
            {
                _wglSwapIntervalEXT?.Invoke(0);
                var dpy = _eglGetCurrentDisplay?.Invoke() ?? IntPtr.Zero;
                _eglSwapInterval?.Invoke(dpy, 0);
                _vsyncDisabled = true;
            }
            catch { }
        }

        double currentTicks = _st.Elapsed.TotalSeconds;
        float delta = (float)(currentTicks - _lastTicks);
        if (delta <= 0) delta = 1f / 120f; // Default to 120Hz for high-Hertz screens if timing is too fast
        _lastTicks = currentTicks;

        SnapshotSpectrum();
        _isAnimating = UpdatePhysics(delta);
        float scaling = (float)(VisualRoot?.RenderScaling ?? 1.0);
        float winAspect = (float)(Bounds.Width / Math.Max(1, Bounds.Height));

        gl.Viewport(0, 0, (int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
        gl.ClearColor(0, 0, 0, 0);
        gl.Clear(0x00004000 | 0x00000100); 
        gl.Enable(GL_BLEND);

        if (_glBlendFunc != null) _glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        if (_textureDirty && Cover != null) UploadTexture(gl);
        gl.UseProgram(_coverProgram);

        float uScale = 1.0f, vScale = 1.0f;
        if (Cover != null)
        {
            float imgAspect = (float)Cover.PixelSize.Width / Cover.PixelSize.Height;
            if (CoverStretch == Stretch.UniformToFill)
            {
                if (imgAspect > 1.0f) uScale = 1.0f / imgAspect; else vScale = imgAspect;
            }
            else if (CoverStretch == Stretch.Uniform)
            {
                if (imgAspect > 1.0f) vScale = imgAspect; else uScale = 1.0f / imgAspect;
            }
            gl.BindTexture(0x0DE1, _texture);
        }

        SetUniform1F(_coverAspectLoc, winAspect);
        SetUniform1F(_coverAngleLoc, Rotate ? _rotationAngle : 0);
        SetUniform2F(_coverUvScaleLoc, uScale, vScale);
        SetUniform3F(_coverRingColorLoc, RingColor.R / 255f, RingColor.G / 255f, RingColor.B / 255f);
        SetUniform4F(_coverInnerColorLoc, InnerCircleColor.R / 255f, InnerCircleColor.G / 255f, InnerCircleColor.B / 255f, InnerCircleColor.A / 255f);
        SetUniform1F(_coverOpacityLoc, (float)CoverOpacity);
        SetUniform1F(_coverHasTextureLoc, Cover != null ? 1.0f : 0.0f);

        gl.BindVertexArray(_vao);
        gl.BindBuffer(0x8892, _texVbo);
        gl.VertexAttribPointer(0, 2, 0x1406, 0, 16, nint.Zero);
        gl.VertexAttribPointer(1, 2, 0x1406, 0, 16, 8);
        gl.EnableVertexAttribArray(0); gl.EnableVertexAttribArray(1);
        gl.DrawArrays(0x0005, 0, 4);

        RenderSpectrum(gl, winAspect, scaling);
        UpdateAdaptiveFramePacing(delta, _st.Elapsed.TotalMilliseconds - renderStartMs);
        if (!_isAnimating && Volatile.Read(ref _pendingRedraw) == 0)
            _renderTimer?.Stop();
    }

    private void SnapshotSpectrum()
    {
        var spectrum = Spectrum;
        if (spectrum == null)
        {
            _spectrumCount = 0;
            return;
        }

        lock (spectrum)
        {
            int count = spectrum.Count;
            if (_spectrumSnapshot.Length < count)
                _spectrumSnapshot = new float[count];

            for (int i = 0; i < count; i++)
                _spectrumSnapshot[i] = (float)spectrum[i];

            _spectrumCount = count;
        }
    }

    private bool UpdatePhysics(float delta)
    {
        if (!IsVisible) return false;

        int n = Math.Max(2, SampleCount);
        if (_displayedBarLevels.Length != n)
        {
            _displayedBarLevels = new double[n];
            _glowLevels = new double[n];
            _rawSmoothed = new double[n];
            _sampleMap = new int[n];
            _sampleMapTargetCount = -1;
        }
        
        // Re-precompute trig values if count changed
        if (_lastSampleCount != n)
        {
            _precomputedCos = new float[n];
            _precomputedSin = new float[n];
            for (int i = 0; i < n; i++)
            {
                float angle = (float)(i * 2.0 * Math.PI / n);
                _precomputedCos[i] = (float)Math.Cos(angle);
                _precomputedSin[i] = (float)Math.Sin(angle);
            }
            _lastSampleCount = n;
        }

        if (_sampleMapTargetCount != n || _sampleMapSourceCount != _spectrumCount)
        {
            if (_sampleMap.Length < n)
                _sampleMap = new int[n];

            int srcCount = _spectrumCount;
            for (int i = 0; i < n; i++)
            {
                _sampleMap[i] = srcCount > 0
                    ? Math.Min(srcCount - 1, (int)((i / (double)n) * srcCount))
                    : 0;
            }

            _sampleMapTargetCount = n;
            _sampleMapSourceCount = srcCount;
        }

        // Apply toggle logic
        float timeFactor = UseDeltaTime ? delta * 60f : 1.0f; 
        double adjAttack = 1.0 - Math.Pow(1.0 - AttackLerp, timeFactor);
        double adjRelease = 1.0 - Math.Pow(1.0 - ReleaseLerp, timeFactor);
        double adjPeakDecay = 1.0 - Math.Pow(1.0 - PeakDecay, timeFactor);

        double obsMax = 0;
        bool hasVisibleActivity = false;
        double pushMargin = GlowMargin * PushDistanceMultiplier;

        for (int i = 0; i < n; i++)
        {
            double src = _spectrumCount > 0 ? _spectrumSnapshot[_sampleMap[i]] : 0;
            if (double.IsNaN(src) || double.IsInfinity(src) || src < 0.001) src = 0.0;

            _rawSmoothed[i] += (src - _rawSmoothed[i]) * Math.Min(1.0, PrePowAttackAlpha * timeFactor);
            double target = _rawSmoothed[i];

            double effectiveTarget = target > _displayedBarLevels[i]
                ? Math.Min(target, _displayedBarLevels[i] + Math.Max(target * MaxRiseFraction, MaxRiseAbsolute) * timeFactor)
                : target;

            double lerp = (effectiveTarget < _displayedBarLevels[i]) ? adjRelease : adjAttack;
            _displayedBarLevels[i] += (effectiveTarget - _displayedBarLevels[i]) * lerp;

            if (_displayedBarLevels[i] >= _glowLevels[i])
            {
                _glowLevels[i] = _displayedBarLevels[i] + pushMargin;
            }
            else
            {
                _glowLevels[i] += (_displayedBarLevels[i] - _glowLevels[i]) * adjPeakDecay;
            }

            obsMax = Math.Max(obsMax, _displayedBarLevels[i]);
            if (_displayedBarLevels[i] > 0.001 || _glowLevels[i] > 0.001 || _rawSmoothed[i] > 0.001)
                hasVisibleActivity = true;
        }

        double desiredMax = Math.Max(obsMax, _globalMax * 0.98);
        _globalMax += (desiredMax - _globalMax) * (0.12 * timeFactor);
        if (_globalMax < 1e-6) _globalMax = 1e-6;
        if (Rotate) _rotationAngle = (_rotationAngle + 0.4f * timeFactor) % 360;
        return hasVisibleActivity || Rotate || _globalMax > 0.002;
    }

    private unsafe void RenderSpectrum(GlInterface gl, float aspect, float scaling)
    {
        int n = _displayedBarLevels.Length; if (n == 0) return;
        int requiredFloatCount = n * 36;
        if (_glVertices.Length < requiredFloatCount) _glVertices = new float[requiredFloatCount];
        _barVertexFloatCount = requiredFloatCount;
        float innerR = 0.72f;
        double denom = _globalMax * 1.1 + 1e-9;
        float hFactor = (0.25f * (float)(BarHeightPercent / 100.0)) / (float)denom;
        float halfW = (float)BarWidth / 2f;
        float lineThickNdc = (float)(GlowLineThickness / Math.Min(Bounds.Width, Bounds.Height)) * scaling;

        gl.UseProgram(_barProgram);
        SetUniform1F(_barAspectLoc, aspect);
        SetUniform1F(_barOpacityLoc, (float)BarOpacity);
        SetUniform1F(_barBlurLoc, (float)BarBlur);
        SetUniform1I(_barUseVerticalBlendLoc, UseVerticalBlend ? 1 : 0);
        SetUniform1I(_barInvertBlendLoc, InvertBlend ? 1 : 0);
        UpdateGradientUniforms();

        for (int i = 0; i < n; i++)
        {
            float h = (float)(_displayedBarLevels[i] * hFactor);
            FillQuadData(_glVertices, i * 36, innerR, h, halfW, i, (float)i / n);
        }

        SetUniform1I(_barGlowModeLoc, 0);
        gl.BindVertexArray(_vao);
        gl.BindBuffer(0x8892, _vbo);
        fixed (float* ptr = _glVertices)
        {
            UploadVertexData(gl, ptr, _barVertexFloatCount * sizeof(float));
        }

        SetupAttributes(gl);
        gl.DrawArrays(4, 0, _barVertexFloatCount / 6);

        if (UseGlowLine)
        {
            SetUniform1I(_barGlowModeLoc, -1);
            if (_glowLineVertices.Length < requiredFloatCount) _glowLineVertices = new float[requiredFloatCount];
            _glowVertexFloatCount = requiredFloatCount;
            for (int i = 0; i < n; i++)
            {
                float ur = (float)i / n;
                if (GlowMode == 2)
                {
                    float hPeak = (float)(_glowLevels[i] * hFactor);
                    FillQuadData(_glowLineVertices, i * 36, innerR + hPeak, lineThickNdc * 2.0f, halfW, i, ur);
                }
                else
                {
                    int next = (i + 1) % n;
                    float h1 = (float)(_displayedBarLevels[i] * hFactor), h2 = (float)(_displayedBarLevels[next] * hFactor);
                    FillBridgeLine(_glowLineVertices, i * 36, innerR, h1, h2, i, next, lineThickNdc, ur);
                }
            }
            fixed (float* ptr = _glowLineVertices)
            {
                UploadVertexData(gl, ptr, _glowVertexFloatCount * sizeof(float));
            }
            gl.DrawArrays(4, 0, _glowVertexFloatCount / 6);
        }
    }

    private void FillQuadData(float[] buffer, int off, float r, float h, float hw, int index, float ur)
    {
        float c = _precomputedCos[index], s = _precomputedSin[index], tc = -s, ts = c;
        float x0 = (r * c) - (hw * tc), y0 = (r * s) - (hw * ts), x1 = (r * c) + (hw * tc), y1 = (r * s) + (hw * ts);
        float x2 = ((r + h) * c) - (hw * tc), y2 = ((r + h) * s) - (hw * ts), x3 = ((r + h) * c) + (hw * tc), y3 = ((r + h) * s) + (hw * ts);
        WriteV(buffer, off, 0, x0, y0, ur, 0, -1, 0);
        WriteV(buffer, off, 1, x1, y1, ur, 0, 1, 0);
        WriteV(buffer, off, 2, x2, y2, ur, 1, -1, 1);
        WriteV(buffer, off, 3, x1, y1, ur, 0, 1, 0);
        WriteV(buffer, off, 4, x3, y3, ur, 1, 1, 1);
        WriteV(buffer, off, 5, x2, y2, ur, 1, -1, 1);
    }

    private void FillBridgeLine(float[] buffer, int off, float r, float h1, float h2, int idx1, int idx2, float thick, float ur)
    {
        float cos1 = _precomputedCos[idx1], sin1 = _precomputedSin[idx1];
        float cos2 = _precomputedCos[idx2], sin2 = _precomputedSin[idx2];
        float x1 = (r + h1) * cos1, y1 = (r + h1) * sin1;
        float x2 = (r + h2) * cos2, y2 = (r + h2) * sin2;
        float dx = x2 - x1, dy = y2 - y1, len = (float)Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy)), nx = -dy / len * thick, ny = dx / len * thick;
        WriteV(buffer, off, 0, x1 - nx, y1 - ny, ur, 1, 0, 1);
        WriteV(buffer, off, 1, x2 - nx, y2 - ny, ur, 1, 0, 1);
        WriteV(buffer, off, 2, x1 + nx, y1 + ny, ur, 1, 0, 1);
        WriteV(buffer, off, 3, x2 - nx, y2 - ny, ur, 1, 0, 1);
        WriteV(buffer, off, 4, x2 + nx, y2 + ny, ur, 1, 0, 1);
        WriteV(buffer, off, 5, x1 + nx, y1 + ny, ur, 1, 0, 1);
    }

    private void WriteV(float[] b, int off, int i, float x, float y, float u, float v, float qu, float qv)
    {
        int p = off + (i * 6);
        b[p + 0] = x; b[p + 1] = y; b[p + 2] = u; b[p + 3] = v; b[p + 4] = qu; b[p + 5] = qv;
    }

    private void SetupAttributes(GlInterface gl)
    {
        gl.VertexAttribPointer(0, 2, 0x1406, 0, 24, nint.Zero);
        gl.VertexAttribPointer(1, 2, 0x1406, 0, 24, 8);
        gl.VertexAttribPointer(2, 2, 0x1406, 0, 24, 16);
        gl.EnableVertexAttribArray(0); gl.EnableVertexAttribArray(1); gl.EnableVertexAttribArray(2);
    }

    private unsafe void UploadTexture(GlInterface gl)
    {
        _textureDirty = false; if (Cover == null) return;
        gl.BindTexture(0x0DE1, _texture);
        var size = Cover.PixelSize; int stride = size.Width * 4; int pxLen = size.Height * stride;

        // Rent from shared pool to avoid frequent large allocations
        var pool = ArrayPool<byte>.Shared;
        byte[]? px = null;
        try
        {
            px = pool.Rent(pxLen);
            fixed (byte* p = px)
            {
                Cover.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pxLen, stride);
                if (_isEs)
                {
                    // swizzle in-place
                    for (int i = 0; i < pxLen; i += 4) { byte b = px[i + 0], g = px[i + 1], r = px[i + 2], a = px[i + 3]; px[i + 0] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a; }
                    gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x1908, 0x1401, (IntPtr)p);
                }
                else gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x80E1, 0x1401, (IntPtr)p);
            }
        }
        finally
        {
            if (px != null) pool.Return(px, clearArray: false);
        }

        gl.TexParameteri(0x0DE1, 0x2801, 0x2601);
        gl.TexParameteri(0x0DE1, 0x2800, 0x2601);
    }

    private int CreateProgram(GlInterface gl, string v, string f) { int p = gl.CreateProgram(), vs = gl.CreateShader(0x8B31), fs = gl.CreateShader(0x8B30); CompileShader(gl, vs, v); CompileShader(gl, fs, f); gl.AttachShader(p, vs); gl.AttachShader(p, fs); gl.LinkProgram(p); gl.DeleteShader(vs); gl.DeleteShader(fs); return p; }
    private unsafe void CompileShader(GlInterface gl, int s, string src) { var b = Encoding.UTF8.GetBytes(src); int len = b.Length; fixed (byte* p = b) { sbyte* ps = (sbyte*)p; sbyte** pps = &ps; gl.ShaderSource(s, 1, (IntPtr)pps, (IntPtr)(&len)); } gl.CompileShader(s); }
    private unsafe int GetUniformLocation(GlInterface gl, int program, string name) { byte[] nameBytes = Encoding.UTF8.GetBytes(name + '\0'); fixed (byte* ptr = nameBytes) return gl.GetUniformLocation(program, (nint)ptr); }
    private void SetUniform1F(int location, float value) { if (location != -1 && _glUniform1F != null) _glUniform1F(location, value); }
    private void SetUniform1I(int location, int value) { if (location != -1 && _glUniform1I != null) _glUniform1I(location, value); }
    private void SetUniform2F(int location, float x, float y) { if (location != -1 && _glUniform2F != null) _glUniform2F(location, x, y); }
    private void SetUniform3F(int location, float r, float g, float b) { if (location != -1 && _glUniform3F != null) _glUniform3F(location, r, g, b); }
    private void SetUniform4F(int location, float r, float g, float b, float a) { if (location != -1 && _glUniform4F != null) _glUniform4F(location, r, g, b, a); }
    private void UploadVertexData(GlInterface gl, float* ptr, int byteCount) { if (byteCount > _vertexBufferCapacityBytes) { gl.BufferData(0x8892, byteCount, (nint)ptr, GL_DYNAMIC_DRAW); _vertexBufferCapacityBytes = byteCount; } else if (_glBufferSubData != null) { _glBufferSubData(0x8892, 0, byteCount, (nint)ptr); } else { gl.BufferData(0x8892, byteCount, (nint)ptr, GL_DYNAMIC_DRAW); } }
    private void UpdateGradientUniforms() { if (!_gradientDirty) return; var stops = BarGradient?.GradientStops; if (stops == null || stops.Count == 0) { for (int i = 0; i < 5; i++) SetUniform3F(_barColorLocs[i], 0.0f, 0.8f, 1.0f); _gradientDirty = false; return; } for (int i = 0; i < 5; i++) { var color = GetColorAtOffset(stops, i / 4.0f); int colorIndex = i * 3; _gradientColors[colorIndex] = color.R / 255f; _gradientColors[colorIndex + 1] = color.G / 255f; _gradientColors[colorIndex + 2] = color.B / 255f; SetUniform3F(_barColorLocs[i], _gradientColors[colorIndex], _gradientColors[colorIndex + 1], _gradientColors[colorIndex + 2]); } _gradientDirty = false; }
    private static Color GetColorAtOffset(AvaloniaList<GradientStop> stops, float offset) { GradientStop left = stops[0]; GradientStop right = stops[0]; double leftOffset = double.NegativeInfinity; double rightOffset = double.PositiveInfinity; for (int i = 0; i < stops.Count; i++) { var stop = stops[i]; double stopOffset = stop.Offset; if (stopOffset <= offset && stopOffset >= leftOffset) { left = stop; leftOffset = stopOffset; } if (stopOffset >= offset && stopOffset <= rightOffset) { right = stop; rightOffset = stopOffset; } } if (double.IsNegativeInfinity(leftOffset)) left = right; if (double.IsPositiveInfinity(rightOffset)) right = left; if (leftOffset == rightOffset) return left.Color; float t = (offset - (float)left.Offset) / (float)(right.Offset - left.Offset); return Color.FromArgb((byte)(left.Color.A + (right.Color.A - left.Color.A) * t), (byte)(left.Color.R + (right.Color.R - left.Color.R) * t), (byte)(left.Color.G + (right.Color.G - left.Color.G) * t), (byte)(left.Color.B + (right.Color.B - left.Color.B) * t)); }
    private void ResetAdaptiveFramePacing() { _targetFrameIntervalMs = MinAdaptiveFrameIntervalMs; _averageRenderDurationMs = MinAdaptiveFrameIntervalMs * 0.5; _averageFrameDurationMs = MinAdaptiveFrameIntervalMs; _overBudgetFrames = 0; _underBudgetFrames = 0; ApplyRenderTimerInterval(); }
    private void UpdateAdaptiveFramePacing(float delta, double renderDurationMs) { double frameDurationMs = Math.Clamp(delta * 1000.0, MinAdaptiveFrameIntervalMs, MaxAdaptiveFrameIntervalMs * 2.0); _averageFrameDurationMs += (_averageFrameDurationMs - frameDurationMs) * -0.12; _averageRenderDurationMs += (_averageRenderDurationMs - renderDurationMs) * -0.18; double budgetMs = _targetFrameIntervalMs; bool overBudget = _averageRenderDurationMs > budgetMs * 0.72 || renderDurationMs > budgetMs * 0.9; bool underBudget = _averageRenderDurationMs < budgetMs * 0.45 && _averageFrameDurationMs <= budgetMs * 1.15; if (overBudget) { _overBudgetFrames++; _underBudgetFrames = 0; if (_overBudgetFrames >= 3) { _targetFrameIntervalMs = Math.Min(MaxAdaptiveFrameIntervalMs, (_targetFrameIntervalMs * 1.15) + 0.5); _overBudgetFrames = 0; ApplyRenderTimerInterval(); } return; } _overBudgetFrames = 0; if (underBudget) { _underBudgetFrames++; if (_underBudgetFrames >= 18) { _targetFrameIntervalMs = Math.Max(MinAdaptiveFrameIntervalMs, (_targetFrameIntervalMs * 0.9) - 0.25); _underBudgetFrames = 0; ApplyRenderTimerInterval(); } } else if (_underBudgetFrames > 0) { _underBudgetFrames--; } }
    private void ApplyRenderTimerInterval() { if (_renderTimer == null) return; if (Math.Abs(_renderTimer.Interval.TotalMilliseconds - _targetFrameIntervalMs) > AdaptiveIntervalToleranceMs) _renderTimer.Interval = TimeSpan.FromMilliseconds(_targetFrameIntervalMs); }
    protected override void OnOpenGlDeinit(GlInterface gl) 
    {
        _renderTimer?.Stop();
        try
        {
            if (_barProgram != 0) gl.DeleteProgram(_barProgram);
            if (_coverProgram != 0) gl.DeleteProgram(_coverProgram);
            if (_vbo != 0) gl.DeleteBuffer(_vbo);
            if (_vao != 0) gl.DeleteVertexArray(_vao);
            if (_texVbo != 0) gl.DeleteBuffer(_texVbo);
            if (_texture != 0) gl.DeleteTexture(_texture);
        }
        catch (Exception ex) { Log.Warn("Error during OpenGL deinitialization", ex); }

        _barProgram = 0;
        _coverProgram = 0;
        _vbo = 0;
        _vao = 0;
        _texVbo = 0;
        _texture = 0;
        _vertexBufferCapacityBytes = 0;
        _gradientDirty = true;
        _vsyncDisabled = false;
        ResetAdaptiveFramePacing();
    }
    public void Dispose() { }
}
