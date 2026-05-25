using AES_Lacrima.Services.Rpcs3;

using log4net;
using AES_Core.Logging;
namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3CustomConfigServiceTests
{
    private static readonly ILog Log = LogHelper.For<Rpcs3CustomConfigServiceTests>();
    [Fact]
    public void GetCustomConfigPath_UsesConfigCustomConfigsFolder()
    {
        var path = Rpcs3CustomConfigService.GetCustomConfigPath(@"C:\emu\PS3\RPCS3", "blus12345");
        Assert.EndsWith(Path.Combine("config", "custom_configs", "config_BLUS12345.yml"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAesCustomConfigPath_MatchesCustomConfigPath()
    {
        var emulatorDir = @"C:\emu\PS3\RPCS3";
        var titleId = "NPUB30000";
        Assert.Equal(
            Rpcs3CustomConfigService.GetCustomConfigPath(emulatorDir, titleId),
            Rpcs3CustomConfigService.GetAesCustomConfigPath(emulatorDir, titleId));
        Assert.Equal(
            Rpcs3CustomConfigService.GetCustomConfigPath(emulatorDir, titleId),
            Rpcs3CustomConfigService.GetRpcs3DeployedConfigPath(emulatorDir, titleId));
    }

    [Fact]
    public void PrepareConfigForLaunch_CreatesPerGameConfigInConfigCustomConfigs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var globalPath = Rpcs3CustomConfigService.GetGlobalConfigPath(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
            File.WriteAllText(globalPath, "Core:\n  PPU Decoder: Interpreter (static)\n");

            Rpcs3CustomConfigService.PrepareConfigForLaunch(tempRoot, "BLUS99999");

            var configPath = Rpcs3CustomConfigService.GetCustomConfigPath(tempRoot, "BLUS99999");

            Assert.True(File.Exists(configPath));
            Assert.Contains("Interpreter (static)", File.ReadAllText(configPath));
            Assert.EndsWith(Path.Combine("config", "custom_configs", "config_BLUS99999.yml"), configPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    [Fact]
    public void TryMigrateLegacyStorage_MovesRootCustomConfigsIntoConfigFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var legacyDirectory = Path.Combine(tempRoot, "custom_configs");
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(Path.Combine(legacyDirectory, "config_BLUS11111.yml"), "Video:\n  Resolution Scale: 150\n");

            Rpcs3CustomConfigService.TryMigrateLegacyStorage(tempRoot);

            var migratedPath = Rpcs3CustomConfigService.GetCustomConfigPath(tempRoot, "BLUS11111");
            Assert.True(File.Exists(migratedPath));
            Assert.Contains("Resolution Scale: 150", File.ReadAllText(migratedPath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }
}
