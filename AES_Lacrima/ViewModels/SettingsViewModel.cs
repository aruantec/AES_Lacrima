using AES_Core.DI;
using AES_Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels;

/// <summary>
/// Marker interface for the settings view model used by the view locator
/// and dependency injection container.
/// </summary>
public interface ISettingsViewModel : IViewModelBase;

/// <summary>
/// View model that exposes application settings used by the UI. Settings
/// are loaded and saved via the inherited settings infrastructure.
/// </summary>
[AutoRegister]
public partial class SettingsViewModel : ViewModelBase, ISettingsViewModel
{
    /// <summary>
    /// Backing field for the <c>FfmpegPath</c> observable property.
    /// The generated property contains the path to the ffmpeg executable
    /// used by the application when exporting or processing media.
    /// </summary>
    [ObservableProperty]
    private string? _ffmpegPath;

    /// <summary>
    /// Backing field for the <c>ScaleFactor</c> observable property.
    /// Controls UI scaling applied by the <c>ScalableDecorator</c>.
    /// </summary>
    [ObservableProperty]
    private double _scaleFactor = 1.0;

    /// <summary>
    /// Backing field for the <c>ParticleCount</c> observable property.
    /// Determines how many particles are rendered by the particle system.
    /// </summary>
    [ObservableProperty]
    private double _particleCount = 10;

    /// <summary>
    /// Backing field for the <c>ShowShaderToy</c> observable property.
    /// When true the ShaderToy view will be visible in the UI.
    /// </summary>
    [ObservableProperty]
    private bool _showShaderToy;

    public override void Prepare()
    {
        LoadSettings();
    }

    /// <summary>
    /// Called during initialization to prepare the view model; loads persisted
    /// settings into the observable properties.
    /// </summary>

    protected override void OnLoadSettings(JsonObject section)
    {
        ScaleFactor = ReadDoubleSetting(section, nameof(ScaleFactor), 1.0);
        ParticleCount = ReadDoubleSetting(section, nameof(ParticleCount), 10);
        ShowShaderToy = ReadBoolSetting(section, nameof(ShowShaderToy), false);
    }

    /// <summary>
    /// Reads settings from the provided JSON section and applies them to
    /// this view model's properties.
    /// </summary>
    /// <param name="section">The JSON object that contains persisted settings.</param>

    protected override void OnSaveSettings(JsonObject section)
    {
        WriteSetting(section, nameof(ScaleFactor), ScaleFactor);
        WriteSetting(section, nameof(ParticleCount), ParticleCount);
        WriteSetting(section, nameof(ShowShaderToy), ShowShaderToy);
    }
}