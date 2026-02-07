using Avalonia.Layout;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Settings
{
    [JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
    [JsonSerializable(typeof(HorizontalAlignment))]
    [JsonSerializable(typeof(VerticalAlignment))]
    public partial class SettingsJsonContext : JsonSerializerContext;
}