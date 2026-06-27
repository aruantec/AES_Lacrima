using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Helpers;
using AES_Lacrima.Serialization;
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
using AES_Core.Logging;
using File = System.IO.File;
using Path = System.IO.Path;


namespace AES_Lacrima.Services
{
    public partial class MetadataService : ViewModelBase, IMetadataService 
    {
        private static bool IsAudioMetadataFile(string? path) => MediaCoverPaths.IsAudioMediaFile(path);

        private static IEnumerable<MetadataImageEntry> ToMetadataImageEntries(IEnumerable<TagImageModel> images) =>
            images
                .Where(img => img.Data is { Length: > 0 })
                .Select(img => new MetadataImageEntry(img.Kind, img.Data.ToArray(), img.MimeType));

        private static List<TagImageModel> CreateTagImageModelsFromMetadata(
            CustomMetadata metadata,
            Action<TagImageModel> onDeleteImage)
        {
            return BinaryMetadataHelper.ReadMetadataImages(metadata)
                .Select(entry => new TagImageModel(entry.Kind, entry.Data, entry.MimeType)
                {
                    OnDeleteImage = onDeleteImage
                })
                .ToList();
        }

        private static string BuildGameplayVideoQuery(MediaItem item, string? albumName)
        {
            var title = NormalizeRomSearchTitle(item.Title);
            if (string.IsNullOrWhiteSpace(title))
                title = NormalizeRomSearchTitle(ExtractFilenameForSearch(item.FileName));

            var normalizedAlbum = NormalizeSearchTitle(albumName ?? item.Album);
            var consoleLabel = NormalizeSearchTitle(EmulationConsoleCatalog.GetPreferredBoxArtSearchLabel(normalizedAlbum));
            var query = string.Join(" ",
                new[] { title, consoleLabel, "Gameplay" }
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => part!.Trim()));
            return MultiSpaceRegex.Replace(query, " ").Trim();
        }

        private static async Task<List<WebImageSearchResult>> SearchYouTubeGameplayVideosAsync(string query)
        {
            var results = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var url = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Referrer = new Uri("https://www.youtube.com/");

            using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return results;

            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            foreach (Match match in YouTubeVideoIdRegex.Matches(html))
            {
                var id = match.Groups["id"].Value;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var videoUrl = $"https://www.youtube.com/watch?v={id}";
                if (!seen.Add(videoUrl))
                    continue;

                results.Add(new WebImageSearchResult
                {
                    FullImageUrl = videoUrl,
                    ThumbnailUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                    Title = string.Empty,
                    Artist = "YouTube"
                });

                if (results.Count >= MaxImageSearchResults)
                    break;
            }

            return results;
        }

        private async Task LoadImageAsync(TagImageModel model)
        {
            var ffmpegPath = FFmpegLocator.FindFFmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath) || model.Kind != TagImageKind.LiveWallpaper)
                return;

            try
            {
                var bitmap = await Task.Run(() =>
                {
                    var tempVideoPath = Path.GetTempFileName() + ".mp4";
                    File.WriteAllBytes(tempVideoPath, model.Data);
                    var outputFile = Path.GetTempFileName() + ".png";
                    var psi = new ProcessStartInfo(ffmpegPath, $"-ss 00:00:01 -i \"{tempVideoPath}\" -vframes 1 \"{outputFile}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    process?.WaitForExit();
                    if (process?.ExitCode != 0)
                        throw new Exception("FFmpeg failed");

                    var bmp = new Bitmap(outputFile);
                    File.Delete(tempVideoPath);
                    File.Delete(outputFile);
                    return bmp;
                });

                // Update cache on UI thread
                await Dispatcher.UIThread.InvokeAsync(() => { model.Image = bitmap; });
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to generate thumbnail for live wallpaper", ex);
                // Fallback: set to null or default
                await Dispatcher.UIThread.InvokeAsync(() => { model.Image = null; });
            }
        }

        private static List<string> BuildAutoCoverQueries(MediaItem item, string? albumName)
        {
            var title = NormalizeRomSearchTitle(item.Title);
            if (string.IsNullOrWhiteSpace(title))
                title = NormalizeRomSearchTitle(ExtractFilenameForSearch(item.FileName));

            var albumToResolve = albumName ?? item.Album;
            var preferredConsoleLabel = NormalizeSearchTitle(EmulationConsoleCatalog.GetPreferredBoxArtSearchLabel(albumToResolve));

            var queries = new List<string>();

            // This matches the exact query generated by the manual "Use Title" search button
            AddDistinctQuery(queries, title, preferredConsoleLabel, "cover art");

            return queries;
        }

        private static List<string> BuildWallpaperBezelSearchQueries(MediaItem item, string? albumName)
        {
            var title = NormalizeRomSearchTitle(item.Title);
            if (string.IsNullOrWhiteSpace(title))
                title = NormalizeRomSearchTitle(ExtractFilenameForSearch(item.FileName));

            return BuildWallpaperBezelSearchQueriesFromTitle(title, albumName ?? item.Album);
        }

        private static List<string> BuildWallpaperBezelSearchQueriesFromTitle(string? title, string? albumName = null)
        {
            var normalizedTitle = NormalizeSearchTitle(title);
            var sectionLabel = NormalizeSearchTitle(EmulationConsoleCatalog.GetPreferredBoxArtSearchLabel(albumName));
            var queries = new List<string>();

            AddDistinctQuery(queries, normalizedTitle, "wallpaper", "wide", sectionLabel);

            if (!string.IsNullOrWhiteSpace(sectionLabel))
            {
                AddDistinctQuery(queries, normalizedTitle, sectionLabel, "bezel");
                AddDistinctQuery(queries, normalizedTitle, sectionLabel, "marquee");
            }

            return queries;
        }

        private Task<IReadOnlyList<WebImageSearchResult>> FindImageResultsForAutoCoverAsync(string query, CancellationToken cancellationToken)
            => FindImageResultsForAutoCoverAsync(query, cancellationToken, AutoCoverLookupOptions.Default);

        private async Task<IReadOnlyList<WebImageSearchResult>> FindImageResultsForAutoCoverAsync(
            string query,
            CancellationToken cancellationToken,
            AutoCoverLookupOptions options)
        {
            using var searchTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            searchTimeout.CancelAfter(TimeSpan.FromSeconds(options.SearchTimeoutSeconds));

            try
            {
                // Match the metadata editor "Use Title" ROM search pipeline and keep ranking order.
                var results = await SearchWebImagesAsync([query], isRomSearch: true)
                    .WaitAsync(searchTimeout.Token)
                    .ConfigureAwait(false);

                var limit = Math.Max(options.MaxCandidatesPerQuery, MaxImageSearchResults);
                return results
                    .Where(candidate => !AutoCoverImageHeuristics.ShouldSkipSearchResultUrl(candidate.FullImageUrl))
                    .Take(limit)
                    .ToList();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SLog.Debug($"Auto cover image search timed out for query '{query}'.");
                return [];
            }
        }

        private static bool HasPersistedCoverImageInMetadata(CustomMetadata? metadata) =>
            metadata?.Images?.Any(image => image.Kind == TagImageKind.Cover && image.Data.Length > 0) == true;

        private static bool HasPersistedCoverImage(CustomMetadata? metadata, string? filePath = null)
        {
            if (HasPersistedCoverImageInMetadata(metadata))
                return true;

            if (string.IsNullOrWhiteSpace(filePath) || IsAudioMetadataFile(filePath))
                return false;

            return EmulationCoverCacheHelper.HasCover(filePath);
        }

        private static void SanitizeStaleCoverScannedFlags(CustomMetadata? metadata, string cachePath, string? filePath = null)
        {
            if (metadata == null)
                return;

            var changed = false;
            if (metadata.CoverScanned && !HasPersistedCoverImage(metadata, filePath))
            {
                metadata.CoverScanned = false;
                changed = true;
            }

            if (metadata.CoverLookupExhausted && HasPersistedCoverImage(metadata, filePath))
            {
                metadata.CoverLookupExhausted = false;
                changed = true;
            }

            if (changed)
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
        }

        private async Task TryApplyLocalMetadataTitlesAsync(
            MediaItem item,
            CustomMetadata? metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata == null)
                return;

            var resolvedTitle = NintendoDiscMetadataHelper.ResolveBestTitle(metadata.Title, item.FileName, metadata);
            var shouldApplyTitle = !string.IsNullOrWhiteSpace(resolvedTitle) &&
                                   (string.IsNullOrWhiteSpace(item.Title) ||
                                    NintendoDiscMetadataHelper.IsFilenameLikeTitle(item.Title, item.FileName));

            var shouldApplyAlbum = !string.IsNullOrWhiteSpace(metadata.Album) &&
                                   (string.IsNullOrWhiteSpace(item.Album) ||
                                    string.Equals(item.Album.Trim(), Path.GetFileNameWithoutExtension(item.FileName), StringComparison.OrdinalIgnoreCase));

            if (!shouldApplyTitle && !shouldApplyAlbum)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (shouldApplyTitle)
                    item.Title = resolvedTitle;

                if (shouldApplyAlbum)
                    item.Album = metadata.Album;
            }, DispatcherPriority.Background);
        }

        private async Task<bool> TryApplyCoverFromLocalMetadataAsync(MediaItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = await Task.Run(() =>
            {
                var loaded = BinaryMetadataHelper.LoadMetadata(cachePath);
                if (loaded != null)
                    SanitizeStaleCoverScannedFlags(loaded, cachePath, item.FileName);
                return BinaryMetadataHelper.LoadMetadata(cachePath);
            }, cancellationToken).ConfigureAwait(false);

            await TryApplyLocalMetadataTitlesAsync(item, metadata, cancellationToken).ConfigureAwait(false);

            if (IsAudioMetadataFile(item.FileName))
                return await TryApplyAudioCoverFromMetadataAsync(item, metadata, cachePath, cancellationToken).ConfigureAwait(false);

            if (EmulationCoverCacheHelper.TryEnsureCoverSidecar(item.FileName))
                return await EmulationCoverLoaderService.ApplyLocalCoverToItemAsync(item, cancellationToken).ConfigureAwait(false);

            var cover = metadata?.Images?.FirstOrDefault(image => image.Kind == TagImageKind.Cover && image.Data.Length > 0);
            if (cover == null)
                return false;

            if (EmulationCoverCacheHelper.WriteCoverFromBytes(item.FileName, cover.Data))
            {
                metadata!.Images = metadata.Images.Where(image => image.Kind != TagImageKind.Cover).ToList();
                metadata.CoverScanned = true;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
                return await EmulationCoverLoaderService.ApplyLocalCoverToItemAsync(item, cancellationToken).ConfigureAwait(false);
            }

            await ApplyCoverBytesToItemAsync(
                    item,
                    cover.Data,
                    cover.MimeType ?? GuessMimeTypeFromBytes(cover.Data),
                    cancellationToken,
                    EmulationCoverCacheHelper.GetCoverCachePath(item.FileName))
                .ConfigureAwait(false);

            return true;
        }

        private async Task<bool> TryApplyAudioCoverFromMetadataAsync(
            MediaItem item,
            CustomMetadata? metadata,
            string cachePath,
            CancellationToken cancellationToken)
        {
            var cover = metadata?.Images?.FirstOrDefault(image => image.Kind == TagImageKind.Cover && image.Data.Length > 0);
            if (cover != null)
            {
                await ApplyCoverBytesToItemAsync(
                        item,
                        cover.Data,
                        cover.MimeType ?? GuessMimeTypeFromBytes(cover.Data),
                        cancellationToken,
                        cachePath)
                    .ConfigureAwait(false);
                return true;
            }

            if (!EmulationCoverCacheHelper.HasCover(item.FileName))
                return false;

            var sidecarBytes = EmulationCoverCacheHelper.TryReadCoverBytes(item.FileName);
            if (sidecarBytes is not { Length: > 0 })
                return false;

            await Task.Run(
                    () => RestoreAudioCoverToMetadataCache(cachePath, item.FileName!, sidecarBytes),
                    cancellationToken)
                .ConfigureAwait(false);

            await ApplyCoverBytesToItemAsync(
                    item,
                    sidecarBytes,
                    GuessMimeTypeFromBytes(sidecarBytes),
                    cancellationToken,
                    cachePath)
                .ConfigureAwait(false);
            return true;
        }

        private static void RestoreAudioCoverToMetadataCache(string cachePath, string filePath, byte[] coverBytes)
        {
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
            var preserved = BinaryMetadataHelper.ReadMetadataImages(metadata)
                .Where(entry => entry.Kind != TagImageKind.Cover)
                .ToList();
            preserved.Insert(0, new MetadataImageEntry(
                TagImageKind.Cover,
                coverBytes.ToArray(),
                GuessMimeTypeFromBytes(coverBytes)));

            metadata.CoverScanned = true;
            metadata.CoverLookupExhausted = false;
            BinaryMetadataHelper.WriteMetadataImages(metadata, preserved);
            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            EmulationCoverCacheHelper.TryDeleteCoverSidecar(filePath);
        }

        private async Task PersistPs3MetadataToMetadataCacheAsync(string? filePath, string? ps3TitleId, string? ps3Version)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(ps3TitleId))
                    metadata.Ps3TitleId = ps3TitleId;
                if (!string.IsNullOrWhiteSpace(ps3Version))
                    metadata.Ps3Version = ps3Version;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task PersistPs4IdToMetadataCacheAsync(string? filePath, string? ps4TitleId)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(ps4TitleId))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                metadata.Ps4TitleId = ps4TitleId;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task PersistPs4MetadataToMetadataCacheAsync(string? filePath, string? ps4TitleId, string? ps4Version)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(ps4TitleId))
                    metadata.Ps4TitleId = ps4TitleId;
                if (!string.IsNullOrWhiteSpace(ps4Version))
                    metadata.Ps4Version = ps4Version;
                 BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task PersistPsXMetadataToMetadataCacheAsync(string? filePath, string? psXTitleId, string? psXVersion, string? titleName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(psXTitleId))
                    metadata.PsXTitleId = psXTitleId;
                if (!string.IsNullOrWhiteSpace(psXVersion))
                    metadata.PsXVersion = psXVersion;
                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task PersistPs2MetadataToMetadataCacheAsync(string? filePath, string? ps2TitleId, string? ps2Version, string? titleName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(ps2TitleId))
                    metadata.Ps2TitleId = ps2TitleId;
                if (!string.IsNullOrWhiteSpace(ps2Version))
                    metadata.Ps2Version = ps2Version;
                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task PersistPspMetadataToMetadataCacheAsync(string? filePath, string? pspTitleId, string? titleName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(pspTitleId))
                    metadata.PspTitleId = pspTitleId;
                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task LoadPspMetadataAsync(MediaItem item)
        {
            if (!EmulationHashTitleResolver.IsSupportedAlbum(item.Album, out var platform) || platform == null)
                return;

            var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName!, DiscSection.PSP)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(romInfo?.GameId))
                PspTitleId = romInfo.GameId;

            var resolvedTitle = EmulationHashTitleResolver.TryResolveOffline(item.FileName!, platform)
                ?? romInfo?.InternalTitle;
            if (!string.IsNullOrWhiteSpace(resolvedTitle))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Title = resolvedTitle.Trim();
                    if (_currentSelectedMedia == item)
                        Title = resolvedTitle.Trim();
                }, DispatcherPriority.Background);
            }

            if (!string.IsNullOrWhiteSpace(PspTitleId) || !string.IsNullOrWhiteSpace(resolvedTitle))
                await PersistPspMetadataToMetadataCacheAsync(item.FileName, PspTitleId, resolvedTitle).ConfigureAwait(false);
        }

        private static bool IsPspAlbum(string? albumTitle)
        {
            if (EmulationConsoleCatalog.TryGetDefinition(albumTitle, out var definition))
                return string.Equals(definition.Key, "PSP", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "PlayStation Portable", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PSP", StringComparison.OrdinalIgnoreCase);
        }

        private async Task LoadNintendoDiscMetadataAsync(MediaItem item, DiscSection section, string? albumContext = null)
        {
            var romPath = NintendoDiscMetadataHelper.NormalizeRomPath(item.FileName);
            if (string.IsNullOrWhiteSpace(romPath))
                return;

            var albumTitle = NintendoDiscMetadataHelper.ResolveAlbumTitle(item.Album, albumContext);
            var loadResult = await Task.Run(() =>
                LoadNintendoDiscMetadataCore(romPath, albumTitle, section)).ConfigureAwait(false);

            string? resolvedGameId = null;
            string? extractedTitle = null;
            var resolvedSection = loadResult.Section;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                WiiTitleId = loadResult.WiiTitleId;
                GameCubeTitleId = loadResult.GameCubeTitleId;
                IsWiiMetadata = loadResult.IsWiiMetadata;
                IsGameCubeMetadata = loadResult.IsGameCubeMetadata;
                NotifyNintendoDiscGameIdChanged();
                resolvedGameId = NintendoDiscGameId;
                extractedTitle = loadResult.ExtractedTitle;
            });

            if (!string.IsNullOrWhiteSpace(resolvedGameId) || ShouldUpdateExtractedTitle(item.Title, extractedTitle))
            {
                await ApplyExtractedNintendoTitleAsync(item, extractedTitle, resolvedGameId, resolvedSection)
                    .ConfigureAwait(false);
            }
        }

        private static NintendoDiscLoadResult LoadNintendoDiscMetadataCore(
            string romPath,
            string? albumTitle,
            DiscSection section)
        {
            NintendoDiscInspectionResult inspection;
            try
            {
                inspection = NintendoDiscMetadataHelper.InspectAndPersist(romPath, albumTitle);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Nintendo disc inspection failed for '{romPath}'.", ex);
                inspection = NintendoDiscInspectionResult.Empty;
            }

            section = inspection.Section != DiscSection.Auto
                ? inspection.Section
                : NintendoDiscMetadataHelper.ResolveDiscSection(albumTitle, romPath);

            var cachePath = NintendoDiscMetadataHelper.GetMetadataCachePath(romPath);
            var refreshed = BinaryMetadataHelper.LoadMetadata(cachePath);
            if (refreshed != null)
                NintendoDiscMetadataHelper.TryMigrateMisfiledTitleId(refreshed, section, cachePath);
            refreshed = BinaryMetadataHelper.LoadMetadata(cachePath);

            var wiiTitleId = CoalesceTitleId(
                section == DiscSection.Wii ? inspection.GameId : null,
                refreshed?.WiiTitleId,
                refreshed?.GameCubeTitleId);
            var gameCubeTitleId = CoalesceTitleId(
                section == DiscSection.GameCube ? inspection.GameId : null,
                refreshed?.GameCubeTitleId,
                refreshed?.WiiTitleId);

            if (string.IsNullOrWhiteSpace(wiiTitleId) && string.IsNullOrWhiteSpace(gameCubeTitleId))
            {
                var romInfo = RomInspector.Inspect(romPath, section);
                var gameId = romInfo?.GameId;
                if (section == DiscSection.Wii)
                    wiiTitleId = gameId;
                else
                    gameCubeTitleId = gameId;

                if (string.IsNullOrWhiteSpace(inspection.Title))
                    inspection = inspection with { Title = romInfo?.InternalTitle };
            }

            var isWiiMetadata = section == DiscSection.Wii || !string.IsNullOrWhiteSpace(wiiTitleId);
            var isGameCubeMetadata = section == DiscSection.GameCube || !string.IsNullOrWhiteSpace(gameCubeTitleId);
            var extractedTitle = NintendoDiscMetadataHelper.ResolveBestTitle(
                CoalesceTitleId(inspection.Title, refreshed?.Title),
                romPath,
                refreshed);

            return new NintendoDiscLoadResult(
                section,
                wiiTitleId,
                gameCubeTitleId,
                isWiiMetadata,
                isGameCubeMetadata,
                extractedTitle);
        }

        private readonly record struct NintendoDiscLoadResult(
            DiscSection Section,
            string? WiiTitleId,
            string? GameCubeTitleId,
            bool IsWiiMetadata,
            bool IsGameCubeMetadata,
            string? ExtractedTitle);

        private static string? CoalesceTitleId(params string?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate.Trim();
            }

            return null;
        }

        private async Task LoadSwitchMetadataAsync(MediaItem item)
        {
            var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName!, DiscSection.Switch))
                .ConfigureAwait(false);

            SwitchTitleId = romInfo?.GameId;

            var inspection = await Task.Run(() => SwitchRomMetadataHelper.InspectAndPersist(item.FileName, item.Album))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(SwitchTitleId))
                SwitchTitleId = inspection.TitleId;

            var cachePath = GetMetadataCachePath(item.FileName);
            var cached = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
            var extractedTitle = SwitchRomMetadataHelper.ResolveBestTitle(
                romInfo?.InternalTitle ?? inspection.Title,
                item.FileName,
                cached);

            if (!string.IsNullOrWhiteSpace(SwitchTitleId) ||
                ShouldUpdateExtractedTitle(item.Title, extractedTitle, item.FileName))
            {
                await ApplyExtractedSwitchTitleAsync(item, extractedTitle, SwitchTitleId).ConfigureAwait(false);
            }
        }

        private async Task ApplyExtractedSwitchTitleAsync(MediaItem item, string? extractedTitle, string? titleId)
        {
            string? titleToPersist = null;
            if (ShouldUpdateExtractedTitle(item.Title, extractedTitle, item.FileName))
            {
                titleToPersist = extractedTitle!.Trim();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Title = titleToPersist;
                    if (_currentSelectedMedia == item)
                        Title = titleToPersist;
                }, DispatcherPriority.Background);
            }

            await PersistSwitchMetadataToMetadataCacheAsync(item.FileName, titleId, titleToPersist).ConfigureAwait(false);
        }

        private async Task PersistSwitchMetadataToMetadataCacheAsync(string? filePath, string? titleId, string? titleName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(titleId))
                    metadata.SwitchTitleId = titleId;
                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;

                metadata.RomScanned = true;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private static bool IsSwitchAlbum(string? albumTitle) =>
            SwitchRomMetadataHelper.IsSwitchAlbum(albumTitle);

        private async Task LoadNintendo3dsMetadataAsync(MediaItem item)
        {
            var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName!, DiscSection.Nintendo3ds)).ConfigureAwait(false);
            var titleId = romInfo?.GameId;
            var extractedTitle = romInfo?.InternalTitle;

            Nintendo3dsTitleId = titleId;

            if (!string.IsNullOrWhiteSpace(titleId))
            {
                await ApplyExtractedNintendo3dsTitleAsync(item, extractedTitle, titleId).ConfigureAwait(false);
            }
            else
            {
                var cachePath = GetMetadataCachePath(item.FileName);
                var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(Nintendo3dsTitleId))
                    Nintendo3dsTitleId = refreshed?.Nintendo3dsTitleId;

                extractedTitle = refreshed?.Title;
                if (ShouldUpdateExtractedTitle(item.Title, extractedTitle))
                    await ApplyExtractedNintendo3dsTitleAsync(item, extractedTitle, Nintendo3dsTitleId).ConfigureAwait(false);
            }
        }

        private async Task LoadWiiUMetadataAsync(MediaItem item)
        {
            var resolved = await Task.Run(() => WiiUInstalledGameHelper.ResolveMetadata(item.FileName))
                .ConfigureAwait(false);
            var titleId = resolved.TitleId;
            var extractedTitle = resolved.TitleName;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                WiiUTitleId = titleId;
            }, DispatcherPriority.Background);

            if (!string.IsNullOrWhiteSpace(titleId))
            {
                await ApplyExtractedWiiUTitleAsync(item, extractedTitle, titleId).ConfigureAwait(false);
            }
            else
            {
                var cachePath = GetMetadataCachePath(item.FileName);
                var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(WiiUTitleId))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        WiiUTitleId = refreshed?.WiiUTitleId;
                    }, DispatcherPriority.Background);
                }

                extractedTitle = refreshed?.Title;
                if (ShouldUpdateExtractedTitle(item.Title, extractedTitle))
                    await ApplyExtractedWiiUTitleAsync(item, extractedTitle, WiiUTitleId).ConfigureAwait(false);
            }
        }

        private async Task ApplyExtractedNintendoTitleAsync(
            MediaItem item,
            string? extractedTitle,
            string? gameId,
            DiscSection section)
        {
            string? titleToPersist = null;
            if (ShouldUpdateExtractedTitle(item.Title, extractedTitle))
            {
                titleToPersist = extractedTitle!.Trim();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Title = titleToPersist;
                    if (_currentSelectedMedia == item)
                        Title = titleToPersist;
                }, DispatcherPriority.Background);
            }

            await PersistNintendoDiscMetadataToMetadataCacheAsync(
                item.FileName,
                gameId,
                section,
                titleToPersist).ConfigureAwait(false);
        }

        private async Task ApplyExtractedWiiUTitleAsync(MediaItem item, string? extractedTitle, string? titleId)
        {
            string? titleToPersist = null;
            if (ShouldUpdateExtractedTitle(item.Title, extractedTitle))
            {
                titleToPersist = extractedTitle!.Trim();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Title = titleToPersist;
                    if (_currentSelectedMedia == item)
                        Title = titleToPersist;
                }, DispatcherPriority.Background);
            }

            await PersistWiiUMetadataToMetadataCacheAsync(item.FileName, titleId, titleToPersist).ConfigureAwait(false);
        }

        private async Task ApplyExtractedNintendo3dsTitleAsync(MediaItem item, string? extractedTitle, string? titleId)
        {
            string? titleToPersist = null;
            if (ShouldUpdateExtractedTitle(item.Title, extractedTitle))
            {
                titleToPersist = extractedTitle!.Trim();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Title = titleToPersist;
                    if (_currentSelectedMedia == item)
                        Title = titleToPersist;
                }, DispatcherPriority.Background);
            }

            await PersistNintendo3dsMetadataToMetadataCacheAsync(item.FileName, titleId, titleToPersist).ConfigureAwait(false);
        }

        private static bool ShouldUpdateExtractedTitle(
            string? currentTitle,
            string? extractedTitle,
            string? filePath = null)
        {
            if (string.IsNullOrWhiteSpace(extractedTitle))
                return false;

            if (string.IsNullOrWhiteSpace(currentTitle))
                return true;

            if (!string.IsNullOrWhiteSpace(filePath) &&
                NintendoDiscMetadataHelper.IsFilenameLikeTitle(currentTitle, filePath))
            {
                return true;
            }

            return !string.Equals(currentTitle.Trim(), extractedTitle.Trim(), StringComparison.Ordinal);
        }

        private async Task PersistNintendoDiscMetadataToMetadataCacheAsync(
            string? filePath,
            string? gameId,
            DiscSection section,
            string? titleName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                NintendoDiscMetadataHelper.ApplyTitleIdToMetadata(metadata, gameId, section);

                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;

                metadata.RomScanned = true;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task PersistWiiUMetadataToMetadataCacheAsync(string? filePath, string? titleId, string? titleName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(titleId))
                    metadata.WiiUTitleId = titleId;
                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;

                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task PersistNintendo3dsMetadataToMetadataCacheAsync(string? filePath, string? titleId, string? titleName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var cachePath = GetMetadataCachePath(filePath);
            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(titleId))
                    metadata.Nintendo3dsTitleId = titleId;
                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;

                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private static bool IsGameCubeAlbum(string? albumTitle) =>
            NintendoDiscMetadataHelper.IsGameCubeAlbum(albumTitle);

        private static bool IsWiiAlbum(string? albumTitle) =>
            NintendoDiscMetadataHelper.IsWiiAlbum(albumTitle);

        private static bool IsWiiUAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo Wii U", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "Wii U", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "WiiU", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "WII U", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNintendo3dsAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo 3DS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "3DS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "N3DS", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> TryApplyTitleFromPs4InstalledGameAsync(MediaItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.FileName))
                return false;

            var ps4TitleName = Ps4InstalledGameHelper.GetTitleName(item.FileName);
            if (string.IsNullOrWhiteSpace(ps4TitleName))
                return false;

            var shouldUpdateTitle = string.IsNullOrWhiteSpace(item.Title) || !string.Equals(item.Title.Trim(), ps4TitleName.Trim(), StringComparison.Ordinal);
            var shouldUpdateAlbum = string.IsNullOrWhiteSpace(item.Album) || !string.Equals(item.Album.Trim(), ps4TitleName.Trim(), StringComparison.Ordinal);

            if (!shouldUpdateTitle && !shouldUpdateAlbum)
                return false;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (shouldUpdateTitle)
                {
                    item.Title = ps4TitleName;
                    if (_currentSelectedMedia == item)
                        Title = ps4TitleName;
                }

                if (shouldUpdateAlbum)
                {
                    item.Album = ps4TitleName;
                    if (_currentSelectedMedia == item)
                        Album = ps4TitleName;
                }
            }, DispatcherPriority.Background);

            await SavePs4TitleToMetadataCacheAsync(item, ps4TitleName, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task SavePs4TitleToMetadataCacheAsync(MediaItem item, string titleName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(item.FileName) || string.IsNullOrWhiteSpace(titleName))
                return;

            var cachePath = GetMetadataCachePath(item.FileName);
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                metadata.Title = string.IsNullOrWhiteSpace(item.Title) ? titleName : item.Title;
                metadata.Album = string.IsNullOrWhiteSpace(item.Album) ? titleName : item.Album;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryApplyCoverFromPs3InstalledGameAsync(
            MediaItem item,
            CancellationToken cancellationToken,
            bool persistToCache = true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await TryApplyTitleFromPs3InstalledGameAsync(item, cancellationToken).ConfigureAwait(false);

            var iconPath = Ps3InstalledGameHelper.GetPreferredIconPath(item.FileName);
            if (string.IsNullOrWhiteSpace(iconPath))
                return false;

            byte[] iconBytes;
            try
            {
                iconBytes = await File.ReadAllBytesAsync(iconPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to read PS3 installed-game icon '{iconPath}'.", ex);
                return false;
            }

            if (iconBytes.Length == 0)
                return false;

            byte[]? backCoverBytes = null;
            string? backCoverMimeType = null;
            var backCoverPath = Ps3InstalledGameHelper.GetPreferredBackCoverPath(item.FileName);
            if (!string.IsNullOrWhiteSpace(backCoverPath) && !string.Equals(backCoverPath, iconPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    backCoverBytes = await File.ReadAllBytesAsync(backCoverPath, cancellationToken).ConfigureAwait(false);
                    if (backCoverBytes.Length == 0)
                    {
                        backCoverBytes = null;
                    }
                    else
                    {
                        backCoverMimeType = GuessMimeTypeFromUrl(backCoverPath);
                        if (!backCoverMimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                            backCoverMimeType = GuessMimeTypeFromBytes(backCoverBytes);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to read PS3 installed-game back cover '{backCoverPath}'.", ex);
                }
            }

            var mimeType = GuessMimeTypeFromUrl(iconPath);
            if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mimeType = GuessMimeTypeFromBytes(iconBytes);

            await ApplyCoverBytesToItemAsync(item, iconBytes, mimeType, cancellationToken).ConfigureAwait(false);
            if (persistToCache)
                await SaveCoverToMetadataCacheAsync(item, iconBytes, mimeType, backCoverBytes, backCoverMimeType).ConfigureAwait(false);

            return true;
        }

        private async Task<bool> TryApplyTitleFromPs3InstalledGameAsync(MediaItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.FileName))
                return false;

            var ps3TitleName = Ps3InstalledGameHelper.GetTitleName(item.FileName);
            if (string.IsNullOrWhiteSpace(ps3TitleName))
                return false;

            var normalizedFileName = item.FileName.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folderName = string.IsNullOrWhiteSpace(normalizedFileName) ? string.Empty : Path.GetFileName(normalizedFileName);
            var shouldUpdateTitle = string.IsNullOrWhiteSpace(item.Title) || string.Equals(item.Title.Trim(), folderName, StringComparison.OrdinalIgnoreCase);
            var shouldUpdateAlbum = string.IsNullOrWhiteSpace(item.Album) || string.Equals(item.Album.Trim(), folderName, StringComparison.OrdinalIgnoreCase);

            if (!shouldUpdateTitle && !shouldUpdateAlbum)
                return false;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (shouldUpdateTitle)
                    item.Title = ps3TitleName;

                if (shouldUpdateAlbum)
                    item.Album = ps3TitleName;
            }, DispatcherPriority.Background);

            await SavePs3TitleToMetadataCacheAsync(item, ps3TitleName, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> TryApplyTitleFromPsxGameAsync(MediaItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsPsXMetadata || string.IsNullOrWhiteSpace(item.FileName))
                return false;

            return await TryApplyTitleFromPsGameAsync(item, cancellationToken, preferPs2TitleId: false).ConfigureAwait(false);
        }

        private async Task<bool> TryApplyTitleFromPs2GameAsync(MediaItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsPs2Metadata || string.IsNullOrWhiteSpace(item.FileName))
                return false;

            return await TryApplyTitleFromPsGameAsync(item, cancellationToken, preferPs2TitleId: true).ConfigureAwait(false);
        }

        private async Task<bool> TryApplyTitleFromPsGameAsync(MediaItem item, CancellationToken cancellationToken, bool preferPs2TitleId)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.FileName))
                return false;

            var filePath = item.FileName;
            var titleId = preferPs2TitleId ? Ps2TitleId : PsXTitleId;
            if (string.IsNullOrWhiteSpace(titleId))
            {
                var romInfo = await Task.Run(() => RomInspector.Inspect(filePath, preferPs2TitleId ? DiscSection.PS2 : DiscSection.PSX), cancellationToken).ConfigureAwait(false);
                titleId = romInfo?.GameId;
                if (string.IsNullOrWhiteSpace(titleId))
                    return false;

                if (preferPs2TitleId)
                    Ps2TitleId = titleId;
                else
                    PsXTitleId = titleId;
            }

            var lookup = preferPs2TitleId ? LoadPs2TitleLookup() : LoadPsxTitleLookup();
            if (!lookup.TryGetValue(NormalizeSerialKey(titleId), out var dbTitle) || string.IsNullOrWhiteSpace(dbTitle))
            {
                if (preferPs2TitleId)
                    await PersistPs2MetadataToMetadataCacheAsync(filePath, Ps2TitleId, Ps2Version).ConfigureAwait(false);
                else
                    await PersistPsXMetadataToMetadataCacheAsync(filePath, PsXTitleId, PsXVersion).ConfigureAwait(false);

                return true;
            }

            var shouldUpdateTitle = string.IsNullOrWhiteSpace(item.Title) || !string.Equals(item.Title.Trim(), dbTitle.Trim(), StringComparison.Ordinal);
            if (shouldUpdateTitle)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Title = dbTitle;
                    if (_currentSelectedMedia == item)
                        Title = dbTitle;
                }, DispatcherPriority.Background);
            }

            if (preferPs2TitleId)
                await PersistPs2MetadataToMetadataCacheAsync(filePath, Ps2TitleId, Ps2Version, dbTitle).ConfigureAwait(false);
            else
                await PersistPsXMetadataToMetadataCacheAsync(filePath, PsXTitleId, PsXVersion, dbTitle).ConfigureAwait(false);

            return true;
        }

        private static string? ResolvePsTitle(string? titleId, bool preferPs2TitleId)
        {
            if (string.IsNullOrWhiteSpace(titleId))
                return null;

            var lookup = preferPs2TitleId ? LoadPs2TitleLookup() : LoadPsxTitleLookup();
            return lookup.TryGetValue(NormalizeSerialKey(titleId), out var title) ? title : null;
        }

        private static Dictionary<string, string> LoadPsxTitleLookup()
        {
            if (_psxTitleLookup != null)
                return _psxTitleLookup;

            lock (PsTitleLookupLock)
            {
                if (_psxTitleLookup != null)
                    return _psxTitleLookup;

                _psxTitleLookup = LoadTitleLookupFromDatabase("psx.json");
                return _psxTitleLookup;
            }
        }

        private static Dictionary<string, string> LoadPs2TitleLookup()
        {
            if (_ps2TitleLookup != null)
                return _ps2TitleLookup;

            lock (PsTitleLookupLock)
            {
                if (_ps2TitleLookup != null)
                    return _ps2TitleLookup;

                _ps2TitleLookup = LoadTitleLookupFromDatabase("ps2.json");
                return _ps2TitleLookup;
            }
        }

        private static Dictionary<string, string> LoadTitleLookupFromDatabase(string fileName)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var json = EmbeddedDatabaseResource.ReadText(fileName);
            if (string.IsNullOrWhiteSpace(json))
                return lookup;

            try
            {
                var entries = JsonSerializer.Deserialize(json, RomTitleDatabaseJsonContext.Default.ListRomTitleEntry) ?? [];

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry?.Serial) || string.IsNullOrWhiteSpace(entry.Title))
                        continue;

                    var serial = NormalizeSerialKey(entry.Serial);
                    if (string.IsNullOrWhiteSpace(serial))
                        continue;

                    if (!lookup.ContainsKey(serial))
                        lookup[serial] = entry.Title.Trim();
                }
            }
            catch (Exception logEx) { SLog.Warn("Non-critical error", logEx); }

            return lookup;
        }

        private static string NormalizeSerialKey(string serial)
        {
            return serial.Trim()
                         .Replace(' ', '-')
                         .Replace('_', '-')
                         .Replace('.', '-')
                         .ToUpperInvariant();
        }

        private async Task SavePs3TitleToMetadataCacheAsync(MediaItem item, string titleName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(item.FileName) || string.IsNullOrWhiteSpace(titleName))
                return;

            var cachePath = GetMetadataCachePath(item.FileName);
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                metadata.Title = string.IsNullOrWhiteSpace(item.Title) ? titleName : item.Title;
                metadata.Album = string.IsNullOrWhiteSpace(item.Album) ? titleName : item.Album;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryApplyCoverFromPs4InstalledGameAsync(
            MediaItem item,
            CancellationToken cancellationToken,
            bool persistToCache = true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var iconPath = Ps4InstalledGameHelper.GetPreferredIconPath(item.FileName);
            if (string.IsNullOrWhiteSpace(iconPath))
                return false;

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(iconPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to read PS4 installed-game icon '{iconPath}'.", ex);
                return false;
            }

            if (bytes.Length == 0)
                return false;

            var mimeType = GuessMimeTypeFromUrl(iconPath);
            if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mimeType = GuessMimeTypeFromBytes(bytes);

            await ApplyCoverBytesToItemAsync(item, bytes, mimeType, cancellationToken).ConfigureAwait(false);
            if (persistToCache)
                await SaveCoverToMetadataCacheAsync(item, bytes, mimeType).ConfigureAwait(false);
            return true;
        }

        private static string GetMetadataCachePath(string? filePath) =>
            NintendoDiscMetadataHelper.GetMetadataCachePath(filePath);

        public async Task ClearCacheForItemsAsync(IEnumerable<MediaItem> items)
        {
            if (items == null)
                return;

            await Task.Run(() =>
            {
                foreach (var item in items)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(item?.FileName))
                            continue;

                        var cachePath = GetMetadataCachePath(item.FileName);
                        if (File.Exists(cachePath))
                            File.Delete(cachePath);

                        var coverPath = EmulationCoverCacheHelper.GetCoverCachePath(item.FileName);
                        if (File.Exists(coverPath))
                            File.Delete(coverPath);
                    }
                    catch (Exception logEx) { SLog.Warn("Non-critical error", logEx); }
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Background auto-cover search: DuckDuckGo first, then Google/Bing as fallback.
        /// </summary>
        private async Task<IReadOnlyList<WebImageSearchResult>> FindAutoCoverWebImageResultsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            var results = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();

            await LoadDuckDuckGoImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
                await LoadGoogleImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
                await LoadBingImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);

            return results;
        }

        private async Task<IReadOnlyList<WebImageSearchResult>> FindWebImageResultsAsync(string query, CancellationToken cancellationToken)
        {
            var results = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();

            // Prioritize Google to match the "Use Title" expectations
            await LoadGoogleImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);

            if (results.Count == 0)
            {
                await LoadBingImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);
            }

            return results;
        }

        private static async Task LoadDuckDuckGoImageResultsForExactQuery(
            string query,
            HashSet<string> seen,
            List<WebImageSearchResult> sink,
            CancellationToken cancellationToken)
        {
            try
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var mainUrl = $"https://duckduckgo.com/?q={Uri.EscapeDataString(query)}&iax=images&ia=images";
                using var mainRequest = new HttpRequestMessage(HttpMethod.Get, mainUrl);
                mainRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

                using var mainResponse = await ImageHttpClient
                    .SendAsync(mainRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!mainResponse.IsSuccessStatusCode)
                {
                    SLog.Warn($"DuckDuckGo image search returned HTTP {(int)mainResponse.StatusCode} for exact query '{query}'.");
                    return;
                }

                var mainHtml = await mainResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var vqdMatch = Regex.Match(mainHtml, @"vqd=['""](?<vqd>[^'""]+)['""]|vqd=(?<vqd2>[^&'""\s]+)", RegexOptions.IgnoreCase);
                var vqd = vqdMatch.Groups["vqd"].Value;
                if (string.IsNullOrEmpty(vqd))
                    vqd = vqdMatch.Groups["vqd2"].Value;

                if (string.IsNullOrEmpty(vqd))
                {
                    SLog.Debug($"DuckDuckGo image search could not resolve VQD token for query '{query}'.");
                    return;
                }

                var apiUrl = $"https://duckduckgo.com/i.js?l=us-en&o=json&q={Uri.EscapeDataString(query)}&vqd={vqd}&f=,,,";
                using var apiRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                apiRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                apiRequest.Headers.Referrer = new Uri(mainUrl);

                using var apiResponse = await ImageHttpClient
                    .SendAsync(apiRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!apiResponse.IsSuccessStatusCode)
                {
                    SLog.Warn($"DuckDuckGo image API returned HTTP {(int)apiResponse.StatusCode} for exact query '{query}'.");
                    return;
                }

                var json = await apiResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ExtractDuckDuckGoImageResults(json, seen, sink);
                SLog.Debug($"DuckDuckGo image search extracted {sink.Count} candidate URLs for exact query '{query}'.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"DuckDuckGo image search failed for exact query: {query}", ex);
            }
        }

        private static async Task LoadBingImageResultsForExactQuery(string query, HashSet<string> seen, List<WebImageSearchResult> sink, CancellationToken cancellationToken)
        {
            try
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var url = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(query)}&form=HDRSC3&first=1";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                request.Headers.Referrer = new Uri("https://www.bing.com/");

                using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    SLog.Warn($"Bing image search returned HTTP {(int)response.StatusCode} for exact query '{query}'.");
                    return;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ExtractBingImageResults(html, seen, sink);
                SLog.Debug($"Bing image search extracted {sink.Count} candidate URLs for exact query '{query}'.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Bing image search failed for exact query: {query}", ex);
            }
        }

        private static async Task LoadGoogleImageResultsForExactQuery(string query, HashSet<string> seen, List<WebImageSearchResult> sink, CancellationToken cancellationToken)
        {
            try
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var url = $"https://www.google.com/search?hl=en&q={Uri.EscapeDataString(query)}&udm=2";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                request.Headers.Add("Cookie", GoogleConsentCookie);
                request.Headers.Referrer = new Uri("https://www.google.com/");

                using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    SLog.Warn($"Google image search returned HTTP {(int)response.StatusCode} for exact query '{query}'.");
                    return;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ExtractGoogleImageResults(html, seen, sink);
                SLog.Debug($"Google image search extracted {sink.Count} candidate URLs for exact query '{query}'.");

                if (sink.Count == 0)
                {
                    var snippet = html.Length <= 400 ? html : html[..400];
                    SLog.Warn($"Google image search extracted 0 candidates for '{query}'. Response snippet: {snippet}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Google image search failed for exact query: {query}", ex);
            }
        }

        private Task<(byte[]? Bytes, string? MimeType)> TryDownloadImageBytesAsync(string url, CancellationToken cancellationToken)
            => TryDownloadImageBytesAsync(url, cancellationToken, AutoCoverLookupOptions.Default);

        private async Task<(byte[]? Bytes, string? MimeType)> TryDownloadImageBytesAsync(
            string url,
            CancellationToken cancellationToken,
            AutoCoverLookupOptions options)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return (null, null);
            }

            if (AutoCoverImageHeuristics.ShouldSkipSlowDownloadUrl(url))
            {
                SLog.Debug($"Skipping auto cover candidate with slow-download URL pattern: {url}");
                return (null, null);
            }

            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadTimeout.CancelAfter(TimeSpan.FromSeconds(options.DownloadTimeoutSeconds));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

                using var response = await ImageHttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return (null, null);

                if (response.Content.Headers.ContentLength is long contentLength &&
                    contentLength > MaxAutoCoverDownloadBytes)
                {
                    SLog.Debug($"Skipping auto cover candidate larger than {MaxAutoCoverDownloadBytes} bytes: {url}");
                    return (null, null);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(downloadTimeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), downloadTimeout.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    if (buffer.Length + read > MaxAutoCoverDownloadBytes)
                    {
                        SLog.Debug($"Skipping auto cover candidate that exceeded download budget: {url}");
                        return (null, null);
                    }

                    buffer.Write(chunk, 0, read);
                }

                var bytes = buffer.ToArray();
                if (bytes.Length == 0)
                    return (null, null);

                var mimeType = response.Content.Headers.ContentType?.MediaType;
                mimeType ??= GuessMimeTypeFromUrl(uri.AbsolutePath);
                if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return (null, null);

                return (bytes, mimeType);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SLog.Debug($"Auto cover image download timed out: {url}");
                return (null, null);
            }
            catch (Exception ex)
            {
                SLog.Debug($"Auto cover image download failed: {url}", ex);
                return (null, null);
            }
        }

        private async Task SaveCoverToMetadataCacheAsync(MediaItem item, byte[] bytes, string mimeType, byte[]? backCoverBytes = null, string? backCoverMimeType = null)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
                return;

            var cachePath = GetMetadataCachePath(item.FileName);
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                metadata.Title = string.IsNullOrWhiteSpace(item.Title) ? metadata.Title : item.Title;
                metadata.Artist = string.IsNullOrWhiteSpace(item.Artist) ? metadata.Artist : item.Artist;
                metadata.Album = string.IsNullOrWhiteSpace(item.Album) ? metadata.Album : item.Album;
                metadata.Track = item.Track == 0 ? metadata.Track : item.Track;
                metadata.Year = item.Year == 0 ? metadata.Year : item.Year;
                metadata.Duration = item.Duration <= 0 ? metadata.Duration : item.Duration;
                metadata.Genre = string.IsNullOrWhiteSpace(item.Genre) ? metadata.Genre : item.Genre;
                metadata.Comment = string.IsNullOrWhiteSpace(item.Comment) ? metadata.Comment : item.Comment;
                metadata.Lyrics = string.IsNullOrWhiteSpace(item.Lyrics) ? metadata.Lyrics : item.Lyrics;
                metadata.ReplayGainTrackGain = item.ReplayGainTrackGain;
                metadata.ReplayGainAlbumGain = item.ReplayGainAlbumGain;

                var existingEntries = BinaryMetadataHelper.ReadMetadataImages(metadata);
                var existingBackCover = existingEntries.FirstOrDefault(entry =>
                    entry.Kind == TagImageKind.BackCover && entry.Data is { Length: > 0 });

                var preserved = existingEntries
                    .Where(entry => entry.Kind is not TagImageKind.Cover and not TagImageKind.BackCover)
                    .ToList();

                if (backCoverBytes is { Length: > 0 })
                {
                    preserved.Insert(0, new MetadataImageEntry(
                        TagImageKind.BackCover,
                        backCoverBytes.ToArray(),
                        backCoverMimeType ?? GuessMimeTypeFromBytes(backCoverBytes)));
                }
                else if (existingBackCover.Data is { Length: > 0 })
                {
                    preserved.Insert(0, new MetadataImageEntry(
                        TagImageKind.BackCover,
                        existingBackCover.Data.ToArray(),
                        existingBackCover.MimeType));
                }

                if (IsAudioMetadataFile(item.FileName))
                {
                    preserved.Insert(0, new MetadataImageEntry(
                        TagImageKind.Cover,
                        bytes.ToArray(),
                        mimeType));
                }
                else
                {
                    EmulationCoverCacheHelper.WriteCoverFromBytes(item.FileName, bytes);
                }

                metadata.CoverScanned = true;
                metadata.CoverLookupExhausted = false;
                BinaryMetadataHelper.WriteMetadataImages(metadata, preserved);
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private static Task<bool> IsCoverLookupExhaustedAsync(string? filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return Task.FromResult(false);

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cachePath = GetMetadataCachePath(filePath);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                return metadata?.CoverLookupExhausted == true;
            }, cancellationToken);
        }

        private async Task TryClearCoverLookupExhaustedForResolvedTitleAsync(
            MediaItem item,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
                return;

            if (NintendoDiscMetadataHelper.IsFilenameLikeTitle(item.Title, item.FileName))
                return;

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cachePath = GetMetadataCachePath(item.FileName);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                if (metadata?.CoverLookupExhausted != true)
                    return;

                metadata.CoverLookupExhausted = false;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Task<bool> IsCoverLookupAlreadyScannedAsync(string? filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return Task.FromResult(false);

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsAudioMetadataFile(filePath) && EmulationCoverCacheHelper.HasCover(filePath))
                    return true;

                var cachePath = GetMetadataCachePath(filePath);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                if (metadata != null)
                    SanitizeStaleCoverScannedFlags(metadata, cachePath, filePath);

                metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                return metadata?.CoverLookupExhausted == true || HasPersistedCoverImage(metadata, filePath);
            }, cancellationToken);
        }

        private async Task MarkCoverLookupCompleteAsync(MediaItem item, bool coverFound)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
                return;

            await Task.Run(() =>
            {
                var cachePath = GetMetadataCachePath(item.FileName);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (coverFound)
                {
                    metadata.CoverScanned = HasPersistedCoverImage(metadata, item.FileName);
                    metadata.CoverLookupExhausted = false;
                }
                else
                {
                    metadata.CoverLookupExhausted = true;
                }

                if (!string.IsNullOrWhiteSpace(item.Title))
                    metadata.Title = item.Title;
                if (!string.IsNullOrWhiteSpace(item.Album))
                    metadata.Album = item.Album;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.MetadataProcessed = true;
                item.CoverFound = coverFound;
            }, DispatcherPriority.Background);
        }

        private async Task ApplyCoverBytesToItemAsync(MediaItem item, byte[] bytes, string mimeType, CancellationToken cancellationToken, string? cachePath = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isAudio = IsAudioMetadataFile(item.FileName);
            byte[] decodeBytes;
            string resolvedCachePath;
            int maxDimension;

            if (isAudio)
            {
                decodeBytes = bytes;
                resolvedCachePath = cachePath ?? GetMetadataCachePath(item.FileName);
                maxDimension = NormalizedCoverMaxDimension;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(item.FileName))
                    EmulationCoverCacheHelper.WriteCoverFromBytes(item.FileName, bytes);

                var sidecarBytes = !string.IsNullOrWhiteSpace(item.FileName)
                    ? EmulationCoverCacheHelper.TryReadCoverBytes(item.FileName)
                    : null;
                decodeBytes = sidecarBytes is { Length: > 0 }
                    ? sidecarBytes
                    : CoverImageBarCropHelper.TryCropBytes(bytes, item.FileName);
                resolvedCachePath = !string.IsNullOrWhiteSpace(item.FileName)
                    ? EmulationCoverCacheHelper.GetCoverCachePath(item.FileName)
                    : cachePath ?? GetMetadataCachePath(item.FileName);
                maxDimension = EmulationCoverCacheHelper.MaxCoverDimension;
            }

            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(decodeBytes, writable: false);
                try
                {
                    return Bitmap.DecodeToWidth(stream, maxDimension);
                }
                catch
                {
                    stream.Position = 0;
                    return new Bitmap(stream);
                }
            }, cancellationToken).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.LocalCoverPath = resolvedCachePath;
                item.CoverFound = !isAudio;
                item.CoverBitmap = bitmap;
                item.SaveCoverBitmapAction = saveItem =>
                {
                    if (!string.IsNullOrWhiteSpace(saveItem.FileName))
                        _ = SaveCoverToMetadataCacheAsync(saveItem, decodeBytes, mimeType);
                };
            }, DispatcherPriority.Background);
        }

        private static string GuessMimeTypeFromBytes(byte[] bytes)
        {
            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return "image/webp";
            }

            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            {
                return "image/gif";
            }

            return "image/jpeg";
        }

        private static readonly Regex RomParentheticalRegex = new(@"\([^)]*\)", RegexOptions.Compiled);

        private static string NormalizeRomSearchTitle(string? title)
        {
            var normalized = NormalizeSearchTitle(title);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            normalized = RomParentheticalRegex.Replace(normalized, " ");
            normalized = RomDumpTokenRegex.Replace(normalized, " ");
            normalized = RomReleaseTokenRegex.Replace(normalized, " ");
            normalized = normalized.Replace('!', ' ')
                .Replace(',', ' ')
                .Replace('.', ' ')
                .Replace("  ", " ");

            return MultiSpaceRegex.Replace(normalized, " ").Trim();
        }

        private static string StripRomReleaseTokens(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var stripped = RomReleaseTokenRegex.Replace(title, " ");
            return MultiSpaceRegex.Replace(stripped, " ").Trim();
        }
    }
}
