using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation.Controls;
using AES_Emulation;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Linux;
using AES_Emulation.Platform;
using AES_Emulation.Windows.API;
using AES_Lacrima.Mac.API;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using AES_Lacrima.Services.Cemu;
using AES_Lacrima.Services.Rpcs3;
using AES_Lacrima.Services.ShadPs4;
using AES_Lacrima.Services.Xenia;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AES_Core.Logging;


namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {
        private FolderMediaItem? GetActiveEmulationAlbum() => GetBrowseAlbum();

        public bool ShowAlbumRomImportMenuItems =>
            GetActiveEmulationAlbum() is { } album && !IsSteamAlbum(album);

        /// <summary>
        /// Album used for section-scoped UI (render options handler tab, per-section settings).
        /// Only an opened album drives handler context; row selection alone does not.
        /// </summary>
        private FolderMediaItem? GetActiveEmulationSectionAlbum() => LoadedAlbum;

        private EmulationSectionItem? TryResolveEmulationSection(FolderMediaItem? album)
        {
            if (album == null || SettingsViewModel == null)
                return null;

            var sectionKey = GetAlbumPersistenceKey(album);
            if (!string.IsNullOrWhiteSpace(sectionKey))
            {
                var byKey = SettingsViewModel.FindEmulationSection(sectionKey);
                if (byKey != null)
                    return byKey;
            }

            return SettingsViewModel.FindEmulationSection(album.Title);
        }

        private IEmulatorHandler? ResolveEmulatorHandlerForAlbum(FolderMediaItem album)
        {
            EnsureSettingsViewModelSubscription();

            if (TryResolveEmulationSection(album) is { } section)
                return SettingsViewModel?.GetConfiguredEmulatorHandlerForSection(section);

            return SettingsViewModel?.GetConfiguredEmulatorHandler(album.Title);
        }

        private EmulationSectionLaunchSettings? ResolveEmulationLaunchSettingsForAlbum(FolderMediaItem album)
        {
            EnsureSettingsViewModelSubscription();

            var sectionKey = TryResolveEmulationSection(album)?.SectionKey;
            if (!string.IsNullOrWhiteSpace(sectionKey))
                return SettingsViewModel?.GetResolvedEmulationSectionLaunchSettings(sectionKey);

            return SettingsViewModel?.GetResolvedEmulationSectionLaunchSettings(album.Title);
        }

        private void SyncCurrentSectionEmulatorContext()
        {
            OnPropertyChanged(nameof(CurrentEmulationSectionItem));
            OnPropertyChanged(nameof(CurrentSectionEmulatorHandler));
            OnPropertyChanged(nameof(CaptureWindowAspectRatio));
            OnPropertyChanged(nameof(CanShowRenderOptions));

            if (!IsEmulatorRunning)
                UpdateCurrentEmulatorHandlerForSelection(GetActiveEmulationSectionAlbum());

            RefreshCurrentSectionLaunchOptionsState();
            SyncCurrentSectionRetroArchCoreSelection();
            TryDiscoverInstalledRetroArchLauncher();

            if (!IsEmulatorRunning && !IsGameplayRecording)
                RefreshCurrentSectionFlatpakApplications();
        }

        private void UpdateCurrentEmulatorHandlerForSelection(FolderMediaItem? album)
        {
            if (album == null)
            {
                CurrentEmulatorHandler = null;
                return;
            }

            var configuredHandler = TryResolveEmulationSection(album) is { } section
                ? SettingsViewModel?.GetConfiguredEmulatorHandlerForSection(section)
                : null;

            CurrentEmulatorHandler = configuredHandler;
        }

        partial void OnSelectedAlbumIndexChanged(int value)
        {
            if (_isSyncingAlbumSelection)
                return;

            var nextAlbum =
                value >= 0 && value < AlbumList.Count
                    ? AlbumList[value]
                    : null;

            if (!ReferenceEquals(SelectedAlbum, nextAlbum))
                SelectedAlbum = nextAlbum;

            ScheduleAlbumRowCoverLoadsForIndex(value);
        }

        private void ScheduleAlbumRowCoverLoadsForIndex(int index)
        {
            _pendingAlbumRowCoverLoadIndex = index;

            _albumRowCoverLoadDebounceTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AlbumRowCoverLoadDebounceMs)
            };

            _albumRowCoverLoadDebounceTimer.Stop();
            _albumRowCoverLoadDebounceTimer.Tick -= OnAlbumRowCoverLoadDebounceTick;
            _albumRowCoverLoadDebounceTimer.Tick += OnAlbumRowCoverLoadDebounceTick;
            _albumRowCoverLoadDebounceTimer.Start();
        }

        private void OnAlbumRowCoverLoadDebounceTick(object? sender, EventArgs e)
        {
            _albumRowCoverLoadDebounceTimer?.Stop();

            var index = _pendingAlbumRowCoverLoadIndex;
            if (index < 0 || index >= AlbumList.Count)
                return;

            if (AlbumList[index] is EmulationAlbumItem emulationAlbum && emulationAlbum.Children.Count > 0)
                QueueAlbumPreviewCoverLoad(emulationAlbum);

            QueueAlbumPresentationCoverLoadsNearIndex(index);
        }

        partial void OnCarouselSliderPreviewChanged(double? value)
            => NotifyCarouselOverlayItemChanged();

        partial void OnSelectedIndexChanged(double value)
        {
            NotifyCarouselOverlayItemChanged();
            OnPropertyChanged(nameof(ShowSteamProtonVersionMenuItem));

            if (Math.Abs(value - Math.Round(value)) > 0.001)
                return;

            int roundedIndex = GetRoundedSelectedIndex(value);
            if (roundedIndex < 0 || roundedIndex >= CoverItems.Count)
                return;

            if (!_suppressSelectionStopForGameplayPreview &&
                roundedIndex != _lastRoundedSelectedIndexForPreview)
            {
                StopGameplayPreview();
            }

            _lastRoundedSelectedIndexForPreview = roundedIndex;
            _lastSelectedIndexForPreview = value;
            ScheduleHighlightedItemUpdate(roundedIndex);
        }

        partial void OnHighlightedItemChanged(MediaItem value)
        {
            if (!IsAlbumListCollapsed)
                return;

            QueueGameplayPreview(value);
        }

        private void ScheduleHighlightedItemUpdate(int roundedIndex)
        {
            _pendingHighlightedCarouselIndex = roundedIndex;

            _carouselHighlightDebounceTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(140)
            };

            _carouselHighlightDebounceTimer.Tick -= OnCarouselHighlightDebounceTick;
            _carouselHighlightDebounceTimer.Tick += OnCarouselHighlightDebounceTick;
            _carouselHighlightDebounceTimer.Stop();
            _carouselHighlightDebounceTimer.Start();
        }

        private void OnCarouselHighlightDebounceTick(object? sender, EventArgs e)
        {
            _carouselHighlightDebounceTimer?.Stop();

            int index = _pendingHighlightedCarouselIndex;
            if (index < 0 || index >= CoverItems.Count)
                return;

            var item = CoverItems[index];
            if (!ReferenceEquals(HighlightedItem, item))
                HighlightedItem = item;
        }

        [RelayCommand]
        private void ToggleAlbumList() => IsAlbumListCollapsed = !IsAlbumListCollapsed;

        [RelayCommand]
        private void SetCarouselIndex(double index)
        {
            CarouselSliderPreview = null;
            SelectedIndex = index;
        }

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        [RelayCommand]
        private void ToggleEmulatorViewport()
        {
            if (!IsEmulatorRunning)
                return;

            IsEmulatorViewportDismissed = !IsEmulatorViewportDismissed;
        }

        [RelayCommand]
        private void ToggleRenderOptions()
        {
            IsRenderOptionsOpen = !IsRenderOptionsOpen;
            NotifyCaptureChromeMarginChanged();

            if (IsRenderOptionsOpen && !IsEmulatorRunning && !IsGameplayRecording)
                RefreshCurrentSectionFlatpakApplications();
        }

        private async Task OpenCurrentSectionEdenUpdates()
        {
            IsRenderOptionsOpen = true;
            RenderOptionsSelectedTabIndex = 1;
            if (ShowCurrentSectionEdenUpdateControls)
                await RefreshCurrentSectionEdenInfo();
            else if (ShowCurrentSectionShadPs4UpdateControls)
                await RefreshCurrentSectionShadPs4Info();
            else if (ShowCurrentSectionRpcs3UpdateControls)
                await RefreshCurrentSectionRpcs3Info();
            else if (ShowCurrentSectionDolphinUpdateControls)
                await RefreshCurrentSectionDolphinInfo();
            else if (ShowCurrentSectionPcsx2UpdateControls)
                await RefreshCurrentSectionPcsx2Info();
        }

        [RelayCommand]
        private void LaunchCurrentSectionHandlerSetup()
        {
            if (!CanLaunchCurrentSectionHandlerSetup)
                return;

            var handler = CurrentSectionEmulatorHandler;
            if (handler == null)
                return;

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = handler.BuildSetupStartInfo(
                    handler.LauncherPath,
                    ResolveCurrentSectionPreferredEmulatorDirectory(handler));

                if (OperatingSystem.IsLinux())
                {
                    if (string.Equals(handler.HandlerId, XeniaHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                    {
                        XeniaPathsService.ApplyLinuxStorageRootLaunchArguments(
                            startInfo,
                            CurrentSectionXeniaEmulatorPath,
                            handler.LauncherPath);
                    }

                    if (string.IsNullOrWhiteSpace(handler.FlatpakAppId))
                        LinuxAppImageLaunchHelper.PrepareDirectExtractAndRunLaunch(startInfo);
                    else
                        FlatpakLaunchHelper.Apply(startInfo, handler.FlatpakAppId);
                }

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to launch {handler.DisplayName} setup.", ex);
            }
        }

        private string? ResolveCurrentSectionPreferredEmulatorDirectory(IEmulatorHandler handler)
        {
            if (string.Equals(handler.HandlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionPcsx2EmulatorPath;
            if (string.Equals(handler.HandlerId, XeniaHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionXeniaEmulatorPath;
            if (string.Equals(handler.HandlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionDuckStationEmulatorPath;
            if (string.Equals(handler.HandlerId, DolphinHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionDolphinEmulatorPath;
            if (string.Equals(handler.HandlerId, CemuHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionCemuEmulatorPath;
            if (string.Equals(handler.HandlerId, Rpcs3Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionRpcs3EmulatorPath;
            if (string.Equals(handler.HandlerId, ShadPs4Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionShadPs4EmulatorPath;
            if (string.Equals(handler.HandlerId, FlyCastHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionFlycastEmulatorPath;
            if (string.Equals(handler.HandlerId, XemuHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionXemuEmulatorPath;
            if (string.Equals(handler.HandlerId, EdenHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
                return CurrentSectionEdenEmulatorPath;
            if (handler.UsesRetroArchCores)
                return CurrentSectionRetroArchEmulatorPath;

            return null;
        }

        private void RefreshCurrentSectionSetupLaunchIcon()
        {
            if (IsEmulatorRunning || IsGameplayRecording)
                return;

            var handler = CurrentSectionEmulatorHandler;
            if (handler == null)
            {
                ReplaceSetupLaunchIcon(null, null);
                return;
            }

            if (!string.IsNullOrWhiteSpace(handler.FlatpakAppId) &&
                EmulatorFlatpakCatalog.IsCompatibleApplicationId(handler.HandlerId, handler.FlatpakAppId))
            {
                var flatpakKey = $"flatpak:{handler.FlatpakAppId}";
                if (string.Equals(_currentSetupLaunchIconExecutablePath, flatpakKey, StringComparison.OrdinalIgnoreCase) &&
                    _currentSetupLaunchIcon != null)
                {
                    return;
                }

                var flatpakIcon = EmulatorSetupLaunchIconService.TryLoadFlatpakSetupLaunchIcon(handler.FlatpakAppId);
                ReplaceSetupLaunchIcon(flatpakIcon, flatpakKey);
                return;
            }

            var launcherPath = handler.NormalizeLauncherPath(handler.LauncherPath) ?? handler.LauncherPath;
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                ReplaceSetupLaunchIcon(null, null);
                return;
            }

            var executablePath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(launcherPath);
            if (string.IsNullOrWhiteSpace(executablePath) ||
                (!File.Exists(executablePath) && !Directory.Exists(executablePath)))
            {
                ReplaceSetupLaunchIcon(null, null);
                return;
            }

            if (string.Equals(_currentSetupLaunchIconExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase) &&
                _currentSetupLaunchIcon != null)
            {
                return;
            }

            var newIcon = EmulatorSetupLaunchIconService.TryLoadSetupLaunchIcon(launcherPath);
            ReplaceSetupLaunchIcon(newIcon, executablePath);
        }

        private void ReplaceSetupLaunchIcon(Bitmap? newIcon, string? executablePath)
        {
            var oldIcon = _currentSetupLaunchIcon;
            _currentSetupLaunchIcon = newIcon;
            _currentSetupLaunchIconExecutablePath = executablePath;
            OnPropertyChanged(nameof(CurrentSectionSetupLaunchIcon));
            OnPropertyChanged(nameof(HasCurrentSectionSetupLaunchIcon));
            oldIcon?.Dispose();
        }

        [RelayCommand]
        private void ToggleFullscreen()
        {
            if (!IsEmulatorRunning)
                return;

            IsFullscreen = !IsFullscreen;
        }

        [RelayCommand]
        private void ToggleRetroArchErrorOverlay()
        {
            if (!HasRetroArchError)
                return;

            IsRetroArchErrorOverlayOpen = !IsRetroArchErrorOverlayOpen;
        }

        [RelayCommand]
        private void DismissEmulatorUpdateNoticeOverlay()
        {
            _emulatorUpdateNoticeSuppressedAlbumTitle = LoadedAlbum?.Title;
            IsEmulatorUpdateNoticeOverlayOpen = false;
        }

        [RelayCommand]
        private async Task OpenEmulatorUpdateNoticeOverlay()
        {
            _emulatorUpdateNoticeSuppressedAlbumTitle = LoadedAlbum?.Title;
            IsEmulatorUpdateNoticeOverlayOpen = false;
            await OpenCurrentSectionEdenUpdates();
        }

        private void SyncEmulatorUpdateNoticeOverlay()
        {
            if (LoadedAlbum == null || !IsCurrentSectionHandlerUpdateAvailable)
                return;

            if (string.Equals(_emulatorUpdateNoticeSuppressedAlbumTitle, LoadedAlbum.Title, StringComparison.OrdinalIgnoreCase))
                return;

            var (currentVersion, latestVersion) = GetCurrentSectionUpdateVersionInfo();
            var emulatorName = string.IsNullOrWhiteSpace(CurrentEmulatorHandler?.DisplayName)
                ? "emulator"
                : CurrentEmulatorHandler.DisplayName;

            EmulatorUpdateNoticeSummary = $"A new version of {emulatorName} is available.";

            var details = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(currentVersion))
                details.Append("Installed: ").AppendLine(currentVersion);
            if (!string.IsNullOrWhiteSpace(latestVersion))
                details.Append("Latest: ").AppendLine(latestVersion);

            EmulatorUpdateNoticeDetails = details.Length > 0 ? details.ToString().TrimEnd() : null;
            EmulatorUpdateNoticeChanges = _sectionLatestReleaseNotes;
            EmulatorUpdateNoticeFooter = "Open Settings → Handler to download and install the update.";
            IsEmulatorUpdateNoticeOverlayOpen = true;
        }

        private (string? CurrentVersion, string? LatestVersion) GetCurrentSectionUpdateVersionInfo()
        {
            if (ShowCurrentSectionRetroArchUpdateControls && IsCurrentSectionRetroArchUpdateAvailable)
                return (CurrentSectionRetroArchCurrentVersion, CurrentSectionRetroArchLatestVersion);
            if (ShowCurrentSectionEdenUpdateControls && IsCurrentSectionEdenUpdateAvailable)
                return (CurrentSectionEdenCurrentVersion, CurrentSectionEdenLatestVersion);
            if (ShowCurrentSectionShadPs4UpdateControls && IsCurrentSectionShadPs4UpdateAvailable)
                return (CurrentSectionShadPs4CurrentVersion, CurrentSectionShadPs4LatestVersion);
            if (ShowCurrentSectionXeniaUpdateControls && IsCurrentSectionXeniaUpdateAvailable)
                return (CurrentSectionXeniaCurrentVersion, CurrentSectionXeniaLatestVersion);
            if (ShowCurrentSectionRpcs3UpdateControls && IsCurrentSectionRpcs3UpdateAvailable)
                return (CurrentSectionRpcs3CurrentVersion, CurrentSectionRpcs3LatestVersion);
            if (ShowCurrentSectionPcsx2UpdateControls && IsCurrentSectionPcsx2UpdateAvailable)
                return (CurrentSectionPcsx2CurrentVersion, CurrentSectionPcsx2LatestVersion);
            if (ShowCurrentSectionDolphinUpdateControls && IsCurrentSectionDolphinUpdateAvailable)
                return (CurrentSectionDolphinCurrentVersion, CurrentSectionDolphinLatestVersion);
            if (ShowCurrentSectionFlycastUpdateControls && IsCurrentSectionFlycastUpdateAvailable)
                return (CurrentSectionFlycastCurrentVersion, CurrentSectionFlycastLatestVersion);
            if (ShowCurrentSectionXemuUpdateControls && IsCurrentSectionXemuUpdateAvailable)
                return (CurrentSectionXemuCurrentVersion, CurrentSectionXemuLatestVersion);
            if (ShowCurrentSectionDuckStationUpdateControls && IsCurrentSectionDuckStationUpdateAvailable)
                return (CurrentSectionDuckStationCurrentVersion, CurrentSectionDuckStationLatestVersion);
            if (ShowCurrentSectionCemuSection && IsCurrentSectionCemuUpdateAvailable)
                return (CurrentSectionCemuCurrentVersion, CurrentSectionCemuLatestVersion);

            return (null, null);
        }

        private void ClearRetroArchErrorState()
        {
            EmulatorErrorOverlayTitle = "Emulator launch warning";
            RetroArchErrorSummary = null;
            RetroArchErrorDetails = null;
            IsRetroArchErrorOverlayOpen = false;
        }

        private void ShowEmulatorLaunchFailure(IEmulatorHandler? handler, string? context, string details)
        {
            var handlerName = !string.IsNullOrWhiteSpace(handler?.DisplayName)
                ? handler.DisplayName
                : "Emulator";
            EmulatorErrorOverlayTitle = $"{handlerName} launch warning";
            RetroArchErrorSummary = string.IsNullOrWhiteSpace(context)
                ? "Emulator launch failed."
                : $"Could not launch {context}.";
            RetroArchErrorDetails = details;
            IsRetroArchErrorOverlayOpen = true;
        }

        private void ShowEmulatorCaptureFailure(string romPath, IEmulatorHandler handler, string? details = null)
        {
            var handlerName = string.IsNullOrWhiteSpace(handler.DisplayName) ? "emulator" : handler.DisplayName;
            EmulatorErrorOverlayTitle = $"{handlerName} capture warning";
            RetroArchErrorSummary = $"{handlerName} capture failed.";
            RetroArchErrorDetails = string.IsNullOrWhiteSpace(details)
                ? $"AES could not capture '{romPath}'. The emulator may still be running. Please retry, or reopen the emulator window and try again."
                : details;
            IsRetroArchErrorOverlayOpen = true;
        }

        [RelayCommand]
        private void CloseEmulator()
        {
            SLog.Info("EmulationViewModel.CloseEmulator requested by the user.");
            _pendingEmulatorLaunchRequest = null;
            IsRenderOptionsOpen = false;
            ClearRetroArchErrorState();

            if (TryGetRunningTrackedEmulatorProcess(out var process))
            {
                PrepareEmulatorShutdownCapture();
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            PrepareEmulatorShutdownCapture();
            if (OperatingSystem.IsLinux())
                TeardownLinuxGamescopeSession();
            EmulatorTargetHwnd = IntPtr.Zero;
            EmulatorTargetProcessId = 0;
            IsEmulatorRunning = false;
            IsEmulatorPaused = false;
            UpdateCurrentEmulatorHandlerForSelection(GetActiveEmulationSectionAlbum());
            DetachTrackedEmulatorProcess();
            ResetEmulatorShutdownCaptureState();
        }

        [RelayCommand(CanExecute = nameof(IsEmulatorRunning))]
        private void ToggleEmulatorPause()
        {
            if (!TryGetRunningTrackedEmulatorProcess(out var process))
                return;

            if (IsEmulatorPaused)
            {
                if (!ResumeEmulatorExecution(process))
                    return;

                IsEmulatorPaused = false;
                SLog.Info($"EmulationViewModel: Resumed emulator process PID {process.Id}.");
            }
            else
            {
                if (!SuspendEmulatorExecution(process))
                    return;

                IsEmulatorPaused = true;
                SLog.Info($"EmulationViewModel: Suspended emulator process PID {process.Id}.");
            }
        }

        public void ShutdownForApplicationExit()
        {
            SLog.Info("EmulationViewModel.ShutdownForApplicationExit started.");
            _pendingEmulatorLaunchRequest = null;
            IsRenderOptionsOpen = false;
            ClearRetroArchErrorState();

            if (OperatingSystem.IsLinux())
            {
                LinuxEmulationLifecycle.IsApplicationExitInProgress = true;
                TeardownLinuxGamescopeSession(scheduleKill: true);
                RequestStopEmulatorCapture = true;
                EmulatorTargetProcessId = 0;
                EmulatorTargetHwnd = IntPtr.Zero;
                IsEmulatorRunning = false;
                IsEmulatorPaused = false;
                CurrentEmulatorHandler = null;
                DetachTrackedEmulatorProcess();
                ResetEmulatorShutdownCaptureState();
                SLog.Info("EmulationViewModel.ShutdownForApplicationExit finished (Linux fast path).");
                return;
            }

            PrepareEmulatorShutdownCapture();

            var shutdownHwnd = EmulatorTargetHwnd;
            if (shutdownHwnd != IntPtr.Zero)
            {
                SLog.Info($"EmulationViewModel clearing emulator hwnd 0x{shutdownHwnd.ToInt64():X} for application shutdown.");
                EmulatorTargetHwnd = IntPtr.Zero;
            }

            if (!TryGetRunningTrackedEmulatorProcess(out var process))
            {
                IsEmulatorRunning = false;
                IsEmulatorPaused = false;
                CurrentEmulatorHandler = null;
                DetachTrackedEmulatorProcess();
                ResetEmulatorShutdownCaptureState();
                return;
            }

            try
            {
                if (string.Equals(CurrentEmulatorHandler?.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase))
                {
                    TryKeepEmulatorHiddenForShutdown(shutdownHwnd, process);
                    TryRequestRpcs3Shutdown(process);
                    return;
                }

                var forceKillFirst = string.Equals(CurrentEmulatorHandler?.HandlerId, "pcsx2", StringComparison.OrdinalIgnoreCase);
                forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "dolphin", StringComparison.OrdinalIgnoreCase);

                if (!forceKillFirst)
                {
                    try
                    {
                        forceKillFirst = process.ProcessName.Contains("pcsx2", StringComparison.OrdinalIgnoreCase) ||
                                         process.ProcessName.Contains("dolphin", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception logEx) { SLog.Warn("Exception caught", logEx); }
                }

                TryKeepEmulatorHiddenForShutdown(shutdownHwnd, process);

                if (forceKillFirst)
                {
                    SLog.Info($"EmulationViewModel force-terminating emulator pid={process.Id} during application shutdown.");
                    process.Kill(true);
                }
                else
                {
                    var closeMainWindowResult = process.CloseMainWindow();
                    SLog.Info($"EmulationViewModel CloseMainWindow returned {closeMainWindowResult} for pid={process.Id} during application shutdown.");
                    if (!closeMainWindowResult)
                    {
                        process.Kill(true);
                    }
                    else if (!process.WaitForExit(3000))
                    {
                        SLog.Info($"EmulationViewModel force-closing emulator pid={process.Id} after graceful shutdown timed out during application shutdown.");
                        process.Kill(true);
                    }
                }

                if (!process.HasExited)
                {
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to stop tracked emulator cleanly during application shutdown.", ex);

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception killEx)
                {
                    SLog.Debug("Failed to force-close emulator during application shutdown.", killEx);
                }
            }
            finally
            {
                IsEmulatorRunning = false;
                IsEmulatorPaused = false;
                CurrentEmulatorHandler = null;
                DetachTrackedEmulatorProcess();
                ResetEmulatorShutdownCaptureState();
                SLog.Info("EmulationViewModel.ShutdownForApplicationExit finished.");
            }
        }
    }
}
