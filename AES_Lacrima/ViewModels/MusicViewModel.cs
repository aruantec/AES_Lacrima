using AES_Core.DI;

namespace AES_Lacrima.ViewModels
{
    public interface IMusicViewModel;

    [AutoRegister]
    internal partial class MusicViewModel : ViewModelBase, IMusicViewModel
    {

    }
}