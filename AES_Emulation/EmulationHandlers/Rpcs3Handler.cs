using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Emulation.Controls;

namespace AES_Emulation.EmulationHandlers;

public sealed class Rpcs3Handler : EmulatorHandlerBase
{
    private const uint WS_CHILD = 0x40000000;
    private const string StretchConfigFileName = "aes-lacrima-rpcs3-stretch.yml";

    public static Rpcs3Handler Instance { get; } = new();

    private Rpcs3Handler()
    {
    }

    public override string HandlerId => "rpcs3";

    public override string SectionKey => "PS3";

    public override string SectionTitle => "PlayStation 3";

    public override string DisplayName => "RPCS3";

    public override bool HideUntilCaptured => true;

    public override bool ForceUseTargetClientAreaCapture => true;

    public override bool IsWindowEmbeddingSupported => true;

    public override EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

    public override int CaptureStartupDelayMs => 2500;

    public override bool IsLauncherPathValid(string? launcherPath)
        => !string.IsNullOrWhiteSpace(ResolveRpcs3LauncherPath(launcherPath));

    public override string? NormalizeLauncherPath(string? launcherPath)
        => ResolveRpcs3LauncherPath(launcherPath) ?? base.NormalizeLauncherPath(launcherPath);

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Playstation 3", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PS3", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        // RPCS3 supports direct CLI boot. Using no-GUI mode avoids the launcher/game-list shell
        // and gets us to the actual render window faster.
        startInfo.ArgumentList.Add("--no-gui");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(GetStretchConfigPath());

        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public override void PrepareProcessForCapture(Process process)
    {
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        return FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyRpcs3RenderWindow,
            fallbackTitleHint: DisplayName);
    }

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyRpcs3RenderWindow(hwnd, mainWindowHandle);

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

    private static string? ResolveRpcs3LauncherPath(string? launcherPath)
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

        var executableNames = new[]
        {
            "rpcs3.exe",
            "RPCS3.exe",
            "rpcs3",
            "RPCS3"
        };

        foreach (var executableName in executableNames)
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
                    return string.Equals(fileName, "rpcs3", StringComparison.OrdinalIgnoreCase) ||
                           fileName.Contains("rpcs3", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsLikelyRpcs3RenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).Trim();
        var lowerTitle = title.ToLowerInvariant();
        var style = GetWindowStyle(hwnd);
        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasChildStyle = (style & WS_CHILD) == WS_CHILD;
        var parent = GetParent(hwnd);
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (hasChildStyle || parent != IntPtr.Zero)
            return false;

        if (lowerTitle.Contains("settings") ||
            lowerTitle.Contains("configuration") ||
            lowerTitle.Contains("controller") ||
            lowerTitle.Contains("input") ||
            lowerTitle.Contains("audio") ||
            lowerTitle.Contains("video") ||
            lowerTitle.Contains("about") ||
            lowerTitle.Contains("help") ||
            lowerTitle.Contains("log") ||
            lowerTitle.Contains("debug") ||
            lowerTitle.Contains("game list") ||
            lowerTitle.Contains("shader") ||
            lowerTitle.Contains("launcher") ||
            lowerTitle.StartsWith("rpcs3 ", StringComparison.OrdinalIgnoreCase) ||
            lowerTitle.Contains("master") ||
            lowerTitle.Contains("alpha"))
        {
            looksLikePrimaryUi = true;
        }

        if (lowerTitle.StartsWith("fps:", StringComparison.OrdinalIgnoreCase) ||
            lowerTitle.Contains("vulkan") ||
            lowerTitle.Contains("opengl") ||
            lowerTitle.Contains("render") ||
            lowerTitle.Contains("gpu") ||
            lowerTitle.Contains("swapchain"))
        {
            return true;
        }

        if (lowerTitle.Contains("rpcs3") && !looksLikePrimaryUi && !hasCaption)
            return true;

        return false;
    }

    private static string GetStretchConfigPath()
    {
        var configPath = Path.Combine(Path.GetTempPath(), StretchConfigFileName);
        var configText = string.Join(Environment.NewLine, new[]
        {
            "Video:",
            "  Stretch To Display Area: true"
        }) + Environment.NewLine;

        try
        {
            if (!File.Exists(configPath) || !string.Equals(File.ReadAllText(configPath), configText, StringComparison.Ordinal))
            {
                File.WriteAllText(configPath, configText);
            }
        }
        catch
        {
        }

        return configPath;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);
}
