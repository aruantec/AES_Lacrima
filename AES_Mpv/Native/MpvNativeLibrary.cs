using System.Reflection;

namespace AES_Mpv.Native;

public static class MpvNativeLibrary
{
    public const string ImportName = "libmpv";

    public static string? SearchDirectory { get; set; } = AppContext.BaseDirectory;
    public static string WindowsFileName { get; set; } = "libmpv-2";
    public static string LinuxFileName { get; set; } = "libmpv.so";
    public static string AndroidFileName { get; set; } = "libmpv.so";
    public static string MacFileName { get; set; } = "libmpv.2.dylib";

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
            return NativeLibrary.Load(ResolvePath(LinuxFileName), assembly, searchPath);

        return IntPtr.Zero;
    }
}
