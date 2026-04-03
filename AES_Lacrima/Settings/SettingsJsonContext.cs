using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Settings
{
    [JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    // Basic UI types
    [JsonSerializable(typeof(HorizontalAlignment))]
    [JsonSerializable(typeof(VerticalAlignment))]
    // Media model types used in persisted settings
    [JsonSerializable(typeof(MediaItem))]
    [JsonSerializable(typeof(FolderMediaItem))]
    // Equalizer band model
    [JsonSerializable(typeof(BandModel))]
    // Dynamic settings tree root
    [JsonSerializable(typeof(JsonObject))]
    // Collections used when saving/loading view-model lists
    [JsonSerializable(typeof(List<MediaItem>))]
    [JsonSerializable(typeof(List<FolderMediaItem>))]
    [JsonSerializable(typeof(List<BandModel>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, List<MediaItem>>))]
    public partial class SettingsJsonContext : JsonSerializerContext;
}
