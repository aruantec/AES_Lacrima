using System.Diagnostics;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Linux;

namespace AES_Tests.AES_Emulation;

public sealed class ShadPs4HandlerLinuxTests
{
    [Fact]
    public void BuildStartInfo_OnLinux_UsesAppImageAndGameArguments()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var appImagePath = Path.Combine(tempRoot, "Shadps4-sdl.AppImage");
            File.WriteAllText(appImagePath, string.Empty);
            var gamePath = Path.Combine(tempRoot, "CUSA01067");
            Directory.CreateDirectory(Path.Combine(gamePath, "sce_sys"));
            File.WriteAllText(Path.Combine(gamePath, "sce_sys", "param.sfo"), string.Empty);

            var startInfo = ShadPs4Handler.Instance.BuildStartInfo(appImagePath, gamePath, startFullscreen: false);

            Assert.Equal(appImagePath, startInfo.FileName);
            Assert.Contains("-g", startInfo.ArgumentList);
            Assert.Contains(gamePath, startInfo.ArgumentList);
            Assert.Contains("-f", startInfo.ArgumentList);
            Assert.Contains("false", startInfo.ArgumentList);
            Assert.True(startInfo.Environment.TryGetValue("SHADPS4_USER_DIR", out var userDir));
            Assert.Equal(ShadPs4UserDirectoryHelper.ResolveUserDirectory(tempRoot), userDir);
            Assert.Equal("pulse", startInfo.Environment["SDL_AUDIODRIVER"]);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void BuildStartInfo_OnLinux_UsesPortableUserDirectoryWhenPresent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "user"));
        try
        {
            var appImagePath = Path.Combine(tempRoot, "Shadps4-sdl.AppImage");
            File.WriteAllText(appImagePath, string.Empty);
            var gamePath = Path.Combine(tempRoot, "CUSA01067");
            Directory.CreateDirectory(Path.Combine(gamePath, "sce_sys"));
            File.WriteAllText(Path.Combine(gamePath, "sce_sys", "param.sfo"), string.Empty);

            var startInfo = ShadPs4Handler.Instance.BuildStartInfo(appImagePath, gamePath, startFullscreen: false);

            Assert.True(startInfo.Environment.TryGetValue("SHADPS4_USER_DIR", out var userDir));
            Assert.Equal(Path.Combine(tempRoot, "user"), userDir);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void BuildStartInfo_OnLinux_ForcesWindowedEvenWhenFullscreenRequested()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var appImagePath = Path.Combine(tempRoot, "Shadps4-sdl.AppImage");
            File.WriteAllText(appImagePath, string.Empty);
            var gamePath = Path.Combine(tempRoot, "CUSA01067");
            Directory.CreateDirectory(Path.Combine(gamePath, "sce_sys"));
            File.WriteAllText(Path.Combine(gamePath, "sce_sys", "param.sfo"), string.Empty);

            var startInfo = ShadPs4Handler.Instance.BuildStartInfo(appImagePath, gamePath, startFullscreen: true);

            Assert.Contains("-f", startInfo.ArgumentList);
            Assert.Equal("false", startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-f") + 1]);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { /* ignored */ }
        }
    }

    [Fact]
    public void PrepareDirectGamescopeLaunch_UnwrapsEnvWrapperToDirectAppImage()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var appImagePath = "/tmp/Shadps4-sdl.AppImage";
        var startInfo = new ProcessStartInfo
        {
            FileName = "env",
        };
        startInfo.ArgumentList.Add("APPIMAGE_EXTRACT_AND_RUN=1");
        startInfo.ArgumentList.Add(appImagePath);
        startInfo.ArgumentList.Add("--appimage-extract-and-run");
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add("/game");

        LinuxAppImageLaunchHelper.PrepareDirectGamescopeLaunch(startInfo);

        Assert.Equal(appImagePath, startInfo.FileName);
        Assert.DoesNotContain("--appimage-extract-and-run", startInfo.ArgumentList);
        Assert.Equal("-g", startInfo.ArgumentList[0]);
        Assert.Equal("/game", startInfo.ArgumentList[1]);
    }

    [Fact]
    public void PrepareEmulatorStartInfoForGamescope_StripsHostDisplayEnvironment()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/true",
            Environment =
            {
                ["DISPLAY"] = ":0",
                ["WAYLAND_DISPLAY"] = "wayland-0",
            },
        };

        LinuxCompositorLaunchHelper.PrepareEmulatorStartInfoForGamescope(startInfo);

        Assert.False(startInfo.Environment.ContainsKey("DISPLAY"));
        Assert.False(startInfo.Environment.ContainsKey("WAYLAND_DISPLAY"));
        Assert.Equal("x11", startInfo.Environment["SDL_VIDEODRIVER"]);
    }
}
