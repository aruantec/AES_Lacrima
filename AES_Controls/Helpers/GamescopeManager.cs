using AES_Core.DI;
using AES_Core.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using log4net;

using AES_Core.Logging;

namespace AES_Controls.Helpers;

/// <summary>
/// Detects and manages the gamescope compositor used to launch Linux emulators.
/// </summary>
[AutoRegister]
public partial class GamescopeManager : ObservableObject
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<GamescopeManager>();
    private const string Repo = "ValveSoftware/gamescope";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly Regex SemverTagRegex = new(@"^\d+\.\d+(?:\.\d+){1,2}$", RegexOptions.CultureInvariant);
    private static string? _resolvedPathCache;
    private static GamescopeCacheEntry? _cache;
    private static readonly string CachePath = Path.Combine(ApplicationPaths.DataRootDirectory, "gamescope_cache.json");
    private int _lastExitCode;
    private string? _lastCommandError;
    private string? _resolvedInstallLogPath;

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private bool _isBusy;

    public string InstallLogPath => _resolvedInstallLogPath ?? Path.Combine(ApplicationPaths.LogsDirectory, "gamescope-build.log");

    public event EventHandler<InstallationCompletedEventArgs>? InstallationCompleted;

    public static bool IsSupported => OperatingSystem.IsLinux();

    public const string AptSourceBuildDependencyList =
        "git meson ninja-build pkg-config python3 cmake wayland-protocols " +
        "libdbus-1-dev libdrm-dev libinput-dev libpipewire-0.3-dev libudev-dev " +
        "libx11-dev libx11-xcb-dev libxcb-composite0-dev libxcb-ewmh-dev libxcb-icccm4-dev " +
        "libxcb-randr0-dev libxcb-res0-dev libxcb-util-dev libxcb-xfixes0-dev libxcb-xkb-dev libxcb1-dev " +
        "libcap-dev libdecor-0-dev libfontconfig-dev libliftoff-dev liblcms2-dev libpango1.0-dev " +
        "libpixman-1-dev libseat-dev libsystemd-dev libvulkan-dev libwayland-dev libxcomposite-dev " +
        "libxcursor-dev libxdamage-dev libxext-dev libxfixes-dev libxi-dev libxkbcommon-dev libxmu-dev " +
        "libxrender-dev libxres-dev libxtst-dev libxxf86vm-dev libxxhash-dev libluajit-5.1-dev " +
        "glslang-tools";

    public const string PacmanSourceBuildDependencyList =
        "base-devel meson ninja git cmake pkgconf python wayland-protocols pipewire libpipewire " +
        "libdrm libinput wayland libx11 libxcb xcb-util xcb-util-wm xcb-util-errors libliftoff libdecor " +
        "libcap systemd libvulkan vulkan-headers libxkbcommon pango pixman fontconfig libxcomposite " +
        "libxcursor libxdamage libxext libxfixes libxi libxrender libxres libxtst libxmu libxxf86vm " +
        "xxhash luajit glslang lcms2 libseat hwdata";

    public static bool IsInstalled => !string.IsNullOrWhiteSpace(ResolveExecutablePath());

    public static void InvalidateResolvedPathCache() => _resolvedPathCache = null;

    public static string? ResolveExecutablePath()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        if (!string.IsNullOrWhiteSpace(_resolvedPathCache) && File.Exists(_resolvedPathCache))
            return _resolvedPathCache;

        var toolsDir = ApplicationPaths.ToolsDirectory;
        var candidates = new[]
        {
            ApplicationPaths.GetToolFile("gamescope"),
            Path.Combine(toolsDir, "bin", "gamescope"),
            Path.Combine(AppContext.BaseDirectory, "gamescope"),
            "/usr/bin/gamescope",
            "/usr/local/bin/gamescope",
            "/usr/games/gamescope",
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    _resolvedPathCache = candidate;
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to probe gamescope candidate path '{candidate}'.", ex);
            }
        }

        var fromPath = ResolveFromPath("gamescope");
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            _resolvedPathCache = fromPath;
            return fromPath;
        }

        _resolvedPathCache = null;
        return null;
    }

    public bool IsAvailable() => IsInstalled;

    public async Task<string?> GetCurrentVersionAsync()
    {
        var path = ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var output = await ExecuteCommandCaptureAsync(path, "--version").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
            output = await ExecuteCommandCaptureAsync(path, "-h").ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output))
            return null;

        var firstLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return ExtractVersionFromText(firstLine ?? output);
    }

    public async Task<List<GamescopeReleaseInfo>> GetAvailableVersionsAsync(bool forceRefresh = false)
    {
        LoadCache();

        if (!forceRefresh &&
            _cache?.Versions is { Count: > 0 } cached &&
            (DateTime.Now - _cache.LastUpdated).TotalMinutes < 15)
        {
            return cached;
        }

        try
        {
            Client.DefaultRequestHeaders.UserAgent.Clear();
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; GamescopeManager; AES_Lacrima)");

            var apiUrl = $"https://api.github.com/repos/{Repo}/tags?per_page=100";
            Log.Debug($"Fetching gamescope tags from {apiUrl}");

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            if (!string.IsNullOrEmpty(_cache?.ETag))
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_cache.ETag));

            using var response = await Client.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                if (_cache != null)
                    _cache.LastUpdated = DateTime.Now;
                return _cache?.Versions ?? [];
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Status = "GitHub API rate limit exceeded. Please wait a few minutes and try again.";
                Log.Warn(Status);
                return _cache?.Versions ?? [];
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);

            var tags = new List<string>();
            foreach (var tagElement in doc.RootElement.EnumerateArray())
            {
                if (!tagElement.TryGetProperty("name", out var nameProp))
                    continue;

                var tag = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(tag) || !SemverTagRegex.IsMatch(tag))
                    continue;

                tags.Add(tag);
            }

            var distroPackages = await GetDistroPackageVersionsAsync().ConfigureAwait(false);
            var latestDistroTag = distroPackages
                .Select(ExtractVersionFromText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .OrderByDescending(ParseVersionKey, Comparer<string>.Create(CompareVersionKeys))
                .FirstOrDefault();

            var versions = new List<GamescopeReleaseInfo>();
            foreach (var tag in tags.OrderByDescending(ParseVersionKey, Comparer<string>.Create(CompareVersionKeys)))
            {
                var packageVersion = FindMatchingDistroPackage(tag, distroPackages);
                if (!string.IsNullOrWhiteSpace(packageVersion))
                {
                    versions.Add(new GamescopeReleaseInfo
                    {
                        Tag = tag,
                        Title = $"{tag} (Distro package)",
                        InstallMethod = GamescopeInstallMethod.DistroPackage,
                        PackageVersion = packageVersion,
                        IsPrerelease = false,
                    });
                    continue;
                }

                var isPrerelease = latestDistroTag == null || CompareVersionKeys(tag, latestDistroTag) > 0;
                versions.Add(new GamescopeReleaseInfo
                {
                    Tag = tag,
                    Title = isPrerelease
                        ? $"{tag} (Pre-release — build from source)"
                        : $"{tag} (Build from source)",
                    InstallMethod = GamescopeInstallMethod.SourceBuild,
                    IsPrerelease = isPrerelease,
                });
            }

            _cache ??= new GamescopeCacheEntry();
            _cache.Versions = versions;
            _cache.ETag = response.Headers.ETag?.Tag;
            _cache.LastUpdated = DateTime.Now;
            SaveCache();

            Log.Info($"Retrieved {versions.Count} installable gamescope versions.");
            return versions;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to fetch gamescope versions from GitHub.", ex);
            if (Status == "Idle")
                Status = "Failed to fetch available gamescope versions.";
            return _cache?.Versions ?? [];
        }
    }

    public async Task<bool> EnsureInstalledAsync()
    {
        if (IsAvailable())
        {
            Status = "gamescope is already installed.";
            return true;
        }

        var versions = await GetAvailableVersionsAsync().ConfigureAwait(false);
        var preferred = versions.FirstOrDefault(v => !v.IsPrerelease) ?? versions.FirstOrDefault();
        return preferred == null
            ? await InstallAsync().ConfigureAwait(false)
            : await InstallVersionAsync(preferred).ConfigureAwait(false);
    }

    public Task<bool> InstallAsync()
        => InstallVersionAsync(null);

    public Task<bool> UpgradeAsync()
        => InstallVersionAsync(null);

    public async Task<bool> InstallVersionAsync(GamescopeReleaseInfo? release)
    {
        if (!IsSupported)
        {
            Status = "gamescope is only available on Linux.";
            return false;
        }

        release ??= (await GetAvailableVersionsAsync()).FirstOrDefault();
        if (release == null)
        {
            Status = "No installable gamescope versions were found.";
            return false;
        }

        IsBusy = true;

        await BeginInstallLogAsync(
            $"gamescope install requested: {release.Tag} ({release.InstallMethod})").ConfigureAwait(true);
        OnPropertyChanged(nameof(InstallLogPath));

        if (release.InstallMethod == GamescopeInstallMethod.DistroPackage)
        {
            var currentVersion = await GetCurrentVersionAsync().ConfigureAwait(true);
            var currentTag = ExtractVersionFromText(currentVersion ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(currentTag) &&
                CompareVersionKeys(currentTag, release.Tag) >= 0)
            {
                Status =
                    $"Distro package {release.Tag} is already installed. Select a pre-release build to compile a newer gamescope.";
                await AppendInstallLogAsync(Status).ConfigureAwait(true);
                IsBusy = false;
                return false;
            }
        }

        Log.Info($"gamescope install requested for {release.Tag} ({release.InstallMethod}).");
        Status = release.InstallMethod == GamescopeInstallMethod.DistroPackage
            ? $"Installing gamescope {release.Tag} from distro packages..."
            : $"Building gamescope {release.Tag} from source (log: {InstallLogPath})...";
        await AppendInstallLogAsync(Status).ConfigureAwait(true);

        try
        {
            var success = release.InstallMethod == GamescopeInstallMethod.DistroPackage
                ? await InstallDistroPackageAsync(release).ConfigureAwait(true)
                : await InstallFromSourceAsync(release.Tag).ConfigureAwait(true);

            if (success)
                InvalidateResolvedPathCache();

            if (success)
            {
                var installedVersion = await GetCurrentVersionAsync().ConfigureAwait(true);
                var installedTag = ExtractVersionFromText(installedVersion ?? string.Empty);
                if (string.IsNullOrWhiteSpace(installedTag) ||
                    CompareVersionKeys(installedTag, release.Tag) != 0)
                {
                    var resolvedPath = ResolveExecutablePath() ?? "unknown";
                    _lastCommandError =
                        $"Installed binary reports {installedTag ?? "unknown"} at {resolvedPath}, expected {release.Tag}.";
                    success = false;
                }
            }

            Status = success
                ? $"gamescope {release.Tag} installed successfully ({ResolveExecutablePath()})."
                : BuildFailureStatus($"gamescope {release.Tag} installation failed");
            await AppendInstallLogAsync(Status).ConfigureAwait(true);
            if (!success && !string.IsNullOrWhiteSpace(_lastCommandError))
                await AppendInstallLogAsync(_lastCommandError).ConfigureAwait(true);

            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(success, Status));
            return success;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BeginInstallLogAsync(string message)
    {
        try
        {
            var logPath = EnsureInstallLogPath();
            await File.WriteAllTextAsync(logPath, $"[{DateTime.Now:u}] {message}{Environment.NewLine}")
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to initialize gamescope install log.", ex);
        }
    }

    private async Task AppendInstallLogAsync(string message)
    {
        try
        {
            var logPath = EnsureInstallLogPath();
            await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:u}] {message}{Environment.NewLine}")
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to append gamescope install log entry.", ex);
        }
    }

    private string EnsureInstallLogPath()
    {
        if (!string.IsNullOrWhiteSpace(_resolvedInstallLogPath))
            return _resolvedInstallLogPath;

        var preferred = Path.Combine(ApplicationPaths.LogsDirectory, "gamescope-build.log");
        try
        {
            Directory.CreateDirectory(ApplicationPaths.LogsDirectory);
            using (File.Open(preferred, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
            }

            _resolvedInstallLogPath = preferred;
            return preferred;
        }
        catch (Exception ex)
        {
            Log.Warn($"gamescope install log is not writable at '{preferred}'. Using temp fallback.", ex);
            _resolvedInstallLogPath = Path.Combine(Path.GetTempPath(), "aes-gamescope-build.log");
            OnPropertyChanged(nameof(InstallLogPath));
            return _resolvedInstallLogPath;
        }
    }

    public async Task<bool> UninstallAsync()
    {
        if (!IsSupported)
        {
            Status = "gamescope is only available on Linux.";
            return false;
        }

        IsBusy = true;
        Status = "Uninstalling gamescope...";

        var managedBinary = ApplicationPaths.GetToolFile("gamescope");
        var success = true;

        if (File.Exists(managedBinary))
        {
            try
            {
                File.Delete(managedBinary);
                foreach (var sibling in new[] { "gamescopereaper", "gamescopestream", "gamescopectl" })
                {
                    var path = ApplicationPaths.GetToolFile(sibling);
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to remove managed gamescope binaries.", ex);
                success = false;
            }
        }

        if (CommandExists("pacman") || CommandExists("apt-get") || CommandExists("dnf") || CommandExists("zypper"))
            success = await RunLinuxUninstallAsync().ConfigureAwait(false) && success;

        if (success)
            InvalidateResolvedPathCache();

        Status = success
            ? "gamescope uninstalled."
            : BuildFailureStatus("gamescope uninstall failed");
        IsBusy = false;

        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(success, Status));
        return success;
    }

    private async Task<bool> InstallDistroPackageAsync(GamescopeReleaseInfo release)
    {
        if (CommandExists("pacman"))
        {
            var args = string.IsNullOrWhiteSpace(release.PackageVersion)
                ? "pacman -S --needed --noconfirm gamescope"
                : $"pacman -S --needed --noconfirm gamescope";
            return await RunPrivilegedPackageCommandAsync(args).ConfigureAwait(false);
        }

        if (CommandExists("apt-get"))
        {
            var aptGet = ResolveSystemExecutable("apt-get") ?? "/usr/bin/apt-get";
            var packageSpec = string.IsNullOrWhiteSpace(release.PackageVersion)
                ? "gamescope"
                : $"gamescope={release.PackageVersion}";
            return await RunPrivilegedPackageCommandAsync(
                $"{aptGet} update && {aptGet} install -y {packageSpec}").ConfigureAwait(false);
        }

        if (CommandExists("dnf"))
            return await RunPrivilegedPackageCommandAsync("dnf install -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await RunPrivilegedPackageCommandAsync("zypper install -y gamescope").ConfigureAwait(false);

        Status = "No supported Linux package manager found for gamescope installation.";
        return false;
    }

    private async Task<bool> InstallFromSourceAsync(string tag)
    {
        var installDir = ApplicationPaths.ToolsDirectory;
        Directory.CreateDirectory(installDir);

        Status = $"Installing gamescope {tag} build dependencies (password required)...";
        await AppendInstallLogAsync(Status).ConfigureAwait(true);
        if (!await EnsureSourceBuildDependenciesAsync().ConfigureAwait(true))
            return false;

        Status = $"Building gamescope {tag} from source (log: {InstallLogPath})...";
        await AppendInstallLogAsync(Status).ConfigureAwait(true);
        var script = BuildSourceInstallScript(tag, installDir, InstallLogPath);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-gamescope-build-{tag}.sh");
        await File.WriteAllTextAsync(scriptPath, script).ConfigureAwait(true);
        await AppendInstallLogAsync($"Running build script: {scriptPath}").ConfigureAwait(true);

        try
        {
            if (!await ExecuteLoggedCommandAsync("bash", scriptPath, InstallLogPath).ConfigureAwait(true))
            {
                AppendMesonFailureHint(InstallLogPath);
                return false;
            }

            PromoteInstalledBinaries(installDir);
            return File.Exists(ApplicationPaths.GetToolFile("gamescope")) ||
                   File.Exists(Path.Combine(installDir, "bin", "gamescope"));
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to delete temporary gamescope build script '{scriptPath}'.", ex);
            }
        }
    }

    private static void PromoteInstalledBinaries(string installDir)
    {
        foreach (var binary in new[] { "gamescope", "gamescopereaper", "gamescopestream", "gamescopectl" })
        {
            var builtPath = Path.Combine(installDir, "bin", binary);
            var managedPath = Path.Combine(installDir, binary);
            if (!File.Exists(builtPath))
                continue;

            File.Copy(builtPath, managedPath, overwrite: true);
            try
            {
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(managedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to chmod promoted gamescope binary '{managedPath}'.", ex);
            }
        }
    }

    private void AppendMesonFailureHint(string logPath)
    {
        try
        {
            if (File.Exists(logPath))
            {
                _lastCommandError = string.Join(Environment.NewLine, File.ReadLines(logPath).TakeLast(12));
                return;
            }

            var buildRoot = Directory.GetDirectories(Path.GetTempPath(), "aes-gamescope-src-*")
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
            var mesonLog = buildRoot == null
                ? null
                : Path.Combine(buildRoot, "build", "meson-logs", "meson-log.txt");

            if (mesonLog != null && File.Exists(mesonLog))
                _lastCommandError = string.Join(Environment.NewLine, File.ReadLines(mesonLog).TakeLast(12));
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to read gamescope meson failure log.", ex);
        }
    }

    private async Task<bool> ExecuteLoggedCommandAsync(string fileName, string args, string logPath)
    {
        await AppendInstallLogAsync($"Running logged command: {fileName} {args}").ConfigureAwait(true);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return false;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync()).ConfigureAwait(false);

            var combined = stdoutTask.Result;
            if (!string.IsNullOrWhiteSpace(stderrTask.Result))
                combined += Environment.NewLine + stderrTask.Result;
            if (!string.IsNullOrWhiteSpace(combined))
                await File.AppendAllTextAsync(logPath, combined + Environment.NewLine).ConfigureAwait(false);

            _lastExitCode = process.ExitCode;
            _lastCommandError = stderrTask.Result.Trim();
            if (_lastExitCode != 0)
            {
                Log.Warn($"Logged command failed: {fileName} {args} ExitCode={_lastExitCode}");
            }

            return _lastExitCode == 0;
        }
        catch (Exception ex)
        {
            _lastCommandError = ex.Message;
            await File.AppendAllTextAsync(logPath, ex + Environment.NewLine).ConfigureAwait(false);
            Log.Error($"Logged command failed: {fileName} {args}", ex);
            return false;
        }
    }

    private async Task<bool> EnsureSourceBuildDependenciesAsync()
    {
        if (CommandExists("apt-get"))
        {
            var aptGet = ResolveSystemExecutable("apt-get") ?? "/usr/bin/apt-get";
            return await RunPrivilegedPackageCommandAsync($"{aptGet} install -y {AptSourceBuildDependencyList}")
                .ConfigureAwait(false);
        }

        if (CommandExists("pacman"))
        {
            return await RunPrivilegedPackageCommandAsync(
                $"pacman -S --needed --noconfirm {PacmanSourceBuildDependencyList}").ConfigureAwait(false);
        }

        if (CommandExists("dnf"))
        {
            return await RunPrivilegedPackageCommandAsync(
                "dnf install -y git meson ninja-build pkg-config python3 cmake wayland-protocols " +
                "dbus-devel libdrm-devel libinput-devel pipewire-devel systemd-devel libX11-devel libxcb-devel " +
                "wayland-devel libxkbcommon-devel vulkan-devel libdecor-devel libliftoff-devel libcap-devel " +
                "pango-devel pixman-devel fontconfig-devel libXcomposite-devel libXcursor-devel libXdamage-devel " +
                "libXext-devel libXfixes-devel libXi-devel libXrender-devel libXres-devel libXtst-devel libXmu-devel " +
                "libXxf86vm-devel xxhash-devel luajit-devel glslang-devel lcms2-devel libseat-devel hwdata").ConfigureAwait(false);
        }

        Status = "Automatic build dependency installation is not configured for this distro.";
        return false;
    }

    private static string BuildSourceInstallScript(string tag, string installDir, string logPath)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"aes-gamescope-src-{tag}");
        return $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            TAG='{{tag}}'
            INSTALL_DIR='{{installDir}}'
            WORK_DIR='{{workDir}}'

            echo "[gamescope] starting source build for ${TAG}"

            rm -rf "$WORK_DIR"
            mkdir -p "$WORK_DIR" "$INSTALL_DIR/bin"

            git clone --depth 1 --recursive --branch "$TAG" https://github.com/ValveSoftware/gamescope.git "$WORK_DIR/src"
            meson setup "$WORK_DIR/build" "$WORK_DIR/src" \
              --prefix="$INSTALL_DIR" \
              -Denable_openvr_support=false \
              -Denable_tests=false \
              -Dbenchmark=disabled \
              -Dinput_emulation=disabled \
              -Davif_screenshots=disabled
            ninja -C "$WORK_DIR/build"
            meson install -C "$WORK_DIR/build" --skip-subprojects

            for bin in gamescope gamescopereaper gamescopestream gamescopectl; do
              if [[ -f "$INSTALL_DIR/bin/$bin" ]]; then
                cp -f "$INSTALL_DIR/bin/$bin" "$INSTALL_DIR/$bin"
                chmod +x "$INSTALL_DIR/$bin"
              fi
            done

            echo "[gamescope] installed to $INSTALL_DIR"
            "$INSTALL_DIR/gamescope" --version || "$INSTALL_DIR/bin/gamescope" --version
            rm -rf "$WORK_DIR"
            """;
    }

    private async Task<List<string>> GetDistroPackageVersionsAsync()
    {
        if (CommandExists("apt-cache"))
        {
            var output = await ExecuteCommandCaptureAsync("bash", "-lc \"apt-cache madison gamescope 2>/dev/null\"").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
                output = await ExecuteCommandCaptureAsync("bash", "-lc \"apt-cache policy gamescope 2>/dev/null\"").ConfigureAwait(false);

            return ParseAptPackageVersions(output);
        }

        if (CommandExists("pacman"))
        {
            var output = await ExecuteCommandCaptureAsync("bash", "-lc \"pacman -Si gamescope 2>/dev/null | awk '/^Version/{print $3}'\"").ConfigureAwait(false);
            return ParseLineList(output);
        }

        if (CommandExists("dnf"))
        {
            var output = await ExecuteCommandCaptureAsync("bash", "-lc \"dnf info gamescope 2>/dev/null | awk '/^Version/{print $3}'\"").ConfigureAwait(false);
            return ParseLineList(output);
        }

        return [];
    }

    public static List<string> ParseAptPackageVersions(string? output)
    {
        var versions = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
            return versions;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("Candidate:", StringComparison.Ordinal))
            {
                var candidate = trimmed.Split(':', 2)[1].Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Equals("(none)", StringComparison.OrdinalIgnoreCase))
                    versions.Add(candidate);
                continue;
            }

            var madisonMatch = Regex.Match(trimmed, @"\|\s*([^\s|]+)\s*\|");
            if (madisonMatch.Success)
                versions.Add(madisonMatch.Groups[1].Value);
        }

        return versions.Distinct(StringComparer.Ordinal).ToList();
    }

    public static string? FindMatchingDistroPackage(string tag, IReadOnlyList<string> distroPackages)
    {
        foreach (var packageVersion in distroPackages)
        {
            var packageTag = ExtractVersionFromText(packageVersion);
            if (string.Equals(packageTag, tag, StringComparison.Ordinal))
                return packageVersion;
        }

        return null;
    }

    public static int CompareVersionKeys(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return 0;
        if (string.IsNullOrWhiteSpace(left))
            return -1;
        if (string.IsNullOrWhiteSpace(right))
            return 1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < count; i++)
        {
            var l = i < leftParts.Length && int.TryParse(leftParts[i], out var lNum) ? lNum : 0;
            var r = i < rightParts.Length && int.TryParse(rightParts[i], out var rNum) ? rNum : 0;
            if (l != r)
                return l.CompareTo(r);
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static string ParseVersionKey(string? value) => ExtractVersionFromText(value ?? string.Empty) ?? string.Empty;

    private static List<string> ParseLineList(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<bool> RunLinuxUninstallAsync()
    {
        if (CommandExists("pacman"))
            return await RunPrivilegedPackageCommandAsync("pacman -R --noconfirm gamescope").ConfigureAwait(false);

        if (CommandExists("apt-get"))
            return await RunPrivilegedPackageCommandAsync("apt-get remove -y gamescope").ConfigureAwait(false);

        if (CommandExists("dnf"))
            return await RunPrivilegedPackageCommandAsync("dnf remove -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await RunPrivilegedPackageCommandAsync("zypper remove -y gamescope").ConfigureAwait(false);

        Status = "No supported Linux package manager found for gamescope uninstall.";
        return false;
    }

    private async Task<bool> RunPrivilegedPackageCommandAsync(string arguments)
    {
        var attempts = new (string Command, string Label)[]
        {
            ("pkexec", "pkexec"),
        };

        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-gamescope-priv-{Guid.NewGuid():N}.sh");
        var logPath = EnsureInstallLogPath();
        var escapedLogPath = logPath.Replace("'", "'\\''");
        var escapedArguments = arguments.Replace("'", "'\\''");
        await File.WriteAllTextAsync(
            scriptPath,
            $"""
            #!/usr/bin/env bash
            set -uo pipefail
            exec >> '{escapedLogPath}' 2>&1
            echo "[{DateTime.Now:u}] [privileged] {escapedArguments}"
            {arguments}
            exit_code=$?
            echo "[{DateTime.Now:u}] [privileged] exit code: $exit_code"
            exit $exit_code
            """).ConfigureAwait(true);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to chmod temporary gamescope privileged script '{scriptPath}'.", ex);
        }

        await AppendInstallLogAsync($"Privileged command: {arguments}").ConfigureAwait(true);
        await AppendInstallLogAsync($"Privileged script: {scriptPath}").ConfigureAwait(true);

        try
        {
            foreach (var (command, label) in attempts)
            {
                var runner = ResolveSystemExecutable(command);
                if (string.IsNullOrWhiteSpace(runner))
                    continue;

                Status = $"Running gamescope package command via {label}...";
                Log.Info($"Trying gamescope package command via {label}: {arguments}");
                await AppendInstallLogAsync(Status).ConfigureAwait(true);

                if (await ExecutePrivilegedCommandAsync(runner, scriptPath).ConfigureAwait(true))
                    return true;
            }

            return false;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to delete temporary gamescope privileged script '{scriptPath}'.", ex);
            }
        }
    }

    private async Task<bool> ExecutePrivilegedCommandAsync(string runner, string scriptPath)
    {
        try
        {
            var bashPath = ResolveSystemExecutable("bash") ?? "/bin/bash";
            Log.Info($"Starting privileged command: {runner} {bashPath} {scriptPath}");
            await AppendInstallLogAsync($"Executing: {runner} {bashPath} {scriptPath}").ConfigureAwait(true);

            var startInfo = new ProcessStartInfo
            {
                FileName = runner,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(bashPath);
            startInfo.ArgumentList.Add(scriptPath);

            CopyPolkitEnvironment(startInfo);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return false;

            var exitedTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(45));
            var completedTask = await Task.WhenAny(exitedTask, timeoutTask).ConfigureAwait(true);

            if (completedTask != exitedTask)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception killEx)
                {
                    Log.Debug("Failed to kill timed-out privileged gamescope command.", killEx);
                }

                _lastExitCode = -1;
                _lastCommandError = "Privileged command timed out after 45 minutes.";
                await AppendInstallLogAsync(_lastCommandError).ConfigureAwait(true);
                return false;
            }

            _lastExitCode = process.ExitCode;
            _lastCommandError = _lastExitCode == 0
                ? null
                : _lastExitCode == 126
                    ? "Polkit denied the privileged command."
                    : ReadPrivilegedFailureDetail();
            await AppendInstallLogAsync($"Privileged command exit code: {_lastExitCode}").ConfigureAwait(true);
            return _lastExitCode == 0;
        }
        catch (Exception ex)
        {
            _lastCommandError = ex.Message;
            await AppendInstallLogAsync($"Privileged command exception: {ex.Message}").ConfigureAwait(true);
            Log.Error($"Privileged command failed: {runner} {scriptPath}", ex);
            return false;
        }
    }

    private static string? ResolveSystemExecutable(string command)
    {
        if (command.Contains('/', StringComparison.Ordinal))
            return command;

        return ResolveFromPath(command);
    }

    private static string? ResolveFromPath(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"command -v {executable}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve gamescope from PATH.", ex);
            return null;
        }
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"command -v {command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ExecuteCommandAsync(string fileName, string args)
    {
        try
        {
            Log.Info($"Starting external command: {fileName} {args}");
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            CopyPolkitEnvironment(startInfo);

            var usePkexec = string.Equals(fileName, "pkexec", StringComparison.Ordinal) ||
                            Path.GetFileName(fileName).Equals("pkexec", StringComparison.OrdinalIgnoreCase);
            if (usePkexec)
            {
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return false;

            Task<string> outputTask = Task.FromResult(string.Empty);
            Task<string> errorTask = Task.FromResult(string.Empty);
            if (!usePkexec)
            {
                outputTask = process.StandardOutput.ReadToEndAsync();
                errorTask = process.StandardError.ReadToEndAsync();
            }

            var exitedTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(45));
            var completedTask = await Task.WhenAny(exitedTask, timeoutTask).ConfigureAwait(true);

            if (completedTask != exitedTask)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception killEx)
                {
                    Log.Debug("Failed to kill timed-out gamescope command.", killEx);
                }

                _lastExitCode = -1;
                _lastCommandError = "Operation timed out.";
                Log.Warn($"External command timed out: {fileName} {args}");
                return false;
            }

            if (!usePkexec)
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(true);

            _lastExitCode = process.ExitCode;
            _lastCommandError = usePkexec
                ? (_lastExitCode == 0 ? null : "Authentication was cancelled or denied.")
                : errorTask.Result.Trim();
            if (_lastExitCode != 0)
            {
                Log.Warn(
                    $"External command failed: {fileName} {args} ExitCode={_lastExitCode} StdErr={_lastCommandError} StdOut={outputTask.Result.Trim()}");
            }

            return _lastExitCode == 0;
        }
        catch (Exception ex)
        {
            _lastCommandError = ex.Message;
            Log.Error($"External command failed: {fileName} {args}", ex);
            return false;
        }
    }

    private string ReadPrivilegedFailureDetail()
    {
        try
        {
            var logPath = EnsureInstallLogPath();
            if (File.Exists(logPath))
            {
                var lines = File.ReadLines(logPath)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(8)
                    .ToList();
                if (lines.Count > 0)
                    return string.Join(Environment.NewLine, lines);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to read privileged command failure details from install log.", ex);
        }

        return $"Privileged command failed (exit code {_lastExitCode}). See {InstallLogPath} for details.";
    }

    private static void CopyPolkitEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS", "DESKTOP_SESSION" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[key] = value;
        }
    }

    private string BuildFailureStatus(string prefix)
    {
        var exitCode = _lastExitCode == 0 ? "Unknown" : _lastExitCode.ToString();
        if (string.IsNullOrWhiteSpace(_lastCommandError))
            return $"{prefix} (exit code: {exitCode}).";

        var detail = _lastCommandError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return $"{prefix} (exit code: {exitCode}): {detail}";
    }

    private static async Task<string?> ExecuteCommandCaptureAsync(string fileName, string args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync()).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;

            var output = outputTask.Result;
            return string.IsNullOrWhiteSpace(output) ? errorTask.Result : output;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to capture output from {fileName} {args}.", ex);
            return null;
        }
    }

    public static string? ExtractVersionFromText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var match = Regex.Match(input, @"\d+(?:\.\d+){1,3}");
        return match.Success ? match.Value : null;
    }

    private void LoadCache()
    {
        if (_cache != null)
            return;

        if (!File.Exists(CachePath))
        {
            _cache = new GamescopeCacheEntry();
            return;
        }

        try
        {
            var json = File.ReadAllText(CachePath);
            _cache = JsonSerializer.Deserialize(json, GamescopeManagerJsonContext.Default.GamescopeCacheEntry)
                       ?? new GamescopeCacheEntry();
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to load gamescope version cache.", ex);
            _cache = new GamescopeCacheEntry();
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var json = JsonSerializer.Serialize(_cache ?? new GamescopeCacheEntry(), GamescopeManagerJsonContext.Default.GamescopeCacheEntry);
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to save gamescope version cache.", ex);
        }
    }

    private sealed class GamescopeCacheEntry
    {
        public string? ETag { get; set; }
        public List<GamescopeReleaseInfo>? Versions { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(GamescopeCacheEntry))]
    [JsonSerializable(typeof(GamescopeReleaseInfo))]
    [JsonSerializable(typeof(List<GamescopeReleaseInfo>))]
    private partial class GamescopeManagerJsonContext : JsonSerializerContext;
}
