using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<RomTitleEntry>))]
[JsonSerializable(typeof(RomTitleEntry))]
[JsonSerializable(typeof(List<Xbox360TitleEntry>))]
[JsonSerializable(typeof(Xbox360TitleEntry))]
internal partial class RomTitleDatabaseJsonContext : JsonSerializerContext;

internal sealed class RomTitleEntry
{
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

internal sealed class Xbox360TitleEntry
{
    [JsonPropertyName("titleid")]
    public string? TitleId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
