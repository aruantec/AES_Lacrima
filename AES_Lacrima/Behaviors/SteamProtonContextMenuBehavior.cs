using AES_Lacrima.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;
using System.ComponentModel;
using System.Linq;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// Populates the Steam Proton submenu when the carousel context menu opens.
/// </summary>
public sealed class SteamProtonContextMenuBehavior : Behavior<Control>
{
    private ContextMenu? _contextMenu;

    protected override void OnAttached()
    {
        base.OnAttached();
        _contextMenu = AssociatedObject?.ContextMenu;
        _contextMenu?.Opening += OnContextMenuOpening;
    }

    protected override void OnDetaching()
    {
        _contextMenu?.Opening -= OnContextMenuOpening;
        _contextMenu = null;
        base.OnDetaching();
    }

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu contextMenu ||
            AssociatedObject?.DataContext is not EmulationViewModel viewModel)
        {
            return;
        }

        var protonMenu = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Name, "SteamProtonVersionMenuItem", System.StringComparison.Ordinal));

        if (protonMenu == null)
            return;

        viewModel.PopulateSteamProtonContextMenu(protonMenu);
    }
}
