using System.Text.Json;
using System.Text.Json.Nodes;
using AES_Lacrima.Settings;

namespace AES_Lacrima.Tests;

public sealed class SettingsBaseTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"aes-lacrima-settings-{Guid.NewGuid():N}");

    public SettingsBaseTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void SaveSettings_WritesConcreteSectionUnderViewModels()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var viewModel = new TestSettingsViewModel(settingsPath)
        {
            Name = "Library",
            Count = 3,
            IsEnabled = true,
            Ratio = 1.5
        };

        viewModel.SaveSettings();

        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var section = root["ViewModels"]![nameof(TestSettingsViewModel)]!.AsObject();

        Assert.Equal("Library", section["Name"]!.GetValue<string>());
        Assert.Equal(3, section["Count"]!.GetValue<int>());
        Assert.True(section["IsEnabled"]!.GetValue<bool>());
        Assert.Equal(1.5, section["Ratio"]!.GetValue<double>());
    }

    [Fact]
    public void LoadSettings_ReadsSavedValuesBackIntoInstance()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var root = new JsonObject
        {
            ["ViewModels"] = new JsonObject
            {
                [nameof(TestSettingsViewModel)] = new JsonObject
                {
                    ["Name"] = "Loaded",
                    ["Count"] = 42,
                    ["IsEnabled"] = true,
                    ["Ratio"] = 2.25
                }
            }
        };

        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var viewModel = new TestSettingsViewModel(settingsPath);
        viewModel.LoadSettings();

        Assert.Equal("Loaded", viewModel.Name);
        Assert.Equal(42, viewModel.Count);
        Assert.True(viewModel.IsEnabled);
        Assert.Equal(2.25, viewModel.Ratio);
    }

    [Fact]
    public void SaveSettings_RemovesEmptySectionAndWrapper()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var viewModel = new EmptySettingsViewModel(settingsPath);

        viewModel.SaveSettings();

        var root = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

        Assert.False(root.ContainsKey("ViewModels"));
    }

    [Fact]
    public void RemoveSavedSection_RemovesOnlyCurrentViewModelSection()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var root = new JsonObject
        {
            ["ViewModels"] = new JsonObject
            {
                [nameof(TestSettingsViewModel)] = new JsonObject { ["Name"] = "Remove me" },
                [nameof(OtherSettingsViewModel)] = new JsonObject { ["Flag"] = true }
            }
        };

        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var viewModel = new TestSettingsViewModel(settingsPath);
        viewModel.RemoveSavedSection();

        var updatedRoot = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var viewModels = updatedRoot["ViewModels"]!.AsObject();

        Assert.False(viewModels.ContainsKey(nameof(TestSettingsViewModel)));
        Assert.True(viewModels.ContainsKey(nameof(OtherSettingsViewModel)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed class TestSettingsViewModel(string settingsFilePath) : SettingsBase
    {
        protected override string SettingsFilePath { get; } = settingsFilePath;

        public string? Name { get; set; }
        public int Count { get; set; }
        public bool IsEnabled { get; set; }
        public double Ratio { get; set; }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, "Name", Name);
            WriteSetting(section, "Count", Count);
            WriteSetting(section, "IsEnabled", IsEnabled);
            WriteSetting(section, "Ratio", Ratio);
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            Name = ReadStringSetting(section, "Name");
            Count = ReadIntSetting(section, "Count");
            IsEnabled = ReadBoolSetting(section, "IsEnabled");
            Ratio = ReadDoubleSetting(section, "Ratio");
        }
    }

    private sealed class EmptySettingsViewModel(string settingsFilePath) : SettingsBase
    {
        protected override string SettingsFilePath { get; } = settingsFilePath;
    }

    private sealed class OtherSettingsViewModel(string settingsFilePath) : SettingsBase
    {
        protected override string SettingsFilePath { get; } = settingsFilePath;
    }
}
