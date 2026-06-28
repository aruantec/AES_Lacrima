using AES_Core.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AES_Controls.Helpers
{
    /// <summary>
    /// A helper class to locate the FFmpeg executable.
    /// </summary>
    public static class FFmpegLocator
    {
        /// <summary>
        /// Checks whether FFmpeg is available on the current system.
        /// </summary>
        /// <returns>True if FFmpeg is found; otherwise, false.</returns>
        public static bool IsFFmpegAvailable() => FindFFmpegPath() != null;

        /// <summary>
        /// Finds the path to the FFmpeg executable.
        /// </summary>
        /// <returns>The path to the FFmpeg executable, or null if not found.</returns>
        public static string? FindFFmpegPath()
            => FindToolBinary("ffmpeg");

        /// <summary>
        /// Finds the path to the FFprobe executable.
        /// </summary>
        public static string? FindFFprobePath()
            => FindToolBinary("ffprobe");

        /// <summary>
        /// Returns true when the media file contains at least one video stream.
        /// </summary>
        public static bool MediaHasVideoStream(string? mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return false;

            var ffprobePath = FindFFprobePath();
            if (ffprobePath == null)
                return false;

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_type -of csv=p=0 \"{mediaPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (process == null)
                    return false;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10_000);
                return process.ExitCode == 0 &&
                       output.Trim().Equals("video", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the primary video stream dimensions when available.
        /// </summary>
        public static bool TryGetVideoDimensions(string? mediaPath, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return false;

            var ffprobePath = FindFFprobePath();
            if (ffprobePath == null)
                return false;

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 \"{mediaPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (process == null)
                    return false;

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(10_000);
                if (process.ExitCode != 0)
                    return false;

                var parts = output.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length == 2 &&
                       int.TryParse(parts[0], out width) &&
                       int.TryParse(parts[1], out height) &&
                       width > 0 &&
                       height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string? FindToolBinary(string baseName)
        {
            var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? baseName + ".exe"
                : baseName;

            // Prefer the per-user Tools directory (inside the OS standard app data folder) first.
            string managedToolPath = ApplicationPaths.GetToolFile(binaryName);
            if (File.Exists(managedToolPath)) return managedToolPath;

            // Fallback: allow a bundled copy next to the executable (portable builds).
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, binaryName);
            if (File.Exists(localPath)) return localPath;

            // Search the System PATH
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                // PathSeparator is ';' on Windows and ':' on Linux/macOS
                var paths = pathVar.Split(Path.PathSeparator);
                foreach (var path in paths)
                {
                    var fullPath = Path.Combine(path, binaryName);
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            // Check common paths (Backups for macOS/Linux)
            // /opt/homebrew -> Apple Silicon
            string[] commonPaths = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? [@"C:\ffmpeg\bin", @"C:\Program Files\ffmpeg\bin"]
                : ["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin"];

            return commonPaths.Select(path => Path.Combine(path, binaryName)).FirstOrDefault(File.Exists);
        }
    }
}
