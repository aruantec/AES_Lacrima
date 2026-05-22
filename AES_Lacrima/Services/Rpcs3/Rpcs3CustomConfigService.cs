using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Manages per-game RPCS3 YAML configs stored under the AES emulator folder and deploys them
/// to RPCS3's <c>config/custom_configs/config_{TITLEID}.yml</c> before each launch.
/// </summary>
public static class Rpcs3CustomConfigService
{
    private static readonly ILog Log = LogHelper.For(typeof(Rpcs3CustomConfigService));

    public const string DefaultTemplateFileName = "config.default.yml";
    public const string GlobalConfigRelativePath = "config/config.yml";
    public const string RpcS3CustomConfigsRelativePath = "config/custom_configs";
    public const string AesCustomConfigsFolderName = "custom_configs";
    public const string PerGameConfigFilePrefix = "config_";

    public static string GetDefaultEmulatorDirectory() =>
        Path.Combine(ApplicationPaths.EmulatorsDirectory, Rpcs3Handler.Instance.SectionKey, "RPCS3");

    public static string ResolveEmulatorDirectory(string? launcherPath)
    {
        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            try
            {
                var launcherDirectory = Path.GetDirectoryName(Path.GetFullPath(launcherPath.Trim()));
                if (!string.IsNullOrWhiteSpace(launcherDirectory) && Directory.Exists(launcherDirectory))
                    return launcherDirectory;
            }
            catch
            {
            }
        }

        var managedDirectory = GetDefaultEmulatorDirectory();
        Directory.CreateDirectory(managedDirectory);
        return managedDirectory;
    }

    public static string GetAesCustomConfigsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, AesCustomConfigsFolderName);
    }

    public static string GetRpcs3CustomConfigsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, RpcS3CustomConfigsRelativePath);
    }

    public static string GetAesCustomConfigPath(string? emulatorDirectory, string titleId)
    {
        var directory = GetAesCustomConfigsDirectory(emulatorDirectory);
        return Path.Combine(directory, BuildPerGameFileName(titleId));
    }

    public static string GetRpcs3DeployedConfigPath(string? emulatorDirectory, string titleId)
    {
        var directory = GetRpcs3CustomConfigsDirectory(emulatorDirectory);
        return Path.Combine(directory, BuildPerGameFileName(titleId));
    }

    public static string GetGlobalConfigPath(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, GlobalConfigRelativePath);
    }

    public static string GetDefaultTemplatePath(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, DefaultTemplateFileName);
    }

    public static bool HasCustomConfig(string? emulatorDirectory, string titleId) =>
        File.Exists(GetAesCustomConfigPath(emulatorDirectory, titleId));

    public static string NormalizeTitleId(string? titleId) =>
        string.IsNullOrWhiteSpace(titleId) ? string.Empty : titleId.Trim().ToUpperInvariant();

    public static void EnsureDefaultTemplate(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        Directory.CreateDirectory(emulatorDirectory);

        var templatePath = GetDefaultTemplatePath(emulatorDirectory);
        if (File.Exists(templatePath))
            return;

        var globalPath = GetGlobalConfigPath(emulatorDirectory);
        if (File.Exists(globalPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(templatePath) ?? emulatorDirectory);
            File.Copy(globalPath, templatePath, overwrite: false);
            Log.Info($"Created RPCS3 default config template from '{GlobalConfigRelativePath}'.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(globalPath) ?? Path.Combine(emulatorDirectory, "config"));
        File.WriteAllText(globalPath, CreateMinimalGlobalConfigYaml());
        File.Copy(globalPath, templatePath, overwrite: false);
        Log.Warn("Created minimal RPCS3 global and default config templates.");
    }

    /// <summary>
    /// Ensures AES-side storage exists, then deploys it where RPCS3 loads per-game settings.
    /// </summary>
    public static void PrepareConfigForLaunch(string? emulatorDirectory, string? titleId)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        var normalizedTitleId = NormalizeTitleId(titleId);
        if (string.IsNullOrWhiteSpace(normalizedTitleId))
            return;

        try
        {
            TryMigrateLegacyStorage(emulatorDirectory);
            Directory.CreateDirectory(GetAesCustomConfigsDirectory(emulatorDirectory));
            Directory.CreateDirectory(GetRpcs3CustomConfigsDirectory(emulatorDirectory));
            EnsureDefaultTemplate(emulatorDirectory);

            var aesConfigPath = GetAesCustomConfigPath(emulatorDirectory, normalizedTitleId);
            if (!File.Exists(aesConfigPath))
            {
                var templatePath = GetDefaultTemplatePath(emulatorDirectory);
                if (!File.Exists(templatePath))
                    throw new FileNotFoundException("RPCS3 default config template was not found.", templatePath);

                File.Copy(templatePath, aesConfigPath, overwrite: false);
                Log.Info($"Created RPCS3 custom config for {normalizedTitleId} from default template.");
            }

            var deployedPath = GetRpcs3DeployedConfigPath(emulatorDirectory, normalizedTitleId);
            File.Copy(aesConfigPath, deployedPath, overwrite: true);
            Log.Info($"Deployed RPCS3 custom config for {normalizedTitleId} to '{deployedPath}'.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to prepare RPCS3 custom config for launch (title '{normalizedTitleId}').", ex);
        }
    }

    /// <summary>
    /// Copies RPCS3's per-game config back into AES storage after a session (e.g. in-game settings changes).
    /// </summary>
    public static void ImportFromRpcs3AfterSession(string? emulatorDirectory, string? titleId)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        var normalizedTitleId = NormalizeTitleId(titleId);
        if (string.IsNullOrWhiteSpace(normalizedTitleId))
            return;

        try
        {
            var deployedPath = GetRpcs3DeployedConfigPath(emulatorDirectory, normalizedTitleId);
            if (!File.Exists(deployedPath))
                return;

            Directory.CreateDirectory(GetAesCustomConfigsDirectory(emulatorDirectory));
            var aesConfigPath = GetAesCustomConfigPath(emulatorDirectory, normalizedTitleId);
            File.Copy(deployedPath, aesConfigPath, overwrite: true);
            Log.Info($"Imported RPCS3 custom config for {normalizedTitleId} from '{deployedPath}'.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to import RPCS3 custom config after session (title '{normalizedTitleId}').", ex);
        }
    }

    public static void TryMigrateLegacyStorage(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        var targetDirectory = GetAesCustomConfigsDirectory(emulatorDirectory);
        Directory.CreateDirectory(targetDirectory);

        foreach (var legacyDirectory in EnumerateLegacyCustomConfigDirectories())
        {
            if (!Directory.Exists(legacyDirectory))
                continue;

            foreach (var legacyFile in Directory.EnumerateFiles(legacyDirectory, "*.yml", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(legacyFile);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var destinationPath = Path.Combine(targetDirectory, fileName);
                if (File.Exists(destinationPath))
                    continue;

                try
                {
                    File.Copy(legacyFile, destinationPath, overwrite: false);
                    Log.Info($"Migrated legacy RPCS3 custom config '{legacyFile}' to '{destinationPath}'.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to migrate legacy RPCS3 custom config '{legacyFile}'.", ex);
                }
            }
        }
    }

    private static string BuildPerGameFileName(string titleId) =>
        $"{PerGameConfigFilePrefix}{NormalizeTitleId(titleId)}.yml";

    private static IEnumerable<string> EnumerateLegacyCustomConfigDirectories()
    {
        yield return Path.Combine(ApplicationPaths.EmulatorsDirectory, "RPCS3", AesCustomConfigsFolderName);
    }

    private static string CreateMinimalGlobalConfigYaml() =>
        """
        Core:
          PPU Decoder: Recompiler (LLVM)
          SPU Decoder: Recompiler (LLVM)
        Video:
          Renderer: Vulkan
        Audio:
          Renderer: Cubeb
        System:
          Language: English (US)
        """;
}
