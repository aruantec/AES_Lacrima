using log4net;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace AES_Lacrima.Services;

public readonly record struct MprisState(
    bool IsPlaying,
    bool IsStopped,
    bool CanPlay,
    bool CanPause,
    bool CanSeek,
    bool CanGoNext,
    bool CanGoPrevious,
    bool Shuffle,
    string LoopStatus,
    double Volume,
    long PositionUs,
    long LengthUs,
    string TrackIdObjectPath,
    string Title,
    string Artist,
    string Album,
    string ArtUrl
);

/// <summary>
/// Minimal MPRIS 2.2 service exposed on Linux through D-Bus.
/// </summary>
public sealed class MprisService : IMethodHandler, IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MprisService));

    private const string ObjectPathValue = "/org/mpris/MediaPlayer2";
    private const string BusName = "org.mpris.MediaPlayer2.aes_lacrima";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string IntrospectableInterface = "org.freedesktop.DBus.Introspectable";
    private const string DBusInterface = "org.freedesktop.DBus";

    private static readonly string IntrospectionXml =
        """
        <node name="/org/mpris/MediaPlayer2">
          <interface name="org.freedesktop.DBus.Introspectable">
            <method name="Introspect">
              <arg name="xml_data" type="s" direction="out"/>
            </method>
          </interface>
          <interface name="org.freedesktop.DBus.Properties">
            <method name="Get">
              <arg name="interface_name" type="s" direction="in"/>
              <arg name="property_name" type="s" direction="in"/>
              <arg name="value" type="v" direction="out"/>
            </method>
            <method name="Set">
              <arg name="interface_name" type="s" direction="in"/>
              <arg name="property_name" type="s" direction="in"/>
              <arg name="value" type="v" direction="in"/>
            </method>
            <method name="GetAll">
              <arg name="interface_name" type="s" direction="in"/>
              <arg name="properties" type="a{sv}" direction="out"/>
            </method>
            <signal name="PropertiesChanged">
              <arg name="interface_name" type="s"/>
              <arg name="changed_properties" type="a{sv}"/>
              <arg name="invalidated_properties" type="as"/>
            </signal>
          </interface>
          <interface name="org.mpris.MediaPlayer2">
            <method name="Raise"/>
            <method name="Quit"/>
            <property name="CanQuit" type="b" access="read"/>
            <property name="CanRaise" type="b" access="read"/>
            <property name="HasTrackList" type="b" access="read"/>
            <property name="Identity" type="s" access="read"/>
            <property name="DesktopEntry" type="s" access="read"/>
            <property name="SupportedUriSchemes" type="as" access="read"/>
            <property name="SupportedMimeTypes" type="as" access="read"/>
          </interface>
          <interface name="org.mpris.MediaPlayer2.Player">
            <method name="Next"/>
            <method name="Previous"/>
            <method name="Pause"/>
            <method name="PlayPause"/>
            <method name="Stop"/>
            <method name="Play"/>
            <method name="Seek">
              <arg name="Offset" type="x" direction="in"/>
            </method>
            <method name="SetPosition">
              <arg name="TrackId" type="o" direction="in"/>
              <arg name="Position" type="x" direction="in"/>
            </method>
            <method name="OpenUri">
              <arg name="Uri" type="s" direction="in"/>
            </method>
            <signal name="Seeked">
              <arg name="Position" type="x"/>
            </signal>
            <property name="PlaybackStatus" type="s" access="read"/>
            <property name="LoopStatus" type="s" access="readwrite"/>
            <property name="Rate" type="d" access="readwrite"/>
            <property name="Shuffle" type="b" access="readwrite"/>
            <property name="Metadata" type="a{sv}" access="read"/>
            <property name="Volume" type="d" access="readwrite"/>
            <property name="Position" type="x" access="read"/>
            <property name="MinimumRate" type="d" access="read"/>
            <property name="MaximumRate" type="d" access="read"/>
            <property name="CanGoNext" type="b" access="read"/>
            <property name="CanGoPrevious" type="b" access="read"/>
            <property name="CanPlay" type="b" access="read"/>
            <property name="CanPause" type="b" access="read"/>
            <property name="CanSeek" type="b" access="read"/>
            <property name="CanControl" type="b" access="read"/>
          </interface>
        </node>
        """;

    private readonly Func<MprisState> _getState;
    private readonly Func<Task> _playAsync;
    private readonly Func<Task> _pauseAsync;
    private readonly Func<Task> _playPauseAsync;
    private readonly Func<Task> _stopAsync;
    private readonly Func<Task> _nextAsync;
    private readonly Func<Task> _previousAsync;
    private readonly Func<long, Task> _seekRelativeAsync;
    private readonly Func<long, Task> _setPositionAsync;
    private readonly Func<double, Task> _setVolumeAsync;
    private readonly Func<bool, Task> _setShuffleAsync;
    private readonly Func<string, Task> _setLoopStatusAsync;
    private readonly Func<string, Task>? _openUriAsync;
    private readonly Action? _raiseRequested;
    private readonly Action? _quitRequested;

    private readonly Connection _connection = Connection.Session;
    private bool _started;
    private bool _disposed;

    public MprisService(
        Func<MprisState> getState,
        Func<Task> playAsync,
        Func<Task> pauseAsync,
        Func<Task> playPauseAsync,
        Func<Task> stopAsync,
        Func<Task> nextAsync,
        Func<Task> previousAsync,
        Func<long, Task> seekRelativeAsync,
        Func<long, Task> setPositionAsync,
        Func<double, Task> setVolumeAsync,
        Func<bool, Task> setShuffleAsync,
        Func<string, Task> setLoopStatusAsync,
        Func<string, Task>? openUriAsync = null,
        Action? raiseRequested = null,
        Action? quitRequested = null)
    {
        _getState = getState;
        _playAsync = playAsync;
        _pauseAsync = pauseAsync;
        _playPauseAsync = playPauseAsync;
        _stopAsync = stopAsync;
        _nextAsync = nextAsync;
        _previousAsync = previousAsync;
        _seekRelativeAsync = seekRelativeAsync;
        _setPositionAsync = setPositionAsync;
        _setVolumeAsync = setVolumeAsync;
        _setShuffleAsync = setShuffleAsync;
        _setLoopStatusAsync = setLoopStatusAsync;
        _openUriAsync = openUriAsync;
        _raiseRequested = raiseRequested;
        _quitRequested = quitRequested;
    }

    public string Path => ObjectPathValue;

    public bool RunMethodHandlerSynchronously(Message message) => false;

    public async Task StartAsync()
    {
        if (_started || _disposed)
            return;

        await _connection.ConnectAsync().ConfigureAwait(false);
        _connection.AddMethodHandler(this);

        var requestResult = await CallBusUInt32MethodAsync("RequestName", "su", w =>
        {
            w.WriteString(BusName);
            w.WriteUInt32(0u);
        }).ConfigureAwait(false);

        _started = requestResult == 1u || requestResult == 4u;
        if (!_started)
        {
            Log.Warn($"MPRIS RequestName returned {requestResult}. Service may not be active.");
        }
        else
        {
            NotifyStateChanged();
            Log.Info("MPRIS service started.");
        }
    }

    public async ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            await HandleMethodInternalAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("MPRIS method handling failed", ex);
            if (!context.ReplySent && !context.NoReplyExpected)
            {
                context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
            }
        }
    }

    public void NotifyStateChanged(string? changedPropertyName = null)
    {
        if (!_started || _disposed)
            return;

        var state = _getState();
        var changed = BuildPlayerProperties(state, includeMetadata: true, includePosition: true);
        EmitPropertiesChanged(PlayerInterface, changed);

        if (string.Equals(changedPropertyName, "Position", StringComparison.Ordinal))
        {
            EmitSeeked(state.PositionUs);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _connection.RemoveMethodHandler(ObjectPathValue);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to remove MPRIS handler", ex);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CallBusUInt32MethodAsync("ReleaseName", "s", w => w.WriteString(BusName)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to release MPRIS bus name", ex);
            }
        });
    }

    private async Task HandleMethodInternalAsync(MethodContext context)
    {
        var request = context.Request;
        var iface = request.InterfaceAsString ?? string.Empty;
        var member = request.MemberAsString ?? string.Empty;

        if (string.Equals(iface, IntrospectableInterface, StringComparison.Ordinal) &&
            string.Equals(member, "Introspect", StringComparison.Ordinal))
        {
            using var reply = context.CreateReplyWriter("s");
            reply.WriteString(IntrospectionXml);
            context.Reply(reply.CreateMessage());
            return;
        }

        if (string.Equals(iface, PropertiesInterface, StringComparison.Ordinal))
        {
            await HandlePropertiesCallAsync(context, member).ConfigureAwait(false);
            return;
        }

        if (string.Equals(iface, RootInterface, StringComparison.Ordinal))
        {
            await HandleRootCallAsync(context, member).ConfigureAwait(false);
            return;
        }

        if (string.Equals(iface, PlayerInterface, StringComparison.Ordinal))
        {
            await HandlePlayerCallAsync(context, member).ConfigureAwait(false);
            return;
        }

        context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", $"Unsupported interface/member '{iface}.{member}'.");
    }

    private async Task HandlePropertiesCallAsync(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        switch (member)
        {
            case "Get":
            {
                var interfaceName = reader.ReadString();
                var propertyName = reader.ReadString();
                var value = GetProperty(interfaceName, propertyName);
                using var reply = context.CreateReplyWriter("v");
                reply.WriteVariant(value);
                context.Reply(reply.CreateMessage());
                return;
            }
            case "GetAll":
            {
                var interfaceName = reader.ReadString();
                var properties = GetAllProperties(interfaceName);
                using var reply = context.CreateReplyWriter("a{sv}");
                reply.WriteDictionary(properties);
                context.Reply(reply.CreateMessage());
                return;
            }
            case "Set":
            {
                var interfaceName = reader.ReadString();
                var propertyName = reader.ReadString();
                var propertyValue = reader.ReadVariantValue();
                await SetPropertyAsync(interfaceName, propertyName, propertyValue).ConfigureAwait(false);

                if (!context.NoReplyExpected)
                {
                    using var reply = context.CreateReplyWriter(string.Empty);
                    context.Reply(reply.CreateMessage());
                }
                return;
            }
            default:
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", $"Unsupported properties member '{member}'.");
                return;
        }
    }

    private async Task HandleRootCallAsync(MethodContext context, string member)
    {
        switch (member)
        {
            case "Raise":
                _raiseRequested?.Invoke();
                break;
            case "Quit":
                _quitRequested?.Invoke();
                break;
            default:
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", $"Unsupported root member '{member}'.");
                return;
        }

        if (!context.NoReplyExpected)
        {
            using var reply = context.CreateReplyWriter(string.Empty);
            context.Reply(reply.CreateMessage());
        }
        await Task.CompletedTask;
    }

    private async Task HandlePlayerCallAsync(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        switch (member)
        {
            case "Play":
                await _playAsync().ConfigureAwait(false);
                break;
            case "Pause":
                await _pauseAsync().ConfigureAwait(false);
                break;
            case "PlayPause":
                await _playPauseAsync().ConfigureAwait(false);
                break;
            case "Stop":
                await _stopAsync().ConfigureAwait(false);
                break;
            case "Next":
                await _nextAsync().ConfigureAwait(false);
                break;
            case "Previous":
                await _previousAsync().ConfigureAwait(false);
                break;
            case "Seek":
            {
                var offsetUs = reader.ReadInt64();
                await _seekRelativeAsync(offsetUs).ConfigureAwait(false);
                break;
            }
            case "SetPosition":
            {
                _ = reader.ReadObjectPath();
                var positionUs = reader.ReadInt64();
                await _setPositionAsync(positionUs).ConfigureAwait(false);
                break;
            }
            case "OpenUri":
            {
                var uri = reader.ReadString();
                if (_openUriAsync == null)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.NotSupported", "OpenUri is not supported.");
                    return;
                }

                await _openUriAsync(uri).ConfigureAwait(false);
                break;
            }
            default:
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", $"Unsupported player member '{member}'.");
                return;
        }

        if (!context.NoReplyExpected)
        {
            using var reply = context.CreateReplyWriter(string.Empty);
            context.Reply(reply.CreateMessage());
        }
    }

    private Dictionary<string, VariantValue> GetAllProperties(string interfaceName)
    {
        return interfaceName switch
        {
            RootInterface => BuildRootProperties(),
            PlayerInterface => BuildPlayerProperties(_getState(), includeMetadata: true, includePosition: true),
            _ => []
        };
    }

    private VariantValue GetProperty(string interfaceName, string propertyName)
    {
        var all = GetAllProperties(interfaceName);
        if (all.TryGetValue(propertyName, out var value))
            return value;

        throw new InvalidOperationException($"Unknown MPRIS property '{interfaceName}.{propertyName}'.");
    }

    private async Task SetPropertyAsync(string interfaceName, string propertyName, VariantValue value)
    {
        if (!string.Equals(interfaceName, PlayerInterface, StringComparison.Ordinal))
            throw new InvalidOperationException($"Properties on '{interfaceName}' are read-only.");

        var unwrapped = Unwrap(value);
        switch (propertyName)
        {
            case "Volume":
                if (!TryReadDouble(unwrapped, out var volume))
                    throw new InvalidOperationException("Volume expects a floating-point value.");
                await _setVolumeAsync(volume).ConfigureAwait(false);
                break;
            case "Shuffle":
                if (unwrapped.Type != VariantValueType.Bool)
                    throw new InvalidOperationException("Shuffle expects a boolean value.");
                await _setShuffleAsync(unwrapped.GetBool()).ConfigureAwait(false);
                break;
            case "LoopStatus":
                if (unwrapped.Type != VariantValueType.String)
                    throw new InvalidOperationException("LoopStatus expects a string value.");
                await _setLoopStatusAsync(unwrapped.GetString()).ConfigureAwait(false);
                break;
            case "Rate":
                // AES currently runs at normal speed only; accept but ignore.
                break;
            default:
                throw new InvalidOperationException($"Property '{propertyName}' is not writable.");
        }

        NotifyStateChanged(propertyName);
    }

    private static VariantValue Unwrap(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }
        return value;
    }

    private static bool TryReadDouble(VariantValue value, out double result)
    {
        result = 0;
        return value.Type switch
        {
            VariantValueType.Double => (result = value.GetDouble()) == result,
            VariantValueType.Int32 => (result = value.GetInt32()) == result,
            VariantValueType.UInt32 => (result = value.GetUInt32()) == result,
            VariantValueType.Int64 => (result = value.GetInt64()) == result,
            VariantValueType.UInt64 => (result = value.GetUInt64()) == result,
            _ => false
        };
    }

    private static Dictionary<string, VariantValue> BuildRootProperties()
    {
        return new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["CanQuit"] = VariantValue.Bool(true),
            ["CanRaise"] = VariantValue.Bool(true),
            ["HasTrackList"] = VariantValue.Bool(false),
            ["Identity"] = VariantValue.String("AES - Lacrima"),
            ["DesktopEntry"] = VariantValue.String("aes-lacrima"),
            ["SupportedUriSchemes"] = VariantValue.Array(new[] { "file", "http", "https" }),
            ["SupportedMimeTypes"] = VariantValue.Array(new[]
            {
                "audio/mpeg",
                "audio/flac",
                "audio/ogg",
                "audio/wav",
                "audio/x-wav",
                "audio/mp4"
            })
        };
    }

    private static Dictionary<string, VariantValue> BuildPlayerProperties(MprisState state, bool includeMetadata, bool includePosition)
    {
        var dict = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["PlaybackStatus"] = VariantValue.String(GetPlaybackStatus(state)),
            ["LoopStatus"] = VariantValue.String(string.IsNullOrWhiteSpace(state.LoopStatus) ? "None" : state.LoopStatus),
            ["Rate"] = VariantValue.Double(1.0),
            ["Shuffle"] = VariantValue.Bool(state.Shuffle),
            ["Volume"] = VariantValue.Double(Math.Clamp(state.Volume, 0.0, 1.0)),
            ["MinimumRate"] = VariantValue.Double(1.0),
            ["MaximumRate"] = VariantValue.Double(1.0),
            ["CanGoNext"] = VariantValue.Bool(state.CanGoNext),
            ["CanGoPrevious"] = VariantValue.Bool(state.CanGoPrevious),
            ["CanPlay"] = VariantValue.Bool(state.CanPlay),
            ["CanPause"] = VariantValue.Bool(state.CanPause),
            ["CanSeek"] = VariantValue.Bool(state.CanSeek),
            ["CanControl"] = VariantValue.Bool(true)
        };

        if (includePosition)
        {
            dict["Position"] = VariantValue.Int64(Math.Max(0, state.PositionUs));
        }

        if (includeMetadata)
        {
            dict["Metadata"] = BuildMetadata(state).AsVariantValue();
        }

        return dict;
    }

    private static Dict<string, VariantValue> BuildMetadata(MprisState state)
    {
        var metadata = new Dict<string, VariantValue>();
        var trackPath = string.IsNullOrWhiteSpace(state.TrackIdObjectPath)
            ? "/org/mpris/MediaPlayer2/track/none"
            : state.TrackIdObjectPath;

        metadata["mpris:trackid"] = VariantValue.ObjectPath(trackPath);
        metadata["mpris:length"] = VariantValue.Int64(Math.Max(0, state.LengthUs));
        metadata["xesam:title"] = VariantValue.String(state.Title ?? string.Empty);
        metadata["xesam:artist"] = VariantValue.Array(new[] { state.Artist ?? string.Empty });
        metadata["xesam:album"] = VariantValue.String(state.Album ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(state.ArtUrl))
        {
            metadata["mpris:artUrl"] = VariantValue.String(state.ArtUrl);
        }

        return metadata;
    }

    private static string GetPlaybackStatus(MprisState state)
    {
        if (state.IsPlaying)
            return "Playing";
        return state.IsStopped ? "Stopped" : "Paused";
    }

    private void EmitPropertiesChanged(string interfaceName, Dictionary<string, VariantValue> changedProperties)
    {
        if (_disposed || changedProperties.Count == 0)
            return;

        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null!, ObjectPathValue, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
            writer.WriteString(interfaceName);
            writer.WriteDictionary(changedProperties);
            writer.WriteArray(Array.Empty<string>());
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to emit MPRIS PropertiesChanged signal", ex);
        }
    }

    private void EmitSeeked(long positionUs)
    {
        if (_disposed)
            return;

        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null!, ObjectPathValue, PlayerInterface, "Seeked", "x");
            writer.WriteInt64(Math.Max(0, positionUs));
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to emit MPRIS Seeked signal", ex);
        }
    }

    private Task<uint> CallBusUInt32MethodAsync(string method, string signature, Action<MessageWriter> bodyWriter)
    {
        MessageBuffer message;
        using (var writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", DBusInterface, method, signature, MessageFlags.None);
            bodyWriter(writer);
            message = writer.CreateMessage();
        }

        return _connection.CallMethodAsync(message, static (reply, _) =>
        {
            var reader = reply.GetBodyReader();
            return reader.ReadUInt32();
        }, null!);
    }
}
