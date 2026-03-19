using AES_Core.DI;
using AES_Lacrima.ViewModels;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;

namespace AES_Lacrima.Mini.ViewModels
{
    public interface IVisualizerViewModel;

    [AutoRegister]
    public partial class VisualizerViewModel : ViewModelBase
    {
        private SettingsViewModel? _subscribedSettingsViewModel;

        [AutoResolve]
        [ObservableProperty]
        private MusicViewModel? _musicViewModel;

        [AutoResolve]
        [ObservableProperty]
        private MinViewModel? _minViewModel;

        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        public bool IsShaderToySelected =>
            SettingsViewModel?.ShowShaderToy == true &&
            SettingsViewModel.SelectedShadertoy != null;

        public AvaloniaList<double>? SpectrumSource =>
            MinViewModel?.MusicViewModel?.AudioPlayer?.Spectrum ??
            MusicViewModel?.AudioPlayer?.Spectrum;

        partial void OnMusicViewModelChanged(MusicViewModel? value)
        {
            OnPropertyChanged(nameof(SpectrumSource));
        }

        partial void OnMinViewModelChanged(MinViewModel? value)
        {
            OnPropertyChanged(nameof(SpectrumSource));
        }

        partial void OnSettingsViewModelChanged(SettingsViewModel? value)
        {
            if (_subscribedSettingsViewModel != null)
            {
                _subscribedSettingsViewModel.PropertyChanged -= OnSettingsPropertyChanged;
            }

            if (value != null)
            {
                value.PropertyChanged += OnSettingsPropertyChanged;
            }

            _subscribedSettingsViewModel = value;
            OnPropertyChanged(nameof(IsShaderToySelected));
            OnPropertyChanged(nameof(SpectrumSource));
        }

        [RelayCommand]
        private void SelectShaderToy(ShaderItem? shaderItem)
        {
            if (SettingsViewModel == null) return;

            SettingsViewModel.SelectedShadertoy = shaderItem;
            SettingsViewModel.ShowShaderToy = shaderItem != null;
            OnPropertyChanged(nameof(IsShaderToySelected));
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedShadertoy) ||
                e.PropertyName == nameof(SettingsViewModel.ShowShaderToy))
            {
                OnPropertyChanged(nameof(IsShaderToySelected));
            }
        }
    }
}
