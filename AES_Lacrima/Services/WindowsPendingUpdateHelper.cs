using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using log4net;

using AES_Core.IO;
using AES_Core.Logging;

namespace AES_Lacrima.Services;

internal static class WindowsPendingUpdateHelper
{
    private const string ApplyScriptFileName = "apply-update.ps1";
    private static readonly ILog Log = LogHelper.For(typeof(WindowsPendingUpdateHelper));

    private const string ApplyScriptContent = """
        param(
            [Parameter(Mandatory = $true)][int]$WaitProcessId,
            [Parameter(Mandatory = $true)][string]$ManifestPath,
            [Parameter(Mandatory = $true)][string]$LogPath
        )

        function Write-UpdaterLog {
            param([string]$Message)
            $timestamp = (Get-Date).ToString('o')
            Add-Content -Path $LogPath -Encoding UTF8 -Value "[$timestamp] $Message"
        }

        function Wait-ForProcessExit {
            param([int]$ProcessId, [int]$TimeoutSeconds = 120)
            if ($ProcessId -le 0) { return }
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ((Get-Date) -lt $deadline) {
                $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
                if (-not $proc) { return }
                if ($proc.HasExited) { return }
                Start-Sleep -Milliseconds 500
            }
            Write-UpdaterLog "Timed out waiting for process $ProcessId to exit; attempting apply anyway."
        }

        function Copy-DirectoryContents {
            param([string]$Source, [string]$Destination)
            New-Item -ItemType Directory -Force -Path $Destination | Out-Null
            $robocopyArgs = @(
                $Source, $Destination,
                '/E', '/IS', '/IT',
                '/R:10', '/W:1',
                '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS', '/NP'
            )
            & robocopy @robocopyArgs | Out-Null
            $code = $LASTEXITCODE
            if ($code -ge 8) {
                throw "robocopy failed with exit code $code"
            }
        }

        try {
            Write-UpdaterLog "Windows external update helper started (WaitProcessId=$WaitProcessId)"

            if (-not (Test-Path -LiteralPath $ManifestPath)) {
                Write-UpdaterLog "Manifest no longer exists; exiting."
                exit 0
            }

            $json = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8
            $manifest = $json | ConvertFrom-Json

            if ($manifest.previousProcessId -gt 0) {
                Wait-ForProcessExit -ProcessId $manifest.previousProcessId
            }

            Wait-ForProcessExit -ProcessId $WaitProcessId

            if (-not (Test-Path -LiteralPath $ManifestPath)) {
                Write-UpdaterLog "Manifest removed while waiting; exiting."
                exit 0
            }

            Write-UpdaterLog "Applying update: source=$($manifest.preparedSourcePath) target=$($manifest.targetPath)"

            $targetKind = [int]$manifest.targetKind
            if ($targetKind -ne 0) {
                throw "Unsupported target kind $targetKind for Windows external apply."
            }

            if (-not (Test-Path -LiteralPath $manifest.preparedSourcePath)) {
                throw "Prepared source path does not exist: $($manifest.preparedSourcePath)"
            }

            Copy-DirectoryContents -Source $manifest.preparedSourcePath -Destination $manifest.targetPath

            Write-UpdaterLog "Update files copied successfully."

            Remove-Item -LiteralPath $ManifestPath -Force -ErrorAction Stop

            if ($manifest.stagingRoot -and (Test-Path -LiteralPath $manifest.stagingRoot)) {
                Remove-Item -LiteralPath $manifest.stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
            }

            $restartPath = $manifest.restartPath
            $workingDirectory = Split-Path -Parent $restartPath
            Write-UpdaterLog "Relaunching: $restartPath"

            Start-Process -FilePath $restartPath -WorkingDirectory $workingDirectory

            Write-UpdaterLog "Windows external update helper finished successfully."
            exit 0
        }
        catch {
            Write-UpdaterLog "Windows external update apply failed: $_"
            exit 1
        }
        """;

    public static bool TryScheduleApply(PendingUpdateManifest manifest, int waitProcessId)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (manifest.TargetKind != PendingUpdateTargetKind.DirectoryContents)
            return false;

        try
        {
            Directory.CreateDirectory(ApplicationPaths.UpdatesDirectory);
            Directory.CreateDirectory(ApplicationPaths.LogsDirectory);

            var scriptPath = Path.Combine(ApplicationPaths.UpdatesDirectory, ApplyScriptFileName);
            File.WriteAllText(scriptPath, ApplyScriptContent, Encoding.UTF8);

            var logPath = Path.Combine(ApplicationPaths.LogsDirectory, "updater.log");
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-WaitProcessId");
            startInfo.ArgumentList.Add(waitProcessId.ToString());
            startInfo.ArgumentList.Add("-ManifestPath");
            startInfo.ArgumentList.Add(PendingUpdateApplier.ManifestPath);
            startInfo.ArgumentList.Add("-LogPath");
            startInfo.ArgumentList.Add(logPath);

            Process.Start(startInfo);
            WriteDiagnosticLog(
                "Scheduled Windows external update apply helper",
                $"WaitProcessId={waitProcessId}",
                $"PreparedSourcePath={manifest.PreparedSourcePath}",
                $"TargetPath={manifest.TargetPath}",
                $"RestartPath={manifest.RestartPath}",
                logPath);
            return true;
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog($"Failed to schedule Windows external update apply: {ex}");
            Log.Error("Failed to schedule Windows external update apply", ex);
            return false;
        }
    }

    private static void WriteDiagnosticLog(string message, params string[] details)
    {
        try
        {
            var logPath = Path.Combine(ApplicationPaths.LogsDirectory, "updater.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            using var writer = new StreamWriter(logPath, append: true, Encoding.UTF8);
            writer.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
            foreach (var detail in details)
                writer.WriteLine($"  {detail}");
        }
        catch (Exception ex)
        {
            Log.Warn("Diagnostics should never interfere with app behavior.", ex);
        }
    }
}
