using AES_Lacrima.Services.Steam;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels;

public sealed partial class SettingsViewModel
{
    private const string SteamGameProtonOverridesSettingName = "SteamGameProtonOverrides";

    private Dictionary<string, string> _steamGameProtonOverrides = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string? _steamDefaultProtonDirectory;

    [ObservableProperty]
    private AvaloniaList<SteamProtonVersionItem> _steamProtonVersionItems = [];

    [ObservableProperty]
    private SteamProtonVersionItem? _selectedSteamDefaultProtonItem;

    public bool IsSteamProtonSettingsVisible => OperatingSystem.IsLinux();

    partial void OnSelectedSteamDefaultProtonItemChanged(SteamProtonVersionItem? value)
    {
        var nextDirectory = value?.DirectoryPath;
        if (string.Equals(SteamDefaultProtonDirectory, nextDirectory, StringComparison.Ordinal))
            return;

        SteamDefaultProtonDirectory = nextDirectory;
        SaveSettings();
    }

    public SteamProtonLaunchPreferences BuildSteamProtonLaunchPreferences()
        => new(SteamDefaultProtonDirectory, _steamGameProtonOverrides);

    public void RefreshSteamProtonCatalog()
    {
        if (!OperatingSystem.IsLinux())
        {
            SteamProtonVersionItems = [];
            SelectedSteamDefaultProtonItem = null;
            return;
        }

        var installedVersions = SteamProtonCatalogHelper.GetInstalledProtonVersions();
        var items = new AvaloniaList<SteamProtonVersionItem>(
            new[] { SteamProtonCatalogHelper.AutomaticOption }.Concat(installedVersions));

        SteamProtonVersionItems = items;
        SelectedSteamDefaultProtonItem =
            FindSteamDefaultProtonSelection(items, SteamDefaultProtonDirectory) ?? SteamProtonCatalogHelper.AutomaticOption;
    }

    public void SetSteamGameProtonOverride(string appId, string? protonDirectory)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return;

        var normalized = SteamProtonCatalogHelper.NormalizeProtonDirectory(protonDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
            _steamGameProtonOverrides.Remove(appId);
        else
            _steamGameProtonOverrides[appId] = normalized;

        SaveSettings();
    }

    public string? GetSteamGameProtonOverride(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        return _steamGameProtonOverrides.TryGetValue(appId, out var directory)
            ? directory
            : null;
    }

    public string? ResolveEffectiveSteamProtonDirectory(string appId)
    {
        var game = SteamInstalledGameHelper.GetInstalledGame(appId);
        if (game == null)
            return null;

        return SteamInstalledGameHelper.ResolveProtonDirectoryForGame(game, BuildSteamProtonLaunchPreferences());
    }

    private void LoadSteamProtonSettings(JsonObject section)
    {
        SteamDefaultProtonDirectory = ReadStringSetting(section, nameof(SteamDefaultProtonDirectory));
        _steamGameProtonOverrides = ReadObjectSetting<Dictionary<string, string>>(section, SteamGameProtonOverridesSettingName)
            ?.Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => SteamProtonCatalogHelper.NormalizeProtonDirectory(pair.Value) ?? string.Empty,
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        _steamGameProtonOverrides = _steamGameProtonOverrides
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        RefreshSteamProtonCatalog();
    }

    private void SaveSteamProtonSettings(JsonObject section)
    {
        WriteSetting(section, nameof(SteamDefaultProtonDirectory), SteamDefaultProtonDirectory ?? string.Empty);
        WriteObjectSetting(
            section,
            SteamGameProtonOverridesSettingName,
            _steamGameProtonOverrides
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private static SteamProtonVersionItem? FindSteamDefaultProtonSelection(
        IReadOnlyList<SteamProtonVersionItem> items,
        string? defaultProtonDirectory)
    {
        if (string.IsNullOrWhiteSpace(defaultProtonDirectory))
            return items.FirstOrDefault(item => item.DirectoryPath == null);

        var normalized = SteamProtonCatalogHelper.NormalizeProtonDirectory(defaultProtonDirectory);
        return items.FirstOrDefault(item =>
            item.DirectoryPath != null &&
            string.Equals(item.DirectoryPath, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
