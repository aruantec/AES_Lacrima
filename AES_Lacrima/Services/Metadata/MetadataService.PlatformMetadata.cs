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
        private static readonly HashSet<string> AudioMetadataExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma",
            ".ape", ".wv", ".mpc", ".aiff", ".aif", ".alac"
        };

        private static bool IsAudioMetadataFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && AudioMetadataExtensions.Contains(extension);
        }

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

        private async Task<IReadOnlyList<WebImageSearchResult>> FindImageResultsForAutoCoverAsync(string query, CancellationToken cancellationToken)
        {
            // Use the same search engine pipeline (SearchWebImagesAsync) as the manual "Use Title" button
            // This ensures results are identical between manual search and the background scraper.
            var results = await SearchWebImagesAsync(new[] { query }, isRomSearch: true).ConfigureAwait(false);

            return results
                .Take(MaxAutoCoverCandidatesPerQuery)
                .ToList();
        }

        private async Task<bool> TryApplyCoverFromLocalMetadataAsync(MediaItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath), cancellationToken).ConfigureAwait(false);
            var cover = metadata?.Images?.FirstOrDefault(image => image.Kind == TagImageKind.Cover && image.Data.Length > 0);

            var shouldApplyTitle = !string.IsNullOrWhiteSpace(metadata?.Title) &&
                                   (string.IsNullOrWhiteSpace(item.Title) ||
                                    string.Equals(item.Title.Trim(), Path.GetFileNameWithoutExtension(item.FileName), StringComparison.OrdinalIgnoreCase));

            var shouldApplyAlbum = !string.IsNullOrWhiteSpace(metadata?.Album) &&
                                   (string.IsNullOrWhiteSpace(item.Album) ||
                                    string.Equals(item.Album.Trim(), Path.GetFileNameWithoutExtension(item.FileName), StringComparison.OrdinalIgnoreCase));

            if (cover == null)
            {
                if (metadata?.CoverScanned == true)
                {
                    if (shouldApplyTitle)
                        await Dispatcher.UIThread.InvokeAsync(() => item.Title = metadata!.Title);

                    if (shouldApplyAlbum)
                        await Dispatcher.UIThread.InvokeAsync(() => item.Album = metadata!.Album);

                    return true;
                }

                if (!shouldApplyTitle && !shouldApplyAlbum)
                    return false;

                if (shouldApplyTitle)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => item.Title = metadata!.Title);
                }

                if (shouldApplyAlbum)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => item.Album = metadata!.Album);
                }

                return true;
            }

            await ApplyCoverBytesToItemAsync(item, cover.Data, cover.MimeType ?? GuessMimeTypeFromBytes(cover.Data), cancellationToken, cachePath)
                .ConfigureAwait(false);

            if (shouldApplyTitle)
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.Title = metadata!.Title);
            }

            if (shouldApplyAlbum)
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.Album = metadata!.Album);
            }

            return true;
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

        private async Task LoadNintendoDiscMetadataAsync(MediaItem item, DiscSection section)
        {
            var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName!, section)).ConfigureAwait(false);
            var gameId = romInfo?.GameId;
            var extractedTitle = romInfo?.InternalTitle;

            if (section == DiscSection.Wii)
                WiiTitleId = gameId;
            else
                GameCubeTitleId = gameId;

            if (!string.IsNullOrWhiteSpace(gameId))
            {
                await ApplyExtractedNintendoTitleAsync(item, extractedTitle, gameId, section).ConfigureAwait(false);
            }
            else
            {
                var cachePath = GetMetadataCachePath(item.FileName);
                var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                if (section == DiscSection.Wii)
                {
                    if (string.IsNullOrWhiteSpace(WiiTitleId))
                        WiiTitleId = refreshed?.WiiTitleId;
                }
                else if (string.IsNullOrWhiteSpace(GameCubeTitleId))
                {
                    GameCubeTitleId = refreshed?.GameCubeTitleId;
                }

                extractedTitle = refreshed?.Title;
                if (ShouldUpdateExtractedTitle(item.Title, extractedTitle))
                    await ApplyExtractedNintendoTitleAsync(item, extractedTitle, gameId, section).ConfigureAwait(false);
            }
        }

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
            var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName!, DiscSection.WiiU)).ConfigureAwait(false);
            var titleId = romInfo?.GameId ?? WiiUInstalledGameHelper.GetTitleId(item.FileName);
            var extractedTitle = romInfo?.InternalTitle ?? WiiUInstalledGameHelper.GetTitleName(item.FileName);

            WiiUTitleId = titleId;

            if (!string.IsNullOrWhiteSpace(titleId))
            {
                await ApplyExtractedWiiUTitleAsync(item, extractedTitle, titleId).ConfigureAwait(false);
            }
            else
            {
                var cachePath = GetMetadataCachePath(item.FileName);
                var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(WiiUTitleId))
                    WiiUTitleId = refreshed?.WiiUTitleId;

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

        private static bool ShouldUpdateExtractedTitle(string? currentTitle, string? extractedTitle)
        {
            if (string.IsNullOrWhiteSpace(extractedTitle))
                return false;

            return string.IsNullOrWhiteSpace(currentTitle) ||
                   !string.Equals(currentTitle.Trim(), extractedTitle.Trim(), StringComparison.Ordinal);
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
                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    if (section == DiscSection.Wii)
                        metadata.WiiTitleId = gameId;
                    else
                        metadata.GameCubeTitleId = gameId;
                }

                if (!string.IsNullOrWhiteSpace(titleName))
                    metadata.Title = titleName;

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

        private static bool IsGameCubeAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo GameCube", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "GameCube", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "GCN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "GC", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWiiAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo Wii", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "Wii", StringComparison.OrdinalIgnoreCase);
        }

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

        private static string GetMetadataCachePath(string? filePath)
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(filePath ?? string.Empty);
            return ApplicationPaths.GetCacheFile(cacheId + ".meta");
        }

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
                    }
                    catch (Exception logEx) { SLog.Warn("Non-critical error", logEx); }
                }
            }).ConfigureAwait(false);
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

        private async Task<(byte[]? Bytes, string? MimeType)> TryDownloadImageBytesAsync(string url, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return (null, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, null);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return (null, null);

            var mimeType = response.Content.Headers.ContentType?.MediaType;
            mimeType ??= GuessMimeTypeFromUrl(uri.AbsolutePath);
            if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return (null, null);

            return (bytes, mimeType);
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

                var preserved = BinaryMetadataHelper.ReadMetadataImages(metadata)
                    .Where(entry => entry.Kind is not TagImageKind.Cover and not TagImageKind.BackCover)
                    .ToList();

                preserved.Insert(0, new MetadataImageEntry(TagImageKind.Cover, bytes.ToArray(), mimeType));

                if (backCoverBytes is { Length: > 0 })
                {
                    preserved.Insert(1, new MetadataImageEntry(
                        TagImageKind.BackCover,
                        backCoverBytes.ToArray(),
                        backCoverMimeType ?? GuessMimeTypeFromBytes(backCoverBytes)));
                }

                metadata.CoverScanned = true;
                BinaryMetadataHelper.WriteMetadataImages(metadata, preserved);
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private static Task<bool> IsCoverLookupAlreadyScannedAsync(string? filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return Task.FromResult(false);

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = BinaryMetadataHelper.LoadMetadata(GetMetadataCachePath(filePath));
                return metadata?.CoverScanned == true;
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
                metadata.CoverScanned = true;
                if (!string.IsNullOrWhiteSpace(item.Title))
                    metadata.Title = item.Title;
                if (!string.IsNullOrWhiteSpace(item.Album))
                    metadata.Album = item.Album;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.MetadataProcessed = true;
                item.CoverFound = true;
            }, DispatcherPriority.Background);
        }

        private async Task ApplyCoverBytesToItemAsync(MediaItem item, byte[] bytes, string mimeType, CancellationToken cancellationToken, string? cachePath = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Bitmap bitmap;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                try
                {
                    bitmap = Bitmap.DecodeToWidth(stream, NormalizedCoverMaxDimension);
                }
                catch
                {
                    stream.Position = 0;
                    bitmap = new Bitmap(stream);
                }
            }

            var resolvedCachePath = cachePath ?? GetMetadataCachePath(item.FileName);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.CoverBitmap = bitmap;
                item.CoverFound = true;
                item.LocalCoverPath = resolvedCachePath;
                item.SaveCoverBitmapAction = saveItem =>
                {
                    _ = SaveCoverToMetadataCacheAsync(saveItem, bytes, mimeType);
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

        private static string NormalizeRomSearchTitle(string? title)
        {
            var normalized = NormalizeSearchTitle(title);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

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
