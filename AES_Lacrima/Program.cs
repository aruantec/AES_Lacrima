using Avalonia;
using AES_Core.IO;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using System;
using System.IO;
using System.Linq;

namespace AES_Lacrima
{
    internal sealed class Program
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Set working directory to the app base directory so it doesn't crash on macOS when launched via double-click where Working Directory is '/'
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // Add standard tool locations to PATH so native libraries (ffmpeg, libmpv, yt-dlp) can be located.
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

            // Ensure our per-user Tools directory is also on the PATH so native libraries can be loaded
            // even when the app is installed in a protected location (Program Files).
            try
            {
                var toolsDirectory = ApplicationPaths.ToolsDirectory;
                Directory.CreateDirectory(toolsDirectory);
                if (!currentPath.Contains(toolsDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    currentPath = $"{currentPath}{pathSeparator}{toolsDirectory}";
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to add Tools directory to PATH", ex);
            }

            Environment.SetEnvironmentVariable("PATH", currentPath);

            // Ensure key application folders exist
            Directory.CreateDirectory(ApplicationPaths.LogsDirectory);
            Directory.CreateDirectory(ApplicationPaths.SettingsDirectory);
            Directory.CreateDirectory(ApplicationPaths.CacheDirectory);
            Directory.CreateDirectory(ApplicationPaths.ShadersDirectory);
            Directory.CreateDirectory(ApplicationPaths.ToolsDirectory);
            EnsureShadersPresent();

            // Ensure libmpv and other native helpers are loaded from the per-user Tools folder
            try
            {
                LibMPVSharp.LibraryName.LibraryDirectory = ApplicationPaths.ToolsDirectory;
            }
            catch
            {
                // Ignore if the LibMPVSharp assembly isn't available in this build configuration
            }

            Environment.SetEnvironmentVariable("PATH", currentPath);

            var logsDirectory = ApplicationPaths.LogsDirectory;
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

        private static void EnsureShadersPresent()
        {
            try
            {
                var targetRoot = ApplicationPaths.ShadersDirectory;
                var sourceRoot = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!Directory.Exists(sourceRoot))
                {
                    Log.Warn($"Shaders source directory not found: {sourceRoot}");
                    return;
                }

                // Keep user-provided/cached shader folders intact, but always copy any newly shipped shaders.
                CopyDirectoryIfMissing(sourceRoot, targetRoot);
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to ensure default shaders are available", ex);
            }
        }

        private static void CopyDirectoryIfMissing(string sourceRoot, string targetRoot)
        {
            Directory.CreateDirectory(targetRoot);

            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, directory);
                Directory.CreateDirectory(Path.Combine(targetRoot, relativePath));
            }

            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, file);
                var destination = Path.Combine(targetRoot, relativePath);
                var destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                if (!File.Exists(destination))
                {
                    File.Copy(file, destination, overwrite: false);
                    continue;
                }

                // Update shipped files when the source is newer, while preserving
                // locally modified files that have a newer timestamp.
                var sourceWriteTime = File.GetLastWriteTimeUtc(file);
                var destinationWriteTime = File.GetLastWriteTimeUtc(destination);
                if (sourceWriteTime > destinationWriteTime)
                {
                    File.Copy(file, destination, overwrite: true);
                }
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new SkiaOptions() { MaxGpuResourceSizeBytes = 256000000 })
                .LogToTrace();
    }
}
