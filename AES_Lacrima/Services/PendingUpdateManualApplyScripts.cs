using System;
using System.IO;
using System.Text;
using AES_Core.IO;
using log4net;

using AES_Core.Logging;

namespace AES_Lacrima.Services;

internal static class PendingUpdateManualApplyScripts
{
    private const string PowerShellScriptFileName = "apply-pending-update-manually.ps1";
    private const string CmdScriptFileName = "apply-pending-update-manually.cmd";
    private static readonly ILog Log = LogHelper.For(typeof(PendingUpdateManualApplyScripts));

    private const string PowerShellScriptContent = """
        # Manually apply a downloaded AES - Lacrima pending update.
        # Generated automatically when an update is downloaded.
        #
        # Usage:
        #   1. Close AES - Lacrima completely.
        #   2. From cmd:
        #        "%LOCALAPPDATA%\AES_Lacrima\Updates\apply-pending-update-manually.cmd"
        #      Or from PowerShell:
        #        powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AES_Lacrima\Updates\apply-pending-update-manually.ps1"
        #
        # Optional:
        #   -ManifestPath "C:\path\to\pending-update.json"
        #   -NoRelaunch

        [CmdletBinding()]
        param(
            [string]$ManifestPath = (Join-Path $env:LOCALAPPDATA "AES_Lacrima\Updates\pending-update.json"),
            [switch]$NoRelaunch
        )

        $ErrorActionPreference = "Stop"

        function Write-UpdaterLog {
            param([string]$Message)
            $logPath = Join-Path $env:LOCALAPPDATA "AES_Lacrima\Logs\updater.log"
            $logDir = Split-Path -Parent $logPath
            if (-not (Test-Path -LiteralPath $logDir)) {
                New-Item -ItemType Directory -Force -Path $logDir | Out-Null
            }
            $timestamp = (Get-Date).ToString("o")
            Add-Content -Path $logPath -Encoding UTF8 -Value "[$timestamp] [manual] $Message"
            Write-Host $Message
        }

        function Wait-ForProcessExit {
            param(
                [string]$ProcessName = "AES_Lacrima",
                [int]$TimeoutSeconds = 300
            )

            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ((Get-Date) -lt $deadline) {
                $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
                if (-not $proc) { return }
                Write-UpdaterLog "Waiting for $ProcessName to exit..."
                Start-Sleep -Seconds 2
            }

            throw "$ProcessName is still running. Close it and run this script again."
        }

        function Normalize-RobocopyPath {
            param([string]$Path)
            if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
            $full = [System.IO.Path]::GetFullPath($Path)
            return $full.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        }

        function Copy-DirectoryContents {
            param(
                [string]$Source,
                [string]$Destination
            )

            $sourcePath = Normalize-RobocopyPath $Source
            $destinationPath = Normalize-RobocopyPath $Destination
            New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
            $robocopyArgs = @(
                $sourcePath, $destinationPath,
                "/E", "/IS", "/IT",
                "/R:10", "/W:1",
                "/NFL", "/NDL", "/NJH", "/NJS", "/NC", "/NS", "/NP"
            )
            & robocopy @robocopyArgs *> $null
            $code = $LASTEXITCODE
            if ($code -ge 8) {
                throw "robocopy failed with exit code $code. Close AES - Lacrima and any Explorer windows on the install folder, then retry."
            }
        }

        try {
            Write-UpdaterLog "Manual pending update apply started."

            if (-not (Test-Path -LiteralPath $ManifestPath)) {
                Write-UpdaterLog "No pending update manifest found. The update was already applied or nothing is waiting."
                Write-Host ""
                Write-Host "Nothing to do. No pending update is waiting." -ForegroundColor Green
                exit 0
            }

            $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

            if ([int]$manifest.targetKind -ne 0) {
                throw "Unsupported target kind $($manifest.targetKind). This script only handles Windows folder updates."
            }

            if (-not (Test-Path -LiteralPath $manifest.preparedSourcePath)) {
                throw "Prepared source path does not exist: $($manifest.preparedSourcePath)"
            }

            if (-not (Test-Path -LiteralPath $manifest.targetPath)) {
                throw "Target install path does not exist: $($manifest.targetPath)"
            }

            Write-UpdaterLog "Pending version: $($manifest.releaseVersion)"
            Write-UpdaterLog "Source: $($manifest.preparedSourcePath)"
            Write-UpdaterLog "Target: $($manifest.targetPath)"

            Wait-ForProcessExit

            Write-UpdaterLog "Copying update files..."
            Copy-DirectoryContents -Source $manifest.preparedSourcePath -Destination $manifest.targetPath
            Write-UpdaterLog "Update files copied successfully."

            Remove-Item -LiteralPath $ManifestPath -Force
            Write-UpdaterLog "Removed pending update manifest."

            if ($manifest.stagingRoot -and (Test-Path -LiteralPath $manifest.stagingRoot)) {
                Remove-Item -LiteralPath $manifest.stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
                Write-UpdaterLog "Removed staging folder."
            }

            if (-not $NoRelaunch -and $manifest.restartPath -and (Test-Path -LiteralPath $manifest.restartPath)) {
                $workingDirectory = Split-Path -Parent $manifest.restartPath
                Write-UpdaterLog "Relaunching: $($manifest.restartPath)"
                Start-Process -FilePath $manifest.restartPath -WorkingDirectory $workingDirectory
            }

            Write-UpdaterLog "Manual pending update apply finished successfully."
        }
        catch {
            Write-UpdaterLog "Manual pending update apply failed: $_"
            Write-Error $_
            exit 1
        }
        """;

    private const string CmdScriptContent = """
        @echo off
        setlocal

        REM Manually apply a downloaded AES - Lacrima pending update.
        REM Generated automatically when an update is downloaded.
        REM Close AES - Lacrima before running.
        REM
        REM Usage from cmd:
        REM   "%LOCALAPPDATA%\AES_Lacrima\Updates\apply-pending-update-manually.cmd"
        REM
        REM Optional: pass /norelaunch to copy only without starting the app.

        set "SCRIPT_DIR=%~dp0"
        set "PS_SCRIPT=%SCRIPT_DIR%apply-pending-update-manually.ps1"
        set "EXTRA_ARGS="

        if /I "%~1"=="/norelaunch" set "EXTRA_ARGS=-NoRelaunch"
        if /I "%~1"=="-norelaunch" set "EXTRA_ARGS=-NoRelaunch"

        if not exist "%PS_SCRIPT%" (
            echo PowerShell script not found: %PS_SCRIPT%
            exit /b 1
        )

        powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %EXTRA_ARGS%
        set "EXITCODE=%ERRORLEVEL%"

        if not "%EXITCODE%"=="0" (
            echo.
            echo Update failed. Check: "%LOCALAPPDATA%\AES_Lacrima\Logs\updater.log"
            pause
        ) else (
            echo.
            echo Done.
        )

        exit /b %EXITCODE%
        """;

    public static string PowerShellScriptPath =>
        Path.Combine(ApplicationPaths.UpdatesDirectory, PowerShellScriptFileName);

    public static string CmdScriptPath =>
        Path.Combine(ApplicationPaths.UpdatesDirectory, CmdScriptFileName);

    public static void EnsureWritten()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Directory.CreateDirectory(ApplicationPaths.UpdatesDirectory);
            File.WriteAllText(PowerShellScriptPath, PowerShellScriptContent, Encoding.UTF8);
            File.WriteAllText(CmdScriptPath, CmdScriptContent, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to write manual pending update helper scripts", ex);
        }
    }
}
