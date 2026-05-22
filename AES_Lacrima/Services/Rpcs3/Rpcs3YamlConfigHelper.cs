using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace AES_Lacrima.Services.Rpcs3;

internal static class Rpcs3YamlConfigHelper
{
    public static Dictionary<string, string?> ReadFlatValues(string? path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return values;

        try
        {
            using var reader = new StreamReader(path);
            var yaml = new YamlStream();
            yaml.Load(reader);

            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                return values;

            ReadMapping(values, root, parentSection: null, currentSection: null);
        }
        catch
        {
        }

        return values;
    }

    public static void ApplyFlatValues(string? sourcePath, IReadOnlyDictionary<string, string?> values, string outputPath)
    {
        var root = LoadRootMapping(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);

        foreach (var field in Rpcs3ConfigSchema.AllFields)
        {
            var compositeKey = Rpcs3ConfigSchema.ComposeKey(field.Section, field.Key, field.ParentSection);
            if (!values.TryGetValue(compositeKey, out var rawValue))
                continue;

            var sectionNode = GetOrCreateSection(root, field.Section, field.ParentSection);
            sectionNode.Children[CreateScalar(field.Key)] = CreateScalarNode(rawValue, field);
        }

        SaveRootMapping(root, outputPath);
    }

    private static void ReadMapping(
        IDictionary<string, string?> values,
        YamlMappingNode mapping,
        string? parentSection,
        string? currentSection)
    {
        foreach (var entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode keyScalar)
                continue;

            var key = keyScalar.Value ?? string.Empty;

            if (entry.Value is YamlMappingNode nested)
            {
                if (currentSection == null)
                    ReadMapping(values, nested, parentSection: null, currentSection: key);
                else
                    ReadMapping(values, nested, parentSection: currentSection, currentSection: key);
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentSection))
                continue;

            values[Rpcs3ConfigSchema.ComposeKey(currentSection, key, parentSection)] = ScalarToString(entry.Value);
        }
    }

    private static YamlMappingNode LoadRootMapping(string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            try
            {
                using var reader = new StreamReader(sourcePath);
                var yaml = new YamlStream();
                yaml.Load(reader);
                if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode existing)
                    return existing;
            }
            catch
            {
            }
        }

        return new YamlMappingNode();
    }

    private static void SaveRootMapping(YamlMappingNode root, string outputPath)
    {
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StreamWriter(outputPath);
        stream.Save(writer, assignAnchors: false);
    }

    private static YamlMappingNode GetOrCreateSection(YamlMappingNode root, string section, string? parentSection)
    {
        if (string.IsNullOrWhiteSpace(parentSection))
        {
            var sectionScalar = CreateScalar(section);
            if (root.Children.TryGetValue(sectionScalar, out var existing) && existing is YamlMappingNode mapping)
                return mapping;

            var created = new YamlMappingNode();
            root.Children[sectionScalar] = created;
            return created;
        }

        var parentScalar = CreateScalar(parentSection);
        if (!root.Children.TryGetValue(parentScalar, out var parentNode) || parentNode is not YamlMappingNode parentMapping)
        {
            parentMapping = new YamlMappingNode();
            root.Children[parentScalar] = parentMapping;
        }

        var nestedScalar = CreateScalar(section);
        if (parentMapping.Children.TryGetValue(nestedScalar, out var nestedExisting) && nestedExisting is YamlMappingNode nestedMapping)
            return nestedMapping;

        var nestedCreated = new YamlMappingNode();
        parentMapping.Children[nestedScalar] = nestedCreated;
        return nestedCreated;
    }

    private static YamlScalarNode CreateScalar(string value) => new(value);

    private static YamlNode CreateScalarNode(string? rawValue, Rpcs3ConfigFieldDefinition field)
    {
        if (rawValue == null)
            return CreateScalar(string.Empty);

        return field.Kind switch
        {
            Rpcs3ConfigValueKind.Boolean => CreateScalar(bool.TryParse(rawValue, out var boolean) && boolean ? "true" : "false"),
            Rpcs3ConfigValueKind.Integer => CreateScalar(
                int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : "0"),
            Rpcs3ConfigValueKind.Float => CreateScalar(
                double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                    ? floating.ToString(CultureInfo.InvariantCulture)
                    : "0"),
            _ => CreateScalar(rawValue)
        };
    }

    private static string? ScalarToString(YamlNode? node) =>
        node switch
        {
            YamlScalarNode scalar => scalar.Value,
            _ => node?.ToString()
        };
}
