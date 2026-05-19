using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AES_Lacrima.Services;

/// <summary>
/// Reads Wii U installed title metadata from Cemu-style package folders (code/content/meta).
/// See <see href="https://wiiubrew.org/wiki/Meta.xml">WiiUBrew meta.xml</see> and
/// <see href="https://github.com/cemu-project/Cemu">Cemu</see> title list parsing.
/// </summary>
internal static class WiiUInstalledGameHelper
{
    private static readonly string[] PreferredLongNameKeys =
    [
        "longname_en",
        "longname_us",
        "longname_ja",
        "longname_fr",
        "longname_de",
        "longname_es",
        "longname_it",
    ];

    public static bool IsInstalledGameFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return Directory.Exists(path) &&
                   Directory.Exists(Path.Combine(path, "code")) &&
                   Directory.Exists(Path.Combine(path, "content")) &&
                   Directory.Exists(Path.Combine(path, "meta")) &&
                   File.Exists(GetMetaXmlPath(path));
        }
        catch
        {
            return false;
        }
    }

    public static string? GetTitleId(string? path)
    {
        var document = TryLoadMetaDocument(path);
        if (document == null)
            return null;

        var rawTitleId = document.Root?
            .Element("title_id")?
            .Value?
            .Trim();

        return FormatTitleId(rawTitleId);
    }

    public static string? GetTitleName(string? path)
    {
        var document = TryLoadMetaDocument(path);
        if (document?.Root == null)
            return null;

        foreach (var key in PreferredLongNameKeys)
        {
            var value = document.Root.Element(key)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeTitle(value);
        }

        var fallback = document.Root.Elements()
            .FirstOrDefault(element => element.Name.LocalName.StartsWith("longname_", StringComparison.OrdinalIgnoreCase))
            ?.Value?
            .Trim();

        return string.IsNullOrWhiteSpace(fallback) ? null : NormalizeTitle(fallback);
    }

    private static XDocument? TryLoadMetaDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            foreach (var candidateDirectory in GetCandidateDirectories(path))
            {
                var metaXmlPath = GetMetaXmlPath(candidateDirectory);
                if (!File.Exists(metaXmlPath))
                    continue;

                return XDocument.Load(metaXmlPath);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string GetMetaXmlPath(string directory)
        => Path.Combine(directory, "meta", "meta.xml");

    private static IEnumerable<string> GetCandidateDirectories(string path)
    {
        var normalizedPath = path.Trim();
        if (Directory.Exists(normalizedPath))
        {
            yield return normalizedPath;
            yield break;
        }

        if (!File.Exists(normalizedPath))
            yield break;

        var parent = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            yield return parent;
    }

    private static string? FormatTitleId(string? rawTitleId)
    {
        if (string.IsNullOrWhiteSpace(rawTitleId))
            return null;

        var hex = Regex.Replace(rawTitleId, @"[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();
        if (hex.Length != 16)
            return hex.Length > 0 ? hex : null;

        return $"{hex[..8]}-{hex[8..]}";
    }

    private static string NormalizeTitle(string title)
        => title.Replace("\r", " ").Replace("\n", " ").Trim();
}
