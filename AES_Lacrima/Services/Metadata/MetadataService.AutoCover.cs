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
        public async Task<bool> TryPopulateCoverFromLocalMetadataOrGoogleAsync(MediaItem item, string? albumName, CancellationToken cancellationToken = default)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return false;

            var acquired = false;
            try
            {
                await AutoCoverLookupThrottle.WaitAsync(cancellationToken);
                acquired = true;

                if (await TryApplyCoverFromLocalMetadataAsync(item, cancellationToken).ConfigureAwait(false))
                    return true;

                await TryApplyTitleFromPs3InstalledGameAsync(item, cancellationToken).ConfigureAwait(false);

                if (await TryApplyCoverFromPs3InstalledGameAsync(item, cancellationToken).ConfigureAwait(false))
                    return true;

                if (await TryApplyCoverFromPs4InstalledGameAsync(item, cancellationToken).ConfigureAwait(false))
                    return true;

                var searchQueries = BuildAutoCoverQueries(item, albumName)
                    .Take(MaxAutoCoverQueries)
                    .ToList();
                if (searchQueries.Count == 0)
                    return false;

                SLog.Debug($"Auto cover lookup queries for '{item.FileName}': {string.Join(" | ", searchQueries)}");

                foreach (var searchQuery in searchQueries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var candidates = await FindImageResultsForAutoCoverAsync(searchQuery, cancellationToken).ConfigureAwait(false);
                    if (candidates.Count == 0)
                    {
                        SLog.Debug($"Auto cover lookup returned no candidates for query '{searchQuery}'.");
                        continue;
                    }

                    SLog.Debug($"Auto cover lookup returned {candidates.Count} candidates for query '{searchQuery}'.");

                    foreach (var candidate in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var download = await TryDownloadImageBytesAsync(candidate.FullImageUrl, cancellationToken).ConfigureAwait(false);
                            if (download.Bytes == null || string.IsNullOrWhiteSpace(download.MimeType))
                            {
                                SLog.Debug($"Skipping candidate that could not be downloaded as an image: {candidate.FullImageUrl}");
                                continue;
                            }

                            await ApplyCoverBytesToItemAsync(item, download.Bytes, download.MimeType, cancellationToken).ConfigureAwait(false);
                            await SaveCoverToMetadataCacheAsync(item, download.Bytes, download.MimeType).ConfigureAwait(false);
                            SLog.Info($"Auto cover applied for '{item.Title}' from '{candidate.FullImageUrl}' using query '{searchQuery}'.");
                            return true;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            SLog.Warn($"Failed auto cover candidate for '{item.FileName}' from '{candidate.FullImageUrl}'.", ex);
                        }
                    }
                }

                SLog.Warn($"Auto cover lookup found no usable Bing candidates for '{item.FileName}'.");
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

        private static void AddDistinctQuery(List<string> queries, params string?[] parts)
        {
            var value = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
            value = MultiSpaceRegex.Replace(value, " ").Trim();
            if (!string.IsNullOrWhiteSpace(value) && !queries.Contains(value, StringComparer.OrdinalIgnoreCase))
                queries.Add(value);
        }
    }
}
