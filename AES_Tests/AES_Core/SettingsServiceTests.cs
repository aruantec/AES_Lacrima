using AES_Core.Interfaces;
using AES_Core.Services;

namespace AES_Core.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void SaveSettings_SavesEveryRegisteredSetting()
    {
        var service = new SettingsService();
        var first = new TestSetting();
        var second = new TestSetting();

        service.AddSettingsItem(first);
        service.AddSettingsItem(second);

        service.SaveSettings();

        Assert.Equal(1, first.SaveCalls);
        Assert.Equal(1, second.SaveCalls);
    }

    [Fact]
    public void SaveSettings_DoesNothingWhenNoSettingsAreRegistered()
    {
        var service = new SettingsService();

        var exception = Record.Exception(service.SaveSettings);

        Assert.Null(exception);
    }

    private sealed class TestSetting : ISetting
    {
        public int SaveCalls { get; private set; }

        public void SaveSettings() => SaveCalls++;

        public void LoadSettings()
        {
        }

        public void RemoveSavedSection()
        {
        }
    }
}
