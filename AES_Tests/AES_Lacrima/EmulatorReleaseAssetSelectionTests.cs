using AES_Lacrima.Services;
using System.Runtime.InteropServices;

namespace AES_Tests.AES_Lacrima;

public sealed class EmulatorReleaseAssetSelectionTests
{
    [Fact]
    public void SelectFirstLinuxAsset_prefers_x64_on_amd64_host()
    {
        var assets = new[]
        {
            new TestAsset("pcsx2-linux-appimage-aarch64-Qt.AppImage"),
            new TestAsset("pcsx2-linux-appimage-x64-Qt.AppImage"),
        };

        var selected = EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
            assets,
            static asset => asset.Name,
            static asset => asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("pcsx2-linux-appimage-x64-Qt.AppImage", selected?.Name);
    }

    [Fact]
    public void SelectFirstLinuxAsset_prefers_aarch64_on_arm64_host()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return;

        var assets = new[]
        {
            new TestAsset("pcsx2-linux-appimage-x64-Qt.AppImage"),
            new TestAsset("pcsx2-linux-appimage-aarch64-Qt.AppImage"),
        };

        var selected = EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
            assets,
            static asset => asset.Name,
            static asset => asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("pcsx2-linux-appimage-aarch64-Qt.AppImage", selected?.Name);
    }

    [Fact]
    public void IsArm64AssetName_does_not_treat_x64_as_arm()
    {
        Assert.False(EmulatorReleaseAssetSelection.IsArm64AssetName("pcsx2-linux-appimage-x64-Qt.AppImage"));
        Assert.True(EmulatorReleaseAssetSelection.IsArm64AssetName("pcsx2-linux-appimage-aarch64-Qt.AppImage"));
    }

    [Fact]
    public void IsArm32AssetName_treats_armhf_as_arm32_not_neutral()
    {
        Assert.True(EmulatorReleaseAssetSelection.IsArm32AssetName("DuckStation-armhf.AppImage"));
        Assert.False(EmulatorReleaseAssetSelection.IsArm32AssetName("DuckStation-arm64.AppImage"));
        Assert.False(EmulatorReleaseAssetSelection.MatchesHostLinuxAssetArchitecture("DuckStation-armhf.AppImage"));
        Assert.True(EmulatorReleaseAssetSelection.IsConflictingLinuxAssetArchitecture(
            "DuckStation-armhf.AppImage",
            Architecture.X64));
    }

    [Fact]
    public void SelectDuckStationLinuxAsset_prefers_x64_on_amd64_host()
    {
        var assets = new[]
        {
            new TestAsset("DuckStation-arm64.AppImage"),
            new TestAsset("DuckStation-armhf.AppImage"),
            new TestAsset("DuckStation-x64-SSE2.AppImage"),
            new TestAsset("DuckStation-x64.AppImage"),
        };

        var selected = EmulatorReleaseAssetSelection.SelectDuckStationLinuxAsset(
            assets,
            static asset => asset.Name);

        Assert.Equal("DuckStation-x64.AppImage", selected?.Name);
    }

    [Fact]
    public void SelectDuckStationLinuxAsset_prefers_sse2_fallback_when_x64_missing()
    {
        var assets = new[]
        {
            new TestAsset("DuckStation-armhf.AppImage"),
            new TestAsset("DuckStation-x64-SSE2.AppImage"),
        };

        var selected = EmulatorReleaseAssetSelection.SelectDuckStationLinuxAsset(
            assets,
            static asset => asset.Name);

        Assert.Equal("DuckStation-x64-SSE2.AppImage", selected?.Name);
    }

    [Fact]
    public void SelectDuckStationLinuxAsset_prefers_arm64_on_arm64_host()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return;

        var assets = new[]
        {
            new TestAsset("DuckStation-armhf.AppImage"),
            new TestAsset("DuckStation-x64.AppImage"),
            new TestAsset("DuckStation-arm64.AppImage"),
        };

        var selected = EmulatorReleaseAssetSelection.SelectDuckStationLinuxAsset(
            assets,
            static asset => asset.Name);

        Assert.Equal("DuckStation-arm64.AppImage", selected?.Name);
    }

    [Fact]
    public void SelectFirstWindowsAsset_prefers_matching_architecture()
    {
        var assets = new[]
        {
            new TestAsset("duckstation-windows-x64-release.zip"),
            new TestAsset("duckstation-windows-arm64-release.zip"),
        };

        var selected = EmulatorReleaseAssetSelection.SelectFirstWindowsAsset(
            assets,
            static asset => asset.Name,
            static asset => asset.Name.Contains("duckstation-windows", StringComparison.OrdinalIgnoreCase));

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Assert.Equal("duckstation-windows-arm64-release.zip", selected?.Name);
        else
            Assert.Equal("duckstation-windows-x64-release.zip", selected?.Name);
    }

    [Fact]
    public void SelectFirstLinuxAsset_matches_current_pcsx2_release_asset_names()
    {
        var assets = new[]
        {
            new TestAsset("pcsx2-v2.7.416-linux-flatpak-x64-Qt.flatpak"),
            new TestAsset("pcsx2-v2.7.416-linux-appimage-x64-Qt.AppImage"),
            new TestAsset("pcsx2-v2.7.416-macos-Qt.tar.xz"),
        };

        var selected = EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
            assets,
            static asset => asset.Name,
            static asset =>
                asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains("appimage", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("pcsx2-v2.7.416-linux-appimage-x64-Qt.AppImage", selected?.Name);
    }

    private sealed record TestAsset(string Name);
}
