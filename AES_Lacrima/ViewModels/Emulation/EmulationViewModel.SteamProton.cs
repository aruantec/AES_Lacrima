using AES_Core.DI;
using AES_Lacrima.Services.Steam;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Linq;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    public bool ShowSteamProtonVersionMenuItem =>
        OperatingSystem.IsLinux() &&
        IsSteamAlbum(GetBrowseAlbum()) &&
        HasActiveAlbumItems &&
        !string.IsNullOrWhiteSpace(GetContextMenuSteamAppId());

    public void PopulateSteamProtonContextMenu(MenuItem protonMenu)
    {
        protonMenu.Items.Clear();

        var settings = SettingsViewModel ?? DiLocator.ResolveViewModel<SettingsViewModel>();
        if (settings == null)
            return;

        var appId = GetContextMenuSteamAppId();
        if (string.IsNullOrWhiteSpace(appId))
            return;

        var preferences = settings.BuildSteamProtonLaunchPreferences();
        var game = SteamInstalledGameHelper.GetInstalledGame(appId);
        var effectiveDirectory = game == null
            ? null
            : SteamInstalledGameHelper.ResolveProtonDirectoryForGame(game, preferences);
        var overrideDirectory = settings.GetSteamGameProtonOverride(appId);

        var automaticItem = CreateSteamProtonMenuItem(
            SteamProtonCatalogHelper.AutomaticDisplayName,
            appId,
            protonDirectory: null,
            isSelected: string.IsNullOrWhiteSpace(overrideDirectory));
        protonMenu.Items.Add(automaticItem);

        var installedVersions = SteamProtonCatalogHelper.GetInstalledProtonVersions();

        if (installedVersions.Count == 0)
        {
            protonMenu.Items.Add(new MenuItem
            {
                Header = "No Proton installs found",
                IsEnabled = false
            });
            return;
        }

        protonMenu.Items.Add(new Separator());

        foreach (var version in installedVersions)
        {
            var isSelected = !string.IsNullOrWhiteSpace(overrideDirectory)
                ? string.Equals(overrideDirectory, version.DirectoryPath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(effectiveDirectory, version.DirectoryPath, StringComparison.OrdinalIgnoreCase);

            protonMenu.Items.Add(CreateSteamProtonMenuItem(
                version.DisplayName,
                appId,
                version.DirectoryPath,
                isSelected));
        }
    }

    private static MenuItem CreateSteamProtonMenuItem(
        string header,
        string appId,
        string? protonDirectory,
        bool isSelected)
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
            var settings = DiLocator.ResolveViewModel<SettingsViewModel>();
            settings?.SetSteamGameProtonOverride(appId, protonDirectory);
        };

        return item;
    }

    private string? GetContextMenuSteamAppId()
    {
        var target = ResolveShadPs4ContextMenuTarget(PointedIndex >= 0 ? PointedIndex : SelectedIndex);
        return SteamInstalledGameHelper.GetAppId(target?.FileName);
    }
}
