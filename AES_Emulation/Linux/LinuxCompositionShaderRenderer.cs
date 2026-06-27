using System;
using AES_Controls.EmuGrabbing.ShaderHandling;
using AES_Core.Logging;
using Avalonia.Media;
using Avalonia.OpenGL;
using log4net;
using SkiaSharp;

namespace AES_Emulation.Linux;

/// <summary>
/// Applies RetroArch-style GLSL presets to the Linux in-tree composition capture path via OpenGL + Skia.
/// The UI selects Shaders/hlsl/*.hlsl (same as Windows); this renderer executes the paired glsl preset.
/// </summary>
internal sealed class LinuxCompositionShaderRenderer : IDisposable
{
    private static readonly ILog Log = LogHelper.For<LinuxCompositionShaderRenderer>();
    private const int GlUnpackAlignment = 0x0CF5;
    private const int GlUnpackRowLength = 0x0CF2;
    private const int GlScissorTest = 0x0C11;
    private const int GlDepthTest = 0x0B71;
    private const int GlStencilTest = 0x0B90;
    private const int GlCullFace = 0x0B44;
    private const int GlRgba8 = 0x8058;
    private const uint GlBgra = 0x80E1;

    private GlInterface? _gl;
    private SlangShaderPipeline? _slangPipeline;
    private string? _loadedShaderPath;
    private int _captureTextureId;
    private int _intermediateTextureId;
    private int _intermediateFbo;
    private int _texWidth;
    private int _texHeight;
    private uint _uploadGlFormat = GlBgra;
    private IntPtr _glTexSubImage2DPtr;
    private IntPtr _glPixelStoreiPtr;

    public bool HasActiveShader => _slangPipeline?.HasActiveShader == true;

    public void SetShaderPath(GlInterface gl, string? shaderPath)
    {
        _gl = gl;
        var resolved = LinuxCompositionShaderPaths.ResolvePresetPath(shaderPath);
        if (string.Equals(_loadedShaderPath, resolved, StringComparison.OrdinalIgnoreCase))
            return;

        _slangPipeline?.Dispose();
        _slangPipeline = null;
        _loadedShaderPath = resolved;

        if (string.IsNullOrWhiteSpace(resolved))
            return;

        try
        {
            _slangPipeline = new SlangShaderPipeline(gl);
            _slangPipeline.LoadShaderPreset(resolved);
            if (_slangPipeline.HasActiveShader)
            {
                var loadNote = _slangPipeline.LastError ?? "ok";
                if (_slangPipeline.UsedPassthroughFallback ||
                    loadNote.Contains("passthrough", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"Linux composition shader '{resolved}' is running compatibility passthrough: {loadNote}");
                }
                return;
            }

            Log.Warn($"Linux composition shader preset loaded no passes: '{resolved}'.");
            _slangPipeline.Dispose();
            _slangPipeline = null;
            _loadedShaderPath = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Linux composition shader failed to load '{resolved}'.", ex);
            _slangPipeline?.Dispose();
            _slangPipeline = null;
            _loadedShaderPath = null;
        }
    }

    public bool TryDraw(
        SKCanvas canvas,
        GRContext grContext,
        GlInterface gl,
        SKBitmap frame,
        SKRect destRect,
        float brightness,
        float saturation,
        Color tint,
        int cropLeft = 0,
        int cropRight = 0)
    {
        if (!HasActiveShader)
            return false;

        _gl ??= gl;
        var uploadFormat = frame.ColorType == SKColorType.Rgba8888 ? (uint)GlConsts.GL_RGBA : GlBgra;
        EnsureGlResources(gl, frame.Width, frame.Height, uploadFormat);
        EnsureGlProcs(gl);

        if (_glTexSubImage2DPtr == IntPtr.Zero)
            return false;

        // Skia leaves GL scissor enabled after canvas operations; raw shader draws would only
        // fill the clipped region (thin horizontal strip). WgcCaptureControl disables this too.
        PrepareExternalGlState(gl);

        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _captureTextureId);
        ConfigureUnpackState(frame);

        unsafe
        {
            var texSub = (delegate* unmanaged<int, int, int, int, int, int, uint, int, IntPtr, void>)_glTexSubImage2DPtr;
            texSub(
                GlConsts.GL_TEXTURE_2D,
                0,
                0,
                0,
                frame.Width,
                frame.Height,
                uploadFormat,
                GlConsts.GL_UNSIGNED_BYTE,
                frame.GetPixels());
        }

        var tintArray = new[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, tint.A / 255f };
        _slangPipeline!.Brightness = brightness;
        _slangPipeline.Saturation = saturation;
        _slangPipeline.ColorTint = tintArray;

        _slangPipeline.Process(_captureTextureId, frame.Width, frame.Height, _intermediateFbo, 0, 0, frame.Width, frame.Height);
        _slangPipeline.CaptureFrameToHistory(0, 0, frame.Width, frame.Height, _intermediateFbo);

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, 0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);
        gl.UseProgram(0);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);

        grContext.ResetContext();

        var glInfo = new GRGlTextureInfo
        {
            Id = (uint)_intermediateTextureId,
            Target = GlConsts.GL_TEXTURE_2D,
            Format = GlRgba8
        };

        using var backendTexture = new GRBackendTexture(frame.Width, frame.Height, false, glInfo);
        // EGL/GL FBOs use bottom-left origin; TopLeft flips the image vertically on Linux.
        using var image = SKImage.FromTexture(grContext, backendTexture, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        if (image == null)
            return false;

        var srcRect = new SKRect(
            Math.Clamp(cropLeft, 0, Math.Max(0, frame.Width - 1)),
            0,
            Math.Max(cropLeft + 1, frame.Width - Math.Clamp(cropRight, 0, Math.Max(0, frame.Width - 1))),
            frame.Height);
        canvas.DrawImage(image, srcRect, destRect);
        return true;
    }

    private static void PrepareExternalGlState(GlInterface gl)
    {
        gl.Disable(GlScissorTest);
        gl.Disable(GlDepthTest);
        gl.Disable(GlStencilTest);
        gl.Disable(GlCullFace);
    }

    private void ConfigureUnpackState(SKBitmap frame)
    {
        SetPixelStore(GlUnpackAlignment, 1);

        var rowPixels = frame.RowBytes / 4;
        if (rowPixels != frame.Width)
            SetPixelStore(GlUnpackRowLength, rowPixels);
    }

    private void SetPixelStore(int parameter, int value)
    {
        if (_glPixelStoreiPtr == IntPtr.Zero)
            return;

        unsafe
        {
            var pixelStore = (delegate* unmanaged<int, int, void>)_glPixelStoreiPtr;
            pixelStore(parameter, value);
        }
    }

    private void EnsureGlProcs(GlInterface gl)
    {
        if (_glTexSubImage2DPtr == IntPtr.Zero)
            _glTexSubImage2DPtr = gl.GetProcAddress("glTexSubImage2D");
        if (_glPixelStoreiPtr == IntPtr.Zero)
            _glPixelStoreiPtr = gl.GetProcAddress("glPixelStorei");
    }

    private void EnsureGlResources(GlInterface gl, int w, int h, uint uploadFormat)
    {
        if (_captureTextureId != 0 && _texWidth == w && _texHeight == h && _uploadGlFormat == uploadFormat)
            return;

        if (_captureTextureId != 0)
            gl.DeleteTexture(_captureTextureId);
        if (_intermediateTextureId != 0)
            gl.DeleteTexture(_intermediateTextureId);
        if (_intermediateFbo != 0)
            gl.DeleteFramebuffer(_intermediateFbo);

        _uploadGlFormat = uploadFormat;

        _captureTextureId = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _captureTextureId);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA, w, h, 0, (int)uploadFormat, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);

        _intermediateFbo = gl.GenFramebuffer();
        _intermediateTextureId = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _intermediateTextureId);
        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA, w, h, 0, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, _intermediateFbo);
        gl.FramebufferTexture2D(GlConsts.GL_FRAMEBUFFER, GlConsts.GL_COLOR_ATTACHMENT0, GlConsts.GL_TEXTURE_2D, _intermediateTextureId, 0);

        _texWidth = w;
        _texHeight = h;
    }

    public void Dispose()
    {
        if (_gl != null)
        {
            if (_captureTextureId != 0)
                _gl.DeleteTexture(_captureTextureId);
            if (_intermediateTextureId != 0)
                _gl.DeleteTexture(_intermediateTextureId);
            if (_intermediateFbo != 0)
                _gl.DeleteFramebuffer(_intermediateFbo);
        }

        _captureTextureId = 0;
        _intermediateTextureId = 0;
        _intermediateFbo = 0;
        _texWidth = 0;
        _texHeight = 0;
        _uploadGlFormat = GlBgra;
        _glTexSubImage2DPtr = IntPtr.Zero;
        _glPixelStoreiPtr = IntPtr.Zero;

        _slangPipeline?.Dispose();
        _slangPipeline = null;
        _loadedShaderPath = null;
        _gl = null;
    }
}
