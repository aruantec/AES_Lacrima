using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Services.ShadPs4;

public sealed record ShadPs4CheatFileItem(string FilePath, string FileName);

public sealed class ShadPs4CheatDocument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("process")]
    public string Process { get; set; } = "eboot.bin";

    [JsonPropertyName("mods")]
    public List<ShadPs4CheatModDefinition> Mods { get; set; } = [];

    [JsonPropertyName("credits")]
    public List<string> Credits { get; set; } = [];
}

public sealed class ShadPs4CheatModDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "checkbox";

    [JsonPropertyName("memory")]
    public List<ShadPs4CheatMemoryPatch> Memory { get; set; } = [];
}

public sealed class ShadPs4CheatMemoryPatch
{
    [JsonPropertyName("offset")]
    public string Offset { get; set; } = string.Empty;

    [JsonPropertyName("on")]
    public string On { get; set; } = string.Empty;

    [JsonPropertyName("off")]
    public string Off { get; set; } = string.Empty;
}

public sealed class ShadPs4CheatEnabledStateDocument
{
    [JsonPropertyName("enabled_mods")]
    public Dictionary<string, bool> EnabledMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
