using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

/// <summary>
/// JSON source generation for embedded ROM title databases under <c>Database/*.json</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<RomTitleEntry>))]
[JsonSerializable(typeof(RomTitleEntry))]
[JsonSerializable(typeof(List<Xbox360TitleEntry>))]
[JsonSerializable(typeof(Xbox360TitleEntry))]
internal partial class RomTitleDatabaseJsonContext : JsonSerializerContext;

/// <summary>PlayStation and generic serial-keyed title row.</summary>
internal sealed class RomTitleEntry
{
    /// <summary>Serial, product code, or ROM set name used as lookup key.</summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

/// <summary>Xbox 360 title database row.</summary>
internal sealed class Xbox360TitleEntry
{
    [JsonPropertyName("titleid")]
    public string? TitleId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
