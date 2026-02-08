using AES_Core.DI;

namespace AES_Lacrima.ViewModels
{
    public interface IEmulationViewModel;

    [AutoRegister]
    internal partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {

    }
}