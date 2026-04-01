using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AES_Code.Models;

namespace AES_Controls.Helpers;

/// <summary>
/// Represents a container for media metadata and associated binary resources (images, videos).
/// </summary>
public class CustomMetadata
{
    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; set; } = "";
    /// <summary>Gets or sets the artist name.</summary>
    public string Artist { get; set; } = "";
    /// <summary>Gets or sets the album name.</summary>
    public string Album { get; set; } = "";
    /// <summary>Gets or sets the track number.</summary>
    public uint Track { get; set; }
    /// <summary>Gets or sets the release year.</summary>
    public uint Year { get; set; }
    /// <summary>Gets or sets the track lyrics.</summary>
    public string Lyrics { get; set; } = "";
    /// <summary>Gets or sets the track genre.</summary>
    public string Genre { get; set; } = "";
    /// <summary>Gets or sets a general comment or description.</summary>
    public string Comment { get; set; } = "";
    /// <summary>Gets or sets the ReplayGain track gain in dB.</summary>
    public double ReplayGainTrackGain { get; set; }
    /// <summary>Gets or sets the ReplayGain album gain in dB.</summary>
    public double ReplayGainAlbumGain { get; set; }
    /// <summary>Gets or sets the duration in seconds.</summary>
    public double Duration { get; set; }
    /// <summary>Gets or sets the selected gameplay video URL for emulation metadata.</summary>
    public string VideoUrl { get; set; } = "";
    /// <summary>Gets or sets the list of associated images.</summary>
    public List<ImageData> Images { get; set; } = [];
    /// <summary>Gets or sets the list of associated video data.</summary>
    public List<VideoData> Videos { get; set; } = [];
}

/// <summary>
/// Represents binary image data along with its MIME type and classification.
/// </summary>
public class ImageData
{
    /// <summary>Gets or sets the MIME type of the image (e.g., image/jpeg).</summary>
    public string MimeType { get; set; } = "image/png";
    /// <summary>Gets or sets the raw image binary data.</summary>
    public byte[] Data { get; set; } = [];
    /// <summary>Gets or sets the category/kind of image.</summary>
    public TagImageKind Kind { get; set; } = TagImageKind.Cover;
}

/// <summary>
/// Represents binary video data along with its MIME type and classification.
/// </summary>
public class VideoData
{
    /// <summary>Gets or sets the MIME type of the video (e.g., video/mp4).</summary>
    public string MimeType { get; set; } = "video/mp4";
    /// <summary>Gets or sets the raw video binary data.</summary>
    public byte[] Data { get; set; } = [];
    /// <summary>Gets or sets the category/kind of video (e.g., LiveWallpaper).</summary>
    public TagImageKind Kind { get; set; } = TagImageKind.LiveWallpaper;
}

/// <summary>
/// Provides utility methods to save and load media metadata to and from disk.
/// </summary>
public static class BinaryMetadataHelper
{
    private const int IoRetryCount = 3;
    private const int IoRetryDelayMs = 15;

    /// <summary>
    /// Serializes the provided metadata to a file at the specified path.
    /// </summary>
    /// <param name="cachePath">The file path where metadata will be saved.</param>
    /// <param name="metadata">The metadata object to serialize.</param>
    public static void SaveMetadata(string cachePath, CustomMetadata metadata)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tempPath = cachePath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, metadata, BinaryMetadataJsonContext.Default.CustomMetadata);
                fs.Flush(true);
            }

            if (File.Exists(cachePath))
                File.Copy(tempPath, cachePath, overwrite: true);
            else
                File.Move(tempPath, cachePath);

            TryDeleteTempFile(tempPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Deserializes metadata from a file at the specified path.
    /// </summary>
    /// <param name="cachePath">The file path to read from.</param>
    /// <returns>A <see cref="CustomMetadata"/> instance if successful; otherwise, null.</returns>
    public static CustomMetadata? LoadMetadata(string cachePath)
    {
        if (!File.Exists(cachePath)) return null;

        for (int attempt = 0; attempt < IoRetryCount; attempt++)
        {
            try
            {
                using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize(fs, BinaryMetadataJsonContext.Default.CustomMetadata);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"JSON Parsing Error: {ex.Message}");
                return null;
            }
            catch (IOException ex) when (attempt < IoRetryCount - 1)
            {
                Debug.WriteLine($"Metadata cache read retry {attempt + 1} for '{cachePath}': {ex.Message}");
                Thread.Sleep(IoRetryDelayMs);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Metadata cache read failed for '{cachePath}': {ex.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a unique, filename-safe cache ID for a given URL or file path using SHA1 hashing.
    /// </summary>
    public static string GetCacheId(string path)
    {
        if (string.IsNullOrEmpty(path)) return "unknown";
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path));
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Temp cleanup error: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CustomMetadata))]
[JsonSerializable(typeof(ImageData))]
[JsonSerializable(typeof(VideoData))]
internal partial class BinaryMetadataJsonContext : JsonSerializerContext;
