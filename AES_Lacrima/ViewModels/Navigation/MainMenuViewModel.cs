using AES_Core.DI;
using AES_Core.Interfaces;

namespace AES_Lacrima.ViewModels.Navigation
{
    public interface IMainMenuViewModel : IViewModelBase;

    [AutoRegister]
    internal partial class MainMenuViewModel : ViewModelBase, IMainMenuViewModel
    {

    }
}