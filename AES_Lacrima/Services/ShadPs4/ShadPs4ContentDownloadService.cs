using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AES_Lacrima.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AES_Lacrima.Services.ShadPs4;

public static class ShadPs4ContentDownloadService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static string GetPatchesRootDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "user", "patches");
    }

    public static string GetPatchesRepositoryDirectory(string? emulatorDirectory, string repositoryId) =>
        Path.Combine(GetPatchesRootDirectory(emulatorDirectory), repositoryId);

    public static async Task<ShadPs4DownloadResult> DownloadCheatsAsync(
        string? emulatorDirectory,
        string titleId,
        string? gameVersion,
        ShadPs4ContentRepository repository,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return Fail("Emulator directory is not configured.");

        if (string.IsNullOrWhiteSpace(titleId))
            return Fail("Title ID is required to download cheats.");

        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var normalizedVersion = string.IsNullOrWhiteSpace(gameVersion) ? string.Empty : gameVersion.Trim();

        var cheatsDirectory = ShadPs4CheatsService.GetCheatsDirectory(emulatorDirectory);
        Directory.CreateDirectory(cheatsDirectory);

        var (manifestUrl, baseUrl, suffix) = repository.Id switch
        {
            "GoldHEN" => (
                "https://raw.githubusercontent.com/GoldHEN/GoldHEN_Cheat_Repository/main/json.txt",
                "https://raw.githubusercontent.com/GoldHEN/GoldHEN_Cheat_Repository/main/json/",
                "_GoldHEN"),
            "shadPS4" => (
                "https://raw.githubusercontent.com/shadps4-emu/ps4_cheats/main/CHEATS_JSON.txt",
                "https://raw.githubusercontent.com/shadps4-emu/ps4_cheats/main/CHEATS/",
                "_shadPS4"),
            _ => throw new ArgumentOutOfRangeException(nameof(repository), repository.Id, "Unsupported cheat repository.")
        };

        string manifestText;
        try
        {
            manifestText = await Client.GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to download cheat index from {repository.DisplayName}: {ex.Message}");
        }

        var pattern = string.IsNullOrWhiteSpace(normalizedVersion)
            ? $@"{Regex.Escape(normalizedTitleId)}_[^=\s]*\.json"
            : $@"{Regex.Escape(normalizedTitleId)}_{Regex.Escape(normalizedVersion)}[^=\s]*\.json";

        var matches = Regex.Matches(manifestText, pattern, RegexOptions.IgnoreCase);
        if (matches.Count == 0)
            return Fail($"No cheats found for {normalizedTitleId} {normalizedVersion} in {repository.DisplayName}.");

        var downloaded = 0;
        var skipped = 0;
        foreach (Match match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteFileName = match.Value;
            if (string.IsNullOrWhiteSpace(remoteFileName))
                continue;

            var localFileName = InsertSuffixBeforeExtension(remoteFileName, suffix);
            var localFilePath = Path.Combine(cheatsDirectory, localFileName);

            if (File.Exists(localFilePath) && !replaceExisting)
            {
                skipped++;
                continue;
            }

            var remoteUrl = baseUrl + remoteFileName;
            try
            {
                var bytes = await Client.GetByteArrayAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(localFilePath, bytes, cancellationToken).ConfigureAwait(false);
                downloaded++;
            }
            catch (Exception ex)
            {
                return Fail($"Failed to download {remoteFileName}: {ex.Message}");
            }
        }

        if (downloaded == 0 && skipped > 0)
        {
            return new ShadPs4DownloadResult
            {
                Success = true,
                FilesDownloaded = 0,
                Message = $"Cheat file(s) from {repository.DisplayName} already exist. Enable replace to overwrite."
            };
        }

        return new ShadPs4DownloadResult
        {
            Success = true,
            FilesDownloaded = downloaded,
            Message = downloaded == 0
                ? $"No new cheat files downloaded from {repository.DisplayName}."
                : $"Downloaded {downloaded} cheat file(s) from {repository.DisplayName}."
        };
    }

    public static async Task<ShadPs4DownloadResult> DownloadPatchesAsync(
        string? emulatorDirectory,
        ShadPs4ContentRepository repository,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return Fail("Emulator directory is not configured.");

        var repositoryDirectory = GetPatchesRepositoryDirectory(emulatorDirectory, repository.Id);
        Directory.CreateDirectory(repositoryDirectory);

        var apiUrl = repository.Id switch
        {
            "shadPS4" => "https://api.github.com/repos/shadps4-emu/ps4_cheats/contents/PATCHES",
            "GoldHEN" => "https://api.github.com/repos/illusion0001/PS4-PS5-Game-Patch/contents/patches/xml",
            _ => throw new ArgumentOutOfRangeException(nameof(repository), repository.Id, "Unsupported patch repository.")
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github.v3+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "AES_Lacrima");

        string json;
        try
        {
            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to list patches from {repository.DisplayName}: {ex.Message}");
        }

        List<GitHubContentEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize(json, ShadPs4JsonContext.Default.ListGitHubContentEntry);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to parse patch index from {repository.DisplayName}: {ex.Message}");
        }

        if (entries == null || entries.Count == 0)
            return Fail($"No patch files found in {repository.DisplayName}.");

        var downloaded = 0;
        foreach (var entry in entries.Where(static entry => entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
                continue;

            var destinationPath = Path.Combine(repositoryDirectory, entry.Name);
            try
            {
                var bytes = await Client.GetByteArrayAsync(entry.DownloadUrl, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
                downloaded++;
            }
            catch (Exception ex)
            {
                return Fail($"Failed to download patch {entry.Name}: {ex.Message}");
            }
        }

        await Task.Run(() => CreatePatchFilesJson(repositoryDirectory), cancellationToken).ConfigureAwait(false);

        return new ShadPs4DownloadResult
        {
            Success = true,
            FilesDownloaded = downloaded,
            Message = downloaded == 0
                ? $"No patch files downloaded from {repository.DisplayName}."
                : $"Downloaded {downloaded} patch file(s) from {repository.DisplayName}."
        };
    }

    public static void CreatePatchFilesJson(string repositoryDirectory)
    {
        if (string.IsNullOrWhiteSpace(repositoryDirectory) || !Directory.Exists(repositoryDirectory))
            return;

        var filesObject = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var xmlPath in Directory.EnumerateFiles(repositoryDirectory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(xmlPath);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            try
            {
                var document = XDocument.Load(xmlPath);
                var titleIds = document
                    .Descendants()
                    .Where(element => string.Equals(element.Name.LocalName, "ID", StringComparison.OrdinalIgnoreCase))
                    .Select(element => element.Value.Trim())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (titleIds.Count > 0)
                    filesObject[fileName] = titleIds;
            }
            catch
            {
            }
        }

        var jsonPath = Path.Combine(repositoryDirectory, "files.json");
        var json = JsonSerializer.Serialize(filesObject, ShadPs4JsonContext.Default.DictionaryStringListString);
        File.WriteAllText(jsonPath, json);
    }

    private static string InsertSuffixBeforeExtension(string fileName, string suffix)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
            return fileName + suffix;

        var baseName = fileName[..^extension.Length];
        return baseName + suffix + extension;
    }

    private static ShadPs4DownloadResult Fail(string message) =>
        new() { Success = false, Message = message };
}
