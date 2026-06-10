using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

using log4net;
using AES_Core.Logging;
namespace AES_Controls.Composition;

/// <summary>
/// Handles the rendering of Lacrima raindrops on the compositor thread.
/// This class can operate in two modes: OpenGL for hardware acceleration, or Skia for software fallback.
/// </summary>
public class ParticleVisualHandler : CompositionCustomVisualHandler
{
    private const int RainVerticesPerDrop = 9;
    private const int RainFloatsPerVertex = 8;

    private static readonly ILog Log = LogHelper.For<ParticleVisualHandler>();
    private readonly Random _rnd = new();
    private readonly List<Particle> _particles = new();
    private int _pProgram, _bgProgram, _vbo, _vao, _bgVbo, _bgVao;
    private int _texCurrent, _texPrevious;
    private readonly float _fadeFactor = 1.0f;
    private int _lastCount = -1;
    private bool _textureNeedsUpdate;
    private bool _isEs;
    private float _lastDelta = 1f / 60f;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _lastTick;
    private float[] _rainVertexData = Array.Empty<float>();
    private SKPaint? _skPaint = new() { IsAntialias = true };
    private SKPath? _skRainPath;
    private double _frameAccum;

    private int _particleCount = 150;
    private Vector2 _visualSize;
    private Bitmap? _backgroundBitmap;
    private Stretch _stretch = Stretch.UniformToFill;
    private bool _isPaused;

    private GlInterface? _gl;
    private bool _initialized;

    private struct Particle
    {
        public float X, Y, Vx, Vy, Width, Length, R, G, B, A;
    }

    /// <inheritdoc />
    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                Cleanup();
                return;
            case "invalidate":
                Invalidate();
                // start the animation loop using compositor frame callbacks
                RegisterForNextAnimationFrameUpdate();
                return;
            case Vector2 v:
                _visualSize = v;
                Invalidate();
                return;
            case ParticleSettingsMessage pm:
                _particleCount = pm.ParticleCount;
                _backgroundBitmap = pm.Background;
                _stretch = pm.Stretch;
                _isPaused = pm.IsPaused;
                _textureNeedsUpdate = true;
                Invalidate();
                return;
        }

    }

    private void Cleanup()
    {
        _particles.Clear();
        _initialized = false;
        if (_gl != null)
        {
            if (_pProgram != 0) _gl.DeleteProgram(_pProgram);
            if (_bgProgram != 0) _gl.DeleteProgram(_bgProgram);
            if (_vbo != 0) _gl.DeleteBuffer(_vbo);
            if (_bgVbo != 0) _gl.DeleteBuffer(_bgVbo);
            // Delete vertex arrays if supported
            try
            {
                if (_vao != 0) _gl.DeleteVertexArray(_vao);
            }
            catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }

            try
            {
                if (_bgVao != 0) _gl.DeleteVertexArray(_bgVao);
            }
            catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }

            if (_texCurrent != 0) _gl.DeleteTexture(_texCurrent);
            if (_texPrevious != 0) _gl.DeleteTexture(_texPrevious);
        }
        _gl = null;

        // dispose managed GPU/Skia resources
        _skPaint?.Dispose();
        _skPaint = null;
        _skRainPath?.Dispose();
        _skRainPath = null;
    }

    private void EnsureGl(ImmediateDrawingContext context)
    {
        if (_gl != null) return;
        _gl = context.TryGetFeature<IPlatformGraphicsContext>()?.TryGetFeature<IGlContext>()?.GlInterface;
    }

    /// <inheritdoc />
    public override void OnRender(ImmediateDrawingContext context)
    {
        EnsureGl(context);

        // Update fallback timing each render when not using compositor frame timestamps
        var ticks = _stopwatch.ElapsedTicks;
        if (_lastTick != 0)
        {
            var dt = (ticks - _lastTick) / (double)Stopwatch.Frequency;
            if (dt > 0) _lastDelta = (float)dt;
        }
        _lastTick = ticks;

        // Track measured FPS
        _frameAccum += _lastDelta;
        if (_frameAccum >= 0.5)
        {
            _frameAccum = 0;
        }

        if (_gl != null)
        {
            RenderGl();
        }
        else
        {
            RenderSkia(context);
        }
    }

    private void RenderGl()
    {
        if (_gl == null) return;
        
        if (!_initialized)
        {
            InitGl(_gl);
            _initialized = true;
        }

        if (_isPaused) return;

        if (_textureNeedsUpdate) UpdateTexture(_gl);
        if (_particleCount != _lastCount) ResetParticles(_particleCount);

        try
        {
            var w = Math.Max(1, (int)_visualSize.X);
            var h = Math.Max(1, (int)_visualSize.Y);
            _gl.Viewport(0, 0, w, h);
            
            if (_backgroundBitmap != null) RenderBackground(_gl, w, h);
            RenderParticles(_gl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during rendering: {ex.Message}");
        }

        // Request a redraw and ask the compositor for the next animation-frame update
        Invalidate();
        if (!_isPaused) RegisterForNextAnimationFrameUpdate();
    }

    private void RenderSkia(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        var w = Math.Max(1, (int)_visualSize.X);
        var h = Math.Max(1, (int)_visualSize.Y);

        if (!_isPaused)
        {
            if (_particleCount != _lastCount) ResetParticles(_particleCount);
            UpdateRainParticles(_lastDelta);
        }

        var paintToUse = _skPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        paintToUse.MaskFilter = null;
        var path = _skRainPath ??= new SKPath();

        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            float px = (p.X + 1f) * 0.5f * w;
            float py = (1f - (p.Y + 1f) * 0.5f) * h;
            float halfW = Math.Max(0.45f, p.Width * 0.5f);
            float streakLen = Math.Max(4f, p.Length);
            float topHalfW = halfW * 0.3f;
            float bottomHalfW = halfW * 1.25f;
            float topY = py - streakLen * 0.5f;
            float headY = py + streakLen * 0.22f;
            float tipY = py + streakLen * 0.5f + halfW * 0.35f;

            path.Reset();
            path.MoveTo(px - topHalfW, topY);
            path.LineTo(px + topHalfW, topY);
            path.LineTo(px + bottomHalfW, headY);
            path.LineTo(px, tipY);
            path.LineTo(px - bottomHalfW, headY);
            path.Close();

            paintToUse.Color = new SKColor(255, 255, 255, (byte)Math.Clamp(p.A * 255f, 0, 255));
            canvas.DrawPath(path, paintToUse);
        }

        // Request a redraw and ask the compositor for the next animation-frame update
        Invalidate();
        if (!_isPaused) RegisterForNextAnimationFrameUpdate();
    }

    private void InitGl(GlInterface gl)
    {
        _gl = gl;
        var shaderInfo = GetShaderVersion(gl, out _isEs);
        _pProgram = CreateProgram(gl, GetParticleVs(shaderInfo), GetParticleFs(shaderInfo, _isEs));
        _bgProgram = CreateProgram(gl, GetBgVs(shaderInfo), GetBgFs(shaderInfo, _isEs));

        _vbo = gl.GenBuffer(); _vao = gl.GenVertexArray();
        _bgVbo = gl.GenBuffer(); _bgVao = gl.GenVertexArray();

        _texCurrent = gl.GenTexture();
        _texPrevious = gl.GenTexture();

        gl.BindTexture(GlConsts.Texture2D, _texCurrent);
        gl.TexParameteri(GlConsts.Texture2D, GlConsts.TextureMinFilter, GlConsts.Linear);
        gl.TexParameteri(GlConsts.Texture2D, GlConsts.TextureMagFilter, GlConsts.Linear);
        gl.BindTexture(GlConsts.Texture2D, _texPrevious);
        gl.TexParameteri(GlConsts.Texture2D, GlConsts.TextureMinFilter, GlConsts.Linear);
        gl.TexParameteri(GlConsts.Texture2D, GlConsts.TextureMagFilter, GlConsts.Linear);

        _textureNeedsUpdate = true;
    }

    private void UpdateTexture(GlInterface gl)
    {
        _textureNeedsUpdate = false;
        if (_backgroundBitmap == null) return;

        (_texPrevious, _texCurrent) = (_texCurrent, _texPrevious);

        gl.BindTexture(GlConsts.Texture2D, _texCurrent);
        var size = _backgroundBitmap.PixelSize;
        int stride = size.Width * 4; int totalSize = stride * size.Height;
        byte[] pixels = new byte[totalSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            _backgroundBitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), totalSize, stride);
            if (_isEs)
            {
                for (int i = 0; i < totalSize; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]); // BGRA to RGBA
                }
                gl.TexImage2D(GlConsts.Texture2D, 0, GlConsts.Rgba, size.Width, size.Height, 0, GlConsts.Rgba, GlConsts.UnsignedByte, handle.AddrOfPinnedObject());
            }
            else
            {
                gl.TexImage2D(GlConsts.Texture2D, 0, GlConsts.Rgba, size.Width, size.Height, 0, GlConsts.Bgra, GlConsts.UnsignedByte, handle.AddrOfPinnedObject());
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private void ResetParticles(int count)
    {
        _particles.Clear();
        for (int i = 0; i < count; i++)
            _particles.Add(CreateRainParticle(spawnAnywhere: true));
        _lastCount = count;
    }

    private Particle CreateRainParticle(bool spawnAnywhere)
    {
        var speed = (float)(_rnd.NextDouble() * 2.8 + 1.6);
        var length = speed * (float)(_rnd.NextDouble() * 4.5 + 3.5);
        var width = (float)(_rnd.NextDouble() * 1.6 + 0.7);
        var luminance = (float)(_rnd.NextDouble() * 0.12 + 0.88);

        return new Particle
        {
            X = (float)_rnd.NextDouble() * 2f - 1f,
            Y = spawnAnywhere
                ? (float)_rnd.NextDouble() * 2.2f - 1.1f
                : 1.05f + (float)_rnd.NextDouble() * 0.15f,
            Vx = (float)(_rnd.NextDouble() - 0.5) * 0.0009f,
            Vy = -speed * 0.00135f,
            Width = width,
            Length = length,
            R = luminance,
            G = luminance,
            B = luminance,
            A = (float)(_rnd.NextDouble() * 0.2 + 0.12)
        };
    }

    private void UpdateRainParticles(float deltaSeconds)
    {
        float timeFactor = deltaSeconds * 60f;
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.X += p.Vx * timeFactor;
            p.Y += p.Vy * timeFactor;

            if (p.Y < -1.15f)
                p = CreateRainParticle(spawnAnywhere: false);
            else if (p.X < -1.12f)
            {
                p.X = 1.12f;
            }
            else if (p.X > 1.12f)
            {
                p.X = -1.12f;
            }

            _particles[i] = p;
        }
    }

    private unsafe void RenderBackground(GlInterface gl, int viewW, int viewH)
    {
        gl.Disable(GlConsts.Blend);
        gl.UseProgram(_bgProgram);

        float imgW = _backgroundBitmap!.PixelSize.Width;
        float imgH = _backgroundBitmap!.PixelSize.Height;
        float viewRatio = (float)viewW / viewH;
        float imgRatio = imgW / imgH;

        float x = 1.0f, y = 1.0f;
        if (_stretch == Stretch.Uniform)
        {
            if (imgRatio > viewRatio) y = viewRatio / imgRatio; else x = imgRatio / viewRatio;
        }
        else if (_stretch == Stretch.UniformToFill)
        {
            if (imgRatio > viewRatio) x = imgRatio / viewRatio; else y = viewRatio / imgRatio;
        }

        float[] bgData = { -x, y, 0, 0, -x, -y, 0, 1, x, y, 1, 0, x, -y, 1, 1 };

        gl.BindVertexArray(_bgVao);
        gl.BindBuffer(GlConsts.ArrayBuffer, _bgVbo);
        fixed (float* p = bgData) gl.BufferData(GlConsts.ArrayBuffer, new IntPtr(bgData.Length * sizeof(float)), (IntPtr)p, GlConsts.StreamDraw);

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GlConsts.Float, 0, 16, IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GlConsts.Float, 0, 16, new IntPtr(8));

        SetUniform1I(gl, _bgProgram, "uTexNew", 0);
        SetUniform1I(gl, _bgProgram, "uTexOld", 1);
        SetUniform1F(gl, _bgProgram, "uFade", _fadeFactor);

        gl.ActiveTexture(GlConsts.Texture0);
        gl.BindTexture(GlConsts.Texture2D, _texCurrent);
        gl.ActiveTexture(GlConsts.Texture1);
        gl.BindTexture(GlConsts.Texture2D, _texPrevious);

        gl.DrawArrays(GlConsts.TriangleStrip, 0, 4);
    }

    private unsafe void RenderParticles(GlInterface gl)
    {
        gl.Enable(GlConsts.Blend);

        var glBlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
        if (glBlendFunc != null) glBlendFunc(GlConsts.SrcAlpha, GlConsts.One);

        var w = Math.Max(1, (int)_visualSize.X);
        var h = Math.Max(1, (int)_visualSize.Y);
        UpdateRainParticles(_lastDelta);

        var requiredLength = _particles.Count * RainVerticesPerDrop * RainFloatsPerVertex;
        if (_rainVertexData.Length < requiredLength)
            _rainVertexData = new float[requiredLength];

        var data = _rainVertexData;
        var vertexIndex = 0;
        for (int i = 0; i < _particles.Count; i++)
            vertexIndex = AppendRainDropVertices(data, vertexIndex, _particles[i], w, h);

        if (vertexIndex == 0)
            return;

        gl.UseProgram(_pProgram);
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlConsts.ArrayBuffer, _vbo);
        fixed (float* pData = data)
            gl.BufferData(GlConsts.ArrayBuffer, new IntPtr(vertexIndex * sizeof(float)), (IntPtr)pData, GlConsts.StreamDraw);

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GlConsts.Float, 0, 32, IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GlConsts.Float, 0, 32, new IntPtr(8));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, GlConsts.Float, 0, 32, new IntPtr(16));

        gl.DrawArrays(GlConsts.Triangles, 0, vertexIndex / RainFloatsPerVertex);
    }

    private static int AppendRainDropVertices(float[] data, int offset, Particle particle, int viewW, int viewH)
    {
        var widthNorm = particle.Width * 2f / viewW;
        var lengthNorm = particle.Length * 2f / viewH;
        var halfW = widthNorm * 0.5f;
        var halfLen = lengthNorm * 0.5f;
        var topHalfW = halfW * 0.3f;
        var bottomHalfW = halfW * 1.25f;

        var cx = particle.X;
        var tailY = particle.Y + halfLen;
        var headY = particle.Y - halfLen * 0.56f;
        var tipY = particle.Y - halfLen * 1.12f;

        AppendRainVertex(data, ref offset, cx - topHalfW, tailY, 0.1f, 0f, particle);
        AppendRainVertex(data, ref offset, cx + topHalfW, tailY, 0.9f, 0f, particle);
        AppendRainVertex(data, ref offset, cx + bottomHalfW, headY, 1f, 0.78f, particle);

        AppendRainVertex(data, ref offset, cx - topHalfW, tailY, 0.1f, 0f, particle);
        AppendRainVertex(data, ref offset, cx + bottomHalfW, headY, 1f, 0.78f, particle);
        AppendRainVertex(data, ref offset, cx - bottomHalfW, headY, 0f, 0.78f, particle);

        AppendRainVertex(data, ref offset, cx - bottomHalfW, headY, 0f, 0.78f, particle);
        AppendRainVertex(data, ref offset, cx + bottomHalfW, headY, 1f, 0.78f, particle);
        AppendRainVertex(data, ref offset, cx, tipY, 0.5f, 1f, particle);

        return offset;
    }

    private static void AppendRainVertex(float[] data, ref int offset, float x, float y, float u, float v, Particle particle)
    {
        data[offset++] = x;
        data[offset++] = y;
        data[offset++] = u;
        data[offset++] = v;
        data[offset++] = particle.R;
        data[offset++] = particle.G;
        data[offset++] = particle.B;
        data[offset++] = particle.A;
    }

    #region Shader Helpers
    private unsafe void SetUniform1F(GlInterface gl, int prog, string name, float val)
    {
        var namePtr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(prog, namePtr);
        Marshal.FreeHGlobal(namePtr);
        if (loc != -1)
        {
            var glUniform1F = (delegate* unmanaged[Stdcall]<int, float, void>)gl.GetProcAddress("glUniform1f");
            if (glUniform1F != null) glUniform1F(loc, val);
        }
    }

    private unsafe void SetUniform1I(GlInterface gl, int prog, string name, int val)
    {
        var namePtr = Marshal.StringToHGlobalAnsi(name);
        int loc = gl.GetUniformLocation(prog, namePtr);
        Marshal.FreeHGlobal(namePtr);
        if (loc != -1)
        {
            var glUniform1I = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glUniform1i");
            if (glUniform1I != null) glUniform1I(loc, val);
        }
    }

    private string GetShaderVersion(GlInterface gl, out bool isEs)
    {
        var version = gl.GetString(GlConsts.Version);
        isEs = version?.Contains("OpenGL ES") ?? false;
        return isEs ? "#version 300 es" : "#version 330 core";
    }

    private int CreateProgram(GlInterface gl, string vsSrc, string fsSrc)
    {
        int vs = CompileShader(gl, GlConsts.VertexShader, vsSrc);
        int fs = CompileShader(gl, GlConsts.FragmentShader, fsSrc);
        int prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return prog;
    }

    private unsafe int CompileShader(GlInterface gl, int type, string source)
    {
        int shader = gl.CreateShader(type);
        
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = bytes)
        {
            sbyte* pStr = (sbyte*)ptr;
            sbyte** ppStr = &pStr;
            int len = bytes.Length;
            gl.ShaderSource(shader, 1, (IntPtr)ppStr, (IntPtr)(&len));
        }
        
        gl.CompileShader(shader);
        return shader;
    }

    private string GetParticleVs(string shaderVersion) => $@"{shaderVersion}
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aTex;
layout(location = 2) in vec4 aCol;
out vec2 vTex;
out vec4 vCol;
void main() {{
    gl_Position = vec4(aPos, 0.0, 1.0);
    vTex = aTex;
    vCol = aCol;
}}";

    private string GetParticleFs(string shaderVersion, bool isEs) => $@"{shaderVersion}
{(isEs ? "precision mediump float;" : string.Empty)}
in vec2 vTex;
in vec4 vCol;
out vec4 fragColor;
void main() {{
    float across = abs(vTex.x - 0.5) * 2.0;
    float horiz = smoothstep(1.0, 0.12, across);
    float head = smoothstep(0.55, 1.0, vTex.y);
    float tail = smoothstep(1.0, 0.2, 1.0 - vTex.y);
    float alpha = horiz * mix(tail, 1.0, head) * vCol.a;
    fragColor = vec4(vec3(1.0), alpha);
}}";

    private string GetBgVs(string shaderVersion) => $@"{shaderVersion}
in vec2 aPos;
in vec2 aTex;
out vec2 vTex;
void main() {{
    gl_Position = vec4(aPos, 0.0, 1.0);
    vTex = aTex;
}}";

    private string GetBgFs(string shaderVersion, bool isEs) => $@"{shaderVersion}
{(isEs ? "precision mediump float;" : string.Empty)}
uniform sampler2D uTexNew;
uniform sampler2D uTexOld;
uniform float uFade;
in vec2 vTex;
out vec4 fragColor;
void main() {{
    vec4 colNew = texture(uTexNew, vTex);
    vec4 colOld = texture(uTexOld, vTex);
    fragColor = mix(colOld, colNew, uFade);
}}";
    #endregion

    private static class GlConsts
    {
        public const int Texture2D = 0x0DE1;
        public const int TextureMinFilter = 0x2801;
        public const int TextureMagFilter = 0x2800;
        public const int Linear = 0x2601;
        public const int Rgba = 0x1908;
        public const int Bgra = 0x80E1;
        public const int UnsignedByte = 0x1401;
        public const int ProgramPointSize = 0x8642;
        public const int Blend = 0x0BE2;
        public const int SrcAlpha = 0x0302;
        public const int One = 1;
        public const int ArrayBuffer = 0x8892;
        public const int StreamDraw = 0x88E8;
        public const int Float = 0x1406;
        public const int Texture0 = 0x84C0;
        public const int Texture1 = 0x84C1;
        public const int TriangleStrip = 0x0005;
        public const int Triangles = 0x0004;
        public const int Points = 0x0000;
        public const int Version = 0x1F02;
        public const int VertexShader = 0x8B31;
        public const int FragmentShader = 0x8B30;
    }
}