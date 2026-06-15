namespace AES_Controls.Helpers;

/// <summary>
/// Shared cover-cache path rules. Audio keeps embedded covers in <c>.meta</c>;
/// emulation ROMs use sidecar <c>.cover</c> files.
/// </summary>
public static class MediaCoverPaths
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma",
        ".ape", ".wv", ".mpc", ".aiff", ".aif", ".alac"
    };

    public static bool IsAudioMediaFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && AudioExtensions.Contains(extension);
    }

    /// <summary>
    /// Online streams and other non-file paths keep artwork in the metadata sidecar.
    /// </summary>
    public static bool IsOnlineOrMissingMediaFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return true;

        return !File.Exists(path);
    }

    public static bool UsesMetadataImageCache(string? path) =>
        IsAudioMediaFile(path) || IsOnlineOrMissingMediaFile(path);

    public static bool UsesEmulationCoverSidecar(string? path) => !UsesMetadataImageCache(path);
}
