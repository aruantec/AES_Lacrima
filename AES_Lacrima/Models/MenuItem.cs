using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;

namespace AES_Lacrima.Models
{
    public partial class MenuItem : ObservableObject
    {
        [ObservableProperty]
        private string? _title;

        [ObservableProperty]
        private string? _cover;

        [ObservableProperty]
        private string? _tooltip;

        [ObservableProperty]
        private ICommand? _command;
    }
}
