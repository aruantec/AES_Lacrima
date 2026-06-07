using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace AES_Emulation.Linux;

/// <summary>
/// Builds an Avalonia <see cref="GlInterface"/> from the currently bound GL/EGL context on Linux.
/// Custom draw capture often has a Skia GRContext but no <see cref="IGlContext"/> feature on the lease.
/// </summary>
internal static class LinuxGlBootstrap
{
    private static IntPtr? s_eglModule;
    private static IntPtr? s_glModule;
    private static EglGetCurrentContextDel? s_eglGetCurrentContext;
    private static EglGetProcAddressDel? s_eglGetProcAddress;
    private static GlxGetProcAddressDel? s_glxGetProcAddress;
    private static bool s_preferEglProcResolver;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint EglGetCurrentContextDel();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint EglGetProcAddressDel(nint procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GlxGetProcAddressDel(nint procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GlxGetCurrentContextDel();

    public static GlInterface? TryCreateFromCurrentContext()
    {
        if (!HasActiveRenderContext())
            return null;

        foreach (var version in new[]
                 {
                     new GlVersion(GlProfileType.OpenGLES, 3, 0),
                     new GlVersion(GlProfileType.OpenGLES, 2, 0),
                     new GlVersion(GlProfileType.OpenGL, 4, 6),
                     new GlVersion(GlProfileType.OpenGL, 4, 5),
                     new GlVersion(GlProfileType.OpenGL, 3, 3),
                 })
        {
            try
            {
                var gl = new GlInterface(version, ResolveProcAddress);
                var testTexture = gl.GenTexture();
                if (testTexture == 0)
                    continue;

                gl.DeleteTexture(testTexture);
                return gl;
            }
            catch
            {
                // Try the next GL profile.
            }
        }

        return null;
    }

    private static bool HasActiveRenderContext()
    {
        s_preferEglProcResolver = false;

        if (TryEnsureEglExports() && s_eglGetCurrentContext?.Invoke() != nint.Zero)
        {
            s_preferEglProcResolver = true;
            return true;
        }

        if (!TryEnsureGlExports())
            return false;

        if (!NativeLibrary.TryGetExport(s_glModule!.Value, "glXGetCurrentContext", out var proc))
            return false;

        var getContext = Marshal.GetDelegateForFunctionPointer<GlxGetCurrentContextDel>(proc);
        return getContext() != nint.Zero;
    }

    private static nint ResolveProcAddress(string name)
    {
        if (s_preferEglProcResolver)
        {
            var eglProc = ResolveEglProcAddress(name);
            if (IsValidProc(eglProc))
                return eglProc;
        }

        if (TryEnsureGlExports() && s_glxGetProcAddress != null)
        {
            var procName = Marshal.StringToHGlobalAnsi(name);
            try
            {
                var glxProc = s_glxGetProcAddress(procName);
                if (IsValidProc(glxProc))
                    return glxProc;
            }
            finally
            {
                Marshal.FreeHGlobal(procName);
            }
        }

        if (TryEnsureGlExports() &&
            NativeLibrary.TryGetExport(s_glModule!.Value, name, out var export) &&
            IsValidProc(export))
        {
            return export;
        }

        return ResolveEglProcAddress(name);
    }

    private static nint ResolveEglProcAddress(string name)
    {
        if (!TryEnsureEglExports())
            return nint.Zero;

        var procName = Marshal.StringToHGlobalAnsi(name);
        try
        {
            var proc = s_eglGetProcAddress?.Invoke(procName) ?? nint.Zero;
            return IsValidProc(proc) ? proc : nint.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(procName);
        }
    }

    private static bool TryEnsureEglExports()
    {
        if (s_eglGetProcAddress != null)
            return true;

        foreach (var name in new[] { "libEGL.so.1", "libEGL.so" })
        {
            if (!NativeLibrary.TryLoad(name, out var eglModule))
                continue;

            if (!NativeLibrary.TryGetExport(eglModule, "eglGetProcAddress", out var pGetProc) ||
                !NativeLibrary.TryGetExport(eglModule, "eglGetCurrentContext", out var pGetContext))
            {
                continue;
            }

            s_eglModule = eglModule;
            s_eglGetProcAddress = Marshal.GetDelegateForFunctionPointer<EglGetProcAddressDel>(pGetProc);
            s_eglGetCurrentContext = Marshal.GetDelegateForFunctionPointer<EglGetCurrentContextDel>(pGetContext);
            return true;
        }

        return false;
    }

    private static bool TryEnsureGlExports()
    {
        if (s_glxGetProcAddress != null)
            return true;

        foreach (var name in new[] { "libGL.so.1", "libGL.so" })
        {
            if (!NativeLibrary.TryLoad(name, out var glModule))
                continue;

            if (!NativeLibrary.TryGetExport(glModule, "glXGetProcAddress", out var pGetProc) &&
                !NativeLibrary.TryGetExport(glModule, "glXGetProcAddressARB", out pGetProc))
            {
                continue;
            }

            s_glModule = glModule;
            s_glxGetProcAddress = Marshal.GetDelegateForFunctionPointer<GlxGetProcAddressDel>(pGetProc);
            return true;
        }

        return false;
    }

    private static bool IsValidProc(nint proc) =>
        proc != nint.Zero &&
        proc != 1 &&
        proc != 2 &&
        proc != 3 &&
        proc != -1;
}
