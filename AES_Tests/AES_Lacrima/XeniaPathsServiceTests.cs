using AES_Lacrima.Services.Xenia;

namespace AES_Tests.AES_Lacrima;

public sealed class XeniaPathsServiceTests
{
    [Fact]
    public void ResolveStorageRoot_LinuxAppImagePrefersDirectoryWithPatches()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "aes-xenia-paths-" + Guid.NewGuid().ToString("N"));
        var appImagePath = Path.Combine(tempRoot, "xenia-canary.AppImage");
        var managedDirectory = Path.Combine(tempRoot, "managed");
        var patchesDirectory = Path.Combine(tempRoot, "patches");
        try
        {
            Directory.CreateDirectory(patchesDirectory);
            File.WriteAllText(Path.Combine(patchesDirectory, "4D5307E6 - Test.patch.toml"), "[[patch]]\n");

            var resolved = XeniaPathsService.ResolveStorageRoot(managedDirectory, appImagePath);
            Assert.Equal(tempRoot, resolved);
            Assert.Equal(
                patchesDirectory,
                XeniaPathsService.ResolvePatchesDirectory(managedDirectory, appImagePath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolvePatchesDirectory_UsesResolvedStorageRootOnly()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "aes-xenia-paths-" + Guid.NewGuid().ToString("N"));
        var appImagePath = Path.Combine(tempRoot, "app", "xenia.AppImage");
        var managedDirectory = Path.Combine(tempRoot, "managed");
        var appImagePatches = Path.Combine(tempRoot, "app", "patches");
        var managedPatches = Path.Combine(managedDirectory, "patches");
        try
        {
            Directory.CreateDirectory(appImagePatches);
            Directory.CreateDirectory(managedPatches);
            File.WriteAllText(Path.Combine(appImagePatches, "4D5307E6 - Test.patch.toml"), "[[patch]]\n");
            File.WriteAllText(Path.Combine(managedPatches, "4D5307E6 - Stale.patch.toml"), "[[patch]]\n");

            var resolvedPatches = XeniaPathsService.ResolvePatchesDirectory(managedDirectory, appImagePath);
            Assert.Equal(appImagePatches, resolvedPatches);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
