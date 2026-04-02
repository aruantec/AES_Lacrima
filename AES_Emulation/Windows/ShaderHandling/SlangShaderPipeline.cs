using AES_Emulation.Windows;
using AES_Emulation.Windows.ShaderHandling;
using Avalonia.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AES_Controls.EmuGrabbing.ShaderHandling;

/// <summary>
/// Shader pipeline for loading and executing RetroArch GLSL shaders.
/// 
/// CURRENT LIMITATIONS:
/// - Supports multi-pass chains by ping-ponging intermediate framebuffers (no recursive feedback loop beyond two buffers)
/// - Only single texture inputs are supported (no arbitrary multi-target passes or buffer sampling other than previous frames)
/// - Shader parameters are not exposed to UI
/// </summary>
public class SlangShaderPipeline : IDisposable
{
    private readonly GlInterface _gl;
    private readonly List<ShaderPass> _passes = new();
    private readonly FrameHistoryManager _frameHistory;
    private string _shaderVersionHeader = "";
    private bool _isEs = false;
    private const int GL_TRIANGLE_STRIP = 0x0005;
    private int _quadVbo = 0;
    private int _frameCounter = 0;

    // Intermediate textures for multi-pass
    private int[] _intermediateFbos = Array.Empty<int>();
    private int[] _intermediateTextures = Array.Empty<int>();
    private int _lastW, _lastH;

    // External controls
    public float Brightness { get; set; } = 1.0f;
    public float Saturation { get; set; } = 1.0f;
    public float[] ColorTint { get; set; } = { 1.0f, 1.0f, 1.0f, 1.0f };

    // GL enums used for querying
    private const int GL_COMPILE_STATUS = 0x8B81;
    private const int GL_LINK_STATUS = 0x8B82;
    private const int GL_INFO_LOG_LENGTH = 0x8B84;
    private const int GL_ACTIVE_ATTRIBUTES = 0x8B89;
    private const int GL_ACTIVE_ATTRIBUTE_MAX_LENGTH = 0x8B8A;
    private const int GL_ACTIVE_UNIFORMS = 0x8B86;
    private const int GL_ACTIVE_UNIFORM_MAX_LENGTH = 0x8B87;
    
    // Static cached arrays to avoid per-frame allocations
    private static readonly float[] _mvpMatrixIdentity = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
    private static readonly string[] _samplerNames = { "uTex", "Texture", "source", "Source", "uTexture" };

    public bool HasActiveShader => _passes.Count > 0;
    public bool RequiresFrameHistory { get; private set; } = false;

    // Last error message (if any) produced while compiling/linking the last loaded shader
    public string? LastError { get; private set; }

    public SlangShaderPipeline(GlInterface gl)
    {
        _gl = gl;
        _frameHistory = new FrameHistoryManager(gl);
        var info = GlHelper.GetShaderVersion(_gl);
        _shaderVersionHeader = info.Item1;
        _isEs = info.Item2;
        
        // Don't create logs on startup - too many files accumulate
        Debug.WriteLine($"[Pipeline] SlangShaderPipeline initialized");
    }

    public void LoadShaderPreset(string path)
    {
        LastError = null;
        Dispose();
        RequiresFrameHistory = false;
        if (string.IsNullOrEmpty(path)) return;

        if (!File.Exists(path))
        {
            Debug.WriteLine($"[Pipeline] Shader preset file not found: {path}");
            LastError = "Shader preset file not found";
            return;
        }

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            
            if (ext == ".glslp" || ext == ".slangp")
            {
                var shaderPaths = ParsePresetFileMulti(path);
                if (shaderPaths.Count == 0)
                {
                    LastError = "Preset file does not reference any valid shaders";
                    return;
                }
                foreach (var sPath in shaderPaths)
                {
                    LoadShaderFile(sPath);
                }
            }
            else if (ext == ".glsl" || ext == ".slang")
            {
                LoadShaderFile(path);
            }
            else
            {
                LastError = $"Unknown shader format: {ext}";
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Pipeline] Error loading shader {path}: {ex.Message}");
            LastError = ex.ToString();
            SaveShaderLog(path, "load_exception", ex.ToString());
        }
    }

    private List<string> ParsePresetFileMulti(string presetPath)
    {
        var shaderPaths = new List<string>();
        try
        {
            string presetDir = Path.GetDirectoryName(presetPath) ?? string.Empty;
            var lines = File.ReadAllLines(presetPath);
            
            // First find how many shaders are defined
            int shaderCount = 0;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("shaders", StringComparison.OrdinalIgnoreCase))
                {
                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx > 0 && int.TryParse(trimmed[(eqIdx + 1)..].Trim(), out int count))
                        shaderCount = count;
                }
            }

            if (shaderCount == 0) shaderCount = 10; // Fallback to check at least first few

            for (int i = 0; i < shaderCount; i++)
            {
                string key = $"shader{i}";
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                    {
                        int eqIdx = trimmed.IndexOf('=');
                        string shaderRef = trimmed[(eqIdx + 1)..].Trim().Trim('"');
                        string fullPath = Path.IsPathRooted(shaderRef) ? shaderRef : Path.GetFullPath(Path.Combine(presetDir, shaderRef));
                        if (File.Exists(fullPath)) shaderPaths.Add(fullPath);
                        break;
                    }
                }
            }
        }
        catch { }
        return shaderPaths;
    }

    private void LoadShaderFile(string path)
    {
        try
        {
            string source = File.ReadAllText(path);
            
            // Strip an existing #version directive if present to avoid duplicate #version when we prepend our header.
            // Also remove any 'precision' lines (they are only valid in GLES); we'll add precision when _isEs is true.
            try
            {
                // Remove the first #version line if present
                int idx = source.IndexOf("#version", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Find end of line after the version directive
                    int nl = source.IndexOfAny(new char[] { '\r', '\n' }, idx);
                    if (nl >= 0)
                    {
                        // Trim the version line
                        source = source.Remove(idx, (nl - idx) + 1);
                    }
                    else
                    {
                        // only version directive present
                        source = string.Empty;
                    }
                }

                // Remove any precision lines (e.g. 'precision mediump float;') as they are only valid in GLES.
                source = Regex.Replace(source, @"^\s*precision\s+\w+\s+float\s*;[ \t]*\r?\n", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pipeline] Failed to normalize shader header: {ex.Message}");
            }
            
            // Check if shader requires frame history
            RequiresFrameHistory |= source.Contains("PrevTexture") || source.Contains("Prev1Texture");
            
            // Preprocess shader source: remove RetroArch #pragma parameter lines and fix int uniforms used in float math
            try
            {
                var sbSrc = new StringBuilder();
                using (var sr = new StringReader(source))
                {
                    string? ln;
                    while ((ln = sr.ReadLine()) != null)
                    {
                        // skip pragma parameter lines (not recognized by GLSL compiler)
                        if (ln.TrimStart().StartsWith("#pragma parameter", StringComparison.Ordinal)) continue;
                        // Some shaders use '#pragma parameter' without exact casing
                        if (ln.TrimStart().StartsWith("#pragma", StringComparison.OrdinalIgnoreCase) && ln.Contains("parameter")) continue;
                        sbSrc.AppendLine(ln);
                    }
                }
                source = sbSrc.ToString();

                // IMPORTANT: Apply int?float conversion carefully to avoid breaking working shaders
                // Only convert uniforms that are actually used in float context
                // First pass: Convert FrameCount and FrameDirection (known to be used in float math)
                source = Regex.Replace(source, 
                    @"uniform\s+COMPAT_PRECISION\s+int\s+(FrameCount|FrameDirection)\b", 
                    "uniform COMPAT_PRECISION float $1", RegexOptions.IgnoreCase);
                source = Regex.Replace(source, 
                    @"uniform\s+int\s+(FrameCount|FrameDirection)\b", 
                    "uniform float $1", RegexOptions.IgnoreCase);
                
                // NOTE: Not converting other int uniforms to avoid breaking working shaders
                // If a shader requires motion blur (multiple texture inputs), it will fall back to passthrough
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pipeline] Preprocess shader source failed: {ex.Message}");
            }

            string fullHeader = _shaderVersionHeader + "\n" + (_isEs ? "precision mediump float;\n" : "");

            int vs = 0, fs = 0;
            bool compiledReal = false;
            try
            {
                vs = CompileShader(GlConsts.GL_VERTEX_SHADER, fullHeader + "#define VERTEX\n" + source);
                fs = CompileShader(GlConsts.GL_FRAGMENT_SHADER, fullHeader + "#define FRAGMENT\n" + source);
                if (vs != 0 && fs != 0)
                {
                    // Link test
                    int testProg = _gl.CreateProgram();
                    _gl.AttachShader(testProg, vs);
                    _gl.AttachShader(testProg, fs);
                    // bind common attribute names before link
                    try
                    {
                        unsafe
                        {
                            byte[] b0 = Encoding.ASCII.GetBytes("VertexCoord\0");
                            fixed (byte* p0 = b0) _gl.BindAttribLocation(testProg, 0, (IntPtr)p0);
                            byte[] b1 = Encoding.ASCII.GetBytes("TexCoord\0");
                            fixed (byte* p1 = b1) _gl.BindAttribLocation(testProg, 1, (IntPtr)p1);
                        }
                    } catch { }
                    _gl.LinkProgram(testProg);
                    unsafe { int status = 0; _gl.GetProgramiv(testProg, GL_LINK_STATUS, &status); if (status != 0) compiledReal = true; }
                    // cleanup test program but keep shaders if we'll use them
                    try { _gl.DeleteProgram(testProg); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pipeline] Exception while compiling real shader: {ex.Message}");
            }

            if (!compiledReal)
            {
                Debug.WriteLine($"[Pipeline] Real shader compile/link failed, using debug passthrough for {path}");
                // delete any partially compiled shaders
                try { if (vs != 0) _gl.DeleteShader(vs); } catch { }
                try { if (fs != 0) _gl.DeleteShader(fs); } catch { }

                // fallback debug shaders
                string vsDebug = fullHeader + "layout(location = 0) in vec2 VertexCoord; layout(location = 1) in vec2 TexCoord; out vec2 vTex; void main(){ vTex = TexCoord; gl_Position = vec4(VertexCoord, 0.0, 1.0); }";
                string fsSimple = fullHeader + "uniform sampler2D Texture; in vec2 vTex; out vec4 fragColor; void main(){ vec4 c = texture(Texture, vTex); fragColor = c; }";
                string fsCrt = fullHeader + @"
uniform sampler2D Texture;
in vec2 vTex;
out vec4 fragColor;
void main(){
    vec2 uv = vTex;
    // simple scanline effect
    float lines = sin(uv.y * 800.0) * 0.5 + 0.5;
    // slight chromatic offset
    float offs = 0.0015;
    vec4 sampleR = texture(Texture, uv + vec2(offs,0));
    vec4 sampleG = texture(Texture, uv);
    vec4 sampleB = texture(Texture, uv - vec2(offs,0));
    vec3 col;
    // Use sampled channels directly (assume texture was uploaded with correct format)
    col.r = sampleR.r;
    col.g = sampleG.g;
    col.b = sampleB.b;
    col *= mix(0.9, 1.05, lines);
    // vignette
    vec2 pos = uv - 0.5;
    float vig = 1.0 - dot(pos, pos) * 0.5;
    col *= clamp(vig, 0.0, 1.0);
    fragColor = vec4(col, 1.0);
}
";
                string fsSrc = path.ToLower().Contains("crt") ? fsCrt : fsSimple;
                vs = CompileShader(GlConsts.GL_VERTEX_SHADER, vsDebug);
                fs = CompileShader(GlConsts.GL_FRAGMENT_SHADER, fsSrc);
            }

            if (vs == 0 || fs == 0)
            {
                if (vs != 0) try { _gl.DeleteShader(vs); } catch { }
                if (fs != 0) try { _gl.DeleteShader(fs); } catch { }
                Debug.WriteLine($"[Pipeline] Shader compile failed for {path}");
                LastError = "Compilation failed for one or more shader stages.";
                SaveShaderLog(path, "error", LastError);
                return;
            }

            int prog = _gl.CreateProgram();
            _gl.AttachShader(prog, vs);
            _gl.AttachShader(prog, fs);

            // Bind common attribute names to fixed locations so the VBO layout matches shader expectations
            try
            {
                unsafe
                {
                    byte[] b0 = Encoding.ASCII.GetBytes("VertexCoord\0");
                    fixed (byte* p0 = b0) _gl.BindAttribLocation(prog, 0, (IntPtr)p0);
                    byte[] b1 = Encoding.ASCII.GetBytes("TexCoord\0");
                    fixed (byte* p1 = b1) _gl.BindAttribLocation(prog, 1, (IntPtr)p1);
                    byte[] b2 = Encoding.ASCII.GetBytes("COLOR\0");
                    fixed (byte* p2 = b2) _gl.BindAttribLocation(prog, 2, (IntPtr)p2);
                    // Also bind common alternate names used by simpler shaders
                    byte[] b3 = Encoding.ASCII.GetBytes("aPos\0");
                    fixed (byte* p3 = b3) _gl.BindAttribLocation(prog, 0, (IntPtr)p3);
                    byte[] b4 = Encoding.ASCII.GetBytes("aTex\0");
                    fixed (byte* p4 = b4) _gl.BindAttribLocation(prog, 1, (IntPtr)p4);
                }
            }
            catch { }

            _gl.LinkProgram(prog);

            try
            {
                unsafe
                {
                    int status = 0;
                    _gl.GetProgramiv(prog, GL_LINK_STATUS, &status);
                    if (status != 0)
                    {
                        _passes.Add(new ShaderPass { ProgramId = prog });
                        Debug.WriteLine($"[Pipeline] Shader linked and added: {path}");
                        LastError = null;
                    }
                    else
                    {
                        // Retrieve program info log
                        int logLen = 0;
                        _gl.GetProgramiv(prog, GL_INFO_LOG_LENGTH, &logLen);
                        string msg = "Program link failed but no info log available.";
                        if (logLen > 0)
                        {
                            var logBytes = new byte[logLen];
                            fixed (byte* pLog = logBytes)
                            {
                                int actual = 0;
                                try
                                {
                                    _gl.GetProgramInfoLog(prog, logLen, out actual, (void*)pLog);
                                    msg = Encoding.UTF8.GetString(logBytes, 0, Math.Max(0, actual));
                                }
                                catch (Exception ex)
                                {
                                    msg = $"Failed to retrieve program info log: {ex.Message}";
                                }
                            }
                        }
                        Debug.WriteLine($"[Pipeline] Program link error ({path}): {msg}");
                        LastError = msg;
                        SaveShaderLog(path, "link", msg);
                        _gl.DeleteProgram(prog);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pipeline] Exception during program link check: {ex.Message}");
                LastError = ex.ToString();
                SaveShaderLog(path, "link_exception", ex.ToString());
                try { _gl.DeleteProgram(prog); } catch { }
            }
            finally
            {
                try { _gl.DeleteShader(vs); } catch { }
                try { _gl.DeleteShader(fs); } catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Pipeline] Error loading shader file {path}: {ex.Message}");
            LastError = ex.ToString();
            SaveShaderLog(path, "load_exception", ex.ToString());
        }
    }

    private unsafe int CompileShader(int type, string source)
    {
        int shader = 0;
        try
        {
            shader = _gl.CreateShader(type);
            var bytes = Encoding.UTF8.GetBytes(source);
            fixed (byte* ptr = bytes)
            {
                sbyte* pStr = (sbyte*)ptr;
                sbyte** ppStr = &pStr;
                int len = bytes.Length;
                _gl.ShaderSource(shader, 1, (IntPtr)ppStr, (IntPtr)(&len));
            }
            _gl.CompileShader(shader);

            int success = 0;
            _gl.GetShaderiv(shader, GL_COMPILE_STATUS, &success);
            if (success == 0)
            {
                int logLen = 0;
                _gl.GetShaderiv(shader, GL_INFO_LOG_LENGTH, &logLen);
                string msg = "Shader compile failed but no info log available.";
                if (logLen > 0)
                {
                    var logBytes = new byte[logLen];
                    fixed (byte* pLog = logBytes)
                    {
                        int actual = 0;
                        try
                        {
                            _gl.GetShaderInfoLog(shader, logLen, out actual, (void*)pLog);
                            msg = Encoding.UTF8.GetString(logBytes, 0, Math.Max(0, actual));
                        }
                        catch (Exception ex)
                        {
                            msg = $"Failed to retrieve shader info log: {ex.Message}";
                        }
                    }
                }

                Debug.WriteLine($"[Pipeline] Compile Error (type {type}): {msg}");
                LastError = msg;
                SaveShaderLog("inline", "compile", msg);
                _gl.DeleteShader(shader);
                return 0;
            }

            return shader;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Pipeline] CompileShader exception: {ex.Message}");
            LastError = ex.ToString();
            SaveShaderLog("inline", "compile_exception", ex.ToString());
            if (shader != 0) try { _gl.DeleteShader(shader); } catch { }
            return 0;
        }
    }

    private static readonly bool _enableShaderLogging = false;

    private void SaveShaderLog(string shaderPath, string stage, string message)
    {
        // Disable logging to prevent memory accumulation from many log files
        // Set to true only for debugging specific shader issues
        if (!_enableShaderLogging) return;
        
        try
        {
            string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ShaderLogs");
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            string safeName = Path.GetFileNameWithoutExtension(shaderPath) ?? "shader";
            // Only keep one log per shader/stage combo to prevent accumulation
            string file = Path.Combine(baseDir, $"{safeName}_{stage}.log");
            File.WriteAllText(file, message);
            Debug.WriteLine($"[Pipeline] Shader log written: {file}");
        }
        catch { }
    }

    public void Process(int sourceTexture, int w, int h, int finalOutputFbo, int outputX, int outputY, int outputW, int outputH)
    {
        if (_passes.Count == 0) return;

        // Ensure intermediate buffers are ready if multi-pass
        if (_passes.Count > 1) EnsureIntermediateBuffers(w, h);

        int currentSource = sourceTexture;
        _frameCounter++;

        for (int i = 0; i < _passes.Count; i++)
        {
            var pass = _passes[i];
            bool isLastPass = (i == _passes.Count - 1);
            
            int targetFbo = isLastPass ? finalOutputFbo : _intermediateFbos[i % 2];
            int currentTargetX = isLastPass ? outputX : 0;
            int currentTargetY = isLastPass ? outputY : 0;
            int currentTargetW = isLastPass ? outputW : w;
            int currentTargetH = isLastPass ? outputH : h;

            _gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, targetFbo);
            _gl.Viewport(currentTargetX, currentTargetY, currentTargetW, currentTargetH);

            _gl.UseProgram(pass.ProgramId);

            // Set Uniforms
            SetUniform1i(pass.ProgramId, "FrameCount", _frameCounter);
            SetUniform1i(pass.ProgramId, "FrameDirection", _frameCounter % 2 == 0 ? 1 : -1);
            SetUniform1f(pass.ProgramId, "uBrightness", Brightness);
            SetUniform1f(pass.ProgramId, "uSaturation", Saturation);
            SetUniform4fv(pass.ProgramId, "uColorTint", ColorTint);
            SetUniformMatrix4fv(pass.ProgramId, "MVPMatrix", _mvpMatrixIdentity);
            SetUniform2f(pass.ProgramId, "TextureSize", w, h);
            SetUniform2f(pass.ProgramId, "InputSize", w, h);
            SetUniform2f(pass.ProgramId, "OutputSize", currentTargetW, currentTargetH);

            _gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            _gl.BindTexture(GlConsts.GL_TEXTURE_2D, currentSource);

            if (RequiresFrameHistory) _frameHistory.BindToShader(_gl, pass.ProgramId);

            unsafe
            {
                foreach (var sname in _samplerNames)
                {
                    byte[] name = Encoding.ASCII.GetBytes(sname + "\0");
                    fixed (byte* p = name)
                    {
                        int loc = _gl.GetUniformLocation(pass.ProgramId, (IntPtr)p);
                        if (loc != -1)
                        {
                            var ptr = _gl.GetProcAddress("glUniform1i");
                            if (ptr != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<int, int, void>)ptr)(loc, 0);
                        }
                    }
                }
            }

            EnsureQuadVbo();
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _quadVbo);
            _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, 16, IntPtr.Zero);
            _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, 16, (IntPtr)8);

            _gl.DrawArrays(GL_TRIANGLE_STRIP, 0, 4);

            // Ping-pong: current target becomes next source
            if (!isLastPass) currentSource = _intermediateTextures[i % 2];
        }

        _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
    }

    private unsafe void EnsureIntermediateBuffers(int w, int h)
    {
        if (_intermediateFbos.Length == 2 && _lastW == w && _lastH == h) return;

        CleanupIntermediateBuffers();

        _intermediateFbos = new int[2];
        _intermediateTextures = new int[2];
        _lastW = w;
        _lastH = h;

        for (int i = 0; i < 2; i++)
        {
            _intermediateFbos[i] = _gl.GenFramebuffer();
            _intermediateTextures[i] = _gl.GenTexture();
            _gl.BindTexture(GlConsts.GL_TEXTURE_2D, _intermediateTextures[i]);
            _gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA, w, h, 0, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
            _gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
            _gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
            
            _gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, _intermediateFbos[i]);
            _gl.FramebufferTexture2D(GlConsts.GL_FRAMEBUFFER, GlConsts.GL_COLOR_ATTACHMENT0, GlConsts.GL_TEXTURE_2D, _intermediateTextures[i], 0);
        }
        _gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, 0);
    }

    private void CleanupIntermediateBuffers()
    {
        foreach (var fbo in _intermediateFbos) if (fbo != 0) _gl.DeleteFramebuffer(fbo);
        foreach (var tex in _intermediateTextures) if (tex != 0) _gl.DeleteTexture(tex);
        _intermediateFbos = Array.Empty<int>();
        _intermediateTextures = Array.Empty<int>();
    }

    public void CaptureFrameToHistory(int x, int y, int w, int h, int outputFbo)
    {
        if (RequiresFrameHistory && _frameHistory.IsInitialized)
        {
            _frameHistory.CaptureFrame(outputFbo, x, y, w, h);
        }
    }

    private unsafe void EnsureQuadVbo()
    {
        if (_quadVbo != 0) return;
        float[] vertices = { -1f, 1f, 0f, 0f, -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, 1f, -1f, 1f, 1f };
        _quadVbo = _gl.GenBuffer();
        int size = vertices.Length * sizeof(float);
        fixed (float* p = vertices)
        {
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _quadVbo);
            _gl.BufferData(GlConsts.GL_ARRAY_BUFFER, size, (IntPtr)p, 0x88E4); // GL_STATIC_DRAW
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
        }
    }

    private unsafe void SetUniform2f(int prog, string name, float x, float y)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            int loc = _gl.GetUniformLocation(prog, (IntPtr)p);
            if (loc != -1)
            {
                var ptr = _gl.GetProcAddress("glUniform2f");
                if (ptr != IntPtr.Zero)
                    ((delegate* unmanaged[Stdcall]<int, float, float, void>)ptr)(loc, x, y);
            }
        }
    }

    private unsafe void SetUniform1i(int prog, string name, int val)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* pName = bytes)
        {
            int loc = _gl.GetUniformLocation(prog, (IntPtr)pName);
            if (loc == -1) return;
            var ptr = _gl.GetProcAddress("glUniform1i");
            if (ptr == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<int, int, void>)ptr)(loc, val);
        }
    }

    private unsafe void SetUniform1f(int prog, string name, float val)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* pName = bytes)
        {
            int loc = _gl.GetUniformLocation(prog, (IntPtr)pName);
            if (loc == -1) return;
            var ptr = _gl.GetProcAddress("glUniform1f");
            if (ptr == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<int, float, void>)ptr)(loc, val);
        }
    }

    private unsafe void SetUniform4fv(int prog, string name, float[] vals)
    {
        if (vals == null || vals.Length < 4) return;
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* pName = bytes)
        {
            int loc = _gl.GetUniformLocation(prog, (IntPtr)pName);
            if (loc == -1) return;
            var ptr = _gl.GetProcAddress("glUniform4fv");
            if (ptr == IntPtr.Zero) return;
            fixed (float* pVals = vals)
            {
                ((delegate* unmanaged[Stdcall]<int, int, float*, void>)ptr)(loc, 1, pVals);
            }
        }
    }

    private unsafe void SetUniformMatrix4fv(int prog, string name, float[] mat)
    {
        if (mat == null || mat.Length < 16) return;
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* pName = bytes)
        {
            int loc = _gl.GetUniformLocation(prog, (IntPtr)pName);
            if (loc == -1) return;
            var ptr = _gl.GetProcAddress("glUniformMatrix4fv");
            if (ptr == IntPtr.Zero) return;
            fixed (float* pMat = mat)
            {
                ((delegate* unmanaged[Stdcall]<int, int, byte, float*, void>)ptr)(loc, 1, 0, pMat);
            }
        }
    }

    public void Dispose()
    {
        _frameHistory?.Dispose();
        CleanupIntermediateBuffers();
        foreach (var p in _passes) if (p.ProgramId != 0) _gl.DeleteProgram(p.ProgramId);
        _passes.Clear();
        try { if (_quadVbo != 0) _gl.DeleteBuffer(_quadVbo); } catch { }
    }
}

public class ShaderPass { public int ProgramId { get; set; } }
