using Avalonia;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using System;
using System.IO;

namespace AES_Lacrima
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Set working directory to the app base directory so it doesn't crash on macOS when launched via double-click where Working Directory is '/'
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // Add the base directory to the PATH so that MPV can find the bundled yt-dlp binary
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var pathSeparator = Path.PathSeparator;
            
            // On macOS launched via Finder, standard Homebrew and Unix paths are missing. We must inject them so tools like 'brew' function properly.
            if (OperatingSystem.IsMacOS())
            {
                var defaultMacPaths = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin";
                foreach (var p in defaultMacPaths.Split(':'))
                {
                    if (!currentPath.Contains(p)) currentPath += $"{pathSeparator}{p}";
                }
            }

            if (!currentPath.Contains(AppDomain.CurrentDomain.BaseDirectory))
            {
                // Appending BaseDirectory to the END so native fast Homebrew binaries take precedence over slow bundled PyInstaller binaries
                currentPath = $"{currentPath}{pathSeparator}{AppDomain.CurrentDomain.BaseDirectory}";
            }
            Environment.SetEnvironmentVariable("PATH", currentPath);

            var logsDirectory = GetLogsDirectory();
            Directory.CreateDirectory(logsDirectory);

            var layout = new PatternLayout { ConversionPattern = "%date %-5level %logger - %message%newline%exception" };
            layout.ActivateOptions();

            // Use a single file appender that writes to a writable per-user log directory.
            var fileAppender = new FileAppender
            {
                AppendToFile = false,
                File = Path.Combine(logsDirectory, "log.txt"),
                Layout = layout,
                LockingModel = new FileAppender.MinimalLock()
            };
            fileAppender.ActivateOptions();

            // Use the programmatic appender as the basic configuration
            BasicConfigurator.Configure(fileAppender);

            // If a log4net.config file is present, allow it to override/watch settings
            if (File.Exists("log4net.config"))
            {
                XmlConfigurator.ConfigureAndWatch(new FileInfo("log4net.config"));
            }

            // Start the Avalonia application with the classic desktop lifetime
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new SkiaOptions() { MaxGpuResourceSizeBytes = 256000000 })
                .LogToTrace();

        private static string GetLogsDirectory()
        {
            var baseDirectoryLogs = Path.Combine(AppContext.BaseDirectory, "Logs");
            if (IsDirectoryWritable(AppContext.BaseDirectory))
            {
                return baseDirectoryLogs;
            }

            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AES_Lacrima",
                    "Logs");
            }

            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Library",
                    "Logs",
                    "AES_Lacrima");
            }

            var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (!string.IsNullOrWhiteSpace(stateHome))
            {
                return Path.Combine(stateHome, "AES_Lacrima", "Logs");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                ".local",
                "state",
                "AES_Lacrima",
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
}
