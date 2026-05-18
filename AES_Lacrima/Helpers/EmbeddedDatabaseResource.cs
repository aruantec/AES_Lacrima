using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AES_Lacrima.Helpers;

internal static class EmbeddedDatabaseResource
{
    private static readonly Assembly Assembly = typeof(EmbeddedDatabaseResource).Assembly;

    public static string? ReadText(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var resourceName = Assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            using var stream = Assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                return reader.ReadToEnd();
            }
        }

        foreach (var path in GetFallbackPaths(fileName))
        {
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        return null;
    }

    private static string[] GetFallbackPaths(string fileName)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Database", fileName),
            Path.Combine(currentDirectory, "Database", fileName),
            Path.Combine(currentDirectory, "AES_Lacrima", "Database", fileName)
        };
    }
}
