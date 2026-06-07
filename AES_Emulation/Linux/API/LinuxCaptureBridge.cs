using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AES_Emulation.Linux.API;

[SupportedOSPlatform("linux")]
internal static class LinuxCaptureBridge
{
    private const string LibraryName = "libAesLinuxCaptureBridge";
    private const string LinuxFileName = "libAesLinuxCaptureBridge.so";

    static LinuxCaptureBridge()
    {
        if (!OperatingSystem.IsLinux())
            return;

        NativeLibrary.SetDllImportResolver(typeof(LinuxCaptureBridge).Assembly, ResolveNativeLibrary);
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, LinuxFileName),
                     Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LinuxFileName),
                 })
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                return NativeLibrary.Load(candidate, assembly, searchPath);
            }
            catch (Exception)
            {
                // Fall through to default loader.
            }
        }

        return NativeLibrary.Load(LinuxFileName, assembly, searchPath);
    }

    [DllImport(LibraryName)]
    public static extern IntPtr aes_linux_capture_create(IntPtr parentWindowHandle);

    [DllImport(LibraryName)]
    public static extern IntPtr aes_linux_capture_create_headless();

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_destroy(IntPtr capture);

    [DllImport(LibraryName)]
    public static extern IntPtr aes_linux_capture_get_view(IntPtr capture);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_host_size(IntPtr capture, int width, int height);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_crop_frame_extents(IntPtr capture, int cropFrameExtents);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_target(IntPtr capture, int processId, string? windowTitleHint);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_target_window(IntPtr capture, IntPtr windowHandle);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_stop(IntPtr capture);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_forward_focus(IntPtr capture);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_stretch(IntPtr capture, int stretch);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_render_options(
        IntPtr capture,
        float brightness,
        float saturation,
        float tintR,
        float tintG,
        float tintB,
        float tintA);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_crop_insets(IntPtr capture, int left, int top, int right, int bottom);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_capture_behavior(IntPtr capture, int hideTargetWindowAfterCaptureStarts);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_use_pipewire(IntPtr capture, int usePipeWire);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_disable_vsync(IntPtr capture, int disableVsync);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_set_shader_path(IntPtr capture, string? shaderPath);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_reveal_window(IntPtr platformWindowHandle);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_hide_window(IntPtr platformWindowHandle);

    [DllImport(LibraryName)]
    public static extern IntPtr aes_linux_capture_find_window_by_pid(int pid, string? titleHint);

    [DllImport(LibraryName)]
    public static extern int aes_linux_capture_is_active(IntPtr capture);

    [DllImport(LibraryName)]
    public static extern int aes_linux_capture_is_initializing(IntPtr capture);

    [DllImport(LibraryName)]
    public static extern int aes_linux_capture_try_acquire_frame(IntPtr capture, out LinuxExportFrame frame);

    [DllImport(LibraryName)]
    public static extern void aes_linux_capture_release_frame(IntPtr capture, ulong frameId);

    [DllImport(LibraryName)]
    public static extern int aes_linux_capture_copy_held_frame(IntPtr capture, ulong frameId, IntPtr dst, UIntPtr dstSize);

    [DllImport(LibraryName)]
    public static extern double aes_linux_capture_get_fps(IntPtr capture);

    [DllImport(LibraryName)]
    public static extern double aes_linux_capture_get_frame_time_ms(IntPtr capture);

    [DllImport(LibraryName)]
    private static extern int aes_linux_capture_get_status_text(IntPtr capture, StringBuilder buffer, int bufferChars);

    [DllImport(LibraryName)]
    private static extern int aes_linux_capture_get_gpu_renderer(IntPtr capture, StringBuilder buffer, int bufferChars);

    [DllImport(LibraryName)]
    private static extern int aes_linux_capture_get_gpu_vendor(IntPtr capture, StringBuilder buffer, int bufferChars);

    public static string GetStatusText(IntPtr capture) => GetString(capture, aes_linux_capture_get_status_text, string.Empty);

    public static string GetGpuRenderer(IntPtr capture) => GetString(capture, aes_linux_capture_get_gpu_renderer, string.Empty);

    public static string GetGpuVendor(IntPtr capture) => GetString(capture, aes_linux_capture_get_gpu_vendor, string.Empty);

    private static string GetString(IntPtr capture, Func<IntPtr, StringBuilder, int, int> getter, string fallback)
    {
        var buffer = new StringBuilder(512);
        var length = getter(capture, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : fallback;
    }

    public static bool IsCaptureActive(IntPtr capture) => aes_linux_capture_is_active(capture) != 0;

    public static bool IsCaptureInitializing(IntPtr capture) => aes_linux_capture_is_initializing(capture) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LinuxExportFrame
{
    public int Width;
    public int Height;
    public int Stride;
    public int Offset;
    public uint DrmFourcc;
    public uint SpaFormat;
    public int IsDmabuf;
    public int DmabufFd;
    public IntPtr CpuPixels;
    public UIntPtr CpuSize;
    public ulong FrameId;
}
