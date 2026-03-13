using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(AES_Lacrima.Headless.Tests.TestAppBuilder))]

namespace AES_Lacrima.Headless.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
