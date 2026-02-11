using AES_Controls.Player.Interfaces;
using AES_Controls.Player.Models;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Controls.Player;

/// <summary>
/// Defines the contract for an equalizer containing a list of frequency bands.
/// </summary>
public interface IEqualizer
{
    /// <summary>
    /// Gets the collection of equalizer bands.
    /// </summary>
    AvaloniaList<BandModel>? Bands { get; }
}

/// <summary>
/// Implementation of an equalizer that manages audio frequency bands and updates the player.
/// </summary>
public partial class Equalizer : ObservableObject, IEqualizer
{
    private readonly IMediaInterface? _player;

    /// <summary>
    /// The collection of equalizer bands.
    /// </summary>
    [ObservableProperty]
    AvaloniaList<BandModel>? _bands;

    /// <summary>
    /// Initializes a new instance of the <see cref="Equalizer"/> class.
    /// </summary>
    /// <param name="player">The media interface used to apply equalizer settings.</param>
    public Equalizer(IMediaInterface player)
    {
        _player = player;
        
        if (_bands == null)
        {
            // Initialize bands based on player type (simplified, adjust as needed)
            _bands = [];
            // 10-band equalizer
            for (uint i = 0; i < 10; i++)
            {
                _bands.Add(new BandModel
                {
                    Frequency = $"{GetFrequencyForBand(i)} Hz",
                    Gain = 0,
                    Index = i,
                    OnGainChanged = ApplyBandGain
                });
            }
        }
    }

    /// <summary>
    /// Re-initializes the <see cref="BandModel.OnGainChanged"/> callback for all existing bands.
    /// </summary>
    public void InitializeBands()
    {
        if (Bands == null) return;
        foreach (var bandModel in Bands)
        {
            bandModel.OnGainChanged = ApplyBandGain;
        }
    }

    /// <summary>
    /// Returns the center frequency for a given band index.
    /// </summary>
    /// <param name="index">The index of the frequency band.</param>
    /// <returns>The frequency in Hz.</returns>
    private double GetFrequencyForBand(uint index)
    {
        // Simplified frequencies (adjust to LibVLC standard)
        double[] freqs = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];
        return index < freqs.Length ? freqs[index] : 1000;
    }

    /// <summary>
    /// Callback triggered when a band's gain changes, applying the update to the player.
    /// </summary>
    /// <param name="band">The band that was modified.</param>
    private void ApplyBandGain(BandModel band)
    {
        if (_player == null || Bands == null) return;
        // Apply immediately on gain change
        _player?.SetEqualizerBandsThrottled(Bands);
    }
}