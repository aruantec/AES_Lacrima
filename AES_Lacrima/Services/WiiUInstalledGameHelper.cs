using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using AES_Lacrima.Services.Cemu;
using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.Services;

/// <summary>
/// Reads Wii U installed title metadata from Cemu-style package folders (code/content/meta).
/// See <see href="https://wiiubrew.org/wiki/Meta.xml">WiiUBrew meta.xml</see> and
/// <see href="https://github.com/cemu-project/Cemu">Cemu</see> title list parsing.
/// </summary>
internal static class WiiUInstalledGameHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(WiiUInstalledGameHelper));
    private static readonly string[] WiiUFileExtensions = [".wud", ".wux", ".wua", ".rpx"];
    private static readonly Regex TitleIdTextRegex = new(
        @"\b([0-9A-Fa-f]{8}[-_]?[0-9A-Fa-f]{8}|[0-9A-Fa-f]{16})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    internal readonly record struct WiiUMetadataResult(string? TitleId, string? TitleName);

    public static bool IsWiiURomFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return File.Exists(path) && IsWiiUFileExtension(Path.GetExtension(path));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsWiiUFileExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        return WiiUFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static WiiUMetadataResult ResolveMetadata(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return default;

        if (IsInstalledGameFolder(path))
        {
            return new WiiUMetadataResult(
                GetTitleId(path),
                GetTitleName(path));
        }

        if (!File.Exists(path))
            return default;

        foreach (var candidateDirectory in GetCandidateDirectories(path))
        {
            if (!IsInstalledGameFolder(candidateDirectory))
                continue;

            return new WiiUMetadataResult(
                GetTitleId(candidateDirectory),
                GetTitleName(candidateDirectory));
        }

        var titleId = ExtractTitleIdFromText(path) ??
                      ExtractTitleIdFromText(Path.GetFileNameWithoutExtension(path));
        var titleName = ExtractTitleNameFromFilePath(path);

        return new WiiUMetadataResult(titleId, titleName);
    }

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
                   !string.IsNullOrWhiteSpace(ResolveMetaXmlPath(path));
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
                var metaXmlPath = ResolveMetaXmlPath(candidateDirectory);
                if (string.IsNullOrWhiteSpace(metaXmlPath))
                    continue;

                return XDocument.Load(metaXmlPath);
            }
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return null;
    }

    private static string? ResolveMetaXmlPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        try
        {
            var metaDirectory = Path.Combine(directory, "meta");
            if (!Directory.Exists(metaDirectory))
                return null;

            var directPath = Path.Combine(metaDirectory, "meta.xml");
            if (File.Exists(directPath))
                return directPath;

            foreach (var candidate in Directory.EnumerateFiles(metaDirectory))
            {
                if (string.Equals(Path.GetFileName(candidate), "meta.xml", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        catch (Exception logEx) { Log.Warn("Failed to resolve Wii U meta.xml path.", logEx); }

        return null;
    }

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

        var grandParent = Path.GetDirectoryName(parent);
        if (!string.IsNullOrWhiteSpace(grandParent) && Directory.Exists(grandParent))
            yield return grandParent;
    }

    private static string? ExtractTitleIdFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = TitleIdTextRegex.Match(text);
        if (!match.Success)
            return null;

        return CemuTitleIdHelper.NormalizeDisplayTitleId(match.Groups[1].Value);
    }

    private static string? ExtractTitleNameFromFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var cleaned = TitleIdTextRegex.Replace(fileName, " ");
        cleaned = cleaned
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('(', ' ')
            .Replace(')', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();

        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
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
