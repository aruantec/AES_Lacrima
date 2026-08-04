using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AES_Lacrima.Serialization;

/// <summary>
/// On-disk document for AES online playlists (<c>.apl</c>).
/// Only online media URLs and lightweight display metadata are persisted.
/// </summary>
internal sealed class AplPlaylistDocument
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "AES Online Playlist";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("items")]
    public List<AplPlaylistItem> Items { get; set; } = [];
}

internal sealed class AplPlaylistItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("track")]
    public uint Track { get; set; }
}
