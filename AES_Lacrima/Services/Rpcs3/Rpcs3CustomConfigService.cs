using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Manages per-game RPCS3 YAML configs at <c>config/custom_configs/config_{TITLEID}.yml</c>
/// under the AES-managed emulator directory.
/// </summary>
public static class Rpcs3CustomConfigService
{
    private static readonly ILog Log = LogHelper.For(typeof(Rpcs3CustomConfigService));

    public const string DefaultTemplateFileName = "config.default.yml";
    public const string GlobalConfigRelativePath = "config/config.yml";
    public const string CustomConfigsRelativePath = "config/custom_configs";
    public const string PerGameConfigFilePrefix = "config_";

    public static string GetDefaultEmulatorDirectory() =>
        Path.Combine(ApplicationPaths.EmulatorsDirectory, Rpcs3Handler.Instance.SectionKey, "RPCS3");

    /// <summary>
    /// RPCS3 user-data root (trailing separator). When passed as <c>RPCS3_CONFIG_DIR</c>, RPCS3 loads
    /// <c>config/patch_config.yml</c> and <c>patches/patch.yml</c> from here (see fs::get_config_dir).
    /// </summary>
    public static string NormalizeConfigRootDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        var root = Path.GetFullPath(emulatorDirectory.Trim());
        return root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Points RPCS3 at the AES-managed config tree so patch toggles and custom configs apply in-game.
    /// </summary>
    public static void ApplyConfigDirectoryEnvironment(ProcessStartInfo startInfo, string? emulatorDirectory)
    {
        var configRoot = NormalizeConfigRootDirectory(emulatorDirectory);
        if (string.IsNullOrWhiteSpace(configRoot))
            return;

        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(Path.Combine(configRoot, "config"));
        Directory.CreateDirectory(Path.Combine(configRoot, "patches"));

        startInfo.Environment["RPCS3_CONFIG_DIR"] = configRoot;
        Log.Info($"RPCS3 launch will use RPCS3_CONFIG_DIR='{configRoot}'.");
    }

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

    public static string GetCustomConfigsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, CustomConfigsRelativePath);
    }

    public static string GetCustomConfigPath(string? emulatorDirectory, string titleId) =>
        Path.Combine(GetCustomConfigsDirectory(emulatorDirectory), BuildPerGameFileName(titleId));

    /// <summary>Same location as <see cref="GetCustomConfigPath"/>; kept for existing call sites.</summary>
    public static string GetAesCustomConfigPath(string? emulatorDirectory, string titleId) =>
        GetCustomConfigPath(emulatorDirectory, titleId);

    /// <summary>Same location as <see cref="GetCustomConfigPath"/>; RPCS3 reads configs from here directly.</summary>
    public static string GetRpcs3DeployedConfigPath(string? emulatorDirectory, string titleId) =>
        GetCustomConfigPath(emulatorDirectory, titleId);

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
        File.Exists(GetCustomConfigPath(emulatorDirectory, titleId));

    public static IReadOnlyDictionary<string, string?> ReadMergedValues(string? emulatorDirectory, string titleId)
    {
        EnsureDefaultTemplate(emulatorDirectory);
        var normalizedTitleId = NormalizeTitleId(titleId);
        var configPath = GetCustomConfigPath(emulatorDirectory, normalizedTitleId);
        if (!File.Exists(configPath))
        {
            var templatePath = GetDefaultTemplatePath(emulatorDirectory);
            return Rpcs3YamlConfigHelper.ReadFlatValues(templatePath);
        }

        return Rpcs3YamlConfigHelper.ReadFlatValues(configPath);
    }

    public static IReadOnlyDictionary<string, string?> ReadTemplateValues(string? emulatorDirectory)
    {
        EnsureDefaultTemplate(emulatorDirectory);
        return Rpcs3YamlConfigHelper.ReadFlatValues(GetDefaultTemplatePath(emulatorDirectory));
    }

    public static void SaveValues(string? emulatorDirectory, string titleId, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            throw new InvalidOperationException("Emulator directory is required.");

        var normalizedTitleId = NormalizeTitleId(titleId);
        Directory.CreateDirectory(GetCustomConfigsDirectory(emulatorDirectory));
        EnsureDefaultTemplate(emulatorDirectory);

        var configPath = GetCustomConfigPath(emulatorDirectory, normalizedTitleId);
        var sourcePath = File.Exists(configPath)
            ? configPath
            : GetDefaultTemplatePath(emulatorDirectory);

        Rpcs3YamlConfigHelper.ApplyFlatValues(sourcePath, values, configPath);
        Log.Info($"Saved RPCS3 custom config for {normalizedTitleId} to '{configPath}'.");
    }

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
    /// Ensures a per-game config exists under <c>config/custom_configs</c> before launch.
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
            Directory.CreateDirectory(GetCustomConfigsDirectory(emulatorDirectory));
            EnsureDefaultTemplate(emulatorDirectory);

            var configPath = GetCustomConfigPath(emulatorDirectory, normalizedTitleId);
            if (!File.Exists(configPath))
            {
                var templatePath = GetDefaultTemplatePath(emulatorDirectory);
                if (!File.Exists(templatePath))
                    throw new FileNotFoundException("RPCS3 default config template was not found.", templatePath);

                File.Copy(templatePath, configPath, overwrite: false);
                Log.Info($"Created RPCS3 custom config for {normalizedTitleId} at '{configPath}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to prepare RPCS3 custom config for launch (title '{normalizedTitleId}').", ex);
        }
    }

    /// <summary>
    /// No-op when configs live in <c>config/custom_configs</c> (RPCS3 writes there directly during play).
    /// </summary>
    public static void ImportFromRpcs3AfterSession(string? emulatorDirectory, string? titleId)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        var normalizedTitleId = NormalizeTitleId(titleId);
        if (string.IsNullOrWhiteSpace(normalizedTitleId))
            return;

        var configPath = GetCustomConfigPath(emulatorDirectory, normalizedTitleId);
        if (File.Exists(configPath))
            Log.Debug($"RPCS3 custom config for {normalizedTitleId} already at '{configPath}'.");
    }

    public static void TryMigrateLegacyStorage(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        var targetDirectory = GetCustomConfigsDirectory(emulatorDirectory);
        Directory.CreateDirectory(targetDirectory);

        foreach (var legacyDirectory in EnumerateLegacyCustomConfigDirectories(emulatorDirectory))
        {
            if (!Directory.Exists(legacyDirectory))
                continue;

            if (string.Equals(
                    Path.GetFullPath(legacyDirectory),
                    Path.GetFullPath(targetDirectory),
                    StringComparison.OrdinalIgnoreCase))
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

    private static IEnumerable<string> EnumerateLegacyCustomConfigDirectories(string emulatorDirectory)
    {
        yield return Path.Combine(emulatorDirectory, "custom_configs");
        yield return Path.Combine(ApplicationPaths.EmulatorsDirectory, "RPCS3", "custom_configs");
        yield return Path.Combine(ApplicationPaths.EmulatorsDirectory, Rpcs3Handler.Instance.SectionKey, "RPCS3", "custom_configs");
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
