using AES_Controls.Helpers;
using AES_Controls.Player.Interfaces;
using AES_Controls.Player.Models;
using AES_Controls.Players;
using Avalonia.Collections;
using LibMPVSharp;
using LibMPVSharp.Extensions;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using log4net;

namespace AES_Controls.Player;

/// <summary>
/// High-level audio player wrapper around libmpv. Exposes playback control,
/// observable properties for UI binding (position, duration, volume, etc.),
/// waveform and spectrum data, and helper methods for loading and managing media.
/// </summary>
public sealed class AudioPlayer : MPVMediaPlayer, IMediaInterface, INotifyPropertyChanged, IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AudioPlayer));

    private string? _loadedFile, _waveformLoadedFile;
    private readonly SynchronizationContext? _syncContext;
    private volatile bool _isLoadingMedia, _isSeeking;
    private CancellationTokenSource? _seekRestartCts;
    private volatile bool _isInternalChange; // Guard to prevent playlist skipping
    private volatile bool _disposed; // Flag to skip native calls during shutdown

    /// <summary>
    /// Holds a reference to the current media item being processed or played.
    /// </summary>
    /// <remarks>This field is null if no media item is currently selected or active.</remarks>
    private MediaItem? _currentMediaItem;

    private readonly FfMpegSpectrumAnalyzer? _spectrumAnalyzer;
    private readonly FFmpegManager? _ffmpegManager;
    private readonly MpvLibraryManager? _mpvLibraryManager;
    private CancellationTokenSource? _waveformCts;
    private readonly TaskCompletionSource _initTcs = new();
    private double _volume = 70;

    // Track the active ffmpeg process to prevent resource exhaustion on macOS
    private Process? _activeFfmpegProcess;

    // THROTTLING: Keep track of the last time the spectrum was updated
    private long _lastSpectrumUpdateTicks;
    private const long SpectrumThrottleIntervalTicks = 83333; // ~8.3ms for 120 FPS

    /// <summary>
    /// True when a programmatic seek operation is in progress.
    /// </summary>
    public bool IsSeeking => _isSeeking;

    /// <summary>
    /// Gets the media item that is currently selected or being processed, or null if no media item is selected.
    /// </summary>
    /// <remarks>Use this property to access the media item that is currently active in the player. If no
    /// media item is selected, the property returns null. This property is typically used to retrieve information about
    /// the current playback item or to perform actions based on the selected media.</remarks>
    public MediaItem? CurrentMediaItem => _currentMediaItem;

    /// <summary>
    /// Occurs when a property value changes.
    /// </summary>
    /// <remarks>This event is typically used in data binding scenarios to notify subscribers that a property
    /// has changed, allowing them to update the UI or perform other actions in response to the change.</remarks>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the PropertyChanged event for the specified property, notifying subscribers that the property's value has
    /// changed.
    /// </summary>
    /// <remarks>This method uses the current synchronization context to ensure that the PropertyChanged event
    /// is raised on the appropriate thread, which is important for UI-bound objects to avoid cross-thread operation
    /// exceptions.</remarks>
    /// <param name="propertyName">The name of the property that changed. This value is used to identify the property in the PropertyChanged event.</param>
    private void OnPropertyChanged(string propertyName) =>
        _syncContext?.Post(_ => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)), null);

    /// <summary>
    /// Per-sample waveform values used by the UI waveform control.
    /// </summary>
    public AvaloniaList<float> Waveform { get; set; }

    /// <summary>
    /// Frequency spectrum values used by the UI spectrum visualiser.
    /// </summary>
    public AvaloniaList<double> Spectrum { get; set; }

    /// <summary>
    /// When true the waveform generation is enabled.
    /// </summary>
    public bool EnableWaveform { get; set; } = true;

    /// <summary>
    /// When true the spectrum analyzer is enabled.
    /// </summary>
    public bool EnableSpectrum { get; set; } = true;

    /// <summary>
    /// Number of buckets used when generating the waveform.
    /// </summary>
    public int WaveformBuckets { get; set; } = 4000;

    /// <summary>
    /// Maximum demuxer cache size in megabytes exposed to the player.
    /// </summary>
    public int CacheSize
    {
        get;
        set
        {
            field = value;
            if (_initTcs.Task.IsCompleted && !_disposed)
                SetProperty("demuxer-max-bytes", $"{value}M");
        }
    } = 32;

    /// <summary>
    /// Playback volume (0..100).
    /// </summary>
    public double Volume
    {
        get => (_initTcs.Task.IsCompleted && !_disposed) ? GetPropertyDouble("volume") : _volume;
        set
        {
            _volume = value;
            if (_initTcs.Task.IsCompleted && !_disposed)
            {
                SetProperty("volume", value);
            }
            OnPropertyChanged(nameof(Volume));
        }
    }

    /// <summary>
    /// Current playback position in seconds.
    /// </summary>
    public double Position
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(Position));
        }
    }

    /// <summary>
    /// Total duration of the currently loaded media in seconds.
    /// </summary>
    public double Duration
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(Duration));
        }
    }

    /// <summary>
    /// The current repeat mode of the player.
    /// </summary>
    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        set
        {
            _repeatMode = value;
            if (_initTcs.Task.IsCompleted && !_disposed)
                SetProperty("loop-file", value == RepeatMode.One ? "yes" : "no");

            OnPropertyChanged(nameof(RepeatMode));
            OnPropertyChanged(nameof(Loop));
            OnPropertyChanged(nameof(IsRepeatOne));
        }
    }
    private RepeatMode _repeatMode = RepeatMode.Off;

    /// <summary>
    /// When true the player will loop the current file or playlist.
    /// Setting this to true will set the RepeatMode to All.
    /// </summary>
    public bool Loop
    {
        get => RepeatMode != RepeatMode.Off;
        set => RepeatMode = value ? RepeatMode.All : RepeatMode.Off;
    }

    /// <summary>
    /// Returns true if the current repeat mode is set to Repeat one.
    /// </summary>
    public bool IsRepeatOne => RepeatMode == RepeatMode.One;

    /// <summary>
    /// Indicates whether the player is currently buffering.
    /// </summary>
    public bool IsBuffering
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsBuffering));
        }
    }

    /// <summary>
    /// True when waveform generation is in progress.
    /// </summary>
    public bool IsLoadingWaveform
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(IsLoadingWaveform));
        }
    }

    /// <summary>
    /// True while media is loading.
    /// </summary>
    public bool IsLoadingMedia
    {
        get => _isLoadingMedia;
        set
        {
            _isLoadingMedia = value;
            OnPropertyChanged(nameof(IsLoadingMedia));
        }
    }

    /// <summary>
    /// Raised when playback starts.
    /// </summary>
    public event EventHandler? Playing;

    /// <summary>
    /// Raised when playback is paused.
    /// </summary>
    public event EventHandler? Paused;

    /// <summary>
    /// Raised when playback is stopped.
    /// </summary>
    public event EventHandler? Stopped;

    /// <summary>
    /// Raised when the currently playing file reaches its end.
    /// </summary>
    public event EventHandler? EndReached;

    /// <summary>
    /// Raised periodically with the current playback time (in milliseconds).
    /// </summary>
    public event EventHandler<long>? TimeChanged;

    /// <summary>
    /// Creates a new <see cref="AudioPlayer"/> instance and configures
    /// default mpv properties and event handlers.
    /// </summary>
    /// <param name="ffmpegManager">Manager to report activity status for external processes.</param>
    /// <param name="mpvLibraryManager">Manager for libmpv installation signals.</param>
    public AudioPlayer(FFmpegManager? ffmpegManager = null, MpvLibraryManager? mpvLibraryManager = null)
    {
        _syncContext = SynchronizationContext.Current;
        _ffmpegManager = ffmpegManager;
        _mpvLibraryManager = mpvLibraryManager;

        if (_ffmpegManager != null)
        {
            _ffmpegManager.RequestFfmpegTermination += OnRequestFfmpegTermination;
            _ffmpegManager.InstallationCompleted += OnFfmpegInstallationCompleted;
        }

        if (_mpvLibraryManager != null)
        {
            _mpvLibraryManager.RequestMpvTermination += OnRequestMpvTermination;
            _mpvLibraryManager.InstallationCompleted += OnMpvInstallationCompleted;
        }

        Waveform = [];
        Spectrum = [];

        // Always create the analyzer, so it's ready if EnabledSpectrum is toggled
        _spectrumAnalyzer = new FfMpegSpectrumAnalyzer(Spectrum, this, _ffmpegManager);

        Playing += (s, e) => CheckAndStartFfmpegTasks();

        // Offload heavy MPV initialization to avoid blocking the UI thread
        _ = Task.Run(InitializeMpvAsync);
    }

    private async Task InitializeMpvAsync()
    {
        try
        {
            // Register properties for observation
            ObservableProperty(Properties.Duration, MpvFormat.MPV_FORMAT_DOUBLE);
            ObservableProperty(Properties.TimePos, MpvFormat.MPV_FORMAT_DOUBLE);
            ObservableProperty("paused-for-cache", MpvFormat.MPV_FORMAT_FLAG);
            ObservableProperty("eof-reached", MpvFormat.MPV_FORMAT_FLAG);

            // --- OS-SPECIFIC AUDIO INITIALIZATION ---
            if (OperatingSystem.IsMacOS())
            {
                SetProperty("ao", "coreaudio");
                try { await ExecuteCommandAsync(["set", "coreaudio-change-device", "no"]); }
                catch (Exception ex) { Log.Error("Failed to set coreaudio-change-device", ex); }
            }
            else if (OperatingSystem.IsWindows())
            {
                SetProperty("ao", "wasapi");
                SetProperty("audio-resample-filter-size", "16");
            }
            else
            {
                SetProperty("ao", "pulse,alsa");
            }

            SetProperty("keep-open", "always");
            SetProperty("cache", "yes");
            SetProperty("demuxer-max-bytes", $"{CacheSize}M");
            SetProperty("demuxer-readahead-secs", "10");
            SetProperty("volume", _volume);
            SetProperty("loop-file", RepeatMode == RepeatMode.One ? "yes" : "no");
            SetProperty("demuxer-max-bytes", $"{CacheSize}M");

            MpvEvent += OnMpvEvent;

            // Mark initialization as complete ONLY after we've applied the final property sync.
            _initTcs.SetResult();

            OnPropertyChanged(nameof(Volume));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to initialize AudioPlayer asynchronously", ex);
            _initTcs.TrySetException(ex);
        }
    }

    private void OnRequestFfmpegTermination()
    {
        _spectrumAnalyzer?.Stop();
        _waveformCts?.Cancel();
        try { _activeFfmpegProcess?.Kill(true); } catch { }
    }

    private void OnRequestMpvTermination()
    {
        Dispose();
    }

    private void OnMpvInstallationCompleted(object? sender, MpvLibraryManager.InstallationCompletedEventArgs e)
    {
        if (e.Success && _initTcs.Task.IsFaulted)
        {
            // libmpv was previously missing but now its here, try to re-init
            // Note: This may not always work if the process has already cached the DllNotFound exception
            _ = Task.Run(InitializeMpvAsync);
        }
    }

    private void OnFfmpegInstallationCompleted(object? sender, FFmpegManager.InstallationCompletedEventArgs e)
    {
        if (e.Success)
        {
            _syncContext?.Post(_ => CheckAndStartFfmpegTasks(), null);
        }
    }

    private void CheckAndStartFfmpegTasks()
    {
        if (string.IsNullOrEmpty(_loadedFile)) return;

        // Start spectrum if playing and enabled
        if (IsPlaying && EnableSpectrum && !IsSeeking)
        {
            _spectrumAnalyzer?.SetStartPosition(Position);
            _spectrumAnalyzer?.Start();
        }

        // Start waveform if missing or not for the current file
        if (EnableWaveform && (_waveformLoadedFile != _loadedFile))
        {
            try { _waveformCts?.Cancel(); }
            catch (Exception ex) { Log.Warn("Failed to cancel waveform CTS", ex); }
            try { _waveformCts?.Dispose(); }
            catch (Exception ex) { Log.Warn("Failed to dispose waveform CTS", ex); }
            _waveformCts = new CancellationTokenSource();
            _ = GenerateWaveformAsync(_loadedFile, _waveformCts.Token, WaveformBuckets);
        }
    }

    /// <summary>
    /// Updates the UI spectrum values with throttling to avoid UI overload.
    /// </summary>
    /// <param name="newData">Array of spectrum magnitudes.</param>
    public void UpdateSpectrumThrottled(double[] newData)
    {
        long currentTicks = DateTime.UtcNow.Ticks;
        if (currentTicks - _lastSpectrumUpdateTicks < SpectrumThrottleIntervalTicks)
            return;

        _lastSpectrumUpdateTicks = currentTicks;

        // Snapshot the data to avoid race conditions between the analysis thread and UI thread
        var snapshot = new double[newData.Length];
        Array.Copy(newData, snapshot, newData.Length);

        _syncContext?.Post(_ =>
        {
            if (Spectrum.Count != snapshot.Length)
            {
                Spectrum.Clear();
                Spectrum.AddRange(snapshot);
            }
            else
            {
                for (int i = 0; i < snapshot.Length; i++)
                    Spectrum[i] = snapshot[i];
            }
        }, null);
    }

    /// <summary>
    /// Prepare the player to load a new file. Stops current playback and
    /// sets internal flags used to suppress transient events during the
    /// load process.
    /// </summary>
    public void PrepareLoad()
    {
        if (_disposed) return;
        _isInternalChange = true;
        IsLoadingMedia = true;
        InternalStop();
    }

    private void OnMpvEvent(object? sender, MpvEvent mpvEvent)
    {
        if (mpvEvent.event_id == MpvEventId.MPV_EVENT_END_FILE)
        {
            // If it's an error from the demuxer/ffmpeg, we must clear the loading state.
            // However, we ignore 'STOP' events during track transitions (_isInternalChange is true)
            // to prevent the spinner from disappearing while waiting for the next file.
            var endData = mpvEvent.ReadData<MpvEventEndFile>();
            if (endData.error < 0)
            {
                IsLoadingMedia = false;
                _isInternalChange = false;
            }
            else if (!_isInternalChange)
            {
                IsLoadingMedia = false;
            }
        }

        if (mpvEvent.event_id == MpvEventId.MPV_EVENT_PROPERTY_CHANGE)


        {
            var prop = mpvEvent.ReadData<MpvEventProperty>();

            if (prop.format == MpvFormat.MPV_FORMAT_NONE)
                return;

            if (prop.name == Properties.Duration)
            {
                if (prop.format == MpvFormat.MPV_FORMAT_DOUBLE)
                {
                    Duration = prop.ReadDoubleValue();
                }
            }
            else if (prop.name == "paused-for-cache")
            {
                if (prop.format == MpvFormat.MPV_FORMAT_INT64)
                {
                    IsBuffering = prop.ReadLongValue() != 0;
                }
            }
            else if (prop.name == Properties.TimePos && !_isSeeking)
            {
                if (prop.format == MpvFormat.MPV_FORMAT_DOUBLE)
                {
                    Position = prop.ReadDoubleValue();

                    // Once we have progress on the NEW file, it's safe to listen to EOF again
                    if (_isInternalChange && Position > 0.1)
                    {
                        _isInternalChange = false;
                    }

                    TimeChanged?.Invoke(this, (long)(Position * 1000));
                    if (IsLoadingMedia && Position > 0)
                    {
                        IsLoadingMedia = false;
                        IsPlaying = true;
                    }
                }
            }
            else if (prop.name == "eof-reached")
            {
                // Fixed: ReadBoolValue() is the correct method for LibMPVSharp flags
                if (prop.format == MpvFormat.MPV_FORMAT_FLAG)
                {
                    bool isEof = prop.ReadBoolValue();

                    // SKIP GUARD: Only trigger EndReached if not an internal change, 
                    // not currently loading, and actually near the end of the duration.
                    // Also skip if RepeatMode is One, as mpv handles looping internally.
                    if (isEof && !_isInternalChange && !IsLoadingMedia && RepeatMode != RepeatMode.One)
                    {
                        if (Position > (Duration - 1.5) || Duration <= 0)
                        {
                            IsPlaying = false;
                            EndReached?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Loads and starts playback of the specified file path.
    /// </summary>
    public async Task PlayFile(MediaItem item, bool video = false)
    {
        await _initTcs.Task;
        if (_disposed) return;
        if (string.IsNullOrEmpty(item.FileName))
        {
            IsLoadingMedia = false;
            Stop();
            return;
        }

        // Check if the file is a URL and if OnlineUrls are available for selection
        var fileToPlay = item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase) && item.OnlineUrls != null && item.OnlineUrls.HasValue 
            ? video ? item.OnlineUrls.Value.Item1 : item.OnlineUrls.Value.Item2 
            : item.FileName; 

        //Set the current media item
        _currentMediaItem = item;
        // Prepare for loading the new file
        _isInternalChange = true;
        IsLoadingMedia = true;
        OnPropertyChanged(nameof(IsLoadingMedia));
        _loadedFile = fileToPlay;
        _waveformLoadedFile = null;

        // Ensure analyzer is fully stopped and path is updated before loading new file
        InternalStop();

        if (EnableSpectrum)
        {
            _spectrumAnalyzer?.SetPath(fileToPlay);
            _spectrumAnalyzer?.Start();
        }
        _waveformCts?.Cancel();

        _syncContext?.Post(_ => { Waveform.Clear(); Spectrum.Clear(); Position = 0; }, null);
        await ExecuteCommandAsync(["loadfile", fileToPlay]);
        Play();
    }


    private async Task GenerateWaveformAsync(string path, CancellationToken token, int buckets = 4000)
    {
        if (!EnableWaveform || string.IsNullOrEmpty(path) || buckets <= 0) return;
        _waveformLoadedFile = path;

        _ffmpegManager?.ReportActivity(true);
        try
        {
            if (_activeFfmpegProcess != null && !_activeFfmpegProcess.HasExited)
            {
                try { _activeFfmpegProcess.Kill(true); }
                catch (Exception ex) { Log.Warn("Failed to kill existing ffmpeg process", ex); }
                try { await _activeFfmpegProcess.WaitForExitAsync(token); }
                catch (Exception ex) { Log.Warn("Error waiting for existing ffmpeg process to exit", ex); }
                _activeFfmpegProcess.Dispose();
            }
        }
        catch (Exception ex) { Log.Warn("Error while attempting to stop active ffmpeg process", ex); }

        IsLoadingWaveform = true;

        try
        {
            if (token.IsCancellationRequested) return;

            bool isRemote = false;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                isRemote = !uri.IsFile && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "rtmp" || uri.Scheme == "rtsp");

            // Wait for a valid duration. 
            // For long files (1h+), mpv might take a split second to probe it.
            var duration = Duration;
            for (int i = 0; i < 50 && duration <= 0; i++)
            {
                await Task.Delay(100, token);
                duration = Duration;
            }

            if (duration <= 0) duration = 300; // Final fallback

            // Accuracy: For streams, we try to analyze more data (up to 10 mins)
            var maxSecondsToAnalyze = isRemote ? Math.Min(duration, 600) : duration;
                
            // PERFORMANCE: Use 16kHz for faster processing as visual waveform doesn't need 44.1kHz
            const int internalSampleRate = 16000;
            const int readBufferSize = 65536; // Larger buffer for better I/O performance

            var timeLimitArg = isRemote ? $"-t {maxSecondsToAnalyze.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
                
            // FIXED: Using a more reasonable probesize. 32 was too low for many files.
            var args = $"-probesize 32768 -analyzeduration 100000 -i \"{path}\" {timeLimitArg} -vn -sn -dn -ac 1 -ar {internalSampleRate} -f s16le -";

            // Get FFmpeg path
            var ffmpegPath = FFmpegLocator.FindFFmpegPath();

            var proc = Process.Start(new ProcessStartInfo(ffmpegPath!, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc == null) return;
            _activeFfmpegProcess = proc;

            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) { Log.Debug("Failed to set ffmpeg process priority", ex); }

            using var output = proc.StandardOutput.BaseStream;

            int samplesPerBucket = Math.Max(1, (int)Math.Ceiling(maxSecondsToAnalyze * internalSampleRate / buckets));

            var waveformData = new float[buckets];
            float globalMax = 0f;
            _syncContext?.Post(_ => Waveform.Clear(), null);

            var buffer = new byte[readBufferSize];
            int bytesRead, currentBucket = 0, samplesInBucket = 0, batchCounter = 0;
            float bucketPeak = 0f;
            int currentBatchSize = 16; // Start with smaller batch for immediate feedback

            while ((bytesRead = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
            {
                if (token.IsCancellationRequested) break;
                    
                // PERFORMANCE: Use Span and MemoryMarshal for fast sample conversion
                var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytesRead));

                foreach (var sample in samples)
                {
                    float v = Math.Abs(sample / 32768f);
                    if (v > bucketPeak) bucketPeak = v;
                    samplesInBucket++;

                    if (samplesInBucket >= samplesPerBucket)
                    {
                        if (currentBucket < buckets)
                        {
                            waveformData[currentBucket] = bucketPeak;
                            if (bucketPeak > globalMax) globalMax = bucketPeak;
                            currentBucket++;
                            batchCounter++;
                        }
                        samplesInBucket = 0;
                        bucketPeak = 0f;

                        if (batchCounter >= currentBatchSize || currentBucket >= buckets)
                        {
                            int currentIdx = currentBucket;
                            int count = batchCounter;
                            var batch = new float[count];
                            Array.Copy(waveformData, currentIdx - count, batch, 0, count);
                            _syncContext?.Post(_ => Waveform.AddRange(batch), null);
                            batchCounter = 0;
                                

                            // Gradually increase batch size for efficiency after initial data is shown
                            if (currentBatchSize < 128) currentBatchSize += 16;
                        }
                        if (currentBucket >= buckets) break;
                    }
                }
                    if (currentBucket >= buckets) break;
                }

                // Handle partial bucket and remaining batch items at end of file
                if (currentBucket < buckets && samplesInBucket > 0)
                {
                    waveformData[currentBucket] = bucketPeak;
                    if (bucketPeak > globalMax) globalMax = bucketPeak;
                    currentBucket++;
                    batchCounter++;
                }

                if (batchCounter > 0)
                {
                    int count = batchCounter;
                    var batch = new float[count];
                    Array.Copy(waveformData, currentBucket - count, batch, 0, count);
                    _syncContext?.Post(_ => { foreach (var b in batch) Waveform.Add(b); }, null);
                }

                // Padding to ensure exactly 'buckets' items to avoid gaps in UI
                if (currentBucket < buckets)
                {
                    int remaining = buckets - currentBucket;
                    _syncContext?.Post(_ => {
                        for (int i = 0; i < remaining; i++) Waveform.Add(0f);
                    }, null);
                }

                if (globalMax <= 0f) globalMax = 1f;
                
            // Final normalization for consistency
            _syncContext?.Post(_ => {
                const float verticalGain = 1.1f;
                const float minVisible = 0.01f;

                for (int i = 0; i < Waveform.Count; i++)
                {
                    var v = (Waveform[i] / globalMax) * verticalGain;
                    v = Math.Max(v, minVisible);
                    Waveform[i] = Math.Min(1f, v);
                }
            }, null);
        }
        catch (Exception ex) { Log.Error($"Error generating waveform for {path}", ex); }
        finally
        {
            IsLoadingWaveform = false;
            _ffmpegManager?.ReportActivity(false);
        }
    }

    /// <summary>
    /// Configures the audio equalizer using the supplied band definitions.
    /// </summary>
    /// <param name="bands">Collection of band models describing frequency/gain.</param>
    public void SetEqualizerBands(AvaloniaList<BandModel> bands)
    {
        var filters = bands
            .Select(b => $"equalizer=f={b.NumericFrequency.ToString(CultureInfo.InvariantCulture)}:width_type=o:w=1:g={b.Gain.ToString(CultureInfo.InvariantCulture)}")
            .ToList();

        SetProperty("af", filters.Any() ? string.Join(",", filters) : "");
    }

    private CancellationTokenSource? _eqCts;
    /// <summary>
    /// Throttled wrapper around <see cref="SetEqualizerBands"/> to reduce
    /// the number of rapid reconfigurations when the UI is changing values.
    /// </summary>
    /// <param name="bands">Equalizer band definitions.</param>
    public void SetEqualizerBandsThrottled(AvaloniaList<BandModel> bands)
    {
        try { _eqCts?.Cancel(); }
        catch (Exception ex) { Log.Warn("Failed to cancel eq CTS", ex); }
        try { _eqCts?.Dispose(); }
        catch (Exception ex) { Log.Warn("Failed to dispose eq CTS", ex); }

        // Create a new CTS for this throttled update. We intentionally do not
        // pass the token into Task.Delay/Task.Run to avoid TaskCanceledException
        // being thrown (and reported as a first-chance exception) when the
        // delay is cancelled. Instead we perform a non-cancelable delay and
        // check the token afterwards.
        _eqCts = new CancellationTokenSource();
        var token = _eqCts.Token;

        Task.Run(async () =>
        {
            // Non-cancelable delay to avoid TaskCanceledException being thrown by
            // Task.Delay when a token is cancelled. We still honor cancellation
            // by checking the token after the delay and skipping the update.
            await Task.Delay(100).ConfigureAwait(false);
            try
            {
                if (!token.IsCancellationRequested)
                {
                    SetEqualizerBands(bands);
                }
            }
            catch (Exception ex) { Log.Warn("SetEqualizerBandsThrottled: exception while setting bands", ex); }
        });
    }

    public bool IsPlaying
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged(nameof(IsPlaying));
            if (field)
            {
                if (EnableSpectrum && !IsSeeking && !_disposed)
                {
                    _spectrumAnalyzer?.SetStartPosition(Position);
                    _spectrumAnalyzer?.Start();
                }
                Playing?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Allow the analyzer to keep running in idle state to perform fade-out
                Paused?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Seeks to the specified position in seconds. Temporarily marks the
    /// player as seeking to suppress position events.
    /// </summary>
    /// <param name="pos">Target position in seconds.</param>
    public void SetPosition(double pos)
    {
        _isSeeking = true;
        // Don't call Stop() here! Let the analyzer's loop handle fading out via the IsSeeking flag.

        if (!_disposed) SetProperty("time-pos", pos);
        Position = pos; // Update immediately for UI feedback

        // Cancel previous restart attempt to debounce
        _seekRestartCts?.Cancel();
        _seekRestartCts?.Dispose();
        _seekRestartCts = new CancellationTokenSource();
        var token = _seekRestartCts.Token;

        Task.Run(async () => {
            try 
            {
                await Task.Delay(400, token); // Debounce delay
                if (token.IsCancellationRequested) return;

                if (!_disposed) 
                {
                    _spectrumAnalyzer?.SetStartPosition(pos, IsPlaying);
                }
                _isSeeking = false;
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// Suspends playback and returns the current position and playing state
    /// so the caller can perform edits and later restore playback.
    /// </summary>
    public async Task<(double Position, bool WasPlaying)> SuspendForEditingAsync()
    {
        await _initTcs.Task;
        var state = (Position, IsPlaying);
        _spectrumAnalyzer?.Stop();
        _waveformCts?.Cancel();

        try
        {
            if (_activeFfmpegProcess != null && !_activeFfmpegProcess.HasExited)
                _activeFfmpegProcess.Kill(true);
        }
        catch (Exception ex) { Log.Warn("Error killing active ffmpeg process during SuspendForEditing", ex); }

        InternalStop();
        await Task.Delay(300); // Wait for OS handle release
        return state;
    }


    /// <summary>
    /// Resumes playback after an editing operation using the supplied state.
    /// /// </summary>
    /// <param name="path">The media path to reload.</param>
    /// <param name="position">Position to seek to after reload.</param>
    /// <param name="wasPlaying">Whether playback should resume.</param>
    public async Task ResumeAfterEditingAsync(string path, double position, bool wasPlaying)
    {
        await _initTcs.Task;
        _isInternalChange = true;
        IsLoadingMedia = true;
        _loadedFile = path;

        // Reload the file
        await ExecuteCommandAsync(["loadfile", path]);

        // WAIT for MPV to initialize the file before seeking
        while (_isLoadingMedia)
        {
            await Task.Delay(50);
        }

        SetPosition(position);

        if (wasPlaying) Play();
        else Pause();
    }

    /// <summary>
    /// Start playback.
    /// </summary>
    public void Play() { if (!_disposed) SetProperty("pause", false); IsPlaying = true; }

    /// <summary>
    /// Pause playback.
    /// </summary>
    public void Pause() { if (!_disposed) SetProperty("pause", true); IsPlaying = false; }

    /// <summary>
    /// Stop playback and reset state.
    /// </summary>
    public void Stop() 
    { 
        InternalStop();
        IsLoadingMedia = false; 
    }

    /// <summary>
    /// Clears the current media item, setting it to null.
    /// </summary>
    public void ClearMedia()
    {
        _currentMediaItem = null;
    }

    private void InternalStop()
    {
        if (!_disposed) ExecuteCommandAsync(["stop"]); 
        IsPlaying = false; 
        Duration = 0;
        Position = 0;
        Stopped?.Invoke(this, EventArgs.Empty); 
    }

    /// <summary>
    /// Writes the provided bytes to a temporary file and begins playback.
    /// </summary>
    /// <param name="b">Byte buffer containing media data.</param>
    /// <param name="m">MIME type hint (unused).</param>
    public async Task PlayBytes(byte[]? b, string m = "video/mp4")
    {
        await _initTcs.Task;
        if (b == null) return;
        var p = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tmp");
        await File.WriteAllBytesAsync(p, b); await PlayFile(new MediaItem() { FileName = p });
    }

    /// <summary>
    /// Dispose managed resources used by the player (stops analyzers and
    /// kills any active FFmpeg helper process).
    /// </summary>
    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ffmpegManager != null)
        {
            _ffmpegManager.RequestFfmpegTermination -= OnRequestFfmpegTermination;
            _ffmpegManager.InstallationCompleted -= OnFfmpegInstallationCompleted;
        }

        if (_mpvLibraryManager != null)
        {
            _mpvLibraryManager.RequestMpvTermination -= OnRequestMpvTermination;
            _mpvLibraryManager.InstallationCompleted -= OnMpvInstallationCompleted;
        }

        try { MpvEvent -= OnMpvEvent; }
        catch (Exception ex) { Log.Warn("Failed to remove mpv event handler", ex); }
        try { _spectrumAnalyzer?.Stop(); }
        catch (Exception ex) { Log.Warn("Failed to stop spectrum analyzer", ex); }
        try { _spectrumAnalyzer?.SetPath(""); }
        catch (Exception ex) { Log.Warn("Failed to clear spectrum analyzer path", ex); }

        try { _waveformCts?.Cancel(); }
        catch (Exception ex) { Log.Warn("Failed to cancel waveform CTS during dispose", ex); }
        try { _waveformCts?.Dispose(); }
        catch (Exception ex) { Log.Warn("Failed to dispose waveform CTS during dispose", ex); }
        _waveformCts = null;

        try { _eqCts?.Cancel(); }
        catch (Exception ex) { Log.Warn("Failed to cancel eq CTS during dispose", ex); }
        try { _eqCts?.Dispose(); }
        catch (Exception ex) { Log.Warn("Failed to dispose eq CTS during dispose", ex); }
        _eqCts = null;

        try
        {
            if (_activeFfmpegProcess != null && !_activeFfmpegProcess.HasExited)
            {
                try { _activeFfmpegProcess.Kill(true); }
                catch (Exception ex) { Log.Warn("Failed to kill active ffmpeg process during dispose", ex); }
                try { _activeFfmpegProcess.WaitForExit(100); }
                catch (Exception ex) { Log.Warn("Error waiting for ffmpeg exit during dispose", ex); }
            }
            try { _activeFfmpegProcess?.Dispose(); }
            catch (Exception ex) { Log.Warn("Failed to dispose ffmpeg process", ex); }
        }
        catch (Exception ex) { Log.Warn("Error while disposing ffmpeg process", ex); }
        _activeFfmpegProcess = null;

        // Ensure the base libmpv handle is correctly disposed and freed from memory
        try
        {
            base.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Error while disposing base MPVMediaPlayer", ex);
        }
    }
}

