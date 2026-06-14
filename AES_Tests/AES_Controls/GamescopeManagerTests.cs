using AES_Controls.Helpers;
using Xunit;

namespace AES_Controls.Tests;

public sealed class GamescopeManagerTests
{
    [Fact]
    public void ParseAptPackageVersions_ParsesMadisonAndCandidateLines()
    {
        const string output = """
            gamescope | 3.16.22+ds-1 | http://archive.ubuntu.com/ubuntu questing/multiverse amd64 Packages
            gamescope | 3.16.20+ds-1 | http://archive.ubuntu.com/ubuntu questing/multiverse amd64 Packages
              Candidate: 3.16.20+ds-1
            """;

        var versions = GamescopeManager.ParseAptPackageVersions(output);

        Assert.Contains("3.16.22+ds-1", versions);
        Assert.Contains("3.16.20+ds-1", versions);
    }

    [Fact]
    public void FindMatchingDistroPackage_MatchesTagPrefix()
    {
        var packages = new List<string> { "3.16.22+ds-1", "3.16.20+ds-1" };

        Assert.Equal("3.16.22+ds-1", GamescopeManager.FindMatchingDistroPackage("3.16.22", packages));
        Assert.Null(GamescopeManager.FindMatchingDistroPackage("3.16.24", packages));
    }

    [Theory]
    [InlineData("3.16.24", "3.16.20", 1)]
    [InlineData("3.16.20", "3.16.24", -1)]
    [InlineData("3.16.22", "3.16.22", 0)]
    [InlineData("3.16.23.1", "3.16.23", 1)]
    public void CompareVersionKeys_OrdersSemverLikeTags(string left, string right, int expectedSign)
    {
        var result = GamescopeManager.CompareVersionKeys(left, right);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(result));
    }

    [Theory]
    [InlineData("[gamescope] console: gamescope version 3.16.20+ds-1 (gcc 15.2.0)", "3.16.20")]
    [InlineData("3.16.22+ds-1", "3.16.22")]
    public void ExtractVersionFromText_ParsesGamescopeVersions(string input, string expected)
    {
        Assert.Equal(expected, GamescopeManager.ExtractVersionFromText(input));
    }

    [Fact]
    public void AptSourceBuildDependencies_UseLibwaylandDevNotServerDevSplitPackage()
    {
        Assert.Contains("libwayland-dev", GamescopeManager.AptSourceBuildDependencyList);
        Assert.DoesNotContain("libwayland-server-dev", GamescopeManager.AptSourceBuildDependencyList);
    }

    [Fact]
    public void SourceBuildDependencies_IncludeWlrootsSessionLibraries()
    {
        Assert.Contains("liblcms2-dev", GamescopeManager.AptSourceBuildDependencyList);
        Assert.Contains("libseat-dev", GamescopeManager.AptSourceBuildDependencyList);
        Assert.Contains("lcms2", GamescopeManager.PacmanSourceBuildDependencyList);
        Assert.Contains("libseat", GamescopeManager.PacmanSourceBuildDependencyList);
    }

    [Fact]
    public void SourceInstallScript_UsesMesonSkipSubprojectsInstall()
    {
        var script = typeof(GamescopeManager)
            .GetMethod("BuildSourceInstallScript", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { "3.16.24", "/tmp/aes-tools", "/tmp/gamescope-build.log" }) as string;

        Assert.NotNull(script);
        Assert.Contains("meson install -C \"$WORK_DIR/build\" --skip-subprojects", script);
        Assert.DoesNotContain("ninja -C \"$WORK_DIR/build\" install", script);
    }
}
