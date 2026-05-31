using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Rpcs3;

public sealed record Rpcs3ArtemisCheatsDownloadResult(
    bool Success,
    string Message,
    int FilesDownloaded);

public static class Rpcs3ArtemisCheatsDownloadService
{
    public const string RepositoryOwner = "chidreams";
    public const string RepositoryName = "Artemis-Patch-Collection-RPCS3";
    public const string DefaultBranch = "main";

    private const string RawContentBaseUrl =
        "https://raw.githubusercontent.com/chidreams/Artemis-Patch-Collection-RPCS3/main/";

    private const string GitHubTreeApiUrl =
        "https://api.github.com/repos/chidreams/Artemis-Patch-Collection-RPCS3/git/trees/main?recursive=1";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "AES_Lacrima" } },
    };

    private static readonly SemaphoreSlim IndexGate = new(1, 1);
    private static IReadOnlyList<string>? _cachedSourceFileNames;
    private static DateTime _indexCachedAtUtc;

    public static async Task<Rpcs3ArtemisCheatsDownloadResult> DownloadForTitleIdAsync(
        string? emulatorDirectory,
        string titleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return Fail("Emulator directory is not configured.");

        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var sourceFiles = await FindSourceFilesForTitleIdAsync(normalizedTitleId, cancellationToken).ConfigureAwait(false);
        if (sourceFiles.Count == 0)
        {
            return Fail(
                $"No Artemis cheat pack found for title ID {normalizedTitleId} in {RepositoryOwner}/{RepositoryName}.");
        }

        var downloadedChunks = new List<string>(sourceFiles.Count);
        foreach (var fileName in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await DownloadSourceFileAsync(fileName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            downloadedChunks.Add(StripVersionHeader(content));
        }

        if (downloadedChunks.Count == 0)
            return Fail($"Failed to download Artemis cheat content for title ID {normalizedTitleId}.");

        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(emulatorDirectory, Rpcs3PatchCatalog.Imported);
            Directory.CreateDirectory(Rpcs3PatchesService.GetPatchesDirectory(emulatorDirectory)!);

            var merged = MergeIntoExisting(patchPath, downloadedChunks);
            await File.WriteAllTextAsync(patchPath, merged, cancellationToken).ConfigureAwait(false);

            if (!Rpcs3PatchYamlLoader.TryLoadRoot(patchPath, out _, out var parseError))
                return Fail(parseError ?? "Merged Artemis cheat file could not be parsed.");

            return new Rpcs3ArtemisCheatsDownloadResult(
                true,
                $"Downloaded {downloadedChunks.Count} Artemis cheat pack(s) to imported_patch.yml for {normalizedTitleId}.",
                downloadedChunks.Count);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to save Artemis cheats: {ex.Message}");
        }
    }

    public static async Task<Rpcs3ArtemisCheatsDownloadResult> DownloadFullDatabaseAsync(
        string? emulatorDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return Fail("Emulator directory is not configured.");

        var sourceFiles = await GetSourceFileNamesAsync(cancellationToken).ConfigureAwait(false);
        if (sourceFiles.Count == 0)
            return Fail("Artemis cheat repository index is empty.");

        var downloadedChunks = new List<string>(sourceFiles.Count);
        foreach (var fileName in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await DownloadSourceFileAsync(fileName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            downloadedChunks.Add(StripVersionHeader(content));
        }

        if (downloadedChunks.Count == 0)
            return Fail("Failed to download Artemis cheat database from GitHub.");

        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(emulatorDirectory, Rpcs3PatchCatalog.Imported);
            Directory.CreateDirectory(Rpcs3PatchesService.GetPatchesDirectory(emulatorDirectory)!);

            var merged = BuildMergedDocument(Array.Empty<string>(), downloadedChunks);
            await File.WriteAllTextAsync(patchPath, merged, cancellationToken).ConfigureAwait(false);

            if (!Rpcs3PatchYamlLoader.TryLoadRoot(patchPath, out _, out var parseError))
                return Fail(parseError ?? "Downloaded Artemis cheat database could not be parsed.");

            return new Rpcs3ArtemisCheatsDownloadResult(
                true,
                $"Downloaded {downloadedChunks.Count} Artemis cheat pack(s) to imported_patch.yml from {RepositoryOwner}/{RepositoryName}.",
                downloadedChunks.Count);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to save Artemis cheat database: {ex.Message}");
        }
    }

    public static async Task<IReadOnlyList<string>> FindSourceFilesForTitleIdAsync(
        string titleId,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var sourceFiles = await GetSourceFileNamesAsync(cancellationToken).ConfigureAwait(false);

        return sourceFiles
            .Where(fileName => Rpcs3PatchesService
                .ExtractTitleIdsFromText(fileName)
                .Contains(normalizedTitleId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> GetSourceFileNamesAsync(CancellationToken cancellationToken)
    {
        await IndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSourceFileNames != null &&
                DateTime.UtcNow - _indexCachedAtUtc < TimeSpan.FromHours(6))
                return _cachedSourceFileNames;

            var json = await Client.GetStringAsync(new Uri(GitHubTreeApiUrl), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var files = new List<string>();
            if (!document.RootElement.TryGetProperty("tree", out var treeElement))
            {
                _cachedSourceFileNames = files;
                _indexCachedAtUtc = DateTime.UtcNow;
                return files;
            }

            foreach (var entry in treeElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), "blob", StringComparison.Ordinal))
                    continue;

                if (!entry.TryGetProperty("path", out var pathElement))
                    continue;

                var path = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (string.Equals(path, "README.md", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "LICENSE", StringComparison.OrdinalIgnoreCase))
                    continue;

                files.Add(path);
            }

            _cachedSourceFileNames = files
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _indexCachedAtUtc = DateTime.UtcNow;
            return _cachedSourceFileNames;
        }
        finally
        {
            IndexGate.Release();
        }
    }

    private static async Task<string?> DownloadSourceFileAsync(string fileName, CancellationToken cancellationToken)
    {
        var url = RawContentBaseUrl + Uri.EscapeDataString(fileName);
        try
        {
            return await Client.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public static string MergeChunkIntoExisting(string patchPath, string yamlChunk) =>
        MergeIntoExisting(patchPath, [yamlChunk]);

    private static string MergeIntoExisting(string patchPath, IReadOnlyList<string> newChunks)
    {
        if (!File.Exists(patchPath))
            return BuildMergedDocument(Array.Empty<string>(), newChunks);

        var existing = StripVersionHeader(File.ReadAllText(patchPath));
        return BuildMergedDocument([existing], newChunks);
    }

    private static string BuildMergedDocument(
        IReadOnlyList<string> existingChunks,
        IReadOnlyList<string> newChunks)
    {
        var builder = new StringBuilder();
        builder.Append("Version: ").Append(Rpcs3PatchesService.PatchEngineVersion).AppendLine();

        foreach (var chunk in existingChunks)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            builder.AppendLine().Append(chunk.Trim()).AppendLine();
        }

        foreach (var chunk in newChunks)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            builder.AppendLine().Append(chunk.Trim()).AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    internal static string StripVersionHeader(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var startIndex = 0;

        while (startIndex < lines.Length)
        {
            var line = lines[startIndex];
            if (string.IsNullOrWhiteSpace(line))
            {
                startIndex++;
                continue;
            }

            if (Rpcs3PatchesService.TryGetRootLevelKey(line, out var key) &&
                string.Equals(key, "Version", StringComparison.OrdinalIgnoreCase))
            {
                startIndex++;
                continue;
            }

            break;
        }

        return startIndex >= lines.Length
            ? string.Empty
            : string.Join('\n', lines.Skip(startIndex)).Trim();
    }

    private static Rpcs3ArtemisCheatsDownloadResult Fail(string message) =>
        new(false, message, 0);
}
