using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AES_Controls.GL;

/// <summary>
/// A control that hosts a ShaderToy-style GLSL fragment shader and exposes
/// properties for feeding audio spectrum data, cover color and playback controls.
/// The control manages an OpenGL program, a texture for audio data and handles
/// rendering and shader caching for improved startup performance.
/// </summary>
public class GlShaderToyControl : OpenGlControlBase
{
    private string _processedShaderCode = "";
    private int _program, _vbo, _vao, _audioTexture;
    private readonly Stopwatch _st = Stopwatch.StartNew();
    private bool _isDirty = true;
    private float _fadeAlpha;
    private bool _isInErrorState;
    private bool _actuallyAllowedToRender;

    // Buffer for the OpenGL texture (512 bins)
    private readonly float[] _gpuBuffer = new float[512]; 
    // Local snapshot to prevent locking the main Spectrum list for too long
    private readonly double[] _snapshot = new double[512]; 
    
    private DispatcherTimer? _uiHeartbeat; 

    // Track subscriptions to dispose on deinit
    private readonly List<IDisposable> _propertySubscriptions = new();

    private static string CachePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "ShaderCache");
    private const int GL_PROGRAM_BINARY_LENGTH = 0x8741;
    private const int GL_LINK_STATUS = 0x8B82;

    #region Styled Properties
    public static readonly StyledProperty<bool> IsRenderingPausedProperty =
        AvaloniaProperty.Register<GlShaderToyControl, bool>(nameof(IsRenderingPaused));
    /// <summary>
    /// Gets or sets a value indicating whether rendering is paused for this control.
    /// When true the control will not request or perform frame rendering until resumed.
    /// </summary>
    public bool IsRenderingPaused
    {
        get => GetValue(IsRenderingPausedProperty);
        set => SetValue(IsRenderingPausedProperty, value);
    }

    public static readonly StyledProperty<string> ShaderSourceProperty =
        AvaloniaProperty.Register<GlShaderToyControl, string>(nameof(ShaderSource), "");

    /// <summary>
    /// Gets or sets the shader source. This may be a raw GLSL fragment shader,
    /// a file path, or an Avalonia resource URI (starting with "avares://").
    /// </summary>
    public string ShaderSource
    {
        get => GetValue(ShaderSourceProperty);
        set => SetValue(ShaderSourceProperty, value);
    }

    public static readonly StyledProperty<object?> CoverProperty =
        AvaloniaProperty.Register<GlShaderToyControl, object?>(nameof(Cover));
    /// <summary>
    /// Gets or sets an optional cover object (color or brush) used as a base
    /// color for the shader's primary/secondary uniforms.
    /// </summary>
    public object? Cover
    {
        get => GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    public static readonly StyledProperty<double> FadeSpeedProperty =
        AvaloniaProperty.Register<GlShaderToyControl, double>(nameof(FadeSpeed), 0.008);
    /// <summary>
    /// Gets or sets the fade-in speed for the shader output. Larger values
    /// increase the fade alpha more quickly when a new shader is loaded.
    /// </summary>
    public double FadeSpeed
    {
        get => GetValue(FadeSpeedProperty);
        set => SetValue(FadeSpeedProperty, value);
    }

    public static readonly StyledProperty<AvaloniaList<double>?> SpectrumProperty =
        AvaloniaProperty.Register<GlShaderToyControl, AvaloniaList<double>?>(nameof(Spectrum));
    /// <summary>
    /// Gets or sets the audio spectrum data used to populate the internal
    /// texture (512 bins) that can be sampled by the shader via iChannel0.
    /// </summary>
    public AvaloniaList<double>? Spectrum
    {
        get => GetValue(SpectrumProperty);
        set => SetValue(SpectrumProperty, value);
    }
    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="GlShaderToyControl"/> class.
    /// Subscribes to property changes and starts an internal UI heartbeat timer
    /// that drives the rendering loop when the control is visible and not paused.
    /// </summary>
    public GlShaderToyControl()
    {
        _propertySubscriptions.Add(this.GetObservable(ShaderSourceProperty).Subscribe(new SimpleObserver<string>(value =>
        {
            _processedShaderCode = ProcessShaderSource(value);
            _isDirty = true;
            _fadeAlpha = 0f;
        }))) ;

        _propertySubscriptions.Add(this.GetObservable(IsRenderingPausedProperty).Subscribe(new SimpleObserver<bool>(paused =>
        {
            if (paused) { _actuallyAllowedToRender = false; _fadeAlpha = 0f; }
            else { _ = ResumeRenderingWithDelay(); }
        })));
        
        // HEARTBEAT: This drives the animation at a steady pace without flooding 
        // the UI thread or blocking other controls.
        _uiHeartbeat = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), 
            DispatcherPriority.Render, 
            (_, _) => { 
                if (!IsRenderingPaused && _actuallyAllowedToRender && IsVisible) 
                    RequestNextFrameRendering(); 
            });
        
        _uiHeartbeat.Start();
    }

    /// <summary>
    /// Called when an Avalonia property changes. The control uses this to
    /// trigger rendering when it becomes visible.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            RequestNextFrameRendering();
    }

    private async Task ResumeRenderingWithDelay()
    {
        await Task.Delay(100);
        await Dispatcher.UIThread.InvokeAsync(() => {
            _actuallyAllowedToRender = true;
            _isDirty = true;
        }, DispatcherPriority.Background);
    }

    private string ProcessShaderSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        // If an absolute path was provided, load it directly
        if (Path.IsPathRooted(source) && File.Exists(source)) return File.ReadAllText(source);

        // If a relative path was provided, attempt to resolve it relative to the
        // application's base directory (where the exe is located) and the current
        // working directory before falling back to treating the value as raw shader
        // source code.
        var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, source);
        if (File.Exists(candidate)) return File.ReadAllText(candidate);
        candidate = Path.Combine(Directory.GetCurrentDirectory(), source);
        if (File.Exists(candidate)) return File.ReadAllText(candidate);
        if (source.StartsWith("avares://"))
        {
            try {
                using var stream = AssetLoader.Open(new Uri(source));
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            } catch { return ""; }
        }

        return source;
    }

    /// <summary>
    /// Called when the OpenGL context is initialized for this control. Allocates
    /// vertex buffers and the audio texture used by the shader.
    /// </summary>
    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        float[] vertices = [-1.0f, 1.0f, -1.0f, -1.0f, 1.0f, 1.0f, 1.0f, -1.0f];
        gl.BindBuffer(0x8892, _vbo);
        fixed (float* p = vertices)
            gl.BufferData(0x8892, vertices.Length * 4, (IntPtr)p, 0x88E4);

        _audioTexture = gl.GenTexture();
        gl.BindTexture(0x0DE1, _audioTexture);
        gl.TexParameteri(0x0DE1, 0x2801, 0x2601); 
        gl.TexParameteri(0x0DE1, 0x2800, 0x2601); 
        gl.TexParameteri(0x0DE1, 0x2802, 0x812F);
        gl.TexParameteri(0x0DE1, 0x2803, 0x812F);
        gl.TexImage2D(0x0DE1, 0, 0x1903, 512, 1, 0, 0x1903, 0x1406, nint.Zero);
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (IsRenderingPaused || !_actuallyAllowedToRender || _isInErrorState || !IsVisible) return;

        try
        {
            if (Bounds.Width < 1 || Bounds.Height < 1) return;
            if (_isDirty) { UpdateProgram(gl); _isDirty = false; }
            if (_program == 0) return;

            UpdateAudioTexture(gl);
            _fadeAlpha = Math.Min(1.0f, _fadeAlpha + (float)FadeSpeed);

            float scaling = (float)(VisualRoot?.RenderScaling ?? 1.0);
            gl.Viewport(0, 0, (int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
            gl.UseProgram(_program);

            SetUniforms(gl, (int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling), scaling);

            gl.ActiveTexture(0x84C0);
            gl.BindTexture(0x0DE1, _audioTexture);
            SetUniform1I(gl, _program, "iChannel0", 0);

            gl.BindVertexArray(_vao);
            gl.BindBuffer(0x8892, _vbo);
            gl.VertexAttribPointer(0, 2, 0x1406, 0, 8, IntPtr.Zero);
            gl.EnableVertexAttribArray(0);
            gl.DrawArrays(0x0005, 0, 4);
        }
        catch { _isInErrorState = true; }
    }

    private unsafe void UpdateAudioTexture(GlInterface gl)
    {
        var src = Spectrum;
        if (src == null || src.Count == 0) return;

        int srcCount = 0;
        lock (src)
        {
            srcCount = Math.Min(src.Count, _snapshot.Length);
            for (int i = 0; i < srcCount; i++) _snapshot[i] = src[i];
        }

        float maxV = 0.0001f;
        for (int i = 0; i < srcCount; i++) if (_snapshot[i] > maxV) maxV = (float)_snapshot[i];

        // FIX: Stretch the data to fill the 512-wide texture
        // This prevents the "half-dead" spectrum issue
        float step = (float)srcCount / _gpuBuffer.Length;

        for (int i = 0; i < _gpuBuffer.Length; i++)
        {
            // Sample from the snapshot based on the stretch ratio
            int snapshotIdx = (int)(i * step);
            float target = (float)(_snapshot[snapshotIdx] / maxV);
        
            // Smooth the transition
            _gpuBuffer[i] += (target - _gpuBuffer[i]) * 0.15f;
        }

        gl.BindTexture(0x0DE1, _audioTexture);
        fixed (float* p = _gpuBuffer)
        {
            var func = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, void*, void>)
                gl.GetProcAddress("glTexSubImage2D");
            if (func != null) func(0x0DE1, 0, 0, 0, 512, 1, 0x1903, 0x1406, p);
        }
    }

    private void UpdateProgram(GlInterface gl)
    {
        if (string.IsNullOrEmpty(_processedShaderCode)) return;
        string hash = GetStringHash(_processedShaderCode);
        string cacheFile = Path.Combine(CachePath, $"{hash}.bin");
        
        if (File.Exists(cacheFile))
        {
            int loadedPrg = LoadProgramBinary(gl, cacheFile);
            if (loadedPrg != 0) {
                if (_program != 0) gl.DeleteProgram(_program);
                _program = loadedPrg;
                return;
            }
        }

        var shaderInfo = GlHelper.GetShaderVersion(gl);
        string vs = $@"{shaderInfo.Item1}
            in vec2 a_pos; void main() {{ gl_Position = vec4(a_pos, 0.0, 1.0); }}";
        string fs = $@"{shaderInfo.Item1}
            precision highp float;
            uniform vec3 iResolution; uniform float iTime; uniform vec4 iMouse;
            uniform float u_fade; uniform sampler2D iChannel0;
            uniform vec3 u_primary; uniform vec3 u_secondary;
            out vec4 outFragColor;
            {_processedShaderCode}
            void main() {{ vec4 c; mainImage(c, gl_FragCoord.xy); outFragColor = vec4(c.rgb * u_fade, 1.0); }}";

        if (_program != 0) gl.DeleteProgram(_program);
        _program = CreateProgram(gl, vs, fs);
        if (_program != 0) SaveProgramBinary(gl, _program, cacheFile);
    }

    private unsafe int LoadProgramBinary(GlInterface gl, string file)
    {
        try {
            using var reader = new BinaryReader(File.Open(file, FileMode.Open));
            int format = reader.ReadInt32();
            byte[] data = reader.ReadBytes((int)(reader.BaseStream.Length - 4));
            int prg = gl.CreateProgram();
            fixed (byte* pData = data) {
                var func = (delegate* unmanaged[Stdcall]<int, int, void*, int, void>)gl.GetProcAddress("glProgramBinary");
                if (func != null) func(prg, format, pData, data.Length);
            }
            int success = 0; gl.GetProgramiv(prg, GL_LINK_STATUS, &success);
            if (success != 0) return prg;
            gl.DeleteProgram(prg); reader.Close(); File.Delete(file);
        } catch { }
        return 0;
    }

    private unsafe void SaveProgramBinary(GlInterface gl, int prg, string file)
    {
        int length = 0; gl.GetProgramiv(prg, GL_PROGRAM_BINARY_LENGTH, &length);
        if (length <= 0) return;
        byte[] buffer = new byte[length];
        int retLen = 0, format = 0;
        fixed (byte* pBuf = buffer) {
            var func = (delegate* unmanaged[Stdcall]<int, int, int*, int*, void*, void>)gl.GetProcAddress("glGetProgramBinary");
            if (func != null) func(prg, length, &retLen, &format, pBuf);
        }
        if (!Directory.Exists(CachePath)) Directory.CreateDirectory(CachePath);
        using var writer = new BinaryWriter(File.Open(file, FileMode.Create));
        writer.Write(format); writer.Write(buffer);
    }

    private string GetStringHash(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "")[..16];
    }

    private void SetUniforms(GlInterface gl, int width, int height, float scaling)
    {
        SetUniform3F(gl, _program, "iResolution", width, height, 1.0f);
        SetUniform1F(gl, _program, "iTime", (float)_st.Elapsed.TotalSeconds);

        float r = 0.5f, g = 0.2f, b = 0.8f;
        if (Cover is Color col) { r = col.R / 255f; g = col.G / 255f; b = col.B / 255f; }
        else if (Cover is ISolidColorBrush scb) { r = scb.Color.R / 255f; g = scb.Color.G / 255f; b = scb.Color.B / 255f; }

        SetUniform3F(gl, _program, "u_primary", r, g, b);
        SetUniform3F(gl, _program, "u_secondary", 1.0f - r, 1.0f - g, 1.0f - b);
        SetUniform1F(gl, _program, "u_fade", _fadeAlpha);
    }

    private int CreateProgram(GlInterface gl, string v, string f)
    {
        int p = gl.CreateProgram(), vs = gl.CreateShader(0x8B31), fs = gl.CreateShader(0x8B30);
        if (!CompileShader(gl, vs, v) || !CompileShader(gl, fs, f)) return 0;
        gl.AttachShader(p, vs); gl.AttachShader(p, fs); gl.LinkProgram(p);
        gl.DeleteShader(vs); gl.DeleteShader(fs); return p;
    }

    private unsafe bool CompileShader(GlInterface gl, int s, string src)
    {
        var b = Encoding.UTF8.GetBytes(src); int len = b.Length;
        fixed (byte* p = b) { sbyte* ps = (sbyte*)p; sbyte** pps = &ps; gl.ShaderSource(s, 1, (IntPtr)pps, (IntPtr)(&len)); }
        gl.CompileShader(s);
        int success = 0; gl.GetShaderiv(s, 0x8B81, &success);
        return success != 0;
    }

    #region Uniform Helpers
    private unsafe void SetUniform1I(GlInterface gl, int prg, string name, int val)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name); int loc = gl.GetUniformLocation(prg, ptr); Marshal.FreeHGlobal(ptr);
        if (loc != -1) { var f = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glUniform1i"); if (f != null) f(loc, val); }
    }
    private unsafe void SetUniform1F(GlInterface gl, int prg, string name, float val)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name); int loc = gl.GetUniformLocation(prg, ptr); Marshal.FreeHGlobal(ptr);
        if (loc != -1) { var f = (delegate* unmanaged[Stdcall]<int, float, void>)gl.GetProcAddress("glUniform1f"); if (f != null) f(loc, val); }
    }
    private unsafe void SetUniform3F(GlInterface gl, int prg, string name, float x, float y, float z)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name); int loc = gl.GetUniformLocation(prg, ptr); Marshal.FreeHGlobal(ptr);
        if (loc != -1) { var f = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)gl.GetProcAddress("glUniform3f"); if (f != null) f(loc, x, y, z); }
    }
    #endregion

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _uiHeartbeat?.Stop();
        if (_program != 0) gl.DeleteProgram(_program);
        if (_audioTexture != 0) gl.DeleteTexture(_audioTexture);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);

        // Dispose property subscriptions to release captured closures
        try
        {
            foreach (var d in _propertySubscriptions) d.Dispose();
        }
        catch { }
        _propertySubscriptions.Clear();
    }
}