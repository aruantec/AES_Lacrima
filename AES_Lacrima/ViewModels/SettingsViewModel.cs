using AES_Core.DI;
using AES_Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels;

public interface ISettingsViewModel : IViewModelBase;

[AutoRegister]
internal partial class SettingsViewModel : ViewModelBase, ISettingsViewModel
{
    [ObservableProperty]
    private string? _ffmpegPath;
}