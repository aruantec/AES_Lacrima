using AES_Controls.Helpers;
using AES_Controls.Player;
using Avalonia.Collections;
using MathNet.Numerics.IntegralTransforms;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace AES_Controls.Players
{
    /// <summary>
    /// Performs continuous spectrum analysis of an audio source using a
    /// short-time Fourier transform performed by MathNet.Numerics.
    /// The analyzer spawns an FFmpeg process to decode audio into raw samples
    /// and publishes throttled spectrum data back to the <see cref="AudioPlayer"/>.
    /// </summary>
    public class FfMpegSpectrumAnalyzer
    {
        private readonly AvaloniaList<double> _spectrum;
        private readonly AudioPlayer _player;
        private readonly FFmpegManager? _ffmpegManager;
        private string? _path;
        private Process? _ffmpegProcess;
        private CancellationTokenSource? _cts;
        private Task? _analysisTask;
        private double _processedSeconds;
        private double[] _currentValues = new double[256];

        /// <summary>
        /// Creates a new spectrum analyzer instance that will push results into
        /// the provided <paramref name="spectrum"/> collection and observe the
        /// given <paramref name="player"/> for playback state.
        /// </summary>
        /// <param name="spectrum">Collection to receive spectrum values.</param>
        /// <param name="player">AudioPlayer used to determine playback state and timing.</param>
        /// <param name="ffmpegManager">Manager to report activity status.</param>
        public FfMpegSpectrumAnalyzer(AvaloniaList<double> spectrum, AudioPlayer player, FFmpegManager? ffmpegManager = null)
        {
            _spectrum = spectrum;
            _player = player;
            _ffmpegManager = ffmpegManager;
            _processedSeconds = 0.0;
        }

        /// <summary>
        /// Set the media path or URL to analyse. This will stop any running
        /// analysis and reset internal timing so analysis starts from the
        /// beginning of <paramref name="path"/> when restarted.
        /// </summary>
        /// <param name="path">Local path or URL of the media to analyse.</param>
        public void SetPath(string path)
        {
            Stop();
            _path = path;
            _processedSeconds = 0;
            // Clear current values so we don't see ghosts of the previous track
            Array.Clear(_currentValues, 0, _currentValues.Length);
        }

        /// <summary>
        /// Update the starting position (in seconds) used when launching the
        /// FFmpeg analysis process. Optionally restarts the analysis if it is running.
        /// </summary>
        /// <param name="seconds">Start position in seconds.</param>
        /// <param name="restartIfRunning">If true, stop and start the analyzer when running.</param>
        public void SetStartPosition(double seconds, bool restartIfRunning = false)
        {
            _processedSeconds = Math.Max(0.0, seconds);
            if (restartIfRunning)
            {
                Stop();
                Start();
            }
        }

        /// <summary>
        /// Starts background analysis if the player is enabled for spectrum
        /// analysis and a path has been configured. This method is safe to
        /// call multiple times; only one analysis task will run concurrently.
        /// </summary>
        public void Start()
        {
            if (!_player.EnableSpectrum || string.IsNullOrEmpty(_path)) return;

            if (_analysisTask != null && (_analysisTask.IsCompleted || _analysisTask.IsFaulted || _analysisTask.IsCanceled))
            {
                _analysisTask = null;
            }

            if (_analysisTask != null) return;

            _cts?.Cancel();
            _cts?.Dispose();

            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            _ffmpegManager?.ReportActivity(true);

            _analysisTask = Task.Run(() => {
                try { AnalyzeSpectrum(token); }
                finally { _ffmpegManager?.ReportActivity(false); }
            }, token);
        }

        /// <summary>
        /// Stops any running analysis, kills the FFmpeg helper process and
        /// clears the visual spectrum data.
        /// </summary>
        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }
            _cts = null;

            // Clear visual data
            Array.Clear(_currentValues, 0, _currentValues.Length);
            _player.UpdateSpectrumThrottled(_currentValues);

            try
            {
                var proc = _ffmpegProcess;
                if (proc != null)
                {
                    if (!proc.HasExited)
                    {
                        try { proc.Kill(true); } catch { }
                        try { proc.WaitForExit(100); } catch { }
                    }
                    try { proc.Dispose(); } catch { }
                }
            }
            catch { }
            finally { _ffmpegProcess = null; }

            _analysisTask = null;
        }

        /// <summary>
        /// Core analysis loop executed on a background thread. Launches FFmpeg
        /// to emit raw float samples, performs an FFT and updates the cached
        /// spectrum magnitudes which are then pushed to the player.
        /// </summary>
        /// <param name="token">Cancellation token to stop the loop.</param>
        private void AnalyzeSpectrum(CancellationToken token)
        {
            const int fftLength = 1024;
            const int sampleRate = 44100;
            var complexBuffer = new Complex[fftLength];
            var byteBuffer = new byte[fftLength * 4];

            while (!token.IsCancellationRequested)
            {
                Process? localProcess = null;
                try
                {
                    bool isStream = _path?.StartsWith("http") == true;
                    string networkFlags = isStream ? "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 " : "";
                    string ssArg = _processedSeconds > 0 ? $"-ss {_processedSeconds.ToString(CultureInfo.InvariantCulture)} " : "";

                    var ffmpegPath = FFmpegLocator.FindFFmpegPath();

                    // Optimized FFmpeg arguments for low latency and fast probing
                    var args = $"{networkFlags}-nostats -hide_banner -loglevel error -fflags +nobuffer -probesize 32768 -analyzeduration 100000 {ssArg}-i \"{_path}\" -vn -sn -dn -ac 1 -ar {sampleRate} -f f32le -";

                    localProcess = Process.Start(new ProcessStartInfo(ffmpegPath!, args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = false, 
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (localProcess == null)
                    {
                        try { Task.Delay(1000, token).Wait(token); } catch { break; }
                        continue;
                    }
                    _ffmpegProcess = localProcess;

                    using var stream = localProcess.StandardOutput.BaseStream;

                    while (!token.IsCancellationRequested && !localProcess.HasExited)
                    {
                        // Check player states that should pause spectrum analysis
                        if (!_player.IsPlaying || _player.IsSeeking)
                        {
                            if (FadeOutSpectrum())
                            {
                                _player.UpdateSpectrumThrottled(_currentValues);
                            }
                            try { Task.Delay(30, token).Wait(token); } catch { break; }
                            continue;
                        }

                        if (_player.IsBuffering)
                        {
                            try { Task.Delay(50, token).Wait(token); } catch { break; }
                            continue;
                        }

                        double currentPos = _player.Position;
                        double drift = _processedSeconds - currentPos;

                        // Reset logic: if we are far off (e.g. after long pause or seek that didn't stop analyzer)
                        // restart FFmpeg to catch up immediately.
                        if (Math.Abs(drift) > 1.5)
                        {
                            _processedSeconds = currentPos;
                            try { localProcess.Kill(true); } catch { }
                            break; 
                        }

                        if (drift > 0.1)
                        {
                            try { Task.Delay(10, token).Wait(token); } catch (OperationCanceledException) { break; }
                            continue;
                        }

                        if (drift < -0.2)
                        {
                            try
                            {
                                int n = stream.Read(byteBuffer.AsSpan(0, byteBuffer.Length));
                                if (n <= 0) break;
                            }
                            catch { break; }
                            _processedSeconds += (double)fftLength / sampleRate;
                            continue;
                        }

                        int bytesReadTotal = 0;
                        while (bytesReadTotal < byteBuffer.Length && !token.IsCancellationRequested)
                        {
                            int n = 0;
                            try { n = stream.Read(byteBuffer.AsSpan(bytesReadTotal, byteBuffer.Length - bytesReadTotal)); }
                            catch (IOException) { break; }
                            if (n <= 0) break;
                            bytesReadTotal += n;
                        }

                        if (bytesReadTotal < byteBuffer.Length) break;

                        for (int i = 0; i < fftLength; i++)
                        {
                            float sample = BitConverter.ToSingle(byteBuffer, i * 4);
                            complexBuffer[i] = new Complex(sample, 0);
                        }

                        Fourier.Forward(complexBuffer, FourierOptions.NoScaling);

                        for (int i = 0; i < 256; i++)
                        {
                            double mag = complexBuffer[i].Magnitude;
                            _currentValues[i] = Math.Log10(mag + 1) * 20;
                        }

                        _player.UpdateSpectrumThrottled(_currentValues);
                        _processedSeconds += (double)fftLength / sampleRate;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Debug.WriteLine($"[SpectrumAnalyzer] Error: {ex.Message}");
                    try { Task.Delay(1000, token).Wait(token); } catch (OperationCanceledException) { break; }
                }
                finally
                {
                    try { localProcess?.Kill(true); } catch { }
                    try { localProcess?.Dispose(); } catch { }
                    if (ReferenceEquals(_ffmpegProcess, localProcess)) _ffmpegProcess = null;
                    
                    if (!token.IsCancellationRequested)
                        try { Task.Delay(200, token).Wait(token); } catch { }
                }
            }
        }

        /// <summary>
        /// Applies a smooth decay to the current spectrum values to produce a
        /// natural fade-out when playback pauses or stops.
        /// </summary>
        /// <returns>True when any value was modified.</returns>
        private bool FadeOutSpectrum()
        {
            bool modified = false;
            // Use time-based decay for smoothness regardless of loop frequency
            const double decayFactor = 0.86; 
            const double floorThreshold = 0.1;

            for (int i = 0; i < _currentValues.Length; i++)
            {
                if (_currentValues[i] > 0.01)
                {
                    _currentValues[i] *= decayFactor;
                    
                    if (_currentValues[i] < floorThreshold)
                    {
                        _currentValues[i] = 0;
                    }
                    modified = true;
                }
            }
            return modified;
        }
    }
}