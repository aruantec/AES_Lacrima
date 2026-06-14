using AES_Emulation.EmulationHandlers;
using AES_Lacrima.Services;

namespace AES_Lacrima.Tests;

public sealed class DolphinEmulatorUpdateServiceTests
{
    [Fact]
    public void ResolveSelectorOs_OnLinux_ReturnsLnx()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Equal("lnx", DolphinEmulatorUpdateService.ResolveSelectorOs());
    }

    [Fact]
    public void BuildSelectorEndpoint_OnLinux_UsesLnx()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var endpoint = DolphinEmulatorUpdateService.BuildSelectorEndpoint("beta");
        Assert.Contains("os=lnx", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void IsDolphinLinuxAssetName_RejectsWindowsZip()
    {
        Assert.False(DolphinEmulatorUpdateService.IsDolphinLinuxAssetName("dolphin-x64.7z"));
        Assert.False(DolphinEmulatorUpdateService.IsDolphinLinuxAssetName("Dolphin-windows-x64.zip"));
    }

    [Fact]
    public void IsDolphinLinuxAssetName_AcceptsLinuxArchives()
    {
        Assert.True(DolphinEmulatorUpdateService.IsDolphinLinuxAssetName("dolphin-2412-linux-x64.tar.xz"));
        Assert.True(DolphinEmulatorUpdateService.IsDolphinLinuxAssetName("Dolphin.AppImage"));
    }

    [Fact]
    public void TryParseFlatpakInfoVersion_ParsesInstalledVersion()
    {
        const string sample = """
            Dolphin Emulator - GameCube / Wii

                 Version: 2603a
                 Branch: stable
            """;

        Assert.Equal("2603a", DolphinEmulatorUpdateService.TryParseFlatpakInfoVersion(sample));
    }

    [Fact]
    public void TryParseFlatpakRemoteVersion_ParsesBranchWhenVersionMissing()
    {
        const string sample = """
                 Ref: app/org.DolphinEmu.dolphin-emu/x86_64/stable
               Zweig: stable
              Commit: 06ead480437bd5b9e648c14ea485296c1cbef7f9a4083f6a3a107d376733ca9b
            """;

        Assert.Equal("stable", DolphinEmulatorUpdateService.TryParseFlatpakRemoteVersion(sample));
    }

    [Fact]
    public void TryParseFlatpakInfoCommit_ParsesInstalledCommit()
    {
        const string sample = """
                 Version: 2603a
                 Commit: 06ead480437bd5b9e648c14ea485296c1cbef7f9a4083f6a3a107d376733c…
            """;

        Assert.Equal(
            "06ead480437bd5b9e648c14ea485296c1cbef7f9a4083f6a3a107d376733c",
            DolphinEmulatorUpdateService.TryParseFlatpakInfoCommit(sample));
    }

    [Fact]
    public void CommitsEquivalent_MatchesTruncatedInstalledCommit()
    {
        const string installed = "06ead480437bd5b9e648c14ea485296c1cbef7f9a4083f6a3a107d376733c…";
        const string remote = "06ead480437bd5b9e648c14ea485296c1cbef7f9a4083f6a3a3a107d376733ca9b";

        Assert.True(DolphinEmulatorUpdateService.CommitsEquivalent(installed, remote));
    }

    [Fact]
    public void ApplyResolvedLauncher_FlatpakPrefix_SetsFlatpakAppId()
    {
        var handler = DolphinHandler.Instance;
        var previousAppId = handler.FlatpakAppId;
        var previousLauncherPath = handler.LauncherPath;

        try
        {
            DolphinEmulatorUpdateService.ApplyResolvedLauncher(handler, "flatpak:org.DolphinEmu.dolphin-emu");
            Assert.Equal("org.DolphinEmu.dolphin-emu", handler.FlatpakAppId);
        }
        finally
        {
            handler.FlatpakAppId = previousAppId;
            handler.LauncherPath = previousLauncherPath;
        }
    }
}
