using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Launches Linux emulator processes inside gamescope.
/// </summary>
public static class LinuxCompositorLaunchHelper
{
    private static readonly ILog SLog = LogHelper.For(typeof(LinuxCompositorLaunchHelper));

    public static bool IsAvailable =>
        OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(GamescopeManager.ResolveExecutablePath());

    public static async Task<Process> LaunchInCompositorAsync(
        ProcessStartInfo emulatorStartInfo,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("gamescope is only supported on Linux.");

        var gamescopePath = GamescopeManager.ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(gamescopePath))
            throw new LinuxCompositorLaunchException(LinuxCompositorLaunchException.MissingBinaryMessage);

        width = Math.Clamp(width, 320, 7680);
        height = Math.Clamp(height, 240, 4320);

        using var readyPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var readyFd = readyPipe.ClientSafePipeHandle.DangerousGetHandle().ToInt32();

        var compositorStartInfo = new ProcessStartInfo
        {
            FileName = gamescopePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(emulatorStartInfo.WorkingDirectory)
                ? Environment.CurrentDirectory
                : emulatorStartInfo.WorkingDirectory,
        };

        foreach (System.Collections.Generic.KeyValuePair<string, string?> variable in emulatorStartInfo.Environment)
            compositorStartInfo.Environment[variable.Key] = variable.Value;

        compositorStartInfo.ArgumentList.Add("--backend");
        compositorStartInfo.ArgumentList.Add("headless");
        compositorStartInfo.ArgumentList.Add("-W");
        compositorStartInfo.ArgumentList.Add(width.ToString());
        compositorStartInfo.ArgumentList.Add("-H");
        compositorStartInfo.ArgumentList.Add(height.ToString());
        compositorStartInfo.ArgumentList.Add("-w");
        compositorStartInfo.ArgumentList.Add(width.ToString());
        compositorStartInfo.ArgumentList.Add("-h");
        compositorStartInfo.ArgumentList.Add(height.ToString());
        compositorStartInfo.ArgumentList.Add("-S");
        compositorStartInfo.ArgumentList.Add("fit");
        compositorStartInfo.ArgumentList.Add("--xwayland-count");
        compositorStartInfo.ArgumentList.Add("1");
        compositorStartInfo.ArgumentList.Add("--expose-wayland");
        compositorStartInfo.ArgumentList.Add("-R");
        compositorStartInfo.ArgumentList.Add(readyFd.ToString());
        compositorStartInfo.ArgumentList.Add("--");
        compositorStartInfo.ArgumentList.Add(emulatorStartInfo.FileName);
        foreach (var argument in emulatorStartInfo.ArgumentList)
            compositorStartInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = compositorStartInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new LinuxCompositorLaunchException("Failed to start the gamescope compositor process.");

            readyPipe.DisposeLocalCopyOfClientHandle();
        }
        catch (LinuxCompositorLaunchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LinuxCompositorLaunchException("Failed to start the gamescope compositor process.", ex);
        }

        var ready = await WaitForCompositorReadyAsync(process, readyPipe, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            var details = await ReadProcessDiagnosticsAsync(process).ConfigureAwait(false);
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to terminate a timed-out gamescope process.", ex);
            }

            throw new LinuxCompositorLaunchException(
                string.IsNullOrWhiteSpace(details)
                    ? "gamescope did not become ready in time."
                    : $"gamescope did not become ready in time.{Environment.NewLine}{details}");
        }

        SLog.Info($"gamescope headless compositor ready at {width}x{height} (16:9, pid={process.Id}).");
        return process;
    }

    public static async Task<Process> LaunchEmulatorInExistingCompositorAsync(
        Process compositorProcess,
        ProcessStartInfo emulatorStartInfo,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("gamescope is only supported on Linux.");

        compositorProcess.Refresh();
        if (compositorProcess.HasExited)
            throw new LinuxCompositorLaunchException("The gamescope compositor is no longer running.");

        if (!TryResolveCompositorEnvironment(compositorProcess.Id, out var compositorEnvironment))
            throw new LinuxCompositorLaunchException("Failed to resolve the gamescope Wayland environment.");

        var launchStartInfo = new ProcessStartInfo
        {
            FileName = emulatorStartInfo.FileName,
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(emulatorStartInfo.WorkingDirectory)
                ? Environment.CurrentDirectory
                : emulatorStartInfo.WorkingDirectory,
        };

        foreach (var argument in emulatorStartInfo.ArgumentList)
            launchStartInfo.ArgumentList.Add(argument);

        foreach (var variable in emulatorStartInfo.Environment)
            launchStartInfo.Environment[variable.Key] = variable.Value;

        foreach (var variable in compositorEnvironment)
            launchStartInfo.Environment[variable.Key] = variable.Value;

        var emulatorProcess = new Process { StartInfo = launchStartInfo, EnableRaisingEvents = true };
        if (!emulatorProcess.Start())
            throw new LinuxCompositorLaunchException("Failed to start the emulator inside the existing gamescope compositor.");

        SLog.Info(
            $"Launched emulator pid={emulatorProcess.Id} inside existing gamescope compositor pid={compositorProcess.Id} " +
            $"(WAYLAND_DISPLAY={compositorEnvironment.GetValueOrDefault("WAYLAND_DISPLAY")}).");

        try
        {
            emulatorProcess.Dispose();
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to dispose temporary emulator process handle after launch.", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);

        compositorProcess.Refresh();
        if (compositorProcess.HasExited)
            throw new LinuxCompositorLaunchException("gamescope exited while launching the emulator.");

        return compositorProcess;
    }

    internal static bool TryResolveCompositorEnvironment(int compositorPid, out Dictionary<string, string> environment)
    {
        environment = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!OperatingSystem.IsLinux() || compositorPid <= 0)
            return false;

        if (!TryReadProcessEnvironment(compositorPid, out var processEnvironment))
            return false;

        CopyIfPresent(processEnvironment, environment, "XDG_RUNTIME_DIR");
        CopyIfPresent(processEnvironment, environment, "WAYLAND_DISPLAY");
        CopyIfPresent(processEnvironment, environment, "DISPLAY");

        if (!environment.ContainsKey("WAYLAND_DISPLAY"))
        {
            var runtimeDir = environment.GetValueOrDefault("XDG_RUNTIME_DIR")
                ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtimeDir) &&
                TryResolveGamescopeWaylandDisplay(runtimeDir, compositorPid, out var waylandDisplay))
            {
                environment["WAYLAND_DISPLAY"] = waylandDisplay;
            }
        }

        return environment.ContainsKey("WAYLAND_DISPLAY");
    }

    public static (int Width, int Height) ResolveOutputSize(int baseHeight = 720)
    {
        const double aspectRatio = 16.0 / 9.0;

        var height = Math.Clamp(baseHeight, 480, 2160);
        var width = (int)Math.Round(height * aspectRatio);
        width = Math.Clamp(width, 640, 3840);
        height = (int)Math.Round(width / aspectRatio);
        return (width, height);
    }

    /// <summary>
    /// Applies environment defaults for emulators running inside gamescope's XWayland session.
    /// </summary>
    public static void PrepareEmulatorStartInfoForGamescope(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux())
            return;

        startInfo.Environment["SDL_VIDEODRIVER"] = "x11";
        startInfo.Environment["GDK_BACKEND"] = "x11";
        startInfo.Environment["QT_QPA_PLATFORM"] = "xcb";

        // Prefer gamescope's XWayland display over its private Wayland socket so GL/Vulkan
        // emulators (RetroArch, etc.) create capturable X11 windows instead of wl_surface-only clients.
        startInfo.Environment.Remove("WAYLAND_DISPLAY");
    }

    /// <summary>
    /// Waits briefly for gamescope to spawn its reaper/inner compositor before capture attaches.
    /// </summary>
    public static async Task<int> ResolveCompositorRootPidAsync(
        int launchedPid,
        CancellationToken cancellationToken = default,
        int timeoutMs = 4000)
    {
        if (!OperatingSystem.IsLinux() || launchedPid <= 0)
            return launchedPid;

        var stopwatch = Stopwatch.StartNew();
        var resolved = LinuxCompositorProcessHelper.ResolveCompositorRootPid(launchedPid);
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            resolved = LinuxCompositorProcessHelper.ResolveCompositorRootPid(launchedPid);
            if (resolved != launchedPid)
                return resolved;

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return LinuxCompositorProcessHelper.ResolveCompositorRootPid(launchedPid);
    }

    private static async Task<bool> WaitForCompositorReadyAsync(
        Process process,
        AnonymousPipeServerStream readyPipe,
        CancellationToken cancellationToken)
    {
        var pipeReadyTask = WaitForReadyPipeAsync(readyPipe, cancellationToken);
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

        const int timeoutMs = 45000;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
                return false;

            if (pipeReadyTask.IsCompleted && await pipeReadyTask.ConfigureAwait(false))
                return true;

            if (!string.IsNullOrWhiteSpace(runtimeDir))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(runtimeDir, "gamescope-*").Any())
                        return true;
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed while probing for gamescope Wayland sockets.", ex);
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return pipeReadyTask.IsCompleted && await pipeReadyTask.ConfigureAwait(false);
    }

    private static async Task<bool> WaitForReadyPipeAsync(AnonymousPipeServerStream readyPipe, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[1];
            var read = await readyPipe.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            return read > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed while waiting for gamescope ready pipe.", ex);
            return false;
        }
    }

    private static void CopyIfPresent(
        IReadOnlyDictionary<string, string> source,
        Dictionary<string, string> destination,
        string key)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            destination[key] = value;
    }

    private static bool TryReadProcessEnvironment(int pid, out Dictionary<string, string> environment)
    {
        environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var environPath = $"/proc/{pid}/environ";
        if (!File.Exists(environPath))
            return false;

        try
        {
            var raw = File.ReadAllBytes(environPath);
            var start = 0;
            for (var i = 0; i <= raw.Length; i++)
            {
                if (i < raw.Length && raw[i] != 0)
                    continue;

                if (i <= start)
                {
                    start = i + 1;
                    continue;
                }

                var entry = System.Text.Encoding.UTF8.GetString(raw, start, i - start);
                var separator = entry.IndexOf('=');
                if (separator > 0)
                {
                    var key = entry[..separator];
                    var value = entry[(separator + 1)..];
                    environment[key] = value;
                }

                start = i + 1;
            }

            return environment.Count > 0;
        }
        catch (Exception ex)
        {
            SLog.Debug($"Failed to read environment from '{environPath}'.", ex);
            return false;
        }
    }

    private static bool TryResolveGamescopeWaylandDisplay(string runtimeDir, int compositorPid, out string waylandDisplay)
    {
        waylandDisplay = string.Empty;
        try
        {
            foreach (var socketPath in Directory.EnumerateFileSystemEntries(runtimeDir, "gamescope-*"))
            {
                var socketName = Path.GetFileName(socketPath);
                if (string.IsNullOrWhiteSpace(socketName))
                    continue;

                if (TryReadProcessEnvironment(compositorPid, out var environment) &&
                    string.Equals(environment.GetValueOrDefault("WAYLAND_DISPLAY"), socketName, StringComparison.Ordinal))
                {
                    waylandDisplay = socketName;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(waylandDisplay))
                    waylandDisplay = socketName;
            }
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed while probing for gamescope Wayland sockets.", ex);
        }

        return !string.IsNullOrWhiteSpace(waylandDisplay);
    }

    private static async Task<string?> ReadProcessDiagnosticsAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stderr))
                    return stderr.Trim();

                return $"gamescope exited with code {process.ExitCode}.";
            }

            var partial = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(partial) ? null : partial.Trim();
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to read gamescope diagnostics.", ex);
            return null;
        }
    }
}
