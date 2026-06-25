using System.Diagnostics;
using AES_Emulation.Linux;

namespace AES_Tests.AES_Emulation;

public sealed class LinuxAudioEnvironmentHelperTests
{
    [Fact]
    public void Apply_SetsRuntimeDirWhenParentEnvironmentIsMissing()
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (!Directory.Exists("/run/user/1000"))
            return;

        var startInfo = new ProcessStartInfo { FileName = "/bin/true" };
        LinuxAudioEnvironmentHelper.Apply(startInfo);

        Assert.True(startInfo.Environment.TryGetValue("XDG_RUNTIME_DIR", out var runtimeDir));
        Assert.StartsWith("/run/user/", runtimeDir, StringComparison.Ordinal);
        Assert.Equal("pulse", startInfo.Environment["SDL_AUDIODRIVER"]);
        Assert.True(startInfo.Environment.ContainsKey("PULSE_SERVER"));
        Assert.StartsWith("unix:", startInfo.Environment["PULSE_SERVER"], StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePreferredSdlAudioDriver_FallsBackToAlsaWhenPulseUnavailable()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var originalPulseServer = Environment.GetEnvironmentVariable("PULSE_SERVER");
        var originalRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PULSE_SERVER", null);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/tmp/aes-no-pulse-runtime");

            Assert.Equal("alsa", LinuxAudioEnvironmentHelper.ResolvePreferredSdlAudioDriver());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PULSE_SERVER", originalPulseServer);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", originalRuntimeDir);
        }
    }
}
