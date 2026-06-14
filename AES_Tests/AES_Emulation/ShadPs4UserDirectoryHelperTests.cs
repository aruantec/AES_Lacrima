using AES_Emulation.Linux;

namespace AES_Tests.AES_Emulation;

public sealed class ShadPs4UserDirectoryHelperTests
{
    [Fact]
    public void ResolveUserDirectory_OnWindows_UsesPortableUserFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var launchRoot = @"C:\emu\shadPS4";
        Assert.Equal(
            Path.Combine(launchRoot, "user"),
            ShadPs4UserDirectoryHelper.ResolveUserDirectory(launchRoot));
    }

    [Fact]
    public void ResolveUserDirectory_OnLinux_PrefersPortableUserWhenPresent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        var portableUser = Path.Combine(tempRoot, "user");
        Directory.CreateDirectory(portableUser);
        try
        {
            Assert.Equal(portableUser, ShadPs4UserDirectoryHelper.ResolveUserDirectory(tempRoot));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void ResolveUserDirectory_OnLinux_FallsBackToXdgWhenPortableMissing()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var resolved = ShadPs4UserDirectoryHelper.ResolveUserDirectory(tempRoot);
            Assert.Equal(ShadPs4UserDirectoryHelper.ResolveLinuxDefaultUserDirectory(), resolved);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void GetUserSubdirectory_UsesResolvedUserDirectoryForPatches()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "user", "patches"));
        try
        {
            var patchesRoot = ShadPs4UserDirectoryHelper.GetUserSubdirectory(tempRoot, "patches");
            Assert.Equal(Path.Combine(tempRoot, "user", "patches"), patchesRoot);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void ResolveContentRootDirectory_PrefersLauncherExecutableDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var appImagePath = Path.Combine(tempRoot, "Shadps4-sdl.AppImage");
        File.WriteAllText(appImagePath, string.Empty);
        try
        {
            var resolved = ShadPs4UserDirectoryHelper.ResolveContentRootDirectory(
                appImagePath,
                @"C:\managed\shadPS4");

            Assert.Equal(tempRoot, resolved);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void TryMirrorLinuxPortableSubtreeFromXdg_CopiesMissingPatchFiles()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        var portableUser = Path.Combine(tempRoot, "user");
        var portablePatches = Path.Combine(portableUser, "patches", "shadPS4");
        var xdgDataHome = Path.Combine(tempRoot, "xdg");
        var xdgPatches = Path.Combine(xdgDataHome, "shadPS4", "patches", "shadPS4");
        Directory.CreateDirectory(portablePatches);
        Directory.CreateDirectory(xdgPatches);
        File.WriteAllText(Path.Combine(xdgPatches, "Bloodborne.xml"), "<Patch />");
        File.WriteAllText(Path.Combine(xdgPatches, "files.json"), """{"Bloodborne.xml":["CUSA00900"]}""");

        var originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgDataHome);
        try
        {
            ShadPs4UserDirectoryHelper.TryMirrorLinuxPortableSubtreeFromXdg(tempRoot, "patches");

            Assert.True(File.Exists(Path.Combine(portablePatches, "Bloodborne.xml")));
            Assert.True(File.Exists(Path.Combine(portablePatches, "files.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdgDataHome);
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void ResolveLaunchRootFromPath_UsesDirectoryWhenLauncherPathIsFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            Assert.Equal(
                Path.GetFullPath(tempRoot),
                ShadPs4UserDirectoryHelper.ResolveLaunchRootFromPath(tempRoot));
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }
}
