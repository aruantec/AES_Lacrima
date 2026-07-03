using AES_Controls.Helpers;

using AES_Controls.Helpers.Windows;

using AES_Emulation.Controls;

using AES_Emulation.EmulationHandlers;

using AES_Emulation.Windows.Parsec;

using Avalonia.Threading;

using System;

using System.Diagnostics;

using System.Threading;

using System.Threading.Tasks;



namespace AES_Lacrima.ViewModels;



public partial class EmulationViewModel

{

    private ParsecVirtualDisplayMonitor? _windowsParsecMonitor;

    private bool _windowsParsecCaptureHandoffCompleted;

    private bool _parsecVirtualDisplayPlacementConfirmed;



    private bool ShouldAttemptWindowsParsecCapture(IEmulatorHandler? handler = null)
    {
        handler ??= CurrentEmulatorHandler;
        return handler != null
               && ParsecVddManager.UsesVirtualDisplayCaptureForHandler(handler.HandlerId)
               && OperatingSystem.IsWindows()
               && handler.PreferredCaptureMode == EmulatorCaptureMode.DirectComposition;
    }

    private bool ShouldUseWindowsParsecCapture(IEmulatorHandler? handler = null)
    {
        handler ??= CurrentEmulatorHandler;
        return handler != null
               && ParsecVddManager.UsesVirtualDisplayCaptureForHandler(handler.HandlerId)
               && OperatingSystem.IsWindows()
               && SelectedCaptureMode == EmulatorCaptureMode.DirectComposition
               && _windowsParsecMonitor is { Handle: not 0 };
    }



    private void TeardownWindowsParsecSession(bool disposeSession = true)

    {

        if (!OperatingSystem.IsWindows())

            return;



        _windowsParsecMonitor = null;

        _windowsParsecCaptureHandoffCompleted = false;

        ResetParsecVirtualDisplayPlacementState();

        // App-level Parsec session is owned by ParsecVirtualDisplayLifetime and survives between games.

    }



    private async Task EnsureWindowsParsecSessionAsync()

    {

        if (_windowsParsecMonitor is { Handle: not 0 } && ParsecVirtualDisplayLifetime.IsReady)

            return;



        try

        {

            if (!await ParsecVirtualDisplayLifetime.EnsureAppSessionAsync().ConfigureAwait(false))

            {

                SLog.Info($"Parsec VDD unavailable ({ParsecVddManager.GetDriverStatusMessage()}); falling back to HWND capture.");

                _windowsParsecMonitor = null;

                return;

            }



            _windowsParsecMonitor = ParsecVirtualDisplayLifetime.ActiveMonitor;

            var monitor = _windowsParsecMonitor;

            NotifyParsecVirtualDisplayStatusChanged();

            SLog.Info(

                $"Using Parsec app virtual display '{monitor?.DeviceName}' " +

                $"{monitor?.Width}x{monitor?.Height} at {monitor?.Left},{monitor?.Top}.");

        }

        catch (Exception ex)

        {

            SLog.Warn("Failed to attach Parsec app virtual display; falling back to standard HWND capture.", ex);

            TeardownWindowsParsecSession();

        }

    }



    internal static void InitializeParsecVirtualDisplayAtStartup()

    {

        if (!OperatingSystem.IsWindows()
            || !ParsecVddManager.UseVirtualDisplayCapture)

            return;



        _ = Task.Run(async () =>

        {

            try

            {

                // Allow settings refresh and driver probe to finish before the one-time plug-in.

                await Task.Delay(1500).ConfigureAwait(false);

                await ParsecVirtualDisplayLifetime.EnsureAppSessionAsync().ConfigureAwait(false);

            }

            catch (Exception ex)

            {

                SLog.Debug("Application startup Parsec virtual display initialization failed.", ex);

            }

        });

    }



    private static bool ShouldPreserveParsecVirtualDisplaySession() =>

        OperatingSystem.IsWindows()

        && ParsecVddManager.UseVirtualDisplayCapture

        && ParsecVirtualDisplayLifetime.IsReady;



    public bool ShowParsecVirtualDisplayStatus => OperatingSystem.IsWindows();

    public bool ParsecVirtualDisplayStatusIsHealthy =>
        UsesParsecVirtualDisplayMonitorCapture && _parsecVirtualDisplayPlacementConfirmed;

    public string ParsecVirtualDisplayStatusText => BuildParsecVirtualDisplayStatusText();

    private string BuildParsecVirtualDisplayStatusText()
    {
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        if (!ParsecVddManager.UseVirtualDisplayCapture)
            return "Virtual display: disabled in Settings";

        if (!ParsecVddManager.UseEmulatorVirtualDisplayCapture
            && IsEmulatorRunning
            && !ParsecVddManager.UsesVirtualDisplayCaptureForHandler(CurrentEmulatorHandler?.HandlerId))
            return "Virtual display: not used — HWND capture on desktop";

        if (!ParsecVirtualDisplayLifetime.IsReady)
            return "Virtual display: unavailable";

        var monitor = ParsecVirtualDisplayLifetime.ActiveMonitor;
        var monitorLabel = FormatParsecMonitorLabel(monitor);

        if (IsEmulatorRunning)
        {
            if (UsesParsecVirtualDisplayMonitorCapture)
            {
                return _parsecVirtualDisplayPlacementConfirmed
                    ? $"Virtual display: game on {monitorLabel}"
                    : $"Virtual display: monitor capture active — waiting for game on {monitorLabel}";
            }

            if (EmulatorTargetHwnd != IntPtr.Zero)
                return "Virtual display: not used — HWND capture on desktop";

            return $"Virtual display: launching on {monitorLabel}";
        }

        return $"Virtual display: ready — {monitorLabel}";
    }

    private static string FormatParsecMonitorLabel(ParsecVirtualDisplayMonitor? monitor)
    {
        if (monitor == null || monitor.Value.Handle == IntPtr.Zero)
            return "Parsec VDD";

        var value = monitor.Value;
        return $"{value.DeviceName} ({value.Width}x{value.Height})";
    }

    private void ResetParsecVirtualDisplayPlacementState()
    {
        _parsecVirtualDisplayPlacementConfirmed = false;
        NotifyParsecVirtualDisplayStatusChanged();
    }

    private void NotifyParsecVirtualDisplayStatusChanged()
    {
        OnPropertyChanged(nameof(ParsecVirtualDisplayStatusText));
        OnPropertyChanged(nameof(ParsecVirtualDisplayStatusIsHealthy));
        OnPropertyChanged(nameof(ShowParsecVirtualDisplayStatus));
    }



    private bool ShouldContinueParsecCaptureStartup()
    {
        if (_isClosingActiveEmulatorForRelaunch)
            return false;

        if (IsEmulatorRunning)
            return true;

        return IsSteamRuntimeLikelyStarting();
    }

    private static IntPtr TryResolvePlacementWindowOnMonitor(
        Process? process,
        ParsecVirtualDisplayMonitor monitor,
        IEmulatorHandler handler)
    {
        if (process != null)
        {
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    var hwnd = ParsecVirtualDisplayLaunchHelper.TryGetWindowOnMonitor(process, monitor, handler);
                    if (hwnd != IntPtr.Zero)
                        return hwnd;
                }
            }
            catch
            {
                // ignored
            }
        }

        return ParsecVirtualDisplayLaunchHelper.TryGetWindowFromInstallDirectory(
            ParsecVirtualDisplayLaunchHelper.ActiveInstallDirectory,
            monitor,
            handler);
    }

    private async Task CompleteWindowsParsecCaptureHandoffAsync(Process process, string romPath)

    {

        try

        {

            if (_windowsParsecMonitor is not { } monitor || CurrentEmulatorHandler is not { } handler)

                return;



            IntPtr placementHwnd = IntPtr.Zero;
            const int maxPlacementAttempts = 120;
            for (var attempt = 0; attempt < maxPlacementAttempts; attempt++)
            {
                if (!ShouldContinueParsecCaptureStartup())
                    return;

                var activeProcess = _activeEmulatorProcess;
                if (activeProcess == null && !IsSteamRuntimeLikelyStarting())
                    return;

                if (activeProcess != null)
                {
                    try
                    {
                        activeProcess.Refresh();
                        if (activeProcess.HasExited &&
                            string.IsNullOrWhiteSpace(_activeSteamInstallDirectory) &&
                            string.IsNullOrWhiteSpace(ParsecVirtualDisplayLaunchHelper.ActiveInstallDirectory) &&
                            !IsSteamRuntimeLikelyStarting())
                        {
                            break;
                        }
                    }
                    catch
                    {
                        if (!IsSteamRuntimeLikelyStarting())
                            break;
                    }
                }

                placementHwnd = TryResolvePlacementWindowOnMonitor(activeProcess, monitor, handler);
                if (placementHwnd != IntPtr.Zero)
                    break;

                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }

            _parsecVirtualDisplayPlacementConfirmed = placementHwnd != IntPtr.Zero;
            if (placementHwnd != IntPtr.Zero)
            {
                SLog.Info(
                    $"Parsec virtual display placement confirmed before capture: " +
                    $"{ParsecVirtualDisplayLaunchHelper.DescribeCaptureTarget(placementHwnd, monitor)}.");
            }
            else
            {
                var activePid = _activeEmulatorProcess?.Id ?? process.Id;
                SLog.Warn(
                    $"Parsec virtual display placement not confirmed before capture handoff for pid={activePid}; " +
                    $"monitor capture may show the VDD desktop until placement completes.");
            }

            var startupDelayMs = placementHwnd != IntPtr.Zero
                ? Math.Max(handler.CaptureStartupDelayMs / 4, 250)
                : Math.Min(Math.Max(handler.CaptureStartupDelayMs / 8, 500), 1500);
            await Task.Delay(startupDelayMs, CancellationToken.None).ConfigureAwait(false);

            if (!ShouldContinueParsecCaptureStartup())
                return;

            var handoffProcess = _activeEmulatorProcess;
            if (handoffProcess != null)
            {
                try
                {
                    handoffProcess.Refresh();
                    if (handoffProcess.HasExited && placementHwnd == IntPtr.Zero && !IsSteamRuntimeLikelyStarting())
                    {
                        SLog.Warn(
                            $"Parsec virtual display capture handoff skipped because emulator pid={handoffProcess.Id} exited before monitor capture started.");
                        await Dispatcher.UIThread.InvokeAsync(() => IsEmulatorLaunchInProgress = false);
                        return;
                    }
                }
                catch
                {
                    if (!IsSteamRuntimeLikelyStarting())
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => IsEmulatorLaunchInProgress = false);
                        return;
                    }
                }
            }
            else if (!IsSteamRuntimeLikelyStarting() && placementHwnd == IntPtr.Zero)
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsEmulatorLaunchInProgress = false);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>

            {

                if (_isClosingActiveEmulatorForRelaunch)

                    return;



                ClearRetroArchErrorState();

                EmulatorTargetHwnd = IntPtr.Zero;

                if (EmulatorTargetMonitor != monitor.Handle)

                    EmulatorTargetMonitor = monitor.Handle;



                _windowsParsecCaptureHandoffCompleted = true;
                _awaitingSteamRuntimeRebind = false;
                IsEmulatorRunning = true;

                IsEmulatorLaunchInProgress = false;
                BeginDesktopDisplayIsolationIfNeeded(handler);

                SLog.Info(

                    $"Parsec virtual display monitor capture handoff complete for '{romPath}'. " +

                    $"Capturing monitor 0x{monitor.Handle.ToInt64():X} ('{monitor.DeviceName}', {monitor.Width}x{monitor.Height}).");

                NotifyParsecVirtualDisplayStatusChanged();

            }, DispatcherPriority.Background);

            if (_activeEmulatorProcess is { } monitorProcess)
                _ = MonitorParsecVirtualDisplayPlacementAsync(monitorProcess, monitor, handler);

        }

        catch (Exception ex)

        {

            SLog.Warn("Parsec virtual display capture handoff failed.", ex);

            await Dispatcher.UIThread.InvokeAsync(() => IsEmulatorLaunchInProgress = false);

        }

    }



    private async Task MonitorParsecVirtualDisplayPlacementAsync(
        Process process,
        ParsecVirtualDisplayMonitor monitor,
        IEmulatorHandler handler)
    {
        while (ShouldContinueParsecCaptureStartup() && UsesParsecVirtualDisplayMonitorCapture)
        {
            try
            {
                var activeProcess = _activeEmulatorProcess;
                if (activeProcess != null)
                {
                    activeProcess.Refresh();
                    if (activeProcess.HasExited &&
                        string.IsNullOrWhiteSpace(ParsecVirtualDisplayLaunchHelper.ActiveInstallDirectory))
                    {
                        break;
                    }
                }

                ParsecVirtualDisplayLaunchHelper.EnforceGameOnVirtualDisplay(activeProcess!, monitor, handler);

                var onVdd = activeProcess != null &&
                            ParsecVirtualDisplayLaunchHelper.TryGetWindowOnMonitor(activeProcess, monitor, handler) != IntPtr.Zero;
                if (!onVdd)
                {
                    onVdd = ParsecVirtualDisplayLaunchHelper.TryGetWindowFromInstallDirectory(
                        ParsecVirtualDisplayLaunchHelper.ActiveInstallDirectory,
                        monitor,
                        handler) != IntPtr.Zero;
                }

                if (onVdd != _parsecVirtualDisplayPlacementConfirmed)
                {
                    _parsecVirtualDisplayPlacementConfirmed = onVdd;
                    await Dispatcher.UIThread.InvokeAsync(NotifyParsecVirtualDisplayStatusChanged, DispatcherPriority.Background);
                }
            }
            catch
            {
                break;
            }

            await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
        }
    }



    private bool HasOrphanedParsecEmulatorWindows()

    {

        if (!OperatingSystem.IsWindows()
            || !ParsecVddManager.UseVirtualDisplayCapture
            || !ParsecVirtualDisplayLifetime.IsReady)

            return false;



        var monitor = ParsecVirtualDisplayLifetime.ActiveMonitor;

        if (monitor == null || monitor.Value.Handle == IntPtr.Zero)

            return false;



        uint? protectedPid = null;

        try

        {

            if (_activeEmulatorProcess != null && !_activeEmulatorProcess.HasExited)

                protectedPid = (uint)_activeEmulatorProcess.Id;

        }

        catch

        {

            // ignored

        }



        return ParsecVirtualDisplayIsolation.HasForeignWindowsOnMonitor(monitor.Value, protectedPid);

    }



    private void CloseParsecMonitorForPendingLaunch()

    {

        if (_isClosingActiveEmulatorForRelaunch)

        {

            SLog.Info("EmulationViewModel ignored a duplicate Parsec virtual display cleanup request because shutdown is already in progress.");

            return;

        }



        _isClosingActiveEmulatorForRelaunch = true;

        SLog.Info("EmulationViewModel starting Parsec virtual display orphan cleanup for pending relaunch.");

        PrepareEmulatorShutdownCapture();

        _ = CloseParsecMonitorForPendingLaunchAsync();

    }



    private async Task CloseParsecMonitorForPendingLaunchAsync()

    {

        try

        {

            ParsecVirtualDisplayLaunchHelper.CancelPlacement();

            await WaitForCaptureStopBeforeClosingProcessAsync().ConfigureAwait(false);



            await Task.Run(() =>

            {

                if (ParsecVirtualDisplayLifetime.ActiveMonitor is { } monitor)

                    ParsecVirtualDisplayIsolation.TerminateForeignProcessesOnMonitor(monitor);

            }).ConfigureAwait(false);

        }

        finally

        {

            await Dispatcher.UIThread.InvokeAsync(() =>

            {

                SLog.Info("EmulationViewModel finished Parsec virtual display orphan cleanup.");

                _isClosingActiveEmulatorForRelaunch = false;

                ResetEmulatorShutdownCaptureState();

                DetachTrackedEmulatorProcess();

                IsEmulatorRunning = false;

                IsEmulatorPaused = false;

                TryLaunchPendingEmulatorRequest();

            }, DispatcherPriority.Background);

        }

    }



    private string? TryBuildEarlyWindowsParsecLaunchFailureDetails()

    {

        if (!OperatingSystem.IsWindows() ||

            _windowsParsecMonitor == null ||

            _windowsParsecCaptureHandoffCompleted ||

            _isClosingActiveEmulatorForRelaunch)

        {

            return null;

        }



        if (_awaitingSteamRuntimeRebind)
            return null;

        if ((DateTime.UtcNow - _emulatorLaunchStartedUtc).TotalSeconds > 60)
            return null;

        if (!string.IsNullOrWhiteSpace(_activeSteamInstallDirectory) &&
            ParsecVirtualDisplayLaunchHelper.HasRunningProcessInInstallDirectory(_activeSteamInstallDirectory))
        {
            return null;
        }

        if (CurrentEmulatorHandler is SteamHandler steamHandler &&
            steamHandler.TryResolveActiveWindowsGameProcess(out var activeGame) &&
            activeGame != null)
        {
            try
            {
                activeGame.Refresh();
                if (!activeGame.HasExited)
                    return null;
            }
            catch
            {
                return null;
            }
        }

        return "The game exited before Parsec virtual display capture could start.";

    }

}


