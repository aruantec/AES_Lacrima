using AES_Core.DI;
using AES_Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels;

public interface ISettingsViewModel : IViewModelBase;

[AutoRegister]
public partial class SettingsViewModel : ViewModelBase, ISettingsViewModel
{
    [ObservableProperty]
    private string? _ffmpegPath;

    [ObservableProperty]
    private double _scaleFactor = 1.0;

    [ObservableProperty]
    private double _particleCount = 10;

    public override void Prepare()
    {
        LoadSettings();
    }

    protected override void OnLoadSettings(JsonObject section)
    {
        ScaleFactor = ReadDoubleSetting(section, nameof(ScaleFactor), 1.0);
        ParticleCount = ReadDoubleSetting(section, nameof(ParticleCount), 10);
    }

    protected override void OnSaveSettings(JsonObject section)
    {
        WriteSetting(section, nameof(ScaleFactor), ScaleFactor);
        WriteSetting(section, nameof(ParticleCount), ParticleCount);
    }
}