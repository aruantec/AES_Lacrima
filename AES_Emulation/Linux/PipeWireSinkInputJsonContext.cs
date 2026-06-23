using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Emulation.Linux;

/// <summary>
/// pactl-compatible sink-input list entry produced from pw-dump when pactl is unavailable.
/// </summary>
internal sealed class PipeWireSinkInputEntry
{
    public int Index { get; set; }

    public bool Mute { get; set; }

    public PipeWireSinkInputVolume? Volume { get; set; }

    public Dictionary<string, string>? Properties { get; set; }
}

internal sealed class PipeWireSinkInputVolume
{
    public PipeWireMonoVolume? Mono { get; set; }
}

internal sealed class PipeWireMonoVolume
{
    public int Value { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<PipeWireSinkInputEntry>))]
internal partial class PipeWireSinkInputJsonContext : JsonSerializerContext;
