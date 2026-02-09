using System.Runtime.InteropServices;

namespace AES_Controls.Helpers;

/// <summary>
/// Helper responsible for ensuring a working libmpv binary is available
/// in the application's folder. If the library is missing the helper will
/// attempt to download or locate a suitable build for the current platform.
/// </summary>
public static class MpvSetup
{
    private static readonly string AppFolder = AppContext.BaseDirectory;

    /// <summary>
    /// Ensures that the platform-specific libmpv library is present in the
    /// application's folder. If the library is missing this method will
    /// attempt to download or copy an appropriate binary for the current OS.
    /// </summary>
    /// <returns>A task that completes when the check and any installation finish.</returns>
    public static async Task EnsureInstalled()
    {
        // Identify the library name for the current OS
        string libName = GetLibName();
        string fullPath = Path.Combine(AppFolder, libName);

        // CHECK: If it exists, do nothing (Directly addresses your request)
        if (File.Exists(fullPath))
        {
            return;
        }

        // SETUP: Only runs if file is missing
        Console.WriteLine($"{libName} missing. Starting automatic setup...");

        try
        {
            var manager = new MpvLibraryManager(); // From the previous code
            await manager.EnsureLibraryInstalledAsync();
        }
        catch (Exception ex)
        {
            // Log or show error if download fails
            Console.WriteLine($"Failed to auto-setup libmpv: {ex.Message}");
        }
    }

    private static string GetLibName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "libmpv-2.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libmpv.dylib";
        return "libmpv.so"; // Linux default
    }
}