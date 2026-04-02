using System.Runtime.InteropServices;
using System.Diagnostics;
using System;
using System.Text;

namespace AES_Emulation.Windows.API;

public static class WgcBridgeApi
{
    // Keep the native library handle so delegates remain valid
    private static IntPtr s_nativeHandle = IntPtr.Zero;

    // Delegates for optional exports
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetD3D11DeviceDel(nint session);
    private static GetD3D11DeviceDel? s_getD3D11Device;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetLatestD3DTextureDel(nint session);
    private static GetLatestD3DTextureDel? s_getLatestD3DTexture;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetInteropEnabledDel(nint session, int enabled);
    private static SetInteropEnabledDel? s_setInteropEnabled;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSharedHandleDel(nint session);
    private static GetSharedHandleDel? s_getSharedHandle;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool AcquireLatestFrameDel(nint session, out IntPtr outBuffer, out nuint outSize, out int width, out int height);
    private static AcquireLatestFrameDel? s_acquireLatestFrame;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseLatestFrameDel(nint session);
    private static ReleaseLatestFrameDel? s_releaseLatestFrame;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetReaderCountDel(nint session);
    private static GetReaderCountDel? s_getReaderCount;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetBorderRequiredDel(nint session, int required);
    private static SetBorderRequiredDel? s_setBorderRequired;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetVrrEnabledDel(nint session, int enabled);
    private static SetVrrEnabledDel? s_setVrrEnabled;

    // Delegates for hot-path exports (to avoid DllImport/IL_STUB overhead)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateCaptureSessionDel(nint targetHwnd);
    private static CreateCaptureSessionDel? s_createCaptureSession;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyCaptureSessionDel(nint session);
    private static DestroyCaptureSessionDel? s_destroyCaptureSession;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetCaptureStatusDel(nint session);
    private static GetCaptureStatusDel? s_getCaptureStatus;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool GetLatestFrameDel(nint session, IntPtr outBuffer, nuint bufferSize, out int width, out int height);
    private static GetLatestFrameDel? s_getLatestFrame;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool PeekLatestFrameDel(nint session, out int width, out int height, out nuint requiredSize);
    private static PeekLatestFrameDel? s_peekLatestFrame;

    static WgcBridgeApi()
    {
        try
        {
            // Attempt to load the native library from the default search path
            IntPtr handle;
            if (NativeLibrary.TryLoad("WgcBridge.dll", out handle))
            {
                // Keep the handle alive for the lifetime of the process so
                // delegates obtained from function pointers remain valid.
                s_nativeHandle = handle;
                Debug.WriteLine("[WGC] Loaded native WgcBridge.dll for export check.");

                string[] exports = new[] {
                    "CreateCaptureSession",
                    "GetLatestFrame",
                    "DestroyCaptureSession",
                    "GetCaptureStatus",
                    "PeekLatestFrame",
                    "SetCaptureMaxResolution",
                    "SetCaptureCropRect",
                    "GetD3D11Device",
                    "GetLatestD3DTexture",
                    "SetInteropEnabled",
                    "GetSharedHandle",
                    "AcquireLatestFrame",
                    "ReleaseLatestFrame",
                    "GetReaderCount",
                    "SetBorderRequired",
                    "SetVrrEnabled"
                };
                foreach (var name in exports)
                {
                    if (NativeLibrary.TryGetExport(handle, name, out IntPtr addr))
                        Debug.WriteLine($"[WGC] Export present: {name}");
                    else
                        Debug.WriteLine($"[WGC] Export MISSING: {name}");
                }

                // Bind optional exports to delegates if present
                if (NativeLibrary.TryGetExport(handle, "GetD3D11Device", out IntPtr pGetD3D))
                    s_getD3D11Device = Marshal.GetDelegateForFunctionPointer<GetD3D11DeviceDel>(pGetD3D);
                if (NativeLibrary.TryGetExport(handle, "GetLatestD3DTexture", out IntPtr pGetTex))
                    s_getLatestD3DTexture = Marshal.GetDelegateForFunctionPointer<GetLatestD3DTextureDel>(pGetTex);
                if (NativeLibrary.TryGetExport(handle, "SetInteropEnabled", out IntPtr pSetInterop))
                    s_setInteropEnabled = Marshal.GetDelegateForFunctionPointer<SetInteropEnabledDel>(pSetInterop);
                if (NativeLibrary.TryGetExport(handle, "GetSharedHandle", out IntPtr pGetShared))
                    s_getSharedHandle = Marshal.GetDelegateForFunctionPointer<GetSharedHandleDel>(pGetShared);
                if (NativeLibrary.TryGetExport(handle, "AcquireLatestFrame", out IntPtr pAcquire))
                    s_acquireLatestFrame = Marshal.GetDelegateForFunctionPointer<AcquireLatestFrameDel>(pAcquire);
                if (NativeLibrary.TryGetExport(handle, "ReleaseLatestFrame", out IntPtr pRelease))
                    s_releaseLatestFrame = Marshal.GetDelegateForFunctionPointer<ReleaseLatestFrameDel>(pRelease);
                if (NativeLibrary.TryGetExport(handle, "GetReaderCount", out IntPtr pReaderCount))
                    s_getReaderCount = Marshal.GetDelegateForFunctionPointer<GetReaderCountDel>(pReaderCount);
                if (NativeLibrary.TryGetExport(handle, "SetBorderRequired", out IntPtr pSetBorder))
                    s_setBorderRequired = Marshal.GetDelegateForFunctionPointer<SetBorderRequiredDel>(pSetBorder);
                if (NativeLibrary.TryGetExport(handle, "SetVrrEnabled", out IntPtr pSetVrr))
                    s_setVrrEnabled = Marshal.GetDelegateForFunctionPointer<SetVrrEnabledDel>(pSetVrr);

                // Bind hot-path/core exports to delegates when possible to avoid per-frame P/Invoke overhead
                if (NativeLibrary.TryGetExport(handle, "CreateCaptureSession", out IntPtr pCreate))
                    s_createCaptureSession = Marshal.GetDelegateForFunctionPointer<CreateCaptureSessionDel>(pCreate);
                if (NativeLibrary.TryGetExport(handle, "DestroyCaptureSession", out IntPtr pDestroy))
                    s_destroyCaptureSession = Marshal.GetDelegateForFunctionPointer<DestroyCaptureSessionDel>(pDestroy);
                if (NativeLibrary.TryGetExport(handle, "GetCaptureStatus", out IntPtr pStatus))
                    s_getCaptureStatus = Marshal.GetDelegateForFunctionPointer<GetCaptureStatusDel>(pStatus);
                if (NativeLibrary.TryGetExport(handle, "GetLatestFrame", out IntPtr pGetLatest))
                    s_getLatestFrame = Marshal.GetDelegateForFunctionPointer<GetLatestFrameDel>(pGetLatest);
                if (NativeLibrary.TryGetExport(handle, "PeekLatestFrame", out IntPtr pPeek))
                    s_peekLatestFrame = Marshal.GetDelegateForFunctionPointer<PeekLatestFrameDel>(pPeek);

                // Do not free the handle here - we need to keep the native module loaded
                // while delegates are in use by the managed code.
            }
            else
            {
                Debug.WriteLine("[WGC] Failed to load WgcBridge.dll for export check.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WGC] Exception while checking native exports: {ex.Message}");
        }
    }

    // Core exports (expected) - mark SetLastError so managed code can inspect native errors
    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "CreateCaptureSession")]
    private static extern nint CreateCaptureSessionNative(nint targetHwnd);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "GetLatestFrame")]
    private static extern bool GetLatestFrameNative(nint session, nint outBuffer, nuint bufferSize, out int width, out int height);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "DestroyCaptureSession")]
    private static extern void DestroyCaptureSessionNative(nint session);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "GetCaptureStatus")]
    private static extern int GetCaptureStatusNative(nint session);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "GetReaderCount")]
    private static extern int GetReaderCountNative(nint session);

    // New: peek latest frame size / required buffer size without copying
    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "PeekLatestFrame")]
    private static extern bool PeekLatestFrameNative(nint session, out int width, out int height, out nuint requiredSize);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "SetCaptureMaxResolution")]
    private static extern void SetCaptureMaxResolutionNative(nint session, int maxWidth, int maxHeight);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "SetCaptureCropRect")]
    private static extern void SetCaptureCropRectNative(nint session, int x, int y, int width, int height);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "SetBorderRequired")]
    private static extern void SetBorderRequiredNative(nint session, int required);

    // Public wrappers that add diagnostics/logging
    public static nint CreateCaptureSession(nint targetHwnd)
    {
        try
        {
            if (s_createCaptureSession != null)
            {
                var result = s_createCaptureSession(targetHwnd);
                if (result == nint.Zero)
                {
                    Debug.WriteLine("[WGC] CreateCaptureSession (delegate) returned NULL");
                }
                else Debug.WriteLine($"[WGC] CreateCaptureSession (delegate) succeeded: 0x{result.ToString("X")}");
                return result;
            }

            var res = CreateCaptureSessionNative(targetHwnd);
            if (res == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[WGC] CreateCaptureSession returned NULL. Win32 error: {err}");
            }
            else Debug.WriteLine($"[WGC] CreateCaptureSession succeeded: 0x{res.ToString("X")}");
            return res;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WGC] Exception CreateCaptureSession: {ex.Message}");
            return nint.Zero;
        }
    }

    public static bool GetLatestFrame(nint session, nint outBuffer, nuint bufferSize, out int width, out int height)
    {
        if (s_getLatestFrame != null)
            return s_getLatestFrame(session, (IntPtr)outBuffer, bufferSize, out width, out height);
        return GetLatestFrameNative(session, outBuffer, bufferSize, out width, out height);
    }

    public static void DestroyCaptureSession(nint session)
    {
        if (s_destroyCaptureSession != null) { s_destroyCaptureSession(session); return; }
        DestroyCaptureSessionNative(session);
    }

    public static int GetCaptureStatus(nint session)
    {
        if (s_getCaptureStatus != null) return s_getCaptureStatus(session);
        return GetCaptureStatusNative(session);
    }

    public static int GetReaderCount(nint session)
    {
        if (s_getReaderCount != null) return s_getReaderCount(session);
        return GetReaderCountNative(session);
    }

    public static bool PeekLatestFrame(nint session, out int width, out int height, out nuint requiredSize)
    {
        if (s_peekLatestFrame != null) return s_peekLatestFrame(session, out width, out height, out requiredSize);
        return PeekLatestFrameNative(session, out width, out height, out requiredSize);
    }

    public static void SetCaptureMaxResolution(nint session, int maxWidth, int maxHeight)
    {
        SetCaptureMaxResolutionNative(session, maxWidth, maxHeight);
    }

    public static void SetCaptureCropRect(nint session, int x, int y, int width, int height)
    {
        SetCaptureCropRectNative(session, x, y, width, height);
    }

    public static void SetBorderRequired(nint session, bool required) => s_setBorderRequired?.Invoke(session, required ? 1 : 0);
    public static void SetVrrEnabled(nint session, bool enabled) => s_setVrrEnabled?.Invoke(session, enabled ? 1 : 0);

    // Optional exports - wrappers that use delegates when available
    public static nint GetD3D11Device(nint session)
    {
        if (s_getD3D11Device != null) return s_getD3D11Device(session);
        return nint.Zero;
    }

    public static nint GetLatestD3DTexture(nint session)
    {
        if (s_getLatestD3DTexture != null) return s_getLatestD3DTexture(session);
        return nint.Zero;
    }

    public static void SetInteropEnabled(nint session, int enabled)
    {
        if (s_setInteropEnabled != null) s_setInteropEnabled(session, enabled);
    }

    public static IntPtr GetSharedHandle(nint session)
    {
        if (s_getSharedHandle != null) return s_getSharedHandle(session);
        return IntPtr.Zero;
    }

    public static bool AcquireLatestFrame(nint session, out IntPtr outBuffer, out nuint outSize, out int width, out int height)
    {
        if (s_acquireLatestFrame != null) return s_acquireLatestFrame(session, out outBuffer, out outSize, out width, out height);
        outBuffer = IntPtr.Zero; outSize = 0; width = 0; height = 0; return false;
    }

    public static void ReleaseLatestFrame(nint session)
    {
        if (s_releaseLatestFrame != null) s_releaseLatestFrame(session);
    }

    // Diagnostics helpers
    public static bool IsNativeLoaded() => s_nativeHandle != IntPtr.Zero;

    public static string GetDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"NativeLoaded: {IsNativeLoaded()}");
        sb.AppendLine($"IntPtr.Size: {IntPtr.Size}");
        sb.AppendLine($"GetD3D11Device delegate: { (s_getD3D11Device != null) }");
        sb.AppendLine($"GetLatestD3DTexture delegate: { (s_getLatestD3DTexture != null) }");
        sb.AppendLine($"SetInteropEnabled delegate: { (s_setInteropEnabled != null) }");
        sb.AppendLine($"GetSharedHandle delegate: { (s_getSharedHandle != null) }");
        sb.AppendLine($"AcquireLatestFrame delegate: { (s_acquireLatestFrame != null) }");
        sb.AppendLine($"ReleaseLatestFrame delegate: { (s_releaseLatestFrame != null) }");
        sb.AppendLine($"GetReaderCount delegate: { (s_getReaderCount != null) }");
        return sb.ToString();
    }
}