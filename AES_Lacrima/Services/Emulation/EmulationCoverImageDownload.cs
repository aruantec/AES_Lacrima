using AES_Lacrima.Services;
using SkiaSharp;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Emulation;

internal static class EmulationCoverImageDownload
{
    private const int MaxDownloadBytes = 2_500_000;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    private const string UserAgent = "Mozilla/5.0 (compatible; AES_Lacrima/1.0; +https://github.com/AES-Team/AES_Lacrima)";

    public static async Task<byte[]?> TryDownloadValidatedCoverAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "image/*,*/*;q=0.8");

            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxDownloadBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (buffer.Length + read > MaxDownloadBytes)
                    return null;

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            return IsValidCoverImage(bytes) ? bytes : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsValidCoverImage(byte[] bytes)
    {
        if (bytes.Length < 8 * 1024)
            return false;

        try
        {
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded == null)
                return false;

            if (AutoCoverImageHeuristics.ShouldRejectDownloadedImage(bytes, decoded.Width, decoded.Height))
                return false;

            if (AutoCoverImageHeuristics.LooksLikeMarketplacePhoto(decoded))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }
}
