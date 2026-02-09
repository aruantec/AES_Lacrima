using System.Diagnostics;
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

namespace AES_Controls.Composition;

/// <summary>
/// Handles the rendering of particles on the compositor thread.
/// This class can operate in two modes: OpenGL for hardware acceleration, or Skia for software fallback.
/// </summary>
public class ParticleVisualHandler : CompositionCustomVisualHandler
{
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
    private long _lastTick = 0;
    private float[] _particleData = Array.Empty<float>();
    private float[] _bgData = new float[16];
    private SKPaint? _skPaint = new SKPaint() { IsAntialias = true };
    private int _frameCounter = 0;
    private double _frameAccum = 0.0;

    private int _particleCount = 150;
    private System.Numerics.Vector2 _visualSize;
    private Bitmap? _backgroundBitmap;
    private Stretch _stretch = Stretch.UniformToFill;
    private bool _isPaused;

    private GlInterface? _gl;
    private bool _initialized;

    private struct Particle { public float X, Y, Vx, Vy, Size, R, G, B, A; }

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
            case System.Numerics.Vector2 v:
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
            catch { }
            try
            {
                if (_bgVao != 0) _gl.DeleteVertexArray(_bgVao);
            }
            catch { }
            if (_texCurrent != 0) _gl.DeleteTexture(_texCurrent);
            if (_texPrevious != 0) _gl.DeleteTexture(_texPrevious);
        }
        _gl = null;

        // dispose managed GPU/Skia resources
        _skPaint?.Dispose();
        _skPaint = null;
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
        _frameCounter++;
        if (_frameAccum >= 0.5)
        {
            var fps = _frameCounter / _frameAccum;
            _frameCounter = 0; _frameAccum = 0;
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
            float timeFactor = _lastDelta * 60f;
            for (int i = 0; i < _particles.Count; i++)
            {
                var p = _particles[i];
                p.X += p.Vx * timeFactor; p.Y += p.Vy * timeFactor;
                if (p.X < -1.1f) p.X = 1.1f; else if (p.X > 1.1f) p.X = -1.1f;
                if (p.Y < -1.1f) p.Y = 1.1f; else if (p.Y > 1.1f) p.Y = -1.1f;
                _particles[i] = p;
            }
        }

        using var paint = new SKPaint();
        paint.IsAntialias = true;
        // reuse SKPaint when possible
        var paintToUse = _skPaint ??= new SKPaint() { IsAntialias = true };
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            float px = (p.X + 1f) * 0.5f * w;
            float py = (1f - (p.Y + 1f) * 0.5f) * h; // flip Y
            paintToUse.Color = new SKColor((byte)(p.R * 255), (byte)(p.G * 255), (byte)(p.B * 255), (byte)(p.A * 255));
            float radius = Math.Max(1f, p.Size * 0.05f);
            canvas.DrawCircle(px, py, radius, paintToUse);
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

        gl.Enable(GlConsts.ProgramPointSize);
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
        for (int i = 0; i < count; i++) _particles.Add(new Particle
        {
            X = (float)_rnd.NextDouble() * 2 - 1,
            Y = (float)_rnd.NextDouble() * 2 - 1,
            Vx = (float)(_rnd.NextDouble() - 0.5) * 0.005f,
            Vy = (float)(_rnd.NextDouble() - 0.5) * 0.005f,
            Size = (float)_rnd.NextDouble() * 40 + 10,
            R = (float)(_rnd.NextDouble() * 0.5 + 0.5),
            G = (float)(_rnd.NextDouble() * 0.5 + 0.5),
            B = 1.0f, A = 0.4f
        });
        _lastCount = count;
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
        
        // Manual BlendFunc
        var glBlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
        if (glBlendFunc != null) glBlendFunc(GlConsts.SrcAlpha, GlConsts.One);

        float timeFactor = _lastDelta * 60f;

        // reuse data array to avoid allocations per-frame
        if (_particleData.Length < _particles.Count * 7) _particleData = new float[_particles.Count * 7];
        var data = _particleData;
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.X += p.Vx * timeFactor; p.Y += p.Vy * timeFactor;
            if (p.X < -1.1f) p.X = 1.1f; else if (p.X > 1.1f) p.X = -1.1f;
            if (p.Y < -1.1f) p.Y = 1.1f; else if (p.Y > 1.1f) p.Y = -1.1f;
            _particles[i] = p;

            int o = i * 7;
            data[o + 0] = p.X; data[o + 1] = p.Y;
            data[o + 2] = p.R; data[o + 3] = p.G; data[o + 4] = p.B; data[o + 5] = p.A;
            data[o + 6] = p.Size;
        }

        gl.UseProgram(_pProgram);
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlConsts.ArrayBuffer, _vbo);
        fixed (float* pData = data) gl.BufferData(GlConsts.ArrayBuffer, new IntPtr(data.Length * sizeof(float)), (IntPtr)pData, GlConsts.StreamDraw);

        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 2, GlConsts.Float, 0, 28, IntPtr.Zero);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 4, GlConsts.Float, 0, 28, new IntPtr(8));
        gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 1, GlConsts.Float, 0, 28, new IntPtr(24));

        gl.DrawArrays(GlConsts.Points, 0, _particles.Count);
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
layout(location = 1) in vec4 aCol;
layout(location = 2) in float aSize;
out vec4 vCol;
void main() {{
    gl_Position = vec4(aPos, 0.0, 1.0);
    gl_PointSize = aSize;
    vCol = aCol;
}}";

    private string GetParticleFs(string shaderVersion, bool isEs) => $@"{shaderVersion}
{(isEs ? "precision mediump float;" : string.Empty)}
in vec4 vCol;
out vec4 fragColor;
void main() {{
    float d = distance(gl_PointCoord, vec2(0.5));
    if (d > 0.5) discard;
    fragColor = vec4(vCol.rgb, vCol.a * smoothstep(0.5, 0.1, d));
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
        public const int Points = 0x0000;
        public const int Version = 0x1F02;
        public const int VertexShader = 0x8B31;
        public const int FragmentShader = 0x8B30;
    }
}