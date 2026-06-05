using System.Reflection;
using System.Text.Json;
using AES_Lacrima.Services;

// AES_Lacrima is annotated as windows-only at the assembly level, but these tests
// exercise cross-platform update logic and are intended to run on every CI host.
#pragma warning disable CA1416

namespace AES_Lacrima.Tests;

public sealed class AppUpdateServiceVersionTests
{
    [Theory]
    [InlineData("0.1.12b", "0.1.12")]
    [InlineData("v0.1.12b", "0.1.12")]
    [InlineData("0.1.12c", "0.1.12b")]
    public void CompareSemanticVersions_SuffixRevision_IsTreatedAsNewer(string left, string right)
    {
        var result = Compare(left, right);

        Assert.True(result > 0, $"Expected '{left}' to be newer than '{right}', but compare result was {result}.");
    }

    [Theory]
    [InlineData("0.1.12-c", "0.1.12c")]
    [InlineData("v0.1.12-c", "0.1.12c")]
    [InlineData("0.1.12-b", "0.1.12b")]
    public void CompareSemanticVersions_DashedSingleLetter_EqualsUndashedSuffix(string left, string right)
    {
        var result = Compare(left, right);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CompareSemanticVersions_Prerelease_RemainsOlderThanStable()
    {
        var result = Compare("0.1.12-beta", "0.1.12");

        Assert.True(result < 0, $"Expected prerelease to be older than stable, but compare result was {result}.");
    }

    [Fact]
    public void PendingUpdateManifest_RoundTripsThroughJson()
    {
        var manifest = new PendingUpdateManifest(
            PendingUpdateTargetKind.LinuxAppImage,
            "/tmp/staged/AES-Lacrima.AppImage",
            "/home/user/Apps/AES-Lacrima.AppImage",
            "/home/user/Apps/AES-Lacrima.AppImage",
            "/tmp/staged",
            "0.1.13",
            4242,
            DateTimeOffset.Parse("2026-05-27T12:00:00Z"));

        var json = JsonSerializer.Serialize(manifest, PendingUpdateJsonContext.Default.PendingUpdateManifest);
        var restored = JsonSerializer.Deserialize(json, PendingUpdateJsonContext.Default.PendingUpdateManifest);

        Assert.NotNull(restored);
        Assert.Equal(manifest, restored);
    }

    [Fact]
    public void SelectBestAsset_LinuxAppImage_PicksCompatibleAsset()
    {
        var assets = new[]
        {
            new AppReleaseAssetInfo("AES-Lacrima-Linux-x86_64.AppImage", "https://example.com/linux.appimage", null),
            new AppReleaseAssetInfo("AES-Lacrima-Windows-x64.zip", "https://example.com/windows.zip", null),
        };

        var selected = InvokeSelectBestAsset(assets, preferAotUpdates: false);

        Assert.NotNull(selected);
        Assert.Equal("AES-Lacrima-Linux-x86_64.AppImage", selected!.Name);
    }

    private static AppReleaseAssetInfo? InvokeSelectBestAsset(IReadOnlyList<AppReleaseAssetInfo> assets, bool preferAotUpdates)
    {
        var method = typeof(AppUpdateService).GetMethod("SelectBestAsset", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        const int linuxAppImageTargetKind = 2;
        var value = method!.Invoke(null, [assets, linuxAppImageTargetKind, preferAotUpdates]);
        return value as AppReleaseAssetInfo;
    }

    private static int Compare(string left, string right)
    {
        var method = typeof(AppUpdateService).GetMethod("CompareSemanticVersions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var value = method!.Invoke(null, [left, right]);
        return Assert.IsType<int>(value);
    }
}

