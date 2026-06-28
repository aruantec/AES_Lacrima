using System.Collections.Concurrent;
using System.Diagnostics;
using AES_Core.IO;

namespace AES_Controls.Helpers;

/// <summary>
/// Sidecar <c>{cacheId}.prev</c> gameplay preview clips for ROM carousel/grid playback.
/// </summary>
public static class EmulationPreviewCacheHelper
{
    public const string PreviewExtension = ".prev";

    private const int PreviewOptimizeMaxEdge = 720;
    private const int PreviewOptimizeSkipMaxBytes = 24 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string GetCacheId(string? filePath) =>
        EmulationCoverCacheHelper.GetCacheId(filePath);

    public static string ResolveRomPathForCache(string? romPath) =>
        EmulationCoverCacheHelper.ResolveRomPathForCache(romPath);

    public static string GetPreviewCachePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        return ApplicationPaths.GetCacheFile(GetCacheId(filePath) + PreviewExtension);
    }

    public static bool IsPreviewCachePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(PreviewExtension, StringComparison.OrdinalIgnoreCase);

    public static bool HasPreview(string? filePath)
    {
        filePath = ResolveRomPathForCache(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var previewPath = GetPreviewCachePath(filePath);
        return File.Exists(previewPath) && new FileInfo(previewPath).Length > 0;
    }

    public static string? TryGetPreviewPath(string? filePath)
    {
        filePath = ResolveRomPathForCache(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var previewPath = GetPreviewCachePath(filePath);
        return File.Exists(previewPath) && new FileInfo(previewPath).Length > 0
            ? previewPath
            : null;
    }

    public static bool TryDeletePreviewSidecar(string? filePath)
    {
        filePath = ResolveRomPathForCache(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var previewPath = GetPreviewCachePath(filePath);
        if (!File.Exists(previewPath))
            return false;

        try
        {
            File.Delete(previewPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Atomically moves a finished recording into the ROM's <c>.prev</c> sidecar.
    /// </summary>
    public static bool TryCommitPreviewFile(string? romFilePath, string? recordedFilePath)
    {
        romFilePath = ResolveRomPathForCache(romFilePath);
        if (string.IsNullOrWhiteSpace(romFilePath) ||
            string.IsNullOrWhiteSpace(recordedFilePath) ||
            !File.Exists(recordedFilePath) ||
            !FFmpegLocator.MediaHasVideoStream(recordedFilePath))
        {
            return false;
        }

        var previewPath = GetPreviewCachePath(romFilePath);
        if (string.IsNullOrWhiteSpace(previewPath))
            return false;

        var fileLock = FileLocks.GetOrAdd(previewPath, _ => new SemaphoreSlim(1, 1));
        fileLock.Wait();
        try
        {
            var directory = Path.GetDirectoryName(previewPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var sourcePath = recordedFilePath;
            string? optimizedPath = null;
            if (FFmpegLocator.MediaHasVideoStream(recordedFilePath) &&
                TryOptimizeGameplayPreviewClip(recordedFilePath, out optimizedPath) &&
                !string.IsNullOrWhiteSpace(optimizedPath))
            {
                sourcePath = optimizedPath;
            }

            var tempPath = previewPath + ".tmp";
            File.Copy(sourcePath, tempPath, overwrite: true);
            if (File.Exists(previewPath))
                File.Replace(tempPath, previewPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, previewPath);

            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }

            if (!string.IsNullOrWhiteSpace(optimizedPath) &&
                !string.Equals(optimizedPath, recordedFilePath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(optimizedPath); } catch { /* best effort */ }
            }

            return File.Exists(previewPath) && new FileInfo(previewPath).Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static bool TryOptimizeGameplayPreviewClip(string sourcePath, out string? optimizedPath)
    {
        optimizedPath = null;
        if (ShouldUseSourceWithoutOptimization(sourcePath))
            return false;

        var ffmpegPath = FFmpegLocator.FindFFmpegPath();
        if (ffmpegPath == null)
            return false;

        optimizedPath = sourcePath + ".optimized.mp4";
        try
        {
            if (File.Exists(optimizedPath))
                File.Delete(optimizedPath);
        }
        catch
        {
            return false;
        }

        var args = string.Join(' ',
            "-hide_banner -loglevel error -y",
            $"-i \"{sourcePath}\"",
            $"-vf scale={PreviewOptimizeMaxEdge}:-2",
            "-c:v libx264 -preset fast -crf 21",
            "-c:a aac -b:a 160k",
            "-movflags +faststart",
            $"\"{optimizedPath}\"");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });

            if (process == null)
            {
                optimizedPath = null;
                return false;
            }

            process.WaitForExit(120_000);
            if (process.ExitCode != 0 || !File.Exists(optimizedPath) || new FileInfo(optimizedPath).Length <= 0)
            {
                try { File.Delete(optimizedPath); } catch { }
                optimizedPath = null;
                return false;
            }

            return true;
        }
        catch
        {
            optimizedPath = null;
            return false;
        }
    }

    private static bool ShouldUseSourceWithoutOptimization(string sourcePath)
    {
        if (!FFmpegLocator.TryGetVideoDimensions(sourcePath, out var width, out var height))
            return false;

        var maxEdge = Math.Max(width, height);
        if (maxEdge > PreviewOptimizeMaxEdge)
            return false;

        try
        {
            return new FileInfo(sourcePath).Length <= PreviewOptimizeSkipMaxBytes;
        }
        catch
        {
            return false;
        }
    }
}
