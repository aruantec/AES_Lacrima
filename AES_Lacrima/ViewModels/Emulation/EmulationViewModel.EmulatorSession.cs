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
using AES_Core.Logging;
using DrawingIcon = System.Drawing.Icon;


namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {
        private static bool Matches(MediaItem item, string query)
        {
            return
                item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        }

        private void RequestEmulatorLaunch(PendingEmulatorLaunchRequest request)
        {
            _pendingEmulatorLaunchRequest = request;
            RequestStopEmulatorCapture = true;
            EmulatorTargetHwnd = IntPtr.Zero;
            IsEmulatorLaunchInProgress = true;

            if (TryGetRunningTrackedEmulatorProcess(out var process))
            {
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            TryLaunchPendingEmulatorRequest();
        }

        private void TryLaunchPendingEmulatorRequest()
        {
            if (_pendingEmulatorLaunchRequest is not { } request)
                return;

            if (TryGetRunningTrackedEmulatorProcess(out var process))
            {
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            _pendingEmulatorLaunchRequest = null;
            _ = LaunchEmulatorAsync(request);
        }

        private async Task LaunchEmulatorAsync(PendingEmulatorLaunchRequest request)
        {
            var launchStopwatch = Stopwatch.StartNew();
            try
            {
                ClearRetroArchErrorState();

                var handler = request.Handler;
                CurrentEmulatorHandler = handler;

                SelectedCaptureMode = handler.PreferredCaptureMode;
                EmulatorCaptureDelayMs = handler.IsWindowEmbeddingSupported
                    ? 0
                    : handler.CaptureStartupDelayMs;

                SLog.Info($"Selected capture mode for '{handler.HandlerId}' is {SelectedCaptureMode}.");

                if (!handler.IsPrepared)
                    handler.Prepare();

                if (handler is CemuHandler cemuHandler)
                    cemuHandler.ApplyFullscreenScalingWorkaround(handler.LauncherPath ?? string.Empty);

                if (string.Equals(handler.HandlerId, XeniaHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(handler.LauncherPath))
                {
                    var xeniaTitleId = XeniaTitleIdResolver.Resolve(request.RomPath);
                    var xeniaDirectory = Path.GetDirectoryName(handler.LauncherPath);
                    await Task.Run(() => XeniaCustomConfigService.PrepareConfigForLaunch(xeniaDirectory, xeniaTitleId))
                        .ConfigureAwait(false);
                }

                var rpcs3TitleId = string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase)
                    ? Ps3InstalledGameHelper.GetTitleId(request.RomPath)
                    : null;
                if (!string.IsNullOrWhiteSpace(rpcs3TitleId))
                {
                    SLog.Info($"EmulationViewModel resolved RPCS3 title id '{rpcs3TitleId}' for '{request.RomPath}'.");
                    var rpcs3Directory = !string.IsNullOrWhiteSpace(CurrentSectionRpcs3EmulatorPath)
                        ? CurrentSectionRpcs3EmulatorPath
                        : Rpcs3CustomConfigService.ResolveEmulatorDirectory(handler.LauncherPath);
                    await Task.Run(() => Rpcs3CustomConfigService.PrepareConfigForLaunch(rpcs3Directory, rpcs3TitleId))
                        .ConfigureAwait(false);
                    _activeRpcs3SessionTitleId = Rpcs3CustomConfigService.NormalizeTitleId(rpcs3TitleId);
                    _activeRpcs3SessionEmulatorDirectory = rpcs3Directory;
                }
                else
                {
                    _activeRpcs3SessionTitleId = null;
                    _activeRpcs3SessionEmulatorDirectory = null;
                }

                EnsureAppTopMostBeforeLaunch();

                var launchRomPath = request.RomPath;
                if (string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(rpcs3TitleId))
                {
                    var preferredBootPath = Ps3InstalledGameHelper.GetPreferredBootPath(request.RomPath);
                    if (!string.IsNullOrWhiteSpace(preferredBootPath))
                    {
                        launchRomPath = preferredBootPath;
                        SLog.Info($"EmulationViewModel booting RPCS3 using EBOOT path '{launchRomPath}'.");
                    }
                    else
                    {
                        launchRomPath = Rpcs3Handler.BuildGameIdBootPath(rpcs3TitleId);
                        SLog.Info($"EmulationViewModel fallback booting RPCS3 by GAMEID using '{launchRomPath}'.");
                    }
                }

                if (handler is ShadPs4Handler shadPs4LaunchHandler)
                    shadPs4LaunchHandler.UseIpcForCheatsLaunch = true;

                var startInfo = handler.BuildStartInfo(
                    handler.LauncherPath ?? string.Empty,
                    launchRomPath,
                    request.LaunchSettings?.StartFullscreen == true,
                    request.AlbumTitle,
                    request.LaunchSettings?.SelectedRetroArchCore);

                if (string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(_activeRpcs3SessionEmulatorDirectory))
                {
                    Rpcs3CustomConfigService.ApplyConfigDirectoryEnvironment(
                        startInfo,
                        _activeRpcs3SessionEmulatorDirectory);
                }

                PrepareLinuxAppImageStartInfo(startInfo);
                var process = Process.Start(startInfo);
                SLog.Info($"Emulation launch started for '{request.AlbumTitle}'/'{request.ItemTitle}' after {launchStopwatch.ElapsedMilliseconds} ms. pid={(process?.Id ?? 0)}.");

                if (process != null)
                {
                    SLog.Info($"Emulator process launched: pid={process.Id}, name={process.ProcessName}, hasExited={process.HasExited}.");
                }

                AttachShadPs4IpcSessionIfNeeded(handler, process);

                RestoreHostWindowFocus();

                Process? runtimeProcess = process;
                if (process != null)
                {
                    try
                    {
                        runtimeProcess = await handler.ResolveRuntimeProcessAsync(process, CancellationToken.None).ConfigureAwait(false) ?? process;
                        SLog.Info($"Emulator runtime process resolution completed in {launchStopwatch.ElapsedMilliseconds} ms for '{request.AlbumTitle}'/'{request.ItemTitle}'. runtimePid={runtimeProcess?.Id ?? 0}.");
                    }
                    catch (OperationCanceledException)
                    {
                        runtimeProcess = process;
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to resolve emulator runtime process for '{request.AlbumTitle}' item '{request.ItemTitle}'.", ex);
                        runtimeProcess = process;
                    }

                    if (handler.HideUntilCaptured &&
                        !handler.DeferWindowHidingUntilCaptured &&
                        runtimeProcess != null)
                    {
                        try
                        {
                            runtimeProcess.Refresh();
                            if (!runtimeProcess.HasExited)
                                handler.PrepareProcessForCapture(runtimeProcess);
                        }
                        catch (Exception logEx) { SLog.Warn("Exception caught", logEx); }
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (process != null && runtimeProcess != null && !ReferenceEquals(process, runtimeProcess))
                    {
                        try
                        {
                            SLog.Info($"Emulator runtime process resolved: launcherPid={process.Id}, runtimePid={runtimeProcess.Id}, runtimeName={runtimeProcess.ProcessName}.");
                        }
                        catch
                        {
                            SLog.Info("Emulator runtime process resolved to a spawned process.");
                        }
                    }

                    TrackEmulatorProcess(runtimeProcess, request.RomPath, handler, request.ItemTitle);
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to launch emulator for '{request.AlbumTitle}' item '{request.ItemTitle}'.", ex);
                if (request.Handler is CemuHandler cemuHandler)
                    cemuHandler.RestoreFullscreenScalingWorkaround(request.Handler.LauncherPath ?? string.Empty);
                RestoreAppTopMost();
                RestoreHostWindowFocus();
                IsEmulatorLaunchInProgress = false;
            }
        }

        private static void PrepareLinuxAppImageStartInfo(ProcessStartInfo startInfo)
        {
            if (!OperatingSystem.IsLinux())
                return;

            if (string.IsNullOrWhiteSpace(startInfo.FileName) ||
                !startInfo.FileName.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var appImagePath = startInfo.FileName;
            var originalArgs = startInfo.ArgumentList.ToArray();

            startInfo.FileName = "env";
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add("APPIMAGE_EXTRACT_AND_RUN=1");
            startInfo.ArgumentList.Add(appImagePath);
            startInfo.ArgumentList.Add("--appimage-extract-and-run");

            foreach (var arg in originalArgs)
                startInfo.ArgumentList.Add(arg);
        }

        private bool TryGetRunningTrackedEmulatorProcess(out Process process)
        {
            process = _activeEmulatorProcess!;
            if (process == null)
                return false;

            try
            {
                if (process.HasExited)
                    return false;
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to inspect the tracked emulator process state.", ex);
                return false;
            }

            return true;
        }

        private bool TryRequestRpcs3Shutdown(Process process)
        {
            if (!string.Equals(CurrentEmulatorHandler?.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase))
                return false;

            var mainWindowHandle = ResolveProcessMainWindowHandle(process);
            if (mainWindowHandle == IntPtr.Zero)
            {
                SLog.Info($"EmulationViewModel could not resolve the RPCS3 main window handle for pid={process.Id}.");
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch (Exception ex)
                {
                    SLog.Debug($"EmulationViewModel failed to force-close RPCS3 pid={process.Id} after it could not resolve the main window.", ex);
                }

                return true;
            }

            var sent = Win32API.TrySendControlS(mainWindowHandle);
            if (sent)
            {
                SLog.Info($"EmulationViewModel sent the RPCS3 stop shortcut to pid={process.Id}.");

                try
                {
                    SLog.Info($"EmulationViewModel waiting up to 5000 ms for RPCS3 pid={process.Id} to exit after sending the stop shortcut.");
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    SLog.Debug("Timed wait for RPCS3 shutdown failed; continuing with final state checks.", ex);
                }

                try
                {
                    if (!process.HasExited)
                    {
                        SLog.Info($"EmulationViewModel is force-closing RPCS3 pid={process.Id} after the stop shortcut timed out.");
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    SLog.Debug("Final forced RPCS3 shutdown hit a process race.", ex);
                }

                return true;
            }

            SLog.Info($"EmulationViewModel failed to send the RPCS3 stop shortcut to pid={process.Id}; forcing termination without closing the window.");

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                SLog.Debug($"EmulationViewModel failed to force-close RPCS3 pid={process.Id} after the stop shortcut could not be sent.", ex);
            }

            return true;
        }

        private static IntPtr ResolveProcessMainWindowHandle(Process process, int maxAttempts = 20, int delayMs = 100)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    process.Refresh();
                    var hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                        return hwnd;
                }
                catch (Exception logEx) { SLog.Warn("Non-critical error", logEx); }

                Thread.Sleep(delayMs);
            }

            return IntPtr.Zero;
        }

        private void CloseTrackedEmulatorForPendingLaunch(Process process)
        {
            if (_isClosingActiveEmulatorForRelaunch)
            {
                SLog.Info("EmulationViewModel ignored a duplicate emulator close request because shutdown is already in progress.");
                return;
            }

            _isClosingActiveEmulatorForRelaunch = true;
            SLog.Info($"EmulationViewModel starting tracked emulator shutdown. pid={process.Id}.");
            if (!RequestStopEmulatorCapture)
                RequestStopEmulatorCapture = true;
            _ = CloseTrackedEmulatorForPendingLaunchAsync(process);
        }

        private async Task CloseTrackedEmulatorForPendingLaunchAsync(Process process)
        {
            try
            {
                await WaitForCaptureStopBeforeClosingProcessAsync().ConfigureAwait(false);

                if (TryRequestRpcs3Shutdown(process))
                {
                    return;
                }

                await Task.Run(() =>
                {
                    var forceKillFirst = string.Equals(CurrentEmulatorHandler?.HandlerId, "pcsx2", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "duckstation", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "dolphin", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase);
                    if (!forceKillFirst)
                    {
                        try
                        {
                            forceKillFirst = process.ProcessName.Contains("pcsx2", StringComparison.OrdinalIgnoreCase) ||
                                             process.ProcessName.Contains("rpcs3", StringComparison.OrdinalIgnoreCase) ||
                                             process.ProcessName.Contains("duckstation", StringComparison.OrdinalIgnoreCase) ||
                                             process.ProcessName.Contains("dolphin", StringComparison.OrdinalIgnoreCase);
                            forceKillFirst |= process.ProcessName.Contains("shadps4", StringComparison.OrdinalIgnoreCase);
                        }
                        catch (Exception logEx) { SLog.Warn("Non-critical error", logEx); }
                    }

                    try
                    {
                        if (forceKillFirst)
                        {
                            SLog.Info($"EmulationViewModel using direct termination for pid={process.Id} to bypass confirm-shutdown dialogs.");
                            process.Kill(true);
                        }
                        else
                        {
                            var closeMainWindowResult = process.CloseMainWindow();
                            SLog.Info($"EmulationViewModel CloseMainWindow returned {closeMainWindowResult} for pid={process.Id}.");
                            if (!closeMainWindowResult)
                                process.Kill(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        SLog.Debug("Failed to close the emulator gracefully; forcing termination.", ex);

                        try
                        {
                            process.Kill(true);
                        }
                        catch (Exception killEx)
                        {
                            SLog.Debug("Failed to force-close the emulator process during relaunch.", killEx);
                        }
                    }

                    try
                    {
                        SLog.Info($"EmulationViewModel waiting up to 5000 ms for emulator pid={process.Id} to exit.");
                        process.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        SLog.Debug("Timed wait for emulator shutdown failed; continuing with final state checks.", ex);
                    }

                    try
                    {
                        if (!process.HasExited)
                        {
                            SLog.Info($"EmulationViewModel is force-closing emulator pid={process.Id} after graceful shutdown timed out.");
                            process.Kill(true);
                            process.WaitForExit(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        SLog.Debug("Final forced emulator shutdown hit a process race.", ex);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SLog.Info("EmulationViewModel finished the tracked emulator shutdown flow.");
                    _isClosingActiveEmulatorForRelaunch = false;
                    TryLaunchPendingEmulatorRequest();
                }, DispatcherPriority.Background);
            }
        }

        private async Task WaitForCaptureStopBeforeClosingProcessAsync()
        {
            const int maxAttempts = 80;
            const int delayMs = 50;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!RequestStopEmulatorCapture)
                    break;

                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            await Task.Delay(250).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (EmulatorTargetHwnd != IntPtr.Zero)
                {
                    SLog.Info($"EmulationViewModel clearing emulator hwnd 0x{EmulatorTargetHwnd.ToInt64():X} after capture stop request.");
                    EmulatorTargetHwnd = IntPtr.Zero;
                }
            }, DispatcherPriority.Background);

            await Task.Delay(250).ConfigureAwait(false);
        }

        private void TrackEmulatorProcess(Process? process, string romPath, IEmulatorHandler handler, string? gameTitle = null)
        {
            EmulatorTargetHwnd = IntPtr.Zero;

            if (process == null)
            {
                SLog.Warn($"Emulator launch for '{romPath}' did not expose a trackable process handle.");
                if (handler is CemuHandler cemuHandler)
                    cemuHandler.RestoreFullscreenScalingWorkaround(handler.LauncherPath ?? string.Empty);
                RestoreAppTopMost();
                RestoreHostWindowFocus();
                EmulatorTargetHwnd = IntPtr.Zero;
                EmulatorTargetProcessId = 0;
                IsEmulatorLaunchInProgress = false;
                StopGameplayPreview();
                return;
            }

            DetachTrackedEmulatorProcess();

            _retroArchLogWatcherCts?.Cancel();
            _retroArchLogWatcherCts?.Dispose();
            _retroArchLogWatcherCts = null;

            _activeEmulatorWatchdogCts?.Cancel();
            _activeEmulatorWatchdogCts?.Dispose();
            _activeEmulatorWatchdogCts = null;

            _activeEmulatorProcess = process;
            _activeEmulatorRomPath = romPath;
            _activeEmulatorGameTitle = gameTitle;
            EmulatorTargetProcessId = process?.Id ?? 0;

            if (OperatingSystem.IsWindows() && process != null)
            {
                try
                {
                    EmulatorJobObject.AssignProcess(process);
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to assign emulator process to job object.", ex);
                }

                try
                {
                    _emulatorAudioVolume.Attach(process.Id);
                    float currentVolume = _emulatorAudioVolume.Volume;
                    EmulatorVolume = Math.Round(currentVolume * 100.0, 1);
                }
                catch
                {
                    EmulatorVolume = 100.0;
                }
            }

            if (_shadPs4IpcSession == null)
                AttachShadPs4IpcSessionIfNeeded(handler, process);

            UpdateShadPs4CheatsIpcState();
            OnPropertyChanged(nameof(ShowShadPs4InGameCheatsButton));

            CancelAppTopmostRestoreTimeout();

            try
            {
                process!.EnableRaisingEvents = true;
                process!.Exited += ActiveEmulatorProcess_Exited;
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to subscribe to emulator exit events.", ex);
            }

            IsEmulatorRunning = !process!.HasExited;

            if (handler is RetroArchHandler retroArchHandler)
                StartRetroArchLogWatcher(process, retroArchHandler);

            if (process.HasExited)
                HandleTrackedEmulatorExited(process);
            else
            {
                StartActiveEmulatorWatchdog(process);
                _ = ResolveEmulatorTargetHwndAsync(process, romPath, handler);
            }

            RestoreHostWindowFocus();
        }

        private void StartActiveEmulatorWatchdog(Process process)
        {
            _activeEmulatorWatchdogCts?.Cancel();
            _activeEmulatorWatchdogCts?.Dispose();

            var cts = new CancellationTokenSource();
            _activeEmulatorWatchdogCts = cts;
            _ = MonitorActiveEmulatorAsync(process, cts.Token);
        }

        private async Task MonitorActiveEmulatorAsync(Process process, CancellationToken cancellationToken)
        {
            const int pollDelayMs = 500;
            const int missingWindowThreshold = 6;
            var missingWindowCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!ReferenceEquals(_activeEmulatorProcess, process))
                    break;

                try
                {
                    process.Refresh();
                    if (process.HasExited)
                        break;
                }
                catch
                {
                    break;
                }

                if (!OperatingSystem.IsWindows())
                    continue;

                var targetHwnd = EmulatorTargetHwnd;
                if (targetHwnd == IntPtr.Zero)
                {
                    missingWindowCount = 0;
                    continue;
                }

                if (NativeIsWindow(targetHwnd))
                {
                    missingWindowCount = 0;
                    continue;
                }

                missingWindowCount++;
                if (missingWindowCount < missingWindowThreshold)
                    continue;

                SLog.Warn($"EmulationViewModel detected that emulator target hwnd 0x{targetHwnd.ToInt64():X} is no longer valid while process pid={process.Id} is still tracked. Triggering close flow.");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_activeEmulatorProcess, process))
                        return;

                    RequestStopEmulatorCapture = true;
                    CloseTrackedEmulatorForPendingLaunch(process);
                }, DispatcherPriority.Background);

                break;
            }
        }

        private void ActiveEmulatorProcess_Exited(object? sender, EventArgs e)
        {
            if (sender is not Process process)
                return;

            Dispatcher.UIThread.Post(() => HandleTrackedEmulatorExited(process), DispatcherPriority.Background);
        }

        private void HandleTrackedEmulatorExited(Process process)
        {
            var currentHandler = CurrentEmulatorHandler;

            if (!ReferenceEquals(_activeEmulatorProcess, process))
            {
                try
                {
                    process.Exited -= ActiveEmulatorProcess_Exited;
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to clean up a stale emulator process reference.", ex);
                }

                return;
            }

            if (string.Equals(currentHandler?.HandlerId, Rpcs3Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_activeRpcs3SessionTitleId) &&
                !string.IsNullOrWhiteSpace(_activeRpcs3SessionEmulatorDirectory))
            {
                var rpcs3TitleId = _activeRpcs3SessionTitleId;
                var rpcs3Directory = _activeRpcs3SessionEmulatorDirectory;
                _ = Task.Run(() => Rpcs3CustomConfigService.ImportFromRpcs3AfterSession(rpcs3Directory, rpcs3TitleId));
            }

            _activeRpcs3SessionTitleId = null;
            _activeRpcs3SessionEmulatorDirectory = null;

            DetachTrackedEmulatorProcess();
            IsEmulatorRunning = false;
            if (!_isClosingActiveEmulatorForRelaunch)
                RequestStopEmulatorCapture = true;
            if (currentHandler is CemuHandler cemuHandler)
                cemuHandler.RestoreFullscreenScalingWorkaround(currentHandler.LauncherPath ?? string.Empty);
            RestoreAppTopMost();

            if (CurrentEmulatorHandler is RetroArchHandler retroArchHandler)
            {
                TryShowRetroArchErrorPrompt(process, retroArchHandler);
            }

            var hadPendingLaunch = _pendingEmulatorLaunchRequest != null;
            TryLaunchPendingEmulatorRequest();

            if (!hadPendingLaunch)
                IsEmulatorLaunchInProgress = false;
        }

        private void DetachTrackedEmulatorProcess()
        {
            _activeEmulatorWatchdogCts?.Cancel();
            _activeEmulatorWatchdogCts?.Dispose();
            _activeEmulatorWatchdogCts = null;

            if (_activeEmulatorProcess == null)
            {
                EmulatorTargetHwnd = IntPtr.Zero;
                EmulatorTargetProcessId = 0;
                _retroArchLogWatcherCts?.Cancel();
                _retroArchLogWatcherCts?.Dispose();
                _retroArchLogWatcherCts = null;
                return;
            }

            try
            {
                _activeEmulatorProcess.Exited -= ActiveEmulatorProcess_Exited;
                _activeEmulatorProcess.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to detach the active emulator process cleanly.", ex);
            }
            finally
            {
                _activeEmulatorProcess = null;
                _activeEmulatorRomPath = null;
                _activeEmulatorGameTitle = null;
                EmulatorTargetHwnd = IntPtr.Zero;
                EmulatorTargetProcessId = 0;
                _emulatorAudioVolume.Detach();
                DetachShadPs4IpcSession();
                OnPropertyChanged(nameof(ShowShadPs4InGameCheatsButton));
            }
        }

        private void AttachShadPs4IpcSessionIfNeeded(IEmulatorHandler handler, Process? process)
        {
            if (handler is not ShadPs4Handler shadPs4Handler || !shadPs4Handler.UseIpcForCheatsLaunch || process == null)
                return;

            try
            {
                if (process.HasExited)
                    return;
            }
            catch
            {
                return;
            }

            if (_shadPs4IpcSession != null && _shadPs4IpcSession.ProcessId == process.Id)
                return;

            DetachShadPs4IpcSession();
            _shadPs4IpcSession = ShadPs4IpcSession.TryAttach(process, shadPs4Handler.CurrentLaunchTranscriptPath);
            if (_shadPs4IpcSession != null)
                _shadPs4IpcSession.CapabilitiesChanged += OnShadPs4IpcCapabilitiesChanged;
        }

        private void DetachShadPs4IpcSession()
        {
            if (_shadPs4IpcSession != null)
                _shadPs4IpcSession.CapabilitiesChanged -= OnShadPs4IpcCapabilitiesChanged;

            _shadPs4IpcSession?.Dispose();
            _shadPs4IpcSession = null;
            UpdateShadPs4CheatsIpcState();
        }

        private void OnShadPs4IpcCapabilitiesChanged()
        {
            Dispatcher.UIThread.Post(UpdateShadPs4CheatsIpcState, DispatcherPriority.Background);
        }

        private void UpdateShadPs4CheatsIpcState()
        {
            var isShadPs4Running = IsEmulatorRunning &&
                                   string.Equals(CurrentEmulatorHandler?.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase);
            ShadPs4CheatsEditor.SetIpcSession(_shadPs4IpcSession, isShadPs4Running);

            if (isShadPs4Running && !string.IsNullOrWhiteSpace(_activeEmulatorRomPath))
            {
                _ = ShadPs4CheatsEditor.EnsureLoadedForGameAsync(
                    CurrentSectionShadPs4EmulatorPath,
                    _activeEmulatorRomPath,
                    _activeEmulatorGameTitle);
            }
        }

        [RelayCommand]
        private async Task ToggleShadPs4CheatsOverlay()
        {
            if (!ShowShadPs4InGameCheatsButton)
                return;

            if (ShadPs4CheatsEditor.IsOpen)
            {
                ShadPs4CheatsEditor.IsOpen = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeEmulatorRomPath))
                return;

            UpdateShadPs4CheatsIpcState();
            await ShadPs4CheatsEditor.LoadAsync(
                CurrentSectionShadPs4EmulatorPath,
                _activeEmulatorRomPath,
                _activeEmulatorGameTitle).ConfigureAwait(true);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool NativeIsWindow(IntPtr hWnd);

        private async Task ResolveEmulatorTargetHwndAsync(Process process, string romPath, IEmulatorHandler handler)
        {
            var captureStopwatch = Stopwatch.StartNew();
            try
            {
                var hwnd = await ResolveCaptureTargetForCurrentPlatformAsync(process, handler).ConfigureAwait(false);
                if (hwnd == IntPtr.Zero)
                {
                    var maxRetries = handler is RetroArchHandler ? 4 : 1;
                    for (int i = 0; i < maxRetries && hwnd == IntPtr.Zero; i++)
                    {
                        SLog.Warn($"Failed to resolve emulator capture target for '{romPath}' (attempt {i + 1}). Retrying...");
                        await Task.Delay(2000).ConfigureAwait(false);
                        hwnd = await ResolveCaptureTargetForCurrentPlatformAsync(process, handler).ConfigureAwait(false);
                    }
                }

                if (hwnd == IntPtr.Zero)
                {
                    SLog.Warn($"Failed to resolve emulator capture target for '{romPath}' after retry.");
                    RestoreAppTopMost();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsEmulatorLaunchInProgress = false;
                        ShowEmulatorCaptureFailure(romPath, handler);
                    });
                    return;
                }

                UseHostWindowCapture = false;
                SLog.Info($"Emulation capture target resolved for '{romPath}' in {captureStopwatch.ElapsedMilliseconds} ms. hwnd=0x{hwnd.ToInt64():X}.");
                await TryApplyEmulatorTargetHwndAsync(process, hwnd, showWindowForCapture: handler.HideUntilCaptured, handler).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SLog.Debug($"Emulator capture target resolution canceled for '{romPath}'.");
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to resolve emulator capture target for '{romPath}'.", ex);
                RestoreAppTopMost();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsEmulatorLaunchInProgress = false;
                    ShowEmulatorCaptureFailure(romPath, handler, ex.Message);
                });
            }
        }

        private static async Task<IntPtr> ResolveCaptureTargetForCurrentPlatformAsync(Process process, IEmulatorHandler handler)
        {
            if (OperatingSystem.IsWindows())
                return await handler.ResolveCaptureTargetAsync(process, CancellationToken.None).ConfigureAwait(false);

            if (handler.CaptureStartupDelayMs > 0)
                await Task.Delay(handler.CaptureStartupDelayMs).ConfigureAwait(false);

            var captureProcess = ResolveCaptureProcessForCurrentPlatform(process, handler);
            try
            {
                captureProcess.Refresh();
                if (captureProcess.HasExited)
                    return IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }

            return new IntPtr(captureProcess.Id);
        }

        private static Process ResolveCaptureProcessForCurrentPlatform(Process process, IEmulatorHandler handler)
        {
            if (TryGetLiveProcess(process, out var liveProcess))
                return liveProcess;

            var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var startInfoName = Path.GetFileNameWithoutExtension(process.StartInfo?.FileName ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(startInfoName))
                    candidateNames.Add(startInfoName);
            }
            catch (Exception logEx) { SLog.Warn("Exception caught", logEx); }

            try
            {
                var executablePath = EmulatorHandlerBase.ResolveLauncherExecutablePath(handler.LauncherPath);
                var executableName = Path.GetFileNameWithoutExtension(executablePath ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(executableName))
                    candidateNames.Add(executableName);
            }
            catch (Exception logEx) { SLog.Warn("Exception caught", logEx); }

            var titleHint = handler.LauncherPath is { Length: > 0 }
                ? Path.GetFileNameWithoutExtension(handler.LauncherPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : handler.DisplayName;
            if (!string.IsNullOrWhiteSpace(titleHint))
                candidateNames.Add(titleHint);

            Process? bestCandidate = null;
            DateTime bestStartTime = DateTime.MinValue;

            foreach (var candidateName in candidateNames)
            {
                Process[] candidates;
                try
                {
                    candidates = Process.GetProcessesByName(candidateName);
                }
                catch
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (!TryGetLiveProcess(candidate, out var liveCandidate))
                        continue;

                    try
                    {
                        if (liveCandidate.StartTime > bestStartTime)
                        {
                            bestStartTime = liveCandidate.StartTime;
                            bestCandidate = liveCandidate;
                        }
                    }
                    catch (Exception logEx) { SLog.Warn("Exception caught", logEx); }
                }
            }

            return bestCandidate ?? process;
        }

        private static bool TryGetLiveProcess(Process process, out Process liveProcess)
        {
            liveProcess = process;

            try
            {
                process.Refresh();
                if (!process.HasExited)
                    return true;
            }
            catch (Exception logEx) { SLog.Warn("Exception caught", logEx); }

            return false;
        }

        private void TryShowRetroArchErrorPrompt(Process process, RetroArchHandler handler)
        {
            if (!RetroArchHandler.TryGetRetroArchErrorDetails(handler.LauncherPath, out var summary, out var details))
                return;

            if (string.IsNullOrWhiteSpace(details))
                return;

            RetroArchErrorSummary = string.IsNullOrWhiteSpace(summary)
                ? "RetroArch reported an error during launch."
                : summary;
            RetroArchErrorDetails = details;

            SLog.Warn($"RetroArch launch issue detected: {RetroArchErrorSummary}");
        }

        private void StartRetroArchLogWatcher(Process process, RetroArchHandler handler)
        {
            if (process.HasExited)
                return;

            _retroArchLogWatcherCts?.Cancel();
            _retroArchLogWatcherCts?.Dispose();
            _retroArchLogWatcherCts = new CancellationTokenSource();
            var token = _retroArchLogWatcherCts.Token;

            _ = Task.Run(async () =>
            {
                var logFilePath = RetroArchHandler.GetRetroArchLogFilePath(handler.LauncherPath);
                if (string.IsNullOrWhiteSpace(logFilePath))
                    return;

                var lastLineCount = 0;
                var startTime = DateTime.UtcNow;
                while (!token.IsCancellationRequested && !process.HasExited && DateTime.UtcNow - startTime < TimeSpan.FromSeconds(12))
                {
                    try
                    {
                        if (!File.Exists(logFilePath))
                        {
                            await Task.Delay(250, token).ConfigureAwait(false);
                            continue;
                        }

                        var lines = File.ReadAllLines(logFilePath);
                        if (lines.Length <= lastLineCount)
                        {
                            await Task.Delay(250, token).ConfigureAwait(false);
                            continue;
                        }

                        var newLines = lines.Skip(lastLineCount).ToArray();
                        lastLineCount = lines.Length;

                        if (RetroArchHandler.TryExtractRetroArchErrorDetails(newLines, out var summary, out var details))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                RetroArchErrorSummary = string.IsNullOrWhiteSpace(summary)
                                    ? "RetroArch reported an error during launch."
                                    : summary;
                                RetroArchErrorDetails = details;
                            }, DispatcherPriority.Background);
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }, token);
        }

        private async Task<bool> TryApplyEmulatorTargetHwndAsync(Process process, IntPtr hwnd, bool showWindowForCapture, IEmulatorHandler handler)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var handoffStopwatch = Stopwatch.StartNew();
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_activeEmulatorProcess, process))
                    return false;

                if (_isClosingActiveEmulatorForRelaunch)
                {
                    SLog.Info(
                        $"EmulationViewModel skipped applying emulator hwnd 0x{hwnd.ToInt64():X} because emulator shutdown is in progress.");
                    return false;
                }

                try
                {
                    if (process.HasExited)
                        return false;
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to confirm emulator process state before applying the capture target.", ex);
                    return false;
                }

                try
                {
                    handler.PrepareWindowForCaptureAttach(hwnd);
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to prepare emulator window geometry before capture attach.", ex);
                }

                RestoreAppTopMost();
                RestoreHostWindowFocus();

                if (EmulatorTargetHwnd != hwnd)
                    EmulatorTargetHwnd = hwnd;

                IsEmulatorLaunchInProgress = false;
                SLog.Info($"Emulation capture handoff completed in {handoffStopwatch.ElapsedMilliseconds} ms for pid={process.Id}. hwnd=0x{hwnd.ToInt64():X}, showWindowForCapture={showWindowForCapture}.");

                return true;
            }, DispatcherPriority.Background);
        }

        private static void TryWaitForInputIdle(Process process, int timeoutMs)
        {
            try
            {
                process.WaitForInputIdle(timeoutMs);
            }
            catch (Exception ex)
            {
                SLog.Debug("Emulator did not provide an input-idle state; falling back to polling.", ex);
            }
        }

        private static void RevealCaptureWindow(IntPtr platformWindowHandle)
        {
            try
            {
                EmulatorCapturePlatform.RevealWindowForCapture(platformWindowHandle);
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to reveal the emulator window for the active capture platform.", ex);
            }
        }


        private void EnsureAppTopMostBeforeLaunch()
        {
            if (_appTopmostOverride)
                return;

            var hwnd = GetHostWindowHandle();
            if (hwnd == IntPtr.Zero)
                return;

            _appWasTopmostBeforeEmulatorLaunch = Win32API.IsWindowTopMost(hwnd);
            _appWindowHandleBeforeEmulatorLaunch = hwnd;

            if (!_appWasTopmostBeforeEmulatorLaunch)
            {
                Win32API.SetWindowTopMost(hwnd);
                _appTopmostOverride = true;
                StartAppTopmostRestoreTimeout();
            }
        }

        private void StartAppTopmostRestoreTimeout()
        {
            _appTopmostRestoreCts?.Cancel();
            _appTopmostRestoreCts?.Dispose();
            _appTopmostRestoreCts = new CancellationTokenSource();
            var token = _appTopmostRestoreCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AppTopmostRestoreTimeout, token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_appTopmostOverride)
                        {
                            SLog.Info("Restoring app topmost because emulator launch did not complete within timeout.");
                            RestoreAppTopMost();
                        }
                    }, DispatcherPriority.Background);
                }
                catch (OperationCanceledException logEx) { SLog.Warn("Cancellation expected when restore happens normally.", logEx); }
                catch (Exception ex)
                {
                    SLog.Warn("App topmost restore timeout task failed.", ex);
                }
            }, token);
        }

        private void CancelAppTopmostRestoreTimeout()
        {
            _appTopmostRestoreCts?.Cancel();
            _appTopmostRestoreCts?.Dispose();
            _appTopmostRestoreCts = null;
        }

        private void RestoreAppTopMost()
        {
            if (!_appTopmostOverride)
                return;

            if (_appWindowHandleBeforeEmulatorLaunch == IntPtr.Zero)
                return;

            CancelAppTopmostRestoreTimeout();

            Win32API.SetWindowNotTopMost(_appWindowHandleBeforeEmulatorLaunch);
            _appTopmostOverride = false;
            _appWindowHandleBeforeEmulatorLaunch = IntPtr.Zero;
        }

        private static void RestoreHostWindowFocus()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow is { } mainWindow)
                {
                    mainWindow.Activate();
                }
            }, DispatcherPriority.Background);
        }

        private static IntPtr GetHostWindowHandle()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return IntPtr.Zero;

            return desktop.MainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }

        private static int GetRoundedSelectedIndex(double value) => (int)Math.Round(value);

        private static MediaItem CreateEmptyMediaItem() => new()
        {
            Title = string.Empty,
            Artist = string.Empty,
            Album = string.Empty
        };

        private bool IsGameplayAutoplayEnabled => SettingsViewModel?.EmulationGameplayAutoplay == true;
        private bool IsYtDlpInstalled => SettingsViewModel?.IsYtDlpInstalled ?? YtDlpManager.IsInstalled;
    }
}
