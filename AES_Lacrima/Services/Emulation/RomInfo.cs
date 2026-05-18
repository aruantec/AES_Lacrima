namespace AES_Lacrima.Services.Emulation;

// Enum describing detected ROM formats
public enum RomFormat { Unknown, Z64, V64, N64, Iso, Nes, Snes, Gbx, Gba, Sms, Genesis }

public class RomInfo
{
    public string? FilePath { get; set; }
    public RomFormat Format { get; set; }
    public string? DiscVolumeLabel { get; set; }
    public string? SystemCnf { get; set; }
    public string? BootFilePath { get; set; }
    public string? GameId { get; set; }
    public string? InternalTitle { get; set; }
    public uint HeaderCrc1 { get; set; }
    public uint HeaderCrc2 { get; set; }
    public string? Md5 { get; set; }
    public string? Sha1 { get; set; }
    public string? Crc32 { get; set; }
    public string? NormSha1 { get; set; }
    public string? ArchiveMd5 { get; set; }
    public string? ArchiveSha1 { get; set; }
    public string? ArchiveCrc32 { get; set; }
    
    // Cover image as raw BGRA pixel data (4 bytes per pixel)
    public byte[]? CoverImageData { get; set; }
    public int CoverImageWidth { get; set; }
    public int CoverImageHeight { get; set; }

    public RomInfo() { Format = RomFormat.Unknown; }
}