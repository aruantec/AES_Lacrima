using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AplPlaylistDocument))]
[JsonSerializable(typeof(AplPlaylistItem))]
[JsonSerializable(typeof(List<AplPlaylistItem>))]
internal partial class AplPlaylistJsonContext : JsonSerializerContext;
