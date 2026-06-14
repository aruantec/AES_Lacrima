using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Helpers;
using AES_Lacrima.Services.Emulation;
using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;
using File = System.IO.File;
using Path = System.IO.Path;


namespace AES_Lacrima.Services
{
    public partial class MetadataService : ViewModelBase, IMetadataService 
    {
        public Task<bool> TryHydrateCoverFromLocalMetadataAsync(MediaItem item, CancellationToken cancellationToken = default)
            => TryApplyCoverFromLocalMetadataAsync(item, cancellationToken);

        public Task<bool> TryPopulateCoverFromLocalMetadataOrGoogleAsync(
            MediaItem item,
            string? albumName,
            CancellationToken cancellationToken = default)
            => TryPopulateCoverFromLocalMetadataOrGoogleAsync(item, albumName, cancellationToken, AutoCoverLookupOptions.Default);

        public async Task<bool> TryPopulateCoverFromLocalMetadataOrGoogleAsync(
            MediaItem item,
            string? albumName,
            CancellationToken cancellationToken,
            AutoCoverLookupOptions options)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return false;

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

                if (await TryApplyCoverFromLocalMetadataAsync(item, effectiveToken).ConfigureAwait(false))
                {
                    await MarkCoverLookupCompleteAsync(item, coverFound: true).ConfigureAwait(false);
                    return true;
                }

                if (await IsCoverLookupAlreadyScannedAsync(item.FileName, effectiveToken).ConfigureAwait(false))
                {
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

                SLog.Debug($"Auto cover lookup queries for '{item.FileName}': {string.Join(" | ", searchQueries)}");

                foreach (var searchQuery in searchQueries)
                {
                    effectiveToken.ThrowIfCancellationRequested();

                    var candidates = AutoCoverImageHeuristics.RankCandidates(
                        await FindImageResultsForAutoCoverAsync(searchQuery, effectiveToken, options).ConfigureAwait(false));
                    if (candidates.Count == 0)
                    {
                        SLog.Debug($"Auto cover lookup returned no candidates for query '{searchQuery}'.");
                        continue;
                    }

                    SLog.Debug($"Auto cover lookup returned {candidates.Count} ranked candidates for query '{searchQuery}'.");

                    var download = await TryDownloadFirstViableAutoCoverAsync(candidates, effectiveToken, options)
                        .ConfigureAwait(false);
                    if (download == null)
                    {
                        SLog.Debug($"Auto cover lookup found no fast viable download for query '{searchQuery}'.");
                        continue;
                    }

                    await SaveCoverToMetadataCacheAsync(item, download.Value.Bytes, download.Value.MimeType).ConfigureAwait(false);
                    await ApplyCoverBytesToItemAsync(item, download.Value.Bytes, download.Value.MimeType, effectiveToken).ConfigureAwait(false);
                    await MarkCoverLookupCompleteAsync(item, coverFound: true).ConfigureAwait(false);
                    SLog.Info($"Auto cover applied for '{item.Title}' using query '{searchQuery}'.");
                    return true;
                }

                SLog.Warn($"Auto cover lookup found no usable Bing candidates for '{item.FileName}'.");
                if (options.MarkExhaustedOnFailure)
                    await MarkCoverLookupCompleteAsync(item, coverFound: false).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SLog.Debug($"Auto cover lookup timed out for '{item.FileName}'.");
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
                SLog.Warn($"Failed to populate auto cover for {item.FileName}", ex);
                return false;
            }
            finally
            {
                if (acquired)
                    AutoCoverLookupThrottle.Release();
            }
        }

        [RelayCommand]
        private void CloseMetadata()
        {
            Close();
        }

        private async Task ReloadImagesFromMetadataCacheAsync(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var metadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(GetMetadataCachePath(filePath))).ConfigureAwait(false);
            if (metadata == null)
                return;

            foreach (var old in Images)
                old.Dispose();

            Images.Clear();
            foreach (var model in CreateTagImageModelsFromMetadata(metadata, OnDeleteImage))
            {
                Images.Add(model);
                if (model.Kind == TagImageKind.LiveWallpaper)
                    await LoadImageAsync(model).ConfigureAwait(false);
            }
        }

        private Task<(byte[] Bytes, string MimeType)?> TryDownloadFirstViableAutoCoverAsync(
            IReadOnlyList<WebImageSearchResult> candidates,
            CancellationToken cancellationToken)
            => TryDownloadFirstViableAutoCoverAsync(candidates, cancellationToken, AutoCoverLookupOptions.Default);

        private async Task<(byte[] Bytes, string MimeType)?> TryDownloadFirstViableAutoCoverAsync(
            IReadOnlyList<WebImageSearchResult> candidates,
            CancellationToken cancellationToken,
            AutoCoverLookupOptions options)
        {
            if (candidates.Count == 0)
                return null;

            var ranked = AutoCoverImageHeuristics.RankCandidates(candidates)
                .Take(options.MaxCandidatesPerQuery)
                .ToList();
            if (ranked.Count == 0)
                return null;

            if (options.PreferSequentialDownloads)
            {
                foreach (var candidate in ranked)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var download = await TryDownloadImageBytesAsync(candidate.FullImageUrl, cancellationToken, options)
                        .ConfigureAwait(false);
                    if (download.Bytes == null || string.IsNullOrWhiteSpace(download.MimeType))
                        continue;

                    if (!TryValidateAutoCoverImageBytes(download.Bytes, out var rejectReason))
                    {
                        SLog.Debug($"Skipping low-quality auto cover candidate ({rejectReason}).");
                        continue;
                    }

                    return (download.Bytes, download.MimeType);
                }

                return null;
            }

            for (int offset = 0; offset < ranked.Count; offset += MaxAutoCoverParallelDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = ranked.Skip(offset).Take(MaxAutoCoverParallelDownloads).ToList();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var tasks = batch
                    .Select(candidate => TryDownloadImageBytesAsync(candidate.FullImageUrl, attemptCts.Token, options))
                    .ToList();

                while (tasks.Count > 0)
                {
                    var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(completed);

                    (byte[]? Bytes, string? MimeType) download;
                    try
                    {
                        download = await completed.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (download.Bytes == null || string.IsNullOrWhiteSpace(download.MimeType))
                        continue;

                    if (!TryValidateAutoCoverImageBytes(download.Bytes, out var rejectReason))
                    {
                        SLog.Debug($"Skipping low-quality auto cover candidate ({rejectReason}).");
                        continue;
                    }

                    attemptCts.Cancel();
                    return (download.Bytes, download.MimeType);
                }
            }

            return null;
        }

        private void Close()
        {
            foreach (var image in Images)
                image.Dispose();

            Images.Clear();
            IsXbox360Metadata = false;
            Xbox360TitleId = null;
            Xbox360MediaId = null;
            IsPsXMetadata = false;
            PsXTitleId = null;
            PsXVersion = null;
            IsPs2Metadata = false;
            Ps2TitleId = null;
            Ps2Version = null;
            IsGameCubeMetadata = false;
            GameCubeTitleId = null;
            IsWiiMetadata = false;
            WiiTitleId = null;
            IsWiiUMetadata = false;
            WiiUTitleId = null;
            IsNintendo3dsMetadata = false;
            Nintendo3dsTitleId = null;
            IsSwitchMetadata = false;
            SwitchTitleId = null;
            IsMetadataLoading = false;
            IsMetadataLoaded = false;
        }

        private void OnDeleteImage(TagImageModel img)
        {
            Dispatcher.UIThread.Post(() => { Images.Remove(img); img.Dispose(); });
        }

        private static string NormalizeSearchTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var normalized = BracketCleanupRegex.Replace(title, " ");
            normalized = normalized.Replace('_', ' ').Replace('|', ' ');

            foreach (var token in NoiseTokens)
                normalized = Regex.Replace(normalized, $@"\b{Regex.Escape(token)}\b", " ", RegexOptions.IgnoreCase);

            normalized = normalized.Replace(" - ", " ");
            normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();
            return normalized;
        }

        private string GetSearchFallbackFromFilename()
        {
            var candidates = new[]
            {
                FilePath,
                _currentSelectedMedia?.FileName
            };

            foreach (var candidate in candidates)
            {
                var normalized = ExtractFilenameForSearch(candidate);
                if (!string.IsNullOrWhiteSpace(normalized))
                    return normalized;
            }

            return string.Empty;
        }

        private static string ExtractFilenameForSearch(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
                return string.Empty;

            string fileName;
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
            {
                fileName = Path.GetFileNameWithoutExtension(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
            }
            else
            {
                fileName = Path.GetFileNameWithoutExtension(pathOrUrl);
            }

            return fileName.Replace('.', ' ')
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Trim();
        }

        private static List<string> BuildMetadataSearchQueries(string? title, string? artist, string? album)
        {
            var queries = new List<string>();

            AddDistinctQuery(queries, title, artist, album);
            AddDistinctQuery(queries, title, artist);
            AddDistinctQuery(queries, title, album);
            AddDistinctQuery(queries, artist, album, title);
            AddDistinctQuery(queries, title);
            AddDistinctQuery(queries, artist, album);
            AddDistinctQuery(queries, artist);
            AddDistinctQuery(queries, album);

            return queries;
        }

        private static bool TryValidateAutoCoverImageBytes(byte[] bytes, out string rejectReason)
        {
            rejectReason = string.Empty;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var bitmap = new Bitmap(stream);
                var width = bitmap.PixelSize.Width;
                var height = bitmap.PixelSize.Height;
                if (AutoCoverImageHeuristics.ShouldRejectDownloadedImage(bytes, width, height))
                {
                    rejectReason = $"{width}x{height}";
                    return false;
                }

                return true;
            }
            catch
            {
                rejectReason = "decode-failed";
                return false;
            }
        }

        private static void AddDistinctQuery(List<string> queries, params string?[] parts)
        {
            var value = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
            value = MultiSpaceRegex.Replace(value, " ").Trim();
            if (!string.IsNullOrWhiteSpace(value) && !queries.Contains(value, StringComparer.OrdinalIgnoreCase))
                queries.Add(value);
        }
    }
}
