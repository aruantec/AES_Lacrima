using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

/// <summary>
/// JSON source generation for hash-keyed ROM title databases under <c>Database/*.json</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<RomHashTitleEntry>))]
[JsonSerializable(typeof(RomHashTitleEntry))]
internal partial class RomHashTitleDatabaseJsonContext : JsonSerializerContext;

/// <summary>Hash-keyed ROM title row (No-Intro / Redump derived).</summary>
internal sealed class RomHashTitleEntry
{
    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("crc")]
    public string? Crc { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Disc/product serial (for example PSP <c>ULUS-10336</c>).</summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }
}
