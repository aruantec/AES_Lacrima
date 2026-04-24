using AES_Core.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;

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
    public static async Task<MediaInfo> GetBasicMetadataAsync(string videoUrl, CancellationToken cancellationToken = default)
    {
        var exePath = await RequireYtDlpPathAsync().ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            AddCommonYtDlpArgs(psi);
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--output-na-placeholder");
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("id");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("title");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("duration");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("artist");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("uploader");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("channel");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("album");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("track");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("track_number");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("release_year");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("genre");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("thumbnail");
            psi.ArgumentList.Add(videoUrl);
        }
        catch
        {
            psi.Arguments =
                BuildCommonYtDlpArgsForCommandLine() + " " +
                "--no-playlist --output-na-placeholder \"\" " +
                "--print id --print title --print duration --print artist --print uploader --print channel " +
                "--print album --print track --print track_number --print release_year --print genre --print thumbnail " +
                "\"" + videoUrl.Replace("\"", "\\\"") + "\"";
        }

        var lines = await RunAndCollectOutputLinesAsync(psi, exePath, cancellationToken).ConfigureAwait(false);

        static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        static int? ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;
        static double? ParseDouble(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        string? line(int index) => index < lines.Count ? Normalize(lines[index]) : null;
        var artist = line(3) ?? line(4) ?? line(5);

        return new MediaInfo
        {
            Id = line(0) ?? string.Empty,
            Title = line(1) ?? string.Empty,
            DurationSeconds = ParseDouble(line(2)),
            Artist = artist,
            Uploader = line(4),
            Channel = line(5),
            Album = line(6),
            Track = line(7),
            TrackNumber = ParseInt(line(8)),
            ReleaseYear = ParseInt(line(9)),
            Genre = line(10),
            ThumbnailUrl = line(11)
        };
    }

    public static async Task<MediaInfo> GetMetaDataAsync(string videoUrl, CancellationToken cancellationToken = default)
    {
        var exePath = await RequireYtDlpPathAsync().ConfigureAwait(false);

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
            AddCommonYtDlpArgs(psi);
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("-J");
            psi.ArgumentList.Add(videoUrl);
        }
        catch
        {
            // Fall back to quoted arguments for runtimes that don't support ArgumentList
            psi.Arguments = BuildCommonYtDlpArgsForCommandLine() + " --no-playlist -J \"" + videoUrl.Replace("\"", "\\\"") + "\"";
        }

        var json = await RunAndCollectOutputAsync(psi, exePath, cancellationToken).ConfigureAwait(false);

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

    private static async Task<string> RunAndCollectOutputAsync(ProcessStartInfo psi, string exePath, CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            var fallbackName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
            if (!string.Equals(psi.FileName, fallbackName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fallbackPsi = CreateFallbackStartInfo(psi, fallbackName);
                    process = Process.Start(fallbackPsi);
                }
                catch (Exception fallbackEx)
                {
                    throw new InvalidOperationException($"Failed to start yt-dlp ('{exePath}') and PATH fallback ('{fallbackName}'). Primary: {ex.Message}. Fallback: {fallbackEx.Message}");
                }
            }
            else
            {
                throw new InvalidOperationException($"Failed to start yt-dlp ('{exePath}'). Make sure yt-dlp is installed and on PATH. Error: {ex.Message}");
            }
        }

        if (process == null)
            throw new InvalidOperationException($"Failed to start yt-dlp ('{exePath}').");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp failed:\n{error}");

        return output;
    }

    private static ProcessStartInfo CreateFallbackStartInfo(ProcessStartInfo source, string fallbackFileName)
    {
        var fallback = new ProcessStartInfo
        {
            FileName = fallbackFileName,
            RedirectStandardOutput = source.RedirectStandardOutput,
            RedirectStandardError = source.RedirectStandardError,
            UseShellExecute = source.UseShellExecute,
            CreateNoWindow = source.CreateNoWindow,
            WorkingDirectory = source.WorkingDirectory
        };

        if (source.ArgumentList.Count > 0)
        {
            foreach (var arg in source.ArgumentList)
            {
                fallback.ArgumentList.Add(arg);
            }
        }
        else
        {
            fallback.Arguments = source.Arguments;
        }

        return fallback;
    }

    private static void AddCommonYtDlpArgs(ProcessStartInfo psi)
    {
        // Reduce YouTube extraction failures in headless/Linux environments.
        psi.ArgumentList.Add("--extractor-args");
        psi.ArgumentList.Add("youtube:player_client=android,web_safari,tv;player_skip=webpage,configs");

        var runtimeList = GetJsRuntimeList();
        if (!string.IsNullOrWhiteSpace(runtimeList))
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add(runtimeList);
        }

        var cookiesFile = Environment.GetEnvironmentVariable("AES_YTDLP_COOKIES_FILE");
        if (!string.IsNullOrWhiteSpace(cookiesFile) && File.Exists(cookiesFile))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesFile);
            return;
        }

        var cookiesFromBrowser = Environment.GetEnvironmentVariable("AES_YTDLP_COOKIES_FROM_BROWSER");
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(cookiesFromBrowser);
        }
    }

    private static string BuildCommonYtDlpArgsForCommandLine()
    {
        var args = new StringBuilder();
        args.Append("--extractor-args \"youtube:player_client=android,web_safari,tv;player_skip=webpage,configs\"");

        var runtimeList = GetJsRuntimeList();
        if (!string.IsNullOrWhiteSpace(runtimeList))
        {
            args.Append(" --js-runtimes ").Append(QuoteArg(runtimeList));
        }

        var cookiesFile = Environment.GetEnvironmentVariable("AES_YTDLP_COOKIES_FILE");
        if (!string.IsNullOrWhiteSpace(cookiesFile) && File.Exists(cookiesFile))
        {
            args.Append(" --cookies ").Append(QuoteArg(cookiesFile));
            return args.ToString();
        }

        var cookiesFromBrowser = Environment.GetEnvironmentVariable("AES_YTDLP_COOKIES_FROM_BROWSER");
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            args.Append(" --cookies-from-browser ").Append(QuoteArg(cookiesFromBrowser));
        }

        return args.ToString();
    }

    private static string? GetJsRuntimeList()
    {
        var candidates = new[] { "node", "deno", "bun" };
        var available = candidates.Where(cmd => FindExecutable(cmd) is not null).ToArray();
        return available.Length > 0 ? string.Join(',', available) : null;
    }

    private static string QuoteArg(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static async Task<string> RequireYtDlpPathAsync()
    {
        var exePath = await ResolveYtDlpPathAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("Failed to locate yt-dlp. Make sure yt-dlp is installed and on PATH.");
        return exePath;
    }

    private static async Task<IReadOnlyList<string>> RunAndCollectOutputLinesAsync(ProcessStartInfo psi, string exePath, CancellationToken cancellationToken)
    {
        var output = await RunAndCollectOutputAsync(psi, exePath, cancellationToken).ConfigureAwait(false);
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
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
        // Prefer the per-user Tools directory (OS standard location) to keep all downloaded helpers together.
        var dirs = new List<string> { ApplicationPaths.ToolsDirectory };

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

    internal static void InvalidateResolvedPathCache()
    {
        _resolvedYtDlpPath = null;
        _ytDlpResolved = false;
    }

    private static async Task<string?> ResolveYtDlpPathAsync()
    {
        if (_ytDlpResolved && !string.IsNullOrWhiteSpace(_resolvedYtDlpPath) && File.Exists(_resolvedYtDlpPath))
            return _resolvedYtDlpPath;

        _ytDlpResolved = false;
        _resolvedYtDlpPath = null;

        string preferred = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        string fallback = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp" : "yt-dlp.exe";

        string? systemPath = FindExecutable(preferred, fallback);
        string? localPath = FindLocalExecutable(preferred, fallback);

        bool localUsable = localPath is not null && await GetVersionAsync(localPath).ConfigureAwait(false) is not null;
        bool systemUsable = systemPath is not null && await GetVersionAsync(systemPath).ConfigureAwait(false) is not null;

        if (localUsable)
        {
            // Prefer the app-managed binary when it is usable.
            _resolvedYtDlpPath = localPath;
        }
        else if (systemUsable)
        {
            _resolvedYtDlpPath = systemPath;
        }
        else
        {
            // Fallback for diagnostics: return any candidate even if not immediately verifiable.
            _resolvedYtDlpPath = localPath ?? systemPath;
        }

        _ytDlpResolved = !string.IsNullOrWhiteSpace(_resolvedYtDlpPath);
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
