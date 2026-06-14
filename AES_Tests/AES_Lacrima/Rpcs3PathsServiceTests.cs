using AES_Lacrima.Services.Rpcs3;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3PathsServiceTests
{
    [Fact]
    public void ResolveEmulatorDirectory_PrefersDirectoryWithPatchMarkers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var patchPath = Rpcs3PatchesService.GetPatchYmlPath(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
        File.WriteAllText(patchPath, "Version: 1.2\n");

        var launcherPath = Path.Combine(tempRoot, "rpcs3-v0.0.41-19462_linux64_nightly.AppImage");
        File.WriteAllText(launcherPath, string.Empty);

        var resolved = Rpcs3PathsService.ResolveEmulatorDirectory(preferredDirectory: null, launcherPath);

        Assert.Equal(Path.GetFullPath(tempRoot), Path.GetFullPath(resolved));
        Assert.True(Directory.Exists(Path.Combine(resolved, "config")));
        Assert.True(Directory.Exists(Path.Combine(resolved, "patches")));
    }

    [Fact]
    public void ResolveEmulatorDirectory_FallsBackToPreferredDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var resolved = Rpcs3PathsService.ResolveEmulatorDirectory(tempRoot, null);

        Assert.Equal(Path.GetFullPath(tempRoot), Path.GetFullPath(resolved));
        Assert.True(Directory.Exists(Path.Combine(resolved, "config", "custom_configs")));
    }

    [Fact]
    public void HasEmulatorDirectoryMarkers_DetectsAppImageBesideManagedTree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "rpcs3-v0.0.41-19462_linux64_nightly.AppImage"), string.Empty);

        Assert.True(Rpcs3PathsService.HasEmulatorDirectoryMarkers(tempRoot));
    }
}
