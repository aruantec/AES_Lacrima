using Avalonia.OpenGL;
using System;
using System.Diagnostics;

namespace AES_Emulation.Windows.ShaderHandling;

/// <summary>
/// Manages a circular buffer of frame textures for temporal shader effects.
/// Supports motion blur and other effects requiring frame history.
/// </summary>
public class FrameHistoryManager : IDisposable
{
    private const int MAX_HISTORY_FRAMES = 8;
    private int[] _historyTextures = new int[MAX_HISTORY_FRAMES];
    private int _currentIndex = 0;
    private int _lastWidth = 0;
    private int _lastHeight = 0;
    private bool _initialized = false;

    private readonly GlInterface _gl;

    // GL constants
    private const int GL_TEXTURE_2D = 0x0DE1;
    private const int GL_TEXTURE_MIN_FILTER = 0x2801;
    private const int GL_TEXTURE_MAG_FILTER = 0x2800;
    private const int GL_LINEAR = 0x2601;
    private const int GL_RGBA = 0x1908;
    private const int GL_UNSIGNED_BYTE = 0x1401;
    private const int GL_FRAMEBUFFER = 0x8D40;
    private const int GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_COLOR_BUFFER_BIT = 0x4000u;
    private const int GL_READ_FRAMEBUFFER = 0x8CA8;
    private const int GL_DRAW_FRAMEBUFFER = 0x8CA9;

    public bool IsInitialized => _initialized;
    public int FrameCount => MAX_HISTORY_FRAMES;

    public FrameHistoryManager(GlInterface gl)
    {
        _gl = gl;
        Debug.WriteLine("[FrameHistory] Manager created");
    }

    /// <summary>
    /// Initialize or reinitialize frame history textures for the given dimensions.
    /// Call this when resolution changes or on first use.
    /// </summary>
    public void Initialize(int width, int height)
    {
        if (_initialized && _lastWidth == width && _lastHeight == height)
            return;

        Dispose(); // Clean up old textures

        _lastWidth = width;
        _lastHeight = height;
        _currentIndex = 0;

        for (int i = 0; i < MAX_HISTORY_FRAMES; i++)
        {
            _historyTextures[i] = _gl.GenTexture();
            _gl.BindTexture(GL_TEXTURE_2D, _historyTextures[i]);
            
            _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
            
            // Allocate storage (don't fill with data yet)
            _gl.TexImage2D(GL_TEXTURE_2D, 0, (int)GlConsts.GL_RGBA, width, height, 0, 
                GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
        }

        _gl.BindTexture(GL_TEXTURE_2D, 0);
        _initialized = true;
        Debug.WriteLine($"[FrameHistory] Initialized {MAX_HISTORY_FRAMES} textures at {width}x{height}");
    }

    /// <summary>
    /// Capture the current rendered frame to the history buffer.
    /// Must be called after rendering is complete.
    /// </summary>
    public void CaptureFrame(int sourceFramebuffer, int x, int y, int width, int height)
    {
        if (!_initialized || width != _lastWidth || height != _lastHeight)
            Initialize(width, height);

        // Move to next history slot (circular buffer)
        _currentIndex = (_currentIndex + 1) % MAX_HISTORY_FRAMES;
        int targetTexture = _historyTextures[_currentIndex];

        // Create temporary framebuffer for reading
        int readFbo = 0;
        try
        {
            var glGenFramebuffers = _gl.GetProcAddress("glGenFramebuffers");
            if (glGenFramebuffers == IntPtr.Zero) return;

            unsafe
            {
                int tempFbo = 0;
                ((delegate* unmanaged[Stdcall]<int, int*, void>)glGenFramebuffers)(1, &tempFbo);
                readFbo = tempFbo;
            }

            if (readFbo == 0) return;

            var glBindFramebuffer = _gl.GetProcAddress("glBindFramebuffer");
            var glFramebufferTexture2D = _gl.GetProcAddress("glFramebufferTexture2D");
            var glBlitFramebuffer = _gl.GetProcAddress("glBlitFramebuffer");
            var glDeleteFramebuffers = _gl.GetProcAddress("glDeleteFramebuffers");

            if (glBindFramebuffer == IntPtr.Zero || glFramebufferTexture2D == IntPtr.Zero || glBlitFramebuffer == IntPtr.Zero)
                return;

            unsafe
            {
                // Bind source framebuffer for reading
                ((delegate* unmanaged[Stdcall]<int, int, void>)glBindFramebuffer)(GL_READ_FRAMEBUFFER, sourceFramebuffer);

                // Create destination framebuffer
                ((delegate* unmanaged[Stdcall]<int, int, void>)glBindFramebuffer)(GL_DRAW_FRAMEBUFFER, readFbo);
                ((delegate* unmanaged[Stdcall]<int, int, int, int, int, void>)glFramebufferTexture2D)
                    (GL_DRAW_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, targetTexture, 0);

                // Blit the frame from the specified sub-rectangle
                ((delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, uint, int, void>)glBlitFramebuffer)
                    (x, y, x + width, y + height, 0, 0, width, height, GL_COLOR_BUFFER_BIT, GL_LINEAR);

                // Cleanup
                ((delegate* unmanaged[Stdcall]<int, int, void>)glBindFramebuffer)(GL_READ_FRAMEBUFFER, 0);
                ((delegate* unmanaged[Stdcall]<int, int, void>)glBindFramebuffer)(GL_DRAW_FRAMEBUFFER, 0);

                if (glDeleteFramebuffers != IntPtr.Zero)
                {
                    int toDelete = readFbo;
                    ((delegate* unmanaged[Stdcall]<int, int*, void>)glDeleteFramebuffers)(1, &toDelete);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameHistory] Error capturing frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Bind frame history textures to shader units.
    /// Maps PrevTexture, Prev1Texture, ... Prev6Texture to texture units 1-7.
    /// </summary>
    public void BindToShader(GlInterface gl, int programId)
    {
        if (!_initialized) return;

        string[] prevTextureNames = 
        {
            "PrevTexture", "Prev1Texture", "Prev2Texture", "Prev3Texture",
            "Prev4Texture", "Prev5Texture", "Prev6Texture"
        };

        for (int i = 0; i < prevTextureNames.Length && i < MAX_HISTORY_FRAMES - 1; i++)
        {
            int textureUnit = i + 1; // Unit 0 is current frame
            int historyIndex = (_currentIndex - i - 1 + MAX_HISTORY_FRAMES * 100) % MAX_HISTORY_FRAMES;
            int texId = _historyTextures[historyIndex];

            gl.ActiveTexture(GlConsts.GL_TEXTURE0 + textureUnit);
            gl.BindTexture(GL_TEXTURE_2D, texId);

            // Set uniform
            SetUniformSampler(gl, programId, prevTextureNames[i], textureUnit);
        }
    }

    private unsafe void SetUniformSampler(GlInterface gl, int programId, string samplerName, int textureUnit)
    {
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(samplerName + "\0");
        fixed (byte* pName = nameBytes)
        {
            int loc = gl.GetUniformLocation(programId, (IntPtr)pName);
            if (loc != -1)
            {
                var glUniform1i = gl.GetProcAddress("glUniform1i");
                if (glUniform1i != IntPtr.Zero)
                {
                    ((delegate* unmanaged[Stdcall]<int, int, void>)glUniform1i)(loc, textureUnit);
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_initialized) return;

        for (int i = 0; i < MAX_HISTORY_FRAMES; i++)
        {
            if (_historyTextures[i] != 0)
            {
                try { _gl.DeleteTexture(_historyTextures[i]); }
                catch { }
                _historyTextures[i] = 0;
            }
        }

        _initialized = false;
        Debug.WriteLine("[FrameHistory] Manager disposed");
    }
}
