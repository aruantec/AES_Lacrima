using AES_Controls.Player.Interfaces;
using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        public override void Prepare()
        {
            //Get fresh player instances
            AudioPlayer = DiLocator.ResolveViewModel<IMediaInterface>();
        }

        [RelayCommand]
        private async Task SetPosition(double position)
        {

        }

        [RelayCommand]
        private void ToggleAlbumlist()
        {
            IsAlbumlistOpen = !IsAlbumlistOpen;
        }
    }
}