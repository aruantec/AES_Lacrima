using AES_Lacrima.Mini.ViewModels;
using AES_Lacrima.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// Opens the attached control's context menu on left click and populates it
/// with ShaderToy entries from <see cref="SettingsViewModel"/>.
/// </summary>
public class OpenShaderToyContextMenuBehavior : Behavior<Control>
{
    private ContextMenu? _contextMenu;

    /// <summary>
    /// Invoked when the behavior is attached to a control.  Hooks the pointer
    /// pressed event and keeps a reference to the control's ContextMenu so that
    /// it can be populated before display.
    /// </summary>
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject == null) return;

        AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        _contextMenu = AssociatedObject.ContextMenu;
        _contextMenu?.Opening += OnContextMenuOpening;
    }

    /// <summary>
    /// Cleans up event handlers when the behavior is removed from its associated
    /// control.
    /// </summary>
    protected override void OnDetaching()
    {
        base.OnDetaching();
        AssociatedObject?.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _contextMenu?.Opening -= OnContextMenuOpening;
    }

    /// <summary>
    /// Handler for <see cref="InputElement.PointerPressedEvent"/>.  If the left
    /// button is pressed the menu is filled and opened manually, and the event is
    /// marked handled to prevent other controls from acting on the click.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (AssociatedObject == null) return;

        var point = e.GetCurrentPoint(AssociatedObject);
        if (!point.Properties.IsLeftButtonPressed) return;

        PopulateContextMenu(AssociatedObject.ContextMenu);
        AssociatedObject.ContextMenu?.Open(AssociatedObject);
        e.Handled = true;
    }

    /// <summary>
    /// Populates the context menu just before it becomes visible.  This allows the
    /// menu to be updated when it is opened via the standard right‑click path.
    /// </summary>
    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is ContextMenu contextMenu)
        {
            PopulateContextMenu(contextMenu);
        }
    }

    /// <summary>
    /// Builds the list of menu items based on the current <see cref="VisualizerViewModel.SettingsViewModel"/>
    /// shader toy collection and assigns it to the supplied <paramref name="contextMenu"/>.
    /// </summary>
    /// <param name="contextMenu">Menu to populate; if <c>null</c> the method does nothing.</param>
    private void PopulateContextMenu(ContextMenu? contextMenu)
    {
        if (contextMenu == null || AssociatedObject?.DataContext is not VisualizerViewModel viewModel)
        {
            return;
        }

        var selected = viewModel.SettingsViewModel?.SelectedShadertoy;

        var items = new List<object>
        {
            CreateShaderMenuItem("Default Spectrum", null, viewModel, selected == null)
        };

        var shaders = viewModel.SettingsViewModel?.ShaderToys;
        if (shaders != null && shaders.Count > 0)
        {
            items.Add(new Separator());
            foreach (var shader in shaders)
            {
                var isSelected = IsSameShader(selected, shader);
                items.Add(CreateShaderMenuItem(shader.Name, shader, viewModel, isSelected));
            }
        }

        contextMenu.ItemsSource = items;
    }

    /// <summary>
    /// Compares two <see cref="ShaderItem"/> instances by path, ignoring case.
    /// </summary>
    /// <param name="selected">Currently selected shader (may be <c>null</c>).</param>
    /// <param name="candidate">Shader to test for equality (may be <c>null</c>).</param>
    /// <returns><c>true</c> if both non‑null items reference the same file path.</returns>
    private static bool IsSameShader(ShaderItem? selected, ShaderItem? candidate)
    {
        if (selected == null || candidate == null) return false;
        return string.Equals(selected.Path, candidate.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Constructs a <see cref="MenuItem"/> representing a single shader toy entry.
    /// </summary>
    /// <param name="header">Text shown in the menu item.</param>
    /// <param name="shader">Associated <see cref="ShaderItem"/> or <c>null</c> for the "none" entry.</param>
    /// <param name="viewModel">Visualizer view model used to dispatch the selection command.</param>
    /// <param name="isSelected">Whether the item corresponds to the currently selected shader; if
    /// <c>true</c> a checkmark icon and bold font are applied.</param>
    /// <returns>A newly created <see cref="MenuItem"/> wired to the view model command.</returns>
    private static MenuItem CreateShaderMenuItem(string header, ShaderItem? shader, VisualizerViewModel viewModel, bool isSelected)
    {
        var item = new MenuItem
        {
            Header = header
        };

        if (isSelected)
        {
            item.Icon = new TextBlock { Text = "✓" };
            item.FontWeight = FontWeight.Bold;
        }

        item.Click += (_, _) =>
        {
            if (viewModel.SelectShaderToyCommand.CanExecute(shader))
            {
                viewModel.SelectShaderToyCommand.Execute(shader);
            }
        };

        return item;
    }
}
