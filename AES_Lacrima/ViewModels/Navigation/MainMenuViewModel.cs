using AES_Core.DI;
using AES_Core.Interfaces;
using System.Windows.Input;

namespace AES_Lacrima.ViewModels.Navigation
{
    public interface IMainMenuViewModel : IViewModelBase;

    [AutoRegister]
    public partial class MainMenuViewModel : ViewModelBase, IMainMenuViewModel
    {
        public ICommand? ShowSettingsCommand { get; set; }
    }
}