using AES_Lacrima.Services.Rpcs3;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3YamlSaveHelperTests
{
    [Fact]
    public void StripYamlStreamDocumentMarkers_RemovesDocumentEndMarker()
    {
        const string input = """
            PPU-test:
              "Patch":
                Game:
                  BLUS00001:
                    All:
                      Enabled: true
            ...
            """;

        var result = Rpcs3YamlSaveHelper.StripYamlStreamDocumentMarkers(input);

        Assert.DoesNotContain("...", result, StringComparison.Ordinal);
        Assert.Contains("Enabled: true", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripYamlStreamDocumentMarkers_RemovesDocumentStartMarker()
    {
        const string input = """
            ---
            Core:
              PPU Decoder: LLVM
            ...
            """;

        var result = Rpcs3YamlSaveHelper.StripYamlStreamDocumentMarkers(input);

        Assert.DoesNotContain("---", result, StringComparison.Ordinal);
        Assert.DoesNotContain("...", result, StringComparison.Ordinal);
        Assert.Contains("Core:", result, StringComparison.Ordinal);
    }
}
