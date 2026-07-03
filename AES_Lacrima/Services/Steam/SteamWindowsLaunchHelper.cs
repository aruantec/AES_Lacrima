using System;
using System.Diagnostics;
using System.IO;
using AES_Core.Logging;
using AES_Emulation.Steam;
using log4net;

namespace AES_Lacrima.Services.Steam;

/// <summary>
/// Launches installed Steam games directly on Windows so the game process can be tracked
/// and placed on the Parsec virtual display. <c>steam.exe -applaunch</c> exits immediately
/// after handing off to Steam, which breaks capture and VDD placement.
/// </summary>
public static class SteamWindowsLaunchHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(SteamWindowsLaunchHelper));

    public static bool TryPrepareDirectLaunch(ProcessStartInfo startInfo, string? romPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var appId = SteamGamePath.GetAppId(romPath);
        if (string.IsNullOrWhiteSpace(appId))
            return false;

        var game = SteamInstalledGameHelper.GetInstalledGame(appId);
        if (game == null)
        {
            Log.Warn($"Steam Windows direct launch skipped: installed game not found for app id '{appId}'.");
            return false;
        }

        if (!SteamInstalledGameHelper.TryResolveGameExecutable(game.InstallDirectory, preferWindowsExecutable: true, out var gameExecutable))
        {
            Log.Warn($"Steam Windows direct launch skipped: no game executable found for '{game.Name}' ({appId}).");
            return false;
        }

        if (!IsSteamClientRunning())
        {
            Log.Warn("Steam client is not running. Start Steam before launching games from AES.");
            return false;
        }

        SteamLinuxLaunchHelper.EnsureSteamAppIdFile(game.InstallDirectory, game.AppId);

        startInfo.FileName = gameExecutable;
        startInfo.UseShellExecute = false;
        startInfo.WorkingDirectory = game.InstallDirectory;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("-windowed");
        startInfo.Environment["SteamAppId"] = game.AppId;
        startInfo.Environment["SteamGameId"] = game.AppId;
        startInfo.Environment["SDL_VIDEO_FULLSCREEN"] = "0";
        startInfo.Environment["SteamDeck"] = "0";

        Log.Info($"Steam Windows direct launch: appId={game.AppId}, executable='{gameExecutable}'.");
        return true;
    }

    public static bool IsSteamClientRunning()
    {
        if (!OperatingSystem.IsWindows())
            return SteamClientIpcHelper.IsSteamClientRunning();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals("steam", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // ignored
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed while probing for a running Steam client on Windows.", ex);
        }

        return false;
    }
}
