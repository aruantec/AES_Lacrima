using log4net;
using AES_Core.Logging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System;
using System.IO;
using System.Text;

namespace AES_Emulation.Windows.API;

public static class WgcBridgeApi
{
    private static readonly ILog Log = LogHelper.For(typeof(WgcBridgeApi));
    // Keep the native library handle so delegates remain valid
    private static IntPtr s_nativeHandle = IntPtr.Zero;
    private static bool s_acquireLatestFrameFaulted;
    private static bool s_releaseLatestFrameFaulted;
    private static bool s_loggedCreateCaptureSession;
    private static bool s_loggedCreateDirectCompositionCaptureSession;
    private static bool s_loggedGetLatestFrame;
    private static bool s_loggedPeekLatestFrame;
    private static bool s_loggedAcquireLatestFrame;

    private static void LogDebug(string message)
    {
        Log.Debug(message);
        Debug.WriteLine(message);
        Trace.WriteLine(message);
    }

    private static void LogInfo(string message)
    {
        Log.Info(message);
        Debug.WriteLine(message);
        Trace.WriteLine(message);
    }

    private static void LogWarn(string message)
    {
        Log.Warn(message);
        Debug.WriteLine(message);
        Trace.WriteLine(message);
    }

    private static void LogError(string message, Exception ex)
    {
        Log.Error(message, ex);
        Debug.WriteLine($"{message} {ex}");
        Trace.WriteLine($"{message} {ex}");
    }

    private static void LogDebugOnce(ref bool flag, string message)
    {
        if (flag)
            return;

        flag = true;
        LogDebug(message);
    }

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
    private delegate nint CreateDirectCompositionCaptureSessionDel(nint targetHwnd, nint presentationHwnd);
    private static CreateDirectCompositionCaptureSessionDel? s_createDirectCompositionCaptureSession;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyCaptureSessionDel(nint session);
    private static DestroyCaptureSessionDel? s_destroyCaptureSession;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetCaptureStatusDel(nint session);
    private static GetCaptureStatusDel? s_getCaptureStatus;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDirectCompositionStateDel(nint session);
    private static GetDirectCompositionStateDel? s_getDirectCompositionState;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDirectCompositionPresentCountDel(nint session);
    private static GetDirectCompositionPresentCountDel? s_getDirectCompositionPresentCount;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDirectCompositionLastErrorDel(nint session, StringBuilder buffer, int bufferChars);
    private static GetDirectCompositionLastErrorDel? s_getDirectCompositionLastError;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetDirectCompositionRenderOptionsDel(nint session, int stretch, float brightness, float saturation, float tintR, float tintG, float tintB, float tintA, int disableVsync);
    private static SetDirectCompositionRenderOptionsDel? s_setDirectCompositionRenderOptions;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetDirectCompositionShaderDel(nint session, [MarshalAs(UnmanagedType.LPWStr)] string? shaderPath);
    private static SetDirectCompositionShaderDel? s_setDirectCompositionShader;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int GetDirectCompositionAdapterInfoDel(nint session, StringBuilder rendererBuffer, int rendererBufferChars, StringBuilder vendorBuffer, int vendorBufferChars);
    private static GetDirectCompositionAdapterInfoDel? s_getDirectCompositionAdapterInfo;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool GetLatestFrameDel(nint session, IntPtr outBuffer, nuint bufferSize, out int width, out int height);
    private static GetLatestFrameDel? s_getLatestFrame;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool PeekLatestFrameDel(nint session, out int width, out int height, out nuint requiredSize);
    private static PeekLatestFrameDel? s_peekLatestFrame;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetInjectionPidDel(nint session, int pid);
    private static SetInjectionPidDel? s_setInjectionPid;

    static WgcBridgeApi()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string bridgePath = Path.Combine(baseDir, "WgcBridge.dll");
            LogInfo(
                $"[WGC] Initializing WgcBridgeApi. baseDir='{baseDir}', " +
                $"bridgeExists={File.Exists(bridgePath)}, processArch={RuntimeInformation.ProcessArchitecture}, osArch={RuntimeInformation.OSArchitecture}, " +
#if NATIVE_AOT
                "nativeAot=true."
#else
                "nativeAot=false."
#endif
            );

            // Attempt to load the native library from the default search path
            IntPtr handle;
            if (NativeLibrary.TryLoad("WgcBridge.dll", out handle))
            {
                // Keep the handle alive for the lifetime of the process so
                // delegates obtained from function pointers remain valid.
                s_nativeHandle = handle;
                LogInfo("[WGC] Loaded native WgcBridge.dll for export check.");

                string[] exports = new[] {
                    "CreateCaptureSession",
                    "CreateDirectCompositionCaptureSession",
                    "GetLatestFrame",
                    "DestroyCaptureSession",
                    "GetCaptureStatus",
                    "GetDirectCompositionState",
                    "GetDirectCompositionPresentCount",
                    "GetDirectCompositionLastError",
                    "SetDirectCompositionRenderOptions",
                    "SetDirectCompositionShader",
                    "GetDirectCompositionAdapterInfo",
                    "PeekLatestFrame",
                    "SetCaptureMaxResolution",
                    "SetCaptureCropRect",
                    "SetInjectionPid",
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
                    {
                        // Removed LogDebug spam to clean up console output
                    }
                    else
                        LogWarn($"[WGC] Export MISSING: {name}");
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
                if (NativeLibrary.TryGetExport(handle, "CreateDirectCompositionCaptureSession", out IntPtr pCreateDComp))
                    s_createDirectCompositionCaptureSession = Marshal.GetDelegateForFunctionPointer<CreateDirectCompositionCaptureSessionDel>(pCreateDComp);
                if (NativeLibrary.TryGetExport(handle, "DestroyCaptureSession", out IntPtr pDestroy))
                    s_destroyCaptureSession = Marshal.GetDelegateForFunctionPointer<DestroyCaptureSessionDel>(pDestroy);
                if (NativeLibrary.TryGetExport(handle, "GetCaptureStatus", out IntPtr pStatus))
                    s_getCaptureStatus = Marshal.GetDelegateForFunctionPointer<GetCaptureStatusDel>(pStatus);
                if (NativeLibrary.TryGetExport(handle, "GetDirectCompositionState", out IntPtr pDCompState))
                    s_getDirectCompositionState = Marshal.GetDelegateForFunctionPointer<GetDirectCompositionStateDel>(pDCompState);
                if (NativeLibrary.TryGetExport(handle, "GetDirectCompositionPresentCount", out IntPtr pDCompPresent))
                    s_getDirectCompositionPresentCount = Marshal.GetDelegateForFunctionPointer<GetDirectCompositionPresentCountDel>(pDCompPresent);
                if (NativeLibrary.TryGetExport(handle, "GetDirectCompositionLastError", out IntPtr pDCompLastError))
                    s_getDirectCompositionLastError = Marshal.GetDelegateForFunctionPointer<GetDirectCompositionLastErrorDel>(pDCompLastError);
                if (NativeLibrary.TryGetExport(handle, "SetDirectCompositionRenderOptions", out IntPtr pDCompOptions))
                    s_setDirectCompositionRenderOptions = Marshal.GetDelegateForFunctionPointer<SetDirectCompositionRenderOptionsDel>(pDCompOptions);

                if (NativeLibrary.TryGetExport(handle, "SetDirectCompositionShader", out IntPtr pDCompShader))
                    s_setDirectCompositionShader = Marshal.GetDelegateForFunctionPointer<SetDirectCompositionShaderDel>(pDCompShader);

                if (NativeLibrary.TryGetExport(handle, "GetDirectCompositionAdapterInfo", out IntPtr pDCompAdapterInfo))
                    s_getDirectCompositionAdapterInfo = Marshal.GetDelegateForFunctionPointer<GetDirectCompositionAdapterInfoDel>(pDCompAdapterInfo);
                if (NativeLibrary.TryGetExport(handle, "GetLatestFrame", out IntPtr pGetLatest))
                    s_getLatestFrame = Marshal.GetDelegateForFunctionPointer<GetLatestFrameDel>(pGetLatest);
                if (NativeLibrary.TryGetExport(handle, "PeekLatestFrame", out IntPtr pPeek))
                    s_peekLatestFrame = Marshal.GetDelegateForFunctionPointer<PeekLatestFrameDel>(pPeek);
                if (NativeLibrary.TryGetExport(handle, "SetInjectionPid", out IntPtr pSetInj))
                    s_setInjectionPid = Marshal.GetDelegateForFunctionPointer<SetInjectionPidDel>(pSetInj);

                // Do not free the handle here - we need to keep the native module loaded
                // while delegates are in use by the managed code.
            }
            else
            {
                LogWarn("[WGC] Failed to load WgcBridge.dll for export check.");
            }
        }
        catch (Exception ex)
        {
            LogError("[WGC] Exception while checking native exports.", ex);
        }
    }

    // Core exports (expected) - mark SetLastError so managed code can inspect native errors
    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "CreateCaptureSession")]
    private static extern nint CreateCaptureSessionNative(nint targetHwnd);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "CreateDirectCompositionCaptureSession")]
    private static extern nint CreateDirectCompositionCaptureSessionNative(nint targetHwnd, nint presentationHwnd);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "GetLatestFrame")]
    private static extern bool GetLatestFrameNative(nint session, nint outBuffer, nuint bufferSize, out int width, out int height);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "DestroyCaptureSession")]
    private static extern void DestroyCaptureSessionNative(nint session);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "GetCaptureStatus")]
    private static extern int GetCaptureStatusNative(nint session);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "GetDirectCompositionState")]
    private static extern int GetDirectCompositionStateNative(nint session);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "GetDirectCompositionPresentCount")]
    private static extern int GetDirectCompositionPresentCountNative(nint session);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "GetDirectCompositionLastError")]
    private static extern int GetDirectCompositionLastErrorNative(nint session, StringBuilder buffer, int bufferChars);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "SetDirectCompositionRenderOptions")]
    private static extern void SetDirectCompositionRenderOptionsNative(nint session, int stretch, float brightness, float saturation, float tintR, float tintG, float tintB, float tintA, int disableVsync);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetDirectCompositionShader")]
    private static extern void SetDirectCompositionShaderNative(nint session, [MarshalAs(UnmanagedType.LPWStr)] string? shaderPath);

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDirectCompositionAdapterInfo")]
    private static extern int GetDirectCompositionAdapterInfoNative(nint session, StringBuilder rendererBuffer, int rendererBufferChars, StringBuilder vendorBuffer, int vendorBufferChars);

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

    [DllImport("WgcBridge.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true, EntryPoint = "SetInjectionPid")]
    private static extern void SetInjectionPidNative(nint session, int pid);

    // Public wrappers that add diagnostics/logging
    public static nint CreateCaptureSession(nint targetHwnd)
    {
        try
        {
            LogDebugOnce(ref s_loggedCreateCaptureSession, "[WGC] CreateCaptureSession invoked for the first time.");
            if (s_createCaptureSession != null)
            {
                var result = s_createCaptureSession(targetHwnd);
                if (result == nint.Zero)
                {
                    LogWarn("[WGC] CreateCaptureSession (delegate) returned NULL");
                }
                else LogInfo($"[WGC] CreateCaptureSession (delegate) succeeded: 0x{result.ToString("X")}");
                return result;
            }

            var res = CreateCaptureSessionNative(targetHwnd);
            if (res == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LogWarn($"[WGC] CreateCaptureSession returned NULL. Win32 error: {err}");
            }
            else LogInfo($"[WGC] CreateCaptureSession succeeded: 0x{res.ToString("X")}");
            return res;
        }
        catch (Exception ex)
        {
            LogError("[WGC] Exception CreateCaptureSession.", ex);
            return nint.Zero;
        }
    }

    public static nint CreateDirectCompositionCaptureSession(nint targetHwnd, nint presentationHwnd)
    {
        try
        {
            LogDebugOnce(ref s_loggedCreateDirectCompositionCaptureSession, "[WGC] CreateDirectCompositionCaptureSession invoked for the first time.");
            if (s_createDirectCompositionCaptureSession != null)
            {
                var result = s_createDirectCompositionCaptureSession(targetHwnd, presentationHwnd);
                if (result == nint.Zero)
                {
                    LogWarn("[WGC] CreateDirectCompositionCaptureSession (delegate) returned NULL");
                }
                else LogInfo($"[WGC] CreateDirectCompositionCaptureSession (delegate) succeeded: 0x{result.ToString("X")}");
                return result;
            }

            var res = CreateDirectCompositionCaptureSessionNative(targetHwnd, presentationHwnd);
            if (res == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LogWarn($"[WGC] CreateDirectCompositionCaptureSession returned NULL. Win32 error: {err}");
            }
            else LogInfo($"[WGC] CreateDirectCompositionCaptureSession succeeded: 0x{res.ToString("X")}");
            return res;
        }
        catch (Exception ex)
        {
            LogError("[WGC] Exception CreateDirectCompositionCaptureSession.", ex);
            return nint.Zero;
        }
    }

    public static bool GetLatestFrame(nint session, nint outBuffer, nuint bufferSize, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            LogDebugOnce(ref s_loggedGetLatestFrame, "[WGC] GetLatestFrame invoked for the first time.");
            if (s_getLatestFrame != null)
                return s_getLatestFrame(session, (IntPtr)outBuffer, bufferSize, out width, out height);

            return GetLatestFrameNative(session, outBuffer, bufferSize, out width, out height);
        }
        catch (SEHException ex)
        {
            LogError("[WGC] SEHException in GetLatestFrame.", ex);
            return false;
        }
        catch (ExternalException ex)
        {
            LogError("[WGC] ExternalException in GetLatestFrame.", ex);
            return false;
        }
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

    public static int GetDirectCompositionState(nint session)
    {
        if (s_getDirectCompositionState != null) return s_getDirectCompositionState(session);
        return GetDirectCompositionStateNative(session);
    }

    public static int GetDirectCompositionPresentCount(nint session)
    {
        if (s_getDirectCompositionPresentCount != null) return s_getDirectCompositionPresentCount(session);
        return GetDirectCompositionPresentCountNative(session);
    }

    public static string GetDirectCompositionLastError(nint session)
    {
        var buffer = new StringBuilder(256);
        int ok = s_getDirectCompositionLastError != null
            ? s_getDirectCompositionLastError(session, buffer, buffer.Capacity)
            : GetDirectCompositionLastErrorNative(session, buffer, buffer.Capacity);
        return ok != 0 ? buffer.ToString() : string.Empty;
    }

    public static void SetDirectCompositionRenderOptions(nint session, int stretch, float brightness, float saturation, float tintR, float tintG, float tintB, float tintA, bool disableVsync)
    {
        if (s_setDirectCompositionRenderOptions != null)
        {
            s_setDirectCompositionRenderOptions(session, stretch, brightness, saturation, tintR, tintG, tintB, tintA, disableVsync ? 1 : 0);
            return;
        }

        SetDirectCompositionRenderOptionsNative(session, stretch, brightness, saturation, tintR, tintG, tintB, tintA, disableVsync ? 1 : 0);
    }

    public static void SetDirectCompositionShader(nint session, string? shaderPath)
    {
        LogInfo($"[WGC] SetDirectCompositionShader session=0x{session.ToString("X")} path='{shaderPath ?? "<null>"}'");

        if (s_setDirectCompositionShader != null)
        {
            s_setDirectCompositionShader(session, shaderPath);
            return;
        }

        SetDirectCompositionShaderNative(session, shaderPath);
    }

    public static (string Renderer, string Vendor) GetDirectCompositionAdapterInfo(nint session)
    {
        var renderer = new StringBuilder(256);
        var vendor = new StringBuilder(128);

        int ok = s_getDirectCompositionAdapterInfo != null
            ? s_getDirectCompositionAdapterInfo(session, renderer, renderer.Capacity, vendor, vendor.Capacity)
            : GetDirectCompositionAdapterInfoNative(session, renderer, renderer.Capacity, vendor, vendor.Capacity);

        return ok != 0
            ? (renderer.ToString(), vendor.ToString())
            : (string.Empty, string.Empty);
    }

    public static int GetReaderCount(nint session)
    {
        if (s_getReaderCount != null) return s_getReaderCount(session);
        return GetReaderCountNative(session);
    }

    public static bool PeekLatestFrame(nint session, out int width, out int height, out nuint requiredSize)
    {
        width = 0;
        height = 0;
        requiredSize = 0;

        try
        {
            LogDebugOnce(ref s_loggedPeekLatestFrame, "[WGC] PeekLatestFrame invoked for the first time.");
            if (s_peekLatestFrame != null)
                return s_peekLatestFrame(session, out width, out height, out requiredSize);

            return PeekLatestFrameNative(session, out width, out height, out requiredSize);
        }
        catch (SEHException ex)
        {
            LogError("[WGC] SEHException in PeekLatestFrame.", ex);
            return false;
        }
        catch (ExternalException ex)
        {
            LogError("[WGC] ExternalException in PeekLatestFrame.", ex);
            return false;
        }
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

    public static void SetInjectionPid(nint session, int pid)
    {
        if (s_setInjectionPid != null) { s_setInjectionPid(session, pid); return; }
        SetInjectionPidNative(session, pid);
    }

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
        outBuffer = IntPtr.Zero;
        outSize = 0;
        width = 0;
        height = 0;

        if (s_acquireLatestFrame == null || s_acquireLatestFrameFaulted || session == IntPtr.Zero)
            return false;

        try
        {
            LogDebugOnce(ref s_loggedAcquireLatestFrame, "[WGC] AcquireLatestFrame invoked for the first time.");
            return s_acquireLatestFrame(session, out outBuffer, out outSize, out width, out height);
        }
        catch (SEHException ex)
        {
            s_acquireLatestFrameFaulted = true;
            s_acquireLatestFrame = null;
            LogError("[WGC] SEHException in AcquireLatestFrame. Disabling zero-copy fast path.", ex);
            return false;
        }
        catch (ExternalException ex)
        {
            s_acquireLatestFrameFaulted = true;
            s_acquireLatestFrame = null;
            LogError("[WGC] ExternalException in AcquireLatestFrame. Disabling zero-copy fast path.", ex);
            return false;
        }
    }

    public static void ReleaseLatestFrame(nint session)
    {
        if (s_releaseLatestFrame == null || s_releaseLatestFrameFaulted || session == IntPtr.Zero)
            return;

        try
        {
            s_releaseLatestFrame(session);
        }
        catch (SEHException ex)
        {
            s_releaseLatestFrameFaulted = true;
            s_releaseLatestFrame = null;
            LogError("[WGC] SEHException in ReleaseLatestFrame. Disabling zero-copy release path.", ex);
        }
        catch (ExternalException ex)
        {
            s_releaseLatestFrameFaulted = true;
            s_releaseLatestFrame = null;
            LogError("[WGC] ExternalException in ReleaseLatestFrame. Disabling zero-copy release path.", ex);
        }
    }

    // Diagnostics helpers
    public static bool IsNativeLoaded() => s_nativeHandle != IntPtr.Zero;

    public static string GetDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[WGC] Diagnostics:");
        sb.AppendLine($"NativeLoaded: {IsNativeLoaded()}");
        sb.AppendLine($"IntPtr.Size: {IntPtr.Size}");
        sb.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        sb.AppendLine($"WgcBridgePathExists: {File.Exists(Path.Combine(AppContext.BaseDirectory, "WgcBridge.dll"))}");
        sb.AppendLine($"GetD3D11Device delegate: { (s_getD3D11Device != null) }");
        sb.AppendLine($"GetLatestD3DTexture delegate: { (s_getLatestD3DTexture != null) }");
        sb.AppendLine($"SetInteropEnabled delegate: { (s_setInteropEnabled != null) }");
        sb.AppendLine($"GetSharedHandle delegate: { (s_getSharedHandle != null) }");
        sb.AppendLine($"AcquireLatestFrame delegate: { (s_acquireLatestFrame != null) }");
        sb.AppendLine($"ReleaseLatestFrame delegate: { (s_releaseLatestFrame != null) }");
        sb.AppendLine($"GetReaderCount delegate: { (s_getReaderCount != null) }");
        sb.AppendLine($"CreateCaptureSession delegate: { (s_createCaptureSession != null) }");
        sb.AppendLine($"CreateDirectCompositionCaptureSession delegate: { (s_createDirectCompositionCaptureSession != null) }");
        sb.AppendLine($"GetLatestFrame delegate: { (s_getLatestFrame != null) }");
        sb.AppendLine($"GetDirectCompositionState delegate: { (s_getDirectCompositionState != null) }");
        sb.AppendLine($"GetDirectCompositionPresentCount delegate: { (s_getDirectCompositionPresentCount != null) }");
        sb.AppendLine($"GetDirectCompositionLastError delegate: { (s_getDirectCompositionLastError != null) }");
        sb.AppendLine($"SetDirectCompositionRenderOptions delegate: { (s_setDirectCompositionRenderOptions != null) }");
        sb.AppendLine($"GetDirectCompositionAdapterInfo delegate: { (s_getDirectCompositionAdapterInfo != null) }");
        sb.AppendLine($"PeekLatestFrame delegate: { (s_peekLatestFrame != null) }");
        return sb.ToString();
    }
}
