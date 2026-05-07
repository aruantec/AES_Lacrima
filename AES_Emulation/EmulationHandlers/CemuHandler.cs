using AES_Emulation.Windows.API;
using AES_Emulation.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AES_Emulation.EmulationHandlers;

public sealed class CemuHandler : EmulatorHandlerBase
{
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

    public override bool ForceUseTargetClientAreaCapture => true;

    public override int CaptureStartupDelayMs => 2000;

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

        if (startFullscreen)
            startInfo.ArgumentList.Add("--fullscreen");

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
        catch
        {
        }

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
        catch
        {
        }

        _fullscreenScalingSettingsPath = null;
        _fullscreenScalingOriginalValue = null;
        _fullscreenScalingElementExisted = false;
        _fullscreenScalingWorkaroundApplied = false;
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preferred = FindPreferredWindowHandle(process);
            if (preferred != IntPtr.Zero)
                return preferred;

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    private static bool IsLikelyCemuRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();

        if (lowerClass.Contains("cemu"))
            return true;

        if (lowerTitle.Contains("cemu") && !lowerTitle.Contains("cemu hook"))
            return true;

        if (!string.IsNullOrWhiteSpace(title) &&
            !lowerTitle.Contains("settings") &&
            !lowerTitle.Contains("graphics") &&
            !lowerTitle.Contains("audio") &&
            !lowerTitle.Contains("input") &&
            !lowerTitle.Contains("about") &&
            !lowerTitle.Contains("profile") &&
            !lowerTitle.Contains("update"))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(lowerClass) && lowerClass.Contains("qwindow") && hwnd != mainWindowHandle)
            return true;

        return false;
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
        catch
        {
        }

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
        catch
        {
        }

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
