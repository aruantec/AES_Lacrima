using System.Runtime.InteropServices;

namespace AES_Controls.Helpers
{
    /// <summary>
    /// A helper class to locate the FFmpeg executable.
    /// </summary>
    public static class FFmpegLocator
    {
        /// <summary>
        /// Finds the path to the FFmpeg executable.
        /// </summary>
        /// <returns>The path to the FFmpeg executable, or null if not found.</returns>
        public static string? FindFFmpegPath()
        {
            // Determine the binary name for the current OS
            string binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

            // Check the local directory first
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