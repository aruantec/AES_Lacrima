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
}
