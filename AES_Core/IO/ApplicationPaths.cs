using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AES_Core.IO;

public static class ApplicationPaths
{
    private const string ApplicationName = "AES_Lacrima";
    private static bool? _isAppBaseWritable;

    public static string DataRootDirectory => IsAppBaseWritable()
        ? AppContext.BaseDirectory
        : GetUserDataRootDirectory();

    public static string LogsDirectory => IsAppBaseWritable()
        ? Path.Combine(AppContext.BaseDirectory, "Logs")
        : GetUserLogsDirectory();

    public static string SettingsDirectory => Path.Combine(DataRootDirectory, "Settings");
    public static string CacheDirectory => Path.Combine(DataRootDirectory, "Cache");
    public static string ToolsDirectory => Path.Combine(DataRootDirectory, "Tools");

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
