using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AES_Core.IO;

namespace AES_Lacrima.Services;

internal static class EmulatorSectionDirectoryHelper
{
    public static string GetEmulatorSectionDirectory(string? sectionKey, string? sectionTitle = null)
    {
        var relativePath = ResolveEmulatorSectionRelativePath(sectionKey, sectionTitle);
        var parts = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizePathPart)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length == 0
            ? ApplicationPaths.EmulatorsDirectory
            : Path.Combine([ApplicationPaths.EmulatorsDirectory, ..parts]);
    }

    public static string ResolveEmulatorSectionRelativePath(string? sectionKey, string? sectionTitle = null)
    {
        var normalizedKey = NormalizeSectionKey(sectionKey);
        if (normalizedKey.Contains('/', StringComparison.Ordinal) ||
            normalizedKey.Contains('\\', StringComparison.Ordinal))
        {
            return normalizedKey.Replace('\\', '/');
        }

        if (IsSaturnSection(normalizedKey, sectionTitle))
            return "Sega/Saturn";

        return normalizedKey;
    }

    public static string GetCanonicalSectionKey(string? sectionKey, string? sectionTitle = null)
        => ResolveEmulatorSectionRelativePath(sectionKey, sectionTitle);

    public static IEnumerable<string> GetSectionConfigurationKeyAliases(string? sectionKey, string? sectionTitle = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateSectionConfigurationKeyAliases(sectionKey, sectionTitle))
        {
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<string> EnumerateSectionConfigurationKeyAliases(string? sectionKey, string? sectionTitle = null)
    {
        yield return GetCanonicalSectionKey(sectionKey, sectionTitle);

        if (!string.IsNullOrWhiteSpace(sectionKey))
            yield return sectionKey.Trim();

        var fileName = Path.GetFileName(sectionKey)?.Trim();
        if (!string.IsNullOrWhiteSpace(fileName))
            yield return fileName;

        if (!IsSaturnSection(sectionKey, sectionTitle))
            yield break;

        yield return "Saturn.png";
        yield return "SATURN";
        yield return "Saturn";
    }

    public static bool TryGetSectionConfiguration<T>(
        IReadOnlyDictionary<string, T> configurations,
        string? sectionKey,
        string? sectionTitle,
        out T value)
    {
        foreach (var alias in GetSectionConfigurationKeyAliases(sectionKey, sectionTitle))
        {
            if (configurations.TryGetValue(alias, out value!))
                return true;
        }

        value = default!;
        return false;
    }

    private static bool IsSaturnSection(string? sectionKey, string? sectionTitle)
    {
        if (!string.IsNullOrWhiteSpace(sectionTitle))
        {
            var normalizedTitle = sectionTitle.Trim();
            if (normalizedTitle.Contains("saturn", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (string.IsNullOrWhiteSpace(sectionKey))
            return false;

        var normalizedKey = NormalizeSectionKey(sectionKey);
        if (normalizedKey.Equals("SATURN", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.Equals("Saturn", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("/Saturn", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("\\Saturn", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedKey.Contains("saturn", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSectionKey(string? sectionKey)
    {
        if (string.IsNullOrWhiteSpace(sectionKey))
            return "Unknown";

        var trimmed = sectionKey.Trim().Replace('\\', '/');
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
            return "Unknown";

        var extension = Path.GetExtension(fileName);
        if (IsImageExtension(extension))
            fileName = Path.GetFileNameWithoutExtension(fileName);

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var directory = Path.GetDirectoryName(trimmed)?.Replace('\\', '/');
            return string.IsNullOrWhiteSpace(directory)
                ? fileName
                : $"{directory}/{fileName}";
        }

        return fileName;
    }

    private static bool IsImageExtension(string? extension)
        => string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase);

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedChars = value
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();

        var sanitized = new string(sanitizedChars)
            .Replace(" ", "_")
            .Trim('_');

        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }
}
