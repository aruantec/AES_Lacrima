using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace AES_Lacrima.ViewModels.Prompts
{
    public partial class EmulatorErrorPromptViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _message = string.Empty;

        [ObservableProperty]
        private string _details = string.Empty;

        public event Action? RequestClose;

        public EmulatorErrorPromptViewModel(string title, string message, string details)
        {
            _title = title;
            _message = message;
            _details = details;
        }

        [RelayCommand]
        private void Close()
        {
            RequestClose?.Invoke();
        }
    }
}
