using AES_Code.Models;
using AES_Controls.Player.Models;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Services;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    public bool ShowArcadeRetroArchCoreMenuItem =>
        HasActiveAlbumItems &&
        IsArcadeRetroArchSectionAlbum(GetBrowseAlbum()) &&
        ResolveArcadeRetroArchCoresForContextMenu().Count > 0;

    public void PopulateArcadeRetroArchCoreContextMenu(MenuItem coreMenu)
    {
        coreMenu.Items.Clear();

        var target = ResolveArcadeRetroArchCoreContextMenuTarget();
        if (target == null || string.IsNullOrWhiteSpace(target.FileName))
        {
            coreMenu.Items.Add(new MenuItem { Header = "No ROM selected", IsEnabled = false });
            return;
        }

        var album = GetBrowseAlbum();
        var section = album == null ? null : TryResolveEmulationSection(album);
        var handler = album == null ? null : ResolveEmulatorHandlerForAlbum(album);
        var sectionDefaultCore = section == null || handler == null || !handler.UsesRetroArchCores
            ? null
            : section.GetSelectedRetroArchCoreForHandler(handler.HandlerId);
        var overrideCore = ArcadeRetroArchCoreMetadataHelper.GetCoreOverride(target.FileName);
        var cores = ResolveArcadeRetroArchCoresForContextMenu();

        if (!string.IsNullOrWhiteSpace(overrideCore) &&
            !cores.Any(core => string.Equals(core, overrideCore, StringComparison.OrdinalIgnoreCase)))
        {
            cores = cores.Append(overrideCore).OrderBy(core => core, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var defaultLabel = string.IsNullOrWhiteSpace(sectionDefaultCore)
            ? "Use Section Default"
            : $"Use Section Default ({sectionDefaultCore})";

        coreMenu.Items.Add(CreateArcadeRetroArchCoreMenuItem(
            defaultLabel,
            target.FileName,
            coreFileName: null,
            isSelected: string.IsNullOrWhiteSpace(overrideCore)));

        if (cores.Count == 0)
        {
            coreMenu.Items.Add(new MenuItem { Header = "No arcade cores found", IsEnabled = false });
            return;
        }

        coreMenu.Items.Add(new Separator());

        foreach (var core in cores)
        {
            coreMenu.Items.Add(CreateArcadeRetroArchCoreMenuItem(
                core,
                target.FileName,
                core,
                isSelected: string.Equals(overrideCore, core, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private EmulationSectionLaunchSettings? ResolveLaunchSettingsForRom(
        FolderMediaItem album,
        IEmulatorHandler handler,
        string romPath)
    {
        var section = TryResolveEmulationSection(album);
        var settings = section == null
            ? SettingsViewModel?.GetResolvedEmulationSectionLaunchSettings(album.Title ?? string.Empty)
            : SettingsViewModel?.GetResolvedEmulationSectionLaunchSettingsForLaunch(section, handler);

        if (settings == null || !handler.UsesRetroArchCores)
            return settings;

        var overrideCore = ArcadeRetroArchCoreMetadataHelper.GetCoreOverride(romPath);
        if (!string.IsNullOrWhiteSpace(overrideCore))
            settings.SelectedRetroArchCore = overrideCore;

        return settings;
    }

    private MediaItem? ResolveArcadeRetroArchCoreContextMenuTarget()
        => ResolveMetadataMenuTarget(PointedIndex >= 0 ? PointedIndex : null);

    private IReadOnlyList<string> ResolveArcadeRetroArchCoresForContextMenu()
    {
        var cores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = ResolveCurrentEmulationSection();

        if (section?.RetroArchCores is { Count: > 0 } sectionCores)
        {
            foreach (var core in sectionCores)
                cores.Add(core);
        }
        else
        {
            foreach (var handler in EmulatorHandlerRegistry.GetRegisteredHandlers().Where(item => item.UsesRetroArchCores))
            {
                var flatpakId = handler.ShouldLaunchViaFlatpak() ? handler.FlatpakAppId : null;
                foreach (var core in RetroArchHandler.GetRetroArchCores(handler.LauncherPath, flatpakId))
                    cores.Add(core);
            }
        }

        if (section != null)
        {
            foreach (var handlerItem in section.Handlers.Where(item => item.Handler.UsesRetroArchCores))
            {
                var selected = section.GetSelectedRetroArchCoreForHandler(handlerItem.HandlerId);
                if (!string.IsNullOrWhiteSpace(selected))
                    cores.Add(selected);
            }
        }

        return RetroArchHandler.FilterArcadeRetroArchCores(cores);
    }

    private bool IsArcadeRetroArchSectionAlbum(FolderMediaItem? album)
    {
        if (album == null)
            return false;

        var section = TryResolveEmulationSection(album);
        if (section != null)
        {
            return section.Handlers.Any(item => item.Handler.UsesRetroArchCores) &&
                   EmulationConsoleCatalog.IsArcadeStyleSection(section.SectionKey, section.SectionTitle);
        }

        return EmulationConsoleCatalog.IsArcadeStyleSection(null, album.Title);
    }

    private static MenuItem CreateArcadeRetroArchCoreMenuItem(
        string header,
        string romPath,
        string? coreFileName,
        bool isSelected)
    {
        var item = new MenuItem { Header = header };

        if (isSelected)
        {
            item.Icon = new TextBlock { Text = "✓" };
            item.FontWeight = FontWeight.Bold;
        }

        item.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(coreFileName))
                ArcadeRetroArchCoreMetadataHelper.ClearCoreOverride(romPath);
            else
                ArcadeRetroArchCoreMetadataHelper.SaveCoreOverride(romPath, coreFileName);
        };

        return item;
    }
}
