using System.Runtime.Versioning;
using log4net;
using AES_Core.Logging;

namespace AES_Controls.Helpers.Windows;

/// <summary>
/// Owns a single Parsec virtual display for the entire AES application lifetime.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ParsecVirtualDisplayLifetime
{
    private static readonly ILog Log = LogHelper.For(typeof(ParsecVirtualDisplayLifetime));
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static ParsecVirtualDisplaySession? _appSession;

    public static bool IsReady => _appSession?.IsActive == true;

    public static ParsecVirtualDisplayMonitor? ActiveMonitor => _appSession?.ActiveMonitor;

    public static async Task<bool> EnsureAppSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !ParsecVddManager.UseVirtualDisplayCapture)
            return false;

        if (_appSession?.IsActive == true)
            return true;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_appSession?.IsActive == true)
                return true;

            if (!ParsecVddManager.IsDriverActive())
            {
                Log.Info($"Parsec app session skipped: {ParsecVddManager.GetDriverStatusMessage()}");
                return false;
            }

            _appSession = await ParsecVirtualDisplaySession.StartAsync(cancellationToken).ConfigureAwait(false);
            var monitor = _appSession.ActiveMonitor;
            if (monitor != null)
            {
                ParsecVirtualDisplayBackdrop.Show(monitor.Value);
                ParsecVirtualDisplayIsolation.ApplyMonitorIsolation(monitor.Value);
            }

            Log.Info(
                $"Parsec app virtual display ready on '{monitor?.DeviceName}' " +
                $"{monitor?.Width}x{monitor?.Height} at {monitor?.Left},{monitor?.Top}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to initialize Parsec app virtual display session.", ex);
            _appSession?.Shutdown();
            _appSession = null;
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void ShutdownAppSession()
    {
        if (_appSession == null)
            return;

        try
        {
            Log.Info("Shutting down Parsec app virtual display session.");
            ParsecVirtualDisplayBackdrop.Hide();
            _appSession.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Debug("Parsec app session shutdown failed.", ex);
        }
        finally
        {
            _appSession = null;
        }
    }
}
