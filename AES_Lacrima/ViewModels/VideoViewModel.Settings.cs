using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels;

internal partial class VideoViewModel
{
    protected override void OnLoadSettings(JsonObject section)
    {
        base.OnLoadSettings(section);
        UseHighQualityStream = ReadBoolSetting(section, nameof(UseHighQualityStream));
    }

    protected override void OnSaveSettings(JsonObject section)
    {
        base.OnSaveSettings(section);
        WriteSetting(section, nameof(UseHighQualityStream), UseHighQualityStream);
    }
}
