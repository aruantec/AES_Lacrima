using System.Reflection;
using System.Runtime.InteropServices;

namespace AES_Mpv.Native;

public static class MpvNativeLibrary
{
    public const string ImportName = "libmpv";

    public static string? SearchDirectory { get; set; } = AppContext.BaseDirectory;
    public static string WindowsFileName { get; set; } = "libmpv-2";
    public static string LinuxFileName { get; set; } = "libmpv.so";
    public static string AndroidFileName { get; set; } = "libmpv.so";
    public static string MacFileName { get; set; } = "libmpv.2.dylib";

    private static readonly string[] LinuxSonames =
    [
        "libmpv.so",
        "libmpv.so.2",
        "libmpv.so.1"
    ];

    internal static void RegisterResolver()
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveLibrary);
    }

    private static string ResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) || string.IsNullOrWhiteSpace(SearchDirectory))
            return fileName;

        return Path.Combine(SearchDirectory, fileName);
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != ImportName)
            return IntPtr.Zero;

        if (OperatingSystem.IsWindows())
            return NativeLibrary.Load(ResolvePath(WindowsFileName), assembly, searchPath);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return NativeLibrary.Load(ResolvePath(MacFileName), assembly, searchPath);

        if (OperatingSystem.IsAndroid())
        {
            try
            {
                return NativeLibrary.Load(AndroidFileName, assembly, searchPath);
            }
            catch
            {
                return NativeLibrary.Load(ResolvePath(AndroidFileName), assembly, searchPath);
            }
        }

        if (OperatingSystem.IsLinux())
            return ResolveLinuxLibrary(assembly, searchPath);

        return IntPtr.Zero;
    }

    private static IntPtr ResolveLinuxLibrary(Assembly assembly, DllImportSearchPath? searchPath)
    {
        // 1) Prefer a copy staged under Tools / SearchDirectory.
        if (!string.IsNullOrWhiteSpace(SearchDirectory))
        {
            foreach (var soname in LinuxSonames)
            {
                var candidate = Path.Combine(SearchDirectory, soname);
                if (File.Exists(candidate) &&
                    NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                {
                    return handle;
                }
            }
        }

        // 2) Fall back to the dynamic linker (distro packages on Arch/CachyOS/Ubuntu).
        //    Arch-based distros often ship only the versioned soname until -dev is installed.
        foreach (var soname in LinuxSonames)
        {
            if (NativeLibrary.TryLoad(soname, assembly, searchPath, out var handle))
                return handle;
        }

        // Let the runtime continue with its default resolution path.
        return IntPtr.Zero;
    }
}
