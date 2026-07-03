using AES_Controls.Helpers.Windows;
using AES_Core.DI;
using AES_Core.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using log4net;
using AES_Core.Logging;

namespace AES_Controls.Helpers;

/// <summary>
/// Downloads, installs, and monitors the Parsec Virtual Display Driver used for gamescope-like capture on Windows.
/// </summary>
[AutoRegister]
public partial class ParsecVddManager : ObservableObject
{
    private const string Repo = "nomi-san/parsec-vdd";
    private const string PortableAssetSuffix = "portable.zip";

    private static readonly ILog Log = LogHelper.For<ParsecVddManager>();
    private static readonly HttpClient Client = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; ParsecVddManager; AES_Lacrima)");
        return client;
    }

    private readonly string _cachePath = ApplicationPaths.GetCacheFile("parsec_vdd_cache.json");

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _isDownloading;

    public event EventHandler<InstallationCompletedEventArgs>? InstallationCompleted;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsKernelDriverPresent() =>
        IsSupported && ParsecVddKernelInstaller.IsKernelDriverPresent();

    public static bool IsDriverActive()
    {
        if (!IsSupported || !IsKernelDriverPresent())
            return false;

        if (ParsecVddKernelInstaller.IsKernelDriverHealthy())
            return true;

        try
        {
            return ParsecVddNative.TryQuickProbe();
        }
        catch (Exception ex)
        {
            Log.Debug("Parsec VDD probe failed.", ex);
            return false;
        }
    }

    public static bool IsDriverRegistered() =>
        IsSupported && IsKernelDriverPresent();

    public static bool IsInstalled => IsDriverActive();

    public const string CaptureRequiredUserMessage =
        "The Parsec Virtual Display Driver enables reliable fullscreen game capture on Windows (similar to gamescope on Linux). " +
        "Install it from Settings → Libraries to use Parsec capture.";

    public const string InstallRequiresAdminMessage =
        "The setup wizard only extracts driver files. After it finishes, click Register Driver (UAC), then Refresh Driver Info.";

    public const string InstallerPendingMessage =
        "Setup wizard opened. When it finishes, click Register Driver, then Refresh Driver Info.";

    public const string RegistrationPendingMessage =
        "Driver files are ready, but the kernel driver is not registered yet. Click Register Driver.";

    public const string ProjectUrl = "https://github.com/nomi-san/parsec-vdd";

    /// <summary>
    /// Settings toggle: Parsec VDD driver/session management is enabled.
    /// </summary>
    public static bool UseVirtualDisplayCapture { get; set; } = true;

    /// <summary>
    /// When false, emulators use legacy HWND capture (main-branch behavior).
    /// Parsec VDD code remains available for other features when this is off.
    /// </summary>
    public static bool UseEmulatorVirtualDisplayCapture { get; set; }

    /// <summary>
    /// Returns whether the given handler should launch on and capture from the Parsec virtual display.
    /// Steam on Windows always uses VDD when <see cref="UseVirtualDisplayCapture"/> is enabled.
    /// </summary>
    public static bool UsesVirtualDisplayCaptureForHandler(string? handlerId)
    {
        if (!UseVirtualDisplayCapture)
            return false;

        if (UseEmulatorVirtualDisplayCapture)
            return true;

        return string.Equals(handlerId, "steam", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetDriverStatusMessage()
    {
        if (!IsSupported)
            return "Parsec VDD is only available on Windows.";

        return ParsecVddNative.QueryDeviceStatus() switch
        {
            ParsecVddNative.DeviceStatus.Ok => "Driver installed and healthy.",
            ParsecVddNative.DeviceStatus.RestartRequired => "Driver installed — restart Windows to finish setup.",
            ParsecVddNative.DeviceStatus.DriverError => "Driver installed but failed to start. Try reinstalling from Settings.",
            ParsecVddNative.DeviceStatus.Disabled or ParsecVddNative.DeviceStatus.DisabledService =>
                "Driver is disabled in Device Manager.",
            ParsecVddNative.DeviceStatus.NotInstalled when ParsecVddKernelInstaller.HasExtractedDriverFiles() =>
                RegistrationPendingMessage,
            ParsecVddNative.DeviceStatus.NotInstalled => "Driver not installed.",
            _ => "Driver status unknown."
        };
    }

    /// <summary>
    /// Returns true only when the Parsec driver is already installed and responding.
    /// Does not launch installers (used during emulation launch fallback).
    /// </summary>
    public Task<bool> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            Status = "Parsec VDD is only supported on Windows.";
            return Task.FromResult(false);
        }

        if (IsDriverActive())
        {
            Status = GetDriverStatusMessage();
            return Task.FromResult(true);
        }

        Status = GetDriverStatusMessage();
        return Task.FromResult(false);
    }

    /// <summary>
    /// Downloads the portable Parsec package and opens the official driver setup wizard.
    /// </summary>
    public async Task<bool> DownloadAndOpenInstallerAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            Status = "Parsec VDD is only supported on Windows.";
            return false;
        }

        if (IsDriverActive())
        {
            Status = GetDriverStatusMessage();
            return true;
        }

        IsBusy = true;
        Status = "Downloading Parsec VDD package...";

        try
        {
            await DownloadPortableBundleAsync(cancellationToken).ConfigureAwait(false);

            if (IsDriverActive())
            {
                Status = "Parsec VDD installed and ready.";
                InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
                return true;
            }

            Status = "Opening Parsec driver setup wizard...";
            ParsecVddKernelInstaller.LaunchDriverInstallerUi();
            Status = InstallerPendingMessage;
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
        }
        catch (Exception ex)
        {
            Status = $"Parsec VDD setup failed: {ex.Message}";
            Log.Error(Status, ex);
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            DownloadProgress = 100;
        }
    }

    public void OpenInstallerFolder()
    {
        try
        {
            ParsecVddKernelInstaller.OpenDriverBundleDirectory();
            Status = $"Opened driver folder: {ParsecVddKernelInstaller.DriverBundleDirectory}";
        }
        catch (Exception ex)
        {
            Status = $"Could not open driver folder: {ex.Message}";
            Log.Warn(Status, ex);
        }
    }

    public void ReopenInstaller()
    {
        try
        {
            ParsecVddKernelInstaller.LaunchDriverInstallerUi();
            Status = InstallerPendingMessage;
        }
        catch (Exception ex)
        {
            Status = $"Could not open Parsec installer: {ex.Message}";
            Log.Warn(Status, ex);
        }
    }

    public async Task<bool> RegisterDriverAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            Status = "Parsec VDD is only supported on Windows.";
            return false;
        }

        if (IsDriverActive())
        {
            Status = GetDriverStatusMessage();
            return true;
        }

        IsBusy = true;
        Status = "Registering Parsec kernel driver...";

        try
        {
            var registered = await ParsecVddKernelInstaller.RegisterKernelDriverAsync(cancellationToken).ConfigureAwait(false);
            if (IsDriverActive())
            {
                Status = "Parsec VDD installed and ready.";
                InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
                return true;
            }

            Status = registered
                ? GetDriverStatusMessage()
                : "Driver registration failed. Approve the UAC prompt when cmd opens, wait for it to finish, then click Refresh Driver Info.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
        }
        catch (Exception ex)
        {
            Status = $"Parsec driver registration failed: {ex.Message}";
            Log.Error(Status, ex);
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return true;

        IsBusy = true;
        Status = "Removing Parsec Virtual Display Driver...";

        try
        {
            if (IsKernelDriverPresent())
            {
                var removed = await ParsecVddKernelInstaller.UninstallDriverAsync(cancellationToken).ConfigureAwait(false);
                if (!removed)
                {
                    Status = "Parsec uninstaller opened. Remove the driver in the setup wizard, then click Refresh Driver Info.";
                    return false;
                }
            }

            if (Directory.Exists(ParsecVddKernelInstaller.DriverBundleDirectory))
                Directory.Delete(ParsecVddKernelInstaller.DriverBundleDirectory, true);

            Status = "Parsec VDD removed.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Parsec VDD uninstall failed: {ex.Message}";
            Log.Error(Status, ex);
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string?> GetInstalledVersionAsync()
    {
        if (!IsDriverActive())
            return null;

        try
        {
            using var session = ParsecVirtualDisplaySession.TryOpenProbe();
            return session?.DriverVersion.ToString();
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetLatestReleaseTagAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Client.DefaultRequestHeaders.UserAgent.Clear();
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; ParsecVddManager; AES_Lacrima)");
            using var response = await Client.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to query latest Parsec VDD release.", ex);
            return null;
        }
    }

    private async Task DownloadPortableBundleAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ParsecVddKernelInstaller.DriverBundleDirectory);
        if (ParsecVddKernelInstaller.HasDriverPayload() || ParsecVddKernelInstaller.FindDriverInstallerExe() != null)
        {
            try
            {
                ParsecVddKernelInstaller.EnsureDriverPayloadExtracted();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("Existing Parsec VDD bundle is incomplete; re-downloading.", ex);
            }
        }

        IsDownloading = true;
        DownloadProgress = 0;
        Status = "Downloading Parsec VDD package...";

        var release = await GetLatestReleaseJsonAsync(cancellationToken).ConfigureAwait(false);
        var assetUrl = release?.Assets?.FirstOrDefault(a =>
            a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;

        assetUrl ??= release?.Assets?.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;

        assetUrl ??= "https://github.com/nomi-san/parsec-vdd/releases/download/v0.45.1/ParsecVDisplay-v0.45-portable.zip";

        var zipPath = Path.Combine(ApplicationPaths.CacheDirectory, "parsec-vdd-portable.zip");
        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        using (var response = await Client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = File.Create(zipPath);
            var buffer = new byte[81920];
            long total = response.Content.Headers.ContentLength ?? -1;
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                readTotal += read;
                if (total > 0)
                    DownloadProgress = Math.Clamp(readTotal * 100.0 / total, 0, 100);
            }
        }

        if (Directory.Exists(ParsecVddKernelInstaller.DriverBundleDirectory))
            Directory.Delete(ParsecVddKernelInstaller.DriverBundleDirectory, true);
        Directory.CreateDirectory(ParsecVddKernelInstaller.DriverBundleDirectory);
        ZipFile.ExtractToDirectory(zipPath, ParsecVddKernelInstaller.DriverBundleDirectory, true);

        if (ParsecVddKernelInstaller.FindDriverInstallerExe() == null)
            throw new FileNotFoundException("Parsec VDD driver installer was not found in the downloaded portable package.");

        Status = "Unpacking Parsec VDD driver files...";
        ParsecVddKernelInstaller.EnsureDriverPayloadExtracted();
    }

    private static async Task<GitHubRelease?> GetLatestReleaseJsonAsync(CancellationToken cancellationToken)
    {
        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; ParsecVddManager; AES_Lacrima)");
        using var response = await Client.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonSerializer.Deserialize<GitHubRelease>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}

/// <summary>
/// Lightweight probe session used only to verify the Parsec VDD device responds.
/// </summary>
public sealed class ParsecVirtualDisplaySession : IDisposable
{
    private readonly IntPtr _handle;
    private readonly Timer? _keepAliveTimer;
    private int _displayIndex = -1;
    private bool _disposed;

    public int DriverVersion { get; }
    public ParsecVirtualDisplayMonitor? ActiveMonitor { get; private set; }

    public bool IsActive => !_disposed && _handle != IntPtr.Zero && ActiveMonitor != null;

    private ParsecVirtualDisplaySession(IntPtr handle, int driverVersion, Timer? keepAliveTimer)
    {
        _handle = handle;
        DriverVersion = driverVersion;
        _keepAliveTimer = keepAliveTimer;
    }

    public static ParsecVirtualDisplaySession? TryOpenProbe()
    {
        var handle = ParsecVddNative.OpenDevice();
        if (handle == IntPtr.Zero)
            return null;

        var version = ParsecVddNative.GetVersion(handle);
        ParsecVddNative.Update(handle);
        return new ParsecVirtualDisplaySession(handle, version, null);
    }

    public static async Task<ParsecVirtualDisplaySession> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        if (ParsecVddNative.QueryDeviceStatus() != ParsecVddNative.DeviceStatus.Ok)
            throw new InvalidOperationException(ParsecVddManager.GetDriverStatusMessage());

        var handle = ParsecVddNative.OpenDevice();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Could not open Parsec Virtual Display Driver device.");

        var version = ParsecVddNative.GetVersion(handle);
        var before = ParsecVirtualDisplayMonitorHelper.EnumerateParsecMonitors();
        if (before.Count > 0)
        {
            var reused = before[^1];
            var keepAlive = CreateKeepAliveTimer(handle);
            return new ParsecVirtualDisplaySession(handle, version, keepAlive)
            {
                ActiveMonitor = reused,
                _displayIndex = -1
            };
        }

        var displayIndex = ParsecVddNative.AddDisplay(handle);
        if (displayIndex < 0)
        {
            ParsecVddNative.CloseDevice(handle);
            throw new InvalidOperationException("Parsec VDD failed to add a virtual display.");
        }

        var timer = CreateKeepAliveTimer(handle);
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);

        var monitor = ParsecVirtualDisplayMonitorHelper.TryGetNewestParsecMonitor(before)
                      ?? ParsecVirtualDisplayMonitorHelper.EnumerateParsecMonitors().LastOrDefault();

        if (monitor.Handle == IntPtr.Zero)
        {
            timer.Dispose();
            ParsecVddNative.RemoveDisplay(handle, displayIndex);
            ParsecVddNative.CloseDevice(handle);
            throw new InvalidOperationException("Parsec virtual display monitor did not appear.");
        }

        return new ParsecVirtualDisplaySession(handle, version, timer)
        {
            ActiveMonitor = monitor,
            _displayIndex = displayIndex
        };
    }

    private static Timer CreateKeepAliveTimer(IntPtr handle) =>
        new(_ =>
        {
            try { ParsecVddNative.Update(handle); }
            catch { /* best effort */ }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

    public void Dispose()
    {
        Shutdown(unplugDisplay: false);
    }

    /// <summary>
    /// Tears down the driver handle. When <paramref name="unplugDisplay"/> is true, also removes the virtual monitor.
    /// </summary>
    public void Shutdown(bool unplugDisplay = true)
    {
        if (_disposed)
            return;

        _disposed = true;
        _keepAliveTimer?.Dispose();

        try
        {
            if (unplugDisplay && _displayIndex >= 0)
                ParsecVddNative.RemoveDisplay(_handle, _displayIndex);
            else
                ParsecVddNative.Update(_handle);
        }
        catch { /* ignore */ }
        finally
        {
            ParsecVddNative.CloseDevice(_handle);
            ActiveMonitor = null;
        }
    }
}

public readonly record struct ParsecVirtualDisplayMonitor(
    IntPtr Handle,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

[SupportedOSPlatform("windows")]
public static class ParsecVirtualDisplayMonitorHelper
{
    private const int MonitorInfoF = 0x00000010;

    public static bool IsParsecDisplayDeviceName(string? deviceName) =>
        !string.IsNullOrWhiteSpace(deviceName) &&
        IsParsecMonitorDeviceName(deviceName);

    public static IReadOnlyList<ParsecVirtualDisplayMonitor> EnumerateParsecMonitors()
    {
        var monitors = new List<ParsecVirtualDisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return monitors;

        bool Callback(IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info))
                return true;

            if (!IsParsecMonitorDeviceName(info.DeviceName))
                return true;

            monitors.Add(new ParsecVirtualDisplayMonitor(
                monitor,
                info.DeviceName,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top,
                (info.Flags & 1) != 0));
            return true;
        }
    }

    private static bool IsParsecMonitorDeviceName(string monitorDeviceName)
    {
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        if (!EnumDisplayDevices(monitorDeviceName, 0, ref device, 0))
            return false;

        return ContainsParsecMarker(device.DeviceString)
               || ContainsParsecMarker(device.DeviceID)
               || ContainsParsecMarker(device.DeviceKey);
    }

    private static bool ContainsParsecMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
         || value.Contains("ParsecVDA", StringComparison.OrdinalIgnoreCase)
         || value.Contains("PSCCDD", StringComparison.OrdinalIgnoreCase));

    public static ParsecVirtualDisplayMonitor? TryGetNewestParsecMonitor(IReadOnlyList<ParsecVirtualDisplayMonitor>? before)
    {
        var current = EnumerateParsecMonitors();
        if (current.Count == 0)
            return null;

        if (before == null || before.Count == 0)
            return current[^1];

        foreach (var monitor in current)
        {
            if (before.All(existing => existing.Handle != monitor.Handle))
                return monitor;
        }

        return current[^1];
    }

    public static int? TryGetDisplayMonitorIndex(IntPtr monitorHandle)
    {
        if (monitorHandle == IntPtr.Zero)
            return null;

        var search = new MonitorIndexSearch(monitorHandle);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, search.Callback, IntPtr.Zero);
        return search.FoundIndex;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private sealed class MonitorIndexSearch(IntPtr target)
    {
        private int _index;
        public int? FoundIndex { get; private set; }

        public bool Callback(IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___)
        {
            if (monitor == target)
                FoundIndex = _index;
            _index++;
            return true;
        }
    }
}
