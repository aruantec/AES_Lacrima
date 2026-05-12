using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using log4net;
using AES_Emulation.Controls;
using AES_Emulation.Windows.API;

namespace AES_Emulation.EmulationHandlers;

public sealed class EdenHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<EdenHandler>();
    private const string RenderReadyOutputToken = "RenderReady";

    public static EdenHandler Instance { get; } = new();

    private EdenHandler()
    {
    }

    public override string HandlerId => "eden";

    public override string SectionKey => "SWITCH";

    public override string SectionTitle => "Nintendo Switch";

    public override string DisplayName => "Eden";

    public override bool HideUntilCaptured => false;

    public override bool ForceUseTargetClientAreaCapture => true;

    public override EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

    public override int ClientAreaCropBottomInset => 0;

    public override int CaptureStartupDelayMs => 6000;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo Switch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Switch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
            EnsureEdenConfigOverrides();

            var resolvedLauncherPath = launcherPath;
            if (!string.IsNullOrWhiteSpace(launcherPath) && string.Equals(Path.GetFileName(launcherPath), "eden.exe", StringComparison.OrdinalIgnoreCase))
            {
                var cliCandidate = Path.Combine(Path.GetDirectoryName(launcherPath) ?? string.Empty, "eden-cli.exe");
                if (File.Exists(cliCandidate))
                {
                    resolvedLauncherPath = cliCandidate;
                    Log.Info($"EdenHandler: switching from eden.exe to eden-cli.exe for capture: '{resolvedLauncherPath}'");
                }
            }

            var startInfo = base.BuildStartInfo(resolvedLauncherPath, romPath, startFullscreen, sectionTitle);
            startInfo.ArgumentList.Clear();

            if (string.IsNullOrWhiteSpace(resolvedLauncherPath) || !File.Exists(resolvedLauncherPath))
                Log.Warn($"Eden launcher path does not exist: '{resolvedLauncherPath}'");

            if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
                Log.Warn($"Eden ROM path does not exist: '{romPath}'");

            var launcherName = Path.GetFileNameWithoutExtension(resolvedLauncherPath)?.ToLowerInvariant() ?? string.Empty;
            var isCli = launcherName.Contains("eden-cli") || launcherName.Contains("edencli");

            if (isCli)
            {
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            if (isCli)
            {
                startInfo.ArgumentList.Add("--config");
                startInfo.ArgumentList.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eden", "config", "qt-config.ini"));

                if (startFullscreen)
                    startInfo.ArgumentList.Add("--fullscreen");

                startInfo.ArgumentList.Add("-g");
                startInfo.ArgumentList.Add(romPath);
            }
            else
            {
                if (startFullscreen)
                    startInfo.ArgumentList.Add("-f");

                startInfo.ArgumentList.Add("-g");
                startInfo.ArgumentList.Add(romPath);
            }

            Log.Debug($"Eden start info: FileName='{startInfo.FileName}', WorkingDirectory='{startInfo.WorkingDirectory}', Arguments='{string.Join(' ', startInfo.ArgumentList)}', UseShellExecute={startInfo.UseShellExecute}");
            return startInfo;
        }

    public override async Task<Process?> ResolveRuntimeProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (process == null)
            return null;

        try
        {
            if (!process.HasExited)
                return process;
        }
        catch
        {
            // ignored
        }

        if (TryResolveChildProcess(process, out var childProcess))
            return childProcess;

        if (TryResolveDetachedProcess(process, out process))
            return process;

        return process;
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        await WaitForRenderReadyOutputAsync(process, cancellationToken).ConfigureAwait(false);
        return await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForRenderReadyOutputAsync(Process process, CancellationToken cancellationToken)
    {
        if (process == null)
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

        if (!process.StartInfo.RedirectStandardOutput && !process.StartInfo.RedirectStandardError)
            return;

        var deadline = DateTime.UtcNow.AddSeconds(45);
        var readers = new List<Task<bool>>();

        if (process.StartInfo.RedirectStandardOutput)
            readers.Add(WaitForTokenAsync(process.StandardOutput, RenderReadyOutputToken, cancellationToken));

        if (process.StartInfo.RedirectStandardError)
            readers.Add(WaitForTokenAsync(process.StandardError, RenderReadyOutputToken, cancellationToken));

        if (readers.Count == 0)
            return;

        while (DateTime.UtcNow < deadline && readers.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            var delayTask = Task.Delay(remaining, cancellationToken);
            var completed = await Task.WhenAny(readers.Cast<Task>().Append(delayTask)).ConfigureAwait(false);
            if (ReferenceEquals(completed, delayTask))
                break;

            var tokenTask = readers.FirstOrDefault(task => ReferenceEquals(task, completed));
            if (tokenTask != null)
            {
                if (await tokenTask.ConfigureAwait(false))
                    return;

                readers.Remove(tokenTask);
            }
        }

        Log.Warn($"Timed out waiting for Eden to emit '{RenderReadyOutputToken}'; continuing with capture target resolution.");
    }

    private static async Task<bool> WaitForTokenAsync(StreamReader reader, string token, CancellationToken cancellationToken)
    {
        if (reader == null)
            return false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
                return false;

            Log.Info($"Eden output: {line}");
            if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

        private static bool TryResolveChildProcess(Process process, out Process? childProcess)
        {
            childProcess = null;
            if (process == null || process.Id == 0 || !OperatingSystem.IsWindows())
                return false;

            var descendants = EnumerateDescendantProcesses(process.Id);
            if (descendants.Count == 0)
                return false;

            var candidate = descendants
                .OrderByDescending(entry => entry.ProcessId)
                .FirstOrDefault();

            if (candidate.ProcessId == 0)
                return false;

            try
            {
                var resolved = Process.GetProcessById((int)candidate.ProcessId);
                if (!resolved.HasExited)
                {
                    childProcess = resolved;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static List<ProcessEntry> EnumerateDescendantProcesses(int rootProcessId)
        {
            var descendants = new List<ProcessEntry>();
            IntPtr snapshot = IntPtr.Zero;
            try
            {
                snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot == INVALID_HANDLE_VALUE)
                    return descendants;

                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snapshot, ref entry))
                    return descendants;

                var allEntries = new List<ProcessEntry>();
                do
                {
                    allEntries.Add(new ProcessEntry(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
                    entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                }
                while (Process32Next(snapshot, ref entry));

                var byParent = allEntries
                    .GroupBy(e => e.ParentProcessId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var queue = new Queue<uint>();
                queue.Enqueue((uint)rootProcessId);
                var visited = new HashSet<uint> { (uint)rootProcessId };

                while (queue.Count > 0)
                {
                    var parentId = queue.Dequeue();
                    if (!byParent.TryGetValue(parentId, out var children))
                        continue;

                    foreach (var child in children)
                    {
                        if (!visited.Add(child.ProcessId))
                            continue;

                        descendants.Add(child);
                        queue.Enqueue(child.ProcessId);
                    }
                }
            }
            finally
            {
                if (snapshot != IntPtr.Zero && snapshot != INVALID_HANDLE_VALUE)
                    CloseHandle(snapshot);
            }

            return descendants;
        }

        private readonly record struct ProcessEntry(uint ProcessId, uint ParentProcessId, string ExeName);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        private static void EnsureEdenConfigOverrides()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configPath = Path.Combine(appData, "eden", "config", "qt-config.ini");
                var configDir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrWhiteSpace(configDir))
                    Directory.CreateDirectory(configDir);

                var desiredValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fullscreen\\default"] = "false",
                    ["fullscreen"] = "false",
                    ["showStatusBar\\default"] = "false",
                    ["showStatusBar"] = "false"
                };

                if (!File.Exists(configPath))
                {
                    File.WriteAllLines(configPath, desiredValues.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                    return;
                }

                var lines = File.ReadAllLines(configPath);
                var updated = false;
                var normalizedLines = lines.Select(line => line.Replace('\r', ' ').Replace('\n', ' ')).ToArray();

                for (int i = 0; i < normalizedLines.Length; i++)
                {
                    var line = normalizedLines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || !line.Contains("="))
                        continue;

                    var parts = line.Split('=', 2);
                    var key = parts[0].Trim();
                    if (desiredValues.TryGetValue(key, out var targetValue))
                    {
                        var newLine = $"{key}={targetValue}";
                        if (!string.Equals(normalizedLines[i], newLine, StringComparison.Ordinal))
                        {
                            normalizedLines[i] = newLine;
                            updated = true;
                        }
                        desiredValues.Remove(key);
                    }
                }

                if (desiredValues.Count > 0)
                {
                    using var writer = File.AppendText(configPath);
                    foreach (var kvp in desiredValues)
                    {
                        writer.WriteLine($"{kvp.Key}={kvp.Value}");
                    }
                    return;
                }

                if (updated)
                    File.WriteAllLines(configPath, normalizedLines);
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to update Eden qt-config.ini settings", ex);
            }
        }

    public override void PrepareProcessForCapture(Process process)
    {
        // Intentionally no-op for Eden because hiding/moving the window can destabilize it.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Intentionally no-op for Eden.
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyEdenRenderWindow, fallbackTitleHint: DisplayName);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyEdenRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyEdenRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (hasCaption && hasThickFrame)
            return false;

        if (!string.IsNullOrWhiteSpace(lowerTitle))
        {
            if (lowerTitle.Contains("settings") || lowerTitle.Contains("audio") || lowerTitle.Contains("video") || lowerTitle.Contains("input") || lowerTitle.Contains("controller") || lowerTitle.Contains("cheat") || lowerTitle.Contains("shader") || lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = true;
            }

            if (lowerTitle.Contains("eden"))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(lowerClass))
        {
            if (lowerClass.Contains("sdl") || lowerClass.Contains("glfw") || lowerClass.Contains("qt") || lowerClass.Contains("eden"))
            {
                return true;
            }
        }

        return !looksLikePrimaryUi && (!hasCaption || string.IsNullOrWhiteSpace(lowerTitle));
    }
}
