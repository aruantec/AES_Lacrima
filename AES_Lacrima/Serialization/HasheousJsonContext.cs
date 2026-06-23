using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

internal sealed class HasheousLookupResponse
{
    public string? Name { get; set; }

    public HasheousPlatformResponse? Platform { get; set; }

    public List<HasheousMetadataEntry>? Metadata { get; set; }
}

internal sealed class HasheousPlatformResponse
{
    public string? Name { get; set; }
}

internal sealed class HasheousMetadataEntry
{
    [JsonPropertyName("objectType")]
    public string? ObjectType { get; set; }

    public string? Source { get; set; }

    public string? Status { get; set; }

    public string? Id { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HasheousLookupResponse))]
[JsonSerializable(typeof(List<Dictionary<string, string>>))]
internal partial class HasheousJsonContext : JsonSerializerContext;
