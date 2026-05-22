using AES_Lacrima.Services.Rpcs3;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3CustomConfigServiceTests
{
    [Fact]
    public void GetAesCustomConfigPath_UsesPs3Rpcs3CustomConfigsFolder()
    {
        var path = Rpcs3CustomConfigService.GetAesCustomConfigPath(@"C:\emu\PS3\RPCS3", "blus12345");
        Assert.EndsWith(Path.Combine("custom_configs", "config_BLUS12345.yml"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRpcs3DeployedConfigPath_UsesConfigCustomConfigsFolder()
    {
        var path = Rpcs3CustomConfigService.GetRpcs3DeployedConfigPath(@"C:\emu\PS3\RPCS3", "npub30000");
        Assert.EndsWith(Path.Combine("config", "custom_configs", "config_NPUB30000.yml"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareConfigForLaunch_CreatesAndDeploysPerGameConfig()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var globalPath = Rpcs3CustomConfigService.GetGlobalConfigPath(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
            File.WriteAllText(globalPath, "Core:\n  PPU Decoder: Interpreter (static)\n");

            Rpcs3CustomConfigService.PrepareConfigForLaunch(tempRoot, "BLUS99999");

            var aesPath = Rpcs3CustomConfigService.GetAesCustomConfigPath(tempRoot, "BLUS99999");
            var deployedPath = Rpcs3CustomConfigService.GetRpcs3DeployedConfigPath(tempRoot, "BLUS99999");

            Assert.True(File.Exists(aesPath));
            Assert.True(File.Exists(deployedPath));
            Assert.Equal(File.ReadAllText(aesPath), File.ReadAllText(deployedPath));
            Assert.Contains("Interpreter (static)", File.ReadAllText(deployedPath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void ImportFromRpcs3AfterSession_CopiesDeployedConfigBackToAesStorage()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            Rpcs3CustomConfigService.PrepareConfigForLaunch(tempRoot, "NPEB00001");

            var deployedPath = Rpcs3CustomConfigService.GetRpcs3DeployedConfigPath(tempRoot, "NPEB00001");
            File.WriteAllText(deployedPath, "Video:\n  Resolution Scale: 200\n");

            Rpcs3CustomConfigService.ImportFromRpcs3AfterSession(tempRoot, "NPEB00001");

            var aesPath = Rpcs3CustomConfigService.GetAesCustomConfigPath(tempRoot, "NPEB00001");
            Assert.Equal(File.ReadAllText(deployedPath), File.ReadAllText(aesPath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }
}
