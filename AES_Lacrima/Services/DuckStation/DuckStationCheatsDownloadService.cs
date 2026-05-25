using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.DuckStation;

public sealed class DuckStationCheatsDownloadResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int FilesExtracted { get; init; }
}

public static class DuckStationCheatsDownloadService
{
    private const string CheatsZipUrl = "https://github.com/duckstation/chtdb/releases/download/latest/cheats.zip";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    static DuckStationCheatsDownloadService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima/1.0");
    }

    public static async Task<DuckStationCheatsDownloadResult> DownloadCheatsForSerialAsync(
        string? emulatorDirectory,
        string serial,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return Fail("Emulator directory is not configured.");

        if (string.IsNullOrWhiteSpace(serial))
            return Fail("Serial number is required to download cheats.");

        var normalizedSerial = serial.Trim().ToUpperInvariant();
        var cheatsDirectory = DuckStationCheatsService.GetCheatsDirectory(emulatorDirectory);
        Directory.CreateDirectory(cheatsDirectory);

        byte[] zipData;
        try
        {
            zipData = await Client.GetByteArrayAsync(CheatsZipUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to download cheat database: {ex.Message}");
        }

        try
        {
            var extracted = 0;
            using var zipStream = new MemoryStream(zipData);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var matchingEntries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                                entry.Name.StartsWith(normalizedSerial, StringComparison.OrdinalIgnoreCase) &&
                                entry.Name.EndsWith(".cht", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingEntries.Count == 0)
                return Fail($"No cheat files found for serial {normalizedSerial} in the database.");

            foreach (var entry in matchingEntries)
            {
                var targetPath = Path.Combine(cheatsDirectory, entry.Name);
                entry.ExtractToFile(targetPath, overwrite: true);
                extracted++;
            }

            return new DuckStationCheatsDownloadResult
            {
                Success = true,
                Message = $"Downloaded {extracted} cheat file(s) for {normalizedSerial}.",
                FilesExtracted = extracted
            };
        }
        catch (Exception ex)
        {
            return Fail($"Failed to extract cheat files: {ex.Message}");
        }
    }

    private static DuckStationCheatsDownloadResult Fail(string message) =>
        new() { Success = false, Message = message };
}
