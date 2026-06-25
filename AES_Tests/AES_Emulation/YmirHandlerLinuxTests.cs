using AES_Emulation.EmulationHandlers;
using AES_Emulation.Linux;

namespace AES_Tests.AES_Emulation;

public sealed class YmirHandlerLinuxTests
{
    [Fact]
    public void BuildStartInfo_OnLinux_ForcesFullscreenPortableProfileAndMenuCrop()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        var ymirRoot = Path.Combine(tempRoot, "Ymir");
        Directory.CreateDirectory(ymirRoot);
        try
        {
            var executablePath = Path.Combine(ymirRoot, "ymir-sdl3");
            File.WriteAllText(executablePath, string.Empty);
            var romPath = Path.Combine(tempRoot, "game.cue");
            File.WriteAllText(romPath, string.Empty);

            var startInfo = YmirHandler.Instance.BuildStartInfo(executablePath, romPath, startFullscreen: false);

            Assert.Contains("--fullscreen", startInfo.ArgumentList);
            Assert.Contains("--profile", startInfo.ArgumentList);
            Assert.Contains(ymirRoot, startInfo.ArgumentList);
            Assert.Equal("pulse", startInfo.Environment["SDL_AUDIODRIVER"]);
            Assert.Equal(0, YmirHandler.Instance.ClientAreaCropTopInset);
            Assert.Equal("fill", YmirHandler.Instance.LinuxGamescopeScalingMode);

            var toml = File.ReadAllText(Path.Combine(ymirRoot, "Ymir.toml"));
            Assert.Contains("FullScreen = true", toml);
            Assert.Contains("Borderless = true", toml);
            Assert.Contains("AutoResizeWindow = false", toml);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void ApplyTomlUpdates_UpdatesNestedSections()
    {
        const string input = """
            [Video]
            FullScreen = false

                [Video.FullScreenMode]
                Borderless = false
            """;

        var updated = YmirHandler.ApplyTomlUpdates(input, new[]
        {
            ("Video", "FullScreen", "true"),
            ("Video.FullScreenMode", "Borderless", "true"),
        });

        Assert.Contains("FullScreen = true", updated);
        Assert.Contains("Borderless = true", updated);
        Assert.DoesNotContain("FullScreen = false", updated);
        Assert.DoesNotContain("Borderless = false", updated);
    }

    [Fact]
    public void ResolveOutputSize_UsesContentAspectRatioWhenProvided()
    {
        var (width, height) = LinuxCompositorLaunchHelper.ResolveOutputSize(1080, 4.0 / 3.0);

        Assert.Equal(1440, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void ComputeLinuxGamescopeBottomCrop_UsesSmallStatusChromeEstimate()
    {
        var top = (int)Math.Round(1080 * YmirHandler.LinuxGamescopeMenuCropHeightFraction);
        var bottom = YmirHandler.ComputeLinuxGamescopeBottomCrop(1080, top);

        Assert.Equal(63, top);
        Assert.Equal(22, bottom);
    }
}
