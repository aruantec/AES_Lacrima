using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Rpcs3;

public sealed record Rpcs3ArtemisCheatImportResult(
    bool Success,
    string Message,
    int CheatsImported,
    string? ResolvedPpuHash = null);

public static class Rpcs3ArtemisCheatImportService
{
    public static Rpcs3ArtemisCheatImportResult ImportRawCheats(
        string? emulatorDirectory,
        string titleId,
        string gameTitle,
        string? appVersion,
        string rawArtemisText,
        string? ppuHash = null)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return Fail("Emulator directory is not configured.");

        if (string.IsNullOrWhiteSpace(titleId))
            return Fail("PS3 Title ID is not available for the selected game.");

        var cheats = Rpcs3ArtemisCheatConverter.ParseRawCheats(rawArtemisText);
        if (cheats.Count == 0)
            return Fail("No valid Artemis cheat blocks were found. Separate cheats with # and use lines like: 0 ADDRESS VALUE.");

        var resolvedHash = ppuHash?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedHash))
        {
            resolvedHash = Rpcs3PatchesService.TryResolvePrimaryPpuHash(
                emulatorDirectory,
                titleId,
                appVersion,
                includeUserPatchCatalogs: false);
            if (string.IsNullOrWhiteSpace(resolvedHash))
            {
                return Fail(
                    "Could not determine a PPU hash for this game. Launch the game once in RPCS3 or use the async import path with automatic PPU detection.");
            }
        }

        return SaveImportedCheats(
            emulatorDirectory,
            titleId,
            cheats,
            resolvedHash,
            gameTitle,
            appVersion);
    }

    public static async Task<Rpcs3ArtemisCheatImportResult> ImportRawCheatsAsync(
        string? emulatorDirectory,
        string titleId,
        string gameTitle,
        string? appVersion,
        string rawArtemisText,
        string? launcherPath = null,
        string? gamePath = null,
        string? ppuHash = null,
        Action<string>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return Fail("Emulator directory is not configured.");

        if (string.IsNullOrWhiteSpace(titleId))
            return Fail("PS3 Title ID is not available for the selected game.");

        var cheats = Rpcs3ArtemisCheatConverter.ParseRawCheats(rawArtemisText);
        if (cheats.Count == 0)
            return Fail("No valid Artemis cheat blocks were found. Separate cheats with # and use lines like: 0 ADDRESS VALUE.");

        var resolvedHash = ppuHash?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedHash))
        {
            statusCallback?.Invoke("Resolving PPU hash...");
            resolvedHash = Rpcs3PatchesService.TryResolvePrimaryPpuHash(
                emulatorDirectory,
                titleId,
                appVersion,
                includeUserPatchCatalogs: false);
        }

        if (string.IsNullOrWhiteSpace(resolvedHash))
        {
            resolvedHash = await Task.Run(
                    async () => await Rpcs3PpuHashProbeService.TryResolveForTitleAsync(
                            emulatorDirectory,
                            titleId,
                            appVersion,
                            launcherPath,
                            gamePath,
                            statusCallback,
                            cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(resolvedHash))
        {
            return Fail(
                "Could not determine a PPU hash for this game. Launch it once in RPCS3, then try importing again.");
        }

        return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return SaveImportedCheats(
                        emulatorDirectory,
                        titleId,
                        cheats,
                        resolvedHash,
                        gameTitle,
                        appVersion);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Rpcs3ArtemisCheatImportResult SaveImportedCheats(
        string emulatorDirectory,
        string titleId,
        IReadOnlyList<Rpcs3ArtemisCheatDefinition> cheats,
        string resolvedHash,
        string gameTitle,
        string? appVersion)
    {
        var yamlChunk = Rpcs3ArtemisCheatConverter.BuildYamlChunk(
            cheats,
            resolvedHash,
            titleId,
            gameTitle,
            appVersion);

        if (string.IsNullOrWhiteSpace(yamlChunk))
            return Fail("Converted cheat data was empty.");

        try
        {
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(emulatorDirectory, Rpcs3PatchCatalog.Imported);
            Directory.CreateDirectory(Rpcs3PatchesService.GetPatchesDirectory(emulatorDirectory)!);

            var merged = Rpcs3ArtemisCheatsDownloadService.MergeChunkIntoExisting(patchPath, yamlChunk);
            File.WriteAllText(patchPath, merged);

            if (!Rpcs3PatchYamlLoader.TryLoadRoot(patchPath, out _, out var parseError))
                return Fail(parseError ?? "Imported Artemis cheats could not be parsed.");

            var normalizedTitleId = titleId.Trim().ToUpperInvariant();
            return new Rpcs3ArtemisCheatImportResult(
                true,
                $"Imported {cheats.Count} Artemis cheat(s) to imported_patch.yml for {normalizedTitleId} using {resolvedHash}. Enable them in the cheats list and save.",
                cheats.Count,
                resolvedHash);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to save imported Artemis cheats: {ex.Message}");
        }
    }

    private static Rpcs3ArtemisCheatImportResult Fail(string message) =>
        new(false, message, 0);
}
