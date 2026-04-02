using Avalonia.OpenGL;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AES_Emulation.Windows.ShaderHandling;

/// <summary>
/// Shader pipeline for loading and executing HLSL shaders.
/// Provides Direct3D/DirectX shader compilation and execution.
/// </summary>
public class HlslShaderPipeline : IDisposable
{
    private readonly GlInterface _gl;
    private int _program = 0;
    private int _quadVbo = 0;
    private int _frameCounter = 0;
    private string _shaderVersionHeader = "";
    private bool _isEs = false;

    private const int GL_TRIANGLE_STRIP = 0x0005;
    private const int GL_COMPILE_STATUS = 0x8B81;
    private const int GL_LINK_STATUS = 0x8B82;
    private const int GL_INFO_LOG_LENGTH = 0x8B84;

    // Static cached arrays to avoid per-frame allocations
    private static readonly float[] _mvpMatrixIdentity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    private static readonly byte[] _frameCountBytes = Encoding.ASCII.GetBytes("FrameCount\0");
    private static readonly byte[] _frameDirectionBytes = Encoding.ASCII.GetBytes("FrameDirection\0");
    private static readonly byte[] _mvpMatrixBytes = Encoding.ASCII.GetBytes("MVPMatrix\0");
    private static readonly byte[] _textureSizeBytes = Encoding.ASCII.GetBytes("TextureSize\0");
    private static readonly byte[] _inputSizeBytes = Encoding.ASCII.GetBytes("InputSize\0");
    private static readonly byte[] _outputSizeBytes = Encoding.ASCII.GetBytes("OutputSize\0");

    // Cached sampler name bytes
    private static readonly byte[] _uTexBytes = Encoding.ASCII.GetBytes("uTex\0");
    private static readonly byte[] _texBytes = Encoding.ASCII.GetBytes("tex\0");
    private static readonly byte[] _textureBytes = Encoding.ASCII.GetBytes("Texture\0");
    private static readonly byte[] _sourceBytes = Encoding.ASCII.GetBytes("source\0");
    private static readonly byte[] _sourceCapitalBytes = Encoding.ASCII.GetBytes("Source\0");
    private static readonly byte[] _uTextureBytes = Encoding.ASCII.GetBytes("uTexture\0");

    // Static cached attribute name bytes to avoid allocations
    private static readonly byte[] _vertexCoordBytes = Encoding.ASCII.GetBytes("VertexCoord\0");
    private static readonly byte[] _texCoordBytes = Encoding.ASCII.GetBytes("TexCoord\0");

    public bool HasActiveShader => _program != 0;
    public string? LastError { get; private set; }

    public HlslShaderPipeline(GlInterface gl)
    {
        _gl = gl;
        var info = GlHelper.GetShaderVersion(gl);
        _shaderVersionHeader = info.Item1;
        _isEs = info.Item2;
        
        Debug.WriteLine("[HLSL] HlslShaderPipeline initialized");
    }

    /// <summary>
    /// Load an HLSL shader file (.hlsl)
    /// </summary>
    public void LoadShader(string path)
    {
        LastError = null;
        Dispose();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            LastError = "Shader file not found";
            return;
        }

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".hlsl")
            {
                LastError = $"Invalid shader format: {ext}. Expected .hlsl";
                return;
            }

            string source = File.ReadAllText(path);

            // For now, treat HLSL as GLSL-compatible (with some preprocessing)
            // In a real implementation, you would use the HLSL compiler (fxc.exe or dxc.exe)
            source = PreprocessHlslSource(source);

            CompileShaderProgram(source, path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HLSL] Error loading shader {path}: {ex.Message}");
            LastError = ex.ToString();
        }
    }

    /// <summary>
    /// Convert HLSL to compatible GLSL syntax
    /// Optimized for minimal allocations to prevent OOM
    /// </summary>
    private string PreprocessHlslSource(string source)
    {
        // Use char array span approach to avoid substring allocations
        var chars = source.AsSpan();
        var sb = new StringBuilder(source.Length);
        
        for (int i = 0; i < chars.Length; i++)
        {
            // Check single character matches first (fastest path)
            if (chars[i] == '#' && i + 1 < chars.Length && chars[i + 1] == 'p')
            {
                if (chars.Slice(i, Math.Min(12, chars.Length - i)).SequenceEqual("#pragma pack"))
                {
                    sb.Append("//");
                    i += 11;
                    continue;
                }
            }
            
            if (chars[i] == 'T' && i + 1 < chars.Length && chars[i + 1] == 'e')
            {
                if (chars.Slice(i, Math.Min(9, chars.Length - i)).SequenceEqual("Texture2D"))
                {
                    sb.Append("sampler2D");
                    i += 8;
                    continue;
                }
            }
            
            if (chars[i] == 'S' && i + 1 < chars.Length)
            {
                if (chars.Slice(i, Math.Min(12, chars.Length - i)).SequenceEqual("SamplerState"))
                {
                    sb.Append("//");
                    i += 11;
                    continue;
                }
            }
            
            if (chars[i] == 'c' && chars.Slice(i, Math.Min(7, chars.Length - i)).SequenceEqual("cbuffer"))
            {
                sb.Append("//");
                i += 6;
                continue;
            }
            
            if (chars[i] == 'r' && chars.Slice(i, Math.Min(9, chars.Length - i)).SequenceEqual("register("))
            {
                sb.Append("/*register(");
                i += 8;
                continue;
            }
            
            if (chars[i] == 'f' && i + 5 < chars.Length)
            {
                if (chars.Slice(i, 6).SequenceEqual("float4"))
                {
                    sb.Append("vec4");
                    i += 5;
                    continue;
                }
                if (i + 5 < chars.Length && chars.Slice(i, 6).SequenceEqual("float3"))
                {
                    sb.Append("vec3");
                    i += 5;
                    continue;
                }
                if (i + 5 < chars.Length && chars.Slice(i, 6).SequenceEqual("float2"))
                {
                    sb.Append("vec2");
                    i += 5;
                    continue;
                }
            }
            
            if (chars[i] == 'u' && i + 4 < chars.Length)
            {
                if (chars.Slice(i, 5).SequenceEqual("uint4"))
                {
                    sb.Append("uvec4");
                    i += 4;
                    continue;
                }
                if (i + 4 < chars.Length && chars.Slice(i, 5).SequenceEqual("uint3"))
                {
                    sb.Append("uvec3");
                    i += 4;
                    continue;
                }
                if (i + 4 < chars.Length && chars.Slice(i, 5).SequenceEqual("uint2"))
                {
                    sb.Append("uvec2");
                    i += 4;
                    continue;
                }
            }
            
            if (chars[i] == 'i' && i + 3 < chars.Length)
            {
                if (chars.Slice(i, 4).SequenceEqual("int4"))
                {
                    sb.Append("ivec4");
                    i += 3;
                    continue;
                }
                if (i + 3 < chars.Length && chars.Slice(i, 4).SequenceEqual("int3"))
                {
                    sb.Append("ivec3");
                    i += 3;
                    continue;
                }
                if (i + 3 < chars.Length && chars.Slice(i, 4).SequenceEqual("int2"))
                {
                    sb.Append("ivec2");
                    i += 3;
                    continue;
                }
            }
            
            if (chars[i] == 'S' && chars.Slice(i, Math.Min(12, chars.Length - i)).SequenceEqual("SampleLevel("))
            {
                sb.Append("textureLod(");
                i += 11;
                continue;
            }
            
            if (chars[i] == 'm' && chars.Slice(i, Math.Min(4, chars.Length - i)).SequenceEqual("mul("))
            {
                sb.Append("mul_hlsl(");
                i += 3;
                continue;
            }
            
            sb.Append(chars[i]);
        }
        
        return sb.ToString();
    }

    private void CompileShaderProgram(string source, string path)
    {
        try
        {
            string fullHeader = _shaderVersionHeader + "\n" + (_isEs ? "precision mediump float;\n" : "");

            // Extract shaders (these use substrings which is acceptable - only done at load time)
            string vertexSource = fullHeader + ExtractVertexShader(source);
            string fragmentSource = fullHeader + ExtractFragmentShader(source);

            int vs = CompileShader(GlConsts.GL_VERTEX_SHADER, vertexSource);
            int fs = CompileShader(GlConsts.GL_FRAGMENT_SHADER, fragmentSource);

            if (vs == 0 || fs == 0)
            {
                if (vs != 0) try { _gl.DeleteShader(vs); } catch { }
                if (fs != 0) try { _gl.DeleteShader(fs); } catch { }
                LastError = "Shader compilation failed";
                return;
            }

            _program = _gl.CreateProgram();
            _gl.AttachShader(_program, vs);
            _gl.AttachShader(_program, fs);

            // Bind common attribute names
            unsafe
            {
                fixed (byte* p0 = _vertexCoordBytes) 
                    _gl.BindAttribLocation(_program, 0, (IntPtr)p0);
                fixed (byte* p1 = _texCoordBytes) 
                    _gl.BindAttribLocation(_program, 1, (IntPtr)p1);
            }

            _gl.LinkProgram(_program);

            unsafe
            {
                int status = 0;
                _gl.GetProgramiv(_program, GL_LINK_STATUS, &status);
                if (status != 0)
                {
                    Debug.WriteLine($"[HLSL] Shader linked successfully: {path}");
                    LastError = null;
                }
                else
                {
                    int logLen = 0;
                    _gl.GetProgramiv(_program, GL_INFO_LOG_LENGTH, &logLen);
                    string msg = "Program link failed";
                    if (logLen > 0)
                    {
                        var logBytes = new byte[logLen];
                        fixed (byte* pLog = logBytes)
                        {
                            int actual = 0;
                            _gl.GetProgramInfoLog(_program, logLen, out actual, (void*)pLog);
                            msg = Encoding.UTF8.GetString(logBytes, 0, Math.Max(0, actual));
                        }
                    }
                    Debug.WriteLine($"[HLSL] Link error: {msg}");
                    LastError = msg;
                    _gl.DeleteProgram(_program);
                    _program = 0;
                }
            }

            try { _gl.DeleteShader(vs); } catch { }
            try { _gl.DeleteShader(fs); } catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HLSL] Compilation exception: {ex.Message}");
            LastError = ex.ToString();
        }
    }

    private string ExtractVertexShader(string source)
    {
        // Look for vertex shader function (main or VS entry point)
        if (source.Contains("[vertexshader]", StringComparison.OrdinalIgnoreCase))
        {
            int idx = source.IndexOf("[vertexshader]", StringComparison.OrdinalIgnoreCase);
            return source[idx..];
        }
        // Assume first function is vertex shader if no markers
        return $@"
            layout(location = 0) in vec2 VertexCoord;
            layout(location = 1) in vec2 TexCoord;
            out vec2 vTex;
            void main() {{
                vTex = TexCoord;
                gl_Position = vec4(VertexCoord, 0.0, 1.0);
            }}
        ";
    }

    private string ExtractFragmentShader(string source)
    {
        // Look for pixel/fragment shader function
        if (source.Contains("[pixelshader]", StringComparison.OrdinalIgnoreCase))
        {
            int idx = source.IndexOf("[pixelshader]", StringComparison.OrdinalIgnoreCase);
            return source[idx..];
        }
        if (source.Contains("[fragmentshader]", StringComparison.OrdinalIgnoreCase))
        {
            int idx = source.IndexOf("[fragmentshader]", StringComparison.OrdinalIgnoreCase);
            return source[idx..];
        }
        // Assume last function is fragment shader
        return source;
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
                string msg = "Compile failed";
                if (logLen > 0)
                {
                    var logBytes = new byte[logLen];
                    fixed (byte* pLog = logBytes)
                    {
                        int actual = 0;
                        _gl.GetShaderInfoLog(shader, logLen, out actual, (void*)pLog);
                        msg = Encoding.UTF8.GetString(logBytes, 0, Math.Max(0, actual));
                    }
                }
                Debug.WriteLine($"[HLSL] Compile Error: {msg}");
                LastError = msg;
                _gl.DeleteShader(shader);
                return 0;
            }

            return shader;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HLSL] CompileShader exception: {ex.Message}");
            LastError = ex.ToString();
            if (shader != 0) try { _gl.DeleteShader(shader); } catch { }
            return 0;
        }
    }

    public void Process(int sourceTexture, int w, int h, int outputFbo, int outputX, int outputY, int outputW, int outputH)
    {
        if (_program == 0) return;

        _gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, outputFbo);
        _gl.Viewport(outputX, outputY, outputW, outputH);
        _gl.UseProgram(_program);

        // Set uniforms using cached byte arrays
        _frameCounter++;
        SetUniform1i(_frameCountBytes, _frameCounter);
        SetUniform1i(_frameDirectionBytes, _frameCounter % 2 == 0 ? 1 : -1);

        SetUniformMatrix4fv(_mvpMatrixBytes, _mvpMatrixIdentity);
        SetUniform2f(_textureSizeBytes, w, h);
        SetUniform2f(_inputSizeBytes, w, h);
        SetUniform2f(_outputSizeBytes, outputW, outputH);

        // Bind source texture
        _gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        _gl.BindTexture(GlConsts.GL_TEXTURE_2D, sourceTexture);

        // Set sampler uniforms using cached byte arrays (no allocations)
        SetSamplerUniform(_uTexBytes, 0);
        SetSamplerUniform(_texBytes, 0);
        SetSamplerUniform(_textureBytes, 0);
        SetSamplerUniform(_sourceBytes, 0);
        SetSamplerUniform(_sourceCapitalBytes, 0);
        SetSamplerUniform(_uTextureBytes, 0);

        // Draw fullscreen quad
        EnsureQuadVbo();
        _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _quadVbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, 16, IntPtr.Zero);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, 16, (IntPtr)8);

        _gl.DrawArrays(GL_TRIANGLE_STRIP, 0, 4);

        _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
    }

    private unsafe void SetSamplerUniform(byte[] nameBytes, int textureUnit)
    {
        fixed (byte* p = nameBytes)
        {
            int loc = _gl.GetUniformLocation(_program, (IntPtr)p);
            if (loc != -1)
            {
                var ptr = _gl.GetProcAddress("glUniform1i");
                if (ptr != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<int, int, void>)ptr)(loc, textureUnit);
            }
        }
    }

    private unsafe void SetUniform2f(byte[] nameBytes, float x, float y)
    {
        fixed (byte* p = nameBytes)
        {
            int loc = _gl.GetUniformLocation(_program, (IntPtr)p);
            if (loc != -1)
            {
                var ptr = _gl.GetProcAddress("glUniform2f");
                if (ptr != IntPtr.Zero)
                    ((delegate* unmanaged[Stdcall]<int, float, float, void>)ptr)(loc, x, y);
            }
        }
    }

    private unsafe void SetUniform1i(byte[] nameBytes, int val)
    {
        fixed (byte* pName = nameBytes)
        {
            int loc = _gl.GetUniformLocation(_program, (IntPtr)pName);
            if (loc == -1) return;
            var ptr = _gl.GetProcAddress("glUniform1i");
            if (ptr == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<int, int, void>)ptr)(loc, val);
        }
    }

    private unsafe void SetUniformMatrix4fv(byte[] nameBytes, float[] mat)
    {
        if (mat == null || mat.Length < 16) return;
        fixed (byte* pName = nameBytes)
        {
            int loc = _gl.GetUniformLocation(_program, (IntPtr)pName);
            if (loc == -1) return;
            var ptr = _gl.GetProcAddress("glUniformMatrix4fv");
            if (ptr == IntPtr.Zero) return;
            fixed (float* pMat = mat)
            {
                ((delegate* unmanaged[Stdcall]<int, int, byte, float*, void>)ptr)(loc, 1, 0, pMat);
            }
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
            _gl.BufferData(GlConsts.GL_ARRAY_BUFFER, size, (IntPtr)p, 0x88E4);
            _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
        }
    }

    public void Dispose()
    {
        if (_program != 0) { try { _gl.DeleteProgram(_program); } catch { } _program = 0; }
        if (_quadVbo != 0) { try { _gl.DeleteBuffer(_quadVbo); } catch { } _quadVbo = 0; }
        Debug.WriteLine("[HLSL] HlslShaderPipeline disposed");
    }
}
