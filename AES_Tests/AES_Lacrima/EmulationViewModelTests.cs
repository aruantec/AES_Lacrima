using System.Reflection;
using AES_Lacrima.ViewModels;

namespace AES_Lacrima.Tests;

public sealed class EmulationViewModelTests
{
    [Fact]
    public void ScanFolderForRomPaths_WiiUPackageDirectory_ReturnsFolderPath()
    {
        using var tempDir = new TempDirectory();
        var wiiuDir = Path.Combine(tempDir.Path, "SuperMario3DWorld");
        Directory.CreateDirectory(wiiuDir);
        Directory.CreateDirectory(Path.Combine(wiiuDir, "code"));
        Directory.CreateDirectory(Path.Combine(wiiuDir, "content"));
        Directory.CreateDirectory(Path.Combine(wiiuDir, "meta"));

        var method = typeof(EmulationViewModel).GetMethod("ScanFolderForRomPaths", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { tempDir.Path, new string[] { "*.wud" } });
        Assert.NotNull(result);

        var paths = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<string>>(result);
        Assert.Contains(wiiuDir, paths, StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
        }
    }
}
