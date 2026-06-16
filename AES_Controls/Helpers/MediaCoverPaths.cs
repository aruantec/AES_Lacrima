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

    private static readonly HashSet<string> RomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".bin", ".cue", ".img", ".chd", ".pbp", ".m3u", ".mdf", ".nrg",
        ".cso", ".ciso", ".gcz", ".rvz", ".wia", ".gcm", ".dol", ".elf", ".tgc",
        ".wbfs", ".wad", ".wud", ".wux", ".wua", ".rpx", ".nds", ".srl", ".3ds",
        ".3dsx", ".cci", ".cxi", ".cia", ".n64", ".z64", ".v64", ".sfc", ".smc",
        ".fig", ".swc", ".nes", ".fds", ".unf", ".unif", ".gba", ".gen", ".md",
        ".smd", ".xci", ".nsp", ".nsz", ".nca", ".cdi", ".gdi", ".pkg", ".ps3",
        ".zip", ".7z", ".rar"
    };

    public static bool IsAudioMediaFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && AudioExtensions.Contains(extension);
    }

    public static bool HasRomExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && RomExtensions.Contains(extension);
    }

    /// <summary>
    /// Online streams and other non-ROM paths keep artwork in the metadata sidecar.
    /// </summary>
    public static bool IsOnlineOrMissingMediaFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return true;

        if (HasRomExtension(path))
            return false;

        return !File.Exists(path);
    }

    /// <summary>
    /// ROM library items store the active cover in a <c>.cover</c> sidecar even when the
    /// file is temporarily unavailable (for example an unmounted external drive).
    /// </summary>
    public static bool UsesEmulationCoverSidecar(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsAudioMediaFile(path))
            return false;

        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HasRomExtension(path))
            return true;

        if (EmulationCoverCacheHelper.HasCover(path))
            return true;

        var metaPath = EmulationCoverCacheHelper.GetMetadataCachePath(path);
        return !string.IsNullOrWhiteSpace(metaPath) && File.Exists(metaPath);
    }

    public static bool UsesMetadataImageCache(string? path) => !UsesEmulationCoverSidecar(path);
}
