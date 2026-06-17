using AES_Core.DI;
using AES_Controls.Player.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels
{
    public interface IVideoViewModel;

    [AutoRegister]
    internal partial class VideoViewModel : MusicViewModel, IVideoViewModel
    {
        public override bool IsVideoMode => true;

        public override bool IsMetadataEditorVisible => false;

        protected override string FilePickerTitle => "Add Video Files";

        protected override string FilePickerTypeName => "Video Files";

        protected override IReadOnlyList<string> SupportedTypes => VideoSupportedTypes;

        protected override bool AllowOnlineCoverLookup => false;

        protected override bool PreferHighQualityOnlineStream => UseHighQualityStream;

        [ObservableProperty]
        private bool _useHighQualityStream = true;

        partial void OnUseHighQualityStreamChanged(bool value)
        {
            SaveSettings();
            _ = ReloadCurrentOnlineStreamIfNeededAsync();
        }

        [RelayCommand]
        private void ToggleHighQualityStream() => UseHighQualityStream = !UseHighQualityStream;

        private async Task ReloadCurrentOnlineStreamIfNeededAsync()
        {
            var player = AudioPlayer;
            if (player == null)
                return;

            var item = player.CurrentMediaItem;
            if (item?.FileName == null || !IsOnlineMediaUrl(item.FileName))
                return;

            item.OnlineUrls = null;
            await TryPlayMediaItemAsync(item).ConfigureAwait(false);
        }

        private static bool IsOnlineMediaUrl(string fileName) =>
            fileName.StartsWith("http", System.StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("http", System.StringComparison.OrdinalIgnoreCase);
    }
}
