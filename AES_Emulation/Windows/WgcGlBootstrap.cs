using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace AES_Emulation.Windows;

/// <summary>
/// Builds an Avalonia <see cref="GlInterface"/> from the currently bound GL/EGL context.
/// Composition custom visuals often have a Skia GRContext but no IGlContext feature.
/// </summary>
internal static class WgcGlBootstrap
{
    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern IntPtr wglGetProcAddress(string proc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern IntPtr wglGetCurrentContext();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint EglGetCurrentDisplayDel();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint EglGetCurrentContextDel();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint EglGetProcAddressDel(nint procName);

    private static IntPtr? s_opengl32Module;
    private static IntPtr? s_eglModule;
    private static IntPtr? s_glesModule;
    private static EglGetCurrentDisplayDel? s_eglGetCurrentDisplay;
    private static EglGetCurrentContextDel? s_eglGetCurrentContext;
    private static EglGetProcAddressDel? s_eglGetProcAddress;
    private static bool s_preferEglProcResolver;

    public static GlInterface? TryCreateFromCurrentContext()
    {
        if (!HasActiveRenderContext())
            return null;

        foreach (var version in new[]
                 {
                     new GlVersion(GlProfileType.OpenGLES, 3, 0),
                     new GlVersion(GlProfileType.OpenGLES, 2, 0),
                     new GlVersion(GlProfileType.OpenGL, 4, 6),
                     new GlVersion(GlProfileType.OpenGL, 3, 3),
                 })
        {
            try
            {
                var gl = new GlInterface(version, ResolveProcAddress);
                var testTexture = gl.GenTexture();
                if (testTexture != 0)
                {
                    gl.DeleteTexture(testTexture);
                    return gl;
                }
            }
            catch
            {
                // Try the next profile.
            }
        }

        return null;
    }

    private static bool HasActiveRenderContext()
    {
        s_preferEglProcResolver = false;

        if (wglGetCurrentContext() != IntPtr.Zero)
            return true;

        if (!TryEnsureEglExports())
            return false;

        if (s_eglGetCurrentContext?.Invoke() != nint.Zero)
        {
            s_preferEglProcResolver = true;
            return true;
        }

        return false;
    }

    private static IntPtr ResolveProcAddress(string name)
    {
        if (s_preferEglProcResolver)
        {
            var eglProc = ResolveEglProcAddress(name);
            if (IsValidProc(eglProc))
                return eglProc;
        }

        var proc = wglGetProcAddress(name);
        if (IsValidProc(proc))
            return proc;

        s_opengl32Module ??= NativeLibrary.Load("opengl32.dll");
        if (NativeLibrary.TryGetExport(s_opengl32Module.Value, name, out var export) && IsValidProc(export))
            return export;

        return ResolveEglProcAddress(name);
    }

    private static IntPtr ResolveEglProcAddress(string name)
    {
        if (!TryEnsureEglExports())
            return IntPtr.Zero;

        var procName = Marshal.StringToHGlobalAnsi(name);
        try
        {
            var proc = s_eglGetProcAddress?.Invoke(procName) ?? nint.Zero;
            return IsValidProc(proc) ? proc : IntPtr.Zero;
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

        foreach (var name in new[] { "libEGL.dll", "EGL.dll" })
        {
            if (!NativeLibrary.TryLoad(name, out var eglModule))
                continue;

            s_eglModule = eglModule;
            if (!NativeLibrary.TryGetExport(eglModule, "eglGetProcAddress", out var pGetProc) ||
                !NativeLibrary.TryGetExport(eglModule, "eglGetCurrentDisplay", out var pGetDisplay) ||
                !NativeLibrary.TryGetExport(eglModule, "eglGetCurrentContext", out var pGetContext))
            {
                continue;
            }

            s_eglGetProcAddress = Marshal.GetDelegateForFunctionPointer<EglGetProcAddressDel>(pGetProc);
            s_eglGetCurrentDisplay = Marshal.GetDelegateForFunctionPointer<EglGetCurrentDisplayDel>(pGetDisplay);
            s_eglGetCurrentContext = Marshal.GetDelegateForFunctionPointer<EglGetCurrentContextDel>(pGetContext);
            NativeLibrary.TryLoad("libGLESv2.dll", out var glesModule);
            if (glesModule != IntPtr.Zero)
                s_glesModule = glesModule;
            return true;
        }

        return false;
    }

    private static bool IsValidProc(IntPtr proc) =>
        proc != IntPtr.Zero &&
        proc != new IntPtr(1) &&
        proc != new IntPtr(2) &&
        proc != new IntPtr(3) &&
        proc != new IntPtr(-1);
}
