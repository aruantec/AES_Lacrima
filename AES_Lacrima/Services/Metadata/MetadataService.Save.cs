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
                    TagImageModel? wallpaperImage = null;
                    TagImageModel? coverImage = null;
                    foreach (var img in Images)
                    {
                        if (img.Kind == TagImageKind.Wallpaper)
                        {
                            wallpaperImage = img;
                        }
                        else if (img.Kind != TagImageKind.LiveWallpaper)
                        {
                            coverImage ??= img;
                            if (img.Kind == TagImageKind.Cover || img.Kind == TagImageKind.Other)
                                coverImage = img;
                        }

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

                    UpdateInfo();
                    SetMediaItemCoverFromTags(coverImage, wallpaperImage);
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
            var cacheId = BinaryMetadataHelper.GetCacheId(path ?? string.Empty);
            var metaDataPath = ApplicationPaths.GetCacheFile(cacheId + ".meta");

            var metaDir = Path.GetDirectoryName(metaDataPath);
            if (!string.IsNullOrEmpty(metaDir) && !Directory.Exists(metaDir))
                Directory.CreateDirectory(metaDir);

            try
            {
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
                     ReplayGainTrackGain = ReplayGainTrackGain,
                    ReplayGainAlbumGain = ReplayGainAlbumGain,
                    Duration = _currentSelectedMedia?.Duration ?? 0.0,
                };

                BinaryMetadataHelper.WriteMetadataImages(customMetadata, ToMetadataImageEntries(Images));
                BinaryMetadataHelper.SaveMetadata(metaDataPath, customMetadata);
            }
            catch (Exception e)
            {
                SLog.Error("Failed to save metadata cache", e);
            }

            UpdateInfo();
            SetMediaItemCoverFromTags(
                Images.FirstOrDefault(img => img.Kind == TagImageKind.Cover),
                Images.FirstOrDefault(img => img.Kind == TagImageKind.Wallpaper));
        }

        private void SetMediaItemCoverFromTags(TagImageModel? coverImage, TagImageModel? wallpaperImage)
        {
            if (_currentSelectedMedia == null)
                return;

            if (coverImage != null)
            {
                // Note: DO NOT use 'using' on the MemoryStream - the Bitmap holds a reference to it
                // and disposes it when it's no longer needed. Disposing the stream early causes
                // ObjectDisposedException when Avalonia tries to measure/render the Image control.
                var ms = new MemoryStream(coverImage.Data);
                _currentSelectedMedia.CoverBitmap = new Bitmap(ms);
            }
            else
            {
                _currentSelectedMedia.CoverBitmap?.Dispose();
                _currentSelectedMedia.CoverBitmap = null;
            }

            if (wallpaperImage != null)
            {
                // Note: DO NOT use 'using' on the MemoryStream - the Bitmap holds a reference to it
                // and disposes it when it's no longer needed. Disposing the stream early causes
                // ObjectDisposedException when Avalonia tries to measure/render the Image control.
                var ms = new MemoryStream(wallpaperImage.Data);
                _currentSelectedMedia.WallpaperBitmap = new Bitmap(ms);
            }
            else
            {
                _currentSelectedMedia.WallpaperBitmap?.Dispose();
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
