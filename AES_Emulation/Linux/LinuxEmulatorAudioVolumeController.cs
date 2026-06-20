using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Adjusts PipeWire/PulseAudio sink-input volume for the active emulator in a gamescope session.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxEmulatorAudioVolumeController : IDisposable
{
    private const int PulseVolumeNominal = 65536;

    private static readonly ILog Log = LogHelper.For<LinuxEmulatorAudioVolumeController>();
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string[] AudioEnvironmentKeys =
    [
        "PATH",
        "XDG_RUNTIME_DIR",
        "PULSE_RUNTIME_PATH",
        "PULSE_SERVER",
        "DBUS_SESSION_BUS_ADDRESS",
        "WAYLAND_DISPLAY",
        "DISPLAY",
        "HOME",
        "USER",
    ];

    private static readonly HashSet<string> IgnoredApplicationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "plasmashell",
        "brave",
        "brave-browser",
        "chrome",
        "chromium",
        "firefox",
        "spotify",
        "discord",
        "obs",
        "vlc",
        "system sounds",
    };

    private int _launchedCompositorPid;
    private int _outerLaunchedPid;
    private readonly HashSet<int> _seedPids = new();
    private readonly HashSet<int> _candidatePids = new();
    private readonly HashSet<string> _processNameHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _baselineSinkSerials = new();
    private float _volume = 1.0f;
    private bool _disposed;
    private bool _pushPending;
    private Timer? _syncTimer;
    private int _activeSinkInputIndex;
    private DateTime _lastUserPushUtc = DateTime.MinValue;
    private DateTime _lastNoMatchLogUtc = DateTime.MinValue;
    private static readonly TimeSpan SyncGracePeriod = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan NoMatchLogInterval = TimeSpan.FromSeconds(5);

    public bool IsAttached => _launchedCompositorPid > 0;

    public event Action<float>? SystemVolumeChanged;

    public void Attach(int launchedCompositorPid, IEnumerable<int>? additionalPids = null, IEnumerable<string>? audioNameHints = null)
    {
        Detach();
        if (launchedCompositorPid <= 0)
            return;

        _outerLaunchedPid = launchedCompositorPid;
        _launchedCompositorPid = LinuxCompositorProcessHelper.ResolveCompositorRootPid(launchedCompositorPid);
        if (_launchedCompositorPid <= 0)
            _launchedCompositorPid = launchedCompositorPid;

        _seedPids.Clear();
        _seedPids.Add(_outerLaunchedPid);
        if (_launchedCompositorPid != _outerLaunchedPid)
            _seedPids.Add(_launchedCompositorPid);

        if (additionalPids != null)
        {
            foreach (var pid in additionalPids)
            {
                if (pid > 0)
                    _seedPids.Add(pid);
            }
        }

        AddAudioNameHints(audioNameHints);
        CaptureBaselineSinkSerials();
        RebuildCandidateState();

        Log.Info(
            $"EmulatorVolume: attached outerPid={_outerLaunchedPid}, compositorRoot={_launchedCompositorPid}, " +
            $"candidates={_candidatePids.Count}, hints=[{string.Join(", ", _processNameHints)}].");

        _pushPending = true;
        StartSyncTimer();
        TrySyncAndApply(forcePush: true);
    }

    public void Detach()
    {
        StopSyncTimer();
        _launchedCompositorPid = 0;
        _outerLaunchedPid = 0;
        _seedPids.Clear();
        _candidatePids.Clear();
        _processNameHints.Clear();
        _baselineSinkSerials.Clear();
        _activeSinkInputIndex = 0;
        _pushPending = false;
        _lastNoMatchLogUtc = DateTime.MinValue;
    }

    public void EnsureSession()
    {
        if (_launchedCompositorPid <= 0)
            return;

        _pushPending = true;
        TrySyncAndApply(forcePush: true);
    }

    /// <summary>
    /// Expands PID/hint matching without resetting the attach baseline. Use after capture is live
    /// or once the runtime emulator process appears so we do not treat game audio as pre-existing.
    /// </summary>
    public void RefreshSessionTargets(IEnumerable<int>? additionalPids = null, IEnumerable<string>? audioNameHints = null)
    {
        if (_launchedCompositorPid <= 0)
            return;

        if (additionalPids != null)
        {
            foreach (var pid in additionalPids)
            {
                if (pid > 0)
                    _seedPids.Add(pid);
            }
        }

        AddAudioNameHints(audioNameHints);
        RebuildCandidateState();
        _pushPending = true;

        Log.Info(
            $"EmulatorVolume: refreshed session targets (candidates={_candidatePids.Count}, " +
            $"hints=[{string.Join(", ", _processNameHints)}]).");

        TrySyncAndApply(forcePush: true);
    }

    public float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0f, 1.0f);
            _volume = clamped;
            _pushPending = true;
            TrySyncAndApply(forcePush: true);
        }
    }

    private void StartSyncTimer()
    {
        StopSyncTimer();
        _syncTimer = new Timer(_ => TrySyncAndApply(forcePush: false), null, SyncInterval, SyncInterval);
    }

    private void StopSyncTimer()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
    }

    private void TrySyncAndApply(bool forcePush)
    {
        if (_launchedCompositorPid <= 0)
            return;

        RebuildCandidateState();

        if (!TryResolveActiveSinkInputs(out var sinkInputs))
        {
            if (_pushPending && DateTime.UtcNow - _lastNoMatchLogUtc >= NoMatchLogInterval)
            {
                _lastNoMatchLogUtc = DateTime.UtcNow;
                Log.Info(
                    $"EmulatorVolume: no sink input matched yet (candidates={_candidatePids.Count}, " +
                    $"hints=[{string.Join(", ", _processNameHints)}]).");
            }

            return;
        }

        if (_pushPending || forcePush)
        {
            var appliedAny = false;
            foreach (var sink in sinkInputs)
            {
                if (!TryApplyVolumeToSink(sink.Index, _volume))
                    continue;

                appliedAny = true;
                _activeSinkInputIndex = sink.Index;
            }

            if (appliedAny)
            {
                _pushPending = false;
                _lastUserPushUtc = DateTime.UtcNow;
                Log.Info(
                    $"EmulatorVolume: pushed {FormatVolumePercent(_volume)} to {sinkInputs.Count} sink-input(s) " +
                    $"(indexes=[{string.Join(", ", sinkInputs.Select(static s => s.Index))}]).");
            }
            else if (sinkInputs.Count > 0)
            {
                Log.Warn(
                    $"EmulatorVolume: failed to push {FormatVolumePercent(_volume)} to matched sink-input(s).");
            }

            return;
        }

        var primary = sinkInputs[0];
        _activeSinkInputIndex = primary.Index;

        if (DateTime.UtcNow - _lastUserPushUtc < SyncGracePeriod)
            return;

        var systemVolume = primary.Muted ? 0.0f : primary.VolumeNormalized;
        if (Math.Abs(systemVolume - _volume) > 0.01f)
        {
            _volume = systemVolume;
            SystemVolumeChanged?.Invoke(_volume);
        }
    }

    private bool TryApplyVolumeToSink(int sinkIndex, float normalizedVolume)
        => LinuxPipeWireAudioBackend.TryApplyStreamVolume(sinkIndex, normalizedVolume);

    private static string FormatVolumePercent(float normalizedVolume)
        => $"{Math.Round(normalizedVolume * 100.0, 1).ToString(CultureInfo.InvariantCulture)}%";

    private void AddAudioNameHints(IEnumerable<string>? audioNameHints)
    {
        if (audioNameHints == null)
            return;

        foreach (var hint in audioNameHints)
            AddProcessNameHintToken(hint);
    }

    private void AddProcessNameHintToken(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return;

        foreach (var token in rawValue.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 2)
                continue;

            _processNameHints.Add(token.ToLowerInvariant());
        }
    }

    private void CaptureBaselineSinkSerials()
    {
        _baselineSinkSerials.Clear();
        if (!LinuxPipeWireAudioBackend.TryListSinkInputsJson(out var output) || string.IsNullOrWhiteSpace(output))
            return;

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("properties", out var properties))
                    continue;

                var serial = TryGetObjectSerial(properties);
                if (serial > 0)
                    _baselineSinkSerials.Add(serial);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("EmulatorVolume: failed to capture baseline sink serials.", ex);
        }
    }

    private void RebuildCandidateState()
    {
        _candidatePids.Clear();
        _processNameHints.RemoveWhere(static hint => hint is "gamescope" or "gamescopereaper");

        LinuxCompositorProcessHelper.CollectSessionProcessTrees(_seedPids, _candidatePids, _processNameHints);

        var primaryEmulatorPid = LinuxCompositorProcessHelper.FindPrimaryEmulatorPid(_launchedCompositorPid);
        if (primaryEmulatorPid > 0)
        {
            _candidatePids.Add(primaryEmulatorPid);
            LinuxCompositorProcessHelper.CollectProcessIdentityHints(primaryEmulatorPid, _processNameHints);
        }

        if (_outerLaunchedPid > 0)
            _candidatePids.Add(_outerLaunchedPid);

        if (_launchedCompositorPid > 0)
            _candidatePids.Add(_launchedCompositorPid);
    }

    private readonly record struct MatchedSinkInput(
        int Index,
        float VolumeNormalized,
        bool Muted,
        bool IsCompositorStream,
        bool IsNewSinceAttach,
        int MatchScore,
        long Serial);

    private bool TryResolveActiveSinkInputs(out List<MatchedSinkInput> sinkInputs)
    {
        sinkInputs = new List<MatchedSinkInput>();

        if (!LinuxPipeWireAudioBackend.TryListSinkInputsJson(out var output) || string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("index", out var indexElement) ||
                    indexElement.ValueKind != JsonValueKind.Number)
                    continue;

                if (!element.TryGetProperty("properties", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryMatchSinkInput(properties, out var matchScore))
                    continue;

                var serial = TryGetObjectSerial(properties);
                sinkInputs.Add(new MatchedSinkInput(
                    indexElement.GetInt32(),
                    TryReadVolumeNormalized(element),
                    element.TryGetProperty("mute", out var muteElement) &&
                    muteElement.ValueKind == JsonValueKind.True,
                    IsLikelyCompositorStream(properties),
                    serial > 0 && !_baselineSinkSerials.Contains(serial),
                    matchScore,
                    serial));
            }

            if (sinkInputs.Count == 0)
                TryCollectFallbackSessionSinks(document.RootElement, sinkInputs);

            if (sinkInputs.Count == 0)
                return false;

            sinkInputs.Sort(static (left, right) =>
            {
                if (left.MatchScore != right.MatchScore)
                    return right.MatchScore.CompareTo(left.MatchScore);

                if (left.IsNewSinceAttach != right.IsNewSinceAttach)
                    return left.IsNewSinceAttach ? -1 : 1;

                if (left.IsCompositorStream != right.IsCompositorStream)
                    return left.IsCompositorStream ? 1 : -1;

                return left.Serial.CompareTo(right.Serial);
            });

            sinkInputs.Reverse();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("EmulatorVolume: failed to parse pactl sink-input JSON.", ex);
            return false;
        }
    }

    private bool TryMatchSinkInput(JsonElement properties, out int matchScore)
    {
        matchScore = 0;
        TryGetProcessId(properties, out var pid);

        if (TryGetPropertyString(properties, "application.name", out var applicationName) &&
            IsIgnoredApplicationName(applicationName))
        {
            return false;
        }

        if (pid > 0 && IsCandidateProcessId(pid))
            matchScore = Math.Max(matchScore, 100);

        if (MatchesProcessNameHints(properties, out var hintScore))
            matchScore = Math.Max(matchScore, hintScore);

        if (matchScore <= 0 &&
            pid > 0 &&
            TryMatchSinkInputFromProcessIdentity(properties, pid, out hintScore))
        {
            matchScore = hintScore;
        }

        if (matchScore <= 0 &&
            IsLikelyCompositorStream(properties) &&
            IsNewSessionSink(properties, out var compositorScore))
        {
            matchScore = compositorScore;
        }

        if (matchScore > 0)
            return true;

        if (pid > 0)
            return false;

        return false;
    }

    private bool TryMatchSinkInputFromProcessIdentity(JsonElement properties, int pid, out int matchScore)
    {
        matchScore = 0;
        if (pid <= 0)
            return false;

        var identityHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LinuxCompositorProcessHelper.CollectProcessIdentityHints(pid, identityHints);
        if (identityHints.Count == 0)
            return false;

        foreach (var key in new[]
                 {
                     "application.process.binary",
                     "application.name",
                     "application.process.command",
                     "node.name",
                     "media.name",
                 })
        {
            if (!TryGetPropertyString(properties, key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.ToLowerInvariant();
            foreach (var hint in identityHints)
            {
                if (hint.Length < 2)
                    continue;

                if (!normalized.Contains(hint, StringComparison.Ordinal))
                    continue;

                matchScore = key switch
                {
                    "application.process.binary" => 85,
                    "application.name" => 75,
                    "node.name" => 65,
                    _ => 55,
                };
                return true;
            }
        }

        return false;
    }

    private bool IsNewSessionSink(JsonElement properties, out int matchScore)
    {
        matchScore = 0;
        var serial = TryGetObjectSerial(properties);
        if (serial <= 0 || _baselineSinkSerials.Contains(serial))
            return false;

        matchScore = IsLikelyCompositorStream(properties) ? 70 : 0;
        return matchScore > 0;
    }

    private void TryCollectFallbackSessionSinks(JsonElement sinkInputsRoot, List<MatchedSinkInput> sinkInputs)
    {
        var fallbackCandidates = new List<MatchedSinkInput>();

        foreach (var element in sinkInputsRoot.EnumerateArray())
        {
            if (!element.TryGetProperty("index", out var indexElement) ||
                indexElement.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            if (!element.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryGetPropertyString(properties, "application.name", out var applicationName) &&
                IsIgnoredApplicationName(applicationName))
            {
                continue;
            }

            var serial = TryGetObjectSerial(properties);
            if (serial <= 0 || _baselineSinkSerials.Contains(serial))
                continue;

            var matchScore = 35;
            if (MatchesProcessNameHints(properties, out var hintScore))
                matchScore = Math.Max(matchScore, hintScore);
            else if (IsLikelyCompositorStream(properties))
                matchScore = Math.Max(matchScore, 40);

            fallbackCandidates.Add(new MatchedSinkInput(
                indexElement.GetInt32(),
                TryReadVolumeNormalized(element),
                element.TryGetProperty("mute", out var muteElement) &&
                muteElement.ValueKind == JsonValueKind.True,
                IsLikelyCompositorStream(properties),
                true,
                matchScore,
                serial));
        }

        if (fallbackCandidates.Count == 0)
            return;

        if (fallbackCandidates.Count == 1)
        {
            sinkInputs.Add(fallbackCandidates[0] with { MatchScore = Math.Max(fallbackCandidates[0].MatchScore, 45) });
            return;
        }

        var compositorStreams = fallbackCandidates.Where(static candidate => candidate.IsCompositorStream).ToArray();
        if (compositorStreams.Length == 1)
        {
            sinkInputs.Add(compositorStreams[0] with { MatchScore = Math.Max(compositorStreams[0].MatchScore, 42) });
        }
    }

    private bool IsCandidateProcessId(int pid)
    {
        if (pid <= 0)
            return false;

        if (_candidatePids.Contains(pid))
            return true;

        foreach (var seedPid in _seedPids)
        {
            if (seedPid <= 0)
                continue;

            if (LinuxCompositorProcessHelper.IsDescendantOf(seedPid, pid))
                return true;

            if (LinuxCompositorProcessHelper.IsDescendantOf(pid, seedPid))
                return true;
        }

        return _launchedCompositorPid > 0 &&
               (LinuxCompositorProcessHelper.IsDescendantOf(_launchedCompositorPid, pid) ||
                LinuxCompositorProcessHelper.IsDescendantOf(pid, _launchedCompositorPid));
    }

    private bool MatchesProcessNameHints(JsonElement properties, out int matchScore)
    {
        matchScore = 0;
        if (_processNameHints.Count == 0)
            return false;

        foreach (var key in new[]
                 {
                     "application.process.binary",
                     "application.name",
                     "application.process.command",
                     "node.name",
                     "media.name",
                     "module-stream-restore.id",
                 })
        {
            if (!TryGetPropertyString(properties, key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (ContainsProcessNameHint(value))
            {
                matchScore = key switch
                {
                    "application.process.binary" => 90,
                    "application.name" => 80,
                    "node.name" => 70,
                    _ => 60,
                };
                return true;
            }
        }

        return false;
    }

    private bool ContainsProcessNameHint(string value)
    {
        var normalized = value.ToLowerInvariant();
        foreach (var hint in _processNameHints)
        {
            if (hint.Length < 2)
                continue;

            if (normalized.Contains(hint, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsIgnoredApplicationName(string? applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
            return false;

        return IgnoredApplicationNames.Contains(applicationName.Trim());
    }

    private static bool IsLikelyCompositorStream(JsonElement properties)
    {
        foreach (var key in new[] { "application.process.binary", "application.name", "application.process.command", "node.name" })
        {
            if (!TryGetPropertyString(properties, key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.Contains("gamescope", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("gamescopereaper", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static float TryReadVolumeNormalized(JsonElement sinkInput)
    {
        if (!sinkInput.TryGetProperty("volume", out var volumeElement) ||
            volumeElement.ValueKind != JsonValueKind.Object)
            return 1.0f;

        foreach (var channel in volumeElement.EnumerateObject())
        {
            if (!channel.Value.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.Number)
                continue;

            return Math.Clamp(valueElement.GetInt32() / (float)PulseVolumeNominal, 0.0f, 1.0f);
        }

        return 1.0f;
    }

    private static long TryGetObjectSerial(JsonElement properties)
    {
        if (!properties.TryGetProperty("object.serial", out var serialProperty))
            return 0;

        return serialProperty.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(serialProperty.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when serialProperty.TryGetInt64(out var number) => number,
            _ => 0,
        };
    }

    private static bool TryGetProcessId(JsonElement properties, out int pid)
    {
        pid = 0;
        foreach (var key in new[]
                 {
                     "application.process.id",
                     "application.process.pid",
                     "module-stream-restore.process.id",
                 })
        {
            if (!properties.TryGetProperty(key, out var pidProperty))
                continue;

            if (TryParseProcessId(pidProperty, out pid))
                return true;
        }

        return false;
    }

    private static bool TryParseProcessId(JsonElement pidProperty, out int pid)
    {
        pid = 0;
        return pidProperty.ValueKind switch
        {
            JsonValueKind.String => int.TryParse(pidProperty.GetString(), out pid),
            JsonValueKind.Number => pidProperty.TryGetInt32(out pid),
            _ => false,
        };
    }

    private static bool TryGetPropertyString(JsonElement properties, string key, out string? value)
    {
        value = null;
        if (!properties.TryGetProperty(key, out var property))
            return false;

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Detach();
    }
}
