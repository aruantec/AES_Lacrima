using AES_Lacrima.Services.Xenia;
using Tomlyn;
using Tomlyn.Model;

using log4net;
using AES_Core.Logging;
namespace AES_Tests.AES_Lacrima;

public sealed class XeniaCustomConfigServiceTests
{
    private static readonly ILog Log = LogHelper.For<XeniaCustomConfigServiceTests>();
    [Fact]
    public void GetJsonConfigPath_UsesCustomConfigsFolder()
    {
        var path = XeniaCustomConfigService.GetJsonConfigPath(@"C:\emu\xenia", "4d5307e6");
        Assert.EndsWith(Path.Combine("custom_configs", "4D5307E6.json"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsOverrides()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var document = new XeniaCustomConfigDocument();
            document.Overrides["GPU"] = new Dictionary<string, string?>
            {
                ["vsync"] = "false",
                ["framerate_limit"] = "0"
            };

            XeniaCustomConfigService.Save(tempRoot, "4D5307E6", document);
            var loaded = XeniaCustomConfigService.LoadOrEmpty(tempRoot, "4D5307E6");

            Assert.True(loaded.Overrides.TryGetValue("GPU", out var gpu));
            Assert.Equal("false", gpu["vsync"]);
            Assert.Equal("0", gpu["framerate_limit"]);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    [Fact]
    public void ApplyOverrides_WritesActiveConfigWithMergedValues()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var templatePath = XeniaCustomConfigService.GetDefaultTemplatePath(tempRoot);
            File.WriteAllText(templatePath,
                """
                [GPU]
                gpu = "any"
                vsync = true
                framerate_limit = 60

                [Display]
                fullscreen = false
                """);

            var overrides = new XeniaCustomConfigDocument();
            overrides.Overrides["GPU"] = new Dictionary<string, string?>
            {
                ["vsync"] = "false",
                ["framerate_limit"] = "0"
            };

            XeniaCustomConfigService.ApplyOverrides(tempRoot, overrides);

            var activePath = XeniaCustomConfigService.GetActiveConfigPath(tempRoot);
            Assert.True(File.Exists(activePath));

            var model = Toml.Parse(File.ReadAllText(activePath)).ToModel();
            Assert.True(model.TryGetValue("GPU", out var gpuSection));
            var gpuTable = Assert.IsType<TomlTable>(gpuSection);
            Assert.False(Assert.IsType<bool>(gpuTable["vsync"]));
            Assert.Equal(0L, Assert.IsType<long>(gpuTable["framerate_limit"]));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    [Fact]
    public void PrepareConfigForLaunch_WithoutCustomJson_WritesDefaultTemplate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var templatePath = XeniaCustomConfigService.GetDefaultTemplatePath(tempRoot);
            File.WriteAllText(templatePath,
                """
                [GPU]
                gpu = "d3d12"
                vsync = true
                """);

            XeniaCustomConfigService.PrepareConfigForLaunch(tempRoot, "4D5307E6");

            var activePath = XeniaCustomConfigService.GetActiveConfigPath(tempRoot);
            Assert.True(File.Exists(activePath));
            Assert.Equal(File.ReadAllText(templatePath), File.ReadAllText(activePath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    [Fact]
    public void ApplyOverrides_WritesDrawResolutionScaleAsIntegers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var templatePath = XeniaCustomConfigService.GetDefaultTemplatePath(tempRoot);
            File.WriteAllText(templatePath,
                """
                [GPU]
                gpu = "d3d12"
                draw_resolution_scale_x = 1
                draw_resolution_scale_y = 1
                """);

            var overrides = new XeniaCustomConfigDocument();
            overrides.Overrides["GPU"] = new Dictionary<string, string?>
            {
                [XeniaCustomConfigService.DrawResolutionScaleXKey] = "3",
                [XeniaCustomConfigService.DrawResolutionScaleYKey] = "3"
            };

            XeniaCustomConfigService.ApplyOverrides(tempRoot, overrides);

            var model = Toml.Parse(File.ReadAllText(XeniaCustomConfigService.GetActiveConfigPath(tempRoot))).ToModel();
            var gpuTable = Assert.IsType<TomlTable>(model["GPU"]);
            Assert.Equal(3L, Assert.IsType<long>(gpuTable[XeniaCustomConfigService.DrawResolutionScaleXKey]));
            Assert.Equal(3L, Assert.IsType<long>(gpuTable[XeniaCustomConfigService.DrawResolutionScaleYKey]));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    [Fact]
    public void BuildOverridesFromValues_StoresOnlyDifferences()
    {
        var template = new Dictionary<string, string?>
        {
            ["GPU.vsync"] = "true",
            ["GPU.framerate_limit"] = "60"
        };

        var current = new Dictionary<string, string?>
        {
            ["GPU.vsync"] = "false",
            ["GPU.framerate_limit"] = "60"
        };

        var document = XeniaCustomConfigService.BuildOverridesFromValues(current, template);
        Assert.Single(document.Overrides);
        Assert.True(document.Overrides.TryGetValue("GPU", out var gpu));
        Assert.Single(gpu);
        Assert.Equal("false", gpu["vsync"]);
    }

    [Fact]
    public void ApplyWindowsVirtualDisplayLaunchValues_SetsBorderlessAndOutputSize()
    {
        var root = new TomlTable
        {
            ["Display"] = new TomlTable { ["fullscreen"] = false },
            ["UI"] = new TomlTable { ["window_size_x"] = 1280L, ["window_size_y"] = 720L },
        };

        XeniaCustomConfigService.ApplyWindowsVirtualDisplayLaunchValues(root, 1920, 1080);

        var display = Assert.IsType<TomlTable>(root["Display"]);
        var ui = Assert.IsType<TomlTable>(root["UI"]);

        Assert.False(Assert.IsType<bool>(display["fullscreen"]));
        Assert.False(Assert.IsType<bool>(display["present_letterbox"]));
        Assert.Equal(1920L, Assert.IsType<long>(ui["window_size_x"]));
        Assert.Equal(1080L, Assert.IsType<long>(ui["window_size_y"]));
    }

    [Fact]
    public void ApplyGamescopeLaunchValues_SetsFullscreenAndOutputSize()
    {
        var root = new TomlTable
        {
            ["Display"] = new TomlTable { ["fullscreen"] = false },
            ["UI"] = new TomlTable { ["window_size_x"] = 1280L, ["window_size_y"] = 720L },
        };

        XeniaCustomConfigService.ApplyGamescopeLaunchValues(root, 1280, 720);

        var display = Assert.IsType<TomlTable>(root["Display"]);
        var ui = Assert.IsType<TomlTable>(root["UI"]);
        var gpu = Assert.IsType<TomlTable>(root["GPU"]);

        Assert.True(Assert.IsType<bool>(display["fullscreen"]));
        Assert.False(Assert.IsType<bool>(display["present_letterbox"]));
        Assert.Equal(1280L, Assert.IsType<long>(ui["window_size_x"]));
        Assert.Equal(720L, Assert.IsType<long>(ui["window_size_y"]));
        Assert.Equal("vulkan", Assert.IsType<string>(gpu["gpu"]));
        Assert.Equal("fbo", Assert.IsType<string>(gpu["render_target_path_vulkan"]));
    }

    [Fact]
    public void EnsureLinuxAudioSettings_ForcesSdlBackendOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var activePath = XeniaCustomConfigService.GetActiveConfigPath(tempRoot);
            File.WriteAllText(activePath,
                """
                [APU]
                apu = "any"
                mute = false
                """);

            XeniaCustomConfigService.EnsureLinuxAudioSettings(tempRoot);

            var model = Toml.Parse(File.ReadAllText(activePath)).ToModel();
            var apu = Assert.IsType<TomlTable>(model["APU"]);
            Assert.Equal("sdl", Assert.IsType<string>(apu["apu"]));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    [Fact]
    public void EnsureLinuxAudioSettings_RespectsExplicitUserApuOverride()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var activePath = XeniaCustomConfigService.GetActiveConfigPath(tempRoot);
            File.WriteAllText(activePath,
                """
                [APU]
                apu = "any"
                """);

            var overrides = new XeniaCustomConfigDocument();
            overrides.Overrides["APU"] = new Dictionary<string, string?> { ["apu"] = "alsa" };

            XeniaCustomConfigService.EnsureLinuxAudioSettings(tempRoot, overrides);

            var model = Toml.Parse(File.ReadAllText(activePath)).ToModel();
            var apu = Assert.IsType<TomlTable>(model["APU"]);
            Assert.Equal("any", Assert.IsType<string>(apu["apu"]));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }
}
