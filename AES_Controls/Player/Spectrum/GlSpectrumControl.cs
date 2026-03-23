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
public unsafe class GlSpectrumControl : OpenGlControlBase, IDisposable
{
    private const int GL_DYNAMIC_DRAW = 0x88E8;
    private const int GL_SRC_ALPHA = 0x0302;
    private const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const int GL_BLEND = 0x0BE2;
    private const double MinAdaptiveFrameIntervalMs = 1000.0 / 120.0;
    private const double MaxAdaptiveFrameIntervalMs = 1000.0 / 30.0;
    private const double AdaptiveIntervalToleranceMs = 0.25;

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
    private float[] _spectrumSnapshot = [];
    private int[] _sampleMap = [];
    private readonly float[] _gradientColors = new float[15];
    private double _globalMax = 1e-6;
    private bool _isFirstFrame = true;
    private bool _vsyncDisabled = false;
    private bool _gradientDirty = true;
    private bool _isAnimating;
    private int _spectrumCount;
    private int _sampleMapTargetCount = -1;
    private int _sampleMapSourceCount = -1;
    private int _vertexFloatCount;
    private int _vertexBufferCapacityBytes;
    private DispatcherTimer? _renderTimer;
    private double _targetFrameIntervalMs = MinAdaptiveFrameIntervalMs;
    private double _averageRenderDurationMs = MinAdaptiveFrameIntervalMs * 0.5;
    private double _averageFrameDurationMs = MinAdaptiveFrameIntervalMs;
    private int _overBudgetFrames;
    private int _underBudgetFrames;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int eglSwapIntervalDel(IntPtr dpy, int interval);
    private eglSwapIntervalDel? _eglSwapInterval;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool wglSwapIntervalEXTDel(int interval);
    private wglSwapIntervalEXTDel? _wglSwapIntervalEXT;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr eglGetCurrentDisplayDel();
    private eglGetCurrentDisplayDel? _eglGetCurrentDisplay;

    private int _pendingRedraw = 0;
    private int _uBlockHeightLoc = -1;
    private int _uTotalHeightLoc = -1;
    private readonly int[] _uColorLocs = new int[5];

    private delegate* unmanaged[Stdcall]<int, int, void> _glBlendFunc;
    private delegate* unmanaged[Stdcall]<int, float, void> _glUniform1F;
    private delegate* unmanaged[Stdcall]<int, float, float, float, void> _glUniform3F;
    private delegate* unmanaged[Stdcall]<int, nint, nint, nint, void> _glBufferSubData;

    private readonly Stopwatch _st = Stopwatch.StartNew();
    private double _lastTicks;

    private INotifyCollectionChanged? _spectrumCollectionRef;
    private NotifyCollectionChangedEventHandler? _spectrumCollectionHandler;

    public GlSpectrumControl()
    {
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
        if (change.Property == SpectrumProperty)
        {
            OnSpectrumChanged(change.GetNewValue<AvaloniaList<double>?>());
        }
        else if (change.Property == BarGradientProperty)
        {
            _gradientDirty = true;
            RequestRedraw();
        }
        else if (change.Property != IsVisibleProperty)
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
        _isAnimating = true;
        RequestRedraw();
    }

    private void RequestRedraw()
    {
        _isAnimating = true;
        Interlocked.Exchange(ref _pendingRedraw, 1);
        if (IsVisible)
            _renderTimer?.Start();
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

        _glBlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
        _glUniform1F = (delegate* unmanaged[Stdcall]<int, float, void>)gl.GetProcAddress("glUniform1f");
        _glUniform3F = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)gl.GetProcAddress("glUniform3f");
        _glBufferSubData = (delegate* unmanaged[Stdcall]<int, nint, nint, nint, void>)gl.GetProcAddress("glBufferSubData");

        _uBlockHeightLoc = GetUniformLocation(gl, "u_blockHeight");
        _uTotalHeightLoc = GetUniformLocation(gl, "u_totalHeight");
        for (int i = 0; i < _uColorLocs.Length; i++)
            _uColorLocs[i] = GetUniformLocation(gl, $"u_col{i}");

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vertexBuffer);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 20, nint.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GL_FLOAT, 0, 20, 8);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 1, GL_FLOAT, 0, 20, 16);

        _gradientDirty = true;
        _vsyncDisabled = false;
        _lastTicks = _st.Elapsed.TotalSeconds;
        ResetAdaptiveFramePacing();
        _renderTimer?.Start();
        _isAnimating = true;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!IsVisible) return;
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
        if (delta <= 0) delta = 1f / 120f;
        _lastTicks = currentTicks;

        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        // Use logical (device-independent) width when calculating how many bars fit so
        // the visual density remains constant across DPI scaling. Convert height to
        // physical pixels for GL viewport and some vertical calculations.
        float logicalWidth = (float)Bounds.Width;
        float physicalWidth = logicalWidth * (float)scaling;
        float physicalHeight = (float)(Bounds.Height * scaling);
        int targetCount = Math.Max(1, (int)(logicalWidth / (BarWidth + BarSpacing)));

        SnapshotSpectrum();
        _isAnimating = UpdatePhysics(targetCount, delta);

        gl.Enable(GL_BLEND);
        if (_glBlendFunc != null) _glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        gl.Viewport(0, 0, (int)physicalWidth, (int)physicalHeight);
        gl.ClearColor(0, 0, 0, 0);
        gl.Clear(GL_COLOR_BUFFER_BIT);
        gl.UseProgram(_program);

        UpdateGradientUniforms(gl);
        SetUniform1F(_uBlockHeightLoc, (float)(BlockHeight * scaling));
        SetUniform1F(_uTotalHeightLoc, physicalHeight);

        // Pass logical width and physical height to PrepareVertices. The vertex
        // generator expects the width parameter to be in logical (device-independent)
        // units for correct normalized X coordinates, while height should be physical
        // pixels for pixel-based offsets (peak indicator size).
        PrepareVertices(logicalWidth, physicalHeight, targetCount);

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vertexBuffer);

        fixed (float* ptr = _glVertices)
        {
            int byteCount = _vertexFloatCount * sizeof(float);
            if (byteCount > _vertexBufferCapacityBytes)
            {
                gl.BufferData(GL_ARRAY_BUFFER, byteCount, (nint)ptr, GL_DYNAMIC_DRAW);
                _vertexBufferCapacityBytes = byteCount;
            }
            else if (_glBufferSubData != null)
            {
                _glBufferSubData(GL_ARRAY_BUFFER, 0, byteCount, (nint)ptr);
            }
            else
            {
                gl.BufferData(GL_ARRAY_BUFFER, byteCount, (nint)ptr, GL_DYNAMIC_DRAW);
            }
        }

        gl.DrawArrays(GL_TRIANGLES, 0, _vertexFloatCount / 5);

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

    private bool UpdatePhysics(int targetCount, float delta)
    {
        if (_displayedBarLevels.Length != targetCount)
        {
            _displayedBarLevels = new double[targetCount];
            _peakLevels = new double[targetCount];
            _rawSmoothed = new double[targetCount];
            _sampleMap = new int[targetCount];
            _sampleMapTargetCount = -1;
            _isFirstFrame = true;
        }

        if (_sampleMapTargetCount != targetCount || _sampleMapSourceCount != _spectrumCount)
        {
            if (_sampleMap.Length < targetCount)
                _sampleMap = new int[targetCount];

            int srcCount = _spectrumCount;
            for (int i = 0; i < targetCount; i++)
            {
                _sampleMap[i] = srcCount > 0
                    ? Math.Min(srcCount - 1, (int)((i / (double)targetCount) * srcCount))
                    : 0;
            }

            _sampleMapTargetCount = targetCount;
            _sampleMapSourceCount = srcCount;
        }

        // Use a fixed 1.0 factor if delta-time is disabled
        float timeFactor = UseDeltaTime ? delta * 60f : 1.0f; 

        double adjAttack = 1.0 - Math.Pow(1.0 - AttackLerp, timeFactor);
        double adjRelease = 1.0 - Math.Pow(1.0 - ReleaseLerp, timeFactor);
        double adjPeakDecay = 1.0 - Math.Pow(1.0 - PeakDecay, timeFactor);

        double observedMax = 0.0;
        bool hasVisibleActivity = false;
        for (int i = 0; i < targetCount; i++)
        {
            double src = _spectrumCount > 0 ? _spectrumSnapshot[_sampleMap[i]] : 0.0;

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
            if (_displayedBarLevels[i] > 0.001 || _peakLevels[i] > 0.001 || _rawSmoothed[i] > 0.001)
                hasVisibleActivity = true;
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
        return hasVisibleActivity || observedMax > 0.001 || _globalMax > 0.051;
    }

    private void PrepareVertices(float w, float h, int n)
    {
        if (n == 0) return;
        int requiredFloatCount = n * 60;
        if (_glVertices.Length < requiredFloatCount)
            _glVertices = new float[requiredFloatCount];
        _vertexFloatCount = requiredFloatCount;

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
        if (!_gradientDirty)
            return;

        var stops = BarGradient?.GradientStops;
        if (stops == null || stops.Count == 0)
        {
            for (int i = 0; i < 5; i++)
                SetUniform3F(_uColorLocs[i], 0.0f, 0.8f, 1.0f);

            _gradientDirty = false;
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            var c = GetColorAtOffset(stops, i / 4.0f);
            int colorIndex = i * 3;
            _gradientColors[colorIndex] = c.R / 255f;
            _gradientColors[colorIndex + 1] = c.G / 255f;
            _gradientColors[colorIndex + 2] = c.B / 255f;
            SetUniform3F(_uColorLocs[i], _gradientColors[colorIndex], _gradientColors[colorIndex + 1], _gradientColors[colorIndex + 2]);
        }

        _gradientDirty = false;
    }

    private static Color GetColorAtOffset(AvaloniaList<GradientStop> stops, float offset)
    {
        GradientStop left = stops[0];
        GradientStop right = stops[0];
        double leftOffset = double.NegativeInfinity;
        double rightOffset = double.PositiveInfinity;

        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            double stopOffset = stop.Offset;

            if (stopOffset <= offset && stopOffset >= leftOffset)
            {
                left = stop;
                leftOffset = stopOffset;
            }

            if (stopOffset >= offset && stopOffset <= rightOffset)
            {
                right = stop;
                rightOffset = stopOffset;
            }
        }

        if (double.IsNegativeInfinity(leftOffset))
            left = right;
        if (double.IsPositiveInfinity(rightOffset))
            right = left;

        if (leftOffset == rightOffset) return left.Color;
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

    private unsafe int GetUniformLocation(GlInterface gl, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* ptr = nameBytes)
            return gl.GetUniformLocation(_program, (nint)ptr);
    }

    private void SetUniform1F(int location, float value)
    {
        if (location != -1 && _glUniform1F != null)
            _glUniform1F(location, value);
    }

    private void SetUniform3F(int location, float r, float g, float b)
    {
        if (location != -1 && _glUniform3F != null)
            _glUniform3F(location, r, g, b);
    }

    private void ResetAdaptiveFramePacing()
    {
        _targetFrameIntervalMs = MinAdaptiveFrameIntervalMs;
        _averageRenderDurationMs = MinAdaptiveFrameIntervalMs * 0.5;
        _averageFrameDurationMs = MinAdaptiveFrameIntervalMs;
        _overBudgetFrames = 0;
        _underBudgetFrames = 0;
        ApplyRenderTimerInterval();
    }

    private void UpdateAdaptiveFramePacing(float delta, double renderDurationMs)
    {
        double frameDurationMs = Math.Clamp(delta * 1000.0, MinAdaptiveFrameIntervalMs, MaxAdaptiveFrameIntervalMs * 2.0);
        _averageFrameDurationMs += (frameDurationMs - _averageFrameDurationMs) * 0.12;
        _averageRenderDurationMs += (renderDurationMs - _averageRenderDurationMs) * 0.18;

        double budgetMs = _targetFrameIntervalMs;
        bool overBudget = _averageRenderDurationMs > budgetMs * 0.72 || renderDurationMs > budgetMs * 0.9;
        bool underBudget = _averageRenderDurationMs < budgetMs * 0.45 && _averageFrameDurationMs <= budgetMs * 1.15;

        if (overBudget)
        {
            _overBudgetFrames++;
            _underBudgetFrames = 0;
            if (_overBudgetFrames >= 3)
            {
                _targetFrameIntervalMs = Math.Min(MaxAdaptiveFrameIntervalMs, (_targetFrameIntervalMs * 1.15) + 0.5);
                _overBudgetFrames = 0;
                ApplyRenderTimerInterval();
            }
            return;
        }

        _overBudgetFrames = 0;

        if (underBudget)
        {
            _underBudgetFrames++;
            if (_underBudgetFrames >= 18)
            {
                _targetFrameIntervalMs = Math.Max(MinAdaptiveFrameIntervalMs, (_targetFrameIntervalMs * 0.9) - 0.25);
                _underBudgetFrames = 0;
                ApplyRenderTimerInterval();
            }
        }
        else if (_underBudgetFrames > 0)
        {
            _underBudgetFrames--;
        }
    }

    private void ApplyRenderTimerInterval()
    {
        if (_renderTimer == null)
            return;

        if (Math.Abs(_renderTimer.Interval.TotalMilliseconds - _targetFrameIntervalMs) > AdaptiveIntervalToleranceMs)
            _renderTimer.Interval = TimeSpan.FromMilliseconds(_targetFrameIntervalMs);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderTimer?.Stop();
        try { if (_program != 0) gl.DeleteProgram(_program); } catch { }
        try { if (_vertexBuffer != 0) gl.DeleteBuffer(_vertexBuffer); } catch { }
        try { if (_vao != 0) gl.DeleteVertexArray(_vao); } catch { }

        _program = 0;
        _vertexBuffer = 0;
        _vao = 0;
        _vertexBufferCapacityBytes = 0;
        _uBlockHeightLoc = -1;
        _uTotalHeightLoc = -1;
        _gradientDirty = true;
        _vsyncDisabled = false;
        ResetAdaptiveFramePacing();
        for (int i = 0; i < _uColorLocs.Length; i++)
            _uColorLocs[i] = -1;

        // Do not clear the property bindings or collection handlers here.
        // GlSpectrumControl may be reattached to the visual tree later and OpenGlInit called again.
    }

    public void Dispose() { }
}
