using AES_Core.DI;
using AES_Lacrima.Services;
using AES_Lacrima.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Threading.Tasks;

namespace AES_Lacrima.Mini.ViewModels
{
    public interface IMiniEqualizerViewModel;

    [AutoRegister]
    public partial class MiniEqualizerViewModel : ViewModelBase, IMiniEqualizerViewModel
    {
        [AutoResolve]
        [ObservableProperty]
        private EqualizerService? _equalizerService;

        [AutoResolve]
        [ObservableProperty]
        private MusicViewModel? _musicViewModel;

        [AutoResolve]
        [ObservableProperty]
        private MinViewModel? _minViewModel;

        public override void Prepare()
        {
            _ = Task.Run(async () =>
            {
                // Initialize equalizer and load settings off-thread
                if (EqualizerService != null && MusicViewModel?.AudioPlayer != null)
                    await EqualizerService.InitializeAsync(MusicViewModel?.AudioPlayer!);
            });
        }
    }
}