using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AES_Emulation.EmulationHandlers;

public sealed class ShadPs4Handler : EmulatorHandlerBase
{
    private const string FlatpakLauncherProcessName = "shadPS4QtLauncher";

    public static ShadPs4Handler Instance { get; } = new();

    private ShadPs4Handler()
    {
    }

    public override string HandlerId => "shadps4";

    public override string SectionKey => "PS4";

    public override string SectionTitle => "PlayStation 4";

    public override string DisplayName => "shadPS4 QtLauncher";

    public override bool HideUntilCaptured => true;

    public override int CaptureStartupDelayMs => 900;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PlayStation 4", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Playstation 4", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PS4", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        var existingArguments = startInfo.ArgumentList.ToArray();
        var gamePath = ResolvePs4GamePath(romPath);

        startInfo.ArgumentList.Clear();

        for (var i = 0; i < existingArguments.Length - 1; i++)
            startInfo.ArgumentList.Add(existingArguments[i]);

        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("default");
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(gamePath);

        if (startFullscreen)
        {
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("--fullscreen");
            startInfo.ArgumentList.Add("true");
        }

        return startInfo;
    }

    public override async System.Threading.Tasks.Task<IntPtr> ResolveCaptureTargetAsync(Process process, System.Threading.CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux() && IsFlatpakLaunch(process) && TryResolveFlatpakLauncherProcess(out var launcherProcess))
            process = launcherProcess;

        return await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolvePs4GamePath(string romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return romPath;

        if (Directory.Exists(romPath))
        {
            var ebootPath = Path.Combine(romPath, "eboot.bin");
            if (File.Exists(ebootPath))
                return ebootPath;
        }

        var parentDirectory = Path.GetDirectoryName(romPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory) && Directory.Exists(parentDirectory))
        {
            var ebootPath = Path.Combine(parentDirectory, "eboot.bin");
            if (File.Exists(ebootPath))
                return ebootPath;
        }

        return romPath;
    }

    private static bool IsFlatpakLaunch(Process process)
    {
        try
        {
            var processName = Path.GetFileName(process.StartInfo?.FileName ?? string.Empty);
            return string.Equals(processName, "flatpak", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "flatpak-spawn", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveFlatpakLauncherProcess(out Process launcherProcess)
    {
        launcherProcess = null!;

        if (TryResolveNamedProcess(FlatpakLauncherProcessName, out launcherProcess))
            return true;

        if (TryResolveNamedProcess(FlatpakLauncherProcessName.ToLowerInvariant(), out launcherProcess))
            return true;

        return false;
    }

    private static bool TryResolveNamedProcess(string processName, out Process resolvedProcess)
    {
        resolvedProcess = null!;

        if (string.IsNullOrWhiteSpace(processName))
            return false;

        try
        {
            var candidates = Process.GetProcessesByName(processName);
            if (candidates.Length == 0)
                return false;

            Process? newestCandidate = null;
            DateTime newestStartTime = DateTime.MinValue;

            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate.StartTime > newestStartTime)
                    {
                        newestStartTime = candidate.StartTime;
                        newestCandidate = candidate;
                    }
                }
                catch
                {
                }
            }

            if (newestCandidate != null)
            {
                resolvedProcess = newestCandidate;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }
}