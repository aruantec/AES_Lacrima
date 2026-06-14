using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Lacrima.Services.Emulation;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services;

public partial class MetadataService
{
    /// <summary>
    /// Fetches emulation box art online using the same title-based query as the metadata editor,
    /// persists to a sidecar <c>.cover</c> file, and applies it to the item.
    /// </summary>
    public async Task<bool> TryFetchEmulationCoverOnlineAsync(
        MediaItem item,
        string? albumName,
        CancellationToken cancellationToken = default,
        AutoCoverLookupOptions? options = null)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FileName))
            return false;

        options ??= AutoCoverLookupOptions.FastSkip;

        var acquired = false;
        using var budgetCts = options.TotalBudgetSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (budgetCts != null)
            budgetCts.CancelAfter(TimeSpan.FromSeconds(options.TotalBudgetSeconds));

        var effectiveToken = budgetCts?.Token ?? cancellationToken;

        try
        {
            await AutoCoverLookupThrottle.WaitAsync(cancellationToken);
            acquired = true;

            if (EmulationCoverCacheHelper.HasCover(item.FileName))
                return await TryApplyEmulationCoverSidecarAsync(item, effectiveToken).ConfigureAwait(false);

            if (await IsCoverLookupAlreadyScannedAsync(item.FileName, effectiveToken).ConfigureAwait(false))
            {
                if (EmulationCoverCacheHelper.TryEnsureCoverSidecar(item.FileName))
                    return await TryApplyEmulationCoverSidecarAsync(item, effectiveToken).ConfigureAwait(false);

                if (options.MarkExhaustedOnFailure)
                    await MarkCoverLookupCompleteAsync(item, coverFound: false).ConfigureAwait(false);
                return false;
            }

            await TryApplyTitleFromPs3InstalledGameAsync(item, effectiveToken).ConfigureAwait(false);

            if (await TryApplyCoverFromPs3InstalledGameAsync(item, effectiveToken).ConfigureAwait(false))
                return true;

            if (await TryApplyCoverFromPs4InstalledGameAsync(item, effectiveToken).ConfigureAwait(false))
                return true;

            var searchQueries = BuildAutoCoverQueries(item, albumName)
                .Take(MaxAutoCoverQueries)
                .ToList();
            if (searchQueries.Count == 0)
                return false;

            foreach (var searchQuery in searchQueries)
            {
                effectiveToken.ThrowIfCancellationRequested();

                var candidates = AutoCoverImageHeuristics.RankCandidates(
                    await FindImageResultsForAutoCoverAsync(searchQuery, effectiveToken, options).ConfigureAwait(false));
                if (candidates.Count == 0)
                    continue;

                var download = await TryDownloadFirstViableAutoCoverAsync(candidates, effectiveToken, options)
                    .ConfigureAwait(false);
                if (download == null)
                    continue;

                await SaveEmulationCoverSidecarAsync(item, download.Value.Bytes).ConfigureAwait(false);
                await TryApplyEmulationCoverSidecarAsync(item, effectiveToken).ConfigureAwait(false);
                await MarkCoverLookupCompleteAsync(item, coverFound: true).ConfigureAwait(false);
                SLog.Info($"Emulation cover applied for '{item.Title}' using query '{searchQuery}'.");
                return true;
            }

            if (options.MarkExhaustedOnFailure)
                await MarkCoverLookupCompleteAsync(item, coverFound: false).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (options.MarkExhaustedOnTimeout)
                await MarkCoverLookupCompleteAsync(item, coverFound: false).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            SLog.Warn($"Failed to fetch emulation cover for {item.FileName}", ex);
            return false;
        }
        finally
        {
            if (acquired)
                AutoCoverLookupThrottle.Release();
        }
    }

    public Task<bool> TryApplyEmulationCoverSidecarAsync(MediaItem item, CancellationToken cancellationToken = default) =>
        EmulationCoverLoaderService.ApplyLocalCoverToItemAsync(item, cancellationToken);

    private async Task SaveEmulationCoverSidecarAsync(MediaItem item, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(item.FileName) || bytes.Length == 0)
            return;

        await Task.Run(() =>
        {
            EmulationCoverCacheHelper.WriteCoverFromBytes(item.FileName, bytes);

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
            metadata.CoverScanned = true;
            metadata.CoverLookupExhausted = false;
            if (!string.IsNullOrWhiteSpace(item.Title))
                metadata.Title = item.Title;
            if (!string.IsNullOrWhiteSpace(item.Album))
                metadata.Album = item.Album;

            metadata.Images = metadata.Images
                .Where(image => image.Kind != TagImageKind.Cover)
                .ToList();
            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
        }).ConfigureAwait(false);
    }
}
