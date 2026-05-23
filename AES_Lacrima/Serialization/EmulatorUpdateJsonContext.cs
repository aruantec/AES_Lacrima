using System;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

/// <summary>
/// Shared release-list HTTP cache payload used by emulator update services.
/// Serialized via <see cref="EmulatorUpdateJsonContext"/> and <see cref="EmulatorReleaseCachePersistence"/>.
/// </summary>
internal sealed class EmulatorReleaseCache
{
    public string? Repository { get; set; }

    public string? ETag { get; set; }

    public string? ReleasesJson { get; set; }

    public DateTimeOffset FetchedAtUtc { get; set; }
}

/// <summary>JSON source generation for <see cref="EmulatorReleaseCache"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(EmulatorReleaseCache))]
internal partial class EmulatorUpdateJsonContext : JsonSerializerContext;
