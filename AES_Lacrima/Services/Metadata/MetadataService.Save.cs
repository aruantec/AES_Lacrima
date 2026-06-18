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
using System.ComponentModel;
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
        [RelayCommand]
        private async Task SaveMetadataAsync(string? path = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    path = FilePath;

                await SaveToMetadataCacheAsync(path);

                var isMissingFile = string.IsNullOrWhiteSpace(path) || !File.Exists(path);

                if (isMissingFile || (path != null && path.Contains("youtu", StringComparison.OrdinalIgnoreCase)))
                    return;

                if (!IsAudioMetadataFile(path))
                    return;

                if (!string.IsNullOrWhiteSpace(VideoUrl))
                    return;

                try
                {
                    using var tlFile = TagLib.File.Create(path);
                    var tag = tlFile.Tag;
                    Debug.WriteLine($"Tag type: {tag.GetType().FullName}");

                    tag.Title = Title;
                    tag.Performers = string.IsNullOrEmpty(Artists) ? [] : [Artists];
                    tag.Album = Album;
                    tag.Track = Track;
                    tag.Year = Year;
                    tag.Lyrics = Lyrics;
                    tag.Genres = string.IsNullOrEmpty(Genres) ? [] : Genres.Split(';');
                    tag.Comment = Comment;

                    var picList = new List<IPicture>();
                    var coverImage = ResolveEmulationSidecarCoverImage(Images);
                    var wallpaperImage = Images.FirstOrDefault(img => img.Kind == TagImageKind.Wallpaper);
                    foreach (var img in Images)
                    {
                        var pic = new Picture(img.Data.ToArray())
                        {
                            Type = MapKindToPictureType(img),
                            MimeType = img.MimeType,
                            Description = BuildPictureDescription(img)
                        };

                        picList.Add(pic);
                    }

                    tag.Pictures = [.. picList];
                    if (_musicViewModel != null
                        && _musicViewModel?.SelectedMediaItem?.FileName == _currentSelectedMedia?.FileName
                        && _musicViewModel != null
                        && _musicViewModel.AudioPlayer != null)
                    {
                        var (position, wasPlaying) = await _musicViewModel.AudioPlayer.SuspendForEditingAsync();
                        tlFile.Save();
                        await _musicViewModel.AudioPlayer.ResumeAfterEditingAsync(_currentSelectedMedia!.FileName!, position, wasPlaying);
                    }
                    else
                    {
                        tlFile.Save();
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        UpdateInfo();
                        SetMediaItemCoverFromTags(
                            coverImage,
                            wallpaperImage,
                            ApplicationPaths.GetCacheFile(BinaryMetadataHelper.GetCacheId(path ?? string.Empty) + ".meta"));
                    });
                    return;
                }
                catch (Exception ex)
                {
                    SLog.Warn("TagLib save failed, falling back to metadata cache", ex);
                    await SaveToMetadataCacheAsync(path);
                    return;
                }

            }
            catch (Exception ex)
            {
                SLog.Error("Failed to save metadata to file", ex);
            }
            finally
            {
                Close();
            }
        }

        private async Task SaveToMetadataCacheAsync(string? path)
        {
            var resolvedPath = MetadataPathHelper.NormalizeMetadataPath(string.IsNullOrWhiteSpace(path) ? FilePath : path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                resolvedPath = string.IsNullOrWhiteSpace(path) ? FilePath : path;

            if (MediaCoverPaths.UsesEmulationCoverSidecar(resolvedPath))
                resolvedPath = EmulationCoverCacheHelper.ResolveRomPathForCache(resolvedPath);

            var metaDataPath = MediaCoverPaths.UsesEmulationCoverSidecar(resolvedPath)
                ? EmulationCoverCacheHelper.GetMetadataCachePath(resolvedPath)
                : MetadataPathHelper.GetMetadataCachePath(resolvedPath);

            var metaDir = Path.GetDirectoryName(metaDataPath);
            if (!string.IsNullOrEmpty(metaDir) && !Directory.Exists(metaDir))
                Directory.CreateDirectory(metaDir);

            var coverImage = MediaCoverPaths.UsesEmulationCoverSidecar(resolvedPath)
                ? ResolveActiveCoverImage(Images)
                : ResolveEmulationSidecarCoverImage(Images);
            var wallpaperImage = Images.FirstOrDefault(img => img.Kind == TagImageKind.Wallpaper);

            try
            {
                var existingCache = File.Exists(metaDataPath)
                    ? await Task.Run(() => BinaryMetadataHelper.LoadMetadata(metaDataPath)).ConfigureAwait(false)
                    : null;

                var customMetadata = new CustomMetadata
                {
                    Title = Title!,
                    Artist = Artists!,
                    Album = Album!,
                    Track = Track,
                    Year = Year,
                    Lyrics = Lyrics!,
                    Genre = Genres!,
                    Comment = Comment!,
                    VideoUrl = VideoUrl ?? string.Empty,
                    Xbox360TitleId = Xbox360TitleId ?? string.Empty,
                    Xbox360MediaId = Xbox360MediaId ?? string.Empty,
                    PsXTitleId = PsXTitleId ?? string.Empty,
                    PsXVersion = PsXVersion ?? string.Empty,
                    Ps2TitleId = Ps2TitleId ?? string.Empty,
                    Ps2Version = Ps2Version ?? string.Empty,
                    GameCubeTitleId = GameCubeTitleId ?? string.Empty,
                    WiiTitleId = WiiTitleId ?? string.Empty,
                    WiiUTitleId = WiiUTitleId ?? string.Empty,
                    Nintendo3dsTitleId = Nintendo3dsTitleId ?? string.Empty,
                    SwitchTitleId = SwitchTitleId ?? string.Empty,
                    ReplayGainTrackGain = ReplayGainTrackGain,
                    ReplayGainAlbumGain = ReplayGainAlbumGain,
                    Duration = _currentSelectedMedia?.Duration ?? 0.0,
                    UserEdited = true,
                };

                if (existingCache != null)
                {
                    customMetadata.CoverScanned = existingCache.CoverScanned;
                    customMetadata.CoverLookupExhausted = existingCache.CoverLookupExhausted;
                    customMetadata.RomScanned = existingCache.RomScanned;
                    if (string.IsNullOrWhiteSpace(customMetadata.Ps3TitleId))
                        customMetadata.Ps3TitleId = existingCache.Ps3TitleId;
                    if (string.IsNullOrWhiteSpace(customMetadata.Ps3Version))
                        customMetadata.Ps3Version = existingCache.Ps3Version;
                    if (string.IsNullOrWhiteSpace(customMetadata.Ps4TitleId))
                        customMetadata.Ps4TitleId = existingCache.Ps4TitleId;
                    if (string.IsNullOrWhiteSpace(customMetadata.Ps4Version))
                        customMetadata.Ps4Version = existingCache.Ps4Version;
                }

                if (coverImage?.Data is { Length: > 0 } && MediaCoverPaths.UsesEmulationCoverSidecar(resolvedPath))
                {
                    if (!EmulationCoverCacheHelper.WriteCoverFromBytes(resolvedPath, coverImage.Data.ToArray()))
                        SLog.Warn($"Failed to write emulation cover sidecar for '{resolvedPath}'.");
                    else
                        _coverRemovedInEditor = false;

                    customMetadata.CoverScanned = true;
                    customMetadata.CoverLookupExhausted = false;
                }
                else if (_coverRemovedInEditor && MediaCoverPaths.UsesEmulationCoverSidecar(resolvedPath))
                {
                    EmulationCoverCacheHelper.TryDeleteCoverSidecar(resolvedPath);
                    customMetadata.CoverScanned = false;
                }
                else if (MediaCoverPaths.UsesEmulationCoverSidecar(resolvedPath) &&
                         EmulationCoverCacheHelper.HasCover(resolvedPath))
                {
                    customMetadata.CoverScanned = true;
                    customMetadata.CoverLookupExhausted = false;
                }

                var metadataImages = MediaCoverPaths.UsesMetadataImageCache(resolvedPath)
                    ? ToMetadataImageEntries(Images)
                    : ToMetadataImageEntries(Images.Where(img => img.Kind != TagImageKind.Cover));
                BinaryMetadataHelper.WriteMetadataImages(customMetadata, metadataImages);
                BinaryMetadataHelper.SaveMetadata(metaDataPath, customMetadata);
            }
            catch (Exception e)
            {
                SLog.Error("Failed to save metadata cache", e);
            }

            var coverCachePath = string.IsNullOrWhiteSpace(resolvedPath)
                ? null
                : MediaCoverPaths.UsesMetadataImageCache(resolvedPath)
                    ? metaDataPath
                    : EmulationCoverCacheHelper.GetCoverCachePath(resolvedPath);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_currentSelectedMedia != null && !string.IsNullOrWhiteSpace(resolvedPath))
                    _currentSelectedMedia.FileName = resolvedPath;

                UpdateInfo();
                SetMediaItemCoverFromTags(
                    coverImage,
                    wallpaperImage,
                    coverCachePath,
                    resolvedPath);

                MetadataCacheSaved?.Invoke(resolvedPath);
            });
        }

        private static TagImageModel? ResolveActiveCoverImage(IEnumerable<TagImageModel> images) =>
            images.FirstOrDefault(img => img.Kind == TagImageKind.Cover && img.Data is { Length: > 0 });

        private static TagImageModel? ResolveEmulationSidecarCoverImage(IEnumerable<TagImageModel> images) =>
            ResolveActiveCoverImage(images)
            ?? images.FirstOrDefault(img => img.Kind == TagImageKind.BoxArt && img.Data is { Length: > 0 });

        private static bool HasActiveFrontCoverImage(IEnumerable<TagImageModel> images) =>
            images.Any(img => img.Kind == TagImageKind.Cover && img.Data is { Length: > 0 });

        private string BuildFrontCoverImageDescription(string? fallback = null) =>
            string.IsNullOrWhiteSpace(Title) ? fallback ?? "Cover" : Title.Trim();

        /// <summary>
        /// Adds an image using the kind selected in the editor combobox.
        /// Cover/BoxArt participate in the single active-cover slot; other kinds are stored as-is.
        /// </summary>
        private void AddMetadataImage(TagImageModel model)
        {
            if (model.Kind is TagImageKind.Cover or TagImageKind.BoxArt)
                AddFrontCoverCandidateImage(model);
            else
            {
                Images.Add(model);
                AttachEditorImageHandler(model);
            }
        }

        /// <summary>
        /// Adds a front-cover candidate. Only one <see cref="TagImageKind.Cover"/> is active at a time;
        /// additional cover picks are stored as <see cref="TagImageKind.BoxArt"/> alternates until promoted.
        /// </summary>
        private void AddFrontCoverCandidateImage(TagImageModel model)
        {
            if (model.Kind is TagImageKind.Cover or TagImageKind.BoxArt)
            {
                _coverRemovedInEditor = false;

                if (model.Kind == TagImageKind.Cover && HasActiveFrontCoverImage(Images))
                    model.Kind = TagImageKind.BoxArt;

                if (string.IsNullOrWhiteSpace(model.Description) ||
                    model.Description.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    model.Description = model.Kind == TagImageKind.Cover
                        ? BuildFrontCoverImageDescription("Active cover")
                        : BuildFrontCoverImageDescription("Cover alternate");
                }
            }

            Images.Add(model);
            AttachEditorImageHandler(model);

            if (model.Kind is TagImageKind.Cover or TagImageKind.BoxArt)
                NotifyFrontCoverSelectionChanged();
        }

        private void AttachEditorImageHandler(TagImageModel model)
        {
            model.PropertyChanged -= EditorImage_PropertyChanged;
            model.PropertyChanged += EditorImage_PropertyChanged;
        }

        private void AttachEditorImageHandlers(IEnumerable<TagImageModel> models)
        {
            foreach (var model in models)
                AttachEditorImageHandler(model);
        }

        private void DetachEditorImageHandlers()
        {
            foreach (var image in Images)
                image.PropertyChanged -= EditorImage_PropertyChanged;
        }

        private void EditorImage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TagImageModel.Kind) || sender is not TagImageModel image)
                return;

            if (image.Kind == TagImageKind.Cover && Images.Count(img => img.Kind == TagImageKind.Cover) > 1)
                SetActiveFrontCover(image);
            else if (image.Kind == TagImageKind.Cover)
                ApplyActiveEmulationCoverToMediaItem(image);
            else
                NotifyFrontCoverSelectionChanged();
        }

        [RelayCommand]
        private void SetActiveFrontCover(TagImageModel? image)
        {
            if (image == null || image.Data is not { Length: > 0 })
                return;

            if (image.Kind is not (TagImageKind.Cover or TagImageKind.BoxArt))
                return;

            _coverRemovedInEditor = false;

            foreach (var existing in Images.Where(img => img.Kind == TagImageKind.Cover && !ReferenceEquals(img, image)).ToList())
                existing.Kind = TagImageKind.BoxArt;

            image.Kind = TagImageKind.Cover;
            image.Description = BuildFrontCoverImageDescription("Active cover");
            NotifyFrontCoverSelectionChanged();
            ApplyActiveEmulationCoverToMediaItem(image);
        }

        private void ApplyActiveEmulationCoverToMediaItem(TagImageModel coverImage)
        {
            if (_currentSelectedMedia == null || coverImage.Data is not { Length: > 0 })
                return;

            var romPath = EmulationCoverCacheHelper.ResolveRomPathForCache(
                FilePath ?? _currentSelectedMedia.FileName);
            if (string.IsNullOrWhiteSpace(romPath) || !MediaCoverPaths.UsesEmulationCoverSidecar(romPath))
                return;

            if (!EmulationCoverCacheHelper.WriteCoverFromBytes(romPath, coverImage.Data.ToArray()))
            {
                SLog.Warn($"Failed to write active emulation cover for '{romPath}'.");
                return;
            }

            _coverRemovedInEditor = false;
            _currentSelectedMedia.FileName = romPath;
            _currentSelectedMedia.LocalCoverPath = EmulationCoverCacheHelper.GetCoverCachePath(romPath);
            using (var ms = new MemoryStream(coverImage.Data))
                _currentSelectedMedia.CoverBitmap = Bitmap.DecodeToWidth(ms, NormalizedCoverMaxDimension);
            _currentSelectedMedia.CoverFound = true;
            _currentSelectedMedia.MetadataProcessed = true;
            _currentSelectedMedia.SaveCoverBitmapAction = null;
            _currentSelectedMedia.DeclineCoverBitmapAction = null;

            MetadataCacheSaved?.Invoke(romPath);
        }

        private void NotifyFrontCoverSelectionChanged()
        {
            foreach (var img in Images.Where(i => i.Kind is TagImageKind.Cover or TagImageKind.BoxArt))
            {
                img.RaisePropertyChanged(nameof(TagImageModel.IsActiveFrontCover));
                img.RaisePropertyChanged(nameof(TagImageModel.CanPromoteToFrontCover));
            }
        }

        private void SetMediaItemCoverFromTags(
            TagImageModel? coverImage,
            TagImageModel? wallpaperImage,
            string? metadataCachePath = null,
            string? resolvedRomPath = null)
        {
            if (_currentSelectedMedia == null)
                return;

            resolvedRomPath ??= _currentSelectedMedia.FileName;

            if (coverImage != null)
            {
                if (MediaCoverPaths.UsesEmulationCoverSidecar(resolvedRomPath ?? _currentSelectedMedia.FileName))
                {
                    if (!string.IsNullOrWhiteSpace(resolvedRomPath))
                        _currentSelectedMedia.FileName = resolvedRomPath;

                    var sidecarPath = EmulationCoverCacheHelper.GetCoverCachePath(_currentSelectedMedia.FileName);
                    _currentSelectedMedia.LocalCoverPath = !string.IsNullOrWhiteSpace(metadataCachePath) &&
                        EmulationCoverCacheHelper.IsCoverCachePath(metadataCachePath)
                            ? metadataCachePath
                            : sidecarPath;
                    using (var ms = new MemoryStream(coverImage.Data))
                        _currentSelectedMedia.CoverBitmap = Bitmap.DecodeToWidth(ms, NormalizedCoverMaxDimension);
                    _currentSelectedMedia.CoverFound = true;
                }
                else
                {
                    var ms = new MemoryStream(coverImage.Data);
                    _currentSelectedMedia.CoverBitmap = Bitmap.DecodeToWidth(ms, NormalizedCoverMaxDimension);
                    _currentSelectedMedia.CoverFound = false;
                    if (!string.IsNullOrWhiteSpace(metadataCachePath))
                        _currentSelectedMedia.LocalCoverPath = metadataCachePath;
                }

                _currentSelectedMedia.SaveCoverBitmapAction = null;
                _currentSelectedMedia.DeclineCoverBitmapAction = null;
                _currentSelectedMedia.MetadataProcessed = true;
            }
            else if (!string.IsNullOrWhiteSpace(resolvedRomPath) &&
                     MediaCoverPaths.UsesEmulationCoverSidecar(resolvedRomPath) &&
                     EmulationCoverCacheHelper.HasCover(resolvedRomPath))
            {
                _currentSelectedMedia.LocalCoverPath = EmulationCoverCacheHelper.GetCoverCachePath(resolvedRomPath);
                _currentSelectedMedia.CoverFound = false;
                _currentSelectedMedia.MetadataProcessed = true;
            }
            else if (_coverRemovedInEditor)
            {
                _currentSelectedMedia.CoverBitmap = null;
                _currentSelectedMedia.CoverFound = false;
                _currentSelectedMedia.LocalCoverPath = null;
            }

            if (wallpaperImage != null)
            {
                var ms = new MemoryStream(wallpaperImage.Data);
                _currentSelectedMedia.WallpaperBitmap = new Bitmap(ms);
            }
            else
            {
                _currentSelectedMedia.WallpaperBitmap = null;
            }
        }

        private static Task PersistXbox360IdsToMetadataCacheAsync(string filePath, string? titleId, string? mediaId)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return Task.CompletedTask;

            return Task.Run(() =>
            {
                var cachePath = GetMetadataCachePath(filePath);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();

                if (!string.IsNullOrWhiteSpace(titleId))
                    metadata.Xbox360TitleId = titleId;

                if (!string.IsNullOrWhiteSpace(mediaId))
                    metadata.Xbox360MediaId = mediaId;

                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            });
        }

        private void UpdateInfo()
        {
            // Update current media item
            _currentSelectedMedia!.Title = Title;
            _currentSelectedMedia!.Artist = Artists;
            _currentSelectedMedia!.Album = Album;
            _currentSelectedMedia!.Track = Track;
            _currentSelectedMedia!.Year = Year;
            _currentSelectedMedia!.Lyrics = Lyrics;
            _currentSelectedMedia!.Genre = Genres;
            _currentSelectedMedia!.Comment = Comment;
            _currentSelectedMedia!.ReplayGainTrackGain = ReplayGainTrackGain;
            _currentSelectedMedia!.ReplayGainAlbumGain = ReplayGainAlbumGain;
            _currentSelectedMedia!.VideoUrl = VideoUrl;
        }
    }
}
