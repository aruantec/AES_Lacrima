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
using System.Collections.Concurrent;
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

    private readonly FfMpegSpectrumAnalyzer _spectrumAnalyzer;
    private readonly FFmpegManager? _ffmpegManager;
    private readonly MpvLibraryManager? _mpvLibraryManager;
    private CancellationTokenSource? _waveformCts;
    private readonly TaskCompletionSource _initTcs = new();
    // Dedicated MPV thread queue and worker to ensure all libmpv interop
    // runs on a single thread that owns the mpv handle.
    private readonly BlockingCollection<Action> _mpvQueue = new();
    private Thread? _mpvThread;
    private int _mpvThreadId;
    private double _volume = 70;
    // Balance: -1 (full left) .. 0 (center) .. 1 (full right)
    private double _balance = 0.0;
    // Stored equalizer filters string (without balance/pan)
    private string _eqAf = string.Empty;
    // Preamp gain in dB applied via af=volume filter. Positive values allow >100% loudness.
    private double _preampDb = 0.0;
    /// <summary>
    /// Master toggle for applying replay gain / loudness normalization at playback.
    /// </summary>
    private double _replayGainAdjustmentDb = 0.0;

    // trailing silence computed from waveform (seconds)
    private double _trailingSilenceSeconds;
    private bool _silenceAdvanceFired;

    /// <summary>
    /// Gets or sets a value indicating whether volume changes are applied smoothly.
    /// </summary>
    public bool SmoothVolumeChange { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether logarithmic volume control is used.
    /// </summary>
    public bool LogarithmicVolumeControl { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether loudness compensation is applied to volume control.
    /// </summary>
    public bool LoudnessCompensatedVolume { get; set; } = true;

    // Cache ReplayGain settings in-memory to ensure consistency across track changes
    private ReplayGainOptions? _lastOptions;

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

    // Helper to run actions on the MPV thread and wait for completion.
    // NOTE: the MPV API (via LibMPVSharp) dispatches command results back
    // on the same thread that owns the mpv handle.  Blocking that thread by
    // waiting for a Task returned from a command (e.g. ``ExecuteCommandAsync``)
    // will prevent the response from ever being delivered and lead to a
    // deadlock.  In practice this manifested on macOS when loading a file
    // (``loadfile``) because the completion callback was posted to the mpv
    // worker thread.  ``InvokeOnMpvThread`` itself is safe, but callers must
    // avoid queuing work that synchronously blocks the mpv thread.  For
    // fire‑and‑forget operations we now provide ``PostToMpvThread`` below.
    private T InvokeOnMpvThread<T>(Func<T> func)
    {
        // If we're already on the mpv thread, invoke directly
        if (Thread.CurrentThread.ManagedThreadId == _mpvThreadId)
            return func();

        // guard against caller using this after the player has been disposed
        if (_mpvQueue.IsAddingCompleted || (_mpvThread != null && !_mpvThread.IsAlive))
            throw new ObjectDisposedException(nameof(AudioPlayer));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mpvQueue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Warn("InvokeOnMpvThread timed out after 5 seconds - ignoring to prevent crash");
            return default!;
        }
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Public wrapper to request recomputation of replaygain for the currently loaded file.
    /// Safe to call from other threads.
    /// </summary>
    public Task RecomputeReplayGainForCurrentAsync()
    {
        try
        {
            var path = _loadedFile;
            var item = _currentMediaItem;
            if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
            return ApplyReplayGainForFileAsync(path, item);
        }
        catch (Exception ex)
        {
            Log.Warn("RecomputeReplayGainForCurrentAsync failed", ex);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Recompute replaygain for the current file using explicit options supplied
    /// from the caller (avoids reading the settings file).
    /// </summary>
    public Task RecomputeReplayGainForCurrentAsync(bool enabled, bool useTags, bool analyze, double preampAnalyze, double preampTags, int tagSource)
    {
        try
        {
            var path = _loadedFile;
            var item = _currentMediaItem;
            if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
            var opts = new ReplayGainOptions(enabled, useTags, analyze, preampAnalyze, preampTags, tagSource);
            return ApplyReplayGainForFileAsync(path, item, opts);
        }
        catch (Exception ex)
        {
            Log.Warn("RecomputeReplayGainForCurrentAsync(options) failed", ex);
            return Task.CompletedTask;
        }
    }

    private record ReplayGainOptions(bool Enabled, bool UseTags, bool Analyze, double PreampAnalyze, double PreampTags, int TagSource);

    /// <summary>
    /// Compute replaygain for the provided file path and apply the adjustment
    /// (in dB) so it's included in the combined preamp. If <paramref name="options"/>
    /// is null the method will use the last known options or try to read from Settings.json.
    /// Attempts tag-based gain first, then optional ffmpeg volumedetect analysis.
    /// </summary>
    private async Task ApplyReplayGainForFileAsync(string path, MediaItem? item, ReplayGainOptions? options = null)
    {
        try
        {
            _replayGainAdjustmentDb = 0.0;

            // Prioritize provided options, then cached options, then disk reading
            if (options != null)
            {
                _lastOptions = options;
            }
            else if (_lastOptions == null)
            {
                // Try reading from disk for the first time
                var settingsPath = Path.Combine(AppContext.BaseDirectory, "Settings", "Settings.json");
                bool enabled = false;
                bool useTags = true;
                bool analyze = true;
                double preampAnalyze = 0.0;
                double preampTags = 0.0;
                int tagSource = 1;

                try
                {
                    if (File.Exists(settingsPath))
                    {
                        var txt = await File.ReadAllTextAsync(settingsPath).ConfigureAwait(false);
                        using var doc = System.Text.Json.JsonDocument.Parse(txt);
                        if (doc.RootElement.TryGetProperty("ViewModels", out var vms) && vms.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (vms.TryGetProperty("SettingsViewModel", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (s.TryGetProperty("ReplayGainEnabled", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.True) enabled = true;
                                if (s.TryGetProperty("ReplayGainUseTags", out var ut) && ut.ValueKind == System.Text.Json.JsonValueKind.False) useTags = false;
                                if (s.TryGetProperty("ReplayGainAnalyzeOnTheFly", out var an) && an.ValueKind == System.Text.Json.JsonValueKind.False) analyze = false;
                                if (s.TryGetProperty("ReplayGainPreampDb", out var pap) && pap.ValueKind == System.Text.Json.JsonValueKind.Number) preampAnalyze = pap.GetDouble();
                                if (s.TryGetProperty("ReplayGainTagsPreampDb", out var ptp) && ptp.ValueKind == System.Text.Json.JsonValueKind.Number) preampTags = ptp.GetDouble();
                                if (s.TryGetProperty("ReplayGainTagSource", out var tsrc) && tsrc.ValueKind == System.Text.Json.JsonValueKind.Number) tagSource = tsrc.GetInt32();
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Debug("Failed to read settings.json for replaygain", ex); }

                _lastOptions = new ReplayGainOptions(enabled, useTags, analyze, preampAnalyze, preampTags, tagSource);
            }

            // Destructuring cached options for actual processing
            bool enabledFinal = _lastOptions.Enabled;
            bool useTagsFinal = _lastOptions.UseTags;
            bool analyzeFinal = _lastOptions.Analyze;
            double preampAnalyzeFinal = _lastOptions.PreampAnalyze;
            double preampTagsFinal = _lastOptions.PreampTags;
            int tagSourceFinal = _lastOptions.TagSource;

            if (!enabledFinal)
            {
                _replayGainAdjustmentDb = 0.0;
                UpdateAf();
                return;
            }

            double? tagGainDb = null;

            if (useTagsFinal)
            {
                try
                {
                    using var f = TagLib.File.Create(path);
                    var xiph = f.GetTag(TagLib.TagTypes.Xiph, false) as TagLib.Ogg.XiphComment;
                    if (xiph != null)
                    {
                        // Prioritize Album gain if selected in settings
                        if (tagSourceFinal == 1) // Album
                        {
                            if (xiph.GetField("REPLAYGAIN_ALBUM_GAIN") is string[] aa && aa.Length > 0)
                                if (double.TryParse(StripDb(aa[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out var av)) tagGainDb = av;
                        }

                        if (tagGainDb == null) // Fallback to Track gain
                        {
                            if (xiph.GetField("REPLAYGAIN_TRACK_GAIN") is string[] arr && arr.Length > 0)
                                if (double.TryParse(StripDb(arr[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) tagGainDb = v;
                        }
                    }

                    if (tagGainDb == null)
                    {
                        var id3 = f.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
                        if (id3 != null)
                        {
                            if (tagSourceFinal == 1) // Album
                            {
                                var albFrm = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "REPLAYGAIN_ALBUM_GAIN", false);
                                if (albFrm != null && albFrm.Text?.Length > 0 && double.TryParse(StripDb(albFrm.Text[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out var v3)) tagGainDb = v3;
                            }

                            if (tagGainDb == null) // Fallback to Track gain
                            {
                                var frm = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "REPLAYGAIN_TRACK_GAIN", false);
                                if (frm != null && frm.Text?.Length > 0 && double.TryParse(StripDb(frm.Text[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out var v2)) tagGainDb = v2;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Debug("Error reading tags for replaygain", ex); }
            }

            if (tagGainDb.HasValue)
            {
                var rawAdj = tagGainDb.Value + preampTagsFinal;
                // Clamp per-file adjustment to a safe range to avoid excessive positive boost
                const double MaxReplayGainDb = 8.0;
                const double MinReplayGainDb = -18.0;
                if (rawAdj > MaxReplayGainDb) rawAdj = MaxReplayGainDb;
                if (rawAdj < MinReplayGainDb) rawAdj = MinReplayGainDb;
                _replayGainAdjustmentDb = rawAdj;
                Log.Info($"ReplayGain: tag={tagGainDb.Value:0.##} dB, preamp={preampTagsFinal:0.##} dB, adjustment={_replayGainAdjustmentDb:0.##} dB");
                UpdateAf();
                return;
            }

            // No tags found; optionally analyze on-the-fly using ffmpeg volumedetect
            if (analyzeFinal && _ffmpegManager != null)
            {
                try
                {
                    if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) { /* skip remote */ UpdateAf(); return; }
                    var ffmpeg = FFmpegLocator.FindFFmpegPath();
                    if (string.IsNullOrEmpty(ffmpeg)) { UpdateAf(); return; }

                    // PERFORMANCE: Analyze first 60s for estimate
                    var args = $"-hide_banner -nostats -i \"{path}\" -t 60 -af volumedetect -f null -";
                    var psi = new ProcessStartInfo(ffmpeg, args) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        proc.WaitForExit(3000);
                        var m = System.Text.RegularExpressions.Regex.Match(stderr, @"mean_volume:\s*(-?[0-9]+\.?[0-9]*)\s*dB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mean))
                        {
                            // Target -18 dBFS reference
                            const double ReferenceLevel = -18.0;
                            var gainNeeded = ReferenceLevel - mean + preampAnalyzeFinal;

                            const double MaxAnalyzedDb = 8.0;
                            const double MinAnalyzedDb = -18.0;
                            if (gainNeeded > MaxAnalyzedDb) gainNeeded = MaxAnalyzedDb;
                            if (gainNeeded < MinAnalyzedDb) gainNeeded = MinAnalyzedDb;
                            _replayGainAdjustmentDb = gainNeeded;
                            Log.Info($"ReplayGain: analyzed={mean:0.##} dB, target={ReferenceLevel} dB, adjustment={_replayGainAdjustmentDb:0.##} dB");
                            UpdateAf();
                            return;
                        }
                    }
                }
                catch (Exception ex) { Log.Debug("Error running ffmpeg volumedetect for replaygain", ex); }
            }

            _replayGainAdjustmentDb = 0.0;
            UpdateAf();
        }
        catch (Exception ex)
        {
            Log.Warn("ApplyReplayGainForFileAsync failed", ex);
        }
    }

    private static string StripDb(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private void InvokeOnMpvThread(Action action)
    {
        if (Thread.CurrentThread.ManagedThreadId == _mpvThreadId)
        {
            action();
            return;
        }

        if (_mpvQueue.IsAddingCompleted || (_mpvThread != null && !_mpvThread.IsAlive))
            throw new ObjectDisposedException(nameof(AudioPlayer));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mpvQueue.Add(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Warn("InvokeOnMpvThread(Action) timed out after 5 seconds - ignoring to prevent crash");
        }
    }

    /// <summary>
    /// Enqueue an action on the MPV thread without waiting for completion.
    /// This is useful for operations that already return a <see cref="Task" />
    /// whose completion is signalled on the MPV thread (e.g. many of the
    /// LibMPVSharp helpers such as <c>ExecuteCommandAsync</c>).  Blocking the
    /// thread in that case would deadlock because the continuation cannot run.
    /// </summary>
    private void PostToMpvThread(Action action)
    {
        if (Thread.CurrentThread.ManagedThreadId == _mpvThreadId)
        {
            action();
            return;
        }

        if (_mpvQueue.IsAddingCompleted || (_mpvThread != null && !_mpvThread.IsAlive))
            throw new ObjectDisposedException(nameof(AudioPlayer));

        _mpvQueue.Add(action);
    }

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
    /// When true the player will automatically fire <see cref="EndReached" />
    /// slightly early if the trailing portion of the audio contains a block
    /// of silence.  Set this to false to disable the behaviour.
    /// </summary>
    private bool _autoSkipTrailingSilence = false;
    public bool AutoSkipTrailingSilence
    {
        get => _autoSkipTrailingSilence;
        set
        {
            if (_autoSkipTrailingSilence == value) return;
            _autoSkipTrailingSilence = value;
            if (value)
            {
                TimeChanged -= OnTimeChangedForSilence;
                TimeChanged += OnTimeChangedForSilence;
            }
            else
            {
                TimeChanged -= OnTimeChangedForSilence;
            }
            OnPropertyChanged(nameof(AutoSkipTrailingSilence));
        }
    }

    /// <summary>
    /// Delay in milliseconds to wait after entering the trailing‑silence region
    /// before signalling <see cref="EndReached" />.  This value is typically
    /// controlled via the settings UI and defaults to 500ms.
    /// </summary>
    public int SilenceAdvanceDelayMs { get; set; } = 500;

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
                InvokeOnMpvThread(() => { SetProperty("demuxer-max-bytes", $"{value}M"); return true; });
        }
    } = 32;

    /// <summary>
    /// Playback volume (0..100).
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(_volume - value) < 0.001) return;
            _volume = value;
            UpdateAf();
            OnPropertyChanged(nameof(Volume));
        }
    }

    /// <summary>
    /// Preamp gain in decibels. Applied via the mpv/ffmpeg volume audio-filter (e.g. +6dB).
    /// Use positive values to increase loudness beyond 100%.
    /// </summary>
    public double PreampDb
    {
        get => _preampDb;
        set
        {
            // Clamp reasonable range to avoid extreme gain
            if (value < -60.0) value = -60.0;
            if (value > 20.0) value = 20.0;
            _preampDb = value;
            UpdateAf();
            OnPropertyChanged(nameof(PreampDb));
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
    /// When no media is loaded and the player is not playing, return 1s
    /// instead of 0 to avoid zero-length edge cases in UI components.
    /// </summary>
    private double _duration;
    public double Duration
    {
        get
        {
            // If no file is loaded and player is idle, expose a 1s default instead of 0.
            if ((_duration == 0.0 || double.IsNaN(_duration)) && !_disposed && !IsPlaying && _currentMediaItem == null)
                return 0.0;
            return _duration;
        }
        set
        {
            _duration = value;
            OnPropertyChanged(nameof(Duration));
            // duration is now available; waveform generation may have been deferred
            if (EnableWaveform && (_waveformLoadedFile != _loadedFile))
            {
                CheckAndStartFfmpegTasks();
            }
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
                InvokeOnMpvThread(() => { SetProperty("loop-file", value == RepeatMode.One ? "yes" : "no"); return true; });

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

        // Start a dedicated thread that will initialize and own the MPV handle
        // and process all MPV-related actions to avoid native interop crashes.
        _mpvThread = new Thread(() =>
        {
            try
            {
                _mpvThreadId = Thread.CurrentThread.ManagedThreadId;
                // Initialize mpv on this dedicated thread
                InitializeMpvAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error("MPV thread initialization failed", ex);
            }

            // Process queued actions until CompleteAdding is called
            try
            {
                foreach (var a in _mpvQueue.GetConsumingEnumerable())
                {
                    try { a(); } catch (Exception ex) { Log.Warn("MPV queued action failed", ex); }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("MPV queue processing terminated", ex);
            }
        }) { IsBackground = true, Name = "mpv-worker" };
        _mpvThread.Start();
    }

    /// <summary>
    /// Gets or sets the stereo balance (-1..1). Setting updates the mpv audio filter chain.
    /// </summary>
    public double Balance
    {
        get => _balance;
        set
        {
            if (value < -1) value = -1;
            if (value > 1) value = 1;
            _balance = value;
            UpdateAf();
            OnPropertyChanged(nameof(Balance));
        }
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
                // Use PostToMpvThread or a safe task for commands that might block or depend on the current thread
                _ = ExecuteCommandAsync(["set", "coreaudio-change-device", "no"]);
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
            SetProperty("replaygain", "no"); // Disable internal mpv replaygain as we apply it manually
            SetProperty("demuxer-max-bytes", $"{CacheSize}M");
            SetProperty("demuxer-readahead-secs", "10");
            SetProperty("volume", _volume);
            SetProperty("volume-max", 200);
            SetProperty("loop-file", RepeatMode == RepeatMode.One ? "yes" : "no");
            SetProperty("demuxer-max-bytes", $"{CacheSize}M");

            MpvEvent += OnMpvEvent;

            // Mark initialization as complete ONLY after we've applied the final property sync.
            _initTcs.SetResult();

            // Ensure any cached AF (equalizer + balance) is applied now that mpv is ready
            UpdateAf();

            OnPropertyChanged(nameof(Volume));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to initialize AudioPlayer asynchronously", ex);
            _initTcs.TrySetException(ex);
        }

    }

    /// <summary>
    /// Updates the mpv "af" property with the equalizer chain. 
    /// Preamp, ReplayGain and Balance are applied via native properties for better stability.
    /// </summary>
    private void UpdateAf()
    {
        try
        {
            if (!(_initTcs.Task.IsCompleted) || _disposed) return;

            // 1. Equalizer is applied via the 'af' property. We keep this minimal for stability.
            // Complex audio-filter strings (like pan or volume) can cause native crashes in some mpv builds.
            var eqOnly = string.IsNullOrEmpty(_eqAf) ? string.Empty : _eqAf;

            if (_initTcs.Task.IsCompleted && !_disposed)
            {
                try
                {
                    InvokeOnMpvThread(() => { SetProperty("af", eqOnly); return true; });
                }
                catch (Exception ex)
                {
                    Log.Warn("Failed to set AF equalizer on MPV thread", ex);
                }
            }

            // 2. Stereo balance is applied using mpv's native 'balance' property.
            try
            {
                if (_initTcs.Task.IsCompleted && !_disposed)
                {
                    InvokeOnMpvThread(() => { SetProperty("balance", _balance); return true; });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to set balance property on MPV thread", ex);
            }

            // 3. Preamp and ReplayGain are applied by adjusting the mpv 'volume' property multiplicatively.
            try
            {
                if (_initTcs.Task.IsCompleted && !_disposed)
                {
                    // Combine global user preamp with per-file ReplayGain adjustment
                    var totalPreampDb = _preampDb + _replayGainAdjustmentDb;

                    // Clamp total preamp to avoid extreme boost causing digital clipping.
                    const double MaxTotalPreampDb = 15.0; 
                    const double MinTotalPreampDb = -40.0;
                    if (totalPreampDb > MaxTotalPreampDb) totalPreampDb = MaxTotalPreampDb;
                    if (totalPreampDb < MinTotalPreampDb) totalPreampDb = MinTotalPreampDb;

                    // Convert dB to linear gain: multiplier = 10^(dB/20)
                    var gain = Math.Pow(10.0, totalPreampDb / 20.0);

                    // Transformation of user-requested 0..100% volume level
                    var inputVolume = _volume;

                    // Logarithmic mapping: quadratically maps linear input to perceived linear output.
                    // This creates a more natural-feeling volume curve for human hearing.
                    if (LogarithmicVolumeControl)
                    {
                        inputVolume = Math.Pow(inputVolume / 100.0, 2) * 100.0;
                    }

                    // Loudness compensation: simple psychoacoustic mapping for low volumes.
                    // ISO 226-2003 inspired boost to help normalize perceived intensity at lower ranges.
                    if (LoudnessCompensatedVolume)
                    {
                        // Using a 1.5-power mapping for loudness compensation approximation
                        inputVolume = Math.Pow(inputVolume / 100.0, 1.0 / 1.5) * 100.0;
                    }

                    var effective = inputVolume * gain; 

                    // Apply effective volume to mpv. We set volume-max to 200 during init to allow this boost.
                    const double MaxEffectiveVolume = 200.0;
                    if (effective < 0) effective = 0;
                    if (effective > MaxEffectiveVolume)
                    {
                        Log.Warn($"Effective volume {effective:0.##}% exceeded max {MaxEffectiveVolume}%. Clamping.");
                        effective = MaxEffectiveVolume;
                    }

                    InvokeOnMpvThread(() => { SetProperty("volume", effective); return true; });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to apply volume/preamp on MPV thread", ex);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("UpdateAf failed", ex);
        }
    }

    private void OnRequestFfmpegTermination()
    {
        // A global request has been made to kill any running FFmpeg instances.
        // The spectrum analyzer uses its own helper process, so shut it down as
        // well; otherwise the UI will continue displaying stale data.
        _spectrumAnalyzer?.Stop();
        _waveformCts?.Cancel();
        try { _activeFfmpegProcess?.Kill(true); } catch { }

        // The analyzer was deliberately stopped above, but playback may still be
        // ongoing.  If we have a valid media item loaded and the player is
        // playing, restart analysis once the termination request has settled.
        // A small delay helps avoid racing with whatever component triggered the
        // shutdown (for example the FFmpeg uninstall flow) and gives the process
        // killing a moment to complete.
        if (IsPlaying && EnableSpectrum && !_disposed)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                _syncContext?.Post(_ => CheckAndStartFfmpegTasks(), null);
            });
        }
    }

    private void OnRequestMpvTermination()
    {
        Dispose();
    }

    private async void OnTimeChangedForSilence(object? sender, long ms)
    {
        if (_silenceAdvanceFired || _trailingSilenceSeconds <= 0) return;
        // fire early when we enter the silent region
        if (Position >= (Duration - _trailingSilenceSeconds))
        {
            _silenceAdvanceFired = true;
            // wait a short grace period before signalling so playlist logic
            // isn’t raced by immediate transition.  duration is user-configurable.
            await Task.Delay(SilenceAdvanceDelayMs);
            // If we're in "repeat one" mode we should not notify listeners;
            // mpv will loop the file itself so external playlist logic must
            // not advance.  Other repeat modes (off/all/shuffle) still
            // require the event to drive Next/stop behaviour.
            if (RepeatMode != RepeatMode.One)
            {
                EndReached?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnMpvInstallationCompleted(object? sender, MpvLibraryManager.InstallationCompletedEventArgs e)
    {
        if (e.Success && _initTcs.Task.IsFaulted)
        {
            // libmpv was previously missing but now its here, try to re-init
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
            _spectrumAnalyzer.SetStartPosition(Position);
            _spectrumAnalyzer.Start();
        }
        // Start waveform if missing or not for the current file *and* we know the duration.
        // Attempting to generate before duration is available often produces garbage
        // data, so simply defer until Duration > 0.  The Duration setter below will
        // call this method again when the property is updated.
        if (EnableWaveform && (_waveformLoadedFile != _loadedFile) && Duration > 0)
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
                Log.Warn($"MPV end-file error for '{_loadedFile}': {endData.error}");
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

        // Prepare for loading the new file
        _isInternalChange = true;
        IsLoadingMedia = true;
        OnPropertyChanged(nameof(IsLoadingMedia));
        _loadedFile = fileToPlay;
        var mpvLoadTarget = ToMpvLoadTarget(fileToPlay);

        // Reset per-file gain synchronously to ensure the initial UpdateAf call doesn't use stale metadata
        _replayGainAdjustmentDb = 0.0;

        // reset silence detection state; will be recalculated after waveform finishes
        _trailingSilenceSeconds = 0;
        _silenceAdvanceFired = false;
        if (AutoSkipTrailingSilence)
        {
            TimeChanged -= OnTimeChangedForSilence;
            TimeChanged += OnTimeChangedForSilence;
        }

        // Compute and apply replaygain/preamp adjustments before starting playback
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyReplayGainForFileAsync(fileToPlay, item).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn("ApplyReplayGainForFile failed", ex); }
        });
        _waveformLoadedFile = null;

        // Ensure analyzer is fully stopped and path is updated before loading new file
        InternalStop();

        //Set the current media item
        _currentMediaItem = item;

        if (EnableSpectrum)
        {
            _spectrumAnalyzer.SetPath(fileToPlay);
            _spectrumAnalyzer.Start();
        }
        _waveformCts?.Cancel();

        _syncContext?.Post(_ => { Waveform.Clear(); Spectrum.Clear(); Position = 0; }, null);
        PostToMpvThread(() =>
        {
            SetProperty("vo", video ? "auto" : "null");
            SetProperty("vid", video ? "auto" : "no");
            SetProperty("audio-display", video ? "auto" : "no");
        });
        // queue the load command but do not block the mpv thread waiting for
        // its completion.  ``ExecuteCommandAsync`` completes on the MPV
        // thread, so waiting there would deadlock (see comments above).
        PostToMpvThread(() => _ = ExecuteCommandAsync(new[] { "loadfile", mpvLoadTarget }));

        // Re-apply audio filters/volume after load in case mpv reset properties during load
        // Use PostToMpvThread instead of UpdateAf to avoid blocking during Load
        PostToMpvThread(UpdateAf);
        Play();
    }


    private async Task GenerateWaveformAsync(string path, CancellationToken token, int buckets = 4000)
    {
        if (!EnableWaveform || string.IsNullOrEmpty(path) || buckets <= 0) return;
        // do not mark the file as "loaded" until we successfully generate the data;
        // earlier code placed this at the start which prevented retries when the
        // operation was cancelled or failed midway.

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
                RedirectStandardError = true,
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

            // compute trailing silence length using raw waveform data before normalization
            if (AutoSkipTrailingSilence && buckets > 0 && Duration > 0)
            {
                const float rawSilenceFrac = 0.05f; // 5% of peak considered silence
                float rawThresh = globalMax * rawSilenceFrac;
                int idxRaw = buckets - 1;
                while (idxRaw >= 0 && waveformData[idxRaw] <= rawThresh)
                    idxRaw--;
                int silentBucketsRaw = buckets - 1 - idxRaw;
                _trailingSilenceSeconds = (silentBucketsRaw / (double)buckets) * Duration;
                if (_trailingSilenceSeconds < 1.0) _trailingSilenceSeconds = 0;
            }

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


            // mark success so future calls know waveform was generated
            _waveformLoadedFile = path;
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
        // PERFORMANCE: Only add bands that have a non-neutral gain value to simplify the AF chain.
        var filters = bands
            .Where(b => Math.Abs(b.Gain) > 0.01)
            .Select(b => $"equalizer=f={b.NumericFrequency.ToString(CultureInfo.InvariantCulture)}:width_type=o:w=1:g={b.Gain.ToString(CultureInfo.InvariantCulture)}")
            .ToList();
        // Store the equalizer portion and update the combined AF state
        _eqAf = filters.Any() ? string.Join(",", filters) : string.Empty;
        UpdateAf();
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
                    _spectrumAnalyzer.SetStartPosition(Position);
                    _spectrumAnalyzer.Start();
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

        if (!_disposed) InvokeOnMpvThread(() => { SetProperty("time-pos", pos); return true; });
        Position = pos; // Update immediately for UI feedback

        // Cancel previous restart attempt to debounce
        _seekRestartCts?.Cancel();
        _seekRestartCts?.Dispose();
        _seekRestartCts = new CancellationTokenSource();
        var token = _seekRestartCts.Token;

        Task.Run(async () => {
            try 
            {
                    // shorter debounce helps spectrum restart sooner on fast seeks
                    await Task.Delay(200, token);
                    if (token.IsCancellationRequested) return;

                    if (!_disposed)
                    {
                        // update the analyzer start position and force a restart if it
                        // is currently running.  this handles the common case where the
                        // analyzer is active while the user seeks; without restarting the
                        // internal ffmpeg process it will continue decoding from the old
                        // offset and the spectrum will remain stuck until something else
                        // (eg. pause/play) restarts it.
                        _spectrumAnalyzer.SetStartPosition(pos, true);
                    }

                    // clear the seeking flag before attempting to start the analyzer so
                    // subsequent events (e.g. Playing) are not suppressed.
                    _isSeeking = false;

                    // make sure the analyzer is running now that we've left the seek
                    // state.  CheckAndStartFfmpegTasks handles the usual enable/playing
                    // logic (and also restarts waveform if required).
                    CheckAndStartFfmpegTasks();
                }
                catch (OperationCanceledException) { }
            });
        }

    /// <summary>
    /// Temporarily stops playback so the caller can perform editing operations.
    /// Returns the current position and playing state so the operation can be
    /// resumed later.  The spectrum analyzer and waveform generator are halted
    /// and any running FFmpeg helper processes are killed to avoid resource leaks.
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
        // enqueue loadfile without blocking the mpv thread; we'll wait for the
        // ``IsLoadingMedia`` flag later which is updated via property events.
        PostToMpvThread(() => _ = ExecuteCommandAsync(new[] { "loadfile", ToMpvLoadTarget(path) }));

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
    public void Play() { if (!_disposed) InvokeOnMpvThread(() => { SetProperty("pause", false); return true; }); IsPlaying = true; }

    /// <summary>
    /// Pause playback.
    /// </summary>
    public void Pause() { if (!_disposed) InvokeOnMpvThread(() => { SetProperty("pause", true); return true; }); IsPlaying = false; }

    /// <summary>
    /// Stop playback and reset state.
    /// </summary>
    public void Stop()
    {
        if (!_disposed)
        {
            PostToMpvThread(() => _ = ExecuteCommandAsync(new[] { "stop" }));
        }

        // make sure the spectrum analyzer is halted when playback is stopped so
        // it can later be restarted cleanly.
        _spectrumAnalyzer?.Stop();

        InternalStop();
        Duration = 0;
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
        IsPlaying = false;
        Position = 0;
        ClearMedia();
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
        try { _spectrumAnalyzer.Stop(); }
        catch (Exception ex) { Log.Warn("Failed to stop spectrum analyzer", ex); }
        try { _spectrumAnalyzer.SetPath(""); }
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

        try
        {
            // Stop the mpv worker thread
            _mpvQueue.CompleteAdding();
            try { _mpvThread?.Join(250); } catch { }
        }
        catch (Exception ex)
        {
            Log.Warn("Error while shutting down mpv worker thread", ex);
        }
    }

    private static string ToMpvLoadTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        // Use a file URI for local files so non-ASCII characters are encoded
        // and can be passed to mpv consistently across platforms.
        if (Path.IsPathRooted(path))
        {
            try { return new Uri(path).AbsoluteUri; }
            catch { return path; }
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                try { return uri.AbsoluteUri; }
                catch { return path; }
            }
        }

        return path;
    }
}
