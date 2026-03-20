using System.Reflection;
using AES_Controls.Player;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace AES_Controls.Tests;

public sealed class VideoViewTests
{
    [AvaloniaFact]
    public void Constructor_SetsExpectedDefaults()
    {
        var view = new VideoView();

        Assert.Null(view.Player);
        Assert.False(view.IsRenderingPaused);
        Assert.Equal(Stretch.Uniform, view.Stretch);
        Assert.Equal(0, view.Rotation);
        Assert.Equal(VideoFlip.None, view.Flip);
        Assert.Equal(60.0, view.HeartbeatFps);
        Assert.False(view.UseCustomHeartbeat);
        Assert.Equal(0.0, view.AudioSyncOffset);
    }

    [AvaloniaFact]
    public void PropertyUpdates_WithNullPlayer_DoNotThrow()
    {
        var view = new VideoView();

        var ex = Record.Exception(() =>
        {
            view.Stretch = Stretch.UniformToFill;
            view.Rotation = 90;
            view.Flip = VideoFlip.Both;
            view.AudioSyncOffset = 250;
            view.HeartbeatFps = 120;
            view.UseCustomHeartbeat = true;
            view.IsRenderingPaused = true;
            view.IsRenderingPaused = false;
        });

        Assert.Null(ex);
    }

    [AvaloniaFact]
    public void CalculateInterval_UsesFallbackForNonPositiveValues()
    {
        var view = new VideoView();
        var method = typeof(VideoView).GetMethod("CalculateInterval", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var zero = Assert.IsType<TimeSpan>(method!.Invoke(view, new object[] { 0.0 }));
        var negative = Assert.IsType<TimeSpan>(method.Invoke(view, new object[] { -10.0 }));
        var sixty = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

        Assert.Equal(sixty, zero);
        Assert.Equal(sixty, negative);
    }
}
