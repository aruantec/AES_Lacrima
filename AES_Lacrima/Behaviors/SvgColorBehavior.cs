using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Controls.Behaviors
{
    /// <summary>
    /// Behavior that loads an SVG resource, applies a tint and optional
    /// per-color mappings, and sets the processed SVG on the associated control.
    ///
    /// The behavior supports multiple source formats (avares://, leading '/'
    /// resource paths, absolute/relative file paths and embedded resources).
    /// It caches raw SVG text to avoid repeated IO, deduplicates concurrent
    /// loads for the same source, and writes temporary files when a control
    /// requires a file path. Processing is performed off the UI thread and
    /// the final result is applied on the UI thread.
    /// </summary>
    public class SvgColorBehavior : Behavior<Control>
    {
        private static readonly ConcurrentDictionary<string, string?> SvgTextCache = new();
        private static readonly ConcurrentDictionary<string, DateTime> FailedCache = new();

        /// <summary>
        /// Path or resource identifier for the SVG to load. Supports
        /// avares:// URIs, leading slash resource paths ("/Assets/..."),
        /// file system paths and other lookup heuristics implemented in
        /// <see cref="LoadSvgText(string)"/>.
        /// </summary>
        public static readonly StyledProperty<string?> SourceProperty =
            AvaloniaProperty.Register<SvgColorBehavior, string?>(nameof(Source));

        /// <summary>
        /// The current source value.
        /// </summary>
        public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

        /// <summary>
        /// Global tint color that will replace fill/stroke values and the
        /// CSS keyword <c>currentColor</c> in the loaded SVG text.
        /// </summary>
        public static readonly StyledProperty<Color> TintProperty =
            AvaloniaProperty.Register<SvgColorBehavior, Color>(nameof(Tint));

        /// <summary>
        /// The current tint color.
        /// </summary>
        public Color Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }

        /// <summary>
        /// Optional mapping of color tokens or literal color strings to
        /// replacement values. Each key/value pair is applied using a
        /// case-insensitive Regex.Replace over the raw SVG text before tinting.
        /// </summary>
        public static readonly StyledProperty<Dictionary<string, string>?> ColorMapProperty =
            AvaloniaProperty.Register<SvgColorBehavior, Dictionary<string, string>?>(nameof(ColorMap));

        /// <summary>
        /// The current color map used for multi-color replacements.
        /// </summary>
        public Dictionary<string, string>? ColorMap { get => GetValue(ColorMapProperty); set => SetValue(ColorMapProperty, value); }

        private readonly Regex _fillRegex = new("(fill\\s*=\\s*\\\")(#?[0-9a-fA-F]{3,8})(\\\")", RegexOptions.Compiled);
        private readonly Regex _strokeRegex = new("(stroke\\s*=\\s*\\\")(#?[0-9a-fA-F]{3,8})(\\\")", RegexOptions.Compiled);

        private string? _lastTempPath;
        // Tracks in-flight load operations so concurrent requests for the same source share work
        private static readonly ConcurrentDictionary<string, Task<string?>> _inflightLoads = new();

        // Debounce helper state to combine rapid property notifications into a single ApplyToTarget call
        private readonly object _debounceLock = new();
        private CancellationTokenSource? _debounceCts;
        private const int DebounceDelayMs = 40;

        protected override void OnAttached()
        {
            base.OnAttached();
            this.GetObservable(SourceProperty).Subscribe(new SimpleObserver<string?>( _ => ScheduleApply()));
            this.GetObservable(TintProperty).Subscribe(new SimpleObserver<Color>( _ => ScheduleApply()));
            this.GetObservable(ColorMapProperty).Subscribe(new SimpleObserver<Dictionary<string, string>?>( _ => ScheduleApply()));

            if (AssociatedObject != null)
            {
                TryObserveTargetPathProperty();
            }
            // Apply shortly after attachment to allow bindings on the target to initialize
            // Schedule initial apply via the debounced path so we only run once after bindings settle
            try
            {
                // Schedule initial apply after bindings have settled
                ScheduleApply();
            }
            catch { }
        }

        private void ScheduleApply()
        {
            lock (_debounceLock)
            {
                try { _debounceCts?.Cancel(); } catch { }
                _debounceCts = new CancellationTokenSource();
                var ct = _debounceCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(DebounceDelayMs, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;
                        await Dispatcher.UIThread.InvokeAsync(() => ApplyToTarget());
                    }
                    catch (TaskCanceledException) { }
                    catch { }
                }, ct);
            }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            // Try to remove temp file if created
            TryRemoveTemp();
        }

        private void TryObserveTargetPathProperty()
        {
            try
            {
                if (AssociatedObject == null) return;
                // Common property names used by Svg controls: Path, Source
                var prop = AssociatedObject.GetType().GetProperty("Path") ?? AssociatedObject.GetType().GetProperty("Source");
                if (prop != null && prop.PropertyType == typeof(string))
                {
                    // No straightforward way to subscribe to arbitrary CLR property changes; require user to bind Source on behavior
                }
            }
            catch { }
        }

        /// <summary>
        /// Main processing routine. Loads the raw SVG text (from cache or
        /// disk/resources), applies the ColorMap and Tint, and then sets the
        /// resulting content on the associated control. Heavy work is run on
        /// a background thread and the final assignment occurs on the UI
        /// thread.
        /// </summary>
        private void ApplyToTarget()
        {
            try
            {
                if (AssociatedObject == null) return;

                // Capture current values quickly to avoid accessing styled properties on background thread
                var associated = AssociatedObject;
                string? effectiveSource = Source;
                if (string.IsNullOrEmpty(effectiveSource))
                {
                    var tmpType = associated.GetType();
                    var propStr = tmpType.GetProperty("Path") ?? tmpType.GetProperty("Source");
                    if (propStr != null && propStr.PropertyType == typeof(string))
                        effectiveSource = propStr.GetValue(associated) as string;
                }

                if (string.IsNullOrEmpty(effectiveSource)) return;

                var tintCopy = Tint;
                var colorMapCopy = ColorMap == null ? null : new Dictionary<string, string>(ColorMap);

                // (diagnostic logging removed)

                // Run the potentially expensive SVG loading + text processing off the UI thread
                Task.Run(async () => {
                    var swTotal = Stopwatch.StartNew();
                    long loadMs = 0;
                    long processMs = 0;
                    try
                    {
                        // Check caches first (cache stores raw SVG text to avoid repeated IO)
                        string? cachedRaw = null;
                        if (SvgTextCache.TryGetValue(effectiveSource, out var cached) && cached != null)
                        {
                            cachedRaw = cached;
                        }

                        // If we've recently failed to resolve this source, skip reattempt for short period
                        if (FailedCache.TryGetValue(effectiveSource, out var lastFailed) && (DateTime.UtcNow - lastFailed).TotalSeconds < 10)
                        {
                            return;
                        }

                        string? svgText = null;
                        var sw = Stopwatch.StartNew();
                        if (cachedRaw != null)
                        {
                            svgText = cachedRaw;
                        }
                        else
                        {
                            // Use in-flight dedupe so concurrent requests share work
                            var loadTask = _inflightLoads.GetOrAdd(effectiveSource, _ => Task.Run(() => LoadSvgText(effectiveSource)));
                            try
                            {
                                svgText = await loadTask.ConfigureAwait(false);
                            }
                            catch
                            {
                                // propagate error after marking failed
                                FailedCache[effectiveSource] = DateTime.UtcNow;
                                throw;
                            }
                            finally
                            {
                                // Remove the task from inflight map if it's the same instance
                                _inflightLoads.TryGetValue(effectiveSource, out var existing);
                                if (existing == loadTask)
                                    _inflightLoads.TryRemove(effectiveSource, out _);
                            }
                        }
                        sw.Stop(); loadMs = sw.ElapsedMilliseconds;
                        if (svgText == null)
                        {
                            Debug.WriteLine($"SvgColorBehavior: failed to load svg text for '{effectiveSource}' on control {associated?.GetType().FullName}");
                            FailedCache[effectiveSource] = DateTime.UtcNow;
                            return;
                        }

                        // Store raw svg text in cache if it wasn't cached already
                        try { SvgTextCache[effectiveSource] = svgText; } catch { }

                        // Apply multi-color mapping first
                        var swProc = Stopwatch.StartNew();
                        if (colorMapCopy != null)
                        {
                            foreach (var kv in colorMapCopy)
                            {
                                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                                svgText = Regex.Replace(svgText, Regex.Escape(kv.Key), kv.Value, RegexOptions.IgnoreCase);
                            }
                        }

                        // Apply global tint
                        var tintHex = $"#{tintCopy.R:X2}{tintCopy.G:X2}{tintCopy.B:X2}";
                        svgText = svgText.Replace("currentColor", tintHex, StringComparison.OrdinalIgnoreCase);
                        svgText = _fillRegex.Replace(svgText, "$1" + tintHex + "$3");
                        svgText = _strokeRegex.Replace(svgText, "$1" + tintHex + "$3");

                        swProc.Stop(); processMs = swProc.ElapsedMilliseconds;

                        // After processing, set on UI thread
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                var controlType = associated.GetType();

                                // Prefer a Stream 'Source' property
                                var propStream = controlType.GetProperty("Source");
                                if (propStream != null && propStream.PropertyType == typeof(Stream))
                                {
                                    var bytes = Encoding.UTF8.GetBytes(svgText);
                                    var ms = new MemoryStream(bytes);
                                    propStream.SetValue(associated, ms);
                                    return;
                                }

                                // If control accepts a string Source/Path, write to temporary file and set path
                                var propString = controlType.GetProperty("Path") ?? controlType.GetProperty("Source");
                                if (propString != null && propString.PropertyType == typeof(string))
                                {
                                    TryRemoveTemp();
                                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aes_svg_{Guid.NewGuid():N}.svg");
                                    File.WriteAllText(temp, svgText, Encoding.UTF8);
                                    _lastTempPath = temp;
                                    propString.SetValue(associated, temp);
                                    return;
                                }

                                // Fallback: try method Load(Stream)
                                var m = controlType.GetMethod("Load") ?? controlType.GetMethod("SetSource");
                                if (m != null)
                                {
                                    using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(svgText));
                                    m.Invoke(associated, new object[] { ms2 });
                                    return;
                                }

                                // Last resort: set property named Svg or SvgSource
                                var p2 = controlType.GetProperty("Svg") ?? controlType.GetProperty("SvgSource");
                                if (p2 != null && p2.PropertyType == typeof(string))
                                {
                                    p2.SetValue(associated, svgText);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"SvgColorBehavior: failed to set processed svg on UI thread: {ex}");
                            }

                            swTotal.Stop();
                            try
                            {
                                var dispatchMs = swTotal.ElapsedMilliseconds - loadMs - processMs;
                                Debug.WriteLine($"SvgColorBehavior: timings source='{effectiveSource}' load={loadMs}ms process={processMs}ms dispatch={dispatchMs}ms total={swTotal.ElapsedMilliseconds}ms");
                            }
                            catch { }
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"SvgColorBehavior: processing failed: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SvgColorBehavior: apply failed: {ex}");
            }
        }

        /// <summary>
        /// Resolve the SVG text for the given source string. Supports:
        /// - avares:// URIs
        /// - leading-slash resource paths (attempts avares, disk, manifest resources)
        /// - relative/absolute file paths and fallback lookup heuristics
        /// The method returns the SVG file contents or <c>null</c> when not found.
        /// </summary>
        private string? LoadSvgText(string source)
        {
            try
            {
                // (diagnostic logging removed)
                if (source.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var stream = AssetLoader.Open(new Uri(source));
                        using var reader = new StreamReader(stream);
                        return reader.ReadToEnd();
                    }
                    catch
                    {
                        // fallback to other resolution attempts below
                    }
                }

                // Support leading-slash resource paths like "/Assets/..." used in XAML by some controls.
                if (source.StartsWith("/"))
                {
                    // First, try an avares:// lookup against likely assemblies in order:
                    // 1) the assembly that defines this behavior (most assets for controls live here)
                    // 2) the AssociatedObject's assembly
                    // 3) the entry assembly
                    try
                    {
                        var tried = new List<string>();
                        var asmCandidates = new[] {
                            typeof(SvgColorBehavior).Assembly.GetName().Name,
                            AssociatedObject?.GetType().Assembly.GetName().Name,
                            Assembly.GetEntryAssembly()?.GetName().Name
                        };
                        foreach (var asmName in asmCandidates)
                        {
                            if (string.IsNullOrEmpty(asmName) || tried.Contains(asmName)) continue;
                            tried.Add(asmName);
                            var avares = $"avares://{asmName}{source}";
                            try
                            {
                                using var stream = AssetLoader.Open(new Uri(avares));
                                using var reader = new StreamReader(stream);
                                return reader.ReadToEnd();
                            }
                            catch { }
                        }
                    }
                    catch { }

                    // Try app base directory first
                    var disk = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, source.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    Debug.WriteLine($"SvgColorBehavior: trying disk path '{disk}'");
                    if (File.Exists(disk)) return File.ReadAllText(disk);

                    // Try manifest resource lookup across loaded assemblies
                    var resourceName = source.TrimStart('/').Replace('/', '.');
                    var asmList = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var a in asmList)
                    {
                        try
                        {
                            // Common packaging uses assembly default namespace prefix -- try both with and without
                            var candidate1 = a.GetName().Name + "." + resourceName;
                            Debug.WriteLine($"SvgColorBehavior: trying manifest resource '{candidate1}' in assembly {a.GetName().Name}");
                            using var s1 = a.GetManifestResourceStream(candidate1);
                            if (s1 != null) { using var sr = new StreamReader(s1); return sr.ReadToEnd(); }

                            Debug.WriteLine($"SvgColorBehavior: trying manifest resource '{resourceName}' in assembly {a.GetName().Name}");
                            using var s2 = a.GetManifestResourceStream(resourceName);
                            if (s2 != null) { using var sr2 = new StreamReader(s2); return sr2.ReadToEnd(); }
                        }
                        catch { }
                    }
                }

                // Try a few likely absolute/relative file locations
                var candidates = new List<string>
                {
                    source,
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, source.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)),
                    // Also consider the application publish location / runtime assets folder
                    Path.Combine(AppContext.BaseDirectory, source.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)),
                };

                var entry = System.Reflection.Assembly.GetEntryAssembly();
                if (entry != null)
                {
                    var dir = Path.GetDirectoryName(entry.Location);
                    if (!string.IsNullOrEmpty(dir))
                        candidates.Add(Path.Combine(dir, source.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
                }

                foreach (var c in candidates.Distinct())
                {
                    try
                    {
                        Debug.WriteLine($"SvgColorBehavior: checking candidate path '{c}'");
                        if (File.Exists(c)) return File.ReadAllText(c);
                    }
                    catch { }
                }

                // As a last resort, enumerate assemblies and log any resource names that contain the file name
                var fileNameOnly = Path.GetFileName(source);
                // Also attempt to look for the file in the project's Assets folder relative to the solution/workspace root
                try
                {
                    var repoRoot = AppContext.BaseDirectory; // best-effort; additional heuristics could be added
                    var workspaceCandidate = Path.Combine(repoRoot, "Assets", "Player", fileNameOnly);
                    if (File.Exists(workspaceCandidate)) return File.ReadAllText(workspaceCandidate);
                }
                catch { }
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var names = a.GetManifestResourceNames();
                        foreach (var n in names)
                        {
                            if (n.EndsWith(fileNameOnly, StringComparison.OrdinalIgnoreCase))
                            {
                                Debug.WriteLine($"SvgColorBehavior: found embedded resource '{n}' in assembly {a.GetName().Name}");
                                using var s = a.GetManifestResourceStream(n);
                                if (s != null) { using var sr = new StreamReader(s); return sr.ReadToEnd(); }
                            }
                        }
                    }
                    catch { }
                }

                return null;
            }
            catch { return null; }
        }

        private void TryRemoveTemp()
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastTempPath) && File.Exists(_lastTempPath))
                {
                    File.Delete(_lastTempPath);
                }
            }
            catch { }
            finally { _lastTempPath = null; }
        }
    }
}
