using System.Collections.Generic;
using System.IO;

namespace AES_Lacrima.Services.Xenia;

public static class XeniaTitleIdResolver
{
    private static readonly Xbox360MetadataService MetadataService = new();

    public static string? Resolve(string? gamePath, ISet<string>? knownTitleIds = null)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return null;

        var metadata = MetadataService.TryReadGameMetadata(gamePath, knownTitleIds);
        if (!string.IsNullOrWhiteSpace(metadata?.TitleId))
            return metadata.TitleId.ToUpperInvariant();

        return null;
    }
}
