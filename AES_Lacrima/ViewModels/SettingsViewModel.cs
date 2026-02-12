using AES_Core.DI;
using AES_Controls.Player.Models;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.ComponentModel;
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
public interface ISettingsViewModel;

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
    /// Gets or sets the collection of dummy media items used for the carousel preview.
    /// </summary>
    [ObservableProperty]
    private AvaloniaList<FolderMediaItem> _previewItems = [];

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
    /// Gets or sets a value indicating whether particle effects are displayed.
    /// </summary>
    [ObservableProperty]
    private bool _showParticles;

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

    /// <summary>
    /// Gets or sets the color used for the played portion of the waveform.
    /// </summary>
    [ObservableProperty]
    private Color _waveformPlayedColor = Color.Parse("RoyalBlue");

    /// <summary>
    /// Gets or sets the color used for the unplayed portion of the waveform.
    /// </summary>
    [ObservableProperty]
    private Color _waveformUnplayedColor = Color.Parse("DimGray");

    /// <summary>
    /// Gets or sets the resolution (number of samples) used for generating the waveform.
    /// </summary>
    [ObservableProperty]
    private int _waveformResolution = 4000;

    /// <summary>
    /// Gets or sets the horizontal gap between waveform bars.
    /// </summary>
    [ObservableProperty]
    private double _waveformBarGap = 0.0;

    /// <summary>
    /// Gets or sets the height of each waveform block.
    /// </summary>
    [ObservableProperty]
    private double _waveformBlockHeight = 0.0;

    /// <summary>
    /// Gets or sets the vertical gap between symmetric waveform halves.
    /// </summary>
    [ObservableProperty]
    private double _waveformVerticalGap = 4.0;

    /// <summary>
    /// Gets or sets the number of visual bars to display for the waveform.
    /// </summary>
    [ObservableProperty]
    private int _waveformVisualBars = 0;

    /// <summary>
    /// Gets or sets a value indicating whether to use a gradient for the waveform.
    /// </summary>
    [ObservableProperty]
    private bool _useWaveformGradient = false;

    /// <summary>
    /// Gets or sets a value indicating whether the waveform is displayed symmetrically.
    /// </summary>
    [ObservableProperty]
    private bool _isWaveformSymmetric = true;

    // Spectrum visualiser settings

    /// <summary>
    /// Gets or sets the height of the spectrum visualizer.
    /// </summary>
    [ObservableProperty]
    private double _spectrumHeight = 60.0;

    /// <summary>
    /// Gets or sets the width of each spectrum bar.
    /// </summary>
    [ObservableProperty]
    private double _barWidth = 4.0;

    /// <summary>
    /// Gets or sets the spacing between spectrum bars.
    /// </summary>
    [ObservableProperty]
    private double _barSpacing = 2.0;

    /// <summary>
    /// Gets or sets a value indicating whether spectrum bars are shown.
    /// </summary>
    [ObservableProperty]
    private bool _showSpectrumBars = true;

    /// <summary>
    /// Gets or sets the gradient brush used for the spectrum visualizer.
    /// </summary>
    [ObservableProperty]
    private LinearGradientBrush? _spectrumGradient;

    // Preset palette for gradient comboboxes
    private readonly AvaloniaList<Color> _presetSpectrumColors =
    [
        Color.Parse("#00CCFF"),
        Color.Parse("#3333FF"),
        Color.Parse("#CC00CC"),
        Color.Parse("#FF004D"),
        Color.Parse("#FFB300")
    ];

    /// <summary>
    /// Gets the collection of preset colors used for the spectrum visualization gradient.
    /// </summary>
    public AvaloniaList<Color> PresetSpectrumColors => _presetSpectrumColors;

    /// <summary>
    /// Gets or sets the first color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor0;

    /// <summary>
    /// Gets or sets the second color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor1;

    /// <summary>
    /// Gets or sets the third color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor2;

    /// <summary>
    /// Gets or sets the fourth color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor3;

    /// <summary>
    /// Gets or sets the fifth color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor4;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// Sets up property monitoring to update the visualization gradient dynamically.
    /// </summary>
    public SettingsViewModel()
    {
        // Initialize individual colors from the preset list
        _spectrumColor0 = _presetSpectrumColors[0];
        _spectrumColor1 = _presetSpectrumColors[1];
        _spectrumColor2 = _presetSpectrumColors[2];
        _spectrumColor3 = _presetSpectrumColors[3];
        _spectrumColor4 = _presetSpectrumColors[4];

        // Update gradient initially and when any spectrum color changes
        PropertyChanged += OnSettingsPropertyChanged;
        UpdateSpectrumGradient();
    }

    // Carousel settings (used by CompositionCarouselControl)
    [ObservableProperty]
    private double _carouselSpacing = 1.0;

    [ObservableProperty]
    private double _carouselScale = 1.0;

    [ObservableProperty]
    private double _carouselVerticalOffset = 0.0;

    [ObservableProperty]
    private double _carouselSliderVerticalOffset = 60.0;

    [ObservableProperty]
    private double _carouselSliderTrackHeight = 4.0;

    [ObservableProperty]
    private double _carouselSideTranslation = 320.0;

    [ObservableProperty]
    private double _carouselStackSpacing = 160.0;

    /// <summary>
    /// Handles property change notifications to synchronize individual color properties
    /// with the internal collection and refresh the visual gradient.
    /// </summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e == null || string.IsNullOrEmpty(e.PropertyName)) return;
        
        if (e.PropertyName == nameof(SpectrumColor0)) _presetSpectrumColors[0] = SpectrumColor0;
        else if (e.PropertyName == nameof(SpectrumColor1)) _presetSpectrumColors[1] = SpectrumColor1;
        else if (e.PropertyName == nameof(SpectrumColor2)) _presetSpectrumColors[2] = SpectrumColor2;
        else if (e.PropertyName == nameof(SpectrumColor3)) _presetSpectrumColors[3] = SpectrumColor3;
        else if (e.PropertyName == nameof(SpectrumColor4)) _presetSpectrumColors[4] = SpectrumColor4;
        else return;

        UpdateSpectrumGradient();
    }

    /// <summary>
    /// Rebuilds the <see cref="SpectrumGradient"/> based on the current collection of colors,
    /// distributing them evenly across the gradient stops.
    /// </summary>
    private void UpdateSpectrumGradient()
    {
        if (_presetSpectrumColors.Count == 0) return;

        var stops = new GradientStops();
        for (int i = 0; i < _presetSpectrumColors.Count; i++)
        {
            double offset = _presetSpectrumColors.Count > 1
                ? (double)i / (_presetSpectrumColors.Count - 1)
                : 0.0;

            stops.Add(new GradientStop(_presetSpectrumColors[i], offset));
        }

        SpectrumGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops = stops
        };
    }

    public override void Prepare()
    {
        // Load shader items from the local "shaders" directory
        ShaderToys = [.. GetLocalShaders(_shaderToysDirectory, "*.frag")];
        // Load settings
        LoadSettings();

        // Generate dummy preview items
        var defaultCover = GenerateDefaultFolderCover();
        var items = new List<FolderMediaItem>();
        for (int i = 1; i <= 10; i++)
        {
            items.Add(new FolderMediaItem
            {
                Title = $"Title {i}",
                Album = $"Album {i}",
                Artist = $"Artist {i}",
                CoverBitmap = defaultCover
            });
        }
        PreviewItems = [.. items];
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
        ShowParticles = ReadBoolSetting(section, nameof(ShowParticles), false);
        // Spectrum settings
        SpectrumHeight = ReadDoubleSetting(section, nameof(SpectrumHeight), SpectrumHeight);
        BarWidth = ReadDoubleSetting(section, nameof(BarWidth), BarWidth);
        BarSpacing = ReadDoubleSetting(section, nameof(BarSpacing), BarSpacing);
        ShowSpectrumBars = ReadBoolSetting(section, nameof(ShowSpectrumBars), ShowSpectrumBars);

        // Individual spectrum colors (persisted as strings)
        if (ReadStringSetting(section, nameof(SpectrumColor0)) is { } c0) SpectrumColor0 = Color.Parse(c0);
        if (ReadStringSetting(section, nameof(SpectrumColor1)) is { } c1) SpectrumColor1 = Color.Parse(c1);
        if (ReadStringSetting(section, nameof(SpectrumColor2)) is { } c2) SpectrumColor2 = Color.Parse(c2);
        if (ReadStringSetting(section, nameof(SpectrumColor3)) is { } c3) SpectrumColor3 = Color.Parse(c3);
        if (ReadStringSetting(section, nameof(SpectrumColor4)) is { } c4) SpectrumColor4 = Color.Parse(c4);
        WaveformPlayedColor = Color.Parse(ReadStringSetting(section, nameof(WaveformPlayedColor), "RoyalBlue")!);
        // Set the selected shadertoy if it exists
        if (ReadStringSetting(section, nameof(SelectedShadertoy)) is { } selectedshadertoy)
        {
            SelectedShadertoy = ShaderToys?.FirstOrDefault(s => s.Name == selectedshadertoy);
        }
        
        // Carousel settings
        CarouselSpacing = ReadDoubleSetting(section, nameof(CarouselSpacing), CarouselSpacing);
        CarouselScale = ReadDoubleSetting(section, nameof(CarouselScale), CarouselScale);
        CarouselVerticalOffset = ReadDoubleSetting(section, nameof(CarouselVerticalOffset), CarouselVerticalOffset);
        CarouselSliderVerticalOffset = ReadDoubleSetting(section, nameof(CarouselSliderVerticalOffset), CarouselSliderVerticalOffset);
        CarouselSliderTrackHeight = ReadDoubleSetting(section, nameof(CarouselSliderTrackHeight), CarouselSliderTrackHeight);
        CarouselSideTranslation = ReadDoubleSetting(section, nameof(CarouselSideTranslation), CarouselSideTranslation);
        CarouselStackSpacing = ReadDoubleSetting(section, nameof(CarouselStackSpacing), CarouselStackSpacing);
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
        WriteSetting(section, nameof(ShowParticles), ShowParticles);
        WriteSetting(section, nameof(WaveformPlayedColor), WaveformPlayedColor.ToString());
        WriteSetting(section, nameof(SelectedShadertoy), SelectedShadertoy?.Name ?? string.Empty);
        // Spectrum settings
        WriteSetting(section, nameof(SpectrumHeight), SpectrumHeight);
        WriteSetting(section, nameof(BarWidth), BarWidth);
        WriteSetting(section, nameof(BarSpacing), BarSpacing);
        WriteSetting(section, nameof(ShowSpectrumBars), ShowSpectrumBars);

        // Persist individual spectrum colors
        WriteSetting(section, nameof(SpectrumColor0), SpectrumColor0.ToString());
        WriteSetting(section, nameof(SpectrumColor1), SpectrumColor1.ToString());
        WriteSetting(section, nameof(SpectrumColor2), SpectrumColor2.ToString());
        WriteSetting(section, nameof(SpectrumColor3), SpectrumColor3.ToString());
        WriteSetting(section, nameof(SpectrumColor4), SpectrumColor4.ToString());

        // Persist Carousel settings
        WriteSetting(section, nameof(CarouselSpacing), CarouselSpacing);
        WriteSetting(section, nameof(CarouselScale), CarouselScale);
        WriteSetting(section, nameof(CarouselVerticalOffset), CarouselVerticalOffset);
        WriteSetting(section, nameof(CarouselSliderVerticalOffset), CarouselSliderVerticalOffset);
        WriteSetting(section, nameof(CarouselSliderTrackHeight), CarouselSliderTrackHeight);
        WriteSetting(section, nameof(CarouselSideTranslation), CarouselSideTranslation);
        WriteSetting(section, nameof(CarouselStackSpacing), CarouselStackSpacing);
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

    /// <summary>
    /// Generates a default cover bitmap with a musical note icon.
    /// </summary>
    private static Bitmap GenerateDefaultFolderCover()
    {
        var size = new PixelSize(400, 400);
        var renderTarget = new RenderTargetBitmap(size, new Vector(96, 96));

        using (var context = renderTarget.CreateDrawingContext())
        {
            // Background Radial Gradient
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.Parse("#E0E0E0"), 0),
                    new GradientStop(Color.Parse("#A0A0A0"), 1)
                ]
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, size.Width, size.Height));

            // Musical Note icon (Double eighth note)
            var noteBrush = new SolidColorBrush(Color.Parse("#2D2D2D"));
            var noteWidth = 200.0;
            var noteLeft = 110.0;
            var noteXOffset = (size.Width - noteWidth) / 2.0 - noteLeft;

            // Note heads (slightly tilted ellipses)
            context.DrawEllipse(noteBrush, null, new Rect(110 + noteXOffset, 260, 80, 60));
            context.DrawEllipse(noteBrush, null, new Rect(230 + noteXOffset, 240, 80, 60));

            // Stems
            context.DrawRectangle(noteBrush, null, new Rect(175 + noteXOffset, 110, 15, 170));
            context.DrawRectangle(noteBrush, null, new Rect(295 + noteXOffset, 90, 15, 170));

            // Beam (tilted rectangle using geometry)
            var stream = new StreamGeometry();
            using (var ctx = stream.Open())
            {
                ctx.BeginFigure(new Point(175 + noteXOffset, 110), true);
                ctx.LineTo(new Point(310 + noteXOffset, 90));
                ctx.LineTo(new Point(310 + noteXOffset, 140));
                ctx.LineTo(new Point(175 + noteXOffset, 160));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(noteBrush, null, stream);
        }

        return renderTarget;
    }
}