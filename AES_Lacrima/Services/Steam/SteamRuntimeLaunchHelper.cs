using System;
using System.IO;
using AES_Core.Logging;
using log4net;

namespace AES_Lacrima.Services.Steam;

/// <summary>
/// Resolves Steam Linux Runtime launch entry points used by the Steam client.
/// Direct <c>python3 proton</c> launches miss pressure-vessel libraries (GStreamer, 32-bit
/// support, etc.) and break intro videos and older Proton titles.
/// </summary>
internal static class SteamRuntimeLaunchHelper
{
    private const string RuntimeDirectoryName = "SteamLinuxRuntime_4";
    private const string EntryPointFileName = "_v2-entry-point";

    private static readonly ILog Log = LogHelper.For(typeof(SteamRuntimeLaunchHelper));

    public static string? TryResolveEntryPoint(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            return null;

        var preferred = Path.Combine(
            libraryRoot,
            "steamapps",
            "common",
            RuntimeDirectoryName,
            EntryPointFileName);
        if (File.Exists(preferred))
            return preferred;

        var commonDirectory = Path.Combine(libraryRoot, "steamapps", "common");
        if (!Directory.Exists(commonDirectory))
            return null;

        try
        {
            foreach (var runtimeDirectory in Directory.EnumerateDirectories(commonDirectory, "SteamLinuxRuntime*"))
            {
                var candidate = Path.Combine(runtimeDirectory, EntryPointFileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed while probing Steam Linux Runtime entry points under '{commonDirectory}'.", ex);
        }

        return null;
    }
}
