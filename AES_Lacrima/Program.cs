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
            // Ensure Logs directory exists and configure a rolling file appender so
            // that logs are written to Logs/app.YYYY-MM-DD.log
            Directory.CreateDirectory("Logs");

            var layout = new PatternLayout { ConversionPattern = "%date %-5level %logger - %message%newline%exception" };
            layout.ActivateOptions();

            // Use a single file appender that writes to Logs/log.txt
            var fileAppender = new FileAppender
            {
                AppendToFile = true,
                File = Path.Combine("Logs", "log.txt"),
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
                .LogToTrace();
    }
}
