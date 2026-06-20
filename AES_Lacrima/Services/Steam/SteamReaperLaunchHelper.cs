using System;
using System.Collections.Generic;
using System.IO;
using AES_Core.Logging;
using log4net;

namespace AES_Lacrima.Services.Steam;

/// <summary>
/// Wraps Proton launches the same way the Steam client does for older 32-bit steam_api titles.
/// Snap Steam games like BASEBALL STARS 2 exit immediately without reaper + launch-wrapper.
/// </summary>
internal static class SteamReaperLaunchHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(SteamReaperLaunchHelper));

    public static bool ShouldUseReaperLaunch(string clientRoot, string installDirectory)
    {
        if (SteamLinuxLaunchHelper.TryResolveSnapLaunchHome() == null)
            return false;

        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            return false;

        if (string.IsNullOrWhiteSpace(clientRoot))
            return false;

        var wrapper = Path.Combine(clientRoot, "ubuntu12_32", "steam-launch-wrapper");
        var reaper = Path.Combine(clientRoot, "ubuntu12_32", "reaper");
        if (!File.Exists(wrapper) || !File.Exists(reaper))
            return false;

        // Reaper is only for legacy 32-bit steam_api titles (e.g. BASEBALL STARS 2).
        // Modern Proton games ship steam_api64.dll and must use the runtime entry point
        // directly so gamescope keeps a sane child process tree for capture.
        return HasLegacy32BitSteamApi(installDirectory);
    }

    internal static bool HasLegacy32BitSteamApi(string installDirectory)
    {
        var steamApi32 = Path.Combine(installDirectory, "steam_api.dll");
        var steamApi64 = Path.Combine(installDirectory, "steam_api64.dll");

        if (File.Exists(steamApi64) && !File.Exists(steamApi32))
            return false;

        return File.Exists(steamApi32);
    }

    public static bool TryAppendReaperLaunchArguments(
        ICollection<string> argumentList,
        string libraryRoot,
        string appId,
        string runtimeEntryPoint,
        string protonPath,
        string gameExecutable)
    {
        var wrapper = Path.Combine(libraryRoot, "ubuntu12_32", "steam-launch-wrapper");
        var reaper = Path.Combine(libraryRoot, "ubuntu12_32", "reaper");
        if (!File.Exists(wrapper) || !File.Exists(reaper))
        {
            Log.Warn(
                $"Steam reaper launch skipped for app '{appId}': wrapper or reaper not found under '{libraryRoot}'.");
            return false;
        }

        argumentList.Add(wrapper);
        argumentList.Add("--");
        argumentList.Add(reaper);
        argumentList.Add($"SteamLaunch AppId={appId}");
        argumentList.Add("--");
        argumentList.Add(runtimeEntryPoint);
        argumentList.Add("--verb=waitforexitandrun");
        argumentList.Add("--");
        argumentList.Add(protonPath);
        argumentList.Add("waitforexitandrun");
        argumentList.Add(gameExecutable);
        return true;
    }
}
