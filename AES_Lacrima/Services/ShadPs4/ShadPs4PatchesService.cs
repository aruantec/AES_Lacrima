using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using AES_Lacrima.Serialization;

namespace AES_Lacrima.Services.ShadPs4;

public sealed record ShadPs4PatchFileItem(string FilePath, string TitleId);

public static class ShadPs4PatchesService
{
    private static readonly string[] PreferredRepositoryOrder = ["shadPS4", "shadps4", "GoldHEN"];

    public static ShadPs4PatchFileItem? FindPatchFile(string? emulatorDirectory, string titleId)
    {
        var patchesRoot = ShadPs4ContentDownloadService.GetPatchesRootDirectory(emulatorDirectory);
        if (string.IsNullOrWhiteSpace(patchesRoot) || !Directory.Exists(patchesRoot))
            return null;

        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repositoryName in PreferredRepositoryOrder)
        {
            searched.Add(repositoryName);
            var match = FindPatchInRepository(Path.Combine(patchesRoot, repositoryName), normalizedTitleId);
            if (match != null)
                return match;
        }

        foreach (var repositoryDirectory in Directory.EnumerateDirectories(patchesRoot))
        {
            var repositoryName = Path.GetFileName(repositoryDirectory);
            if (string.IsNullOrWhiteSpace(repositoryName) || searched.Contains(repositoryName))
                continue;

            var match = FindPatchInRepository(repositoryDirectory, normalizedTitleId);
            if (match != null)
                return match;
        }

        return null;
    }

    private static ShadPs4PatchFileItem? FindPatchInRepository(string repositoryDirectory, string normalizedTitleId)
    {
        if (!Directory.Exists(repositoryDirectory))
            return null;

        var filesJsonPath = Path.Combine(repositoryDirectory, "files.json");
        if (File.Exists(filesJsonPath))
        {
            try
            {
                var json = File.ReadAllText(filesJsonPath);
                var map = JsonSerializer.Deserialize(json, ShadPs4JsonContext.Default.DictionaryStringListString);
                if (map != null)
                {
                    foreach (var (fileName, titleIds) in map)
                    {
                        if (titleIds.Any(id =>
                                string.Equals(id.Trim().ToUpperInvariant(), normalizedTitleId, StringComparison.OrdinalIgnoreCase)))
                        {
                            var filePath = Path.Combine(repositoryDirectory, fileName);
                            if (File.Exists(filePath))
                                return new ShadPs4PatchFileItem(filePath, Path.GetFileNameWithoutExtension(filePath));
                        }
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var xmlPath in Directory.EnumerateFiles(repositoryDirectory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            if (XmlContainsTitleId(xmlPath, normalizedTitleId))
                return new ShadPs4PatchFileItem(xmlPath, Path.GetFileNameWithoutExtension(xmlPath));
        }

        return null;
    }

    private static bool XmlContainsTitleId(string xmlPath, string normalizedTitleId)
    {
        try
        {
            var document = XDocument.Load(xmlPath);
            var idElements = document.Descendants("TitleID").Elements("ID").ToList();
            if (idElements.Count == 0)
            {
                var legacyTitleId = document.Descendants("TitleID").FirstOrDefault()?.Value?.Trim();
                return !string.IsNullOrWhiteSpace(legacyTitleId) &&
                       string.Equals(legacyTitleId.ToUpperInvariant(), normalizedTitleId, StringComparison.OrdinalIgnoreCase);
            }

            return idElements.Any(id =>
                string.Equals(id.Value.Trim().ToUpperInvariant(), normalizedTitleId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
