using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AES_Core.Logging;
using log4net;

namespace AES_Lacrima.Services.Steam;

/// <summary>
/// Applies Steam Input / overlay environment for direct Proton launches.
/// Normal Steam launches preload gameoverlayrenderer.so (Steam Input depends on it).
/// gamescope --steam is intentionally not used: it breaks headless PipeWire capture.
/// Physical controllers are hidden and Steam's virtual gamepad is used when Steam is running.
/// </summary>
internal static class SteamControllerEnvironmentHelper
{
    private const string EnableConfiguratorSupportValue = "4097";
    private const string LaunchAlongsideSteamService = "com.steampowered.PressureVessel.LaunchAlongsideSteam";

    private static readonly ILog Log = LogHelper.For(typeof(SteamControllerEnvironmentHelper));

    private static readonly Regex IgnoreDevicesRegex = new(
        @"^SDL_GAMECONTROLLER_IGNORE_DEVICES=(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool ShouldApplySteamInputRouting()
        => OperatingSystem.IsLinux() && SteamClientIpcHelper.IsSteamClientRunning();

    public static IEnumerable<string> BuildEnvironmentAssignments(
        string libraryRoot,
        string appId,
        string installDirectory,
        string? protonDirectory = null,
        bool applySteamInputDeviceFilters = false)
    {
        yield return $"EnableConfiguratorSupport={EnableConfiguratorSupportValue}";
        yield return "SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD=1";
        yield return "SDL_JOYSTICK_HIDAPI_STEAMXBOX=0";
        yield return "STEAM_COMPAT_PROTON=1";
        yield return "STEAM_COMPAT_FLAGS=search-cwd";
        yield return $"STEAM_COMPAT_INSTALL_PATH={installDirectory}";
        yield return $"STEAM_COMPAT_LIBRARY_PATHS={Path.Combine(libraryRoot, "steamapps")}";
        yield return $"STEAM_BASE_FOLDER={libraryRoot}";
        yield return $"SRT_LAUNCHER_SERVICE_ALONGSIDE_STEAM={LaunchAlongsideSteamService}";

        // Overlay preload is only needed for full Steam Input routing. It can hang older 32-bit
        // steam_api titles during Wine bootstrap when physical controllers are used instead.
        if (applySteamInputDeviceFilters)
        {
            var overlayPreload = TryResolveGameOverlayRendererPreload(libraryRoot);
            if (!string.IsNullOrWhiteSpace(overlayPreload))
                yield return $"LD_PRELOAD={overlayPreload}";
        }

        var compatMounts = BuildCompatMounts(libraryRoot, protonDirectory);
        if (!string.IsNullOrWhiteSpace(compatMounts))
            yield return $"STEAM_COMPAT_MOUNTS={compatMounts}";

        var toolPaths = BuildCompatToolPaths(libraryRoot, protonDirectory);
        if (!string.IsNullOrWhiteSpace(toolPaths))
            yield return $"STEAM_COMPAT_TOOL_PATHS={toolPaths}";

        var shaderCachePath = Path.Combine(libraryRoot, "steamapps", "shadercache", appId);
        if (Directory.Exists(shaderCachePath))
        {
            yield return $"STEAM_COMPAT_SHADER_PATH={shaderCachePath}";
            yield return $"STEAM_COMPAT_TRANSCODED_MEDIA_PATH={shaderCachePath}";
        }

        var ignoreDevices = applySteamInputDeviceFilters
            ? TryResolveSdlGameControllerIgnoreDevices(libraryRoot, appId)
            : null;
        if (!string.IsNullOrWhiteSpace(ignoreDevices))
            yield return $"SDL_GAMECONTROLLER_IGNORE_DEVICES={ignoreDevices}";
    }

    internal static string? TryResolveGameOverlayRendererPreload(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            return null;

        var overlay32 = Path.Combine(libraryRoot, "ubuntu12_32", "gameoverlayrenderer.so");
        var overlay64 = Path.Combine(libraryRoot, "ubuntu12_64", "gameoverlayrenderer.so");
        if (!File.Exists(overlay32) || !File.Exists(overlay64))
        {
            Log.Debug(
                $"Steam overlay preload skipped: gameoverlayrenderer.so not found under '{libraryRoot}'.");
            return null;
        }

        // Match Steam's pressure-vessel ld-preloads format (leading colon appends).
        return $":{overlay32}:{overlay64}";
    }

    internal static string? BuildCompatMounts(string libraryRoot, string? protonDirectory)
    {
        var mounts = new List<string>();
        TryAddExistingDirectory(mounts, Path.Combine(libraryRoot, "steamapps", "common", "Steamworks Shared"));
        TryAddExistingDirectory(mounts, protonDirectory);
        TryAddExistingDirectory(mounts, Path.Combine(libraryRoot, "steamapps", "common", "SteamLinuxRuntime_4"));
        return mounts.Count == 0 ? null : string.Join(':', mounts);
    }

    internal static string? BuildCompatToolPaths(string libraryRoot, string? protonDirectory)
    {
        var toolPaths = new List<string>();
        TryAddExistingDirectory(toolPaths, protonDirectory);
        TryAddExistingDirectory(toolPaths, Path.Combine(libraryRoot, "steamapps", "common", "SteamLinuxRuntime_4"));
        return toolPaths.Count == 0 ? null : string.Join(':', toolPaths);
    }

    internal const string DefaultSteamInputIgnoreDevices =
        "0x054c/0x05c4,0x054c/0x09cc,0x054c/0x0ba0,0x054c/0x0ce6,0x054c/0x0df2";

    internal static string? TryResolveSdlGameControllerIgnoreDevices(string libraryRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(appId))
            return DefaultSteamInputIgnoreDevices;

        try
        {
            var runtimeVarDirectory = Path.Combine(
                libraryRoot,
                "steamapps",
                "common",
                "SteamLinuxRuntime_4",
                "var");

            if (!Directory.Exists(runtimeVarDirectory))
                return DefaultSteamInputIgnoreDevices;

            var fromAppLog = TryResolveIgnoreDevicesFromLatestLog(
                runtimeVarDirectory,
                $"slr-app{appId}-t*.log");
            if (!string.IsNullOrWhiteSpace(fromAppLog))
                return fromAppLog;

            var fromAnyLog = TryResolveIgnoreDevicesFromLatestLog(runtimeVarDirectory, "slr-app*-t*.log");
            if (!string.IsNullOrWhiteSpace(fromAnyLog))
                return fromAnyLog;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to resolve SDL_GAMECONTROLLER_IGNORE_DEVICES for app '{appId}'.", ex);
        }

        return DefaultSteamInputIgnoreDevices;
    }

    private static string? TryResolveIgnoreDevicesFromLatestLog(string runtimeVarDirectory, string pattern)
    {
        var latestLog = Directory.EnumerateFiles(runtimeVarDirectory, pattern)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(latestLog)
            ? null
            : ParseIgnoreDevicesFromRuntimeLog(latestLog);
    }

    internal static string? ParseIgnoreDevicesFromRuntimeLog(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return null;

        foreach (var line in File.ReadLines(logPath))
        {
            if (!line.Contains("SDL_GAMECONTROLLER_IGNORE_DEVICES=", StringComparison.Ordinal))
                continue;

            var markerIndex = line.IndexOf("SDL_GAMECONTROLLER_IGNORE_DEVICES=", StringComparison.Ordinal);
            var candidate = line[markerIndex..].Trim();
            if (candidate.EndsWith('\''))
                candidate = candidate[..^1];

            var match = IgnoreDevicesRegex.Match(candidate);
            if (match.Success)
            {
                var value = match.Groups["value"].Value.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static void TryAddExistingDirectory(IList<string> paths, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        if (!paths.Contains(directory, StringComparer.Ordinal))
            paths.Add(directory);
    }
}
