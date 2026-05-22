using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Loads RPCS3 <c>patch.yml</c> the way yaml-cpp does: duplicate mapping keys are merged
/// instead of rejected (official patch files repeat <c>Anchors</c> and <c>PPU-*</c> blocks).
/// </summary>
internal static class Rpcs3PatchYamlLoader
{
    public static bool TryLoadRoot(string patchPath, out YamlMappingNode root, out string? errorMessage)
    {
        root = null!;
        errorMessage = null;

        try
        {
            var content = File.ReadAllText(patchPath);
            var loaded = LoadRoot(content);
            if (loaded == null)
            {
                errorMessage = "patch.yml did not contain a valid YAML document.";
                return false;
            }

            root = loaded;
            return true;
        }
        catch (YamlException ex)
        {
            errorMessage = FormatYamlError(ex);
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to read patch.yml: {ex.Message}";
            return false;
        }
    }

    private static string FormatYamlError(YamlException ex)
    {
        var message = ex.Message;
        if (message.StartsWith("Duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            return "Failed to read patch.yml: " + message +
                   ". The patch file uses duplicate YAML keys (as RPCS3 allows); try downloading patches again.";
        }

        return "Failed to read patch.yml: " + message;
    }

    private static YamlMappingNode? LoadRoot(string content)
    {
        using var reader = new StringReader(content);
        var parser = new Parser(reader);

        if (!parser.MoveNext())
            return null;

        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();

        if (parser.Current is not MappingStart)
            return null;

        var documentRoot = ParseMapping(parser);

        if (parser.Current is DocumentEnd)
            parser.Consume<DocumentEnd>();

        parser.Consume<StreamEnd>();
        return documentRoot;
    }

    private static YamlNode ParseNode(IParser parser)
    {
        return parser.Current switch
        {
            Scalar => ParseScalar(parser),
            SequenceStart => ParseSequence(parser),
            MappingStart => ParseMapping(parser),
            AnchorAlias => ParseAlias(parser),
            _ => throw new YamlException(
                parser.Current?.Start ?? default,
                parser.Current?.End ?? default,
                $"Unexpected YAML event '{parser.Current?.GetType().Name}' while parsing patch.yml.")
        };
    }

    private static YamlNode ParseAlias(IParser parser)
    {
        var alias = parser.Consume<AnchorAlias>();
        return new YamlScalarNode("*" + alias.Value);
    }

    private static YamlScalarNode ParseScalar(IParser parser)
    {
        var scalar = parser.Consume<Scalar>();
        var node = new YamlScalarNode(scalar.Value)
        {
            Style = scalar.Style
        };

        if (!scalar.Anchor.IsEmpty)
            node.Anchor = scalar.Anchor;

        return node;
    }

    private static YamlSequenceNode ParseSequence(IParser parser)
    {
        var start = parser.Consume<SequenceStart>();
        var sequence = new YamlSequenceNode
        {
            Style = start.Style
        };

        if (!start.Anchor.IsEmpty)
            sequence.Anchor = start.Anchor;

        while (parser.Current is not SequenceEnd)
            sequence.Add(ParseNode(parser));

        parser.Consume<SequenceEnd>();
        return sequence;
    }

    private static YamlMappingNode ParseMapping(IParser parser)
    {
        var start = parser.Consume<MappingStart>();
        var mergedChildren = new Dictionary<string, (YamlNode Key, YamlNode Value)>(StringComparer.Ordinal);

        while (parser.Current is not MappingEnd)
        {
            var keyNode = ParseNode(parser);
            var valueNode = ParseNode(parser);
            var keyText = GetMappingKey(keyNode);

            if (mergedChildren.TryGetValue(keyText, out var existing))
            {
                mergedChildren[keyText] = (existing.Key, MergeNodes(existing.Value, valueNode));
                continue;
            }

            mergedChildren[keyText] = (keyNode, valueNode);
        }

        parser.Consume<MappingEnd>();

        var mapping = new YamlMappingNode
        {
            Style = start.Style
        };

        if (!start.Anchor.IsEmpty)
            mapping.Anchor = start.Anchor;

        foreach (var entry in mergedChildren.Values)
            mapping.Add(entry.Key, entry.Value);

        return mapping;
    }

    private static YamlNode MergeNodes(YamlNode existing, YamlNode incoming)
    {
        if (existing is YamlMappingNode existingMap && incoming is YamlMappingNode incomingMap)
        {
            MergeMappings(existingMap, incomingMap);
            return existingMap;
        }

        if (existing is YamlSequenceNode existingSeq && incoming is YamlSequenceNode incomingSeq)
        {
            foreach (var child in incomingSeq.Children)
                existingSeq.Add(child);

            return existingSeq;
        }

        return incoming;
    }

    private static void MergeMappings(YamlMappingNode target, YamlMappingNode source)
    {
        foreach (var entry in source.Children)
        {
            var keyText = GetMappingKey(entry.Key);

            YamlNode? existingKey = null;
            foreach (var targetEntry in target.Children)
            {
                if (!string.Equals(GetMappingKey(targetEntry.Key), keyText, StringComparison.Ordinal))
                    continue;

                existingKey = targetEntry.Key;
                break;
            }

            if (existingKey != null && target.Children.TryGetValue(existingKey, out var existingValue))
            {
                target.Children[existingKey] = MergeNodes(existingValue, entry.Value);
                continue;
            }

            target.Add(entry.Key, entry.Value);
        }
    }

    private static string GetMappingKey(YamlNode keyNode) =>
        keyNode switch
        {
            YamlScalarNode scalar => scalar.Value ?? string.Empty,
            _ => keyNode.ToString()
        };
}
