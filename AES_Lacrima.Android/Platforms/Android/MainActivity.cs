using Android.App;
using Android.Content.PM;
using Android.OS;
using AES_Core.IO;
using Avalonia;
using Avalonia.Android;
using System.IO;

namespace AES_Lacrima.Android;

[Activity(
    Label = "AES Lacrima",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation |
                           ConfigChanges.ScreenSize |
                           ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize |
                           ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        var customizedBuilder = base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256000000 })
            .LogToTrace();

        return customizedBuilder;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        EnsureBundledShadersAvailable();
        base.OnCreate(savedInstanceState);

        // Keep the Android app fullscreen to mimic the desktop no-chrome experience.
        Window?.SetFlags(
            global::Android.Views.WindowManagerFlags.Fullscreen,
            global::Android.Views.WindowManagerFlags.Fullscreen);
    }

    private void EnsureBundledShadersAvailable()
    {
        try
        {
            var destinationRoot = Path.Combine(ApplicationPaths.ShadersDirectory, "Shadertoys");
            Directory.CreateDirectory(destinationRoot);
            CopyAssetTree("Shadertoys", destinationRoot);
        }
        catch
        {
            // Ignore asset extraction failures; app can still run without shaders.
        }
    }

    private void CopyAssetTree(string assetPath, string destinationPath)
    {
        var entries = Assets?.List(assetPath) ?? [];
        if (entries.Length == 0)
        {
            using var input = Assets?.Open(assetPath);
            if (input == null) return;

            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir))
                Directory.CreateDirectory(destinationDir);

            // Keep existing files to preserve user-modified shader copies.
            if (File.Exists(destinationPath)) return;

            using var output = File.Create(destinationPath);
            input.CopyTo(output);
            return;
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var entry in entries)
        {
            var childAssetPath = $"{assetPath}/{entry}";
            var childDestination = Path.Combine(destinationPath, entry);
            CopyAssetTree(childAssetPath, childDestination);
        }
    }
}
