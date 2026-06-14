using System;
using System.IO;
using System.Reflection;
using AES_Core.IO;
using Xunit;

namespace AES_Core.Tests;

public sealed class ApplicationPathsTests : IDisposable
{
    private readonly bool? _originalWritableFlag;
    private readonly string? _originalXdgDataHome;
    private readonly string? _originalXdgStateHome;

    public ApplicationPathsTests()
    {
        _originalWritableFlag = GetPrivateStaticBool("_isAppBaseWritable");
        _originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        _originalXdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
    }

    public void Dispose()
    {
        SetPrivateStaticBool("_isAppBaseWritable", _originalWritableFlag);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", _originalXdgStateHome);
    }

    [Fact]
    public void DataRootDirectory_UsesUserStandardLocation()
    {
        if (OperatingSystem.IsWindows())
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AES_Lacrima");
            Assert.Equal(expected, ApplicationPaths.DataRootDirectory);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", "AES_Lacrima");
            Assert.Equal(expected, ApplicationPaths.DataRootDirectory);
            return;
        }

        // For Linux/Unix, XDG_DATA_HOME should influence the path if present
        var tempDir = Path.Combine(Path.GetTempPath(), "aes-lacrima-test", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", tempDir);
        var expectedLinux = Path.Combine(tempDir, "AES_Lacrima");
        Assert.Equal(expectedLinux, ApplicationPaths.DataRootDirectory);
    }

    [Fact]
    public void GetSettingsCacheToolPaths_AreRelativeToDataRoot()
    {
        SetPrivateStaticBool("_isAppBaseWritable", true);

        var settings = ApplicationPaths.GetSettingsFile("test.json");
        var cache = ApplicationPaths.GetCacheFile("test.cache");
        var tool = ApplicationPaths.GetToolFile("foo.exe");

        Assert.Equal(Path.Combine(ApplicationPaths.DataRootDirectory, "Settings", "test.json"), settings);
        Assert.Equal(Path.Combine(ApplicationPaths.DataRootDirectory, "Cache", "test.cache"), cache);
        Assert.Equal(Path.Combine(ApplicationPaths.DataRootDirectory, "Tools", "foo.exe"), tool);
    }

    [Fact]
    public void IsDirectoryWritable_returns_true_for_user_writable_directory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aes-lacrima-writable-test", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            Assert.True(ApplicationPaths.IsDirectoryWritable(tempDir));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static bool? GetPrivateStaticBool(string fieldName)
    {
        var field = typeof(ApplicationPaths).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        if (field == null) return null;
        return (bool?)field.GetValue(null);
    }

    private static void SetPrivateStaticBool(string fieldName, bool? value)
    {
        var field = typeof(ApplicationPaths).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        if (field == null) return;
        field.SetValue(null, value);
    }
}
