using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AES_Lacrima.Services.ShadPs4;

namespace AES_Lacrima.Serialization;

/// <summary>JSON source generation for shadPS4 configs, cheats, and patch metadata.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ShadPs4CustomConfigDocument))]
[JsonSerializable(typeof(ShadPs4AudioConfig))]
[JsonSerializable(typeof(ShadPs4DebugConfig))]
[JsonSerializable(typeof(ShadPs4GpuConfig))]
[JsonSerializable(typeof(ShadPs4GeneralConfig))]
[JsonSerializable(typeof(ShadPs4InputConfig))]
[JsonSerializable(typeof(ShadPs4LogConfig))]
[JsonSerializable(typeof(ShadPs4VulkanConfig))]
[JsonSerializable(typeof(ShadPs4CheatDocument))]
[JsonSerializable(typeof(ShadPs4CheatModDefinition))]
[JsonSerializable(typeof(ShadPs4CheatMemoryPatch))]
[JsonSerializable(typeof(ShadPs4CheatEnabledStateDocument))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<ShadPs4CheatModDefinition>))]
[JsonSerializable(typeof(List<ShadPs4CheatMemoryPatch>))]
[JsonSerializable(typeof(GitHubContentEntry))]
[JsonSerializable(typeof(List<GitHubContentEntry>))]
internal partial class ShadPs4JsonContext : JsonSerializerContext;

/// <summary>GitHub API entry from a repository contents listing (patch download index).</summary>
internal sealed class GitHubContentEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}
