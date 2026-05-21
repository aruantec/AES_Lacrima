namespace AES_Lacrima.Services.ShadPs4;

public sealed class ShadPs4DownloadResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public int FilesDownloaded { get; init; }
}
