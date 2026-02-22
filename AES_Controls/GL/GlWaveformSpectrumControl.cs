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
using log4net;

namespace AES_Controls.GL;

public class GlWaveformSpectrumControl : OpenGlControlBase, IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(GlWaveformSpectrumControl));
    private const int GL_DYNAMIC_DRAW = 0x88E8;
    private const int GL_TRIANGLE_STRIP = 5;
    private const int GL_SRC_ALPHA = 0x0302;
    private const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const int GL_BLEND = 0x0BE2;

    #region Styled Properties
    public static readonly StyledProperty<AvaloniaList<double>?> SpectrumProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, AvaloniaList<double>?>(nameof(Spectrum));

    public static readonly StyledProperty<int> SampleCountProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, int>(nameof(SampleCount), 128);

    public static readonly StyledProperty<double> WaveHeightPercentProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, double>(nameof(WaveHeightPercent), 85.0);

    public static readonly StyledProperty<double> AttackLerpProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, double>(nameof(AttackLerp), 0.45);

    public static readonly StyledProperty<double> ReleaseLerpProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, double>(nameof(ReleaseLerp), 0.15);

    public static readonly StyledProperty<bool> SetDualSpectrumProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, bool>(nameof(SetDualSpectrum));

    public static readonly StyledProperty<bool> BarModeProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, bool>(nameof(BarMode));

    public static readonly StyledProperty<double> BarGapSpacingProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, double>(nameof(BarGapSpacing), 2.0);

    public static readonly StyledProperty<double> BarOpacityProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, double>(nameof(BarOpacity), 1.0);

    public static readonly StyledProperty<LinearGradientBrush?> BarGradientProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, LinearGradientBrush?>(nameof(BarGradient),
            new LinearGradientBrush
            {
                GradientStops = [
                    new GradientStop(Color.Parse("#00CCFF"), 0.0),
                    new GradientStop(Color.Parse("#3333FF"), 0.25),
                    new GradientStop(Color.Parse("#CC00CC"), 0.5),
                    new GradientStop(Color.Parse("#FF004D"), 0.75),
                    new GradientStop(Color.Parse("#FFB300"), 1.0)
                ]
            });

    public static readonly StyledProperty<bool> UseDeltaTimeProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, bool>(nameof(UseDeltaTime), true);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<GlWaveformSpectrumControl, bool>(nameof(DisableVSync), true);

    public AvaloniaList<double>? Spectrum { get => GetValue(SpectrumProperty); set => SetValue(SpectrumProperty, value); }
    public int SampleCount { get => GetValue(SampleCountProperty); set => SetValue(SampleCountProperty, value); }
    public double WaveHeightPercent { get => GetValue(WaveHeightPercentProperty); set => SetValue(WaveHeightPercentProperty, value); }
    public double AttackLerp { get => GetValue(AttackLerpProperty); set => SetValue(AttackLerpProperty, value); }
    public double ReleaseLerp { get => GetValue(ReleaseLerpProperty); set => SetValue(ReleaseLerpProperty, value); }
    public bool SetDualSpectrum { get => GetValue(SetDualSpectrumProperty); set => SetValue(SetDualSpectrumProperty, value); }
    public bool BarMode { get => GetValue(BarModeProperty); set => SetValue(BarModeProperty, value); }
    public double BarGapSpacing { get => GetValue(BarGapSpacingProperty); set => SetValue(BarGapSpacingProperty, value); }
    public double BarOpacity { get => GetValue(BarOpacityProperty); set => SetValue(BarOpacityProperty, value); }
    public LinearGradientBrush? BarGradient { get => GetValue(BarGradientProperty); set => SetValue(BarGradientProperty, value); }
    public bool UseDeltaTime { get => GetValue(UseDeltaTimeProperty); set => SetValue(UseDeltaTimeProperty, value); }
    public bool DisableVSync { get => GetValue(DisableVSyncProperty); set => SetValue(DisableVSyncProperty, value); }
    #endregion

    private int _program, _vertexBuffer, _vao;
    private float[] _glVertices = [];
    private double[] _smoothedLevels = [];
    private double _globalMax = 1e-6;

    private int _renderRequested = 0;
    private readonly Action _renderAction;
    private readonly Stopwatch _st = Stopwatch.StartNew();
    private double _lastTicks;
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

    private readonly List<IDisposable> _propertySubscriptions = new();
    private INotifyCollectionChanged? _spectrumCollectionRef;
    private NotifyCollectionChangedEventHandler? _spectrumCollectionHandler;

    public GlWaveformSpectrumControl()
    {
        _propertySubscriptions.Add(this.GetObservable(SpectrumProperty).Subscribe(new SimpleObserver<AvaloniaList<double>?>(OnSpectrumChanged)));

        _renderAction = () =>
        {
            try { RequestNextFrameRendering(); }
            finally { Interlocked.Exchange(ref _renderRequested, 0); }
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
        try { if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null) _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler; } catch (Exception ex) { Log.Warn("Error unsubscribing from spectrum collection", ex); }
        _spectrumCollectionRef = null; _spectrumCollectionHandler = null;
        if (col is INotifyCollectionChanged notify)
        {
            _spectrumCollectionRef = notify;
            _spectrumCollectionHandler = (s, e) => RequestRedraw();
            notify.CollectionChanged += _spectrumCollectionHandler;
        }
        RequestRedraw();
    }

    private void RequestRedraw()
    {
        if (Interlocked.CompareExchange(ref _renderRequested, 1, 0) == 0)
        {
            try { Dispatcher.UIThread.Post(_renderAction, DispatcherPriority.Render); }
            catch (Exception ex) { Log.Error("Error posting render action", ex); Interlocked.Exchange(ref _renderRequested, 0); }
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        var shaderInfo = GlHelper.GetShaderVersion(gl);
        var shaderVersion = shaderInfo.Item1;
        var isEs = shaderInfo.Item2;

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

        string vertexShaderSource = $@"{shaderVersion}
        layout(location = 0) in vec2 a_position; 
        layout(location = 1) in vec2 a_uv;
        out vec2 v_uv; 
        out float v_posX;
        void main() {{ 
            v_uv = a_uv; 
            v_posX = a_position.x; 
            gl_Position = vec4(a_position, 0.0, 1.0); 
        }}";

        string fragmentShaderSource = $@"{shaderVersion}
        {(isEs ? "precision mediump float;" : "")}
        in vec2 v_uv; 
        in float v_posX;
        uniform vec3 u_col0, u_col1, u_col2, u_col3, u_col4;
        uniform float u_barMode, u_sampleCount, u_gapPixels, u_totalWidth, u_opacity;
        out vec4 fragColor;
        void main() {{
            if (u_barMode > 0.5) {{
                float barWidthWorld = 2.0 / u_sampleCount;
                float gapWidthWorld = (u_gapPixels * 2.0) / u_totalWidth;
                float localX = mod(v_posX + 1.0, barWidthWorld);
                if (localX < gapWidthWorld * 0.5 || localX > (barWidthWorld - gapWidthWorld * 0.5)) discard;
            }}

            float u = v_uv.x; 
            vec3 color;
            if (u < 0.25) color = mix(u_col0, u_col1, u/0.25);
            else if (u < 0.5) color = mix(u_col1, u_col2, (u-0.25)/0.25);
            else if (u < 0.75) color = mix(u_col2, u_col3, (u-0.5)/0.25);
            else color = mix(u_col3, u_col4, (u-0.75)/0.25);

            float dist = abs(v_uv.y - 0.5) * 2.0;
            float alpha = smoothstep(1.0, 0.0, dist);
            float glow = exp(-pow(dist * 2.5, 2.0));
            
            fragColor = vec4(color + (glow * 0.4), max(alpha * u_opacity, glow * (u_opacity * 0.7)));
        }}";

        _program = gl.CreateProgram();
        int vShader = gl.CreateShader(GL_VERTEX_SHADER);
        int fShader = gl.CreateShader(GL_FRAGMENT_SHADER);
        CompileShader(gl, vShader, vertexShaderSource);
        CompileShader(gl, fShader, fragmentShaderSource);
        gl.AttachShader(_program, vShader); 
        gl.AttachShader(_program, fShader); 
        gl.LinkProgram(_program);
        gl.DeleteShader(vShader); 
        gl.DeleteShader(fShader);
        
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

        UpdatePhysics(delta);
        gl.Enable(GL_BLEND);
        var glBlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
        if (glBlendFunc != null) glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        float actualWidth = (float)(Bounds.Width * scaling);
        float actualHeight = (float)(Bounds.Height * scaling);

        gl.Viewport(0, 0, (int)actualWidth, (int)actualHeight);
        gl.ClearColor(0, 0, 0, 0); 
        gl.Clear(GL_COLOR_BUFFER_BIT);
        gl.UseProgram(_program);

        PrepareVertices();
        UpdateGradientUniforms(gl);

        SetUniform1F(gl, "u_barMode", BarMode ? 1.0f : 0.0f);
        SetUniform1F(gl, "u_sampleCount", SampleCount);
        SetUniform1F(gl, "u_gapPixels", (float)(BarGapSpacing * scaling));
        SetUniform1F(gl, "u_totalWidth", actualWidth);
        SetUniform1F(gl, "u_opacity", (float)BarOpacity);

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vertexBuffer);
        fixed (float* ptr = _glVertices) 
            gl.BufferData(GL_ARRAY_BUFFER, _glVertices.Length * 4, (nint)ptr, GL_DYNAMIC_DRAW);

        gl.EnableVertexAttribArray(0); 
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 16, nint.Zero);
        gl.EnableVertexAttribArray(1); 
        gl.VertexAttribPointer(1, 2, GL_FLOAT, 0, 16, 8);

        gl.DrawArrays(GL_TRIANGLE_STRIP, 0, _glVertices.Length / 4);
        RequestNextFrameRendering();
    }

    private void UpdatePhysics(float delta)
    {
        int n = Math.Max(2, SampleCount);
        if (_smoothedLevels.Length != n) _smoothedLevels = new double[n];
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

        float timeFactor = UseDeltaTime ? delta * 60f : 1.0f;
        double adjAttack = 1.0 - Math.Pow(1.0 - AttackLerp, timeFactor);
        double adjRelease = 1.0 - Math.Pow(1.0 - ReleaseLerp, timeFactor);

        double currentMax = 0.0;
        for (int i = 0; i < n; i++)
        {
            double src = values.Length > 0 ? values[(int)Math.Min(values.Length - 1, (i / (double)(n - 1)) * values.Length)] : 0;
            if (double.IsNaN(src) || double.IsInfinity(src)) src = 0;

            double lerp = src > _smoothedLevels[i] ? adjAttack : adjRelease;
            _smoothedLevels[i] += (src - _smoothedLevels[i]) * Math.Min(1.0, lerp);

            if (_smoothedLevels[i] > currentMax) currentMax = _smoothedLevels[i];
        }
        _globalMax += (Math.Max(currentMax, _globalMax * 0.95) - _globalMax) * Math.Min(1.0, 0.1 * timeFactor);
        if (_globalMax < 1e-6) _globalMax = 1e-6;
    }

        private void PrepareVertices()
        {
            int n = _smoothedLevels.Length;
            if (_glVertices.Length != n * 8) _glVertices = new float[n * 8];
            double verticalFill = Math.Clamp(WaveHeightPercent / 100.0, 0.0, 1.0);
            double normalizedMax = Math.Max(_globalMax, 1e-6);
            double hFactor = verticalFill / (normalizedMax + 1e-9);

        for (int i = 0; i < n; i++)
        {
            float x, u;
            double level;

            if (SetDualSpectrum)
            {
                float center = (n - 1) / 2.0f;
                float normalizedDist = Math.Abs(i - center) / center;
                x = -1.0f + (2.0f * i / (n - 1));
                int dataIdx = (int)((1.0f - normalizedDist) * (n - 1));
                level = _smoothedLevels[Math.Clamp(dataIdx, 0, n - 1)];
                u = 1.0f - normalizedDist;
            }
            else
            {
                x = -1.0f + (2.0f * i / (n - 1));
                u = (float)i / (n - 1);
                level = _smoothedLevels[i];
            }

            float y = (float)(level * hFactor);
            int offT = i * 8, offB = i * 8 + 4;
            _glVertices[offT + 0] = x; 
            _glVertices[offT + 1] = y; 
            _glVertices[offT + 2] = u; 
            _glVertices[offT + 3] = 1.0f;
            
            _glVertices[offB + 0] = x; 
            _glVertices[offB + 1] = -y; 
            _glVertices[offB + 2] = u; 
            _glVertices[offB + 3] = 0.0f;
        }
    }

    private void UpdateGradientUniforms(GlInterface gl)
    {
        var stops = BarGradient?.GradientStops.OrderBy(x => x.Offset).ToList(); 
        if (stops == null || stops.Count < 2) return;
        for (int i = 0; i < 5; i++) 
        { 
            var color = GetColorAtOffset(stops, i / 4.0f); 
            SetUniform3F(gl, $"u_col{i}", color.R / 255f, color.G / 255f, color.B / 255f); 
        }
    }

    private Color GetColorAtOffset(List<GradientStop> stops, float offset)
    {
        var left = stops.LastOrDefault(x => x.Offset <= offset) ?? stops.First(); 
        var right = stops.FirstOrDefault(x => x.Offset >= offset) ?? stops.Last();
        if (left == right) return left.Color; 
        float t = (offset - (float)left.Offset) / (float)(right.Offset - left.Offset);
        return Color.FromUInt32((uint)(((byte)(left.Color.A + (right.Color.A - left.Color.A) * t) << 24) | 
                                       ((byte)(left.Color.R + (right.Color.R - left.Color.R) * t) << 16) | 
                                       ((byte)(left.Color.G + (right.Color.G - left.Color.G) * t) << 8) | 
                                       (byte)(left.Color.B + (right.Color.B - left.Color.B) * t)));
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
            gl.CompileShader(shader); 
        }
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
        try
        {
            if (_program != 0) gl.DeleteProgram(_program);
            if (_vertexBuffer != 0) gl.DeleteBuffer(_vertexBuffer);
            if (_vao != 0) gl.DeleteVertexArray(_vao);
        }
        catch { }

        try { foreach (var d in _propertySubscriptions) d.Dispose(); } catch { }
        _propertySubscriptions.Clear();

        try { if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null) _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler; } catch { }
        _spectrumCollectionRef = null; _spectrumCollectionHandler = null;
    }

    public void Dispose() { }
}