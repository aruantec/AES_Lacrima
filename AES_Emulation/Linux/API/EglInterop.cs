using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux.API;

[SupportedOSPlatform("linux")]
internal static class EglInterop
{
    private const string libEGL = "libEGL.so.1";

    public const uint EGL_NATIVE_PIXMAP_KHR = 0x30B0;
    public static readonly IntPtr EGL_NO_CONTEXT = IntPtr.Zero;

    [DllImport(libEGL)]
    public static extern IntPtr eglGetCurrentDisplay();

    [DllImport(libEGL)]
    public static extern IntPtr eglCreateImageKHR(IntPtr dpy, IntPtr ctx, uint target, IntPtr buffer, int[]? attrib_list);

    [DllImport(libEGL)]
    public static extern bool eglDestroyImageKHR(IntPtr dpy, IntPtr image);
}

// OpenGL Extension Delegate
internal delegate void glEGLImageTargetTexture2DOESDelegate(uint target, IntPtr image);
