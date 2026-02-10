using AES_Controls.Player.Interfaces;
using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels
{
    public interface IMusicViewModel;

    [AutoRegister]
    internal partial class MusicViewModel : ViewModelBase, IMusicViewModel
    {
        [ObservableProperty]
        private bool _isAlbumlistOpen;

        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        [ObservableProperty]
        private IMediaInterface? _audioPlayer;

        [AutoResolve]
        private MainWindowViewModel? _mainWindowViewModel;

        public override void Prepare()
        {
            //Load settings
            LoadSettings();
            //Get fresh player instances
            AudioPlayer = DiLocator.ResolveViewModel<IMediaInterface>();
            //Set main spectrum
            _mainWindowViewModel?.Spectrum = AudioPlayer?.Spectrum;
            // PlayFile may be null if resolution fails; invoke only when available.
            _ = AudioPlayer?.PlayFile(@"C:\Users\Admin\Music\WE DANCED THE NIGHT AWAY.mp3");
        }

        [RelayCommand]
        private async Task SetPosition(double position)
        {
            AudioPlayer?.SetPosition(position);
        }

        [RelayCommand]
        private void ToggleAlbumlist()
        {
            IsAlbumlistOpen = !IsAlbumlistOpen;
        }

        [RelayCommand]
        private void Stop()
        {
            AudioPlayer?.Stop();
        }

        [RelayCommand]
        private void TogglePlay()
        {
            if (AudioPlayer == null) return;
            if (AudioPlayer.IsPlaying)
                AudioPlayer.Pause();
            else
                AudioPlayer.Play();
        }

        [RelayCommand]
        private void ToggleRepeat()
        {
            if (AudioPlayer != null)
            {
                AudioPlayer.Loop = !AudioPlayer.Loop;
            }
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            IsAlbumlistOpen = ReadBoolSetting(section, nameof(IsAlbumlistOpen), false);
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(IsAlbumlistOpen), IsAlbumlistOpen);
        }
    }
}