using System.Reflection;
using System.Text.Json;
using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class MpvLibraryManagerTests : IDisposable
{
    private readonly string _tempDataRoot;
    private readonly string? _originalXdgDataHome;

    public MpvLibraryManagerTests()
    {
        _tempDataRoot = Path.Combine(Path.GetTempPath(), "aes-mpv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataRoot);
        _originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _tempDataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
        try
        {
            if (Directory.Exists(_tempDataRoot))
                Directory.Delete(_tempDataRoot, recursive: true);
        }
        catch { }

        // Reset any cached static state in MpvLibraryManager (cache path, loaded cache)
        var cacheField = typeof(MpvLibraryManager).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic);
        cacheField?.SetValue(null, null);

        ResetApplicationPathsWritableFlag();
    }

    private static void ResetApplicationPathsWritableFlag()
    {
        var field = typeof(AES_Core.IO.ApplicationPaths).GetField("_isAppBaseWritable", BindingFlags.Static | BindingFlags.NonPublic);
        field?.SetValue(null, null);
    }

    [Fact]
    public void ReportActivity_IncrementsAndDecrementsStatus()
    {
        var mgr = new MpvLibraryManager();
        mgr.ReportActivity(true);
        Assert.Contains("libmpv is active", mgr.Status);

        mgr.ReportActivity(false);
        Assert.Equal("Idle", mgr.Status);
    }

    [Fact]
    public void KillAllMpvActivity_InvokesRequestEvent()
    {
        var mgr = new MpvLibraryManager();
        bool called = false;
        mgr.RequestMpvTermination += () => called = true;

        mgr.KillAllMpvActivity();

        Assert.True(called, "RequestMpvTermination should be invoked.");
    }

    [Fact]
    public async Task GetAvailableVersionsAsync_UsesCacheWhenFresh()
    {
        // Arrange: Force ApplicationPaths to use our temp XDG_DATA_HOME and compute actual cache path.
        ResetApplicationPathsWritableFlag();

        var cachePathField = typeof(MpvLibraryManager).GetField("_cachePath", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(cachePathField);
        var cachePath = (string?)cachePathField.GetValue(null);
        Assert.False(string.IsNullOrEmpty(cachePath));

        var cacheDir = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(cacheDir);

        var cacheEntry = new
        {
            ETag = "\"abc\"",
            Versions = new[] { new { Tag = "v1", Title = "v1" }, new { Tag = "v2", Title = "v2" } },
            LastUpdated = DateTime.Now,
            LatestETag = "\"abc\"",
            LatestReleaseJson = "[]",
            LastLatestUpdated = DateTime.Now
        };
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cacheEntry));

        // Act
        var mgr = new MpvLibraryManager();
        var versions = await mgr.GetAvailableVersionsAsync();

        // Assert
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Tag == "v1");
    }

    [Fact]
    public void ExtractVersionFromText_ParsesVersionStringsCorrectly()
    {
        var method = typeof(MpvLibraryManager).GetMethod("ExtractVersionFromText", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        string? result = (string?)method.Invoke(null, new object[] { "mpv 0.35.0-unknown" });
        Assert.Equal("0.35.0-unknown", result);

        result = (string?)method.Invoke(null, new object[] { "mpv 1.2" });
        Assert.Equal("1.2", result);

        result = (string?)method.Invoke(null, new object[] { "no version here" });
        Assert.Null(result);
    }
}
