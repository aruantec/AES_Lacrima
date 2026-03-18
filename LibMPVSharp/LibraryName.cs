using System.IO;
using System.Reflection;

namespace LibMPVSharp
{
    public static class LibraryName
    {
        public const string Name = "libmpv";

        /// <summary>
        /// Directory to use when loading the native libmpv binary.
        /// If null or empty, the runtime's default native library search paths will be used.
        /// </summary>
        public static string? LibraryDirectory { get; set; } = AppContext.BaseDirectory;

        public static string WindowsLibrary { get; set; } = "libmpv-2";
        public static string LinuxLibrary { get; set; } = "libmpv.so";
        public static string AndroidLibrary { get; set; } = "libmpv.so";
        public static string MacLibrary { get; set; } = "libmpv.2.dylib";

        internal static void DllImportResolver()
        {
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), DllImportResolver);
        }

        private static string ResolvePath(string libraryFilename)
        {
            if (string.IsNullOrEmpty(libraryFilename))
                return libraryFilename;

            if (Path.IsPathRooted(libraryFilename) || string.IsNullOrEmpty(LibraryDirectory))
                return libraryFilename;

            return Path.Combine(LibraryDirectory, libraryFilename);
        }

        private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == Name)
            {
                if (OperatingSystem.IsWindows())
                {
                    return NativeLibrary.Load(ResolvePath(WindowsLibrary), assembly, searchPath);
                }
                else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
                {
                    return NativeLibrary.Load(ResolvePath(MacLibrary), assembly, searchPath);
                }
                else if (OperatingSystem.IsAndroid())
                {
                    return NativeLibrary.Load(ResolvePath(AndroidLibrary), assembly, searchPath);
                }
                else if (OperatingSystem.IsLinux())
                {
                    return NativeLibrary.Load(ResolvePath(LinuxLibrary), assembly, searchPath);
                }
            }
            return IntPtr.Zero;
        }
    }
}
