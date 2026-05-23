using System;
using System.IO;
using System.Text.Json;

namespace AES_Lacrima.Serialization;

/// <summary>
/// Reads and writes <see cref="EmulatorReleaseCache"/> files for emulator updater services.
/// </summary>
internal static class EmulatorReleaseCachePersistence
{
    /// <summary>Loads a cache file, or <see langword="null" /> if missing or invalid.</summary>
    public static EmulatorReleaseCache? Load(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            var json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize(json, EmulatorUpdateJsonContext.Default.EmulatorReleaseCache);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persists a cache file, creating parent directories when needed.</summary>
    public static void Save(string cachePath, EmulatorReleaseCache cache)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(cache, EmulatorUpdateJsonContext.Default.EmulatorReleaseCache);
            File.WriteAllText(cachePath, json);
        }
        catch
        {
        }
    }
}
