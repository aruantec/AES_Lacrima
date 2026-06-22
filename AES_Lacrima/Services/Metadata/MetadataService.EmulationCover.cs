using AES_Lacrima.Services.Emulation;
using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Lacrima.ViewModels.SectionHandlers;
using Avalonia.Threading;
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

        options ??= AutoCoverLookupOptions.EmulationAlbumScan;

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

            await EnsureEmulationTitleBeforeCoverLookupAsync(item, albumName, effectiveToken).ConfigureAwait(false);

            if (EmulationCoverCacheHelper.HasCover(item.FileName))
                return await TryApplyEmulationCoverSidecarAsync(item, effectiveToken).ConfigureAwait(false);

            if (EmulationCoverCacheHelper.TryEnsureCoverSidecar(item.FileName))
                return await TryApplyEmulationCoverSidecarAsync(item, effectiveToken).ConfigureAwait(false);

            await TryClearCoverLookupExhaustedForResolvedTitleAsync(item, effectiveToken).ConfigureAwait(false);

            if (await TryFetchEmulationCoverFromHashProvidersAsync(item, albumName, effectiveToken).ConfigureAwait(false))
                return true;

            if (await IsCoverLookupExhaustedAsync(item.FileName, effectiveToken).ConfigureAwait(false))
                return false;

            var searchQueries = BuildAutoCoverQueries(item, albumName)
                .Take(1)
                .ToList();
            if (searchQueries.Count == 0)
                return false;

            foreach (var searchQuery in searchQueries)
            {
                effectiveToken.ThrowIfCancellationRequested();

                var candidates = await FindImageResultsForAutoCoverAsync(searchQuery, effectiveToken, options)
                    .ConfigureAwait(false);
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

    private async Task EnsureEmulationTitleBeforeCoverLookupAsync(
        MediaItem item,
        string? albumName,
        CancellationToken cancellationToken)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FileName))
            return;

        var resolved = await GenericAlbumNormalizer.EnsureRomTitleResolvedAsync(
                item.FileName,
                albumName ?? item.Album,
                item.Title,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(resolved) ||
            string.Equals(item.Title, resolved, StringComparison.Ordinal))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => item.Title = resolved, DispatcherPriority.Background);
    }

    private async Task<bool> TryFetchEmulationCoverFromHashProvidersAsync(
        MediaItem item,
        string? albumName,
        CancellationToken cancellationToken)
    {
        var result = await EmulationOnlineCoverResolver
            .TryResolveCoverAsync(item, albumName, cancellationToken)
            .ConfigureAwait(false);
        if (result == null)
            return false;

        if (!string.IsNullOrWhiteSpace(result.ResolvedTitle) &&
            EmulationOnlineCoverResolver.ShouldApplyResolvedTitle(item, result.ResolvedTitle))
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => item.Title = result.ResolvedTitle,
                DispatcherPriority.Background);
        }

        await SaveEmulationCoverSidecarAsync(item, result.Bytes).ConfigureAwait(false);
        await TryApplyEmulationCoverSidecarAsync(item, cancellationToken).ConfigureAwait(false);
        await MarkCoverLookupCompleteAsync(item, coverFound: true).ConfigureAwait(false);
        SLog.Info($"Emulation cover applied for '{item.Title}' via {result.Source}.");
        return true;
    }

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
