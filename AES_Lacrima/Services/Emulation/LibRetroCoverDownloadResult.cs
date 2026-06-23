namespace AES_Lacrima.Services.Emulation;

internal sealed class LibRetroCoverDownloadResult
{
    public required byte[] Bytes { get; init; }
    public required string MatchedTitle { get; init; }
}
