using AES_Controls.Player.Models;

namespace AES_Lacrima.ViewModels.SectionHandlers
{
    /// <summary>
    /// Interface for album-specific ROM title normalization.
    /// Different platforms have different title extraction and normalization rules.
    /// </summary>
    public interface IAlbumNormalizer
    {
        /// <summary>Normalize ROM titles for a specific album</summary>
        void NormalizeRomTitles(FolderMediaItem album);
    }
}
