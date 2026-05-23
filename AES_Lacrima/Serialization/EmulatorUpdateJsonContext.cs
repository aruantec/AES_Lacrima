using System;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

/// <summary>
/// Shared release-list HTTP cache payload used by emulator update services.
/// </summary>
internal sealed class EmulatorReleaseCache
{
    public string? Repository { get; set; }

    public string? ETag { get; set; }

    public string? ReleasesJson { get; set; }

    public DateTimeOffset FetchedAtUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(EmulatorReleaseCache))]
internal partial class EmulatorUpdateJsonContext : JsonSerializerContext;
