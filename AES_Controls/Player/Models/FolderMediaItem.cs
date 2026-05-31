using Avalonia.Collections;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
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
                if (previewItems.Count >= 2)
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
    }
}
