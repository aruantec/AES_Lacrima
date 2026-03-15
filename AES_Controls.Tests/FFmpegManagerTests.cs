using System;
using AES_Controls.Helpers;
using Xunit;

namespace AES_Controls.Tests;

public sealed class FFmpegManagerTests
{
    [Fact]
    public void ReportActivity_UpdatesStatus()
    {
        var mgr = new FFmpegManager();

        mgr.ReportActivity(true);
        Assert.Contains("FFmpeg is active", mgr.Status);

        mgr.ReportActivity(false);
        Assert.Equal("Idle", mgr.Status);
    }

    [Fact]
    public void KillAllFfmpegActivity_InvokesEvent()
    {
        var mgr = new FFmpegManager();
        bool called = false;
        mgr.RequestFfmpegTermination += () => called = true;

        mgr.KillAllFfmpegActivity();

        Assert.True(called, "RequestFfmpegTermination should be invoked.");
    }
}
