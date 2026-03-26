using System.Reflection;
using AES_Lacrima.Services;

namespace AES_Lacrima.Tests;

public sealed class AppUpdateServiceVersionTests
{
    [Theory]
    [InlineData("0.1.12b", "0.1.12")]
    [InlineData("v0.1.12b", "0.1.12")]
    [InlineData("0.1.12c", "0.1.12b")]
    public void CompareSemanticVersions_SuffixRevision_IsTreatedAsNewer(string left, string right)
    {
        var result = Compare(left, right);

        Assert.True(result > 0, $"Expected '{left}' to be newer than '{right}', but compare result was {result}.");
    }

    [Fact]
    public void CompareSemanticVersions_Prerelease_RemainsOlderThanStable()
    {
        var result = Compare("0.1.12-beta", "0.1.12");

        Assert.True(result < 0, $"Expected prerelease to be older than stable, but compare result was {result}.");
    }

    private static int Compare(string left, string right)
    {
        var method = typeof(AppUpdateService).GetMethod("CompareSemanticVersions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var value = method!.Invoke(null, [left, right]);
        return Assert.IsType<int>(value);
    }
}
