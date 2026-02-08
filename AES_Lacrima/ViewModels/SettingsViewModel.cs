using AES_Core.DI;
using AES_Core.Interfaces;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels;

/// <summary>
/// Marker interface for the settings view model used by the view locator
/// and dependency injection container.
/// </summary>
public interface ISettingsViewModel : IViewModelBase;

/// <summary>
/// Represents a shader resource with its file path and display name.
/// </summary>
/// <param name="Path">The file system path to the shader resource. Cannot be null or empty.</param>
/// <param name="Name">The display name of the shader. Cannot be null or empty.</param>
public record ShaderItem(string Path, string Name);

/// <summary>
/// View model that exposes application settings used by the UI. Settings
/// are loaded and saved via the inherited settings infrastructure.
/// </summary>
[AutoRegister]
public partial class SettingsViewModel : ViewModelBase, ISettingsViewModel
{
    private string _shaderToysDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders", "shadertoys");
    private string _shadersDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders", "glsl");

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

    /// <summary>
    /// Gets or sets the collection of shader items used by the control.
    /// </summary>
    /// <remarks>The collection can be modified to add, remove, or update shader items at runtime. Changes to
    /// the collection will be observed and reflected in the control's behavior. The property may be null if no shader
    /// items are assigned.</remarks>
    [ObservableProperty]
    private AvaloniaList<ShaderItem>? _shaderToys = [];

    /// <summary>
    /// Gets or sets the currently selected Shadertoy shader item.
    /// </summary>
    [ObservableProperty]
    private ShaderItem? _selectedShadertoy;

    public override void Prepare()
    {
        // Load shader items from the local "shaders" directory
        ShaderToys = [.. GetLocalShaders(_shaderToysDirectory, "*.frag")];
        // Load settings
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
        // Set the selected shadertoy if it exists
        if (ReadStringSetting(section, nameof(SelectedShadertoy)) is { } selectedshadertoy)
        {
            SelectedShadertoy = ShaderToys?.FirstOrDefault(s => s.Name == selectedshadertoy);
        }
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
        WriteSetting(section, nameof(SelectedShadertoy), SelectedShadertoy?.Name ?? "");
    }

    /// <summary>
    /// Retrieves a list of local shader files from the specified directory that match the given search pattern.
    /// </summary>
    /// <param name="directory">The path to the directory to search for shader files. Must be a valid directory path.</param>
    /// <param name="pattern">The search pattern used to filter files within the directory, such as "*.shader". Supports standard wildcard
    /// characters.</param>
    /// <returns>A list of ShaderItem objects representing the shader files found in the directory. Returns an empty list if the
    /// directory does not exist or no files match the pattern.</returns>
    private List<ShaderItem> GetLocalShaders(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];

        return [.. Directory.EnumerateFiles(directory, pattern).Select(file => new ShaderItem(file, Path.GetFileNameWithoutExtension(file)))];
    }
}