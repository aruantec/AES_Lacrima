using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AES_Core.Logging;
using AES_Emulation.Steam;
using log4net;

namespace AES_Lacrima.Services.Steam;

/// <summary>
/// Launches installed Steam games directly (Proton or native) inside gamescope.
/// The Steam client wrapper exits immediately when spawned as a gamescope child (Snap/Flatpak),
/// so we bypass <c>steam -applaunch</c> and run the game binary instead.
/// </summary>
public static class SteamLinuxLaunchHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(SteamLinuxLaunchHelper));

    public static bool TryPrepareDirectLaunch(
        ProcessStartInfo startInfo,
        string? romPath,
        SteamProtonLaunchPreferences? preferences = null)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var appId = SteamGamePath.GetAppId(romPath);
        if (string.IsNullOrWhiteSpace(appId))
            return false;

        var game = SteamInstalledGameHelper.GetInstalledGame(appId);
        if (game == null)
        {
            Log.Warn($"Steam direct launch skipped: installed game not found for app id '{appId}'.");
            return false;
        }

        if (SteamInstalledGameHelper.TryResolveProtonLaunch(game, out var protonPath, out var gameExecutable, preferences))
        {
            var clientRoot = SteamInstalledGameHelper.TryResolveSteamClientRoot() ?? game.LibraryRoot;
            SteamClientIpcHelper.EnsureUserSteamDirectory(clientRoot);
            return ApplyProtonLaunch(startInfo, game, clientRoot, protonPath, gameExecutable);
        }

        if (SteamInstalledGameHelper.TryResolveNativeLaunch(game, out var nativeExecutable))
            return ApplyNativeLaunch(startInfo, game, nativeExecutable);

        Log.Warn($"Steam direct launch skipped: no Proton or native executable resolved for '{game.Name}' ({appId}).");
        return false;
    }

    private static bool ApplyProtonLaunch(
        ProcessStartInfo startInfo,
        SteamInstalledGame game,
        string clientRoot,
        string protonPath,
        string gameExecutable)
    {
        var compatDataPath = Path.Combine(game.LibraryRoot, "steamapps", "compatdata", game.AppId);

        EnsureSteamAppIdFile(game.InstallDirectory, game.AppId);

        startInfo.UseShellExecute = false;
        startInfo.WorkingDirectory = game.InstallDirectory;
        startInfo.ArgumentList.Clear();

        // Pass Proton/Steam env on the env(1) command line so gamescope's child receives them
        // reliably. Proton exits immediately with "No compat data path?" otherwise.
        startInfo.FileName = ResolveEnvExecutable();
        foreach (var assignment in BuildProtonEnvironmentAssignments(
                     clientRoot,
                     game.LibraryRoot,
                     game.AppId,
                     compatDataPath,
                     game.InstallDirectory,
                     Path.GetDirectoryName(protonPath)))
            startInfo.ArgumentList.Add(assignment);

        var runtimeEntryPoint = SteamRuntimeLaunchHelper.TryResolveEntryPoint(clientRoot);
        if (!string.IsNullOrWhiteSpace(runtimeEntryPoint))
        {
            if (SteamReaperLaunchHelper.ShouldUseReaperLaunch(clientRoot, game.InstallDirectory) &&
                SteamReaperLaunchHelper.TryAppendReaperLaunchArguments(
                    startInfo.ArgumentList,
                    clientRoot,
                    game.AppId,
                    runtimeEntryPoint,
                    protonPath,
                    gameExecutable))
            {
                Log.Info(
                    $"Steam reaper Proton launch: appId={game.AppId}, runtime='{runtimeEntryPoint}', proton='{protonPath}', " +
                    $"game='{gameExecutable}', compat='{compatDataPath}', clientRoot='{clientRoot}'.");
                return true;
            }

            startInfo.ArgumentList.Add(runtimeEntryPoint);
            startInfo.ArgumentList.Add("--verb=waitforexitandrun");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(protonPath);
            startInfo.ArgumentList.Add("waitforexitandrun");
            startInfo.ArgumentList.Add(gameExecutable);

            Log.Info(
                $"Steam runtime Proton launch: appId={game.AppId}, runtime='{runtimeEntryPoint}', proton='{protonPath}', " +
                $"game='{gameExecutable}', compat='{compatDataPath}', clientRoot='{clientRoot}'.");
            return true;
        }

        Log.Warn(
            $"Steam Linux Runtime entry point not found for '{game.LibraryRoot}'. " +
            "Falling back to direct python3 Proton launch.");

        var pythonPath = ResolvePythonExecutable();
        startInfo.ArgumentList.Add(pythonPath);
        startInfo.ArgumentList.Add(protonPath);
        startInfo.ArgumentList.Add("waitforexitandrun");
        startInfo.ArgumentList.Add(gameExecutable);

            Log.Info(
                $"Steam direct Proton launch: appId={game.AppId}, python='{pythonPath}', proton='{protonPath}', " +
                $"game='{gameExecutable}', compat='{compatDataPath}', " +
                $"steamInputRouting={SteamControllerEnvironmentHelper.ShouldApplySteamInputRouting()}.");
        return true;
    }

    internal static IEnumerable<string> BuildProtonEnvironmentAssignments(
        string clientRoot,
        string gameLibraryRoot,
        string appId,
        string compatDataPath,
        string installDirectory,
        string? protonDirectory = null)
    {
        yield return $"STEAM_COMPAT_DATA_PATH={compatDataPath}";
        yield return $"STEAM_COMPAT_CLIENT_INSTALL_PATH={clientRoot}";
        yield return $"STEAM_COMPAT_APP_ID={appId}";
        yield return $"SteamAppId={appId}";
        yield return $"SteamGameId={appId}";

        foreach (var assignment in SteamControllerEnvironmentHelper.BuildEnvironmentAssignments(
                     clientRoot,
                     appId,
                     installDirectory,
                     protonDirectory,
                     applySteamInputDeviceFilters: SteamControllerEnvironmentHelper.ShouldApplySteamInputRouting()))
        {
            yield return assignment;
        }

        yield return "SDL_VIDEODRIVER=x11";
        yield return "GDK_BACKEND=x11";
        yield return "QT_QPA_PLATFORM=xcb";

        var launchHome = TryResolveSnapLaunchHome();
        yield return launchHome != null
            ? $"PATH={BuildSnapLaunchPath(clientRoot)}"
            : $"PATH={BuildLaunchPath()}";

        foreach (var assignment in BuildHostEnvironmentAssignments(clientRoot, installDirectory))
            yield return assignment;

        foreach (var assignment in BuildSteamGameRuntimeEnvironmentAssignments(gameLibraryRoot, appId))
            yield return assignment;
    }

    internal static IEnumerable<string> BuildHostEnvironmentAssignments(
        string clientRoot,
        string? installDirectory = null)
    {
        var snapHome = TryResolveSnapLaunchHome();
        var home = snapHome ?? Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            yield return $"HOME={home}";

        var user = Environment.GetEnvironmentVariable("USER");
        if (!string.IsNullOrWhiteSpace(user))
            yield return $"USER={user}";

        if (!string.IsNullOrWhiteSpace(snapHome))
        {
            yield return $"XDG_CACHE_HOME={Path.Combine(snapHome, ".cache")}";
            yield return $"XDG_CONFIG_HOME={Path.Combine(snapHome, ".config")}";
            yield return $"XDG_DATA_HOME={Path.Combine(snapHome, ".local", "share")}";
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir) && !string.IsNullOrWhiteSpace(home))
        {
            try
            {
                var userId = UnixUserId.GetEffectiveUserId();
                if (userId >= 0)
                {
                    var candidate = $"/run/user/{userId}";
                    if (Directory.Exists(candidate))
                        runtimeDir = candidate;
                }
            }
            catch
            {
                // ignored
            }
        }

        if (!string.IsNullOrWhiteSpace(runtimeDir))
            yield return $"XDG_RUNTIME_DIR={runtimeDir}";

        var dbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(dbus))
            yield return $"DBUS_SESSION_BUS_ADDRESS={dbus}";

        foreach (var assignment in BuildSnapLaunchEnvironmentAssignments(clientRoot, installDirectory))
            yield return assignment;
    }

    internal static string? TryResolveSnapLaunchHome()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;

        var snapCommonHome = Path.Combine(home, "snap", "steam", "common");
        return Directory.Exists(snapCommonHome) ? snapCommonHome : null;
    }

    internal static string? TryResolveLaunchHome(string libraryRoot)
    {
        var snapHome = TryResolveSnapLaunchHome();
        if (snapHome != null)
            return snapHome;

        if (string.IsNullOrWhiteSpace(libraryRoot))
            return null;

        var normalized = libraryRoot.Replace('\\', '/');
        if (!normalized.Contains("/snap/steam/", StringComparison.Ordinal))
            return null;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;

        var snapCommonHome = Path.Combine(home, "snap", "steam", "common");
        return Directory.Exists(snapCommonHome) ? snapCommonHome : null;
    }

    internal static IEnumerable<string> BuildSnapLaunchEnvironmentAssignments(
        string clientRoot,
        string? installDirectory = null)
    {
        if (TryResolveSnapLaunchHome() == null)
            yield break;

        yield return "DISABLE_WAYLAND=1";

        var revision = TryResolveSnapSteamRevision();
        if (string.IsNullOrWhiteSpace(revision))
            yield break;

        var pressureVesselLibraryPath = BuildSnapPressureVesselLibraryPath(clientRoot, revision, installDirectory);
        if (!string.IsNullOrWhiteSpace(pressureVesselLibraryPath))
        {
            yield return $"PRESSURE_VESSEL_APP_LD_LIBRARY_PATH={pressureVesselLibraryPath}";
            yield return $"LD_LIBRARY_PATH={pressureVesselLibraryPath}";
        }

        var steamRuntimeLibraryPath = BuildSteamRuntimeLibraryPath(clientRoot, revision, installDirectory);
        if (!string.IsNullOrWhiteSpace(steamRuntimeLibraryPath))
            yield return $"STEAM_RUNTIME_LIBRARY_PATH={steamRuntimeLibraryPath}";

        var gstreamerRoot = $"/snap/steam/{revision}/usr/lib/i386-linux-gnu/gstreamer-1.0";
        if (Directory.Exists(gstreamerRoot))
        {
            yield return $"GST_PLUGIN_PATH={gstreamerRoot}";
            yield return $"GST_PLUGIN_SYSTEM_PATH={gstreamerRoot}";
        }

        var scanner = $"/snap/steam/{revision}/usr/lib/i386-linux-gnu/gstreamer1.0/gstreamer-1.0/gst-plugin-scanner";
        if (File.Exists(scanner))
            yield return $"GST_PLUGIN_SCANNER={scanner}";
    }

    internal static string? BuildSnapPressureVesselLibraryPath(
        string libraryRoot,
        string revision,
        string? installDirectory = null)
    {
        var snapRoot = $"/snap/steam/{revision}";
        var entries = new List<string>();
        AddExistingDirectory(entries, $"{snapRoot}/graphics/usr/lib/i386-linux-gnu");
        AddExistingDirectory(entries, $"{snapRoot}/graphics/usr/lib");
        AddExistingDirectory(entries, $"{snapRoot}/usr/lib/i386-linux-gnu");
        AddExistingDirectory(entries, $"{snapRoot}/usr/lib/x86_64-linux-gnu");
        AddExistingDirectory(entries, $"{snapRoot}/lib/i386-linux-gnu");
        AddExistingDirectory(entries, $"{snapRoot}/usr/lib/i386-linux-gnu/pulseaudio");
        AddExistingDirectory(entries, $"{snapRoot}/usr/lib/x86_64-linux-gnu/alsa-lib");
        AddExistingDirectory(entries, $"{snapRoot}/usr/lib/x86_64-linux-gnu/pulseaudio");
        AddExistingDirectory(entries, $"{snapRoot}/graphics/usr/lib/x86_64-linux-gnu");
        AddExistingDirectory(entries, "/var/lib/snapd/lib/gl");
        AddExistingDirectory(entries, "/var/lib/snapd/lib/gl32");
        AddExistingDirectory(entries, "/usr/lib/x86_64-linux-gnu");
        AddExistingDirectory(entries, "/lib");
        AddExistingDirectory(entries, installDirectory);
        return entries.Count == 0 ? null : string.Join(':', entries);
    }

    internal static string? BuildSteamRuntimeLibraryPath(
        string libraryRoot,
        string revision,
        string? installDirectory = null)
    {
        var entries = new List<string>();
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "pinned_libs_32"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "pinned_libs_64"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "lib", "i386-linux-gnu"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "usr", "lib", "i386-linux-gnu"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "lib", "x86_64-linux-gnu"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "usr", "lib", "x86_64-linux-gnu"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "lib"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "usr", "lib"));

        var pressureVesselLibraryPath = BuildSnapPressureVesselLibraryPath(libraryRoot, revision, installDirectory);
        if (!string.IsNullOrWhiteSpace(pressureVesselLibraryPath))
        {
            foreach (var entry in pressureVesselLibraryPath.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddExistingDirectory(entries, entry);
        }

        return entries.Count == 0 ? null : string.Join(':', entries);
    }

    internal static string BuildSnapLaunchPath(string libraryRoot)
    {
        var entries = new List<string>();
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "game-bin"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "amd64", "bin"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "amd64", "usr", "bin"));
        AddExistingDirectory(entries, Path.Combine(libraryRoot, "ubuntu12_32", "steam-runtime", "usr", "bin"));

        var revision = TryResolveSnapSteamRevision();
        if (!string.IsNullOrWhiteSpace(revision))
        {
            var snapRoot = $"/snap/steam/{revision}";
            AddExistingDirectory(entries, $"{snapRoot}/usr/sbin");
            AddExistingDirectory(entries, $"{snapRoot}/usr/bin");
            AddExistingDirectory(entries, $"{snapRoot}/sbin");
            AddExistingDirectory(entries, $"{snapRoot}/bin");
        }

        foreach (var entry in BuildLaunchPath().Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddExistingDirectory(entries, entry);

        return string.Join(':', entries);
    }

    private static void AddExistingDirectory(IList<string> entries, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        if (!entries.Contains(directory, StringComparer.Ordinal))
            entries.Add(directory);
    }

    internal static string? TryResolveSnapSteamRevision()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;

        var currentLink = Path.Combine(home, "snap", "steam", "current");
        try
        {
            var target = File.ResolveLinkTarget(currentLink, returnFinalTarget: true);
            var revision = Path.GetFileName(target?.FullName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(revision) && revision.All(char.IsDigit))
                return revision;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve snap steam current revision.", ex);
        }

        try
        {
            var snapDirectory = Path.Combine(home, "snap", "steam");
            if (!Directory.Exists(snapDirectory))
                return null;

            return Directory.EnumerateDirectories(snapDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                               !string.Equals(name, "common", StringComparison.OrdinalIgnoreCase) &&
                               !string.Equals(name, "current", StringComparison.OrdinalIgnoreCase) &&
                               name.All(char.IsDigit))
                .OrderByDescending(name => int.Parse(name!, System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to enumerate snap steam revisions.", ex);
            return null;
        }
    }

    internal static IEnumerable<string> BuildSteamGameRuntimeEnvironmentAssignments(
        string libraryRoot,
        string appId)
    {
        yield return "ENABLE_VK_LAYER_VALVE_steam_fossilize_1=1";
        yield return "ENABLE_VK_LAYER_VALVE_steam_overlay_1=1";
        yield return "AMD_VK_USE_PIPELINE_CACHE=1";
        yield return "AMD_VK_PIPELINE_CACHE_FILENAME=steamapp_shader_cache";
        yield return "BREAKPAD_DUMP_LOCATION=/tmp/dumps";

        var shaderCachePath = Path.Combine(libraryRoot, "steamapps", "shadercache", appId);
        if (!Directory.Exists(shaderCachePath))
            yield break;

        yield return $"AMD_VK_PIPELINE_CACHE_PATH={Path.Combine(shaderCachePath, "AMDv1")}";
        yield return $"DXVK_STATE_CACHE_PATH={Path.Combine(shaderCachePath, "DXVK_state_cache")}";
    }

    internal static string BuildLaunchPath()
    {
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var requiredEntries = new[]
        {
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/games",
            "/snap/bin",
        };

        var entries = new List<string>();
        foreach (var entry in requiredEntries)
        {
            if (Directory.Exists(entry))
                entries.Add(entry);
        }

        foreach (var entry in existingPath.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entries.Contains(entry, StringComparer.Ordinal))
                entries.Add(entry);
        }

        return string.Join(':', entries);
    }

    private static string ResolveEnvExecutable()
    {
        foreach (var candidate in new[] { "/usr/bin/env", "/bin/env" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "env";
    }

    private static string ResolvePythonExecutable()
    {
        foreach (var candidate in new[] { "/usr/bin/python3", "/bin/python3" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "python3";
    }

    internal static void EnsureSteamAppIdFile(string installDirectory, string appId)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || string.IsNullOrWhiteSpace(appId))
            return;

        try
        {
            var path = Path.Combine(installDirectory, "steam_appid.txt");
            File.WriteAllText(path, appId.Trim());
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to write steam_appid.txt under '{installDirectory}'.", ex);
        }
    }

    private static bool ApplyNativeLaunch(
        ProcessStartInfo startInfo,
        SteamInstalledGame game,
        string nativeExecutable)
    {
        startInfo.FileName = nativeExecutable;
        startInfo.UseShellExecute = false;
        startInfo.WorkingDirectory = game.InstallDirectory;
        startInfo.ArgumentList.Clear();

        startInfo.Environment["SteamAppId"] = game.AppId;
        startInfo.Environment["SteamGameId"] = game.AppId;

        Log.Info($"Steam direct native launch: appId={game.AppId}, executable='{nativeExecutable}'.");
        return true;
    }

    private static class UnixUserId
    {
        public static int GetEffectiveUserId()
        {
            try
            {
                return (int)getuid();
            }
            catch
            {
                return -1;
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern uint getuid();
    }
}
