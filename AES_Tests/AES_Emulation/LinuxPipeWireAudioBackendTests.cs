using AES_Emulation.Linux;

namespace AES_Tests.AES_Emulation;

public sealed class LinuxPipeWireAudioBackendTests
{
    [Theory]
    [InlineData("Volume: 1.00", 1.0f, false)]
    [InlineData("Volume: 0.46", 0.46f, false)]
    [InlineData("Volume: 0.00 [MUTED]", 0.0f, true)]
    public void TryParseWpctlVolume_ParsesOutput(string input, float expectedVolume, bool expectedMuted)
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.True(LinuxPipeWireAudioBackend.TryParseWpctlVolume(input, out var volume, out var muted));
        Assert.Equal(expectedVolume, volume, precision: 3);
        Assert.Equal(expectedMuted, muted);
    }

    [Fact]
    public void TryListSinkInputsJson_UsesNativePipeWireToolsWhenPactlMissing()
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (LinuxAudioEnvironmentHelper.ResolvePactlExecutable() != null)
            return;

        if (LinuxAudioEnvironmentHelper.ResolvePwDumpExecutable() == null ||
            LinuxAudioEnvironmentHelper.ResolveWpctlExecutable() == null)
        {
            return;
        }

        Assert.True(LinuxPipeWireAudioBackend.TryListSinkInputsJson(out var json));
        Assert.StartsWith("[", json, StringComparison.Ordinal);
    }
}
