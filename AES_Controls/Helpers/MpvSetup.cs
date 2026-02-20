using System.Runtime.InteropServices;
using log4net;

namespace AES_Controls.Helpers;

/// <summary>
/// Helper responsible for ensuring a working libmpv binary is available
/// in the application's folder. If the library is missing the helper will
/// attempt to download or locate a suitable build for the current platform.
/// </summary>
public static class MpvSetup
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MpvSetup));
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
        bool skipAutoSetup = false;

        // CLEANUP: Attempt to remove any pending-delete/update files from previous sessions
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                string updatePath = fullPath + ".update";
                string deleteMarker = fullPath + ".delete";

                // 1. Process uninstalls stashed via .delete marker
                if (File.Exists(deleteMarker))
                {
                    skipAutoSetup = true;

                    if (File.Exists(fullPath))
                    {
                        try 
                        { 
                             File.Delete(fullPath); 
                                   try { File.Delete(deleteMarker); } catch (Exception ex) { Log.Warn($"Failed to delete {deleteMarker}", ex); }
                                  Log.Info("Applied pending libmpv uninstallation.");
                             } 
                             catch (IOException ex)
                        {
                            Log.Warn($"libmpv is locked; attempting rename trick for cleanup: {ex.Message}");
                            // If locked at startup, move it out of the way using a unique GUID name
                            try 
                            { 
                                string tempDel = fullPath + "." + Guid.NewGuid().ToString("N") + ".delete";
                                File.Move(fullPath, tempDel);
                            } catch (Exception moveEx) { Log.Error($"Rename trick failed for {fullPath}", moveEx); }
                        }
                    }
                    else
                    {
                        // Library is already gone, remove the marker too
                        try { File.Delete(deleteMarker); } catch (Exception ex) { Log.Warn($"Failed to remove stale {deleteMarker}", ex); }
                    }
                }

                // 2. Process updates stashed as .update (usually when rename trick failed)
                if (!skipAutoSetup && File.Exists(updatePath))
                {
                    try
                    {
                        if (File.Exists(fullPath))
                        {
                             try { File.Delete(fullPath); }
                             catch (IOException ex)
                             {
                                 Log.Warn($"libmpv is locked during update; attempting rename trick: {ex.Message}");
                                 // Rename trick as fallback at startup
                                 string tempDel = fullPath + "." + Guid.NewGuid().ToString("N") + ".delete";
                                 try { File.Move(fullPath, tempDel); } catch (Exception moveEx) { Log.Error($"Rename trick failed for {fullPath} update", moveEx); }
                             }
                        }
                             File.Move(updatePath, fullPath);
                            Log.Info("Applied pending libmpv update.");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Could not apply pending update: {ex.Message}", ex);
                        }
                }

                // 3. Absolute cleanup for any GUID-based .delete files from THIS or previous runs
                if (Directory.Exists(AppFolder))
                {
                    foreach (var delFile in Directory.EnumerateFiles(AppFolder, libName + ".*.delete"))
                    {
                        try { File.Delete(delFile); } catch (Exception ex) { Log.Warn($"Failed to cleanup {delFile}", ex); }
                    }
                    // Legacy cleanup
                    foreach (var oldFile in Directory.EnumerateFiles(AppFolder, libName + ".*.old"))
                    {
                        try { File.Delete(oldFile); } catch (Exception ex) { Log.Warn($"Failed to cleanup {oldFile}", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("An error occurred during MpvSetup cleanup", ex);
            }
        }

        if (skipAutoSetup) return;

        // SETUP: Always attempt to initialize through manager to get status if needed
        try
        {
            // Resolve the manager from DI if available, otherwise fallback to new instance
            var manager = AES_Core.DI.DiLocator.ResolveViewModel<MpvLibraryManager>() ?? new MpvLibraryManager();
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