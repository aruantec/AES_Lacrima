using AES_Controls.Player.Models;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel
    {
        [RelayCommand]
        private async Task OpenCurrentSectionDolphinCheats(object? parameter)
        {
            if (!ShowCurrentSectionDolphinCheatsMenuItem)
                return;

            var target = ResolveShadPs4ContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            await DolphinCheatsEditor.LoadAsync(
                CurrentSectionDolphinEmulatorPath,
                CurrentSectionEmulatorHandler?.LauncherPath,
                CurrentSectionEmulatorHandler?.FlatpakAppId,
                target.FileName,
                target.Title,
                LoadedAlbum?.Title).ConfigureAwait(true);
        }
    }
}
