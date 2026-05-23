using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation.Controls;
using AES_Emulation.EmulationHandlers;
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
using DrawingIcon = System.Drawing.Icon;


namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {
        private FolderMediaItem? GetActiveEmulationAlbum() => LoadedAlbum ?? SelectedAlbum;

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

        private void SyncCurrentSectionEmulatorContext()
        {
            OnPropertyChanged(nameof(CurrentEmulationSectionItem));
            OnPropertyChanged(nameof(CurrentSectionEmulatorHandler));

            if (!IsEmulatorRunning)
                UpdateCurrentEmulatorHandlerForSelection(GetActiveEmulationAlbum());

            RefreshCurrentSectionLaunchOptionsState();
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
        }

        partial void OnSelectedIndexChanged(double value)
        {
            if (!_suppressSelectionStopForGameplayPreview &&
                !double.IsNaN(_lastSelectedIndexForPreview) &&
                Math.Abs(value - _lastSelectedIndexForPreview) > 0.0001)
            {
                StopGameplayPreview();
            }
            _lastSelectedIndexForPreview = value;

            if (Math.Abs(value - Math.Round(value)) > 0.001)
                return;

            int roundedIndex = GetRoundedSelectedIndex(value);
            if (roundedIndex >= 0 && roundedIndex < CoverItems.Count)
            {
                HighlightedItem = CoverItems[roundedIndex];
            }
        }

        partial void OnHighlightedItemChanged(MediaItem value)
        {
            QueueGameplayPreview(value);
        }

        [RelayCommand]
        private void ToggleAlbumList() => IsAlbumListCollapsed = !IsAlbumListCollapsed;

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

            var handlerId = CurrentSectionEmulatorHandler?.HandlerId;
            if (string.Equals(handlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                LaunchCurrentSectionDuckStationSetup();
                return;
            }

            if (string.Equals(handlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                LaunchCurrentSectionPcsx2Setup();
                return;
            }

            if (string.Equals(handlerId, DolphinHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                LaunchCurrentSectionDolphinSetup();
                return;
            }

            LaunchCurrentSectionGenericHandlerSetup();
        }

        [RelayCommand]
        private void LaunchCurrentSectionDolphinSetup()
        {
            var handler = CurrentSectionEmulatorHandler;
            if (handler == null ||
                !string.Equals(handler.HandlerId, DolphinHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                var executableDirectory = Path.GetDirectoryName(startInfo.FileName);
                var dolphinUserDirectory = string.IsNullOrWhiteSpace(executableDirectory)
                    ? startInfo.WorkingDirectory
                    : Path.Combine(executableDirectory, "User");

                if (!string.IsNullOrWhiteSpace(dolphinUserDirectory))
                {
                    startInfo.ArgumentList.Add("-u");
                    startInfo.ArgumentList.Add(dolphinUserDirectory);
                }

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to launch Dolphin.", ex);
            }
        }

        private Bitmap? ResolveCurrentSectionSetupLaunchIcon()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            var executablePath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(CurrentSectionEmulatorHandler?.LauncherPath);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return null;

            if (string.Equals(_currentSetupLaunchIconExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase) &&
                _currentSetupLaunchIcon != null)
            {
                return _currentSetupLaunchIcon;
            }

            _currentSetupLaunchIcon?.Dispose();
            _currentSetupLaunchIcon = TryLoadExecutableIcon(executablePath);
            _currentSetupLaunchIconExecutablePath = executablePath;
            return _currentSetupLaunchIcon;
        }

        private static Bitmap? TryLoadExecutableIcon(string executablePath)
        {
#pragma warning disable CA1416 // Windows-only System.Drawing APIs
            try
            {
                using var icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
                if (icon == null)
                    return null;

                using var drawingBitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                drawingBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
#pragma warning restore CA1416
        }

        private static bool IsCurrentSectionSetupLaunchSupported()
            => true;

        [RelayCommand]
        private void LaunchCurrentSectionGenericHandlerSetup()
        {
            var handler = CurrentSectionEmulatorHandler;
            if (handler == null)
                return;

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to launch {handler.DisplayName}.", ex);
            }
        }

        [RelayCommand]
        private void LaunchCurrentSectionPcsx2Setup()
        {
            var handler = CurrentSectionEmulatorHandler;
            if (handler == null ||
                !string.Equals(handler.HandlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                startInfo.ArgumentList.Add("-portable");

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to launch PCSX2.", ex);
            }
        }

        [RelayCommand]
        private void LaunchCurrentSectionDuckStationSetup()
        {
            var handler = CurrentSectionEmulatorHandler;
            if (handler == null ||
                !string.Equals(handler.HandlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                DuckStationHandler.EnsurePortableModeMarker(startInfo.FileName, startInfo.WorkingDirectory);

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to launch DuckStation.", ex);
            }
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
            EmulatorUpdateNoticeFooter = "Open Render Options ΓåÆ Updates to download and install the update.";
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
            if (ShowCurrentSectionDuckStationUpdateControls && IsCurrentSectionDuckStationUpdateAvailable)
                return (CurrentSectionDuckStationCurrentVersion, CurrentSectionDuckStationLatestVersion);
            if (ShowCurrentSectionCemuSection && IsCurrentSectionCemuUpdateAvailable)
                return (CurrentSectionCemuCurrentVersion, CurrentSectionCemuLatestVersion);

            return (null, null);
        }

        private void ClearRetroArchErrorState()
        {
            RetroArchErrorSummary = null;
            RetroArchErrorDetails = null;
            IsRetroArchErrorOverlayOpen = false;
        }

        private void ShowEmulatorCaptureFailure(string romPath, IEmulatorHandler handler, string? details = null)
        {
            var handlerName = string.IsNullOrWhiteSpace(handler.DisplayName) ? "emulator" : handler.DisplayName;
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
                RequestStopEmulatorCapture = true;
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            RequestStopEmulatorCapture = true;
            EmulatorTargetHwnd = IntPtr.Zero;
            IsEmulatorRunning = false;
            UpdateCurrentEmulatorHandlerForSelection(GetActiveEmulationAlbum());
            DetachTrackedEmulatorProcess();
        }

        public void ShutdownForApplicationExit()
        {
            SLog.Info("EmulationViewModel.ShutdownForApplicationExit started.");
            _pendingEmulatorLaunchRequest = null;
            IsRenderOptionsOpen = false;
            ClearRetroArchErrorState();
            RequestStopEmulatorCapture = true;

            if (EmulatorTargetHwnd != IntPtr.Zero)
            {
                SLog.Info($"EmulationViewModel clearing emulator hwnd 0x{EmulatorTargetHwnd.ToInt64():X} for application shutdown.");
                EmulatorTargetHwnd = IntPtr.Zero;
            }

            if (!TryGetRunningTrackedEmulatorProcess(out var process))
            {
                IsEmulatorRunning = false;
                CurrentEmulatorHandler = null;
                ClearSessionCaptureStretchOverride();
                DetachTrackedEmulatorProcess();
                return;
            }

            try
            {
                if (string.Equals(CurrentEmulatorHandler?.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase))
                {
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
                    catch
                    {
                    }
                }

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
                CurrentEmulatorHandler = null;
                ClearSessionCaptureStretchOverride();
                DetachTrackedEmulatorProcess();
                SLog.Info("EmulationViewModel.ShutdownForApplicationExit finished.");
            }
        }
    }
}
