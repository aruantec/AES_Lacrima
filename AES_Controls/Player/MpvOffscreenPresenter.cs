using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using SkiaSharp;

namespace AES_Controls.Player;

/// <summary>
/// Renders mpv into a fixed-size offscreen framebuffer and presents a scaled blit
/// to the target surface so mpv is never resized during UI layout animations.
/// </summary>
internal sealed class MpvOffscreenPresenter : IDisposable
{
    private const int GlFramebuffer = 0x8D40;
    private const int GlReadFramebuffer = 0x8CA8;
    private const int GlDrawFramebuffer = 0x8CA9;
    private const int GlColorBufferBit = 0x4000;
    private const int GlTexture2D = 0x0DE1;
    private const int GlRgba = 0x1908;
    private const int GlRgba8 = 0x8058;
    private const int GlUnsignedByte = 0x1401;
    private const int GlLinear = 0x2601;
    private const int GlClampToEdge = 0x812F;
    private const int GlColorAttachment0 = 0x8CE0;
    private const int GlDepthAttachment = 0x8D00;
    private const int GlRenderbuffer = 0x8D41;
    private const int GlDepthComponent24 = 0x81A6;
    private const int GlFramebufferComplete = 0x8CD5;
    private const int GlArrayBuffer = 0x8892;
    private const int GlDynamicDraw = 0x88E8;
    private const int GlFloat = 0x1406;
    private const int GlTriangleStrip = 0x0005;
    private const int GlTexture0 = 0x84C0;
    private const int GlVertexShader = 0x8B31;
    private const int GlFragmentShader = 0x8B30;
    private const int GlBlend = 0x0BE2;

    public const int DefaultRenderWidth = 1920;
    public const int DefaultRenderHeight = 1080;
    public const int DefaultInternalFormat = GlRgba8;

    private delegate void GlUniform1iDelegate(int location, int value);
    private delegate void GlBlitFramebufferDelegate(
        int srcX0, int srcY0, int srcX1, int srcY1,
        int dstX0, int dstY0, int dstX1, int dstY1,
        int mask, int filter);

    private int _renderWidth = DefaultRenderWidth;
    private int _renderHeight = DefaultRenderHeight;
    private int _fbo;
    private int _colorTexture;
    private int _depthRenderbuffer;
    private int _program;
    private int _vao;
    private int _vbo;
    private int _textureUniform;
    private bool _isEs;
    private bool _initialized;
    private bool _useGpuBlit;
    private GlUniform1iDelegate? _uniform1i;
    private GlBlitFramebufferDelegate? _blitFramebuffer;

    public int RenderWidth => _renderWidth;
    public int RenderHeight => _renderHeight;
    public int InternalFormat => DefaultInternalFormat;

    public void EnsureInitialized(GlInterface gl, int renderWidth, int renderHeight)
    {
        renderWidth = Math.Clamp(renderWidth, 320, DefaultRenderWidth);
        renderHeight = Math.Clamp(renderHeight, 180, DefaultRenderHeight);

        if (_initialized && _renderWidth == renderWidth && _renderHeight == renderHeight)
            return;

        DisposeGl(gl);
        _renderWidth = renderWidth;
        _renderHeight = renderHeight;
        CreateResources(gl);
        _initialized = true;
    }

    public void RenderMpvToOffscreen(GlInterface gl, Action<int, int, int, int> renderMpv)
    {
        if (!_initialized)
            return;

        gl.BindFramebuffer(GlFramebuffer, _fbo);
        gl.Viewport(0, 0, _renderWidth, _renderHeight);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(GlColorBufferBit);
        renderMpv(_renderWidth, _renderHeight, _fbo, DefaultInternalFormat);
        gl.BindFramebuffer(GlFramebuffer, 0);
    }

    public void BlitToTarget(GlInterface gl, int targetFramebuffer, int viewWidth, int viewHeight, Stretch stretch)
    {
        if (!_initialized || viewWidth <= 0 || viewHeight <= 0)
            return;

        gl.Disable(GlBlend);
        gl.BindFramebuffer(GlDrawFramebuffer, targetFramebuffer);
        gl.Viewport(0, 0, viewWidth, viewHeight);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(GlColorBufferBit);

        ComputeDestRect(viewWidth, viewHeight, _renderWidth, _renderHeight, stretch, out int x, out int y, out int w, out int h);
        if (w <= 0 || h <= 0)
            return;

        if (_useGpuBlit && _blitFramebuffer != null)
        {
            int dstY0 = viewHeight - y - h;
            int dstY1 = viewHeight - y;
            gl.BindFramebuffer(GlReadFramebuffer, _fbo);
            _blitFramebuffer.Invoke(
                0, _renderHeight, _renderWidth, 0,
                x, dstY0, x + w, dstY1,
                GlColorBufferBit, GlLinear);
            gl.BindFramebuffer(GlReadFramebuffer, 0);
            gl.BindFramebuffer(GlDrawFramebuffer, 0);
            return;
        }

        BlitWithShader(gl, x, y, w, h, viewWidth, viewHeight);
        gl.BindFramebuffer(GlFramebuffer, 0);
    }

    public void DisposeGl(GlInterface gl)
    {
        if (!_initialized)
            return;

        if (_program != 0)
        {
            gl.DeleteProgram(_program);
            _program = 0;
        }

        if (_vbo != 0)
        {
            gl.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_vao != 0)
        {
            gl.DeleteVertexArray(_vao);
            _vao = 0;
        }

        if (_depthRenderbuffer != 0)
        {
            gl.DeleteRenderbuffer(_depthRenderbuffer);
            _depthRenderbuffer = 0;
        }

        if (_colorTexture != 0)
        {
            gl.DeleteTexture(_colorTexture);
            _colorTexture = 0;
        }

        if (_fbo != 0)
        {
            gl.DeleteFramebuffer(_fbo);
            _fbo = 0;
        }

        _initialized = false;
        _useGpuBlit = false;
        _blitFramebuffer = null;
    }

    public void Dispose()
    {
        _uniform1i = null;
    }

    private void BlitWithShader(GlInterface gl, int x, int y, int width, int height, int viewWidth, int viewHeight)
    {
        if (_program == 0)
            return;

        UpdateQuad(gl, x, y, width, height, viewWidth, viewHeight);

        gl.UseProgram(_program);
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _colorTexture);
        if (_textureUniform >= 0)
            _uniform1i?.Invoke(_textureUniform, 0);

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlArrayBuffer, _vbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GlFloat, 0, 4 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GlFloat, 0, 4 * sizeof(float), new IntPtr(2 * sizeof(float)));
        gl.DrawArrays(GlTriangleStrip, 0, 4);
        gl.BindVertexArray(0);
    }

    private void CreateResources(GlInterface gl)
    {
        var uniformPtr = gl.GetProcAddress("glUniform1i");
        if (uniformPtr != IntPtr.Zero)
            _uniform1i = Marshal.GetDelegateForFunctionPointer<GlUniform1iDelegate>(uniformPtr);

        var blitPtr = gl.GetProcAddress("glBlitFramebuffer");
        if (blitPtr != IntPtr.Zero)
        {
            _blitFramebuffer = Marshal.GetDelegateForFunctionPointer<GlBlitFramebufferDelegate>(blitPtr);
            _useGpuBlit = true;
        }

        var (shaderVersion, isEs) = GlHelper.GetShaderVersion(gl);
        _isEs = isEs;

        string vertexSource = shaderVersion + "\n" + """
            layout(location = 0) in vec2 aPos;
            layout(location = 1) in vec2 aUv;
            out vec2 vUv;
            void main()
            {
                gl_Position = vec4(aPos, 0.0, 1.0);
                vUv = aUv;
            }
            """;

        string esPrecision = _isEs ? "precision mediump float;\n" : string.Empty;
        string fragmentSource = shaderVersion + "\n" + esPrecision + """
            in vec2 vUv;
            uniform sampler2D uTexture;
            out vec4 fragColor;
            void main()
            {
                fragColor = texture(uTexture, vUv);
            }
            """;

        _program = CreateProgram(gl, vertexSource, fragmentSource);
        if (_program == 0)
            Debug.WriteLine("MpvOffscreenPresenter: blit shader program creation failed.");
        else
            _textureUniform = GetUniformLocation(gl, _program, "uTexture");

        _colorTexture = gl.GenTexture();
        gl.BindTexture(GlTexture2D, _colorTexture);
        gl.TexParameteri(GlTexture2D, 0x2801, GlLinear);
        gl.TexParameteri(GlTexture2D, 0x2800, GlLinear);
        gl.TexParameteri(GlTexture2D, 0x2802, GlClampToEdge);
        gl.TexParameteri(GlTexture2D, 0x2803, GlClampToEdge);
        gl.TexImage2D(GlTexture2D, 0, GlRgba8, _renderWidth, _renderHeight, 0, GlRgba, GlUnsignedByte, IntPtr.Zero);

        _depthRenderbuffer = gl.GenRenderbuffer();
        gl.BindRenderbuffer(GlRenderbuffer, _depthRenderbuffer);
        gl.RenderbufferStorage(GlRenderbuffer, GlDepthComponent24, _renderWidth, _renderHeight);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(GlFramebuffer, _fbo);
        gl.FramebufferTexture2D(GlFramebuffer, GlColorAttachment0, GlTexture2D, _colorTexture, 0);
        gl.FramebufferRenderbuffer(GlFramebuffer, GlDepthAttachment, GlRenderbuffer, _depthRenderbuffer);

        if (!IsFramebufferComplete(gl))
            Debug.WriteLine("MpvOffscreenPresenter: offscreen framebuffer is incomplete.");

        gl.BindFramebuffer(GlFramebuffer, 0);
        gl.BindRenderbuffer(GlRenderbuffer, 0);

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlArrayBuffer, _vbo);
        gl.BindVertexArray(0);
    }

    private bool IsFramebufferComplete(GlInterface gl)
    {
        var statusPtr = gl.GetProcAddress("glCheckFramebufferStatus");
        if (statusPtr == IntPtr.Zero)
            return true;

        var check = Marshal.GetDelegateForFunctionPointer<GlCheckFramebufferStatusDelegate>(statusPtr);
        return check(GlFramebuffer) == GlFramebufferComplete;
    }

    private delegate int GlCheckFramebufferStatusDelegate(int target);

    private delegate void GlReadPixelsDelegate(int x, int y, int width, int height, int format, int type, nint data);

    public SKImage? TryCaptureRgbaFrame(GlInterface gl)
    {
        if (!_initialized || _renderWidth <= 0 || _renderHeight <= 0)
            return null;

        var readPtr = gl.GetProcAddress("glReadPixels");
        if (readPtr == IntPtr.Zero)
            return null;

        var readPixels = Marshal.GetDelegateForFunctionPointer<GlReadPixelsDelegate>(readPtr);
        var pixels = new byte[_renderWidth * _renderHeight * 4];
        int rowBytes = _renderWidth * 4;

        gl.BindFramebuffer(GlFramebuffer, _fbo);
        var finishPtr = gl.GetProcAddress("glFinish");
        if (finishPtr != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<Action>(finishPtr).Invoke();

        unsafe
        {
            fixed (byte* p = pixels)
                readPixels(0, 0, _renderWidth, _renderHeight, GlRgba, GlUnsignedByte, (nint)p);
        }
        gl.BindFramebuffer(GlFramebuffer, 0);

        var flipped = new byte[pixels.Length];
        for (int y = 0; y < _renderHeight; y++)
            Buffer.BlockCopy(pixels, y * rowBytes, flipped, (_renderHeight - 1 - y) * rowBytes, rowBytes);

        var info = new SKImageInfo(_renderWidth, _renderHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return SKImage.FromPixelCopy(info, flipped);
    }

    private void UpdateQuad(GlInterface gl, int x, int y, int width, int height, int viewWidth, int viewHeight)
    {
        float left = (2f * x / viewWidth) - 1f;
        float right = (2f * (x + width) / viewWidth) - 1f;
        float top = 1f - (2f * y / viewHeight);
        float bottom = 1f - (2f * (y + height) / viewHeight);

        float[] vertices =
        [
            left, bottom, 0f, 1f,
            right, bottom, 1f, 1f,
            left, top, 0f, 0f,
            right, top, 1f, 0f,
        ];

        gl.BindBuffer(GlArrayBuffer, _vbo);
        unsafe
        {
            fixed (float* ptr = vertices)
                gl.BufferData(GlArrayBuffer, new IntPtr(vertices.Length * sizeof(float)), (IntPtr)ptr, GlDynamicDraw);
        }
    }

    private static void ComputeDestRect(
        int viewWidth,
        int viewHeight,
        int contentWidth,
        int contentHeight,
        Stretch stretch,
        out int x,
        out int y,
        out int width,
        out int height)
    {
        if (viewWidth <= 0 || viewHeight <= 0 || contentWidth <= 0 || contentHeight <= 0)
        {
            x = y = 0;
            width = Math.Max(0, viewWidth);
            height = Math.Max(0, viewHeight);
            return;
        }

        switch (stretch)
        {
            case Stretch.None:
                width = Math.Min(viewWidth, contentWidth);
                height = Math.Min(viewHeight, contentHeight);
                x = (viewWidth - width) / 2;
                y = (viewHeight - height) / 2;
                return;
            case Stretch.Fill:
                x = 0;
                y = 0;
                width = viewWidth;
                height = viewHeight;
                return;
            case Stretch.UniformToFill:
            {
                float contentAspect = (float)contentWidth / contentHeight;
                float viewAspect = (float)viewWidth / viewHeight;
                if (contentAspect > viewAspect)
                {
                    height = viewHeight;
                    width = (int)Math.Round(viewHeight * contentAspect);
                    x = (viewWidth - width) / 2;
                    y = 0;
                }
                else
                {
                    width = viewWidth;
                    height = (int)Math.Round(viewWidth / contentAspect);
                    x = 0;
                    y = (viewHeight - height) / 2;
                }

                return;
            }
            default:
            {
                float contentAspect = (float)contentWidth / contentHeight;
                float viewAspect = (float)viewWidth / viewHeight;
                if (contentAspect > viewAspect)
                {
                    width = viewWidth;
                    height = (int)Math.Round(viewWidth / contentAspect);
                    x = 0;
                    y = (viewHeight - height) / 2;
                }
                else
                {
                    height = viewHeight;
                    width = (int)Math.Round(viewHeight * contentAspect);
                    x = (viewWidth - width) / 2;
                    y = 0;
                }

                return;
            }
        }
    }

    private static int CreateProgram(GlInterface gl, string vertexSource, string fragmentSource)
    {
        int vertexShader = gl.CreateShader(GlVertexShader);
        int fragmentShader = gl.CreateShader(GlFragmentShader);
        if (!CompileShader(gl, vertexShader, vertexSource, "vertex"))
            return 0;
        if (!CompileShader(gl, fragmentShader, fragmentSource, "fragment"))
            return 0;

        int program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.LinkProgram(program);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        if (!IsProgramLinked(gl, program))
        {
            Debug.WriteLine("MpvOffscreenPresenter: shader program link failed.");
            gl.DeleteProgram(program);
            return 0;
        }

        return program;
    }

    private static unsafe bool CompileShader(GlInterface gl, int shader, string source, string label)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = bytes)
        {
            sbyte* sourcePtr = (sbyte*)ptr;
            sbyte** sourcePtrPtr = &sourcePtr;
            int length = bytes.Length;
            gl.ShaderSource(shader, 1, (IntPtr)sourcePtrPtr, (IntPtr)(&length));
        }

        gl.CompileShader(shader);
        if (IsShaderCompiled(gl, shader))
            return true;

        Debug.WriteLine($"MpvOffscreenPresenter: {label} shader compile failed.");
        return false;
    }

    private static unsafe bool IsShaderCompiled(GlInterface gl, int shader)
    {
        var ptr = gl.GetProcAddress("glGetShaderiv");
        if (ptr == IntPtr.Zero)
            return true;

        var getShaderiv = Marshal.GetDelegateForFunctionPointer<GlGetShaderivDelegate>(ptr);
        int status = 0;
        getShaderiv(shader, 0x8B81, out status);
        return status != 0;
    }

    private static unsafe bool IsProgramLinked(GlInterface gl, int program)
    {
        var ptr = gl.GetProcAddress("glGetProgramiv");
        if (ptr == IntPtr.Zero)
            return true;

        var getProgramiv = Marshal.GetDelegateForFunctionPointer<GlGetProgramivDelegate>(ptr);
        int status = 0;
        getProgramiv(program, 0x8B82, out status);
        return status != 0;
    }

    private delegate void GlGetShaderivDelegate(int shader, int pname, out int parameters);
    private delegate void GlGetProgramivDelegate(int program, int pname, out int parameters);

    private static unsafe int GetUniformLocation(GlInterface gl, int program, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* ptr = bytes)
            return gl.GetUniformLocation(program, (IntPtr)ptr);
    }
}
