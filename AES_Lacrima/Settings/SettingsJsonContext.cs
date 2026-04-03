using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AES_Controls.Player.Models;

namespace AES_Lacrima.Settings
{
    [JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    // Dynamic settings tree root
    [JsonSerializable(typeof(JsonObject))]
    // Collection roots used by SettingsBase and persisted view-model snapshots
    [JsonSerializable(typeof(List<MediaItem>))]
    [JsonSerializable(typeof(List<FolderMediaItem>))]
    [JsonSerializable(typeof(List<BandModel>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, List<MediaItem>>))]
    public partial class SettingsJsonContext : JsonSerializerContext;
}
