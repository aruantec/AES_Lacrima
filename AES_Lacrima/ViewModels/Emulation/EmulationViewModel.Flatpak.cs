using AES_Emulation.EmulationHandlers;
using AES_Emulation.Linux;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    private FlatpakApplicationItem? _selectedCurrentSectionFlatpakApplication;
    private int _currentSectionFlatpakListRefreshDepth;

    public bool ShowCurrentSectionFlatpakSelection =>
        OperatingSystem.IsLinux() && FlatpakLaunchHelper.IsFlatpakAvailable();

    public AvaloniaList<FlatpakApplicationItem> CurrentSectionFlatpakApplications { get; } = [];

    public FlatpakApplicationItem? SelectedCurrentSectionFlatpakApplication
    {
        get => _selectedCurrentSectionFlatpakApplication ??= ResolveCurrentSectionFlatpakApplication();
        set
        {
            var handler = CurrentSectionEmulatorHandler;
            if (handler == null)
                return;

            // ComboBox list rebuilds push null through TwoWay binding; ignore those resets.
            if (value == null && !string.IsNullOrWhiteSpace(handler.FlatpakAppId))
                return;

            if (_currentSectionFlatpakListRefreshDepth > 0 || _isSyncingCurrentSectionFlatpakSelection)
            {
                _selectedCurrentSectionFlatpakApplication = value ?? FlatpakApplicationItem.Empty;
                OnPropertyChanged();
                return;
            }

            var normalized = value ?? FlatpakApplicationItem.Empty;
            if (!normalized.IsEmpty)
            {
                normalized = CurrentSectionFlatpakApplications.FirstOrDefault(app =>
                                 string.Equals(app.ApplicationId, normalized.ApplicationId, StringComparison.OrdinalIgnoreCase))
                             ?? normalized;
            }

            var appId = normalized.IsEmpty ? null : normalized.ApplicationId;
            if (string.Equals(handler.FlatpakAppId, appId, StringComparison.Ordinal) &&
                _selectedCurrentSectionFlatpakApplication is not null &&
                Equals(_selectedCurrentSectionFlatpakApplication, normalized))
            {
                return;
            }

            _selectedCurrentSectionFlatpakApplication = normalized;
            handler.FlatpakAppId = appId;
            SettingsViewModel?.SaveSettings();
            if (handler.UsesRetroArchCores)
            {
                SettingsViewModel?.RefreshRetroArchCores();
                OnPropertyChanged(nameof(CurrentSectionRetroArchCores));
                OnPropertyChanged(nameof(ShowCurrentSectionRetroArchCoreSelection));
                SyncCurrentSectionRetroArchCoreSelection();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentSectionEmulatorHandler));
            RefreshCurrentSectionSetupLaunchIcon();
        }
    }

    [RelayCommand]
    private void RefreshCurrentSectionFlatpakApplications()
        => RefreshCurrentSectionFlatpakApplications(forceRefresh: true);

    internal void RefreshCurrentSectionFlatpakApplications(bool forceRefresh = false)
    {
        if (!OperatingSystem.IsLinux())
            return;

        _currentSectionFlatpakListRefreshDepth++;
        try
        {
            _isSyncingCurrentSectionFlatpakSelection = true;

            if (forceRefresh)
                LinuxFlatpakApplicationService.InvalidateCache();

            CurrentSectionFlatpakApplications.Clear();
            if (ShowCurrentSectionFlatpakSelection && CurrentSectionEmulatorHandler != null)
            {
                foreach (var app in LinuxFlatpakApplicationService.GetApplicationsForHandler(
                             CurrentSectionEmulatorHandler.HandlerId,
                             forceRefresh))
                {
                    CurrentSectionFlatpakApplications.Add(app);
                }
            }

            OnPropertyChanged(nameof(CurrentSectionFlatpakApplications));
            OnPropertyChanged(nameof(ShowCurrentSectionFlatpakSelection));
            ApplyCurrentSectionFlatpakSelection();
            _ = PopulateCurrentSectionFlatpakIconsAfterRefreshAsync();
        }
        finally
        {
            _isSyncingCurrentSectionFlatpakSelection = false;
            _currentSectionFlatpakListRefreshDepth--;
            Dispatcher.UIThread.Post(ReleaseCurrentSectionFlatpakSelectionGuards, DispatcherPriority.Loaded);
        }
    }

    private async Task PopulateCurrentSectionFlatpakIconsAfterRefreshAsync()
    {
        if (!OperatingSystem.IsLinux())
            return;

        await LinuxFlatpakApplicationService.PopulateIconsAsync(CurrentSectionFlatpakApplications).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(CurrentSectionFlatpakApplications));
            ApplyCurrentSectionFlatpakSelection();
            RefreshCurrentSectionSetupLaunchIcon();
        });
    }

    internal void SyncCurrentSectionFlatpakSelection()
    {
        ApplyCurrentSectionFlatpakSelection();
    }

    internal void ApplyCurrentSectionFlatpakSelection()
    {
        var resolved = ResolveCurrentSectionFlatpakApplication();
        if (_selectedCurrentSectionFlatpakApplication is not null &&
            Equals(_selectedCurrentSectionFlatpakApplication, resolved))
        {
            return;
        }

        try
        {
            _isSyncingCurrentSectionFlatpakSelection = true;
            _selectedCurrentSectionFlatpakApplication = resolved;
            OnPropertyChanged(nameof(SelectedCurrentSectionFlatpakApplication));
        }
        finally
        {
            _isSyncingCurrentSectionFlatpakSelection = false;
        }
    }

    private void ReleaseCurrentSectionFlatpakSelectionGuards()
    {
        _isSyncingCurrentSectionFlatpakSelection = false;
        if (_currentSectionFlatpakListRefreshDepth < 0)
            _currentSectionFlatpakListRefreshDepth = 0;
    }

    private FlatpakApplicationItem ResolveCurrentSectionFlatpakApplication()
    {
        var handler = CurrentSectionEmulatorHandler;
        if (handler == null || string.IsNullOrWhiteSpace(handler.FlatpakAppId))
            return FlatpakApplicationItem.Empty;

        return CurrentSectionFlatpakApplications.FirstOrDefault(app =>
                   string.Equals(app.ApplicationId, handler.FlatpakAppId, StringComparison.OrdinalIgnoreCase))
               ?? new FlatpakApplicationItem(handler.FlatpakAppId, handler.FlatpakAppId);
    }
}
