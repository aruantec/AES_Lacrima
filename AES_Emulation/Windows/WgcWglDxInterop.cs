using System;
using System.Runtime.InteropServices;
using AES_Emulation.Windows.API;
using Avalonia.OpenGL;

namespace AES_Emulation.Windows;

/// <summary>
/// Desktop GL NV_DX_interop path for binding WGC D3D11 textures into the current GL context.
/// </summary>
internal sealed class WgcWglDxInterop : IDisposable
{
    private const int GlTexture2D = 0x0DE1;
    private const uint WglAccessReadOnlyNv = 0x00000000;

    private GlInterface? _gl;
    private nint _dxDevicePtr;
    private nint _wglDeviceHandle;
    private nint _wglObjectHandle;
    private nint _dxTexturePtr;
    private int _textureId;
    private bool _initialized;

    private wglDXOpenDeviceNVDel? _wglDXOpenDeviceNV;
    private wglDXCloseDeviceNVDel? _wglDXCloseDeviceNV;
    private wglDXRegisterObjectNVDel? _wglDXRegisterObjectNV;
    private wglDXUnregisterObjectNVDel? _wglDXUnregisterObjectNV;
    private wglDXLockObjectsNVDel? _wglDXLockObjectsNV;
    private wglDXUnlockObjectsNVDel? _wglDXUnlockObjectsNV;

    public bool IsAvailable => _initialized;

    public int TextureId => _textureId;

    public bool TryInitialize(GlInterface gl, nint session)
    {
        if (_initialized && ReferenceEquals(_gl, gl))
            return true;

        Cleanup();
        _gl = gl;

        if (session == nint.Zero)
            return false;

        var devPtr = WgcBridgeApi.GetD3D11Device(session);
        if (devPtr == nint.Zero)
            return false;

        try
        {
            var procOpen = gl.GetProcAddress("wglDXOpenDeviceNV");
            var procClose = gl.GetProcAddress("wglDXCloseDeviceNV");
            var procReg = gl.GetProcAddress("wglDXRegisterObjectNV");
            var procUnreg = gl.GetProcAddress("wglDXUnregisterObjectNV");
            var procLock = gl.GetProcAddress("wglDXLockObjectsNV");
            var procUnlock = gl.GetProcAddress("wglDXUnlockObjectsNV");
            if (procOpen == nint.Zero || procClose == nint.Zero || procReg == nint.Zero ||
                procUnreg == nint.Zero || procLock == nint.Zero || procUnlock == nint.Zero)
            {
                return false;
            }

            _wglDXOpenDeviceNV = Marshal.GetDelegateForFunctionPointer<wglDXOpenDeviceNVDel>(procOpen);
            _wglDXCloseDeviceNV = Marshal.GetDelegateForFunctionPointer<wglDXCloseDeviceNVDel>(procClose);
            _wglDXRegisterObjectNV = Marshal.GetDelegateForFunctionPointer<wglDXRegisterObjectNVDel>(procReg);
            _wglDXUnregisterObjectNV = Marshal.GetDelegateForFunctionPointer<wglDXUnregisterObjectNVDel>(procUnreg);
            _wglDXLockObjectsNV = Marshal.GetDelegateForFunctionPointer<wglDXLockObjectsNVDel>(procLock);
            _wglDXUnlockObjectsNV = Marshal.GetDelegateForFunctionPointer<wglDXUnlockObjectsNVDel>(procUnlock);

            _dxDevicePtr = devPtr;
            _wglDeviceHandle = _wglDXOpenDeviceNV?.Invoke(_dxDevicePtr) ?? nint.Zero;
            if (_wglDeviceHandle == nint.Zero)
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

    public bool TryBindD3DTexture(nint dxTexture)
    {
        if (!_initialized || _gl == null || dxTexture == nint.Zero || _wglDeviceHandle == nint.Zero)
            return false;

        if (_dxTexturePtr == dxTexture && _wglObjectHandle != nint.Zero)
            return LockObject();

        UnregisterObject();

        _wglObjectHandle = _wglDXRegisterObjectNV?.Invoke(
            _wglDeviceHandle,
            dxTexture,
            (uint)_textureId,
            (uint)GlTexture2D,
            WglAccessReadOnlyNv) ?? nint.Zero;

        if (_wglObjectHandle == nint.Zero)
            return false;

        _dxTexturePtr = dxTexture;
        return LockObject();
    }

    public void Dispose() => Cleanup();

    private bool LockObject()
    {
        if (_wglObjectHandle == nint.Zero || _wglDXLockObjectsNV == null || _wglDXUnlockObjectsNV == null)
            return false;

        var handles = new[] { _wglObjectHandle };
        if (!_wglDXLockObjectsNV(_wglDeviceHandle, 1, handles))
            return false;

        _wglDXUnlockObjectsNV(_wglDeviceHandle, 1, handles);
        return true;
    }

    private void UnregisterObject()
    {
        if (_wglObjectHandle != nint.Zero && _wglDeviceHandle != nint.Zero && _wglDXUnregisterObjectNV != null)
        {
            try
            {
                _wglDXUnregisterObjectNV(_wglDeviceHandle, _wglObjectHandle);
            }
            catch
            {
                // ignored
            }
        }

        _wglObjectHandle = nint.Zero;
        _dxTexturePtr = nint.Zero;
    }

    private void Cleanup()
    {
        UnregisterObject();

        if (_wglDeviceHandle != nint.Zero && _wglDXCloseDeviceNV != null)
        {
            try
            {
                _wglDXCloseDeviceNV(_wglDeviceHandle);
            }
            catch
            {
                // ignored
            }
        }

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
        _wglDeviceHandle = nint.Zero;
        _dxDevicePtr = nint.Zero;
        _wglDXOpenDeviceNV = null;
        _wglDXCloseDeviceNV = null;
        _wglDXRegisterObjectNV = null;
        _wglDXUnregisterObjectNV = null;
        _wglDXLockObjectsNV = null;
        _wglDXUnlockObjectsNV = null;
        _gl = null;
        _initialized = false;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint wglDXOpenDeviceNVDel(nint dxDevice);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool wglDXCloseDeviceNVDel(nint hDevice);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint wglDXRegisterObjectNVDel(nint hDevice, nint dxResource, uint name, uint type, uint access);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool wglDXUnregisterObjectNVDel(nint hDevice, nint hObject);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool wglDXLockObjectsNVDel(nint hDevice, int count, nint[] handles);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool wglDXUnlockObjectsNVDel(nint hDevice, int count, nint[] handles);
}
