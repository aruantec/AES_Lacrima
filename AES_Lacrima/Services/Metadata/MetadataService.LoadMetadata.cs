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
        public async Task LoadMetadataAsync(MediaItem item)
        {
            _currentSelectedMedia = item;
            var resolvedPath = item.FileName;
            FilePath = resolvedPath;
            IsOnlineMedia = false;
            IsXbox360Metadata = string.Equals(item.Album, "Xbox 360", StringComparison.OrdinalIgnoreCase);
            Xbox360TitleId = null;
            Xbox360MediaId = null;

            if (IsXbox360Metadata && !string.IsNullOrWhiteSpace(item.FileName) && _xbox360MetadataService != null)
            {
                var xbox360Metadata = await Task.Run(() => _xbox360MetadataService.TryReadGameMetadata(item.FileName)).ConfigureAwait(false);
                Xbox360TitleId = xbox360Metadata?.TitleId;
                Xbox360MediaId = xbox360Metadata?.MediaId;

                if (!string.IsNullOrWhiteSpace(item.FileName) &&
                    (!string.IsNullOrWhiteSpace(Xbox360TitleId) || !string.IsNullOrWhiteSpace(Xbox360MediaId)))
                {
                    await PersistXbox360IdsToMetadataCacheAsync(item.FileName, Xbox360TitleId, Xbox360MediaId).ConfigureAwait(false);
                }
            }

            if (IsXbox360Metadata &&
                !string.IsNullOrWhiteSpace(item.FileName) &&
                (string.IsNullOrWhiteSpace(Xbox360TitleId) || string.IsNullOrWhiteSpace(Xbox360MediaId)))
            {
                var cachePath = GetMetadataCachePath(item.FileName);
                var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(Xbox360TitleId))
                    Xbox360TitleId = refreshed?.Xbox360TitleId;
                if (string.IsNullOrWhiteSpace(Xbox360MediaId))
                    Xbox360MediaId = refreshed?.Xbox360MediaId;
            }

            IsPs3Metadata = string.Equals(item.Album, "PlayStation 3", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(item.Album, "PS3", StringComparison.OrdinalIgnoreCase) ||
                           Ps3InstalledGameHelper.IsInstalledGameFolder(item.FileName);
            Ps3TitleId = null;
            Ps3Version = null;

            if (IsPs3Metadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                Ps3TitleId = Ps3InstalledGameHelper.GetTitleId(item.FileName);
                Ps3Version = Ps3InstalledGameHelper.GetVersion(item.FileName);
                if (!string.IsNullOrWhiteSpace(Ps3TitleId) || !string.IsNullOrWhiteSpace(Ps3Version))
                {
                    await PersistPs3MetadataToMetadataCacheAsync(item.FileName, Ps3TitleId, Ps3Version).ConfigureAwait(false);
                }
                else
                {
                    var cachePath = GetMetadataCachePath(item.FileName);
                    var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(Ps3TitleId))
                        Ps3TitleId = refreshed?.Ps3TitleId;
                    if (string.IsNullOrWhiteSpace(Ps3Version))
                        Ps3Version = refreshed?.Ps3Version;
                }
            }

            IsPs4Metadata = string.Equals(item.Album, "PlayStation 4", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(item.Album, "PS4", StringComparison.OrdinalIgnoreCase) ||
                           Ps4InstalledGameHelper.IsInstalledGameFolder(item.FileName);
            Ps4TitleId = null;
            Ps4Version = null;

            if (IsPs4Metadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                Ps4TitleId = Ps4InstalledGameHelper.GetTitleId(item.FileName);
                Ps4Version = Ps4InstalledGameHelper.GetVersion(item.FileName);
                if (!string.IsNullOrWhiteSpace(Ps4TitleId) || !string.IsNullOrWhiteSpace(Ps4Version))
                {
                    await PersistPs4MetadataToMetadataCacheAsync(item.FileName, Ps4TitleId, Ps4Version).ConfigureAwait(false);
                }
                else
                {
                    var cachePath = GetMetadataCachePath(item.FileName);
                    var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(Ps4TitleId))
                        Ps4TitleId = refreshed?.Ps4TitleId;
                    if (string.IsNullOrWhiteSpace(Ps4Version))
                        Ps4Version = refreshed?.Ps4Version;
                }
            }

            IsPsXMetadata = string.Equals(item.Album, "PlayStation", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PSX", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PS1", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PlayStation 1", StringComparison.OrdinalIgnoreCase);
            PsXTitleId = null;
            PsXVersion = null;

            IsPs2Metadata = string.Equals(item.Album, "PlayStation 2", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PS2", StringComparison.OrdinalIgnoreCase);
            Ps2TitleId = null;
            Ps2Version = null;

            if (IsPsXMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName, DiscSection.PSX)).ConfigureAwait(false);
                PsXTitleId = romInfo?.GameId;
                if (!string.IsNullOrWhiteSpace(PsXTitleId))
                {
                    await PersistPsXMetadataToMetadataCacheAsync(item.FileName, PsXTitleId, PsXVersion).ConfigureAwait(false);
                }
                else
                {
                    var cachePath = GetMetadataCachePath(item.FileName);
                    var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(PsXTitleId))
                        PsXTitleId = refreshed?.PsXTitleId;
                    if (string.IsNullOrWhiteSpace(PsXVersion))
                        PsXVersion = refreshed?.PsXVersion;
                }
            }

            if (IsPs2Metadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName, DiscSection.PS2)).ConfigureAwait(false);
                Ps2TitleId = romInfo?.GameId;
                if (!string.IsNullOrWhiteSpace(Ps2TitleId))
                {
                    await PersistPs2MetadataToMetadataCacheAsync(item.FileName, Ps2TitleId, Ps2Version).ConfigureAwait(false);
                }
                else
                {
                    var cachePath = GetMetadataCachePath(item.FileName);
                    var refreshed = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath)).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(Ps2TitleId))
                        Ps2TitleId = refreshed?.Ps2TitleId;
                    if (string.IsNullOrWhiteSpace(Ps2Version))
                        Ps2Version = refreshed?.Ps2Version;
                }
            }

            IsGameCubeMetadata = IsGameCubeAlbum(item.Album);
            GameCubeTitleId = null;
            if (IsGameCubeMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                await LoadNintendoDiscMetadataAsync(item, DiscSection.GameCube).ConfigureAwait(false);
            }

            IsWiiMetadata = IsWiiAlbum(item.Album);
            WiiTitleId = null;
            if (IsWiiMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                await LoadNintendoDiscMetadataAsync(item, DiscSection.Wii).ConfigureAwait(false);
            }

            IsWiiUMetadata = IsWiiUAlbum(item.Album) ||
                             WiiUInstalledGameHelper.IsInstalledGameFolder(item.FileName);
            WiiUTitleId = null;
            if (IsWiiUMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                await LoadWiiUMetadataAsync(item).ConfigureAwait(false);
            }

            IsNintendo3dsMetadata = IsNintendo3dsAlbum(item.Album);
            Nintendo3dsTitleId = null;
            if (IsNintendo3dsMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                await LoadNintendo3dsMetadataAsync(item).ConfigureAwait(false);
            }

            await TryApplyTitleFromPs3InstalledGameAsync(item, CancellationToken.None).ConfigureAwait(false);
            await TryApplyTitleFromPs4InstalledGameAsync(item, CancellationToken.None).ConfigureAwait(false);
            await TryApplyTitleFromPsxGameAsync(item, CancellationToken.None).ConfigureAwait(false);

            try
            {
                if (string.IsNullOrWhiteSpace(resolvedPath))
                    throw new ArgumentException("file missing", nameof(item));

                if (!File.Exists(resolvedPath))
                {
                    // Pre-populate with current media item info while loading from cache.
                    Title = item.Title;
                    Artists = item.Artist;
                    Album = item.Album;
                    Track = item.Track;
                    Year = item.Year;
                    Genres = item.Genre;
                    Comment = item.Comment;
                    Lyrics = item.Lyrics;
                    VideoUrl = string.Empty;
                    IsOnlineMedia = true;

                    var metadata = await Task.Run(() =>
                    {
                        var cacheId = BinaryMetadataHelper.GetCacheId(resolvedPath);
                        var metaData = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                        return BinaryMetadataHelper.LoadMetadata(metaData);
                    });

                    var newImages = metadata == null
                        ? []
                        : CreateTagImageModelsFromMetadata(metadata, OnDeleteImage);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (metadata != null)
                        {
                            Title = metadata.Title;
                            Album = metadata.Album;
                            Artists = metadata.Artist;
                            Track = metadata.Track;
                            Year = metadata.Year;
                            Lyrics = metadata.Lyrics;
                            Genres = metadata.Genre;
                            Comment = metadata.Comment;
                            VideoUrl = metadata.VideoUrl;
                            ReplayGainTrackGain = metadata.ReplayGainTrackGain;
                            ReplayGainAlbumGain = metadata.ReplayGainAlbumGain;
                            if (_currentSelectedMedia != null)
                                _currentSelectedMedia.VideoUrl = metadata.VideoUrl;
                            if (_currentSelectedMedia != null && metadata.Duration > 0)
                                _currentSelectedMedia.Duration = metadata.Duration;
                        }

                        foreach (var old in Images)
                            old.Dispose();

                        Images.Clear();
                        foreach (var image in newImages)
                        {
                            Images.Add(image);
                            if (image.Kind == TagImageKind.LiveWallpaper)
                                _ = LoadImageAsync(image);
                        }

                        IsMetadataLoaded = true;
                    });

                    return;
                }

                var snapshot = await Task.Run(() =>
                {
                    using var tlFile = TagLib.File.Create(resolvedPath);
                    var tag = tlFile.Tag;
                    var pics = tag.Pictures ?? [];
                    var imagesToAdd = new List<TagImageModel>(pics.Length);
                    foreach (var p in pics)
                    {
                        var kind = MapPictureToKind(p);
                        var data = p.Data.Data.ToArray();
                        var mime = p.MimeType;
                        var desc = StripImageKindMarker(p.Description);
                        imagesToAdd.Add(new TagImageModel(kind, data, mime, desc) { OnDeleteImage = OnDeleteImage });
                    }

                    var cachePath = GetMetadataCachePath(resolvedPath);
                    var cachedMetadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                    if (cachedMetadata != null)
                    {
                        var cachedImages = CreateTagImageModelsFromMetadata(cachedMetadata, OnDeleteImage);
                        if (cachedImages.Count > 0)
                            imagesToAdd = cachedImages;
                    }

                    return new
                    {
                        tag.Title,
                        Artists = tag.JoinedPerformers,
                        tag.Album,
                        tag.Track,
                        tag.Year,
                        tag.Lyrics,
                        Genres = string.Join(";", tag.Genres ?? []),
                        tag.Comment,
                        Images = imagesToAdd
                    };
                });

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Title = snapshot.Title;
                    Artists = snapshot.Artists;
                    Album = snapshot.Album;
                    Track = snapshot.Track;
                    Year = snapshot.Year;
                    Lyrics = snapshot.Lyrics;
                    Genres = snapshot.Genres;
                    Comment = snapshot.Comment;
                    VideoUrl = string.Empty;

                    foreach (var old in Images)
                        old.Dispose();

                    Images.Clear();
                    foreach (var img in snapshot.Images)
                        Images.Add(img);

                    IsMetadataLoaded = true;
                });
            }
            catch (Exception ex)
            {
                SLog.Error("Failed to load metadata", ex);
                await Dispatcher.UIThread.InvokeAsync(() => IsMetadataLoaded = false);
            }
        }

        public async Task LoadMetadataForItemAsync(MediaItem item)
        {
            if (item == null)
                return;

            _currentSelectedMedia = item;
            FilePath = item.FileName;
            IsXbox360Metadata = string.Equals(item.Album, "Xbox 360", StringComparison.OrdinalIgnoreCase);
            Xbox360TitleId = null;
            Xbox360MediaId = null;
            IsPs3Metadata = string.Equals(item.Album, "PlayStation 3", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(item.Album, "PS3", StringComparison.OrdinalIgnoreCase) ||
                           Ps3InstalledGameHelper.IsInstalledGameFolder(item.FileName);
            Ps3TitleId = null;
            Ps3Version = null;
             IsPs4Metadata = string.Equals(item.Album, "PlayStation 4", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PS4", StringComparison.OrdinalIgnoreCase) ||
                            Ps4InstalledGameHelper.IsInstalledGameFolder(item.FileName);
             Ps4TitleId = null;
             Ps4Version = null;
             IsPsXMetadata = string.Equals(item.Album, "PlayStation", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PSX", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PS1", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PlayStation 1", StringComparison.OrdinalIgnoreCase);
             PsXTitleId = null;
             PsXVersion = null;
             IsPs2Metadata = string.Equals(item.Album, "PlayStation 2", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Album, "PS2", StringComparison.OrdinalIgnoreCase);
             Ps2TitleId = null;
             Ps2Version = null;
             IsGameCubeMetadata = IsGameCubeAlbum(item.Album);
             GameCubeTitleId = null;
             IsWiiMetadata = IsWiiAlbum(item.Album);
             WiiTitleId = null;
             IsWiiUMetadata = IsWiiUAlbum(item.Album) ||
                              WiiUInstalledGameHelper.IsInstalledGameFolder(item.FileName);
             WiiUTitleId = null;
             IsNintendo3dsMetadata = IsNintendo3dsAlbum(item.Album);
             Nintendo3dsTitleId = null;

             await TryApplyTitleFromPs3InstalledGameAsync(item, CancellationToken.None).ConfigureAwait(false);
            await TryApplyTitleFromPs4InstalledGameAsync(item, CancellationToken.None).ConfigureAwait(false);
            await TryApplyTitleFromPsxGameAsync(item, CancellationToken.None).ConfigureAwait(false);

            Title = item.Title;
            Artists = item.Artist;
            Album = item.Album;
            Track = item.Track;
            Year = item.Year;
            Lyrics = item.Lyrics;
            Genres = item.Genre;
            Comment = item.Comment;
            VideoUrl = string.Empty;
            ReplayGainTrackGain = item.ReplayGainTrackGain;
            ReplayGainAlbumGain = item.ReplayGainAlbumGain;

            Images.Clear();

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath));

            if (metadata != null)
            {
                if (string.IsNullOrWhiteSpace(Xbox360TitleId))
                    Xbox360TitleId = metadata.Xbox360TitleId;
                if (string.IsNullOrWhiteSpace(Xbox360MediaId))
                    Xbox360MediaId = metadata.Xbox360MediaId;
                if (string.IsNullOrWhiteSpace(Ps3TitleId))
                    Ps3TitleId = metadata.Ps3TitleId;
                if (string.IsNullOrWhiteSpace(Ps3Version))
                    Ps3Version = metadata.Ps3Version;
                 if (string.IsNullOrWhiteSpace(Ps4TitleId))
                    Ps4TitleId = metadata.Ps4TitleId;
                 if (string.IsNullOrWhiteSpace(Ps4Version))
                    Ps4Version = metadata.Ps4Version;
                 if (string.IsNullOrWhiteSpace(PsXTitleId))
                    PsXTitleId = metadata.PsXTitleId;
                 if (string.IsNullOrWhiteSpace(PsXVersion))
                    PsXVersion = metadata.PsXVersion;
                 if (string.IsNullOrWhiteSpace(Ps2TitleId))
                    Ps2TitleId = metadata.Ps2TitleId;
                 if (string.IsNullOrWhiteSpace(Ps2Version))
                    Ps2Version = metadata.Ps2Version;
                 if (string.IsNullOrWhiteSpace(GameCubeTitleId))
                    GameCubeTitleId = metadata.GameCubeTitleId;
                 if (string.IsNullOrWhiteSpace(WiiTitleId))
                    WiiTitleId = metadata.WiiTitleId;
                 if (string.IsNullOrWhiteSpace(WiiUTitleId))
                    WiiUTitleId = metadata.WiiUTitleId;
                 if (string.IsNullOrWhiteSpace(Nintendo3dsTitleId))
                    Nintendo3dsTitleId = metadata.Nintendo3dsTitleId;

                    Title = metadata.Title;
                if (string.IsNullOrWhiteSpace(Artists))
                    Artists = metadata.Artist;
                if (string.IsNullOrWhiteSpace(Album))
                    Album = metadata.Album;
                if (Track == 0)
                    Track = metadata.Track;
                if (Year == 0)
                    Year = metadata.Year;
                if (string.IsNullOrWhiteSpace(Lyrics))
                    Lyrics = metadata.Lyrics;
                if (string.IsNullOrWhiteSpace(Genres))
                    Genres = metadata.Genre;
                if (string.IsNullOrWhiteSpace(Comment))
                    Comment = metadata.Comment;
                VideoUrl = metadata.VideoUrl;
                if (ReplayGainTrackGain == 0)
                    ReplayGainTrackGain = metadata.ReplayGainTrackGain;
                if (ReplayGainAlbumGain == 0)
                    ReplayGainAlbumGain = metadata.ReplayGainAlbumGain;
                item.VideoUrl = metadata.VideoUrl;

                foreach (var model in CreateTagImageModelsFromMetadata(metadata, OnDeleteImage))
                {
                    Images.Add(model);
                    if (model.Kind == TagImageKind.LiveWallpaper)
                        await LoadImageAsync(model);
                }
            }

            if (Images.Count == 0 && item.CoverBitmap != null)
            {
                using var ms = new MemoryStream();
                item.CoverBitmap.Save(ms);
                var content = ms.ToArray();
                Images.Add(new TagImageModel(TagImageKind.Cover, content, "image/png", "Cover from album item")
                {
                    OnDeleteImage = OnDeleteImage
                });
            }

            if (Images.Count == 0 && !string.IsNullOrWhiteSpace(item.FileName))
            {
                if (IsPs4Metadata && await TryApplyCoverFromPs4InstalledGameAsync(item, CancellationToken.None, persistToCache: true).ConfigureAwait(false))
                {
                    await ReloadImagesFromMetadataCacheAsync(item.FileName).ConfigureAwait(false);
                }
                else if (IsPs3Metadata && await TryApplyCoverFromPs3InstalledGameAsync(item, CancellationToken.None, persistToCache: true).ConfigureAwait(false))
                {
                    await ReloadImagesFromMetadataCacheAsync(item.FileName).ConfigureAwait(false);
                }
            }

            IsMetadataLoaded = true;

            if (IsXbox360Metadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                if ((string.IsNullOrWhiteSpace(Xbox360TitleId) || string.IsNullOrWhiteSpace(Xbox360MediaId)) && _xbox360MetadataService != null)
                {
                    var xbox360Metadata = await Task.Run(() => _xbox360MetadataService.TryReadGameMetadata(item.FileName)).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(Xbox360TitleId))
                        Xbox360TitleId = xbox360Metadata?.TitleId;
                    if (string.IsNullOrWhiteSpace(Xbox360MediaId))
                        Xbox360MediaId = xbox360Metadata?.MediaId;
                }

                if (!string.IsNullOrWhiteSpace(Xbox360TitleId) || !string.IsNullOrWhiteSpace(Xbox360MediaId))
                    await PersistXbox360IdsToMetadataCacheAsync(item.FileName, Xbox360TitleId, Xbox360MediaId).ConfigureAwait(false);
            }

            if (IsPs3Metadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                if ((string.IsNullOrWhiteSpace(Ps3TitleId) || string.IsNullOrWhiteSpace(Ps3Version)))
                {
                    var ps3TitleId = Ps3InstalledGameHelper.GetTitleId(item.FileName);
                    var ps3Version = Ps3InstalledGameHelper.GetVersion(item.FileName);
                    if (string.IsNullOrWhiteSpace(Ps3TitleId))
                        Ps3TitleId = ps3TitleId;
                    if (string.IsNullOrWhiteSpace(Ps3Version))
                        Ps3Version = ps3Version;
                }

                if (!string.IsNullOrWhiteSpace(Ps3TitleId) || !string.IsNullOrWhiteSpace(Ps3Version))
                    await PersistPs3MetadataToMetadataCacheAsync(item.FileName, Ps3TitleId, Ps3Version).ConfigureAwait(false);
            }

            if (IsPs4Metadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                if ((string.IsNullOrWhiteSpace(Ps4TitleId) || string.IsNullOrWhiteSpace(Ps4Version)))
                {
                    var ps4TitleId = Ps4InstalledGameHelper.GetTitleId(item.FileName);
                    var ps4Version = Ps4InstalledGameHelper.GetVersion(item.FileName);
                    if (string.IsNullOrWhiteSpace(Ps4TitleId))
                        Ps4TitleId = ps4TitleId;
                    if (string.IsNullOrWhiteSpace(Ps4Version))
                        Ps4Version = ps4Version;
                }

                 if (!string.IsNullOrWhiteSpace(Ps4TitleId) || !string.IsNullOrWhiteSpace(Ps4Version))
                    await PersistPs4MetadataToMetadataCacheAsync(item.FileName, Ps4TitleId, Ps4Version).ConfigureAwait(false);
            }

            if (IsPsXMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                if (string.IsNullOrWhiteSpace(PsXTitleId))
                {
                    var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName, DiscSection.PSX)).ConfigureAwait(false);
                    var psxTitleId = romInfo?.GameId;
                    if (!string.IsNullOrWhiteSpace(psxTitleId))
                        PsXTitleId = psxTitleId;
                }

                var psxTitle = ResolvePsTitle(PsXTitleId, preferPs2TitleId: false);
                if (!string.IsNullOrWhiteSpace(psxTitle))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Title = psxTitle;
                        if (_currentSelectedMedia == item)
                            Title = psxTitle;
                    }, DispatcherPriority.Background);
                    await PersistPsXMetadataToMetadataCacheAsync(item.FileName, PsXTitleId, PsXVersion, psxTitle).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(PsXTitleId) || !string.IsNullOrWhiteSpace(PsXVersion))
                {
                    await PersistPsXMetadataToMetadataCacheAsync(item.FileName, PsXTitleId, PsXVersion).ConfigureAwait(false);
                }
            }

            if (IsPs2Metadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                if (string.IsNullOrWhiteSpace(Ps2TitleId))
                {
                    var romInfo = await Task.Run(() => RomInspector.Inspect(item.FileName, DiscSection.PS2)).ConfigureAwait(false);
                    var ps2TitleId = romInfo?.GameId;
                    if (!string.IsNullOrWhiteSpace(ps2TitleId))
                        Ps2TitleId = ps2TitleId;
                }

                var ps2Title = ResolvePsTitle(Ps2TitleId, preferPs2TitleId: true);
                if (!string.IsNullOrWhiteSpace(ps2Title))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Title = ps2Title;
                        if (_currentSelectedMedia == item)
                            Title = ps2Title;
                    }, DispatcherPriority.Background);
                    await PersistPs2MetadataToMetadataCacheAsync(item.FileName, Ps2TitleId, Ps2Version, ps2Title).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(Ps2TitleId) || !string.IsNullOrWhiteSpace(Ps2Version))
                {
                    await PersistPs2MetadataToMetadataCacheAsync(item.FileName, Ps2TitleId, Ps2Version).ConfigureAwait(false);
                }
            }

            if (IsGameCubeMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                await LoadNintendoDiscMetadataAsync(item, DiscSection.GameCube).ConfigureAwait(false);
            }

            if (IsWiiMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                await LoadNintendoDiscMetadataAsync(item, DiscSection.Wii).ConfigureAwait(false);
            }

            if (IsWiiUMetadata && !string.IsNullOrWhiteSpace(item.FileName))
            {
                await LoadWiiUMetadataAsync(item).ConfigureAwait(false);
            }
        }
    }
}
