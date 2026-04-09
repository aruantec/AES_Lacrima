using System;
using System.IO;

namespace AES_Core.IO;

public static class ApplicationPaths
{
    private const string ApplicationName = "AES_Lacrima";
    private static bool? _isAppBaseWritable;

    /// <summary>
    /// Root directory where all application data (cache, settings, downloaded tools, etc.) is stored.
    /// This uses operating system standard locations (e.g. AppData on Windows, ~/.local/share on Linux).
    /// </summary>
    public static string DataRootDirectory => GetUserDataRootDirectory();

    /// <summary>
    /// Directory where log files should be written.
    /// </summary>
    public static string LogsDirectory => GetUserLogsDirectory();

    /// <summary>
    /// Directory where updater-specific log files should be written.
    /// </summary>
    public static string UpdaterLogsDirectory => Path.Combine(LogsDirectory, "updater");

    /// <summary>
    /// Directory for application settings files.
    /// </summary>
    public static string SettingsDirectory => Path.Combine(DataRootDirectory, "Settings");

    /// <summary>
    /// Directory for application cache files.
    /// </summary>
    public static string CacheDirectory => Path.Combine(DataRootDirectory, "Cache");

    /// <summary>
    /// Directory for staged application updates and temporary update artifacts.
    /// </summary>
    public static string UpdatesDirectory => Path.Combine(DataRootDirectory, "Updates");

    /// <summary>
    /// Directory for downloaded native/tool binaries (ffmpeg, libmpv, yt-dlp, etc.).
    /// </summary>
    public static string ToolsDirectory => Path.Combine(DataRootDirectory, "Tools");

    /// <summary>
    /// Directory for shader files used by the application.
    /// This is a shared writable runtime folder adjacent to the Logs folder,
    /// so packaged builds can copy bundled shaders out of the app bundle and
    /// use them consistently from ../Shaders relative to log.txt.
    /// </summary>
    public static string ShadersDirectory => GetSharedShadersDirectory();

    public static string GetSettingsFile(string fileName) => Path.Combine(SettingsDirectory, fileName);
    public static string GetCacheFile(string fileName) => Path.Combine(CacheDirectory, fileName);
    public static string GetToolFile(string fileName) => Path.Combine(ToolsDirectory, fileName);

    public static bool IsAppBaseWritable()
    {
        if (_isAppBaseWritable.HasValue)
        {
            return _isAppBaseWritable.Value;
        }

        _isAppBaseWritable = IsDirectoryWritable(AppContext.BaseDirectory);
        return _isAppBaseWritable.Value;
    }

    private static string GetSharedShadersDirectory()
    {
        var logsParent = Directory.GetParent(LogsDirectory);
        if (logsParent is not null)
        {
            return Path.Combine(logsParent.FullName, "Shaders");
        }

        return Path.Combine(DataRootDirectory, "Shaders");
    }

    private static string GetUserDataRootDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Library",
                "Application Support",
                ApplicationName);
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            return Path.Combine(dataHome, ApplicationName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            ".local",
            "share",
            ApplicationName);
    }

    private static string GetUserLogsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationName,
                "Logs");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Library",
                "Logs",
                ApplicationName);
        }

        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(stateHome))
        {
            return Path.Combine(stateHome, ApplicationName, "Logs");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            ".local",
            "state",
            ApplicationName,
            "Logs");
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
