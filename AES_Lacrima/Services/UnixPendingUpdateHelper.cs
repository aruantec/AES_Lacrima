using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using log4net;

using AES_Core.IO;
using AES_Core.Logging;

namespace AES_Lacrima.Services;

internal static class UnixPendingUpdateHelper
{
    private const string ApplyScriptFileName = "apply-update.sh";
    private static readonly ILog Log = LogHelper.For(typeof(UnixPendingUpdateHelper));

    private const string ApplyScriptContent = """
        #!/usr/bin/env bash
        set -euo pipefail

        WAIT_PROCESS_ID=0
        WAIT_PREVIOUS_PROCESS_ID=0
        TARGET_KIND=0
        PREPARED_SOURCE=""
        TARGET_PATH=""
        RESTART_PATH=""
        MANIFEST_PATH=""
        STAGING_ROOT=""
        LOG_PATH=""

        while [[ $# -gt 0 ]]; do
            case "$1" in
                --wait-process-id) WAIT_PROCESS_ID="$2"; shift 2 ;;
                --wait-previous-process-id) WAIT_PREVIOUS_PROCESS_ID="$2"; shift 2 ;;
                --target-kind) TARGET_KIND="$2"; shift 2 ;;
                --prepared-source) PREPARED_SOURCE="$2"; shift 2 ;;
                --target-path) TARGET_PATH="$2"; shift 2 ;;
                --restart-path) RESTART_PATH="$2"; shift 2 ;;
                --manifest-path) MANIFEST_PATH="$2"; shift 2 ;;
                --staging-root) STAGING_ROOT="$2"; shift 2 ;;
                --log-path) LOG_PATH="$2"; shift 2 ;;
                *) echo "Unknown argument: $1" >&2; exit 2 ;;
            esac
        done

        log() {
            local timestamp
            timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
            printf '[%s] %s\n' "$timestamp" "$1" >> "$LOG_PATH"
        }

        wait_for_pid() {
            local pid="$1"
            if [[ "$pid" -le 0 ]]; then
                return 0
            fi

            local deadline=$(( $(date +%s) + 120 ))
            while [[ $(date +%s) -lt $deadline ]]; do
                if ! kill -0 "$pid" 2>/dev/null; then
                    return 0
                fi
                sleep 0.5
            done

            log "Timed out waiting for process ${pid} to exit; attempting apply anyway."
        }

        copy_directory_contents() {
            local source="$1"
            local destination="$2"
            mkdir -p "$destination"

            if command -v rsync >/dev/null 2>&1; then
                rsync -a "$source"/ "$destination"/
                return 0
            fi

            cp -a "$source"/. "$destination"/
        }

        apply_linux_appimage() {
            local temporary="${TARGET_PATH}.new"
            rm -f "$temporary"
            cp -f "$PREPARED_SOURCE" "$temporary"
            chmod u+x,go+x "$temporary"
            mv -f "$temporary" "$TARGET_PATH"
            chmod u+x,go+x "$TARGET_PATH"
        }

        apply_mac_bundle() {
            local temporary="${TARGET_PATH}.new"
            rm -rf "$temporary"
            cp -a "$PREPARED_SOURCE" "$temporary"
            rm -rf "$TARGET_PATH"
            mv "$temporary" "$TARGET_PATH"
        }

        relaunch() {
            if [[ "$RESTART_PATH" == *.app ]]; then
                /usr/bin/open -n "$RESTART_PATH"
                return 0
            fi

            if [[ ! -e "$RESTART_PATH" ]]; then
                log "Restart path does not exist: ${RESTART_PATH}"
                exit 1
            fi

            chmod u+x,go+x "$RESTART_PATH" 2>/dev/null || true
            nohup "$RESTART_PATH" >/dev/null 2>&1 &
        }

        log "Unix external update helper started (waitProcessId=${WAIT_PROCESS_ID}, targetKind=${TARGET_KIND})"
        wait_for_pid "$WAIT_PREVIOUS_PROCESS_ID"
        wait_for_pid "$WAIT_PROCESS_ID"

        if [[ ! -f "$MANIFEST_PATH" ]]; then
            log "Manifest no longer exists; exiting."
            exit 0
        fi

        case "$TARGET_KIND" in
            1)
                if [[ ! -d "$PREPARED_SOURCE" ]]; then
                    log "Prepared macOS bundle source does not exist: ${PREPARED_SOURCE}"
                    exit 1
                fi
                apply_mac_bundle
                ;;
            2)
                if [[ ! -f "$PREPARED_SOURCE" ]]; then
                    log "Prepared Linux AppImage source does not exist: ${PREPARED_SOURCE}"
                    exit 1
                fi
                apply_linux_appimage
                ;;
            *)
                log "Unsupported target kind ${TARGET_KIND} for Unix external apply."
                exit 1
                ;;
        esac

        log "Update files copied successfully."
        rm -f "$MANIFEST_PATH"

        if [[ -n "$STAGING_ROOT" && -e "$STAGING_ROOT" ]]; then
            rm -rf "$STAGING_ROOT"
        fi

        log "Relaunching: ${RESTART_PATH}"
        relaunch
        log "Unix external update helper finished successfully."
        """;

    public static bool TryScheduleApply(PendingUpdateManifest manifest, int waitProcessId)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        if (manifest.TargetKind is not (PendingUpdateTargetKind.MacBundle or PendingUpdateTargetKind.LinuxAppImage))
            return false;

        try
        {
            Directory.CreateDirectory(ApplicationPaths.UpdatesDirectory);
            Directory.CreateDirectory(ApplicationPaths.LogsDirectory);

            var scriptPath = Path.Combine(ApplicationPaths.UpdatesDirectory, ApplyScriptFileName);
            File.WriteAllText(scriptPath, ApplyScriptContent, Encoding.UTF8);
            TrySetUnixExecutable(scriptPath);

            var logPath = Path.Combine(ApplicationPaths.LogsDirectory, "updater.log");
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--wait-process-id");
            startInfo.ArgumentList.Add(waitProcessId.ToString());
            startInfo.ArgumentList.Add("--wait-previous-process-id");
            startInfo.ArgumentList.Add(manifest.PreviousProcessId.ToString());
            startInfo.ArgumentList.Add("--target-kind");
            startInfo.ArgumentList.Add(((int)manifest.TargetKind).ToString());
            startInfo.ArgumentList.Add("--prepared-source");
            startInfo.ArgumentList.Add(manifest.PreparedSourcePath);
            startInfo.ArgumentList.Add("--target-path");
            startInfo.ArgumentList.Add(manifest.TargetPath);
            startInfo.ArgumentList.Add("--restart-path");
            startInfo.ArgumentList.Add(manifest.RestartPath);
            startInfo.ArgumentList.Add("--manifest-path");
            startInfo.ArgumentList.Add(PendingUpdateApplier.ManifestPath);
            startInfo.ArgumentList.Add("--staging-root");
            startInfo.ArgumentList.Add(manifest.StagingRoot);
            startInfo.ArgumentList.Add("--log-path");
            startInfo.ArgumentList.Add(logPath);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                WriteDiagnosticLog("Failed to start Unix external update helper process.");
                return false;
            }

            WriteDiagnosticLog(
                "Scheduled Unix external update apply helper",
                $"WaitProcessId={waitProcessId}",
                $"PreviousProcessId={manifest.PreviousProcessId}",
                $"PreparedSourcePath={manifest.PreparedSourcePath}",
                $"TargetPath={manifest.TargetPath}",
                $"RestartPath={manifest.RestartPath}",
                logPath);
            return true;
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog($"Failed to schedule Unix external update apply: {ex}");
            Log.Error("Failed to schedule Unix external update apply", ex);
            return false;
        }
    }

    private static void TrySetUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to set executable permissions on Unix update helper script", ex);
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
