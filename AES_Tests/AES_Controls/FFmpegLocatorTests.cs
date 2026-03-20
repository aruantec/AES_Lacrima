using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class FFmpegLocatorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"aes-lacrima-ffmpeg-{Guid.NewGuid():N}");
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public FFmpegLocatorTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void FindFFmpegPath_ReturnsBinaryFromPathEnvironmentVariable()
    {
        var binaryName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var binaryPath = Path.Combine(_tempDirectory, binaryName);
        File.WriteAllText(binaryPath, string.Empty);
        Environment.SetEnvironmentVariable("PATH", _tempDirectory);

        var result = FFmpegLocator.FindFFmpegPath();

        Assert.Equal(binaryPath, result);
        Assert.True(FFmpegLocator.IsFFmpegAvailable());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
