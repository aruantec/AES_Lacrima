using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.RepresentationModel;

namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Saves YAML the way RPCS3 expects (no YamlDotNet stream document markers).
/// </summary>
internal static class Rpcs3YamlSaveHelper
{
    public static void SaveMapping(YamlMappingNode root, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);

        using var stringWriter = new StringWriter();
        var stream = new YamlStream(new YamlDocument(root));
        stream.Save(stringWriter, assignAnchors: false);

        File.WriteAllText(path, StripYamlStreamDocumentMarkers(stringWriter.ToString()));
    }

    /// <summary>
    /// YamlDotNet <see cref="YamlStream.Save"/> appends a document-end line (<c>...</c>); RPCS3 does not.
    /// </summary>
    internal static string StripYamlStreamDocumentMarkers(string yaml)
    {
        if (string.IsNullOrEmpty(yaml))
            return string.Empty;

        var normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var result = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed is "---" or "...")
                continue;

            result.Add(line);
        }

        var output = string.Join(Environment.NewLine, result).TrimEnd();
        return output.Length == 0 ? string.Empty : output + Environment.NewLine;
    }
}
