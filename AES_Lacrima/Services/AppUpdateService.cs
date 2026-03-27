using AES_Core.DI;
using AES_Core.IO;
using AES_Core.Services;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services;

public sealed record AppReleaseAssetInfo(string Name, string DownloadUrl, long? Size);

public sealed record AppReleaseInfo(
    string TagName,
    string Version,
    string ReleasePageUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<AppReleaseAssetInfo> Assets,
    AppReleaseAssetInfo? SelectedAsset = null);

[AutoRegister]
public partial class AppUpdateService : ObservableObject
{
    private const string Repo = "aruantec/AES_Lacrima";
    private const string UpdaterLogFileName = "updater.log";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<AppUpdateService>();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
#if NATIVE_AOT
    private const bool DefaultPreferAotUpdates = true;
#else
    private const bool DefaultPreferAotUpdates = false;
#endif

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppReleaseInfo? _availableRelease;

    private enum UpdateTargetKind
    {
        Unsupported,
        DirectoryContents,
        MacBundle,
        LinuxAppImage
    }

    private sealed record InstallTarget(
        UpdateTargetKind Kind,
        string TargetPath,
        string RestartPath,
        bool CanSelfUpdate,
        string? UnsupportedReason);

    public AppUpdateService()
    {
        CurrentVersion = DetectCurrentVersion();
        var target = ResolveInstallTarget();
        CanSelfUpdate = target.CanSelfUpdate;
        Status = target.UnsupportedReason ?? $"Installed version: {CurrentVersion}.";
    }

    public AppReleaseInfo? AvailableRelease
    {
        get => _availableRelease;
        private set
        {
            if (EqualityComparer<AppReleaseInfo?>.Default.Equals(_availableRelease, value))
                return;

            _availableRelease = value;
            OnPropertyChanged(nameof(AvailableRelease));
        }
    }

    [ObservableProperty]
    private string _currentVersion = "0.0.0-local";

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private string _status = "Idle.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _canSelfUpdate;

    [ObservableProperty]
    private string? _availableAssetName;

    [ObservableProperty]
    private string? _latestReleaseUrl;

    [ObservableProperty]
    private bool _preferAotUpdates = DefaultPreferAotUpdates;

    public bool IsCurrentBuildAot
    {
        get
        {
#if NATIVE_AOT
            return true;
#else
            return false;
#endif
        }
    }

    public string CurrentBuildFlavorLabel => IsCurrentBuildAot ? "AOT" : "Non-AOT";

    public string CurrentVersionDisplay => $"{CurrentVersion} ({CurrentBuildFlavorLabel})";

    public string PreferredUpdateFlavorLabel => PreferAotUpdates ? "AOT" : "Non-AOT";

    public bool IsPreferredBuildFlavorInstalled => PreferAotUpdates == IsCurrentBuildAot;

    public async Task<AppReleaseInfo?> CheckForUpdatesAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            IsBusy = true;
            IsChecking = true;
            IsUpdateAvailable = false;
            AvailableAssetName = null;
            AvailableRelease = null;

            var installTarget = ResolveInstallTarget();
            CanSelfUpdate = installTarget.CanSelfUpdate;
            Status = "Checking for application updates...";
            WriteDiagnosticLog(
                "Starting update check",
                $"CurrentVersion={CurrentVersionDisplay}",
                $"PreferAotUpdates={PreferAotUpdates}",
                $"CanSelfUpdate={installTarget.CanSelfUpdate}",
                $"TargetKind={installTarget.Kind}",
                $"TargetPath={installTarget.TargetPath}",
                $"RestartPath={installTarget.RestartPath}",
                $"UnsupportedReason={installTarget.UnsupportedReason ?? "<none>"}");

            var release = await FetchLatestReleaseAsync().ConfigureAwait(false);
            if (release == null)
            {
                WriteDiagnosticLog("Update check returned no release metadata.");
                Status = "Unable to check for application updates.";
                return null;
            }

            LatestVersion = release.Version;
            LatestReleaseUrl = release.ReleasePageUrl;

            var selectedAsset = SelectBestAsset(release.Assets, installTarget.Kind, PreferAotUpdates);
            WriteDiagnosticLog(
                "Fetched latest release",
                $"ReleaseVersion={release.Version}",
                $"ReleaseTag={release.TagName}",
                $"PublishedAt={release.PublishedAt?.ToString("O") ?? "<none>"}",
                $"AssetCount={release.Assets.Count}",
                $"SelectedAsset={selectedAsset?.Name ?? "<none>"}");
            if (selectedAsset == null)
            {
                var versionComparison = CompareSemanticVersions(release.Version, CurrentVersion);
                Status = versionComparison > 0
                    ? $"Version {release.Version} is available, but there is no compatible {PreferredUpdateFlavorLabel} download for this platform."
                    : $"AES Lacrima is up to date ({CurrentVersionDisplay}).";
                return null;
            }

            var isNewerVersionAvailable = CompareSemanticVersions(release.Version, CurrentVersion) > 0;
            var isPreferredFlavorSwitchAvailable =
                CompareSemanticVersions(release.Version, CurrentVersion) == 0 &&
                !IsPreferredBuildFlavorInstalled &&
                MatchesPreferredBuildFlavor(selectedAsset.Name, PreferAotUpdates);

            if (!isNewerVersionAvailable && !isPreferredFlavorSwitchAvailable)
            {
                Status = $"AES Lacrima is up to date ({CurrentVersionDisplay}).";
                return null;
            }

            AvailableAssetName = selectedAsset.Name;
            if (!installTarget.CanSelfUpdate)
            {
                Status = installTarget.UnsupportedReason
                    ?? (isPreferredFlavorSwitchAvailable
                        ? $"{PreferredUpdateFlavorLabel} build {release.Version} is available, but this installation cannot update itself."
                        : $"Version {release.Version} is available, but this installation cannot update itself.");
                return null;
            }

            AvailableRelease = release with { SelectedAsset = selectedAsset };
            IsUpdateAvailable = true;
            Status = isPreferredFlavorSwitchAvailable
                ? $"{PreferredUpdateFlavorLabel} build {release.Version} is available."
                : $"Version {release.Version} is available.";
            return AvailableRelease;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to check for application updates", ex);
            Status = $"Update check failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsChecking = false;
            IsBusy = false;
            _gate.Release();
        }
    }

    public async Task<bool> DownloadAndRestartToApplyUpdateAsync(AppReleaseInfo release)
    {
        if (release.SelectedAsset == null)
        {
            Status = "The selected release does not contain a compatible download.";
            return false;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var installTarget = ResolveInstallTarget();
            CanSelfUpdate = installTarget.CanSelfUpdate;
            if (!installTarget.CanSelfUpdate)
            {
                Status = installTarget.UnsupportedReason ?? "This installation cannot update itself.";
                return false;
            }

            IsBusy = true;
            IsDownloading = true;
            DownloadProgress = 0;
            Status = $"Downloading {release.SelectedAsset.Name}...";
            WriteDiagnosticLog(
                "Starting update download/apply",
                $"ReleaseVersion={release.Version}",
                $"AssetName={release.SelectedAsset.Name}",
                $"AssetUrl={release.SelectedAsset.DownloadUrl}",
                $"CurrentVersion={CurrentVersionDisplay}",
                $"TargetKind={installTarget.Kind}",
                $"TargetPath={installTarget.TargetPath}",
                $"RestartPath={installTarget.RestartPath}");

            var stagingRoot = Path.Combine(ApplicationPaths.UpdatesDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);

            var downloadPath = Path.Combine(stagingRoot, release.SelectedAsset.Name);
            await DownloadFileAsync(release.SelectedAsset.DownloadUrl, downloadPath).ConfigureAwait(false);
            WriteDiagnosticLog(
                "Download completed",
                $"DownloadPath={downloadPath}",
                $"StagingRoot={stagingRoot}");

            Status = "Preparing update...";
            var preparedSource = PrepareStagedPayload(downloadPath, stagingRoot, release.SelectedAsset, installTarget);
            var scriptPath = CreateUpdateScript(installTarget, preparedSource, stagingRoot);
            WriteDiagnosticLog(
                "Prepared update payload",
                $"PreparedSource={preparedSource}",
                $"ScriptPath={scriptPath}");
            LaunchUpdateScript(scriptPath);

            DiLocator.ResolveViewModel<SettingsService>()?.SaveSettings();
            Status = "Restarting to apply update...";
            App.IsSelfUpdating = true;
            WriteDiagnosticLog("Update helper launched successfully. Requesting app shutdown.");
            ShutdownApplication();
            return true;
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog($"Update apply failed: {ex}");
            Log.Error("Failed to download or stage application update", ex);
            Status = $"App update failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsDownloading = false;
            IsBusy = false;
            if (!OperatingSystem.IsWindows())
                App.IsSelfUpdating = false;
        }
    }

    public void DismissAvailableUpdate()
    {
        AvailableRelease = null;
        IsUpdateAvailable = false;

        if (!string.IsNullOrWhiteSpace(LatestVersion))
        {
            Status = $"Version {LatestVersion} is available.";
        }
        else
        {
            Status = $"Installed version: {CurrentVersionDisplay}.";
        }
    }

    partial void OnCurrentVersionChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentVersionDisplay));
        OnPropertyChanged(nameof(IsPreferredBuildFlavorInstalled));
    }

    partial void OnPreferAotUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(PreferredUpdateFlavorLabel));
        OnPropertyChanged(nameof(IsPreferredBuildFlavorInstalled));
        ReevaluateAvailableReleaseForPreferredFlavor();
    }

    private static string DetectCurrentVersion()
    {
        if (OperatingSystem.IsLinux())
        {
            var linuxPackageVersion = TryGetCurrentLinuxPackageVersion();
            if (!string.IsNullOrWhiteSpace(linuxPackageVersion))
                return linuxPackageVersion;
        }

        var entryAssembly = Assembly.GetEntryAssembly();

        var informational = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var normalizedInformational = NormalizeVersionString(informational);
        if (!string.IsNullOrWhiteSpace(normalizedInformational))
            return normalizedInformational;

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(processPath);
                var productVersion = NormalizeVersionString(versionInfo.ProductVersion);
                if (!string.IsNullOrWhiteSpace(productVersion))
                    return productVersion;

                var fileVersion = NormalizeVersionString(versionInfo.FileVersion);
                if (!string.IsNullOrWhiteSpace(fileVersion))
                    return fileVersion;
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to read file version for the running executable", ex);
            }
        }

        return NormalizeVersionString(entryAssembly?.GetName().Version?.ToString()) ?? "0.0.0-local";
    }

    private static string? TryGetCurrentLinuxPackageVersion()
    {
        try
        {
            var appDir = Environment.GetEnvironmentVariable("APPDIR");
            if (!string.IsNullOrWhiteSpace(appDir))
            {
                var explicitVersionPath = Path.Combine(appDir, "usr", "share", "aes-lacrima", "version.txt");
                if (File.Exists(explicitVersionPath))
                {
                    var explicitVersion = NormalizeVersionString(File.ReadAllText(explicitVersionPath));
                    if (!string.IsNullOrWhiteSpace(explicitVersion))
                        return explicitVersion;
                }
            }

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                var siblingVersionFile = Path.Combine(Path.GetDirectoryName(processPath)!, "version.txt");
                if (File.Exists(siblingVersionFile))
                {
                    var explicitVersion = NormalizeVersionString(File.ReadAllText(siblingVersionFile));
                    if (!string.IsNullOrWhiteSpace(explicitVersion))
                        return explicitVersion;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to read current Linux package version metadata", ex);
            return null;
        }

        return null;
    }

    private static string? NormalizeVersionString(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var normalized = version.Trim();
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
            normalized = normalized[..plusIndex];

        var gitIndex = normalized.IndexOf('+');
        if (gitIndex >= 0)
            normalized = normalized[..gitIndex];

        normalized = normalized.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static InstallTarget ResolveInstallTarget()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return new InstallTarget(
                UpdateTargetKind.Unsupported,
                string.Empty,
                string.Empty,
                false,
                "Unable to determine the current application path for self-update.");
        }

        if (OperatingSystem.IsWindows())
        {
            var canWrite = ApplicationPaths.IsAppBaseWritable();
            return new InstallTarget(
                UpdateTargetKind.DirectoryContents,
                AppContext.BaseDirectory,
                processPath,
                canWrite,
                canWrite ? null : "The app is installed in a protected directory, so self-update is disabled.");
        }

        if (OperatingSystem.IsMacOS())
        {
            var appBundlePath = FindMacAppBundlePath(processPath);
            if (string.IsNullOrWhiteSpace(appBundlePath))
            {
                return new InstallTarget(
                    UpdateTargetKind.Unsupported,
                    string.Empty,
                    string.Empty,
                    false,
                    "macOS self-update currently requires running the packaged .app build.");
            }

            var canWrite = IsDirectoryWritable(Path.GetDirectoryName(appBundlePath)!);
            return new InstallTarget(
                UpdateTargetKind.MacBundle,
                appBundlePath,
                appBundlePath,
                canWrite,
                canWrite ? null : "The application bundle location is not writable, so self-update is disabled.");
        }

        if (OperatingSystem.IsLinux())
        {
            var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrWhiteSpace(appImagePath) && processPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            {
                appImagePath = processPath;
            }

            if (string.IsNullOrWhiteSpace(appImagePath))
            {
                return new InstallTarget(
                    UpdateTargetKind.Unsupported,
                    string.Empty,
                    string.Empty,
                    false,
                    "Linux self-update currently requires running the packaged AppImage build.");
            }

            var canWrite = IsDirectoryWritable(Path.GetDirectoryName(appImagePath)!);
            return new InstallTarget(
                UpdateTargetKind.LinuxAppImage,
                appImagePath,
                appImagePath,
                canWrite,
                canWrite ? null : "The AppImage location is not writable, so self-update is disabled.");
        }

        return new InstallTarget(
            UpdateTargetKind.Unsupported,
            string.Empty,
            string.Empty,
            false,
            "This platform does not currently support self-update.");
    }

    private static string? FindMacAppBundlePath(string processPath)
    {
        var current = Path.GetDirectoryName(processPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (current.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return current;

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var probePath = Path.Combine(directoryPath, $".write-test-{Guid.NewGuid():N}");
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<AppReleaseInfo?> FetchLatestReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; AES-Lacrima-Updater)");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = document.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? string.Empty : string.Empty;
        var htmlUrl = root.TryGetProperty("html_url", out var htmlNode) ? htmlNode.GetString() ?? string.Empty : string.Empty;
        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var publishedNode)
            && DateTimeOffset.TryParse(publishedNode.GetString(), out var parsedPublished))
        {
            publishedAt = parsedPublished;
        }

        var assets = new List<AppReleaseAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsNode) && assetsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var assetNode in assetsNode.EnumerateArray())
            {
                var name = assetNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
                var downloadUrl = assetNode.TryGetProperty("browser_download_url", out var urlNode) ? urlNode.GetString() ?? string.Empty : string.Empty;
                long? size = null;
                if (assetNode.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsedSize))
                    size = parsedSize;

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(downloadUrl))
                    assets.Add(new AppReleaseAssetInfo(name, downloadUrl, size));
            }
        }

        return new AppReleaseInfo(
            TagName: tagName,
            Version: NormalizeVersionString(tagName) ?? tagName,
            ReleasePageUrl: htmlUrl,
            PublishedAt: publishedAt,
            Assets: assets);
    }

    private void ReevaluateAvailableReleaseForPreferredFlavor()
    {
        if (AvailableRelease == null)
            return;

        var installTarget = ResolveInstallTarget();
        var selectedAsset = SelectBestAsset(AvailableRelease.Assets, installTarget.Kind, PreferAotUpdates);
        AvailableAssetName = selectedAsset?.Name;

        if (selectedAsset == null)
        {
            AvailableRelease = null;
            IsUpdateAvailable = false;
            var version = LatestVersion ?? CurrentVersion;
            Status = $"Version {version} is available, but there is no compatible {PreferredUpdateFlavorLabel} download for this platform.";
            return;
        }

        AvailableRelease = AvailableRelease with { SelectedAsset = selectedAsset };
        Status = CompareSemanticVersions(AvailableRelease.Version, CurrentVersion) == 0 && !IsPreferredBuildFlavorInstalled
            ? $"{PreferredUpdateFlavorLabel} build {AvailableRelease.Version} is available."
            : $"Version {AvailableRelease.Version} is available.";
    }

    public bool IsFlavorSwitchRelease(AppReleaseInfo release) =>
        CompareSemanticVersions(release.Version, CurrentVersion) == 0 &&
        !IsPreferredBuildFlavorInstalled &&
        release.SelectedAsset is { } asset &&
        MatchesPreferredBuildFlavor(asset.Name, PreferAotUpdates);

    private static AppReleaseAssetInfo? SelectBestAsset(
        IReadOnlyList<AppReleaseAssetInfo> assets,
        UpdateTargetKind targetKind,
        bool preferAotUpdates)
    {
        if (assets.Count == 0)
            return null;

        return targetKind switch
        {
            UpdateTargetKind.DirectoryContents => ScoreAssets(
                assets,
                asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && MatchesPreferredBuildFlavor(asset.Name, preferAotUpdates),
                RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? ["windows", "x64"]
                    : ["windows", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()]),
            UpdateTargetKind.MacBundle => RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => ScoreAssets(assets, asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && MatchesPreferredBuildFlavor(asset.Name, preferAotUpdates), ["macos", "arm64"]),
                Architecture.X64 => ScoreAssets(assets, asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && MatchesPreferredBuildFlavor(asset.Name, preferAotUpdates), ["macos", "x64"]),
                _ => null
            },
            UpdateTargetKind.LinuxAppImage => RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => ScoreAssets(assets, asset => asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) && MatchesPreferredBuildFlavor(asset.Name, preferAotUpdates), ["aarch64", "arm64"]),
                Architecture.X64 => ScoreAssets(assets, asset => asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) && MatchesPreferredBuildFlavor(asset.Name, preferAotUpdates), ["x86_64", "x64"]),
                _ => null
            },
            _ => null
        };
    }

    private static bool MatchesPreferredBuildFlavor(string assetName, bool preferAotUpdates)
    {
        var isAotAsset = assetName.Contains("-AOT", StringComparison.OrdinalIgnoreCase);
        return preferAotUpdates ? isAotAsset : !isAotAsset;
    }

    private static AppReleaseAssetInfo? ScoreAssets(
        IReadOnlyList<AppReleaseAssetInfo> assets,
        Func<AppReleaseAssetInfo, bool> extensionPredicate,
        IReadOnlyList<string> preferredTokens)
    {
        var candidates = assets.Where(extensionPredicate).ToList();
        if (candidates.Count == 0)
            return null;

        var scored = candidates
            .Select(asset => new
            {
                Asset = asset,
                Score = preferredTokens.Sum(token => asset.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ? 10 : 0)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return scored?.Score > 0 ? scored.Asset : null;
    }

    private async Task DownloadFileAsync(string url, string destinationPath)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var destinationStream = File.Create(destinationPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        while (true)
        {
            var read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            if (read == 0)
                break;

            await destinationStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            totalRead += read;

            if (totalBytes > 0)
            {
                DownloadProgress = (double)totalRead / totalBytes * 100.0;
            }
        }

        if (totalBytes <= 0)
            DownloadProgress = 100.0;
    }

    private static string PrepareStagedPayload(
        string downloadPath,
        string stagingRoot,
        AppReleaseAssetInfo selectedAsset,
        InstallTarget installTarget)
    {
        if (installTarget.Kind == UpdateTargetKind.DirectoryContents)
        {
            var extractDirectory = Path.Combine(stagingRoot, "extracted");
            ZipFile.ExtractToDirectory(downloadPath, extractDirectory, overwriteFiles: true);
            return extractDirectory;
        }

        if (installTarget.Kind == UpdateTargetKind.MacBundle)
        {
            var extractDirectory = Path.Combine(stagingRoot, "extracted");
            ZipFile.ExtractToDirectory(downloadPath, extractDirectory, overwriteFiles: true);
            var appBundle = Directory.EnumerateDirectories(extractDirectory, "*.app", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(appBundle))
                throw new InvalidOperationException("The downloaded macOS archive does not contain an application bundle.");

            return appBundle;
        }

        if (installTarget.Kind == UpdateTargetKind.LinuxAppImage)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(downloadPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to set executable permissions on the staged Linux update", ex);
            }

            return downloadPath;
        }

        throw new InvalidOperationException($"Unsupported update asset '{selectedAsset.Name}' for this installation.");
    }

    private static string CreateUpdateScript(InstallTarget installTarget, string preparedSource, string stagingRoot)
    {
        return installTarget.Kind switch
        {
            UpdateTargetKind.DirectoryContents => CreateWindowsUpdateScript(preparedSource, installTarget.TargetPath, installTarget.RestartPath, stagingRoot),
            UpdateTargetKind.MacBundle => CreateMacUpdateScript(preparedSource, installTarget.TargetPath, stagingRoot),
            UpdateTargetKind.LinuxAppImage => CreateLinuxUpdateScript(preparedSource, installTarget.TargetPath, stagingRoot),
            _ => throw new InvalidOperationException("This installation does not support self-update.")
        };
    }

    private static string CreateWindowsUpdateScript(string sourceDirectory, string targetDirectory, string restartExecutable, string stagingRoot)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-lacrima-update-{Guid.NewGuid():N}.cmd");
        var helperLogPath = Path.Combine(ApplicationPaths.UpdaterLogsDirectory, $"helper-{Environment.ProcessId}.log");
        var helperLogDirectory = ApplicationPaths.UpdaterLogsDirectory;
        var pid = Environment.ProcessId;
        var script = $$"""
            @echo off
            setlocal
            set "PID={{pid}}"
            set "SRC={{EscapeBatchValue(sourceDirectory)}}"
            set "DST={{EscapeBatchValue(targetDirectory)}}"
            set "EXE={{EscapeBatchValue(restartExecutable)}}"
            set "STAGING={{EscapeBatchValue(stagingRoot)}}"
            set "LOGDIR={{EscapeBatchValue(helperLogDirectory)}}"
            set "LOG={{EscapeBatchValue(helperLogPath)}}"
            set "TASKLIST=%SystemRoot%\System32\tasklist.exe"
            set "FIND=%SystemRoot%\System32\find.exe"
            set "TIMEOUT=%SystemRoot%\System32\timeout.exe"
            set "ROBOCOPY=%SystemRoot%\System32\robocopy.exe"
            if not exist "%LOGDIR%" mkdir "%LOGDIR%" >NUL 2>NUL
            (
                echo ==== %DATE% %TIME% helper start ====
                echo PID=%PID%
                echo SRC=%SRC%
                echo DST=%DST%
                echo EXE=%EXE%
                echo STAGING=%STAGING%
                echo SystemRoot=%SystemRoot%
                echo PATH=%PATH%
            )>>"%LOG%"
            :wait_for_exit
            "%TASKLIST%" /FI "PID eq %PID%" 2>NUL | "%FIND%" "%PID%" >NUL
            if not errorlevel 1 (
                >>"%LOG%" echo waiting for PID %PID% to exit
                "%TIMEOUT%" /t 1 /nobreak >NUL
                goto wait_for_exit
            )
            >>"%LOG%" echo process exited, starting robocopy
            "%ROBOCOPY%" "%SRC%" "%DST%" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >>"%LOG%" 2>&1
            if errorlevel 8 goto copy_failed
            >>"%LOG%" echo copy succeeded, restarting app
            start "" /D "%DST%" "%EXE%"
            rmdir /S /Q "%STAGING%" >NUL 2>NUL
            >>"%LOG%" echo cleanup finished
            del "%~f0"
            exit /b 0
            :copy_failed
            >>"%LOG%" echo copy failed with errorlevel %errorlevel%
            start "" /D "%DST%" "%EXE%"
            exit /b 1
            """;

        File.WriteAllText(scriptPath, script, Encoding.ASCII);
        return scriptPath;
    }

    private static string CreateMacUpdateScript(string sourceAppBundle, string targetAppBundle, string stagingRoot)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-lacrima-update-{Guid.NewGuid():N}.sh");
        var helperLogPath = Path.Combine(ApplicationPaths.UpdaterLogsDirectory, $"helper-{Environment.ProcessId}.log");
        var pid = Environment.ProcessId;
        var script = $$"""
            #!/bin/sh
            PID={{pid}}
            SOURCE_APP='{{EscapeShellValue(sourceAppBundle)}}'
            TARGET_APP='{{EscapeShellValue(targetAppBundle)}}'
            STAGING='{{EscapeShellValue(stagingRoot)}}'
            LOG='{{EscapeShellValue(helperLogPath)}}'
            mkdir -p "$(dirname "$LOG")"
            {
                echo "==== $(date '+%Y-%m-%dT%H:%M:%S%z') helper start ===="
                echo "PID=$PID"
                echo "SOURCE_APP=$SOURCE_APP"
                echo "TARGET_APP=$TARGET_APP"
                echo "STAGING=$STAGING"
            } >>"$LOG"
            while kill -0 "$PID" 2>/dev/null; do
                echo "waiting for PID $PID to exit" >>"$LOG"
                sleep 1
            done
            echo "process exited, replacing app bundle" >>"$LOG"
            TMP_APP="${TARGET_APP}.new"
            rm -rf "$TMP_APP"
            cp -R "$SOURCE_APP" "$TMP_APP"
            rm -rf "$TARGET_APP"
            mv "$TMP_APP" "$TARGET_APP"
            echo "replacement finished, relaunching app bundle" >>"$LOG"
            open -n "$TARGET_APP" >/dev/null 2>&1 &
            rm -rf "$STAGING"
            echo "cleanup finished" >>"$LOG"
            rm -- "$0"
            """;

        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            SetUnixExecutablePermissions(scriptPath);
        return scriptPath;
    }

    private static string CreateLinuxUpdateScript(string sourceFile, string targetFile, string stagingRoot)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-lacrima-update-{Guid.NewGuid():N}.sh");
        var helperLogPath = Path.Combine(ApplicationPaths.UpdaterLogsDirectory, $"helper-{Environment.ProcessId}.log");
        var pid = Environment.ProcessId;
        var script = $$"""
            #!/bin/sh
            PID={{pid}}
            SOURCE_FILE='{{EscapeShellValue(sourceFile)}}'
            TARGET_FILE='{{EscapeShellValue(targetFile)}}'
            STAGING='{{EscapeShellValue(stagingRoot)}}'
            LOG='{{EscapeShellValue(helperLogPath)}}'
            mkdir -p "$(dirname "$LOG")"
            {
                echo "==== $(date '+%Y-%m-%dT%H:%M:%S%z') helper start ===="
                echo "PID=$PID"
                echo "SOURCE_FILE=$SOURCE_FILE"
                echo "TARGET_FILE=$TARGET_FILE"
                echo "STAGING=$STAGING"
            } >>"$LOG"
            while kill -0 "$PID" 2>/dev/null; do
                echo "waiting for PID $PID to exit" >>"$LOG"
                sleep 1
            done
            echo "process exited, replacing AppImage" >>"$LOG"
            chmod +x "$SOURCE_FILE"
            cp "$SOURCE_FILE" "${TARGET_FILE}.new"
            chmod +x "${TARGET_FILE}.new"
            mv "${TARGET_FILE}.new" "$TARGET_FILE"
            echo "replacement finished, relaunching AppImage" >>"$LOG"
            nohup "$TARGET_FILE" >/dev/null 2>&1 &
            rm -rf "$STAGING"
            echo "cleanup finished" >>"$LOG"
            rm -- "$0"
            """;

        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            SetUnixExecutablePermissions(scriptPath);
        return scriptPath;
    }

    private static void LaunchUpdateScript(string scriptPath)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            var commandInterpreter = GetWindowsCommandInterpreterPath();
            startInfo = new ProcessStartInfo
            {
                FileName = commandInterpreter,
                Arguments = $"/d /c call \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };
            WriteDiagnosticLog(
                "Launching Windows update helper",
                $"CommandInterpreter={commandInterpreter}",
                $"ScriptPath={scriptPath}");
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };
            WriteDiagnosticLog(
                "Launching POSIX update helper",
                $"Shell=/bin/sh",
                $"ScriptPath={scriptPath}");
        }

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to launch the update helper process.");
    }

    private static void ShutdownApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            if (OperatingSystem.IsWindows())
                Environment.Exit(0);
            return;
        }

        Environment.Exit(0);
    }

    private static string EscapeBatchValue(string value)
        => value.Replace("%", "%%", StringComparison.Ordinal);

    private static string EscapeShellValue(string value)
        => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void SetUnixExecutablePermissions(string path)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string GetWindowsCommandInterpreterPath()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
            return comSpec;

        var systemDirectory = Environment.SystemDirectory;
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            var cmdPath = Path.Combine(systemDirectory, "cmd.exe");
            if (File.Exists(cmdPath))
                return cmdPath;
        }

        return "cmd.exe";
    }

    private static void WriteDiagnosticLog(string message, params string[] details)
    {
        try
        {
            Directory.CreateDirectory(ApplicationPaths.UpdaterLogsDirectory);
            var logPath = Path.Combine(ApplicationPaths.UpdaterLogsDirectory, UpdaterLogFileName);
            using var writer = new StreamWriter(logPath, append: true, Encoding.UTF8);
            writer.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
            foreach (var detail in details)
            {
                writer.WriteLine($"  {detail}");
            }
        }
        catch
        {
            // Diagnostics should never interfere with app behavior.
        }
    }

    private static int CompareSemanticVersions(string left, string right)
    {
        var leftParsed = SemanticVersion.Parse(left);
        var rightParsed = SemanticVersion.Parse(right);
        return leftParsed.CompareTo(rightParsed);
    }

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease, string? RevisionSuffix) : IComparable<SemanticVersion>
    {
        public static SemanticVersion Parse(string value)
        {
            var normalized = NormalizeVersionString(value) ?? "0.0.0";
            var dashIndex = normalized.IndexOf('-');
            var core = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
            var preRelease = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : null;

            var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var major = parts.Length > 0 ? ParseIntegerToken(parts[0]) : 0;
            var minor = parts.Length > 1 ? ParseIntegerToken(parts[1]) : 0;
            var (patch, revisionSuffix) = parts.Length > 2 ? ParsePatchToken(parts[2]) : (0, null);
            if (string.IsNullOrWhiteSpace(revisionSuffix) && IsSingleLetterRevision(preRelease))
            {
                revisionSuffix = preRelease;
                preRelease = null;
            }
            return new SemanticVersion(major, minor, patch, preRelease, revisionSuffix);
        }

        public int CompareTo(SemanticVersion other)
        {
            var majorCompare = Major.CompareTo(other.Major);
            if (majorCompare != 0) return majorCompare;

            var minorCompare = Minor.CompareTo(other.Minor);
            if (minorCompare != 0) return minorCompare;

            var patchCompare = Patch.CompareTo(other.Patch);
            if (patchCompare != 0) return patchCompare;

            if (string.IsNullOrWhiteSpace(PreRelease) && string.IsNullOrWhiteSpace(other.PreRelease))
                return CompareRevisionSuffix(RevisionSuffix, other.RevisionSuffix);
            if (string.IsNullOrWhiteSpace(PreRelease))
                return 1;
            if (string.IsNullOrWhiteSpace(other.PreRelease))
                return -1;

            var preReleaseCompare = ComparePreRelease(PreRelease!, other.PreRelease!);
            if (preReleaseCompare != 0)
                return preReleaseCompare;

            return CompareRevisionSuffix(RevisionSuffix, other.RevisionSuffix);
        }

        private static int ComparePreRelease(string left, string right)
        {
            var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var max = Math.Max(leftParts.Length, rightParts.Length);

            for (var i = 0; i < max; i++)
            {
                if (i >= leftParts.Length) return -1;
                if (i >= rightParts.Length) return 1;

                var leftPart = leftParts[i];
                var rightPart = rightParts[i];
                var leftIsNumeric = int.TryParse(leftPart, out var leftNumber);
                var rightIsNumeric = int.TryParse(rightPart, out var rightNumber);

                if (leftIsNumeric && rightIsNumeric)
                {
                    var numberCompare = leftNumber.CompareTo(rightNumber);
                    if (numberCompare != 0) return numberCompare;
                    continue;
                }

                if (leftIsNumeric != rightIsNumeric)
                    return leftIsNumeric ? -1 : 1;

                var stringCompare = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                if (stringCompare != 0) return stringCompare;
            }

            return 0;
        }

        private static int CompareRevisionSuffix(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
                return 0;
            if (string.IsNullOrWhiteSpace(left))
                return -1;
            if (string.IsNullOrWhiteSpace(right))
                return 1;

            return ComparePreRelease(left!, right!);
        }

        private static bool IsSingleLetterRevision(string? token)
        {
            return !string.IsNullOrWhiteSpace(token)
                && token!.Length == 1
                && char.IsLetter(token[0]);
        }

        private static int ParseIntegerToken(string token)
        {
            return int.TryParse(token, out var parsed) ? parsed : 0;
        }

        private static (int Number, string? Suffix) ParsePatchToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return (0, null);

            var index = 0;
            while (index < token.Length && char.IsDigit(token[index]))
                index++;

            var number = index > 0 && int.TryParse(token[..index], out var parsed) ? parsed : 0;
            var suffix = index < token.Length ? token[index..].TrimStart('.', '-', '_') : null;
            return (number, string.IsNullOrWhiteSpace(suffix) ? null : suffix);
        }
    }
}
