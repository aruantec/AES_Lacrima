using AES_Controls.Player.Models;
using Avalonia.Collections;

namespace AES_Controls.Player.Interfaces;

public interface IMediaInterface
{
    /// <summary>
    /// Enable waveform data
    /// </summary>
    bool EnableWaveform { get; set; }
    /// <summary>
    /// Waveform resolution buckets
    /// </summary>
    int WaveformBuckets { get; set; }
    /// <summary>
    /// Whether waveform is currently loading
    /// </summary>
    bool IsLoadingWaveform { get; set; }
    /// <summary>
    /// Enable spectrum data
    /// </summary>
    bool EnableSpectrum { get; set; }
    
    /// <summary>
    /// Volume level from 0.0 (mute) to 100.0 (maximum)
    /// </summary>
    double Volume { get; set; }
    /// <summary>
    /// Position in seconds
    /// </summary>
    double Position { get; set; }
    /// <summary>
    /// Duration in seconds
    /// </summary>
    double Duration { get; set; }
    /// <summary>
    /// Whether media is currently playing
    /// </summary>
    bool IsPlaying { get; set; }
    /// <summary>
    /// Loop playback
    /// </summary>
    bool Loop { get; set; }
    /// <summary>
    /// Get the buffering status
    /// </summary>
    bool IsBuffering { get; }
    /// <summary>
    /// Whether media is currently loading
    /// </summary>
    bool IsLoadingMedia { get; set; }
    
    /// <summary>
    /// Max cache size in megabytes
    /// </summary>
    int CacheSize { get; set; }

    /// <summary>
    /// Waveform data

    /// </summary>
    AvaloniaList<float> Waveform { get; set; }
    /// <summary>
    /// Spectrum data
    /// </summary>
    AvaloniaList<double> Spectrum { get; set; }
    
    /// <summary>
    /// Playing event
    /// </summary>
    event EventHandler? Playing;
    /// <summary>
    /// Paused event
    /// </summary>
    event EventHandler? Paused;
    /// <summary>
    /// Stopped event
    /// </summary>
    event EventHandler? Stopped;
    /// <summary>
    /// Time changed event
    /// </summary>
    event EventHandler<long>? TimeChanged;
    /// <summary>
    /// End reached event
    /// </summary>
    event EventHandler? EndReached;
    
    /// <summary>
    /// Play media
    /// </summary>
    void Play();
    /// <summary>
    /// Pause media
    /// </summary>
    void Pause();
    /// <summary>
    /// Stop media
    /// </summary>
    void Stop();

    /// <summary>
    /// Stop current playback and prepare for a new load (keeps IsLoadingMedia flag true)
    /// </summary>
    void PrepareLoad();

    /// <summary>
    /// Plays the specified media file asynchronously.
    /// </summary>
    /// <remarks>Ensure that the media item is properly initialized and accessible before calling this method.
    /// This method may throw exceptions if the media file is not found or cannot be played.</remarks>
    /// <param name="mediaItem">The media item to be played. This parameter cannot be null and must represent a valid media file.</param>
    /// <returns>A task that represents the asynchronous operation of playing the media file.</returns>
    Task PlayFile(MediaItem mediaItem);
    /// <summary>
    /// Play media from bytes
    /// </summary>
    /// <param name="b">Bytes to use</param>
    /// <param name="m">Mime type</param>
    /// <returns>Task result</returns>
    Task PlayBytes(byte[]? b, string m = "video/mp4");
    /// <summary>
    /// Set equalizer bands
    /// </summary>
    /// <param name="bands">Bands to set</param>
    void SetEqualizerBands(AvaloniaList<BandModel> bands);

    /// <summary>
    /// Sets the equalizer bands, but throttles the update to avoid blocking the player.
    /// </summary>
    /// <param name="bands"></param>
    void SetEqualizerBandsThrottled(AvaloniaList<BandModel> bands);
    
    /// <summary>
    /// Set playback position in seconds
    /// </summary>
    /// <param name="position"></param>
    void SetPosition(double position);
    /// <summary>
    /// Ensure render context is created
    /// </summary>
    void EnsureRenderContextCreated();

    /// <summary>
    /// Suspends playback to allow editing and returns the current playback position and state.
    /// </summary>
    /// <remarks>Use this method before performing editing operations that require playback to be paused.
    /// After editing is complete, resume playback as needed based on the returned state.</remarks>
    /// <returns>A task that represents the asynchronous operation. The result contains a tuple with the current playback
    /// position, in seconds, and a value indicating whether playback was active before suspension.</returns>
    Task<(double Position, bool WasPlaying)> SuspendForEditingAsync();
    /// <summary>
    /// Resumes playback of the specified media file after editing, restoring the playback position and state.
    /// </summary>
    /// <param name="path">The path to the media file to resume playback for. Cannot be null or empty.</param>
    /// <param name="position">The position, in seconds, at which to resume playback. Must be greater than or equal to 0.</param>
    /// <param name="wasPlaying">A value indicating whether playback was active before editing. Set to <see langword="true"/> to resume playback;
    /// otherwise, <see langword="false"/> to pause.</param>
    /// <returns>A task that represents the asynchronous resume operation.</returns>
    Task ResumeAfterEditingAsync(string path, double position, bool wasPlaying);
}