using System.Diagnostics;
using System.Text.Json;

namespace AES_Controls.Helpers;

public static class JsonExtensions
{
    public static string? GetStringOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public static int? GetIntOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number &&
        p.TryGetInt32(out var v)
            ? v
            : null;

    public static double? GetDoubleOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number &&
        p.TryGetDouble(out var v)
            ? v
            : null;

    public static long? GetLongOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number &&
        p.TryGetInt64(out var v)
            ? v
            : null;
}

public sealed class MediaInfo
{
    // Core identity
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public double? DurationSeconds { get; init; }

    // Music metadata
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Track { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int? ReleaseYear { get; init; }
    public string? Genre { get; init; }

    // Channel / uploader
    public string? Uploader { get; init; }
    public string? Channel { get; init; }
    public string? ChannelId { get; init; }

    // Artwork
    public string? ThumbnailUrl { get; init; }

    // Available formats
    public IReadOnlyList<VideoFormat> VideoFormats { get; init; } = [];
    public IReadOnlyList<AudioFormat> AudioFormats { get; init; } = [];
    public IReadOnlyList<MuxedFormat> MuxedFormats { get; init; } = [];
}

public sealed class VideoFormat
{
    public string FormatId { get; init; } = string.Empty;
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }
    public string Codec { get; init; } = string.Empty;
    public long? Bitrate { get; init; }
    public long? FileSize { get; init; }
    public string Url { get; init; } = string.Empty;
}

public sealed class AudioFormat
{
    public string FormatId { get; init; } = string.Empty;
    public string Codec { get; init; } = string.Empty;
    public int? Bitrate { get; init; }
    public long? FileSize { get; init; }
    public string Url { get; init; } = string.Empty;
}

public sealed class MuxedFormat
{
    public string FormatId { get; init; } = string.Empty;
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }
    public string VideoCodec { get; init; } = string.Empty;
    public string AudioCodec { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public static class YtDlpMetadata
{
    public static async Task<MediaInfo> GetMetaDataAsync(string videoUrl, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp.exe",
            Arguments = "-J \"" + videoUrl + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp.");

        var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed:\n{error}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var videos = new List<VideoFormat>();
        var audios = new List<AudioFormat>();
        var muxed = new List<MuxedFormat>();

        foreach (var f in root.GetProperty("formats").EnumerateArray())
        {
            var url = f.GetStringOrNull("url");
            if (string.IsNullOrEmpty(url))
                continue;

            var vcodec = f.GetStringOrNull("vcodec") ?? "none";
            var acodec = f.GetStringOrNull("acodec") ?? "none";
            var formatId = f.GetStringOrNull("format_id") ?? string.Empty;

            if (vcodec != "none" && acodec == "none")
            {
                videos.Add(new VideoFormat
                {
                    FormatId = formatId,
                    Width = f.GetIntOrNull("width"),
                    Height = f.GetIntOrNull("height"),
                    Fps = f.GetDoubleOrNull("fps"),
                    Codec = vcodec,
                    Bitrate = f.GetLongOrNull("vbr"),
                    FileSize = f.GetLongOrNull("filesize"),
                    Url = url
                });
            }
            else if (vcodec == "none" && acodec != "none")
            {
                audios.Add(new AudioFormat
                {
                    FormatId = formatId,
                    Codec = acodec,
                    Bitrate = f.GetIntOrNull("abr"),
                    FileSize = f.GetLongOrNull("filesize"),
                    Url = url
                });
            }
            else
            {
                muxed.Add(new MuxedFormat
                {
                    FormatId = formatId,
                    Width = f.GetIntOrNull("width"),
                    Height = f.GetIntOrNull("height"),
                    Fps = f.GetDoubleOrNull("fps"),
                    VideoCodec = vcodec,
                    AudioCodec = acodec,
                    Url = url
                });
            }
        }

        string? FirstString(params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = root.GetStringOrNull(key);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            return null;
        }

        return new MediaInfo
        {
            Id = root.GetStringOrNull("id") ?? string.Empty,
            Title = root.GetStringOrNull("title") ?? string.Empty,
            DurationSeconds = root.GetDoubleOrNull("duration"),

            Artist = FirstString("artist", "creator", "uploader", "channel"),
            Album = root.GetStringOrNull("album"),
            Track = root.GetStringOrNull("track"),
            TrackNumber = root.GetIntOrNull("track_number"),
            DiscNumber = root.GetIntOrNull("disc_number"),
            ReleaseYear = root.GetIntOrNull("release_year"),
            Genre = root.GetStringOrNull("genre"),

            Uploader = root.GetStringOrNull("uploader"),
            Channel = root.GetStringOrNull("channel"),
            ChannelId = root.GetStringOrNull("channel_id"),

            ThumbnailUrl = root.GetStringOrNull("thumbnail"),

            VideoFormats = videos,
            AudioFormats = audios,
            MuxedFormats = muxed
        };
    }
}