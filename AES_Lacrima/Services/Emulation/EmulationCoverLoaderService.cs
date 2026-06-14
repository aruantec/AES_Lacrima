using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.Services;
using AES_Lacrima.ViewModels;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Single cover loading pipeline for EmulationView carousel and grid.
/// Reads sidecar <c>.cover</c> files, migrates legacy metadata covers, and delegates online fetch to <see cref="MetadataService"/>.
/// </summary>
[AutoRegister]
public sealed partial class EmulationCoverLoaderService : ViewModelBase
{
    private static readonly ILog SLog = AES_Core.Logging.LogHelper.For<EmulationCoverLoaderService>();

    private readonly ConcurrentDictionary<string, Task<bool>> _inflightLoads = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOnlineLookupExhausted(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var metadata = BinaryMetadataHelper.LoadMetadata(EmulationCoverCacheHelper.GetMetadataCachePath(filePath));
            return metadata?.CoverLookupExhausted == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures a ROM item has a cover loaded into <see cref="MediaItem.CoverBitmap"/> and <see cref="MediaItem.LocalCoverPath"/>.
    /// Concurrent requests for the same ROM share one in-flight task per load mode (local-only vs full).
    /// </summary>
    public Task<bool> EnsureCoverAsync(
        MediaItem item,
        string? albumTitle,
        MetadataService? metadataService,
        EmulationCoverLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FileName))
            return Task.FromResult(false);

        var inflightKey = BuildInflightKey(item.FileName, request.AllowOnlineLookup);
        if (_inflightLoads.TryGetValue(inflightKey, out var existing))
            return existing;

        var task = LoadCoreAsync(item, albumTitle, metadataService, request, cancellationToken);
        if (!_inflightLoads.TryAdd(inflightKey, task))
            return _inflightLoads[inflightKey];

        return ObserveInflightAsync(inflightKey, task);
    }

    private async Task<bool> ObserveInflightAsync(string key, Task<bool> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            _inflightLoads.TryRemove(key, out _);
        }
    }

    private async Task<bool> LoadCoreAsync(
        MediaItem item,
        string? albumTitle,
        MetadataService? metadataService,
        EmulationCoverLoadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsCoverAppliedToItem(item))
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (!IsCoverAppliedToItem(item))
                            item.IsLoadingCover = true;
                    },
                    DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to mark emulation cover loading for '{item.FileName}'.", ex);
            }
        }

        if (IsCoverAppliedToItem(item))
            return true;

        var romPath = item.FileName!;

        if (EmulationCoverCacheHelper.TryEnsureCoverSidecar(romPath))
        {
            var coverPath = EmulationCoverCacheHelper.GetCoverCachePath(romPath);
            var bytes = EmulationCoverCacheHelper.TryReadCoverBytesFromPath(coverPath);

            if (bytes is { Length: > 0 } &&
                await ApplyLocalCoverToItemAsync(item, bytes, coverPath, cancellationToken).ConfigureAwait(false))
                return true;

            if (bytes == null || bytes.Length == 0)
                EmulationCoverCacheHelper.TryDeleteCoverSidecar(romPath);
        }

        if (!request.AllowOnlineLookup)
            return false;

        if (metadataService == null)
            return false;

        if (IsOnlineLookupExhausted(romPath))
            return false;

        return await metadataService.TryFetchEmulationCoverOnlineAsync(
                item,
                albumTitle,
                cancellationToken,
                request.OnlineOptions)
            .ConfigureAwait(false);
    }

    private static bool IsCoverAppliedToItem(MediaItem item) =>
        item.CoverFound &&
        item.CoverBitmap != null &&
        !string.IsNullOrWhiteSpace(item.LocalCoverPath);

    internal static Task<bool> ApplyLocalCoverToItemAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.FileName))
            return Task.FromResult(false);

        if (!EmulationCoverCacheHelper.TryEnsureCoverSidecar(item.FileName))
            return Task.FromResult(false);

        var coverPath = EmulationCoverCacheHelper.GetCoverCachePath(item.FileName);
        var bytes = EmulationCoverCacheHelper.TryReadCoverBytesFromPath(coverPath);
        if (bytes == null || bytes.Length == 0)
            return Task.FromResult(false);

        return ApplyLocalCoverToItemAsync(item, bytes, coverPath, cancellationToken);
    }

    private static async Task<bool> ApplyLocalCoverToItemAsync(
        MediaItem item,
        byte[] bytes,
        string coverPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(
                () => EmulationCoverCacheHelper.DecodeCoverBytesToBitmap(bytes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SLog.Warn($"Failed to decode emulation cover bytes for '{item.FileName}'.", ex);
            return false;
        }

        if (bitmap == null)
            return false;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.LocalCoverPath = coverPath;
                item.CoverFound = true;
                item.CoverBitmap = bitmap;
                item.IsLoadingCover = false;
            }, DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            SLog.Warn($"Failed to assign emulation cover bitmap for '{item.FileName}'.", ex);
            bitmap.Dispose();
            return false;
        }

        return true;
    }

    private static string BuildInflightKey(string filePath, bool allowOnlineLookup)
    {
        var normalizedKey = NormalizeKey(filePath);
        return allowOnlineLookup ? normalizedKey + "|full" : normalizedKey + "|local";
    }

    private static string NormalizeKey(string filePath)
    {
        try
        {
            return Path.GetFullPath(filePath.Trim());
        }
        catch
        {
            return filePath.Trim();
        }
    }
}

public readonly record struct EmulationCoverLoadRequest(
    bool AllowOnlineLookup,
    AutoCoverLookupOptions OnlineOptions)
{
    public static EmulationCoverLoadRequest LocalOnly { get; } = new(false, AutoCoverLookupOptions.FastSkip);

    public static EmulationCoverLoadRequest WithOnline(AutoCoverLookupOptions? options = null) =>
        new(true, options ?? AutoCoverLookupOptions.EmulationAlbumScan);
}

