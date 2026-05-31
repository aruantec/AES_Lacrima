using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Lacrima.Services.Rpcs3;

public sealed record Rpcs3PpuHashProbeResult(
    bool Success,
    string? PpuHash,
    string Message);

/// <summary>
/// Resolves a game's PPU executable hash from RPCS3.log and, when needed, by booting the title briefly.
/// </summary>
public static class Rpcs3PpuHashProbeService
{
    private static readonly ILog Log = LogHelper.For(typeof(Rpcs3PpuHashProbeService));

    private static readonly Regex PpuExecutableHashLogRegex = new(
        @"PPU executable hash:\s*(PPU-[0-9A-Fa-f]{40})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BootGameTitleIdLogRegex = new(
        @"BootGame:.*title_id='([A-Z]{4}\d{5})'",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int ProbePollIntervalMs = 400;
    private const int ProbeTimeoutMs = 120_000;

    public static string? GetRpcs3LogPath(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return null;

        var direct = Path.Combine(emulatorDirectory, "RPCS3.log");
        if (File.Exists(direct))
            return direct;

        var nested = Path.Combine(emulatorDirectory, "log", "RPCS3.log");
        return File.Exists(nested) ? nested : null;
    }

    public static string? TryReadPpuHashFromLog(string? emulatorDirectory, string? titleId = null)
    {
        var logPath = GetRpcs3LogPath(emulatorDirectory);
        if (string.IsNullOrWhiteSpace(logPath))
            return null;

        try
        {
            var logText = ReadSharedLogTail(logPath);
            if (string.IsNullOrWhiteSpace(logText))
                return null;

            return TryParsePpuHashFromLogText(logText, titleId);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to read PPU hash from RPCS3 log at '{logPath}'.", ex);
            return null;
        }
    }

    internal static string? TryParsePpuHashFromLogText(string logText, string? titleId)
    {
        if (string.IsNullOrWhiteSpace(logText))
            return null;

        if (!string.IsNullOrWhiteSpace(titleId))
        {
            var normalizedTitleId = titleId.Trim().ToUpperInvariant();
            var bootMatches = BootGameTitleIdLogRegex.Matches(logText);
            for (var i = bootMatches.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(bootMatches[i].Groups[1].Value, normalizedTitleId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var segment = logText[bootMatches[i].Index..];
                var hashMatch = PpuExecutableHashLogRegex.Match(segment);
                if (hashMatch.Success)
                    return NormalizePpuHash(hashMatch.Groups[1].Value);
            }
        }

        var matches = PpuExecutableHashLogRegex.Matches(logText);
        return matches.Count == 0
            ? null
            : NormalizePpuHash(matches[^1].Groups[1].Value);
    }

    /// <summary>
    /// Resolves the PPU hash for a title: existing log, official patch.yml, then a brief RPCS3 boot.
    /// </summary>
    public static async Task<string?> TryResolveForTitleAsync(
        string? emulatorDirectory,
        string titleId,
        string? appVersion,
        string? launcherPath,
        string? gamePath,
        Action<string>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
            return null;

        var normalizedTitleId = titleId.Trim().ToUpperInvariant();

        var fromLog = TryReadPpuHashFromLog(emulatorDirectory, normalizedTitleId);
        if (!string.IsNullOrWhiteSpace(fromLog))
            return fromLog;

        var fromOfficial = Rpcs3PatchesService.TryGetMostCommonPpuHashFromCatalog(
            emulatorDirectory,
            normalizedTitleId,
            appVersion,
            Rpcs3PatchCatalog.Official);
        if (!string.IsNullOrWhiteSpace(fromOfficial))
            return fromOfficial;

        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        statusCallback?.Invoke("Starting RPCS3 briefly to detect the PPU hash for this game...");
        var probe = await TryProbeByBootAsync(
                launcherPath,
                emulatorDirectory,
                normalizedTitleId,
                gamePath,
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(probe.PpuHash))
            return probe.PpuHash;

        Log.Info($"PPU hash probe failed for {normalizedTitleId}: {probe.Message}");
        return null;
    }

    public static async Task<Rpcs3PpuHashProbeResult> TryProbeByBootAsync(
        string? launcherPath,
        string? emulatorDirectory,
        string titleId,
        string? gamePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return new Rpcs3PpuHashProbeResult(false, null, "RPCS3 launcher path is not configured.");

        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var resolvedLauncher = Rpcs3Handler.Instance.NormalizeLauncherPath(launcherPath);
        if (string.IsNullOrWhiteSpace(resolvedLauncher) || !File.Exists(resolvedLauncher))
            return new Rpcs3PpuHashProbeResult(false, null, "RPCS3 executable was not found.");

        var bootTarget = ResolveBootTarget(normalizedTitleId, gamePath);
        if (string.IsNullOrWhiteSpace(bootTarget))
        {
            return new Rpcs3PpuHashProbeResult(
                false,
                null,
                "Could not resolve a boot path for this game. Select an installed PS3 game folder.");
        }

        var logPath = GetRpcs3LogPath(emulatorDirectory);
        var logOffset = GetLogEndOffset(logPath);

        Process? process = null;
        try
        {
            Rpcs3CustomConfigService.PrepareConfigForLaunch(emulatorDirectory, normalizedTitleId);

            var startInfo = Rpcs3Handler.Instance.BuildStartInfo(resolvedLauncher, bootTarget, startFullscreen: false);
            Rpcs3CustomConfigService.ApplyConfigDirectoryEnvironment(startInfo, emulatorDirectory);

            process = Process.Start(startInfo);
            if (process == null)
                return new Rpcs3PpuHashProbeResult(false, null, "Failed to start RPCS3.");

            var deadline = Environment.TickCount64 + ProbeTimeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                    break;

                var hash = TryReadPpuHashFromLogSegment(emulatorDirectory, normalizedTitleId, logPath, logOffset);
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    await StopProbeProcessAsync(process).ConfigureAwait(false);
                    return new Rpcs3PpuHashProbeResult(
                        true,
                        hash,
                        $"Detected PPU hash {hash} from RPCS3.log.");
                }

                await Task.Delay(ProbePollIntervalMs, cancellationToken).ConfigureAwait(false);
            }

            var fallbackHash = TryReadPpuHashFromLogSegment(emulatorDirectory, normalizedTitleId, logPath, logOffset);
            if (!string.IsNullOrWhiteSpace(fallbackHash))
            {
                await StopProbeProcessAsync(process).ConfigureAwait(false);
                return new Rpcs3PpuHashProbeResult(true, fallbackHash, $"Detected PPU hash {fallbackHash} from RPCS3.log.");
            }

            return new Rpcs3PpuHashProbeResult(
                false,
                null,
                "Timed out waiting for RPCS3 to report a PPU executable hash. Launch the game once manually, then try importing again.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"RPCS3 PPU hash probe failed for title ID '{normalizedTitleId}'.", ex);
            return new Rpcs3PpuHashProbeResult(false, null, $"PPU hash probe failed: {ex.Message}");
        }
        finally
        {
            if (process != null)
                await StopProbeProcessAsync(process).ConfigureAwait(false);
        }
    }

    private static string? ResolveBootTarget(string titleId, string? gamePath)
    {
        var preferredBootPath = Ps3InstalledGameHelper.GetPreferredBootPath(gamePath);
        if (!string.IsNullOrWhiteSpace(preferredBootPath))
            return preferredBootPath;

        return Rpcs3Handler.BuildGameIdBootPath(titleId);
    }

    private static long GetLogEndOffset(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return 0;

        try
        {
            return new FileInfo(logPath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string? TryReadPpuHashFromLogSegment(
        string? emulatorDirectory,
        string titleId,
        string? knownLogPath,
        long startOffset)
    {
        var logPath = knownLogPath ?? GetRpcs3LogPath(emulatorDirectory);
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return null;

        try
        {
            var segment = ReadSharedLogFromOffset(logPath, startOffset);
            return TryParsePpuHashFromLogText(segment, titleId);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to read RPCS3 log segment from '{logPath}'.", ex);
            return null;
        }
    }

    private static async Task StopProbeProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            if (process.CloseMainWindow())
            {
                if (await WaitForExitAsync(process, 4000).ConfigureAwait(false))
                    return;
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, 5000).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to stop RPCS3 PPU hash probe process cleanly.", ex);
        }
    }

    private static Task<bool> WaitForExitAsync(Process process, int timeoutMs)
    {
        if (process.HasExited)
            return Task.FromResult(true);

        return Task.Run(() => process.WaitForExit(timeoutMs));
    }

    private static string ReadSharedLogTail(string logPath, int maxBytes = 2 * 1024 * 1024)
    {
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length <= maxBytes)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        stream.Seek(-maxBytes, SeekOrigin.End);
        using (var tailReader = new StreamReader(stream))
            return tailReader.ReadToEnd();
    }

    private static string ReadSharedLogFromOffset(string logPath, long startOffset)
    {
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (startOffset > 0 && startOffset < stream.Length)
            stream.Seek(startOffset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NormalizePpuHash(string rawHash)
    {
        var trimmed = rawHash.Trim();
        return trimmed.StartsWith("PPU-", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"PPU-{trimmed}";
    }
}
