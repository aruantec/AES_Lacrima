using AES_Core.DI;
using AES_Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels;

internal interface ISettingsViewModel : IViewModelBase;

[AutoRegister]
public partial class SettingsViewModel : ViewModelBase, ISettingsViewModel
{
    [ObservableProperty]
    private string? _ffmpegPath;
}