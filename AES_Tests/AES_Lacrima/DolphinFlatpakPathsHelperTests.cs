using AES_Lacrima.Services.Dolphin;

namespace AES_Lacrima.Tests;

public sealed class DolphinFlatpakPathsHelperTests
{
    [Fact]
    public void ResolveUserDirectory_Flatpak_ReturnsVarAppDataPath()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        Assert.False(string.IsNullOrWhiteSpace(home));

        var resolved = DolphinFlatpakPathsHelper.ResolveUserDirectory("org.DolphinEmu.dolphin-emu");
        Assert.Equal(
            Path.Combine(home!, ".var", "app", "org.DolphinEmu.dolphin-emu", "data", "dolphin-emu"),
            resolved);
    }

    [Fact]
    public void ResolvePortableUserDirectory_Flatpak_PrefersVarAppDataPath()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        Assert.False(string.IsNullOrWhiteSpace(home));

        var resolved = DolphinGameIniService.ResolvePortableUserDirectory(
            emulatorDirectory: null,
            launcherPath: null,
            flatpakAppId: "org.DolphinEmu.dolphin-emu");

        Assert.Equal(
            Path.Combine(home!, ".var", "app", "org.DolphinEmu.dolphin-emu", "data", "dolphin-emu"),
            resolved);
        Assert.True(Directory.Exists(resolved));
    }
}
