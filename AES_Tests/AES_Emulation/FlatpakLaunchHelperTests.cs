using System.Diagnostics;
using AES_Emulation.Linux;

namespace AES_Tests.AES_Emulation;

public sealed class FlatpakLaunchHelperTests
{
    [Fact]
    public void Apply_AddsFilesystemGrantForContentPath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aes-flatpak-launch-test");
        try
        {
            var romPath = Path.Combine(tempDirectory.FullName, "game.iso");
            File.WriteAllText(romPath, "test");

            var startInfo = new ProcessStartInfo
            {
                FileName = "dolphin-emu",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-b");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(romPath);

            FlatpakLaunchHelper.Apply(startInfo, "org.DolphinEmu.dolphin-emu", romPath);

            Assert.Equal("flatpak", Path.GetFileName(startInfo.FileName));
            Assert.Equal("run", startInfo.ArgumentList[0]);
            Assert.Contains(
                startInfo.ArgumentList,
                arg => arg.StartsWith("--filesystem=", StringComparison.Ordinal) &&
                       arg.Contains(tempDirectory.FullName, StringComparison.Ordinal) &&
                       arg.EndsWith(":ro", StringComparison.Ordinal));
            Assert.Contains(startInfo.ArgumentList, arg => arg == "org.DolphinEmu.dolphin-emu");
            Assert.Equal(romPath, startInfo.ArgumentList[^1]);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CollectFilesystemGrants_DeduplicatesIdenticalDirectories()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aes-flatpak-grant-test");
        try
        {
            var romPath = Path.Combine(tempDirectory.FullName, "game.iso");
            File.WriteAllText(romPath, "test");
            var otherRomPath = Path.Combine(tempDirectory.FullName, "other.iso");
            File.WriteAllText(otherRomPath, "test");

            var args = new[] { "-e", romPath, $"--exec={otherRomPath}" };
            var grants = FlatpakLaunchHelper.CollectFilesystemGrants(args, romPath, workingDirectory: null).ToList();

            Assert.Single(grants);
            Assert.Equal($"--filesystem={tempDirectory.FullName}:ro", grants[0]);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CollectFilesystemGrants_IgnoresRelativePaths()
    {
        var grants = FlatpakLaunchHelper.CollectFilesystemGrants(["-e", "relative/game.iso"], null, null).ToList();

        Assert.Empty(grants);
    }

    [Fact]
    public void CollectFilesystemGrants_IncludesAdditionalGrantPaths()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aes-flatpak-extra-grant-test");
        try
        {
            var configDirectory = Path.Combine(tempDirectory.FullName, "rpcs3-config");
            Directory.CreateDirectory(configDirectory);

            var grants = FlatpakLaunchHelper.CollectFilesystemGrants([], null, null, configDirectory).ToList();

            Assert.Single(grants);
            Assert.Equal($"--filesystem={configDirectory}", grants[0]);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }
}
