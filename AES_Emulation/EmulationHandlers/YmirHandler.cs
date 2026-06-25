using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AES_Emulation.Linux;

namespace AES_Emulation.EmulationHandlers;

/// <summary>
/// Standalone handler for <see href="https://github.com/StrikerX3/Ymir">Ymir</see> (Sega Saturn).
/// </summary>
public sealed class YmirHandler : EmulatorHandlerBase
{
    public static YmirHandler Instance { get; } = new();

    private YmirHandler()
    {
    }

    public override string HandlerId => "ymir";

    public override string SectionKey => "SATURN";

    public override string SectionTitle => "Sega Saturn";

    public override string DisplayName => "Ymir";

    public override bool HideUntilCaptured => true;

    public override double? CaptureWindowAspectRatio => 4.0 / 3.0;

    public override int CaptureStartupDelayMs => 300;

    public override string LinuxGamescopeScalingMode => "fill";

    /// <summary>
    /// Fraction of compositor height to crop from the top to hide Ymir's menu bar.
    /// </summary>
    public const double LinuxGamescopeMenuCropHeightFraction = 0.058;

    /// <summary>
    /// Fraction of compositor height to crop from the bottom to hide Ymir's status/chrome area.
    /// </summary>
    public const double LinuxGamescopeStatusCropHeightFraction = 0.02;

    public static int ComputeLinuxGamescopeBottomCrop(int height, int topCrop)
    {
        if (height <= 0)
            return 0;

        _ = topCrop;
        return (int)Math.Round(height * LinuxGamescopeStatusCropHeightFraction);
    }

    public override int ClientAreaCropTopInset => 0;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Saturn", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Sega Saturn", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        var profileDirectory = ResolvePortableProfileDirectory(startInfo.FileName, startInfo.WorkingDirectory);
        EnsurePortableProfile(profileDirectory);

        var linuxGamescopeLaunch = OperatingSystem.IsLinux();
        var launchFullscreen = startFullscreen || linuxGamescopeLaunch;
        ApplyAesLaunchProfileSettings(profileDirectory, launchFullscreen, linuxGamescopeLaunch);

        if (OperatingSystem.IsLinux())
            LinuxAudioEnvironmentHelper.Apply(startInfo);

        if (launchFullscreen)
            startInfo.ArgumentList.Insert(0, "--fullscreen");

        if (!string.IsNullOrWhiteSpace(profileDirectory))
        {
            var profileInsertIndex = startInfo.ArgumentList.Count - 1;
            startInfo.ArgumentList.Insert(profileInsertIndex, profileDirectory);
            startInfo.ArgumentList.Insert(profileInsertIndex, "--profile");
        }

        return startInfo;
    }

    public override ProcessStartInfo BuildSetupStartInfo(string? launcherPath, string? preferredEmulatorDirectory = null)
    {
        var startInfo = base.BuildSetupStartInfo(launcherPath, preferredEmulatorDirectory);
        var profileDirectory = ResolvePortableProfileDirectory(startInfo.FileName, startInfo.WorkingDirectory);
        EnsurePortableProfile(profileDirectory);
        return startInfo;
    }

    public static void EnsurePortableProfile(string? profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
            return;

        try
        {
            Directory.CreateDirectory(profileDirectory);
            Directory.CreateDirectory(Path.Combine(profileDirectory, "roms", "ipl"));
        }
        catch
        {
            // Best effort; launch still passes --profile when available.
        }
    }

    internal static void ApplyAesLaunchProfileSettings(
        string? profileDirectory,
        bool launchFullscreen,
        bool linuxGamescopeLaunch = false)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
            return;

        try
        {
            var tomlPath = Path.Combine(profileDirectory, "Ymir.toml");
            var content = File.Exists(tomlPath)
                ? File.ReadAllText(tomlPath)
                : "ConfigVersion = 5" + Environment.NewLine;

            var updates = new List<(string Section, string Key, string Value)>
            {
                ("General", "PauseWhenUnfocused", "false"),
            };

            if (linuxGamescopeLaunch || launchFullscreen)
            {
                updates.Add(("Video", "FullScreen", "true"));
                updates.Add(("Video", "DisplayVideoOutputInWindow", "false"));
                updates.Add(("Video.FullScreenMode", "Borderless", "true"));
                updates.Add(("Video", "AutoResizeWindow", "false"));
                updates.Add(("Video", "ForceAspectRatio", "true"));
                updates.Add(("Video", "ForcedAspect", "1.3333333333333333"));
            }

            if (linuxGamescopeLaunch || OperatingSystem.IsLinux())
            {
                updates.Add(("GUI", "ShowGameNameOnTitleBar", "false"));
                updates.Add(("GUI", "ShowPerformanceOnTitleBar", "false"));
            }

            File.WriteAllText(tomlPath, ApplyTomlUpdates(content, updates));
        }
        catch
        {
            // Best effort; launch arguments still apply when available.
        }
    }

    internal static string ApplyTomlUpdates(string content, IReadOnlyList<(string Section, string Key, string Value)> updates)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();

        foreach (var (section, key, value) in updates)
        {
            var sectionHeader = $"[{section}]";
            var inSection = false;
            var found = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection || string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                    continue;

                if (!TryParseTomlKeyLine(trimmed, out var lineKey) ||
                    !string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var indent = lines[i].TakeWhile(char.IsWhiteSpace).Count();
                var prefix = new string(' ', indent);
                lines[i] = $"{prefix}{key} = {value}";
                found = true;
                break;
            }

            if (!found)
                InsertTomlKey(lines, section, key, value);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool TryParseTomlKeyLine(string trimmed, out string key)
    {
        key = string.Empty;
        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex <= 0)
            return false;

        key = trimmed[..equalsIndex].Trim();
        return key.Length > 0;
    }

    private static void InsertTomlKey(List<string> lines, string section, string key, string value)
    {
        var sectionHeader = $"[{section}]";
        var sectionIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionIndex = i;
                break;
            }
        }

        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);

            lines.Add(sectionHeader);
            lines.Add($"{key} = {value}");
            return;
        }

        var insertIndex = sectionIndex + 1;
        while (insertIndex < lines.Count)
        {
            var trimmed = lines[insertIndex].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                break;

            insertIndex++;
        }

        lines.Insert(insertIndex, $"{key} = {value}");
    }

    public static string? ResolvePortableProfileDirectory(string? executablePath, string? workingDirectory)
    {
        var profileDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
            ? workingDirectory
            : Path.GetDirectoryName(executablePath ?? string.Empty);

        if (string.IsNullOrWhiteSpace(profileDirectory))
            return null;

        return FindYmirInstallRoot(profileDirectory) ?? profileDirectory;
    }

    private static string? FindYmirInstallRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            if (string.Equals(current.Name, "Ymir", StringComparison.OrdinalIgnoreCase))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyYmirRenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyYmirRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyYmirRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var lowerTitle = title.ToLowerInvariant();

            if (lowerTitle.Contains("ymir") &&
                !lowerTitle.Contains("settings") &&
                !lowerTitle.Contains("debugger") &&
                !lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = false;
            }

            if (lowerTitle.Contains("settings") ||
                lowerTitle.Contains("debugger") ||
                lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            var lowerClass = className.ToLowerInvariant();
            if (lowerClass.Contains("sdl") || lowerClass.Contains("glfw") || lowerClass.Contains("ymir"))
                looksLikePrimaryUi |= hasCaption && hasThickFrame;
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
    }
}
