using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static Avalonia.OpenGL.GlConsts;

namespace AES_Controls.Player.Spectrum;

/// <summary>
/// OpenGL-based spectrum visualiser control. Renders a bar-style spectrum
/// using a small GLSL shader and updates from an <see cref="AvaloniaList{double}"/>
/// backing collection. The control supports VSync toggling and delta-time
/// scaled smoothing for stable visuals across frame rates.
/// </summary>
public class GlSpectrumControl : OpenGlControlBase, IDisposable
{
    private const int GL_DYNAMIC_DRAW = 0x88E8;
    private const int GL_SRC_ALPHA = 0x0302;
    private const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const int GL_BLEND = 0x0BE2;

    #region Styled Properties
    public static readonly StyledProperty<AvaloniaList<double>?> SpectrumProperty =
        AvaloniaProperty.Register<GlSpectrumControl, AvaloniaList<double>?>(nameof(Spectrum));

    // NEW PROPERTY: Enable or disable DeltaTime scaling
    public static readonly StyledProperty<bool> UseDeltaTimeProperty =
        AvaloniaProperty.Register<GlSpectrumControl, bool>(nameof(UseDeltaTime), true);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<GlSpectrumControl, bool>(nameof(DisableVSync), true);

    public static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(BarWidth), 4.0);

    public static readonly StyledProperty<double> BarSpacingProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(BarSpacing), 2.0);

    public static readonly StyledProperty<double> BlockHeightProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(BlockHeight), 8.0);

    public static readonly StyledProperty<double> AttackLerpProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(AttackLerp), 0.42);

    public static readonly StyledProperty<double> ReleaseLerpProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(ReleaseLerp), 0.38);

    public static readonly StyledProperty<double> PeakDecayProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(PeakDecay), 0.85);

    public static readonly StyledProperty<double> PrePowAttackAlphaProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(PrePowAttackAlpha), 0.90);

    public static readonly StyledProperty<double> MaxRiseFractionProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(MaxRiseFraction), 0.55);

    public static readonly StyledProperty<double> MaxRiseAbsoluteProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(MaxRiseAbsolute), 0.05);

    public static readonly StyledProperty<LinearGradientBrush?> BarGradientProperty =
        AvaloniaProperty.Register<GlSpectrumControl, LinearGradientBrush?>(nameof(BarGradient),
            new LinearGradientBrush
            {
                GradientStops =
                [
                    new GradientStop(Color.Parse("#00CCFF"), 0.0),
                    new GradientStop(Color.Parse("#3333FF"), 0.25),
                    new GradientStop(Color.Parse("#CC00CC"), 0.5),
                    new GradientStop(Color.Parse("#FF004D"), 0.75),
                    new GradientStop(Color.Parse("#FFB300"), 1.0)
                ]
            });

    /// <summary>
    /// Source collection containing spectrum magnitudes. May be null to
    /// indicate no input.
    /// </summary>
    public AvaloniaList<double>? Spectrum { get => GetValue(SpectrumProperty); set => SetValue(SpectrumProperty, value); }
    /// <summary>
    /// When true the control scales internal lerp factors by frame delta time.
    /// This produces consistent smoothing across variable frame rates.
    /// </summary>
    public bool UseDeltaTime { get => GetValue(UseDeltaTimeProperty); set => SetValue(UseDeltaTimeProperty, value); }
    /// <summary>
    /// When true, attempts to disable VSync on platforms where the extension
    /// is available to allow higher frame rates for the visualiser.
    /// </summary>
    public bool DisableVSync { get => GetValue(DisableVSyncProperty); set => SetValue(DisableVSyncProperty, value); }
    /// <summary>
    /// Width (in device-independent pixels) of each spectrum bar.
    /// </summary>
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    /// <summary>
    /// Spacing (in device-independent pixels) between spectrum bars.
    /// </summary>
    public double BarSpacing { get => GetValue(BarSpacingProperty); set => SetValue(BarSpacingProperty, value); }
    /// <summary>
    /// Height of repeating blocks used to render the bar texture.
    /// </summary>
    public double BlockHeight { get => GetValue(BlockHeightProperty); set => SetValue(BlockHeightProperty, value); }
    /// <summary>
    /// Attack lerp coefficient for rising values.
    /// </summary>
    public double AttackLerp { get => GetValue(AttackLerpProperty); set => SetValue(AttackLerpProperty, value); }
    /// <summary>
    /// Release lerp coefficient for falling values.
    /// </summary>
    public double ReleaseLerp { get => GetValue(ReleaseLerpProperty); set => SetValue(ReleaseLerpProperty, value); }
    /// <summary>
    /// Decay rate used for peak indicators.
    /// </summary>
    public double PeakDecay { get => GetValue(PeakDecayProperty); set => SetValue(PeakDecayProperty, value); }
    /// <summary>
    /// Alpha used when pre-processing values before power/attack smoothing.
    /// </summary>
    public double PrePowAttackAlpha { get => GetValue(PrePowAttackAlphaProperty); set => SetValue(PrePowAttackAlphaProperty, value); }
    /// <summary>
    /// Maximum fraction of the current value the bar is allowed to rise per frame.
    /// </summary>
    public double MaxRiseFraction { get => GetValue(MaxRiseFractionProperty); set => SetValue(MaxRiseFractionProperty, value); }
    /// <summary>
    /// Absolute maximum rise amount per frame for the bars.
    /// </summary>
    public double MaxRiseAbsolute { get => GetValue(MaxRiseAbsoluteProperty); set => SetValue(MaxRiseAbsoluteProperty, value); }
    /// <summary>
    /// Gradient brush used to colour the spectrum bars.
    /// </summary>
    public LinearGradientBrush? BarGradient { get => GetValue(BarGradientProperty); set => SetValue(BarGradientProperty, value); }
    #endregion

    private int _program, _vertexBuffer, _vao;
    private double[] _displayedBarLevels = [], _peakLevels = [], _rawSmoothed = [];
    private float[] _glVertices = [];
    private double _globalMax = 1e-6;
    private bool _isFirstFrame = true;
    private bool _vsyncDisabled = false;
    private DispatcherTimer? _uiHeartbeat;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int eglSwapIntervalDel(IntPtr dpy, int interval);
    private eglSwapIntervalDel? _eglSwapInterval;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglSwapIntervalEXTDel(int interval);
    private wglSwapIntervalEXTDel? _wglSwapIntervalEXT;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr eglGetCurrentDisplayDel();
    private eglGetCurrentDisplayDel? _eglGetCurrentDisplay;

    // Coalescing flag to prevent posting many dispatcher operations
    private int _pendingRedraw = 0;
    private readonly Action _cachedRenderAction;

    private readonly Stopwatch _st = Stopwatch.StartNew();
    private double _lastTicks;

    private readonly List<IDisposable> _propertySubscriptions = new();
    private INotifyCollectionChanged? _spectrumCollectionRef;
    private NotifyCollectionChangedEventHandler? _spectrumCollectionHandler;

    public GlSpectrumControl()
    {
        _propertySubscriptions.Add(this.GetObservable(SpectrumProperty).Subscribe(new SimpleObserver<AvaloniaList<double>?>(OnSpectrumChanged)));

        // Prepare a cached Action to avoid allocating a new closure for each post
        _cachedRenderAction = () =>
        {
            try { RequestNextFrameRendering(); }
            finally { Interlocked.Exchange(ref _pendingRedraw, 0); }
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // If the control just became visible, start the render loop again
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            RequestNextFrameRendering();
        }
    }

    private void OnSpectrumChanged(AvaloniaList<double>? col)
    {
        try { if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null) _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler; } catch { }
        _spectrumCollectionRef = null; _spectrumCollectionHandler = null;
        if (col is INotifyCollectionChanged notify)
        {
            _spectrumCollectionRef = notify;
            _spectrumCollectionHandler = (s, e) => RequestRedraw();
            notify.CollectionChanged += _spectrumCollectionHandler;
        }
        _isFirstFrame = true;
        RequestRedraw();
    }

    private void RequestRedraw()
    {
        if (Interlocked.CompareExchange(ref _pendingRedraw, 1, 0) == 0)
        {
            try
            {
                Dispatcher.UIThread.Post(_cachedRenderAction, DispatcherPriority.Render);
            }
            catch
            {
                Interlocked.Exchange(ref _pendingRedraw, 0);
            }
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            IntPtr pWglSwap = gl.GetProcAddress("wglSwapIntervalEXT");
            if (pWglSwap != IntPtr.Zero) _wglSwapIntervalEXT = Marshal.GetDelegateForFunctionPointer<wglSwapIntervalEXTDel>(pWglSwap);
            IntPtr pEglSwap = gl.GetProcAddress("eglSwapInterval");
            if (pEglSwap != IntPtr.Zero) _eglSwapInterval = Marshal.GetDelegateForFunctionPointer<eglSwapIntervalDel>(pEglSwap);
            IntPtr pGetDisplay = gl.GetProcAddress("eglGetCurrentDisplay");
            if (pGetDisplay != IntPtr.Zero) _eglGetCurrentDisplay = Marshal.GetDelegateForFunctionPointer<eglGetCurrentDisplayDel>(pGetDisplay);
        }
        catch { }

        var shaderInfo = GlHelper.GetShaderVersion(gl);
        string vs = $@"{shaderInfo.Item1}
        layout(location = 0) in vec2 a_position; 
        layout(location = 1) in vec2 a_uv; 
        layout(location = 2) in float a_type;
        out vec2 v_uv; 
        out float v_type;
        void main() {{ 
            v_uv = a_uv; 
            v_type = a_type; 
            gl_Position = vec4(a_position, 0.0, 1.0); 
        }}";

        string fs = $@"{shaderInfo.Item1}
        {(shaderInfo.Item2 ? "precision mediump float;" : "")}
        in vec2 v_uv; 
        in float v_type;
        uniform float u_blockHeight; 
        uniform float u_totalHeight;
        uniform vec3 u_col0, u_col1, u_col2, u_col3, u_col4;
        out vec4 fragColor;
        void main() {{
            float u = v_uv.x; 
            vec3 color;
            if (u < 0.25) color = mix(u_col0, u_col1, u/0.25); 
            else if (u < 0.5) color = mix(u_col1, u_col2, (u-0.25)/0.25);
            else if (u < 0.75) color = mix(u_col2, u_col3, (u-0.5)/0.25); 
            else color = mix(u_col3, u_col4, (u-0.75)/0.25);
            
            if (v_type > 0.5) {{
                fragColor = vec4(1.0, 1.0, 1.0, 0.95);
            }} else {{ 
                float absY = v_uv.y * u_totalHeight; 
                float tile = step(u_blockHeight * 0.15, mod(absY, u_blockHeight));
                fragColor = vec4(color, tile * mix(0.65, 1.0, v_uv.y)); 
            }}
        }}";

        _program = gl.CreateProgram();
        int vShader = gl.CreateShader(GL_VERTEX_SHADER);
        int fShader = gl.CreateShader(GL_FRAGMENT_SHADER);

        CompileShader(gl, vShader, vs);
        CompileShader(gl, fShader, fs);

        gl.AttachShader(_program, vShader);
        gl.AttachShader(_program, fShader);
        gl.LinkProgram(_program);

        _vao = gl.GenVertexArray();
        _vertexBuffer = gl.GenBuffer();

        _uiHeartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(4), DispatcherPriority.Render, (_, _) =>
        {
            if (IsVisible) RequestNextFrameRendering();
        });
        _uiHeartbeat.Start();
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!IsVisible)  return;
        
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
        if (delta <= 0) delta = 1f / 120f;
        _lastTicks = currentTicks;

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        float actualWidth = (float)(Bounds.Width * scaling);
        float actualHeight = (float)(Bounds.Height * scaling);
        int targetCount = Math.Max(1, (int)(actualWidth / (BarWidth + BarSpacing)));

        UpdatePhysics(targetCount, delta);

        gl.Enable(GL_BLEND);
        var glBlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
        if (glBlendFunc != null) glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        gl.Viewport(0, 0, (int)actualWidth, (int)actualHeight);
        gl.ClearColor(0, 0, 0, 0);
        gl.Clear(GL_COLOR_BUFFER_BIT);
        gl.UseProgram(_program);

        UpdateGradientUniforms(gl);
        SetUniform1F(gl, "u_blockHeight", (float)(BlockHeight * scaling));
        SetUniform1F(gl, "u_totalHeight", actualHeight);

        PrepareVertices(actualWidth, actualHeight, targetCount);

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vertexBuffer);

        fixed (float* ptr = _glVertices)
        {
            gl.BufferData(GL_ARRAY_BUFFER, _glVertices.Length * 4, (nint)ptr, GL_DYNAMIC_DRAW);
        }

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 20, nint.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GL_FLOAT, 0, 20, 8);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 1, GL_FLOAT, 0, 20, 16);

        gl.DrawArrays(GL_TRIANGLES, 0, _glVertices.Length / 5);
        RequestNextFrameRendering();
    }

    private void UpdatePhysics(int targetCount, float delta)
    {
        if (_displayedBarLevels.Length != targetCount)
        {
            _displayedBarLevels = new double[targetCount];
            _peakLevels = new double[targetCount];
            _rawSmoothed = new double[targetCount];
            _isFirstFrame = true;
        }

        double[] values = [];
        if (Spectrum != null)
        {
            lock (Spectrum)
            {
                int count = Spectrum.Count;
                if (count > 0)
                {
                    values = new double[count];
                    for (int i = 0; i < count; i++) values[i] = Spectrum[i];
                }
            }
        }

        // Use a fixed 1.0 factor if delta-time is disabled
        float timeFactor = UseDeltaTime ? delta * 60f : 1.0f; 

        double adjAttack = 1.0 - Math.Pow(1.0 - AttackLerp, timeFactor);
        double adjRelease = 1.0 - Math.Pow(1.0 - ReleaseLerp, timeFactor);
        double adjPeakDecay = 1.0 - Math.Pow(1.0 - PeakDecay, timeFactor);

        double observedMax = 0.0;
        for (int i = 0; i < targetCount; i++)
        {
            double src = values.Length > 0 ? values[(int)Math.Min(values.Length - 1, (i / (double)targetCount) * values.Length)] : 0.0;

            if (double.IsNaN(src) || double.IsInfinity(src) || src < 0.0001) src = 0.0;

            _rawSmoothed[i] += (src - _rawSmoothed[i]) * Math.Min(1.0, PrePowAttackAlpha * timeFactor);
            double target = _rawSmoothed[i];

            if (_isFirstFrame && target > 0)
            {
                _displayedBarLevels[i] = target;
                _peakLevels[i] = target;
            }
            else
            {
                double effectiveTarget = target > _displayedBarLevels[i]
                    ? Math.Min(target, _displayedBarLevels[i] + Math.Max(target * MaxRiseFraction, MaxRiseAbsolute) * timeFactor)
                    : target;

                double lerp = (effectiveTarget < _displayedBarLevels[i]) ? adjRelease : adjAttack;
                _displayedBarLevels[i] += (effectiveTarget - _displayedBarLevels[i]) * lerp;

                if (_displayedBarLevels[i] > _peakLevels[i]) _peakLevels[i] = _displayedBarLevels[i];
                else _peakLevels[i] += (_displayedBarLevels[i] - _peakLevels[i]) * adjPeakDecay;
            }

            if (_displayedBarLevels[i] > observedMax) observedMax = _displayedBarLevels[i];
        }

        if (observedMax > 0.001)
        {
            if (_isFirstFrame)
            {
                _globalMax = observedMax;
                _isFirstFrame = false;
            }
            else
            {
                double lerpSpeed = (observedMax > _globalMax) ? 0.15 : 0.01;
                _globalMax += (observedMax - _globalMax) * Math.Min(1.0, lerpSpeed * timeFactor);
            }
        }

        if (_globalMax < 0.05) _globalMax = 0.05;
    }

    private void PrepareVertices(float w, float h, int n)
    {
        if (n == 0) return;
        if (_glVertices.Length != n * 60) _glVertices = new float[n * 60];

        float glStep = 2.0f / n;
        float glBarWidth = (float)((BarWidth / w) * 2.0f);
        float glGapHalf = (float)(BarSpacing / w);
        double denom = _globalMax * 1.1 + 1e-9;

        for (int i = 0; i < n; i++)
        {
            float x0 = -1.0f + (i * glStep) + glGapHalf;
            float x1 = x0 + glBarWidth;
            float u = i / (float)Math.Max(1, n - 1);

            float yBarNorm = (float)Math.Clamp(_displayedBarLevels[i] / denom, 0.0, 1.0);
            float yBar = -1.0f + (yBarNorm * 2.0f);
            int off = i * 60;

            AddVert(off + 0, x0, -1.0f, u, 0.0f, 0.0f);
            AddVert(off + 5, x1, -1.0f, u, 0.0f, 0.0f);
            AddVert(off + 10, x0, yBar, u, yBarNorm, 0.0f);
            AddVert(off + 15, x0, yBar, u, yBarNorm, 0.0f);
            AddVert(off + 20, x1, -1.0f, u, 0.0f, 0.0f);
            AddVert(off + 25, x1, yBar, u, yBarNorm, 0.0f);

            float pyNorm = (float)Math.Clamp(_peakLevels[i] / denom, 0.0, 1.0);
            float py1 = -1.0f + (pyNorm * 2.0f);
            float py0 = py1 - (4.0f / h);
            int pOff = off + 30;

            AddVert(pOff + 0, x0, py0, u, pyNorm, 1.0f);
            AddVert(pOff + 5, x1, py0, u, pyNorm, 1.0f);
            AddVert(pOff + 10, x0, py1, u, pyNorm, 1.0f);
            AddVert(pOff + 15, x0, py1, u, pyNorm, 1.0f);
            AddVert(pOff + 20, x1, py0, u, pyNorm, 1.0f);
            AddVert(pOff + 25, x1, py1, u, pyNorm, 1.0f);
        }
    }

    private void AddVert(int idx, float x, float y, float u, float v, float t)
    {
        _glVertices[idx + 0] = x;
        _glVertices[idx + 1] = y;
        _glVertices[idx + 2] = u;
        _glVertices[idx + 3] = v;
        _glVertices[idx + 4] = t;
    }

    private void UpdateGradientUniforms(GlInterface gl)
    {
        var stops = BarGradient?.GradientStops.OrderBy(x => x.Offset).ToList();
        if (stops == null || stops.Count < 2)
        {
            for (int i = 0; i < 5; i++) SetUniform3F(gl, $"u_col{i}", 0.0f, 0.8f, 1.0f);
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            var c = GetColorAtOffset(stops, i / 4.0f);
            SetUniform3F(gl, $"u_col{i}", c.R / 255f, c.G / 255f, c.B / 255f);
        }
    }

    private Color GetColorAtOffset(List<GradientStop> stops, float offset)
    {
        var left = stops.LastOrDefault(x => x.Offset <= offset) ?? stops.First();
        var right = stops.FirstOrDefault(x => x.Offset >= offset) ?? stops.Last();
        if (left == right) return left.Color;
        float t = (offset - (float)left.Offset) / (float)(right.Offset - left.Offset);
        return Color.FromArgb(
            (byte)(left.Color.A + (right.Color.A - left.Color.A) * t),
            (byte)(left.Color.R + (right.Color.R - left.Color.R) * t),
            (byte)(left.Color.G + (right.Color.G - left.Color.G) * t),
            (byte)(left.Color.B + (right.Color.B - left.Color.B) * t));
    }

    private unsafe void CompileShader(GlInterface gl, int shader, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = bytes)
        {
            sbyte* pStr = (sbyte*)ptr;
            sbyte** ppStr = &pStr;
            int len = bytes.Length;
            gl.ShaderSource(shader, 1, (IntPtr)ppStr, (IntPtr)(&len));
        }
        gl.CompileShader(shader);
    }

    private unsafe void SetUniform1F(GlInterface gl, string name, float val)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(_program, ptr);
        Marshal.FreeHGlobal(ptr);
        if (loc != -1)
        {
            var func = (delegate* unmanaged[Stdcall]<int, float, void>)gl.GetProcAddress("glUniform1f");
            if (func != null) func(loc, val);
        }
    }

    private unsafe void SetUniform3F(GlInterface gl, string name, float r, float g, float b)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(_program, ptr);
        Marshal.FreeHGlobal(ptr);
        if (loc != -1)
        {
            var func = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)gl.GetProcAddress("glUniform3f");
            if (func != null) func(loc, r, g, b);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _uiHeartbeat?.Stop();
        try { if (_program != 0) gl.DeleteProgram(_program); } catch { }
        try { if (_vertexBuffer != 0) gl.DeleteBuffer(_vertexBuffer); } catch { }
        try { if (_vao != 0) gl.DeleteVertexArray(_vao); } catch { }

        try { foreach (var d in _propertySubscriptions) d.Dispose(); } catch { }
        _propertySubscriptions.Clear();
        try { if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null) _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler; } catch { }
        _spectrumCollectionRef = null; _spectrumCollectionHandler = null;
    }

    public void Dispose() { }
}