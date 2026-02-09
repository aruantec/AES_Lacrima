using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AES_Controls.Player.Spectrum;

/// <summary>
/// OpenGL-backed circular cover art visual. Renders an image inside a
/// disc with an inner hole, optional loading spinner and configurable
/// rim glow/stroke. The control uses a small GLSL shader and updates
/// on a dispatcher heartbeat while visible.
/// </summary>
public class GlCoverDiscControl : OpenGlControlBase
{
    private delegate void UnmanagedUniform1I(int location, int v0);
    private delegate void UnmanagedUniform1F(int location, float v0);
    private delegate void UnmanagedUniform2F(int location, float v0, float v1);
    private delegate void UnmanagedUniform4F(int location, float v0, float v1, float v2, float v3);
    private delegate void UnmanagedBlendFunc(int sfactor, int dfactor);

    private UnmanagedUniform1I? _glUniform1I;
    private UnmanagedUniform1F? _glUniform1F;
    private UnmanagedUniform2F? _glUniform2F;
    private UnmanagedUniform4F? _glUniform4F;
    private UnmanagedBlendFunc? _glBlendFunc;

    #region Styled Properties
    public static readonly StyledProperty<Bitmap?> CoverProperty = AvaloniaProperty.Register<GlCoverDiscControl, Bitmap?>(nameof(Cover));
    public static readonly StyledProperty<Stretch> CoverStretchProperty = AvaloniaProperty.Register<GlCoverDiscControl, Stretch>(nameof(CoverStretch), Stretch.UniformToFill);
    public static readonly StyledProperty<double> InnerHoleRatioProperty = AvaloniaProperty.Register<GlCoverDiscControl, double>(nameof(InnerHoleRatio), 0.12);
    public static readonly StyledProperty<Color> ImageFillColorProperty = AvaloniaProperty.Register<GlCoverDiscControl, Color>(nameof(ImageFillColor), Colors.Black);
    public static readonly StyledProperty<bool> IsLoadingProperty = AvaloniaProperty.Register<GlCoverDiscControl, bool>(nameof(IsLoading));
    public static readonly StyledProperty<double> SpinnerSweepProperty = AvaloniaProperty.Register<GlCoverDiscControl, double>(nameof(SpinnerSweep), 60.0);
    public static readonly StyledProperty<Color> SpinnerColorProperty = AvaloniaProperty.Register<GlCoverDiscControl, Color>(nameof(SpinnerColor), Color.FromArgb(220, 240, 240, 240));
    public static readonly StyledProperty<bool> IsRotationEnabledProperty = AvaloniaProperty.Register<GlCoverDiscControl, bool>(nameof(IsRotationEnabled), true);
    public static readonly StyledProperty<bool> IsRenderingPausedProperty = AvaloniaProperty.Register<GlCoverDiscControl, bool>(nameof(IsRenderingPaused));
    public static readonly StyledProperty<double> InnerRimStrokeFactorProperty = AvaloniaProperty.Register<GlCoverDiscControl, double>(nameof(InnerRimStrokeFactor), 1.0);
    public static readonly StyledProperty<double> InnerRimGlowFactorProperty = AvaloniaProperty.Register<GlCoverDiscControl, double>(nameof(InnerRimGlowFactor), 1.0);
    public static readonly StyledProperty<bool> DisableVSyncProperty = AvaloniaProperty.Register<GlCoverDiscControl, bool>(nameof(DisableVSync), true);

    /// <summary>
    /// Source bitmap to render inside the disc. May be null to indicate no image.
    /// </summary>
    public Bitmap? Cover { get => GetValue(CoverProperty); set => SetValue(CoverProperty, value); }
    /// <summary>
    /// How the cover bitmap is stretched inside the disc (Uniform, Fill, etc.).
    /// </summary>
    public Stretch CoverStretch { get => GetValue(CoverStretchProperty); set => SetValue(CoverStretchProperty, value); }
    /// <summary>
    /// Fraction (0..1) of the disc radius reserved for the inner hole.
    /// </summary>
    public double InnerHoleRatio { get => GetValue(InnerHoleRatioProperty); set => SetValue(InnerHoleRatioProperty, value); }
    /// <summary>
    /// Fallback fill color blended with the image when rendering.
    /// </summary>
    public Color ImageFillColor { get => GetValue(ImageFillColorProperty); set => SetValue(ImageFillColorProperty, value); }
    /// <summary>
    /// When true the loading spinner overlay is shown on the disc.
    /// </summary>
    public bool IsLoading { get => GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    /// <summary>
    /// Angular sweep (degrees) of the loading spinner segment.
    /// </summary>
    public double SpinnerSweep { get => GetValue(SpinnerSweepProperty); set => SetValue(SpinnerSweepProperty, value); }
    /// <summary>
    /// Colour used when drawing the loading spinner overlay.
    /// </summary>
    public Color SpinnerColor { get => GetValue(SpinnerColorProperty); set => SetValue(SpinnerColorProperty, value); }
    /// <summary>
    /// When true the disc rotates continuously (useful for subtle animation).
    /// </summary>
    public bool IsRotationEnabled { get => GetValue(IsRotationEnabledProperty); set => SetValue(IsRotationEnabledProperty, value); }
    /// <summary>
    /// When true rendering is paused and the control will not request frames.
    /// </summary>
    public bool IsRenderingPaused { get => GetValue(IsRenderingPausedProperty); set => SetValue(IsRenderingPausedProperty, value); }
    /// <summary>
    /// Factor controlling the inner rim stroke strength.
    /// </summary>
    public double InnerRimStrokeFactor { get => GetValue(InnerRimStrokeFactorProperty); set => SetValue(InnerRimStrokeFactorProperty, value); }
    /// <summary>
    /// Factor controlling the glow intensity around the inner rim.
    /// </summary>
    public double InnerRimGlowFactor { get => GetValue(InnerRimGlowFactorProperty); set => SetValue(InnerRimGlowFactorProperty, value); }
    /// <summary>
    /// When true, attempts to disable VSync on supported platforms to
    /// increase rendering frame rate for the control.
    /// </summary>
    public bool DisableVSync { get => GetValue(DisableVSyncProperty); set => SetValue(DisableVSyncProperty, value); }
    #endregion

    private int _program, _vbo, _vao, _textureId;
    private int _uRotLoc, _uInnerLoc, _uFillLoc, _uLoadLoc, _uSpinRotLoc, _uSpinSweepLoc, _uSpinColLoc, _uGlowLoc, _uStrokeLoc, _uUvScaleLoc, _uTexLoc;
    private bool _textureDirty;
    private float _rotationAngle, _loadingRotation;
    private bool _isEs;
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

    private readonly Stopwatch _st = Stopwatch.StartNew();
    private double _lastTicks;

    /// <summary>
    /// Responds to property changes (marks the texture dirty when the Cover
    /// changes and requests rendering when the control becomes visible).
    /// </summary>
    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoverProperty) _textureDirty = true;
        // If the control just became visible, start the render loop again
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// Initialize GL resources (shaders, buffers and texture) and start the
    /// internal heartbeat used to schedule frames.
    /// </summary>
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

        _glUniform1I = Marshal.GetDelegateForFunctionPointer<UnmanagedUniform1I>(gl.GetProcAddress("glUniform1i"));
        _glUniform1F = Marshal.GetDelegateForFunctionPointer<UnmanagedUniform1F>(gl.GetProcAddress("glUniform1f"));
        _glUniform2F = Marshal.GetDelegateForFunctionPointer<UnmanagedUniform2F>(gl.GetProcAddress("glUniform2f"));
        _glUniform4F = Marshal.GetDelegateForFunctionPointer<UnmanagedUniform4F>(gl.GetProcAddress("glUniform4f"));
        _glBlendFunc = Marshal.GetDelegateForFunctionPointer<UnmanagedBlendFunc>(gl.GetProcAddress("glBlendFunc"));

        var shaderInfo = GlHelper.GetShaderVersion(gl);
        _isEs = shaderInfo.Item2;

        string vs = $@"{shaderInfo.Item1}
                layout(location = 0) in vec2 aPos; layout(location = 1) in vec2 aTex;
                uniform float uRotation; uniform vec2 uUVScale;
                out vec2 vTex, vDiscCoord;
                void main() {{
                    float rad = radians(uRotation); float c = cos(rad), s = sin(rad);
                    gl_Position = vec4(mat2(c, -s, s, c) * aPos, 0.0, 1.0);
                    vTex = (aTex - 0.5) * uUVScale + 0.5; vDiscCoord = aTex; 
                }}";

        string swizzleLine = _isEs ? "" : "tex = vec4(tex.b, tex.g, tex.r, tex.a);";
        string fs = $@"{shaderInfo.Item1}
                {(_isEs ? "precision mediump float;" : "")}
                in vec2 vTex, vDiscCoord;
                uniform sampler2D uTexture; uniform float uInnerRatio, uSpinnerRotation, uSpinnerSweep, uGlowFactor, uStrokeFactor;
                uniform vec4 uFillColor, uSpinnerColor; uniform bool uIsLoading;
                out vec4 fragColor;
                void main() {{
                    vec2 uvShape = vDiscCoord - vec2(0.5); float dist = length(uvShape) * 2.0;
                    if (dist > 1.0 || dist < uInnerRatio) discard;
                    vec4 tex = texture(uTexture, vTex); {swizzleLine}
                    vec4 color = mix(uFillColor, tex, tex.a);
                    float glowDist = dist - uInnerRatio;
                    if (glowDist < 0.05 * uGlowFactor) color += vec4(0.3) * (1.0 - (glowDist / (0.05 * uGlowFactor))) * 0.4;
                    if (glowDist < 0.01 * uStrokeFactor) color = mix(color, vec4(1.0, 1.0, 1.0, 0.8), 0.5);
                    if (uIsLoading) {{
                        float angle = degrees(atan(uvShape.y, uvShape.x)) + 180.0;
                        if (dist > 0.94 && mod(angle - uSpinnerRotation, 360.0) < uSpinnerSweep) color = mix(color, uSpinnerColor, uSpinnerColor.a);
                    }}
                    fragColor = color;
                }}";

        _program = CreateProgram(gl, vs, fs);
        _uRotLoc = GetLoc(gl, "uRotation"); _uInnerLoc = GetLoc(gl, "uInnerRatio"); _uFillLoc = GetLoc(gl, "uFillColor");
        _uLoadLoc = GetLoc(gl, "uIsLoading"); _uSpinRotLoc = GetLoc(gl, "uSpinnerRotation"); _uSpinSweepLoc = GetLoc(gl, "uSpinnerSweep");
        _uSpinColLoc = GetLoc(gl, "uSpinnerColor"); _uGlowLoc = GetLoc(gl, "uGlowFactor"); _uStrokeLoc = GetLoc(gl, "uStrokeFactor");
        _uUvScaleLoc = GetLoc(gl, "uUVScale"); _uTexLoc = GetLoc(gl, "uTexture");

        _vbo = gl.GenBuffer(); _vao = gl.GenVertexArray(); _textureId = gl.GenTexture();
        gl.BindTexture(0x0DE1, _textureId);
        gl.TexParameteri(0x0DE1, 0x2801, 0x2601); gl.TexParameteri(0x0DE1, 0x2800, 0x2601);
        _textureDirty = true;

        _uiHeartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(4), DispatcherPriority.Render, (_, _) =>
        {
            if (IsVisible && !IsRenderingPaused) RequestNextFrameRendering();
        });
        _uiHeartbeat.Start();
    }

    /// <summary>
    /// Render the disc into the provided framebuffer. Updates rotation and
    /// loading animation parameters before issuing the draw call.
    /// </summary>
    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (IsRenderingPaused || !IsVisible) return;
        
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

        if (_textureDirty) UpdateTexture(gl);

        double currentTicks = _st.Elapsed.TotalSeconds;
        float delta = (float)(currentTicks - _lastTicks);
        if (delta <= 0) delta = 1f / 120f;
        _lastTicks = currentTicks;

        // Framerate independent rotation speed (40 degrees per second)
        if (IsRotationEnabled) _rotationAngle = (_rotationAngle + (40.0f * delta)) % 360;
        if (IsLoading) _loadingRotation = (_loadingRotation + (300.0f * delta)) % 360;

        gl.BindFramebuffer(0x8D40, fb);
        gl.Viewport(0, 0, (int)(Bounds.Width * (VisualRoot?.RenderScaling ?? 1.0)), (int)(Bounds.Height * (VisualRoot?.RenderScaling ?? 1.0)));
        gl.ClearColor(0, 0, 0, 0); gl.Clear(0x4000);
        gl.Enable(0x0BE2); _glBlendFunc?.Invoke(1, 0x0303);
        gl.UseProgram(_program);

        float uScale = 1.0f, vScale = 1.0f;
        if (Cover != null)
        {
            float aspect = (float)Cover.PixelSize.Width / Cover.PixelSize.Height;
            if (CoverStretch == Stretch.UniformToFill) { if (aspect > 1.0f) uScale = 1.0f / aspect; else vScale = aspect; }
            else if (CoverStretch == Stretch.Uniform) { if (aspect > 1.0f) vScale = aspect; else uScale = 1.0f / aspect; }
        }

        _glUniform1F?.Invoke(_uRotLoc, _rotationAngle); _glUniform1F?.Invoke(_uInnerLoc, (float)InnerHoleRatio);
        _glUniform2F?.Invoke(_uUvScaleLoc, uScale, vScale); _glUniform1F?.Invoke(_uGlowLoc, (float)InnerRimGlowFactor);
        _glUniform1F?.Invoke(_uStrokeLoc, (float)InnerRimStrokeFactor); _glUniform1I?.Invoke(_uLoadLoc, IsLoading ? 1 : 0);
        _glUniform1F?.Invoke(_uSpinRotLoc, _loadingRotation); _glUniform1F?.Invoke(_uSpinSweepLoc, (float)SpinnerSweep);
        _glUniform4F?.Invoke(_uFillLoc, ImageFillColor.R / 255f, ImageFillColor.G / 255f, ImageFillColor.B / 255f, ImageFillColor.A / 255f);
        _glUniform4F?.Invoke(_uSpinColLoc, SpinnerColor.R / 255f, SpinnerColor.G / 255f, SpinnerColor.B / 255f, SpinnerColor.A / 255f);

        float[] v = { -1f, 1f, 0f, 0f, -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, 1f, -1f, 1f, 1f };
        gl.BindVertexArray(_vao); gl.BindBuffer(0x8892, _vbo);
        fixed (float* p = v) gl.BufferData(0x8892, new IntPtr(v.Length * 4), (IntPtr)p, 0x88E8);
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 2, 0x1406, 0, 16, IntPtr.Zero);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 2, 0x1406, 0, 16, new IntPtr(8));
        gl.ActiveTexture(0x84C0); gl.BindTexture(0x0DE1, _textureId); _glUniform1I?.Invoke(_uTexLoc, 0);
        gl.DrawArrays(0x0005, 0, 4);
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Uploads the current <see cref="Cover"/> bitmap into the GL texture.
    /// </summary>
    private unsafe void UpdateTexture(GlInterface gl)
    {
        _textureDirty = false; if (Cover == null) return;
        gl.BindTexture(0x0DE1, _textureId);
        var size = Cover.PixelSize; int stride = size.Width * 4; byte[] px = new byte[size.Height * stride];
        fixed (byte* p = px)
        {
            Cover.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, px.Length, stride);
            if (_isEs)
            {
                for (int i = 0; i < px.Length; i += 4) { byte b = px[i]; px[i] = px[i + 2]; px[i + 2] = b; }
                gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x1908, 0x1401, (IntPtr)p);
            }
            else gl.TexImage2D(0x0DE1, 0, 0x1908, size.Width, size.Height, 0, 0x80E1, 0x1401, (IntPtr)p);
        }
    }

    /// <summary>
    /// Helper to get a uniform location for a null-terminated UTF8 name.
    /// </summary>
    private unsafe int GetLoc(GlInterface gl, string n) { var b = Encoding.UTF8.GetBytes(n + "\0"); fixed (byte* p = b) return gl.GetUniformLocation(_program, (IntPtr)p); }
    /// <summary>
    /// Create and link a GL program from the provided vertex and fragment sources.
    /// </summary>
    private int CreateProgram(GlInterface gl, string vSrc, string fSrc)
    {
        int vs = gl.CreateShader(0x8B31), fs = gl.CreateShader(0x8B30);
        CompileS(gl, vs, vSrc); CompileS(gl, fs, fSrc);
        int p = gl.CreateProgram(); gl.AttachShader(p, vs); gl.AttachShader(p, fs); gl.LinkProgram(p);
        gl.DeleteShader(vs); gl.DeleteShader(fs); return p;
    }
    /// <summary>
    /// Compile a shader from source.
    /// </summary>
    private unsafe void CompileS(GlInterface gl, int s, string src) { var b = Encoding.UTF8.GetBytes(src); fixed (byte* p = b) { sbyte* ps = (sbyte*)p; sbyte** pps = &ps; int len = b.Length; gl.ShaderSource(s, 1, (IntPtr)pps, (IntPtr)(&len)); } gl.CompileShader(s); }
    /// <summary>
    /// Release GL resources created by this control.
    /// </summary>
    protected override void OnOpenGlDeinit(GlInterface gl) 
    { 
        _uiHeartbeat?.Stop();
        gl.DeleteProgram(_program); 
        gl.DeleteBuffer(_vbo); 
        gl.DeleteVertexArray(_vao); 
        gl.DeleteTexture(_textureId); 
    }
}