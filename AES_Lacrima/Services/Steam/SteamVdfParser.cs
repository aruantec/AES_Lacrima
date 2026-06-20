using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AES_Lacrima.Services.Steam;

internal static class SteamVdfParser
{
    internal sealed class VdfNode
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static VdfNode? ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var index = 0;
            SkipWhitespace(text, ref index);

            // Steam VDF files begin with a quoted root key, e.g. "libraryfolders" { ... }
            if (index < text.Length && text[index] == '"')
            {
                if (!TryReadQuotedToken(text, ref index, out _))
                    return null;

                SkipWhitespace(text, ref index);
            }

            return ParseNode(text, ref index);
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<string> CollectStringValues(VdfNode? root, string keyName)
    {
        if (root == null)
            yield break;

        foreach (var value in WalkNodes(root))
        {
            if (value.Values.TryGetValue(keyName, out var path) && !string.IsNullOrWhiteSpace(path))
                yield return path.Trim();
        }
    }

    private static IEnumerable<VdfNode> WalkNodes(VdfNode node)
    {
        yield return node;

        foreach (var child in node.Children.Values)
        {
            foreach (var nested in WalkNodes(child))
                yield return nested;
        }
    }

    private static VdfNode? ParseNode(string text, ref int index)
    {
        SkipWhitespace(text, ref index);
        if (index >= text.Length)
            return null;

        if (text[index] != '{')
            return null;

        index++;
        var node = new VdfNode();

        while (index < text.Length)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
                break;

            if (text[index] == '}')
            {
                index++;
                break;
            }

            if (!TryReadQuotedToken(text, ref index, out var key))
                break;

            SkipWhitespace(text, ref index);
            if (index >= text.Length)
                break;

            if (text[index] == '{')
            {
                var child = ParseNode(text, ref index);
                if (child != null)
                    node.Children[key] = child;
                continue;
            }

            if (!TryReadQuotedToken(text, ref index, out var value))
                break;

            node.Values[key] = value;
        }

        return node;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool TryReadQuotedToken(string text, ref int index, out string token)
    {
        token = string.Empty;
        SkipWhitespace(text, ref index);
        if (index >= text.Length)
            return false;

        if (text[index] != '"')
            return false;

        index++;
        var builder = new StringBuilder();
        while (index < text.Length)
        {
            var ch = text[index++];
            if (ch == '"')
                break;

            if (ch == '\\' && index < text.Length)
                ch = text[index++];

            builder.Append(ch);
        }

        token = builder.ToString();
        return true;
    }
}
