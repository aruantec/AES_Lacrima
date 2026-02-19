using Avalonia.Layout;
using AES_Controls.Player.Models;
using System.Collections.Generic;
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
    // Collections used when saving/loading view-model lists
    [JsonSerializable(typeof(List<MediaItem>))]
    [JsonSerializable(typeof(List<FolderMediaItem>))]
    [JsonSerializable(typeof(List<BandModel>))]
    public partial class SettingsJsonContext : JsonSerializerContext;
}