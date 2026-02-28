using AES_Core.DI;
using AES_Lacrima.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.Mini.ViewModels
{
    public interface IVisualizerViewModel;

    [AutoRegister]
    public partial class VisualizerViewModel : ViewModelBase
    {
        [AutoResolve]
        [ObservableProperty]
        private MusicViewModel? _musicViewModel;
    }
}