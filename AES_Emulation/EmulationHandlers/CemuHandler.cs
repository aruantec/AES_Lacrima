using AES_Emulation.Windows.API;
using AES_Emulation.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using log4net;
using AES_Core.Logging;
namespace AES_Emulation.EmulationHandlers;

public sealed class CemuHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<CemuHandler>();
    private const string ReadyOutputToken = "------- Run title -------";
    private const int StretchFullscreenScaling = 1;

    private string? _fullscreenScalingSettingsPath;
    private string? _fullscreenScalingOriginalValue;
    private bool _fullscreenScalingElementExisted;
    private bool _fullscreenScalingWorkaroundApplied;

    private static readonly string[] CemuExecutableNames =
    [
        "Cemu.exe",
        "cemu.exe",
        "Cemu",
        "cemu"
    ];

    public static CemuHandler Instance { get; } = new();

    private CemuHandler()
    {
    }

    public override string HandlerId => "cemu";

    public override string SectionKey => "WIIU";

    public override string SectionTitle => "Wii U";

    public override string DisplayName => "Cemu";

    public override bool IsLauncherPathValid(string? launcherPath)
        => !string.IsNullOrWhiteSpace(ResolveCemuLauncherPath(launcherPath));

    public override string? NormalizeLauncherPath(string? launcherPath)
        => ResolveCemuLauncherPath(launcherPath) ?? base.NormalizeLauncherPath(launcherPath);

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo Wii U", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "WiiU", StringComparison.OrdinalIgnoreCase);
    }

    public override bool HideUntilCaptured => true;

    public override int CaptureStartupDelayMs => 250;

    public override EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        var preferredHwnd = FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyCemuRenderWindow,
            fallbackTitleHint: DisplayName);

        return preferredHwnd != IntPtr.Zero
            ? preferredHwnd
            : FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: true, isPreferredRenderWindow: null);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        var executableDirectory = Path.GetDirectoryName(launcherPath);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            // Force portable mode by pointing config and mlc to the local directory
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(executableDirectory);
            startInfo.ArgumentList.Add("-mlc");
            startInfo.ArgumentList.Add(executableDirectory);
        }

        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(romPath);

        return startInfo;
    }

    public void ApplyFullscreenScalingWorkaround(string launcherPath)
    {
        if (!TryResolveSettingsPath(launcherPath, out var settingsPath))
            return;

        if (_fullscreenScalingWorkaroundApplied && string.Equals(_fullscreenScalingSettingsPath, settingsPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var settingsExists = File.Exists(settingsPath);
            var document = settingsExists
                ? XDocument.Load(settingsPath, LoadOptions.PreserveWhitespace)
                : new XDocument(new XElement("cemu", new XElement("content", new XElement("Graphic"))));

            var contentElement = document.Descendants("content").FirstOrDefault();
            if (contentElement == null)
            {
                contentElement = new XElement("content");
                document.Root?.Add(contentElement);
            }

            if (contentElement == null)
                return;

            var graphicElement = contentElement.Element("Graphic") ?? new XElement("Graphic");
            if (graphicElement.Parent == null)
                contentElement.Add(graphicElement);

            var fullscreenScalingElement = graphicElement.Element("FullscreenScaling");
            _fullscreenScalingElementExisted = fullscreenScalingElement != null;
            if (fullscreenScalingElement == null)
            {
                fullscreenScalingElement = new XElement("FullscreenScaling");
                graphicElement.Add(fullscreenScalingElement);
            }

            if (fullscreenScalingElement.Parent == null)
                graphicElement.Add(fullscreenScalingElement);

            _fullscreenScalingSettingsPath = settingsPath;
            _fullscreenScalingOriginalValue = fullscreenScalingElement.Value;
            _fullscreenScalingWorkaroundApplied = true;

            fullscreenScalingElement.Value = StretchFullscreenScaling.ToString();
            document.Save(settingsPath);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        if (!_fullscreenScalingWorkaroundApplied)
        {
            _fullscreenScalingSettingsPath = null;
            _fullscreenScalingOriginalValue = null;
            _fullscreenScalingElementExisted = false;
        }
    }

    public void RestoreFullscreenScalingWorkaround(string launcherPath)
    {
        if (!_fullscreenScalingWorkaroundApplied)
            return;

        if (!TryResolveSettingsPath(launcherPath, out var settingsPath))
            settingsPath = _fullscreenScalingSettingsPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(settingsPath) || !string.Equals(settingsPath, _fullscreenScalingSettingsPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (!File.Exists(settingsPath))
                return;

            var document = XDocument.Load(settingsPath, LoadOptions.PreserveWhitespace);
            var contentElement = document.Descendants("content").FirstOrDefault() ?? document.Root;
            if (contentElement == null)
                return;

            var graphicElement = contentElement.Element("Graphic");
            if (graphicElement == null)
                return;

            var fullscreenScalingElement = graphicElement.Element("FullscreenScaling");
            if (fullscreenScalingElement == null)
                return;

            if (_fullscreenScalingElementExisted)
                fullscreenScalingElement.Value = _fullscreenScalingOriginalValue ?? string.Empty;
            else
                fullscreenScalingElement.Remove();

            document.Save(settingsPath);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        _fullscreenScalingSettingsPath = null;
        _fullscreenScalingOriginalValue = null;
        _fullscreenScalingElementExisted = false;
        _fullscreenScalingWorkaroundApplied = false;
    }

    public override void PrepareProcessForCapture(Process process)
    {
        // Do not hide or resize during resolution — Cemu's render window must finish constructing first.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Geometry/hiding are applied once a stable game window is selected in ResolveCaptureTargetAsync.
    }

    public override void PrepareWindowForCaptureAttach(IntPtr hwnd)
    {
        // Geometry is applied once the render window is fully constructed during resolve.
    }

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyCemuRenderWindow(hwnd, mainWindowHandle);

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        await WaitForRenderReadyLogAsync(process, cancellationToken).ConfigureAwait(false);

        const int maxAttempts = 200;
        const int delayMs = 50;
        const int stableAttemptsBeforeAssign = 8;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;
        var lastStableWidth = 0;
        var lastStableHeight = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr mainWindowHandle = IntPtr.Zero;
            try
            {
                process.Refresh();
                mainWindowHandle = process.MainWindowHandle;
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            var hwnd = FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero)
                KeepWindowHiddenDuringResolve(hwnd);

            if (hwnd != IntPtr.Zero &&
                IsStableCaptureCandidate(hwnd, mainWindowHandle) &&
                Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            {
                var dimensionsStable = width == lastStableWidth && height == lastStableHeight;
                if (hwnd == observedHwnd && dimensionsStable)
                    observedStableAttempts++;
                else
                {
                    observedHwnd = hwnd;
                    observedStableAttempts = 1;
                    lastStableWidth = width;
                    lastStableHeight = height;
                }

                if (observedStableAttempts >= stableAttemptsBeforeAssign)
                {
                    ApplyCaptureGeometryOnce(hwnd);
                    KeepWindowHiddenDuringResolve(hwnd);
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    return hwnd;
                }
            }
            else
            {
                observedHwnd = IntPtr.Zero;
                observedStableAttempts = 0;
                lastStableWidth = 0;
                lastStableHeight = 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        var fallback = FindPreferredWindowHandle(process);
        if (fallback != IntPtr.Zero && IsLikelyCemuRenderWindow(fallback, process.MainWindowHandle))
        {
            ApplyCaptureGeometryOnce(fallback);
            KeepWindowHiddenDuringResolve(fallback);
        }

        return fallback;
    }

    private static async Task WaitForRenderReadyLogAsync(Process process, CancellationToken cancellationToken)
    {
        if (process == null)
            return;

        var logFilePath = ResolveCemuLogFilePath(process.StartInfo?.FileName);
        if (string.IsNullOrWhiteSpace(logFilePath))
            return;

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(logFilePath))
                {
                    var logText = await File.ReadAllTextAsync(logFilePath, cancellationToken).ConfigureAwait(false);
                    if (logText.Contains(ReadyOutputToken, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ResolveCemuLogFilePath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        try
        {
            var executablePath = Path.GetFullPath(launcherPath.Trim());
            var executableDirectory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(executableDirectory))
                return null;

            // Launch uses -c <executableDirectory>; log.txt is created there even before the file exists.
            var localLogPath = Path.Combine(executableDirectory, "log.txt");
            if (File.Exists(localLogPath) || Directory.Exists(executableDirectory))
                return localLogPath;

            var portableDirectory = Path.Combine(executableDirectory, "portable");
            var portableLogPath = Path.Combine(portableDirectory, "log.txt");
            if (Directory.Exists(portableDirectory) || File.Exists(portableLogPath))
                return portableLogPath;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cemu",
                "log.txt");
        }
        catch
        {
            return null;
        }
    }

    private static void KeepWindowHiddenDuringResolve(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            Win32API.SetWindowCloaked(hwnd, cloaked: true);
            Win32API.MoveAway(hwnd, useCloak: true);
            Win32API.EnsureRenderActiveForCapture(hwnd, bringOnScreen: false);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private void ApplyCaptureGeometryOnce(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            Win32API.TryExitFullscreenWindow(hwnd);

            if (Win32API.HasWindowCaption(hwnd))
                Win32API.RemoveWindowDecorations(hwnd);

            var aspect = CaptureWindowAspectRatio;
            if (aspect is > 0)
                Win32API.ResizeWindowToAspectRatioInPlace(hwnd, aspect.Value);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyCemuRenderWindow(hwnd, mainWindowHandle))
            return false;

        if (IsIconic(hwnd))
            return false;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            return false;

        return width >= 640 && height >= 360;
    }

    private static bool IsCemuShellWindow(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var trimmed = title.Trim();
        if (string.Equals(trimmed, "Cemu", StringComparison.OrdinalIgnoreCase))
            return true;

        var lower = trimmed.ToLowerInvariant();
        return lower.Contains("title list", StringComparison.Ordinal) ||
               lower.Contains("getting started", StringComparison.Ordinal) ||
               lower.Contains("graphic pack", StringComparison.Ordinal) ||
               lower.Contains("input settings", StringComparison.Ordinal) ||
               lower.Contains("general settings", StringComparison.Ordinal);
    }

    private static bool IsLikelyCemuRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        if (IsCemuShellWindow(title))
            return false;

        var lowerTitle = title.ToLowerInvariant();
        if (lowerTitle.Contains("settings") ||
            lowerTitle.Contains("about") ||
            lowerTitle.Contains("profile") ||
            lowerTitle.Contains("update") ||
            lowerTitle.Contains("options") ||
            lowerTitle.Contains("cemu hook"))
        {
            return false;
        }

        if (lowerTitle.Contains("fps:") ||
            lowerTitle.Contains("loading") ||
            lowerTitle.Contains("compiling"))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(title) && title.Length > 5)
            return true;

        var lowerClass = GetWindowClassName(hwnd).ToLowerInvariant();
        if ((lowerClass.Contains("qwindow") || lowerClass.Contains("qt6")) &&
            hwnd != mainWindowHandle &&
            Win32API.TryGetWindowClientSize(hwnd, out var width, out var height) &&
            width >= 480 &&
            height >= 270)
        {
            return true;
        }

        return hwnd == mainWindowHandle &&
               !string.IsNullOrWhiteSpace(title) &&
               !string.Equals(title, "Cemu", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCemuLauncherPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        var normalizedPath = launcherPath.Trim();
        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        if (File.Exists(normalizedPath))
            return normalizedPath;

        if (!Directory.Exists(normalizedPath))
            return null;

        foreach (var executableName in CemuExecutableNames)
        {
            var candidate = Path.Combine(normalizedPath, executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            var launcherCandidate = Directory.EnumerateFiles(normalizedPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    return string.Equals(fileName, "cemu", StringComparison.OrdinalIgnoreCase) ||
                           fileName.Contains("cemu", StringComparison.OrdinalIgnoreCase);
                });

            if (launcherCandidate != null)
                return launcherCandidate;

            var files = Directory.EnumerateFiles(normalizedPath, "*", SearchOption.AllDirectories).ToArray();
            if (files.Length == 1)
                return files[0];
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return null;
    }

    private static bool TryResolveSettingsPath(string? launcherPath, out string settingsPath)
    {
        settingsPath = string.Empty;

        var executablePath = ResolveCemuLauncherPath(launcherPath);
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            return false;

        var portableDirectory = Path.Combine(executableDirectory, "portable");
        var portableSettingsPath = Path.Combine(portableDirectory, "settings.xml");
        if (Directory.Exists(portableDirectory) || File.Exists(portableSettingsPath))
        {
            settingsPath = portableSettingsPath;
            return true;
        }

        var portableDirectorySettingsPath = Path.Combine(executableDirectory, "settings.xml");
        if (File.Exists(portableDirectorySettingsPath))
        {
            settingsPath = portableDirectorySettingsPath;
            return true;
        }

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appDataPath))
            return false;

        settingsPath = Path.Combine(appDataPath, "Cemu", "settings.xml");
        return true;
    }

}
