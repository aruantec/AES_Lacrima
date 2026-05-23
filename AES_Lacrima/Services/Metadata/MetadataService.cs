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

        public string ImageSearchOverlayHeader =>
            _searchMode == MetadataSearchMode.GameplayVideo
                ? "Select Gameplay Video"
                : "Select Cover Image";

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
    }
}
