using AES_Lacrima.Services;

namespace AES_Tests.AES_Lacrima;

public sealed class EdenEmulatorUpdateServiceTests
{
    [Fact]
    public void SelectEdenLinuxAsset_PrefersAmd64GccStandardAppImage()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var assets = new[]
        {
            "Eden-Linux-v0.2.1-aarch64-clang-pgo.AppImage",
            "Eden-Linux-v0.2.1-amd64-clang-pgo.AppImage",
            "Eden-Linux-v0.2.1-amd64-gcc-standard.AppImage",
            "Eden-Windows-v0.2.1-amd64-gcc-standard.zip",
            "Eden-Linux-amd64-clang-pgo.AppImage.zsync",
        };

        var selected = EmulatorReleaseAssetSelection.SelectEdenLinuxAsset(assets, static name => name);

        Assert.Equal("Eden-Linux-v0.2.1-amd64-gcc-standard.AppImage", selected);
    }

    [Fact]
    public void SelectEdenLinuxAsset_DoesNotFallBackToWindowsZip()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var assets = new[]
        {
            "Eden-Windows-v0.2.1-amd64-gcc-standard.zip",
            "Eden-macOS-v0.2.1.dmg",
        };

        var selected = EmulatorReleaseAssetSelection.SelectEdenLinuxAsset(assets, static name => name);

        Assert.Null(selected);
    }

    [Fact]
    public void IsEdenLinuxAppImageAssetName_RejectsZsyncAndWindowsAssets()
    {
        Assert.True(EmulatorReleaseAssetSelection.IsEdenLinuxAppImageAssetName(
            "Eden-Linux-v0.2.1-amd64-gcc-standard.AppImage"));
        Assert.False(EmulatorReleaseAssetSelection.IsEdenLinuxAppImageAssetName(
            "Eden-Linux-amd64-gcc-standard.AppImage.zsync"));
        Assert.False(EmulatorReleaseAssetSelection.IsEdenLinuxAppImageAssetName(
            "Eden-Windows-v0.2.1-amd64-gcc-standard.zip"));
    }
}
