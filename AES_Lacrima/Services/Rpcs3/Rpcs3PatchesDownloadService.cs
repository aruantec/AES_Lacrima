using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Rpcs3;

public sealed record Rpcs3PatchesDownloadResult(bool Success, string Message, bool WasAlreadyUpToDate);

public static class Rpcs3PatchesDownloadService
{
    private const string DownloadEndpointTemplate =
        "https://rpcs3.net/compatibility?patch&api=v1&v={0}";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static async Task<Rpcs3PatchesDownloadResult> DownloadLatestAsync(
        string? emulatorDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return new Rpcs3PatchesDownloadResult(false, "Emulator directory is not configured.", false);

        var patchVersion = Rpcs3PatchesService.TryReadLocalPatchEngineVersion(emulatorDirectory)
                           ?? Rpcs3PatchesService.PatchEngineVersion;

        var url = string.Format(DownloadEndpointTemplate, Uri.EscapeDataString(patchVersion));
        var patchPath = Rpcs3PatchesService.GetPatchYmlPath(emulatorDirectory);

        if (File.Exists(patchPath))
        {
            try
            {
                var existingText = await File.ReadAllTextAsync(patchPath, cancellationToken).ConfigureAwait(false);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(existingText))).ToLowerInvariant();
                url += "&sha256=" + hash;
            }
            catch (Exception ex)
            {
                return new Rpcs3PatchesDownloadResult(false, $"Failed to hash existing patch file: {ex.Message}", false);
            }
        }

        string jsonText;
        try
        {
            jsonText = await Client.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new Rpcs3PatchesDownloadResult(false, $"Failed to download patches: {ex.Message}", false);
        }

        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;

        if (!root.TryGetProperty("return_code", out var returnCodeElement) ||
            !returnCodeElement.TryGetInt32(out var returnCode))
        {
            return new Rpcs3PatchesDownloadResult(false, "Patch server response did not include a return code.", false);
        }

        if (returnCode == 1)
            return new Rpcs3PatchesDownloadResult(true, "Patch file is already up to date.", true);

        if (returnCode != 0)
        {
            var message = returnCode switch
            {
                -1 => "No patches found for the specified version.",
                -2 => "Patch server is in maintenance mode.",
                -3 => "Patch server rejected the request.",
                _ => $"Patch server returned error code {returnCode}."
            };

            return new Rpcs3PatchesDownloadResult(false, message, false);
        }

        if (!root.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
        {
            return new Rpcs3PatchesDownloadResult(false, "Patch server response did not include a version.", false);
        }

        var responseVersion = versionElement.GetString();
        if (!string.Equals(responseVersion, patchVersion, StringComparison.Ordinal))
        {
            return new Rpcs3PatchesDownloadResult(
                false,
                $"Patch server version mismatch (expected {patchVersion}, received {responseVersion}).",
                false);
        }

        if (!root.TryGetProperty("sha256", out var sha256Element) ||
            sha256Element.ValueKind != JsonValueKind.String)
        {
            return new Rpcs3PatchesDownloadResult(false, "Patch server response did not include a checksum.", false);
        }

        if (!root.TryGetProperty("patch", out var patchElement) ||
            patchElement.ValueKind != JsonValueKind.String)
        {
            return new Rpcs3PatchesDownloadResult(false, "Patch server response did not include patch content.", false);
        }

        var patchContent = patchElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(patchContent))
            return new Rpcs3PatchesDownloadResult(false, "Patch server returned empty patch content.", false);

        var expectedHash = sha256Element.GetString() ?? string.Empty;
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patchContent))).ToLowerInvariant();
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            return new Rpcs3PatchesDownloadResult(false, "Downloaded patch content failed checksum validation.", false);

        try
        {
            var patchesDirectory = Rpcs3PatchesService.GetPatchesDirectory(emulatorDirectory);
            Directory.CreateDirectory(patchesDirectory);

            if (File.Exists(patchPath))
            {
                var backupPath = patchPath + ".old";
                File.Copy(patchPath, backupPath, overwrite: true);
            }

            await File.WriteAllTextAsync(patchPath, patchContent, cancellationToken).ConfigureAwait(false);

            if (!Rpcs3PatchYamlLoader.TryLoadRoot(patchPath, out _, out var parseError))
            {
                return new Rpcs3PatchesDownloadResult(
                    false,
                    parseError ?? "Downloaded patch file could not be parsed.",
                    false);
            }

            return new Rpcs3PatchesDownloadResult(true, "Downloaded the latest RPCS3 patch file.", false);
        }
        catch (Exception ex)
        {
            return new Rpcs3PatchesDownloadResult(false, $"Failed to save patch file: {ex.Message}", false);
        }
    }
}
