using AES_Core.DI;

namespace AES_Lacrima.ViewModels
{
    public interface IVideoViewModel;

    [AutoRegister]
    internal partial class VideoViewModel : ViewModelBase, IVideoViewModel
    {

    }
}