using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace AES_Controls.Player.Models;

public class BandModel : ObservableObject
{
    private float _gain;
    
    [XmlIgnore]
    [JsonIgnore]
    public Action<BandModel>? OnGainChanged { get; set; }

    public string? Frequency { get; set; }
    
    /// <summary>
    /// The numeric frequency in Hz, used for efficient filtering.
    /// </summary>
    [XmlIgnore]
    [JsonIgnore]
    public double NumericFrequency { get; set; }

    public uint Index { get; set; }

    public float Gain
    {         
        get => _gain;
        set
        {
            SetProperty(ref _gain, value);

            // Notify that the gain has changed
            OnGainChanged?.Invoke(this);
        }
    }
}