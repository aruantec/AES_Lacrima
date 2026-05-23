using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

/// <summary>
/// Flycast updater cache. <see cref="Payload"/> holds GitHub releases JSON or nightly S3 listing XML.
/// </summary>
internal sealed class FlycastReleaseCache
{
    public string? Repository { get; set; }

    public string? ETag { get; set; }

    public string? Payload { get; set; }

    public DateTimeOffset FetchedAtUtc { get; set; }
}

/// <summary>JSON source generation for <see cref="FlycastReleaseCache"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FlycastReleaseCache))]
internal partial class FlycastUpdateJsonContext : JsonSerializerContext;

/// <summary>Reads and writes <see cref="FlycastReleaseCache"/> files.</summary>
internal static class FlycastReleaseCachePersistence
{
    /// <inheritdoc cref="EmulatorReleaseCachePersistence.Load"/>
    public static FlycastReleaseCache? Load(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            var json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize(json, FlycastUpdateJsonContext.Default.FlycastReleaseCache);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc cref="EmulatorReleaseCachePersistence.Save"/>
    public static void Save(string cachePath, FlycastReleaseCache cache)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(cache, FlycastUpdateJsonContext.Default.FlycastReleaseCache);
            File.WriteAllText(cachePath, json);
        }
        catch
        {
        }
    }
}
