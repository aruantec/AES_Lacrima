using AES_Controls.Helpers;
using AES_Core.IO;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Diagnostics;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using log4net;

namespace AES_Controls.GL;

/// <summary>
/// A control that hosts a ShaderToy-style GLSL fragment shader and exposes
/// properties for feeding audio spectrum data, cover color and playback controls.
/// The control manages an OpenGL program, a texture for audio data and handles
/// rendering and shader caching for improved startup performance.
/// </summary>
public class GlShaderToyControl : OpenGlControlBase
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(GlShaderToyControl));
    private string _processedShaderCode = string.Empty;
    private int _program, _vbo, _vao, _audioTexture, _coverTexture;
    private bool _coverTextureDirty = true;
    private PixelSize _coverTextureSize;
    private readonly Stopwatch _st = Stopwatch.StartNew();
    private bool _isDirty = true;
    private float _fadeAlpha;
    private bool _isInErrorState;
    private bool _actuallyAllowedToRender;
    private bool _isEs;

    // Buffer for the OpenGL texture (512 bins)
    private readonly float[] _gpuBuffer = new float[512];

    // Local snapshot to prevent locking the main Spectrum list for too long
    private readonly double[] _snapshot = new double[512];

    private DispatcherTimer? _uiHeartbeat;

    // Track subscriptions to dispose on deinit
    private readonly List<IDisposable> _propertySubscriptions = new();
    private readonly BitmapColorHelper _bitmapColorHelper = new();
    private Color? _coverPrimaryColor;
    private Color? _coverSecondaryColor;
    private float _primaryR = 0.5f, _primaryG = 0.2f, _primaryB = 0.8f;
    private float _secondaryR = 0.5f, _secondaryG = 0.8f, _secondaryB = 0.2f;
    private bool _colorSmoothingInitialized;
    private double _lastColorUpdateTime;

    private static string CachePath => Path.Combine(ApplicationPaths.CacheDirectory, "ShaderCache");
    private static string LogPath => ApplicationPaths.LogsDirectory;
    private const int GlProgramBinaryLength = 0x8741;
    private const int GlLinkStatus = 0x8B82;

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
        AvaloniaProperty.Register<GlShaderToyControl, string>(nameof(ShaderSource), string.Empty);

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

    public static readonly StyledProperty<Bitmap?> CoverBitmapProperty =
        AvaloniaProperty.Register<GlShaderToyControl, Bitmap?>(nameof(CoverBitmap));

    /// <summary>
    /// Gets or sets the cover bitmap uploaded to the shader as iChannel1.
    /// </summary>
    public Bitmap? CoverBitmap
    {
        get => GetValue(CoverBitmapProperty);
        set => SetValue(CoverBitmapProperty, value);
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

    public static readonly StyledProperty<LinearGradientBrush?> SpectrumGradientProperty =
        AvaloniaProperty.Register<GlShaderToyControl, LinearGradientBrush?>(nameof(SpectrumGradient));

    /// <summary>
    /// Gets or sets the gradient brush used by shaders that sample spectrum colors.
    /// </summary>
    public LinearGradientBrush? SpectrumGradient
    {
        get => GetValue(SpectrumGradientProperty);
        set => SetValue(SpectrumGradientProperty, value);
    }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="GlShaderToyControl"/> class.
    /// Subscribes to property changes and starts an internal UI heartbeat timer
    /// that drives the rendering loop when the control is visible and not paused.
    /// </summary>
    public GlShaderToyControl()
    {
        _propertySubscriptions.Add(this.GetObservable(ShaderSourceProperty).Subscribe(
            new SimpleObserver<string>(value =>
            {
                _processedShaderCode = ProcessShaderSource(value);
                _isDirty = true;
                _fadeAlpha = 0f;
            })));

        _propertySubscriptions.Add(this.GetObservable(IsRenderingPausedProperty).Subscribe(
            new SimpleObserver<bool>(paused =>
            {
                if (paused)
                {
                    _actuallyAllowedToRender = false;
                    _fadeAlpha = 0f;
                }
                else
                {
                    _ = ResumeRenderingWithDelay();
                }
            })));

        _propertySubscriptions.Add(this.GetObservable(CoverBitmapProperty).Subscribe(
            new SimpleObserver<Bitmap?>(bitmap =>
            {
                _coverTextureDirty = true;
                UpdateCoverPalette(bitmap);
                RequestNextFrameRendering();
            })));

        // HEARTBEAT: This drives the animation at a steady pace without flooding 
        // the UI thread or blocking other controls.
        _uiHeartbeat = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) =>
            {
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _actuallyAllowedToRender = true;
            _isDirty = true;
        }, DispatcherPriority.Background);
    }

    private string ProcessShaderSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // If the source is a path/avares URI, load its contents first
        string content = source;
        if (Path.IsPathRooted(source) && File.Exists(source))
            content = File.ReadAllText(source);
        else
        {
            var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, source);
            if (File.Exists(candidate)) content = File.ReadAllText(candidate);
            else
            {
                candidate = Path.Combine(Directory.GetCurrentDirectory(), source);
                if (File.Exists(candidate)) content = File.ReadAllText(candidate);
                else if (source.StartsWith("avares://"))
                {
                    try
                    {
                        using var stream = AssetLoader.Open(new Uri(source));
                        using var reader = new StreamReader(stream);
                        content = reader.ReadToEnd();
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }
            }
        }

        // Sanitize shader snippet so it can be injected into the host wrapper.
        // Remove duplicate or conflicting declarations that the wrapper already
        // provides: #version, precision qualifiers, common uniform declarations
        // and any `main()` implementation. Keep functions like `mainImage`.
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var keep = new List<string>();
        var forbiddenUniformNames = new[] { "iResolution", "iTime", "iMouse", "u_fade", "iChannel0", "iChannel1", "iChannel1Size", "u_primary", "u_secondary", "u_grad0", "u_grad1", "u_grad2", "u_grad3", "u_grad4" };

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) { keep.Add(raw); continue; }

            // Skip version/precision/out/main declarations and comments-only lines
            if (line.StartsWith("#version") || line.StartsWith("precision") || line.StartsWith("out ") )
                continue;

            // Skip any 'void main' definitions - host provides main that calls mainImage
            if (line.StartsWith("void main(" ) || line.StartsWith("void main ("))
            {
                // drop until matching closing brace - simplest approach: stop adding further lines
                break;
            }

            // Remove uniforms that would conflict with wrapper
            if (line.StartsWith("uniform "))
            {
                bool conflict = false;
                foreach (var name in forbiddenUniformNames)
                {
                    if (line.Contains(name)) { conflict = true; break; }
                }
                if (conflict) continue;
            }

            keep.Add(raw);
        }

        return string.Join("\n", keep);
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

        _coverTexture = gl.GenTexture();
        gl.BindTexture(0x0DE1, _coverTexture);
        gl.TexParameteri(0x0DE1, 0x2801, 0x2601);
        gl.TexParameteri(0x0DE1, 0x2800, 0x2601);
        gl.TexParameteri(0x0DE1, 0x2802, 0x812F);
        gl.TexParameteri(0x0DE1, 0x2803, 0x812F);
        _coverTextureSize = new PixelSize(1, 1);
        byte* blackPixel = stackalloc byte[4];
        blackPixel[0] = 0;
        blackPixel[1] = 0;
        blackPixel[2] = 0;
        blackPixel[3] = 255;
        gl.TexImage2D(0x0DE1, 0, 0x1908, 1, 1, 0, 0x1908, 0x1401, (IntPtr)blackPixel);
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (IsRenderingPaused || !_actuallyAllowedToRender || _isInErrorState || !IsVisible) return;

        try
        {
            if (Bounds.Width < 1 || Bounds.Height < 1) return;
            if (_isDirty)
            {
                UpdateProgram(gl);
                _isDirty = false;
            }

            if (_program == 0) return;

            UpdateAudioTexture(gl);
            UpdateCoverTexture(gl);
            _fadeAlpha = Math.Min(1.0f, _fadeAlpha + (float)FadeSpeed);

            float scaling = (float)(VisualRoot?.RenderScaling ?? 1.0);
            gl.Viewport(0, 0, (int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
            gl.ClearColor(0f, 0f, 0f, 0f);
            gl.Clear(0x00004000);

            gl.Enable(0x0BE2); // GL_BLEND
            var glBlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
            if (glBlendFunc != null) glBlendFunc(0x0302, 0x0303); // GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA

            gl.UseProgram(_program);

            SetUniforms(gl, (int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));

            gl.ActiveTexture(0x84C0);
            gl.BindTexture(0x0DE1, _audioTexture);
            SetUniform1I(gl, _program, "iChannel0", 0);

            gl.ActiveTexture(0x84C1);
            gl.BindTexture(0x0DE1, _coverTexture);
            SetUniform1I(gl, _program, "iChannel1", 1);
            SetUniform2F(gl, _program, "iChannel1Size", _coverTextureSize.Width, _coverTextureSize.Height);

            gl.BindVertexArray(_vao);
            gl.BindBuffer(0x8892, _vbo);
            gl.VertexAttribPointer(0, 2, 0x1406, 0, 8, IntPtr.Zero);
            gl.EnableVertexAttribArray(0);
            gl.DrawArrays(0x0005, 0, 4);
        }
        catch
        {
            _isInErrorState = true;
        }
    }

    private unsafe void UpdateAudioTexture(GlInterface gl)
    {
        var src = Spectrum;
        if (src == null || src.Count == 0) return;

        int srcCount;
        lock (src)
        {
            srcCount = Math.Min(src.Count, _snapshot.Length);
            for (int i = 0; i < srcCount; i++) _snapshot[i] = src[i];
        }

        float maxV = 0.0001f;
        for (int i = 0; i < srcCount; i++)
            lock (src)
            {
                if (_snapshot[i] > maxV) maxV = (float)_snapshot[i];
            }

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

    private unsafe void UpdateCoverTexture(GlInterface gl)
    {
        if (!_coverTextureDirty) return;
        _coverTextureDirty = false;

        var bitmap = CoverBitmap;
        gl.BindTexture(0x0DE1, _coverTexture);

        if (bitmap == null || bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
        {
            _coverTextureSize = new PixelSize(1, 1);
            byte* blackPixel = stackalloc byte[4];
            blackPixel[0] = 0;
            blackPixel[1] = 0;
            blackPixel[2] = 0;
            blackPixel[3] = 255;
            gl.TexImage2D(0x0DE1, 0, 0x1908, 1, 1, 0, 0x1908, 0x1401, (IntPtr)blackPixel);
            return;
        }

        var size = bitmap.PixelSize;
        int stride = size.Width * 4;
        int pxLen = size.Height * stride;
        var pool = ArrayPool<byte>.Shared;
        byte[]? px = null;

        try
        {
            px = pool.Rent(pxLen);
            fixed (byte* p = px)
            {
                bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pxLen, stride);

                // Avalonia pixels are top-left origin while OpenGL textures are bottom-left origin.
                // Flip rows so the sampled texture orientation matches the source bitmap.
                for (int y = 0; y < size.Height / 2; y++)
                {
                    int top = y * stride;
                    int bottom = (size.Height - 1 - y) * stride;
                    for (int x = 0; x < stride; x++)
                    {
                        (px[top + x], px[bottom + x]) = (px[bottom + x], px[top + x]);
                    }
                }

                if (_isEs)
                {
                    for (int i = 0; i < pxLen; i += 4)
                    {
                        byte b = px[i + 0], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                        px[i + 0] = r;
                        px[i + 1] = g;
                        px[i + 2] = b;
                        px[i + 3] = a;
                    }

                    gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x1908, 0x1401, (IntPtr)p);
                }
                else
                {
                    // Avalonia bitmaps expose BGRA. Upload directly for desktop GL.
                    gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x80E1, 0x1401, (IntPtr)p);
                }
            }

            gl.TexParameteri(0x0DE1, 0x2801, 0x2601);
            gl.TexParameteri(0x0DE1, 0x2800, 0x2601);
            _coverTextureSize = size;
        }
        catch
        {
            _coverTextureSize = new PixelSize(1, 1);
        }
        finally
        {
            if (px != null) pool.Return(px, clearArray: false);
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
            if (loadedPrg != 0)
            {
                if (_program != 0) gl.DeleteProgram(_program);
                _program = loadedPrg;
                return;
            }
        }

        var shaderInfo = GlHelper.GetShaderVersion(gl);
        _isEs = shaderInfo.Item2;
        string vs = $@"{shaderInfo.Item1}
            in vec2 a_pos; void main() {{ gl_Position = vec4(a_pos, 0.0, 1.0); }}";
        string fs = $@"{shaderInfo.Item1}
            precision highp float;
            uniform vec3 iResolution; uniform float iTime; uniform vec4 iMouse;
            uniform float u_fade; uniform sampler2D iChannel0;
            uniform sampler2D iChannel1; uniform vec2 iChannel1Size;
            uniform vec3 u_primary; uniform vec3 u_secondary;
            out vec4 outFragColor;
            {_processedShaderCode}
            void main() {{ vec4 c; mainImage(c, gl_FragCoord.xy); outFragColor = c; }}";

        // Persist the final shader sources for debugging (useful when a
        // shader compiles on some drivers but not others).
        try
        {
            if (!Directory.Exists(LogPath)) Directory.CreateDirectory(LogPath);
            File.WriteAllText(Path.Combine(LogPath, hash + ".vs.glsl"), vs);
            File.WriteAllText(Path.Combine(LogPath, hash + ".fs.glsl"), fs);
        }
        catch
        {
            // ignore IO failures
        }

        if (_program != 0) gl.DeleteProgram(_program);
        _program = CreateProgram(gl, vs, fs);

        // If creation failed, mark error state and leave useful artifacts
        if (_program == 0)
        {
            _isInErrorState = true;
            try
            {
                if (!Directory.Exists(LogPath)) Directory.CreateDirectory(LogPath);
                File.WriteAllText(Path.Combine(LogPath, hash + ".failed.txt"), "Program creation failed for shader.\n" + fs);
            }
            catch { }
        }
        else
        {
            SaveProgramBinary(gl, _program, cacheFile);
        }
    }

    private unsafe int LoadProgramBinary(GlInterface gl, string file)
    {
        try
        {
            using var reader = new BinaryReader(File.Open(file, FileMode.Open));
            int format = reader.ReadInt32();
            byte[] data = reader.ReadBytes((int)(reader.BaseStream.Length - 4));
            int prg = gl.CreateProgram();
            fixed (byte* pData = data)
            {
                var func =
                    (delegate* unmanaged[Stdcall]<int, int, void*, int, void>)gl.GetProcAddress("glProgramBinary");
                if (func != null) func(prg, format, pData, data.Length);
            }

            int success = 0;
            gl.GetProgramiv(prg, GlLinkStatus, &success);
            if (success != 0) return prg;
            gl.DeleteProgram(prg);
            reader.Close();
            File.Delete(file);
        }
        catch
        {
            // ignored
        }

        return 0;
    }

    private unsafe void SaveProgramBinary(GlInterface gl, int prg, string file)
    {
        int length = 0;
        gl.GetProgramiv(prg, GlProgramBinaryLength, &length);
        if (length <= 0) return;
        byte[] buffer = new byte[length];
        int retLen = 0, format = 0;
        fixed (byte* pBuf = buffer)
        {
            var func =
                (delegate* unmanaged[Stdcall]<int, int, int*, int*, void*, void>)gl.GetProcAddress(
                    "glGetProgramBinary");
            if (func != null) func(prg, length, &retLen, &format, pBuf);
        }

        if (!Directory.Exists(CachePath)) Directory.CreateDirectory(CachePath);
        using var writer = new BinaryWriter(File.Open(file, FileMode.Create));
        writer.Write(format);
        writer.Write(buffer);
    }

    private string GetStringHash(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty)[..16];
    }

    private void SetUniforms(GlInterface gl, int width, int height)
    {
        SetUniform3F(gl, _program, "iResolution", width, height, 1.0f);
        SetUniform1F(gl, _program, "iTime", (float)_st.Elapsed.TotalSeconds);

        float targetPrimaryR = 0.5f, targetPrimaryG = 0.2f, targetPrimaryB = 0.8f;
        float targetSecondaryR;
        float targetSecondaryG;
        float targetSecondaryB;

        if (_coverPrimaryColor is Color coverPrimary)
        {
            targetPrimaryR = coverPrimary.R / 255f;
            targetPrimaryG = coverPrimary.G / 255f;
            targetPrimaryB = coverPrimary.B / 255f;
        }
        else if (Cover is Color col)
        {
            targetPrimaryR = col.R / 255f;
            targetPrimaryG = col.G / 255f;
            targetPrimaryB = col.B / 255f;
        }
        else if (Cover is ISolidColorBrush scb)
        {
            targetPrimaryR = scb.Color.R / 255f;
            targetPrimaryG = scb.Color.G / 255f;
            targetPrimaryB = scb.Color.B / 255f;
        }

        if (_coverSecondaryColor is Color coverSecondary)
        {
            targetSecondaryR = coverSecondary.R / 255f;
            targetSecondaryG = coverSecondary.G / 255f;
            targetSecondaryB = coverSecondary.B / 255f;
        }
        else
        {
            targetSecondaryR = 1.0f - targetPrimaryR;
            targetSecondaryG = 1.0f - targetPrimaryG;
            targetSecondaryB = 1.0f - targetPrimaryB;
        }

        UpdateSmoothedCoverColors(targetPrimaryR, targetPrimaryG, targetPrimaryB, targetSecondaryR, targetSecondaryG,
            targetSecondaryB);

        SetUniform3F(gl, _program, "u_primary", _primaryR, _primaryG, _primaryB);
        SetUniform3F(gl, _program, "u_secondary", _secondaryR, _secondaryG, _secondaryB);
        SetUniform1F(gl, _program, "u_fade", _fadeAlpha);

        var fallback = Color.FromRgb((byte)(_primaryR * 255f), (byte)(_primaryG * 255f), (byte)(_primaryB * 255f));
        var gradientColors = GetGradientColors(SpectrumGradient, fallback);
        SetUniform3F(gl, _program, "u_grad0", gradientColors[0].R / 255f, gradientColors[0].G / 255f,
            gradientColors[0].B / 255f);
        SetUniform3F(gl, _program, "u_grad1", gradientColors[1].R / 255f, gradientColors[1].G / 255f,
            gradientColors[1].B / 255f);
        SetUniform3F(gl, _program, "u_grad2", gradientColors[2].R / 255f, gradientColors[2].G / 255f,
            gradientColors[2].B / 255f);
        SetUniform3F(gl, _program, "u_grad3", gradientColors[3].R / 255f, gradientColors[3].G / 255f,
            gradientColors[3].B / 255f);
        SetUniform3F(gl, _program, "u_grad4", gradientColors[4].R / 255f, gradientColors[4].G / 255f,
            gradientColors[4].B / 255f);
    }

    private void UpdateSmoothedCoverColors(
        float targetPrimaryR, float targetPrimaryG, float targetPrimaryB,
        float targetSecondaryR, float targetSecondaryG, float targetSecondaryB)
    {
        var now = _st.Elapsed.TotalSeconds;
        if (!_colorSmoothingInitialized)
        {
            _primaryR = targetPrimaryR;
            _primaryG = targetPrimaryG;
            _primaryB = targetPrimaryB;
            _secondaryR = targetSecondaryR;
            _secondaryG = targetSecondaryG;
            _secondaryB = targetSecondaryB;
            _lastColorUpdateTime = now;
            _colorSmoothingInitialized = true;
            return;
        }

        var dt = Math.Clamp(now - _lastColorUpdateTime, 0.0, 0.25);
        _lastColorUpdateTime = now;

        // Exponential smoothing to avoid abrupt color jumps when cover art changes.
        float lerp = 1f - MathF.Exp((float)(-dt * 3.6));

        _primaryR += (targetPrimaryR - _primaryR) * lerp;
        _primaryG += (targetPrimaryG - _primaryG) * lerp;
        _primaryB += (targetPrimaryB - _primaryB) * lerp;
        _secondaryR += (targetSecondaryR - _secondaryR) * lerp;
        _secondaryG += (targetSecondaryG - _secondaryG) * lerp;
        _secondaryB += (targetSecondaryB - _secondaryB) * lerp;
    }

    private void UpdateCoverPalette(Bitmap? bitmap)
    {
        _coverPrimaryColor = null;
        _coverSecondaryColor = null;

        if (bitmap == null) return;

        try
        {
            var dominant = BitmapColorHelper.GetDominantColor(bitmap);
            if (dominant.A != 0)
            {
                _coverPrimaryColor = dominant;
            }

            var gradient = _bitmapColorHelper.GetColorGradient(bitmap);
            var stops = gradient.GradientStops?.OrderBy(stop => stop.Offset).ToList();
            if (stops != null && stops.Count >= 2)
            {
                _coverSecondaryColor = stops[^1].Color;
            }
        }
        catch
        {
            // ignore color extraction errors; fallback path remains active
        }
    }

    private static Color[] GetGradientColors(LinearGradientBrush? brush, Color fallback)
    {
        var colors = new[] { fallback, fallback, fallback, fallback, fallback };
        if (brush?.GradientStops == null || brush.GradientStops.Count == 0)
            return colors;

        var stops = brush.GradientStops.OrderBy(stop => stop.Offset).ToList();
        if (stops.Count == 1)
        {
            for (int i = 0; i < colors.Length; i++)
                colors[i] = stops[0].Color;
            return colors;
        }

        for (int i = 0; i < colors.Length; i++)
        {
            double t = i / 4.0;
            var previous = stops[0];
            var next = stops[^1];
            for (int j = 1; j < stops.Count; j++)
            {
                if (stops[j].Offset >= t)
                {
                    next = stops[j];
                    previous = stops[j - 1];
                    break;
                }
            }

            double span = Math.Max(0.0001, next.Offset - previous.Offset);
            double localT = (t - previous.Offset) / span;
            colors[i] = LerpColor(previous.Color, next.Color, localT);
        }

        return colors;
    }

    private static Color LerpColor(Color start, Color end, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte r = (byte)Math.Clamp(start.R + (end.R - start.R) * t, 0.0, 255.0);
        byte g = (byte)Math.Clamp(start.G + (end.G - start.G) * t, 0.0, 255.0);
        byte b = (byte)Math.Clamp(start.B + (end.B - start.B) * t, 0.0, 255.0);
        return Color.FromRgb(r, g, b);
    }

    private int CreateProgram(GlInterface gl, string v, string f)
    {
        int p = gl.CreateProgram(), vs = gl.CreateShader(0x8B31), fs = gl.CreateShader(0x8B30);
        if (!CompileShader(gl, vs, v, "vertex") || !CompileShader(gl, fs, f, "fragment"))
        {
            if (vs != 0) gl.DeleteShader(vs);
            if (fs != 0) gl.DeleteShader(fs);
            if (p != 0) gl.DeleteProgram(p);
            return 0;
        }

        gl.AttachShader(p, vs);
        gl.AttachShader(p, fs);
        BindAttribLocation(gl, p, 0, "a_pos");
        gl.LinkProgram(p);

        int linked = 0;
        unsafe
        {
            gl.GetProgramiv(p, GlLinkStatus, &linked);
        }

        if (linked == 0)
        {
            var linkLog = GetProgramInfoLog(gl, p);
            if (!string.IsNullOrWhiteSpace(linkLog))
            {
                Log.Warn($"ShaderToy program link failed: {linkLog}");
                TryWriteGlErrorFile("shadertoy-link-error.log", linkLog);
            }

            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            gl.DeleteProgram(p);
            return 0;
        }

        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return p;
    }

    private unsafe bool CompileShader(GlInterface gl, int s, string src, string stage)
    {
        var b = Encoding.UTF8.GetBytes(src);
        int len = b.Length;
        fixed (byte* p = b)
        {
            sbyte* ps = (sbyte*)p;
            sbyte** pps = &ps;
            gl.ShaderSource(s, 1, (IntPtr)pps, (IntPtr)(&len));
        }

        gl.CompileShader(s);
        int success = 0;
        gl.GetShaderiv(s, 0x8B81, &success);
        if (success == 0)
        {
            var log = GetShaderInfoLog(gl, s);
            if (!string.IsNullOrWhiteSpace(log))
            {
                Log.Warn($"ShaderToy {stage} shader compilation failed: {log}");
                TryWriteGlErrorFile($"shadertoy-{stage}-compile-error.log", log + Environment.NewLine + Environment.NewLine + src);
            }
        }
        return success != 0;
    }

    private unsafe void BindAttribLocation(GlInterface gl, int program, uint index, string name)
    {
        var bind = (delegate* unmanaged[Stdcall]<uint, uint, sbyte*, void>)gl.GetProcAddress("glBindAttribLocation");
        if (bind == null) return;

        var bytes = Encoding.ASCII.GetBytes(name + '\0');
        fixed (byte* p = bytes)
        {
            bind((uint)program, index, (sbyte*)p);
        }
    }

    private unsafe string GetShaderInfoLog(GlInterface gl, int shader)
    {
        int length = 0;
        gl.GetShaderiv(shader, 0x8B84, &length); // GL_INFO_LOG_LENGTH
        if (length <= 1) return string.Empty;

        var data = new byte[length];
        fixed (byte* p = data)
        {
            int written = 0;
            var getLog = (delegate* unmanaged[Stdcall]<uint, int, int*, sbyte*, void>)gl.GetProcAddress("glGetShaderInfoLog");
            if (getLog == null) return string.Empty;
            getLog((uint)shader, length, &written, (sbyte*)p);
            return Encoding.UTF8.GetString(data, 0, Math.Max(0, written)).TrimEnd('\0', '\r', '\n', ' ');
        }
    }

    private unsafe string GetProgramInfoLog(GlInterface gl, int program)
    {
        int length = 0;
        gl.GetProgramiv(program, 0x8B84, &length); // GL_INFO_LOG_LENGTH
        if (length <= 1) return string.Empty;

        var data = new byte[length];
        fixed (byte* p = data)
        {
            int written = 0;
            var getLog = (delegate* unmanaged[Stdcall]<uint, int, int*, sbyte*, void>)gl.GetProcAddress("glGetProgramInfoLog");
            if (getLog == null) return string.Empty;
            getLog((uint)program, length, &written, (sbyte*)p);
            return Encoding.UTF8.GetString(data, 0, Math.Max(0, written)).TrimEnd('\0', '\r', '\n', ' ');
        }
    }

    private static void TryWriteGlErrorFile(string fileName, string text)
    {
        try
        {
            Directory.CreateDirectory(LogPath);
            File.WriteAllText(Path.Combine(LogPath, fileName), text);
        }
        catch
        {
            // ignore logging failures
        }
    }

    #region Uniform Helpers

    private unsafe void SetUniform1I(GlInterface gl, int prg, string name, int val)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(prg, ptr);
        Marshal.FreeHGlobal(ptr);
        if (loc != -1)
        {
            var f = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glUniform1i");
            if (f != null) f(loc, val);
        }
    }

    private unsafe void SetUniform1F(GlInterface gl, int prg, string name, float val)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(prg, ptr);
        Marshal.FreeHGlobal(ptr);
        if (loc != -1)
        {
            var f = (delegate* unmanaged[Stdcall]<int, float, void>)gl.GetProcAddress("glUniform1f");
            if (f != null) f(loc, val);
        }
    }

    private unsafe void SetUniform3F(GlInterface gl, int prg, string name, float x, float y, float z)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(prg, ptr);
        Marshal.FreeHGlobal(ptr);
        if (loc != -1)
        {
            var f = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)gl.GetProcAddress("glUniform3f");
            if (f != null) f(loc, x, y, z);
        }
    }

    private unsafe void SetUniform2F(GlInterface gl, int prg, string name, float x, float y)
    {
        nint ptr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(prg, ptr);
        Marshal.FreeHGlobal(ptr);
        if (loc != -1)
        {
            var f = (delegate* unmanaged[Stdcall]<int, float, float, void>)gl.GetProcAddress("glUniform2f");
            if (f != null) f(loc, x, y);
        }
    }

    #endregion

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _uiHeartbeat?.Stop();
        if (_program != 0) gl.DeleteProgram(_program);
        if (_audioTexture != 0) gl.DeleteTexture(_audioTexture);
        if (_coverTexture != 0) gl.DeleteTexture(_coverTexture);
        if (_vbo != 0) gl.DeleteBuffer(_vbo);
        if (_vao != 0) gl.DeleteVertexArray(_vao);

    }
}
