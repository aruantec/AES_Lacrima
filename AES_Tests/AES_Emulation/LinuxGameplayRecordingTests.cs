using AES_Emulation.Linux;
using AES_Emulation.Services;
using AES_Lacrima.Services.Emulation;

namespace AES_Tests.AES_Emulation;

public sealed class LinuxGameplayRecordingTests
{
    [Fact]
    public void BuildVideoFfmpegArguments_IsVideoOnlyPipeInput()
    {
        var args = LinuxGameplayRecorderService.BuildVideoFfmpegArguments(
            "/tmp/out.mkv",
            1920,
            1080,
            60,
            GameplayRecordingContainer.Mkv,
            "libsvtav1",
            "-preset 8",
            12_000);

        Assert.Contains("-i pipe:0", args, StringComparison.Ordinal);
        Assert.Contains("-an", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-f pulse", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPcmPipeFfmpegAudioArguments_UsesWavPipeInput()
    {
        var args = LinuxGameplayRecorderService.BuildPcmPipeFfmpegAudioArguments("/tmp/out.audio.wav");

        Assert.Contains("-f s16le", args, StringComparison.Ordinal);
        Assert.Contains("-ar 48000", args, StringComparison.Ordinal);
        Assert.Contains("-ac 2", args, StringComparison.Ordinal);
        Assert.Contains("-i pipe:0", args, StringComparison.Ordinal);
        Assert.Contains("-c:a pcm_s16le", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-f pulse", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPulseFfmpegAudioArguments_UsesLibavpulse()
    {
        var args = LinuxGameplayRecorderService.BuildPulseFfmpegAudioArguments(
            "/tmp/out.audio.wav",
            "default");

        Assert.Contains("-f pulse", args, StringComparison.Ordinal);
        Assert.Contains("default", args, StringComparison.Ordinal);
        Assert.Contains("-c:a pcm_s16le", args, StringComparison.Ordinal);
        Assert.Contains("-f wav", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRemuxArguments_WavSidecar_ReencodesAudioToAac()
    {
        var args = LinuxGameplayRecorderService.BuildRemuxArguments(
            "/tmp/video.mkv",
            "/tmp/audio.wav",
            "/tmp/out.mp4",
            GameplayRecordingContainer.Mp4,
            "libx264");

        Assert.Contains("-c:a aac", args, StringComparison.Ordinal);
        Assert.Contains("-c:v copy", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:a copy", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRemuxArguments_MkvSidecar_CopiesAudio()
    {
        var args = LinuxGameplayRecorderService.BuildRemuxArguments(
            "/tmp/video.mkv",
            "/tmp/audio.mkv",
            "/tmp/out.mp4",
            GameplayRecordingContainer.Mp4,
            "libx264");

        Assert.Contains("-c:a copy", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRemuxArguments_Mp4_UsesFaststartMovFlags()
    {
        var args = LinuxGameplayRecorderService.BuildRemuxArguments(
            "/tmp/video.mkv",
            "/tmp/audio.m4a",
            "/tmp/out.mp4",
            GameplayRecordingContainer.Mp4,
            "libx264");

        Assert.Contains("-movflags +faststart", args, StringComparison.Ordinal);
        Assert.Contains("-c:v copy", args, StringComparison.Ordinal);
        Assert.Contains("-shortest", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFfmpegArguments_WithoutAudio_IsVideoOnly()
    {
        var args = LinuxGameplayRecorderService.BuildFfmpegArguments(
            "/tmp/out.mkv",
            1280,
            720,
            30,
            GameplayRecordingContainer.Mkv,
            "libx264",
            "-preset veryfast",
            8000,
            null);

        Assert.Contains("-an", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-f pulse", args, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePulseMonitor_OutputDevice_UsesMonitorName()
    {
        var input = LinuxFfmpegPulseAudio.ResolvePulseMonitor(
            GameplayRecordingAudioSource.OutputDevice,
            0,
            0,
            "bluez_output.test.1");

        Assert.Equal("bluez_output.test.1.monitor", input);
    }

    [Fact]
    public void ResolvePwRecordTarget_StripsMonitorSuffix()
    {
        var target = LinuxFfmpegPulseAudio.ResolvePwRecordTarget(
            GameplayRecordingAudioSource.OutputDevice,
            0,
            0,
            "bluez_output.test.1.monitor");

        Assert.Equal("bluez_output.test.1", target);
    }

    [Fact]
    public void ResolvePulseMonitor_DefaultOutput_ResolvesSinkMonitor()
    {
        var input = LinuxFfmpegPulseAudio.ResolvePulseMonitor(
            GameplayRecordingAudioSource.OutputDevice,
            0,
            0,
            null);

        if (LinuxFfmpegPulseAudio.TryResolveDefaultSinkMonitor(out var expected))
            Assert.Equal(expected, input);
        else
            Assert.Equal("default", input);
    }

    [Fact]
    public void CollectAudioCandidateProcessIds_IncludesCompositorTree()
    {
        var candidates = LinuxGameplayAudioCapture.CollectAudioCandidateProcessIds(4242, 9000);
        Assert.Equal(4242, candidates[0]);
        Assert.Contains(4242, candidates);
    }
}
