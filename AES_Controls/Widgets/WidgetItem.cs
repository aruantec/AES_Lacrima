using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace AES_Controls.Widgets;

public partial class WidgetItem : ObservableObject
{
    private bool _isVisible = true;
    private bool _followWindowSize;
    
    private string? _widgetViewName;
    [JsonPropertyName("WidgetViewName")]
    public string? WidgetViewName { get => _widgetViewName; set => SetProperty(ref _widgetViewName, value); }
    
    // Position and Size
    private double _left;
    [JsonPropertyName("Left")]
    public double Left { get => _left; set => SetProperty(ref _left, value); }

    private double _top;
    [JsonPropertyName("Top")]
    public double Top { get => _top; set => SetProperty(ref _top, value); }

    private double _width;
    [JsonPropertyName("Width")]
    public double Width { get => _width; set => SetProperty(ref _width, value); }

    private double _height;
    [JsonPropertyName("Height")]
    public double Height { get => _height; set => SetProperty(ref _height, value); }

    private int _zIndex;
    [JsonPropertyName("ZIndex")]
    public int ZIndex { get => _zIndex; set => SetProperty(ref _zIndex, value); }
    
    [XmlIgnore]
    [JsonIgnore]
    public Action? OnSaveSettingsAction { get; set; }
    
    [XmlIgnore]
    [JsonIgnore]
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
    
    [XmlIgnore]
    [JsonIgnore]
    public bool FollowWindowSize
    {
        get => _followWindowSize;
        set => SetProperty(ref _followWindowSize, value);
    }
    
    private bool _isPinned = true;
    [JsonPropertyName("IsPinned")]
    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }
    
    [RelayCommand]
    private void SaveWidgetSettings()
    {
        OnSaveSettingsAction?.Invoke();
    }
}