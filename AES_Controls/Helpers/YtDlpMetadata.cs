using AES_Core.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

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
    public IReadOnlyList<VideoFormat> VideoFormats { get; init; } = Array.Empty<VideoFormat>();
    public IReadOnlyList<AudioFormat> AudioFormats { get; init; } = Array.Empty<AudioFormat>();
    public IReadOnlyList<MuxedFormat> MuxedFormats { get; init; } = Array.Empty<MuxedFormat>();
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
        string? exePath = await ResolveYtDlpPathAsync();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Prefer safe argument passing when supported
        try
        {
            psi.ArgumentList.Add("-J");
            psi.ArgumentList.Add(videoUrl);
        }
        catch
        {
            // Fall back to quoted arguments for runtimes that don't support ArgumentList
            psi.Arguments = "-J \"" + videoUrl.Replace("\"", "\\\"") + "\"";
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start yt-dlp ('{exePath}'). Make sure yt-dlp is installed and on PATH. Error: {ex.Message}");
        }

        if (process == null)
            throw new InvalidOperationException($"Failed to start yt-dlp ('{exePath}').");

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

    // Try to find one of the provided executable names in the system PATH. Returns a full path or null.
    private static string? FindExecutable(params string[] names)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in parts)
        {
            try
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { /* ignore bad PATH entries */ }
        }
        return null;
    }

    private static string? FindLocalExecutable(params string[] names)
    {
        var dirs = new List<string> { AppContext.BaseDirectory, ApplicationPaths.ToolsDirectory };
        
        var processPathDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(processPathDir) && !dirs.Contains(processPathDir))
        {
            dirs.Add(processPathDir);
        }

        foreach (var dir in dirs)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string? _resolvedYtDlpPath = null;
    private static bool _ytDlpResolved = false;

    private static async Task<string?> ResolveYtDlpPathAsync()
    {
        if (_ytDlpResolved) return _resolvedYtDlpPath;

        string preferred = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        string fallback = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp" : "yt-dlp.exe";

        string? systemPath = FindExecutable(preferred, fallback);
        string? localPath = FindLocalExecutable(preferred, fallback);

        if (systemPath != null && localPath != null)
        {
            string? sysVersion = await GetVersionAsync(systemPath);
            string? locVersion = await GetVersionAsync(localPath);

            if (sysVersion != null && locVersion != null && string.Compare(sysVersion, locVersion) < 0)
            {
                // Local is newer, try updating system. If it fails, fallback to local
                bool updated = await TryUpdateYtDlpAsync(systemPath);
                _resolvedYtDlpPath = updated ? systemPath : localPath;
            }
            else
            {
                _resolvedYtDlpPath = systemPath;
            }
        }
        else
        {
            _resolvedYtDlpPath = systemPath ?? localPath;
        }

        _ytDlpResolved = true;
        return _resolvedYtDlpPath;
    }

    private static async Task<string?> GetVersionAsync(string path)
    {
        try 
        {
            var psi = new ProcessStartInfo 
            { 
                FileName = path, 
                Arguments = "--version", 
                RedirectStandardOutput = true, 
                UseShellExecute = false, 
                CreateNoWindow = true 
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output : null;
        } 
        catch { return null; }
    }

    private static async Task<bool> TryUpdateYtDlpAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo 
            { 
                FileName = path, 
                Arguments = "-U", 
                RedirectStandardOutput = true, 
                RedirectStandardError = true, 
                UseShellExecute = false, 
                CreateNoWindow = true 
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }
}
