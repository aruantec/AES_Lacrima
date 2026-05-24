using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace AES_Emulation.Windows;

internal sealed class WgcAngleInterop : IDisposable
{
    private const int GlTexture2D = 0x0DE1;
    private const nint EGLD3DTexture2DShareHandleAngle = 0x3200;

    private GlInterface? _gl;
    private eglGetCurrentDisplayDel? _eglGetCurrentDisplay;
    private eglGetCurrentContextDel? _eglGetCurrentContext;
    private eglCreateImageKHRDel? _eglCreateImageKHR;
    private eglDestroyImageKHRDel? _eglDestroyImageKHR;
    private glEGLImageTargetTexture2DOESDel? _glEglImageTargetTexture2DOES;

    private nint _eglDisplay;
    private nint _eglContext;
    private nint _eglImage;
    private nint _currentSharedHandle;
    private int _textureId;
    private bool _initialized;

    public bool IsAvailable => _initialized;

    public bool TryInitialize(GlInterface gl)
    {
        if (_initialized && ReferenceEquals(_gl, gl))
            return true;

        Cleanup();
        _gl = gl;

        try
        {
            var pGetDisplay = gl.GetProcAddress("eglGetCurrentDisplay");
            var pGetContext = gl.GetProcAddress("eglGetCurrentContext");
            var pCreate = gl.GetProcAddress("eglCreateImageKHR");
            var pDestroy = gl.GetProcAddress("eglDestroyImageKHR");
            var pTarget = gl.GetProcAddress("glEGLImageTargetTexture2DOES");
            if (pGetDisplay == nint.Zero || pGetContext == nint.Zero || pCreate == nint.Zero ||
                pDestroy == nint.Zero || pTarget == nint.Zero)
            {
                return false;
            }

            _eglGetCurrentDisplay = Marshal.GetDelegateForFunctionPointer<eglGetCurrentDisplayDel>(pGetDisplay);
            _eglGetCurrentContext = Marshal.GetDelegateForFunctionPointer<eglGetCurrentContextDel>(pGetContext);
            _eglCreateImageKHR = Marshal.GetDelegateForFunctionPointer<eglCreateImageKHRDel>(pCreate);
            _eglDestroyImageKHR = Marshal.GetDelegateForFunctionPointer<eglDestroyImageKHRDel>(pDestroy);
            _glEglImageTargetTexture2DOES = Marshal.GetDelegateForFunctionPointer<glEGLImageTargetTexture2DOESDel>(pTarget);

            _eglDisplay = _eglGetCurrentDisplay?.Invoke() ?? nint.Zero;
            _eglContext = _eglGetCurrentContext?.Invoke() ?? nint.Zero;
            if (_eglDisplay == nint.Zero || _eglContext == nint.Zero)
                return false;

            if (_textureId == 0)
                _textureId = gl.GenTexture();

            _initialized = true;
            return true;
        }
        catch
        {
            Cleanup();
            return false;
        }
    }

    public bool TryBindSharedHandle(nint sharedHandle)
    {
        if (!_initialized || _gl == null || sharedHandle == nint.Zero)
            return false;

        if (_currentSharedHandle == sharedHandle && _eglImage != nint.Zero)
            return true;

        ReleaseImage();

        var image = _eglCreateImageKHR?.Invoke(_eglDisplay, _eglContext, EGLD3DTexture2DShareHandleAngle, sharedHandle, nint.Zero) ?? nint.Zero;
        if (image == nint.Zero)
            return false;

        _eglImage = image;
        _currentSharedHandle = sharedHandle;

        _gl.BindTexture(GlTexture2D, _textureId);
        _glEglImageTargetTexture2DOES?.Invoke(GlTexture2D, _eglImage);
        return true;
    }

    public int TextureId => _textureId;

    public void Dispose()
    {
        Cleanup();
    }

    private void ReleaseImage()
    {
        if (_eglImage != nint.Zero && _eglDestroyImageKHR != null && _eglDisplay != nint.Zero)
        {
            try
            {
                _eglDestroyImageKHR(_eglDisplay, _eglImage);
            }
            catch
            {
                // ignored
            }
        }

        _eglImage = nint.Zero;
        _currentSharedHandle = nint.Zero;
    }

    private void Cleanup()
    {
        ReleaseImage();

        if (_gl != null && _textureId != 0)
        {
            try
            {
                _gl.DeleteTexture(_textureId);
            }
            catch
            {
                // ignored
            }
        }

        _textureId = 0;
        _eglDisplay = nint.Zero;
        _eglContext = nint.Zero;
        _eglGetCurrentDisplay = null;
        _eglGetCurrentContext = null;
        _eglCreateImageKHR = null;
        _eglDestroyImageKHR = null;
        _glEglImageTargetTexture2DOES = null;
        _gl = null;
        _initialized = false;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint eglGetCurrentDisplayDel();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint eglGetCurrentContextDel();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint eglCreateImageKHRDel(nint display, nint context, nint target, nint buffer, nint attribList);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void eglDestroyImageKHRDel(nint display, nint image);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void glEGLImageTargetTexture2DOESDel(int target, nint image);
}
