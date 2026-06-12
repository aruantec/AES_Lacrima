using Avalonia.Collections;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AES_Controls.Player.Models
{
    /// <summary>
    /// Represents a specialized media item that acts as a container for other media items,
    /// such as a folder or an album directory.
    /// </summary>
    public partial class FolderMediaItem : MediaItem
    {
        /// <summary>
        /// Maximum number of child covers loaded and shown on an album tile fan.
        /// </summary>
        public const int AlbumTilePresentationCoverCount = 4;

        /// <summary>
        /// Gets or sets the list of child media items contained within this folder.
        /// </summary>
        [JsonIgnore]
        [ObservableProperty]
        private AvaloniaList<MediaItem> _children = [];

        /// <summary>
        /// Curated stack used by folder album tiles (fan covers + top cover item).
        /// </summary>
        [JsonIgnore]
        [ObservableProperty]
        private AvaloniaList<MediaItem> _previewItems = [];

        /// <summary>
        /// Total child count when not all items are loaded into <see cref="Children"/> yet.
        /// Zero means use <see cref="Children"/>.Count.
        /// </summary>
        [JsonIgnore]
        [ObservableProperty]
        private int _totalChildCount;

        public void RebuildPreviewItems(bool useFirstItemCover = false, bool rebuildStructure = true)
        {
            Bitmap? topCover = CoverBitmap;
            var firstChild = Children.FirstOrDefault();

            if (useFirstItemCover && firstChild != null)
                topCover = firstChild.CoverBitmap ?? CoverBitmap;

            if (!rebuildStructure && PreviewItems.Count > 0)
            {
                var topItem = PreviewItems[^1];
                if (!Children.Contains(topItem))
                    topItem.CoverBitmap = topCover;

                return;
            }

            var fanSource = useFirstItemCover && firstChild != null
                ? Children.Skip(1)
                : Children;

            var previewItems = new AvaloniaList<MediaItem>();
            foreach (var child in fanSource)
            {
                previewItems.Add(child);
                if (previewItems.Count >= AlbumTilePresentationCoverCount - 1)
                    break;
            }

            previewItems.Add(new MediaItem
            {
                Title = Title,
                Album = Title,
                FileName = FileName,
                CoverBitmap = topCover
            });

            PreviewItems = previewItems;
        }

        /// <summary>
        /// Children whose covers are used for album-tile presentation (at most <see cref="AlbumTilePresentationCoverCount"/>).
        /// </summary>
        public IEnumerable<MediaItem> GetPresentationCoverChildren(bool useFirstItemCover = false)
        {
            var firstChild = Children.FirstOrDefault();
            int yielded = 0;

            if (useFirstItemCover && firstChild != null)
            {
                yield return firstChild;
                yielded++;
            }

            var fanSource = useFirstItemCover && firstChild != null
                ? Children.Skip(1)
                : Children;

            foreach (var child in fanSource)
            {
                if (yielded >= AlbumTilePresentationCoverCount)
                    yield break;

                yield return child;
                yielded++;
            }
        }
    }
}
