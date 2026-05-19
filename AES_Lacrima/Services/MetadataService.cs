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
    internal enum MetadataSearchMode
    {
        Images,
        GameplayVideo
    }

    public sealed class WebImageSearchResult
    {
        public required string ThumbnailUrl { get; init; }
        public required string FullImageUrl { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
    }

    public interface IMetadataService;

    [AutoRegister]
    public partial class MetadataService : ViewModelBase, IMetadataService
    {
        private static readonly ILog SLog = AES_Core.Logging.LogHelper.For<MetadataService>();
        private const int MaxImageSearchResults = 24;
        private const int MaxAutoCoverQueries = 8;
        private const int MaxAutoCoverCandidatesPerQuery = 3;
        private const int NormalizedCoverMaxDimension = 384;
        private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly Regex BracketCleanupRegex = new(@"[\(\[\{].*?[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex ImageKindDescriptionRegex = new(@"^\[AES_KIND:(?<kind>[A-Za-z]+)\]\s*", RegexOptions.Compiled);
        private static readonly Regex GoogleImgTagRegex = new(@"<img[^>]+(?:src|data-src|data-iurl)=""(?<url>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GoogleImgResRegex = new(@"[?&]imgurl=(?<url>[^&""'\s<>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GoogleJsonImageUrlRegex = new(@"(?:""(?:(?:ou)|(?:iurl)|(?:imageUrl)|(?:thumbnailUrl))""\s*:\s*"")(?<url>https?:\\?/\\?/[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GoogleQuotedHttpUrlRegex = new(@"""(?<url>https?:\\?/\\?/[^""'\s<>]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BingJsonImageUrlRegex = new(@"""(?:murl|imgurl|turl|thumb|thumbnailUrl)""\s*:\s*""(?<url>https?:\\?/\\?/[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BingHtmlEncodedImageUrlRegex = new(@"(?:murl|imgurl|turl|thumb|thumbnailUrl)&quot;:&quot;(?<url>https?:[^""'<>]+?)&quot;", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DdgJsonImageUrlRegex = new(@"""(?:image|thumbnail)""\s*:\s*""(?<url>https?:\\?/\\?/[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DirectImageUrlRegex = new(@"https?://[^""'\s<>\\]+?\.(?:jpg|jpeg|png|webp|gif|bmp|avif)(?:\?[^""'\s<>\\]*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RomDumpTokenRegex = new(@"\b(?:rev\s*\d+|beta|proto|prototype|demo|sample|unl|hack|translated?|translation|usa|europe|japan|world)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RomReleaseTokenRegex = new(@"\b(?:complete|fixed|fix|patched?|update(?:d)?|release|final)\b|\bv(?:ersion)?\s*\d+(?:[._-]\d+)*(?:\s+\d+)*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CoverSearchTokenRegex = new(@"\b(?:cover(?:\s+art)?|album\s+cover|box\s*art)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly SemaphoreSlim AutoCoverLookupThrottle = new(2, 2);
        private const string GoogleConsentCookie = "CONSENT=YES+cb.20210328-17-p0.en+FX+471";
        private static readonly object PsTitleLookupLock = new();
        private static Dictionary<string, string>? _psxTitleLookup;
        private static Dictionary<string, string>? _ps2TitleLookup;
        private static readonly string[] NoiseTokens =
        [
            "lyrics", "lyric", "official video", "official audio", "official",
            "music video", "video", "audio", "hd", "4k", "remastered",
            "feat", "ft", "featuring", "live", "karaoke", "visualizer"
        ];

        private MediaItem? _currentSelectedMedia;
        private MetadataSearchMode _searchMode = MetadataSearchMode.Images;
        private static readonly Regex YouTubeVideoIdRegex = new(@"""videoId"":""(?<id>[A-Za-z0-9_-]{11})""", RegexOptions.Compiled);

        [ObservableProperty] private bool _isOnlineMedia;
        [ObservableProperty] private string? _filePath;
        [ObservableProperty] private string? _title;
        [ObservableProperty] private string? _artists;
        [ObservableProperty] private string? _album;
        [ObservableProperty] private uint _track;
        [ObservableProperty] private uint _year;
        [ObservableProperty] private string? _genres;
        [ObservableProperty] private string? _comment;
        [ObservableProperty] private string? _lyrics;
        [ObservableProperty] private string? _videoUrl;
        [ObservableProperty] private bool _isXbox360Metadata;
        [ObservableProperty] private string? _xbox360TitleId;
        [ObservableProperty] private string? _xbox360MediaId;
        [ObservableProperty] private bool _isPs3Metadata;
        [ObservableProperty] private string? _ps3TitleId;
        [ObservableProperty] private string? _ps3Version;
        [ObservableProperty] private bool _isPs4Metadata;
        [ObservableProperty] private string? _ps4TitleId;
        [ObservableProperty] private string? _ps4Version;
        [ObservableProperty] private bool _isPsXMetadata;
        [ObservableProperty] private string? _psXTitleId;
        [ObservableProperty] private string? _psXVersion;
        [ObservableProperty] private bool _isPs2Metadata;
        [ObservableProperty] private string? _ps2TitleId;
        [ObservableProperty] private string? _ps2Version;
        [ObservableProperty] private bool _isGameCubeMetadata;
        [ObservableProperty] private string? _gameCubeTitleId;
        [ObservableProperty] private bool _isWiiMetadata;
        [ObservableProperty] private string? _wiiTitleId;
        [ObservableProperty] private bool _isWiiUMetadata;
        [ObservableProperty] private string? _wiiUTitleId;
        [ObservableProperty] private bool _isNintendo3dsMetadata;
        [ObservableProperty] private string? _nintendo3dsTitleId;
        [ObservableProperty] private double _replayGainTrackGain;
        [ObservableProperty] private double _replayGainAlbumGain;
        [ObservableProperty] private TagImageKind _selectedImageKind;

        [ObservableProperty]
        private bool _isMetadataLoaded;

        [ObservableProperty]
        private AvaloniaList<TagImageModel> _images = [];

        [ObservableProperty] private string? _addImageUrl;
        [ObservableProperty] private string _imageSearchQuery = string.Empty;
        [ObservableProperty] private bool _isImageSearchOverlayOpen;
        [ObservableProperty] private bool _isImageSearchLoading;
        [ObservableProperty] private string _imageSearchStatus = string.Empty;
        [ObservableProperty] private AvaloniaList<WebImageSearchResult> _imageSearchResults = [];

        [AutoResolve]
        private MusicViewModel? _musicViewModel;

        [AutoResolve]
        private Xbox360MetadataService? _xbox360MetadataService;

        public IReadOnlyList<TagImageKind> MetadataImageKinds { get; } =
        [
            TagImageKind.Cover,
            TagImageKind.BackCover,
            TagImageKind.Wallpaper,
            TagImageKind.LiveWallpaper,
            TagImageKind.Artist,
            TagImageKind.Other
        ];

        public IReadOnlyList<TagImageKind> EmulationImageKinds { get; } =
        [
            TagImageKind.Cover,
            TagImageKind.BoxArt,
            TagImageKind.Gameplay,
            TagImageKind.BackCover,
            TagImageKind.Wallpaper,
            TagImageKind.LiveWallpaper,
            TagImageKind.Artist,
            TagImageKind.Other
        ];

        // Keep this for existing bindings; default metadata overlay should not expose
        // emulation-only kinds.
        public IReadOnlyList<TagImageKind> ImageKinds => MetadataImageKinds;

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
            await TryApplyCoverFromPs4InstalledGameAsync(item, CancellationToken.None).ConfigureAwait(false);
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

                    var newImages = (metadata?.Images ?? [])
                        .Select(img => new TagImageModel(img.Kind, img.Data, img.MimeType ?? "image/png") { OnDeleteImage = OnDeleteImage })
                        .ToList();

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
                        var data = p.Data.Data;
                        var mime = p.MimeType;
                        var desc = StripImageKindMarker(p.Description);
                        imagesToAdd.Add(new TagImageModel(kind, data, mime, desc) { OnDeleteImage = OnDeleteImage });
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
            await TryApplyCoverFromPs4InstalledGameAsync(item, CancellationToken.None).ConfigureAwait(false);
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

                foreach (var image in metadata.Images ?? [])
                {
                    if (image.Data == null || image.Data.Length == 0)
                        continue;

                    Images.Add(new TagImageModel(image.Kind, image.Data, image.MimeType ?? "image/png")
                    {
                        OnDeleteImage = OnDeleteImage
                    });
                }

                foreach (var video in metadata.Videos ?? [])
                {
                    if (video.Data == null || video.Data.Length == 0)
                        continue;

                    var model = new TagImageModel(video.Kind, video.Data, video.MimeType ?? "video/mp4")
                    {
                        OnDeleteImage = OnDeleteImage
                    };
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

        [RelayCommand]
        private async Task SaveMetadataAsync(string? path = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    path = FilePath;

                var isMissingFile = string.IsNullOrWhiteSpace(path) || !File.Exists(path);

                if (isMissingFile || (path != null && path.Contains("youtu", StringComparison.OrdinalIgnoreCase)))
                {
                    await SaveToMetadataCacheAsync(path);
                    return;
                }

                if (Images.Any(img => IsLocalMetadataOnlyKind(img.Kind)))
                {
                    await SaveToMetadataCacheAsync(path);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(VideoUrl))
                {
                    await SaveToMetadataCacheAsync(path);
                    return;
                }

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

                        var pic = new Picture([.. img.Data])
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
                    Images = [.. Images.Select(img => new ImageData
                    {
                        Data = img.Data,
                        MimeType = img.MimeType,
                        Kind = img.Kind
                    })],
                    Videos = [.. Images.Where(img => img.Kind == TagImageKind.LiveWallpaper)
                        .Select(img => new VideoData
                        {
                            MimeType = img.MimeType,
                            Data = img.Data,
                            Kind = img.Kind
                        })]
                };

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

        [RelayCommand]
        private async Task OpenAddImageDialog()
        {
            var mainWindow = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow == null) return;

            var storageProvider = mainWindow.StorageProvider;

            var fileOptions = new FilePickerOpenOptions
            {
                Title = "Select Images or Videos",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("JPEG"),
                new FilePickerFileType("PNG"),
                new FilePickerFileType("BMP"),
                new FilePickerFileType("GIF"),
                new FilePickerFileType("WebP"),
                new FilePickerFileType("AVIF"),
                new FilePickerFileType("MP4"),
                new FilePickerFileType("AVI"),
                new FilePickerFileType("MKV"),
                new FilePickerFileType("MOV"),
            ]
            };
            var results = await storageProvider.OpenFilePickerAsync(fileOptions);
            foreach (var result in results)
            {
                await using var stream = await result.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var data = ms.ToArray();
                var lowerName = result.Name.ToLower();
                var mimeType = lowerName switch
                {
                    { } s when s.EndsWith(".png") => "image/png",
                    { } s when s.EndsWith(".webp") => "image/webp",
                    { } s when s.EndsWith(".avif") => "image/avif",
                    { } s when s.EndsWith(".mp4") => "video/mp4",
                    { } s when s.EndsWith(".avi") => "video/avi",
                    { } s when s.EndsWith(".mkv") => "video/x-matroska",
                    { } s when s.EndsWith(".mov") => "video/quicktime",
                    _ => "image/jpeg"
                };
                var isVideo = mimeType.StartsWith("video/");
                var kind = isVideo ? TagImageKind.LiveWallpaper : SelectedImageKind;
                var newImage = new TagImageModel(kind, data, mimeType, isVideo ? result.Path.AbsolutePath : result.Name)
                {
                    OnDeleteImage = OnDeleteImage
                };
                if (newImage.Kind == TagImageKind.LiveWallpaper)
                {
                    await LoadImageAsync(newImage);
                    newImage.RaisePropertyChanged(nameof(Image));
                }
                Images.Add(newImage);
            }
        }

        [RelayCommand]
        private async Task AddImageFromUrlAsync(string? imageUrl = null)
        {
            var url = string.IsNullOrWhiteSpace(imageUrl) ? AddImageUrl : imageUrl;
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (await TryAddImageFromUrlAsync(url, SelectedImageKind))
            {
                AddImageUrl = string.Empty;
                ImageSearchStatus = "Image added.";
            }
        }

        [RelayCommand]
        private async Task PasteImageFromClipboardAsync()
        {
            var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow?.Clipboard == null)
                return;

            try
            {
                using var clipboardData = await mainWindow.Clipboard.TryGetDataAsync();
                var bitmap = clipboardData != null
                    ? await clipboardData.TryGetBitmapAsync()
                    : null;
                if (bitmap != null)
                {
                    using var ms = new MemoryStream();
                    bitmap.Save(ms);
                    AddImageToCollection(ms.ToArray(), "image/png", SelectedImageKind, "clipboard-image");
                    ImageSearchStatus = "Pasted image from clipboard.";
                    return;
                }
            }
            catch (Exception ex)
            {
                SLog.Warn("Clipboard bitmap read failed; trying text fallback.", ex);
            }

            try
            {
                var clipboardText = await mainWindow.Clipboard.TryGetTextAsync();
                if (!string.IsNullOrWhiteSpace(clipboardText))
                    await AddImageFromUrlAsync(clipboardText.Trim());
            }
            catch (Exception ex)
            {
                SLog.Warn("Clipboard text read failed.", ex);
            }
        }

        [RelayCommand]
        private async Task SearchImagesByTitleAsync()
        {
            var titleNorm = NormalizeSearchTitle(Title);
            if (string.IsNullOrWhiteSpace(titleNorm))
                titleNorm = NormalizeSearchTitle(_currentSelectedMedia?.Title);
            
            var artistNorm = NormalizeSearchTitle(Artists);
            if (string.IsNullOrWhiteSpace(artistNorm))
                artistNorm = NormalizeSearchTitle(_currentSelectedMedia?.Artist);

            var albumNorm = NormalizeSearchTitle(Album);
            if (string.IsNullOrWhiteSpace(albumNorm))
                albumNorm = NormalizeSearchTitle(_currentSelectedMedia?.Album);

            if (string.IsNullOrWhiteSpace(titleNorm)
                && string.IsNullOrWhiteSpace(artistNorm)
                && string.IsNullOrWhiteSpace(albumNorm))
            {
                titleNorm = NormalizeSearchTitle(GetSearchFallbackFromFilename());
            }

            IReadOnlyList<string> searchQueries;
            if (_currentSelectedMedia != null)
            {
                // For emulation cover searches triggered from the metadata overlay,
                // prefer the exact leading query shown in the textbox. Falling back
                // through multiple alternates here can dilute results even when the
                // visible query is already correct.
                var romQueries = BuildAutoCoverQueries(_currentSelectedMedia, albumNorm);
                searchQueries = romQueries.Count == 0
                    ? []
                    : [romQueries[0]];
            }
            else
            {
                searchQueries = BuildMetadataSearchQueries(titleNorm, artistNorm, albumNorm);
            }

            if (searchQueries.Count == 0)
                return;

            _searchMode = MetadataSearchMode.Images;
            var activeQuery = searchQueries[0];
            ImageSearchQuery = activeQuery;
            await SearchImagesCoreAsync(activeQuery, searchQueries, isRomSearch: _currentSelectedMedia != null);
        }

        [RelayCommand]
        private async Task AddGameplayAsync()
        {
            if (_currentSelectedMedia == null)
                return;

            var gameplayQuery = BuildGameplayVideoQuery(_currentSelectedMedia, Album);
            if (string.IsNullOrWhiteSpace(gameplayQuery))
                return;

            _searchMode = MetadataSearchMode.GameplayVideo;
            ImageSearchQuery = gameplayQuery;
            IsImageSearchOverlayOpen = true;
            IsImageSearchLoading = true;
            ImageSearchStatus = $"Searching YouTube gameplay videos for \"{gameplayQuery}\"...";

            try
            {
                var results = await SearchYouTubeGameplayVideosAsync(gameplayQuery);
                ImageSearchResults = new AvaloniaList<WebImageSearchResult>(results.Take(MaxImageSearchResults).ToList());
                ImageSearchStatus = ImageSearchResults.Count == 0
                    ? $"No YouTube gameplay videos found for \"{gameplayQuery}\"."
                    : $"Found {ImageSearchResults.Count} YouTube gameplay videos for \"{gameplayQuery}\".";
            }
            catch (Exception ex)
            {
                SLog.Warn("Gameplay video search failed.", ex);
                ImageSearchStatus = "Gameplay video search failed.";
                ImageSearchResults = [];
            }
            finally
            {
                IsImageSearchLoading = false;
            }
        }

        [RelayCommand]
        private async Task SearchImagesAsync(string? query = null)
        {
            var activeQuery = string.IsNullOrWhiteSpace(query) ? ImageSearchQuery : query;
            if (string.IsNullOrWhiteSpace(activeQuery))
                return;

            _searchMode = MetadataSearchMode.Images;
            await SearchImagesCoreAsync(activeQuery.Trim(), [activeQuery.Trim()], isRomSearch: true);
        }

        private async Task SearchImagesCoreAsync(string activeQuery, IReadOnlyList<string> searchQueries, bool isRomSearch = false)
        {
            ImageSearchQuery = activeQuery;
            IsImageSearchOverlayOpen = true;
            IsImageSearchLoading = true;
            ImageSearchStatus = "Searching web images...";

            try
            {
                var results = await SearchWebImagesAsync(searchQueries, isRomSearch);
                ImageSearchResults = new AvaloniaList<WebImageSearchResult>(results.Take(MaxImageSearchResults).ToList());
                ImageSearchStatus = ImageSearchResults.Count == 0
                    ? $"No images found for \"{activeQuery}\"."
                    : $"Found {ImageSearchResults.Count} image candidates for \"{activeQuery}\".";
            }
            catch (Exception ex)
            {
                SLog.Warn("Image search failed.", ex);
                ImageSearchStatus = "Image search failed.";
                ImageSearchResults = [];
            }
            finally
            {
                IsImageSearchLoading = false;
            }
        }

        [RelayCommand]
        private void CloseImageSearchOverlay()
        {
            IsImageSearchOverlayOpen = false;
        }

        [RelayCommand]
        private async Task SelectSearchImageAsync(WebImageSearchResult? result)
        {
            if (result == null)
                return;

            if (_searchMode == MetadataSearchMode.GameplayVideo)
            {
                VideoUrl = result.FullImageUrl;
                if (_currentSelectedMedia != null)
                    _currentSelectedMedia.VideoUrl = VideoUrl;
                IsImageSearchOverlayOpen = false;
                ImageSearchStatus = $"Selected gameplay video: {VideoUrl}";
                return;
            }

            if (await TryAddImageFromUrlAsync(result.FullImageUrl, SelectedImageKind))
            {
                IsImageSearchOverlayOpen = false;
                ImageSearchStatus = $"Selected image added as {SelectedImageKind}.";
            }
        }

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

        private void Close()
        {
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

        private static async Task<List<WebImageSearchResult>> SearchWebImagesAsync(IReadOnlyList<string> queries, bool isRomSearch = false)
        {
            var interimResults = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultsLock = new object();

            // Take fewer queries to significantly speed up searching
            var normalizedQueries = queries
                .Select(NormalizeSearchTitle)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(isRomSearch ? 2 : 3)
                .ToList();

            void AddResult(WebImageSearchResult result)
            {
                lock (resultsLock)
                {
                    if (interimResults.Count < MaxImageSearchResults && seen.Add(result.FullImageUrl))
                    {
                        interimResults.Add(result);
                    }
                }
            }

            // Using a shorter timeout for individual provider searches to skip slow ones
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            if (!isRomSearch)
            {
                try
                {
                    var itunesTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        var songUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=song&limit=40";
                        await LoadItunesResults(songUri, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);

                        results.Clear();
                        var albumUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=album&limit=40";
                        await LoadItunesResults(albumUri, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(itunesTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("iTunes search timed out."); }
            }

            if (interimResults.Count < 8) // Lower threshold to move faster to fallbacks
            {
                try
                {
                    var ddgTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        await LoadDuckDuckGoImageResults(query, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(ddgTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("DuckDuckGo search timed out."); }
            }

            if (interimResults.Count < 12)
            {
                try
                {
                    var bingTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        await LoadBingImageResults(query, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(bingTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("Bing search timed out."); }
            }

            if (interimResults.Count < 4)
            {
                try
                {
                    var googleTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        await LoadGoogleImageResults(query, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(googleTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("Google search timed out."); }
            }

            return interimResults;
        }

        private static async Task LoadDuckDuckGoImageResults(string query, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            try
            {
                foreach (var ddgQuery in BuildGoogleQueries(query))
                {
                    if (sink.Count >= MaxImageSearchResults)
                        break;

                    // DuckDuckGo VQD token is required for the image API
                    // First, get the main search page to extract the VQD
                    var mainUrl = $"https://duckduckgo.com/?q={Uri.EscapeDataString(ddgQuery)}&iax=images&ia=images";
                    using var mainRequest = new HttpRequestMessage(HttpMethod.Get, mainUrl);
                    mainRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

                    using var mainResponse = await ImageHttpClient.SendAsync(mainRequest, HttpCompletionOption.ResponseContentRead);
                    if (!mainResponse.IsSuccessStatusCode)
                        continue;

                    var mainHtml = await mainResponse.Content.ReadAsStringAsync();
                    var vqdMatch = Regex.Match(mainHtml, @"vqd=['""](?<vqd>[^'""]+)['""]|vqd=(?<vqd2>[^&'""\s]+)", RegexOptions.IgnoreCase);
                    var vqd = vqdMatch.Groups["vqd"].Value;
                    if (string.IsNullOrEmpty(vqd)) vqd = vqdMatch.Groups["vqd2"].Value;

                    if (string.IsNullOrEmpty(vqd))
                        continue;

                    // Now call the AJAX endpoint for images
                    var apiUrl = $"https://duckduckgo.com/i.js?l=us-en&o=json&q={Uri.EscapeDataString(ddgQuery)}&vqd={vqd}&f=,,,";
                    using var apiRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    apiRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                    apiRequest.Headers.Referrer = new Uri(mainUrl);

                    using var apiResponse = await ImageHttpClient.SendAsync(apiRequest, HttpCompletionOption.ResponseContentRead);
                    if (!apiResponse.IsSuccessStatusCode)
                        continue;

                    var json = await apiResponse.Content.ReadAsStringAsync();
                    ExtractDuckDuckGoImageResults(json, seen, sink);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"DuckDuckGo image search failed for query: {query}", ex);
            }
        }

        private static void ExtractDuckDuckGoImageResults(string json, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            var decoded = WebUtility.HtmlDecode(json)
                .Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            foreach (Match match in DdgJsonImageUrlRegex.Matches(decoded))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = match.Groups["url"].Value;
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }
        }

        private static async Task LoadBingImageResults(string query, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            try
            {
                foreach (var bingQuery in BuildGoogleQueries(query))
                {
                    if (sink.Count >= MaxImageSearchResults)
                        break;

                    var url = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(bingQuery)}&form=HDRSC3&first=1";

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                    request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                    request.Headers.Referrer = new Uri("https://www.bing.com/");

                    using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var html = await response.Content.ReadAsStringAsync();
                    ExtractBingImageResults(html, seen, sink);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Bing image search failed for query: {query}", ex);
            }
        }

        private static async Task LoadGoogleImageResults(string query, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            try
            {
                foreach (var googleQuery in BuildGoogleQueries(query))
                {
                    if (sink.Count >= MaxImageSearchResults)
                        break;

                    var url = $"https://www.google.com/search?tbm=isch&udm=2&hl=en&q={Uri.EscapeDataString(googleQuery)}";

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                    request.Headers.Add("Cookie", GoogleConsentCookie);
                    request.Headers.Referrer = new Uri("https://www.google.com/");

                    using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var html = await response.Content.ReadAsStringAsync();
                    ExtractGoogleImageResults(html, seen, sink);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Google image search failed for query: {query}", ex);
            }
        }

        private static IEnumerable<string> BuildGoogleQueries(string query)
        {
            var googleQueries = new List<string>();
            var normalized = NormalizeSearchTitle(query);
            AddDistinctQuery(googleQueries, normalized);

            foreach (var aliasQuery in ExpandSearchQueryAliases(normalized))
                AddDistinctQuery(googleQueries, aliasQuery);

            AddDistinctQuery(googleQueries, $"{normalized} album cover");
            AddDistinctQuery(googleQueries, $"{normalized} cover art");

            var stripped = StripCoverSearchTokens(normalized);
            if (!string.IsNullOrWhiteSpace(stripped) && !string.Equals(stripped, normalized, StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctQuery(googleQueries, stripped);
                foreach (var aliasQuery in ExpandSearchQueryAliases(stripped))
                    AddDistinctQuery(googleQueries, aliasQuery);
            }

            return googleQueries;
        }

        private static IEnumerable<string> ExpandSearchQueryAliases(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                yield break;

            foreach (var pair in EmulationConsoleCatalog.SearchAliases)
            {
                if (!query.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var alias in pair.Value)
                    yield return Regex.Replace(query, $@"\b{Regex.Escape(pair.Key)}\b", alias, RegexOptions.IgnoreCase);
            }
        }

        private static string StripCoverSearchTokens(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            var stripped = CoverSearchTokenRegex.Replace(query, " ");
            return MultiSpaceRegex.Replace(stripped, " ").Trim();
        }

        private static void ExtractGoogleImageResults(string html, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            foreach (Match match in GoogleImgTagRegex.Matches(html))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            var decodedHtml = WebUtility.HtmlDecode(html)
                .Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            foreach (Match match in GoogleImgResRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in DirectImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in GoogleJsonImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in GoogleQuotedHttpUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }
        }

        private static void ExtractBingImageResults(string html, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            var decodedHtml = WebUtility.HtmlDecode(html)
                .Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            foreach (Match match in BingJsonImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in BingHtmlEncodedImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in DirectImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }
        }

        private static string DecodeGoogleUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return string.Empty;

            var decoded = WebUtility.HtmlDecode(rawUrl.Trim());
            decoded = decoded.Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            try
            {
                decoded = Uri.UnescapeDataString(decoded);
            }
            catch
            {
                // Keep the best-effort decoded value.
            }

            return decoded;
        }

        private static void TryAddGoogleImageResult(string imageUrl, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            if (sink.Count >= MaxImageSearchResults)
                return;

            if (!IsUsableImageUrl(imageUrl) || !seen.Add(imageUrl))
                return;

            sink.Add(new WebImageSearchResult
            {
                ThumbnailUrl = imageUrl,
                FullImageUrl = imageUrl,
                Title = "Google Image",
                Artist = "Web"
            });
        }

        private static bool IsUsableImageUrl(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return false;

            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            if (imageUrl.Contains("googlelogo", StringComparison.OrdinalIgnoreCase)
                || imageUrl.Contains("/images/branding/", StringComparison.OrdinalIgnoreCase)
                || imageUrl.Contains("/gen_204", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var host = uri.Host;
            var isGoogleThumbnailHost =
                host.StartsWith("encrypted-tbn", StringComparison.OrdinalIgnoreCase)
                || host.Contains("gstatic.com", StringComparison.OrdinalIgnoreCase)
                || host.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase);

            if (host.Contains("google.", StringComparison.OrdinalIgnoreCase) && !isGoogleThumbnailHost)
                return false;

            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Contains("/imgres", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Contains("/url", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Reject marketplaces and social media that often host "lazy" photos or listings
            // like the Dreamcast jewel case on a teal background.
            var trashHosts = new[]
            {
                "ebayimg.com", "ebay.com", "mercari.com", "poshmark.com",
                "fbcdn.net", "fb.com", "instagram.com", "twimg.com",
                "pinterest.com", "etsystatic.com", "etsy.com", "carousell.com",
                "offerup.com", "depop.com", "gumtree.com"
            };

            if (trashHosts.Any(h => host.Contains(h, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }

        private static async Task LoadItunesResults(string uri, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            using var response = await ImageHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("results", out var jsonResults) || jsonResults.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in jsonResults.EnumerateArray())
            {
                var thumb = item.TryGetProperty("artworkUrl100", out var artworkNode)
                    ? artworkNode.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(thumb))
                    continue;

                var full = UpgradeArtworkSize(thumb);
                if (!seen.Add(full))
                    continue;

                var trackName = item.TryGetProperty("trackName", out var trackNode) ? trackNode.GetString() : string.Empty;
                var collectionName = item.TryGetProperty("collectionName", out var collectionNode) ? collectionNode.GetString() : string.Empty;
                var artistName = item.TryGetProperty("artistName", out var artistNode) ? artistNode.GetString() : string.Empty;

                sink.Add(new WebImageSearchResult
                {
                    ThumbnailUrl = thumb,
                    FullImageUrl = full,
                    Title = trackName ?? collectionName ?? string.Empty,
                    Artist = artistName ?? string.Empty
                });

                if (sink.Count >= MaxImageSearchResults)
                    return;
            }
        }

        private static string UpgradeArtworkSize(string artworkUrl)
        {
            if (string.IsNullOrWhiteSpace(artworkUrl))
                return artworkUrl;

            return Regex.Replace(artworkUrl, @"\d+x\d+bb", "1200x1200bb", RegexOptions.IgnoreCase);
        }

        private async Task<bool> TryAddImageFromUrlAsync(string url, TagImageKind kind)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ImageSearchStatus = "Invalid image URL.";
                return false;
            }

            try
            {
                using var response = await ImageHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    ImageSearchStatus = "Could not download image.";
                    return false;
                }

                var mimeType = response.Content.Headers.ContentType?.MediaType;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0)
                {
                    ImageSearchStatus = "Downloaded image is empty.";
                    return false;
                }

                mimeType ??= GuessMimeTypeFromUrl(uri.AbsolutePath);
                if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    ImageSearchStatus = "URL is not an image.";
                    return false;
                }

                AddImageToCollection(bytes, mimeType, kind, uri.AbsoluteUri);
                return true;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to add image from URL: {url}", ex);
                ImageSearchStatus = "Failed to add image from URL.";
                return false;
            }
        }

        private void AddImageToCollection(byte[] data, string mimeType, TagImageKind kind, string description)
        {
            var model = new TagImageModel(kind, data, mimeType, description)
            {
                OnDeleteImage = OnDeleteImage
            };

            Images.Add(model);
        }

        private static string GuessMimeTypeFromUrl(string url)
        {
            var lower = url.ToLowerInvariant();
            return lower switch
            {
                var s when s.EndsWith(".png") => "image/png",
                var s when s.EndsWith(".webp") => "image/webp",
                var s when s.EndsWith(".avif") => "image/avif",
                var s when s.EndsWith(".gif") => "image/gif",
                _ => "image/jpeg"
            };
        }

        private static string BuildPictureDescription(TagImageModel model)
        {
            var baseDescription = StripImageKindMarker(model.Description);
            if (model.Kind == TagImageKind.Wallpaper)
            {
                baseDescription = string.IsNullOrWhiteSpace(baseDescription)
                    ? "wallpaper"
                    : baseDescription;
            }

            return $"[AES_KIND:{model.Kind}] {baseDescription}".Trim();
        }

        private static PictureType MapKindToPictureType(TagImageModel model) => model.Kind switch
        {
            TagImageKind.Cover => PictureType.FrontCover,
            TagImageKind.BackCover => PictureType.BackCover,
            TagImageKind.Artist => PictureType.Artist,
            TagImageKind.Wallpaper => PictureType.Illustration,
            _ => PictureType.Other,
        };

        private static TagImageKind MapPictureToKind(IPicture? pic)
        {
            if (pic == null)
                return TagImageKind.Other;

            if (TryGetKindFromDescription(pic.Description, out var descriptionKind))
                return descriptionKind;

            return pic.Type switch
            {
                PictureType.FrontCover => TagImageKind.Cover,
                PictureType.BackCover => TagImageKind.BackCover,
                PictureType.Artist => TagImageKind.Artist,
                PictureType.Illustration => TagImageKind.Wallpaper, // Map Illustration back to Wallpaper
                _ => (pic.Description?.IndexOf("wallpaper", StringComparison.OrdinalIgnoreCase) >= 0
                    ? TagImageKind.Wallpaper
                    : TagImageKind.Other)
            };
        }

        private static bool TryGetKindFromDescription(string? description, out TagImageKind kind)
        {
            kind = TagImageKind.Other;
            if (string.IsNullOrWhiteSpace(description))
                return false;

            var match = ImageKindDescriptionRegex.Match(description);
            if (!match.Success)
                return false;

            return Enum.TryParse(match.Groups["kind"].Value, ignoreCase: true, out kind);
        }

        private static string StripImageKindMarker(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return string.Empty;

            return ImageKindDescriptionRegex.Replace(description, string.Empty).Trim();
        }

        private static bool IsLocalMetadataOnlyKind(TagImageKind kind)
            => kind is TagImageKind.Gameplay or TagImageKind.BoxArt;

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

        private async Task<bool> TryApplyCoverFromPs3InstalledGameAsync(MediaItem item, CancellationToken cancellationToken)
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
                var entries = JsonSerializer.Deserialize<List<RomTitleEntry>>(json) ?? [];

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
            catch
            {
                // Ignore database load failures and fall back to existing titles.
            }

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

        private sealed class RomTitleEntry
        {
            [JsonPropertyName("serial")]
            public string? Serial { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }
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

        private async Task<bool> TryApplyCoverFromPs4InstalledGameAsync(MediaItem item, CancellationToken cancellationToken)
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
                    catch
                    {
                        // Ignore cache removal errors for now.
                    }
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
                metadata.Images ??= [];
                metadata.Images.RemoveAll(image => image.Kind == TagImageKind.Cover || image.Kind == TagImageKind.BackCover);
                metadata.Images.Insert(0, new ImageData
                {
                    Data = bytes,
                    MimeType = mimeType,
                    Kind = TagImageKind.Cover
                });

                if (backCoverBytes is { Length: > 0 })
                {
                    metadata.Images.Insert(1, new ImageData
                    {
                        Data = backCoverBytes,
                        MimeType = backCoverMimeType ?? GuessMimeTypeFromBytes(backCoverBytes),
                        Kind = TagImageKind.BackCover
                    });
                }

                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
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
