using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

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
        [ObservableProperty]
        private AvaloniaList<MediaItem> _children = [];
    }
}