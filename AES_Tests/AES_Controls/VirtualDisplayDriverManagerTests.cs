using AES_Controls.Helpers;
using Xunit;

namespace AES_Controls.Tests;

public class VirtualDisplayDriverManagerTests
{
    [Fact]
    public void TryReadConfiguredDisplayCount_ReturnsZeroWhenFileMissing()
    {
        var count = VirtualDisplayDriverManager.TryReadConfiguredDisplayCount(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "vdd_settings.xml"));

        Assert.Equal(0, count);
    }

    [Fact]
    public void CaptureRequiredUserMessage_MentionsGamescopeParity()
    {
        Assert.Contains("gamescope", VirtualDisplayDriverManager.CaptureRequiredUserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capture", VirtualDisplayDriverManager.CaptureRequiredUserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WingetPackageId_IsExpectedValue()
    {
        Assert.Equal("VirtualDrivers.Virtual-Display-Driver", VirtualDisplayDriverManager.WingetPackageId);
    }
}
