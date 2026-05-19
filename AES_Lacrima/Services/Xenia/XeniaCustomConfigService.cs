using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using log4net;
using Tomlyn;
using Tomlyn.Model;

namespace AES_Lacrima.Services.Xenia;

public static class XeniaCustomConfigService
{
    private static readonly ILog Log = LogHelper.For(typeof(XeniaCustomConfigService));

    public const string ActiveConfigFileName = "xenia-canary.config.toml";
    public const string DefaultTemplateFileName = "xenia-canary.config.toml.default";

    public const string GpuSection = "GPU";
    public const string DrawResolutionScaleXKey = "draw_resolution_scale_x";
    public const string DrawResolutionScaleYKey = "draw_resolution_scale_y";
    public const int DrawResolutionScaleMin = 1;
    public const int DrawResolutionScaleMax = 7;

    private static readonly string[] DefaultConfigDownloadUrls =
    [
        "https://raw.githubusercontent.com/xenia-canary/xenia-canary/canary/xenia-canary.config.toml",
        "https://raw.githubusercontent.com/xenia-canary/xenia-canary/master/xenia-canary.config.toml"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static string GetCustomConfigsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "custom_configs");
    }

    public static string GetJsonConfigPath(string? emulatorDirectory, string titleId)
    {
        var directory = GetCustomConfigsDirectory(emulatorDirectory);
        return Path.Combine(directory, $"{titleId.ToUpperInvariant()}.json");
    }

    public static string GetActiveConfigPath(string? emulatorDirectory) =>
        string.IsNullOrWhiteSpace(emulatorDirectory)
            ? string.Empty
            : Path.Combine(emulatorDirectory, ActiveConfigFileName);

    public static string GetDefaultTemplatePath(string? emulatorDirectory) =>
        string.IsNullOrWhiteSpace(emulatorDirectory)
            ? string.Empty
            : Path.Combine(emulatorDirectory, DefaultTemplateFileName);

    public static bool HasCustomConfig(string? emulatorDirectory, string titleId) =>
        File.Exists(GetJsonConfigPath(emulatorDirectory, titleId));

    public static XeniaCustomConfigDocument LoadOrEmpty(string? emulatorDirectory, string titleId)
    {
        var path = GetJsonConfigPath(emulatorDirectory, titleId);
        if (!File.Exists(path))
            return new XeniaCustomConfigDocument();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<XeniaCustomConfigDocument>(json, JsonOptions) ?? new XeniaCustomConfigDocument();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load Xenia custom config JSON at '{path}'.", ex);
            return new XeniaCustomConfigDocument();
        }
    }

    public static void Save(string? emulatorDirectory, string titleId, XeniaCustomConfigDocument document)
    {
        var path = GetJsonConfigPath(emulatorDirectory, titleId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static async Task EnsureDefaultTemplateAsync(string? emulatorDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        Directory.CreateDirectory(emulatorDirectory);

        var templatePath = GetDefaultTemplatePath(emulatorDirectory);
        if (File.Exists(templatePath))
            return;

        var activePath = GetActiveConfigPath(emulatorDirectory);
        if (File.Exists(activePath))
        {
            File.Copy(activePath, templatePath, overwrite: false);
            Log.Info($"Created Xenia default config template from existing '{ActiveConfigFileName}'.");
            return;
        }

        foreach (var url in DefaultConfigDownloadUrls)
        {
            try
            {
                using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                await File.WriteAllTextAsync(templatePath, content, cancellationToken).ConfigureAwait(false);
                Log.Info($"Downloaded Xenia default config template from '{url}'.");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to download Xenia default config from '{url}'.", ex);
            }
        }

        var minimal = CreateMinimalDefaultToml();
        await File.WriteAllTextAsync(templatePath, minimal, cancellationToken).ConfigureAwait(false);
        Log.Warn("Created minimal built-in Xenia default config template.");
    }

    public static IReadOnlyDictionary<string, string?> ReadMergedValues(string? emulatorDirectory, XeniaCustomConfigDocument overrides)
    {
        var templatePath = GetDefaultTemplatePath(emulatorDirectory);
        var activePath = GetActiveConfigPath(emulatorDirectory);
        var sourcePath = File.Exists(templatePath) ? templatePath : activePath;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        TomlTable? root = null;

        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            try
            {
                root = Toml.Parse(File.ReadAllText(sourcePath)).ToModel();
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to parse Xenia TOML at '{sourcePath}'.", ex);
            }
        }

        foreach (var field in XeniaConfigSchema.AllFields)
        {
            var key = ComposeKey(field.Section, field.Key);
            if (TryGetOverride(overrides, field.Section, field.Key, out var overrideValue))
            {
                values[key] = overrideValue;
                continue;
            }

            values[key] = root != null ? ReadTomlValueAsString(root, field.Section, field.Key) : null;
        }

        AddDrawResolutionScaleValues(values, overrides, root);
        return values;
    }

    public static XeniaCustomConfigDocument BuildOverridesFromValues(
        IReadOnlyDictionary<string, string?> currentValues,
        IReadOnlyDictionary<string, string?> templateValues)
    {
        var document = new XeniaCustomConfigDocument();

        foreach (var field in XeniaConfigSchema.AllFields)
        {
            var key = ComposeKey(field.Section, field.Key);
            currentValues.TryGetValue(key, out var current);
            templateValues.TryGetValue(key, out var template);

            if (string.Equals(NormalizeValue(current), NormalizeValue(template), StringComparison.Ordinal))
                continue;

            if (!document.Overrides.TryGetValue(field.Section, out var section))
            {
                section = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                document.Overrides[field.Section] = section;
            }

            section[field.Key] = current;
        }

        AddDrawResolutionScaleOverrides(document, currentValues, templateValues);
        return document;
    }

    /// <summary>
    /// Copies the default template to the active config and merges per-game overrides when present.
    /// Always runs before launch so Xenia starts from a known baseline.
    /// </summary>
    public static void PrepareConfigForLaunch(string? emulatorDirectory, string? titleId)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        try
        {
            EnsureDefaultTemplateAsync(emulatorDirectory).GetAwaiter().GetResult();

            var overrides = string.IsNullOrWhiteSpace(titleId)
                ? new XeniaCustomConfigDocument()
                : LoadOrEmpty(emulatorDirectory, titleId);

            ApplyOverrides(emulatorDirectory, overrides);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to prepare Xenia config for launch (title '{titleId ?? "unknown"}').", ex);
        }
    }

    public static void ApplyOverrides(string? emulatorDirectory, XeniaCustomConfigDocument overrides)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            throw new InvalidOperationException("Emulator directory is required.");

        Directory.CreateDirectory(emulatorDirectory);
        EnsureDefaultTemplateAsync(emulatorDirectory).GetAwaiter().GetResult();

        var templatePath = GetDefaultTemplatePath(emulatorDirectory);
        var activePath = GetActiveConfigPath(emulatorDirectory);

        if (!File.Exists(templatePath))
            throw new FileNotFoundException("Xenia default config template was not found.", templatePath);

        if (File.Exists(activePath))
            BackupFile(activePath);

        File.Copy(templatePath, activePath, overwrite: true);

        var root = Toml.Parse(File.ReadAllText(activePath)).ToModel();
        foreach (var (sectionName, sectionOverrides) in overrides.Overrides)
        {
            if (!root.TryGetValue(sectionName, out var sectionObj) || sectionObj is not TomlTable sectionTable)
            {
                sectionTable = new TomlTable();
                root[sectionName] = sectionTable;
            }

            foreach (var (key, rawValue) in sectionOverrides)
            {
                if (IsDrawResolutionScaleKey(sectionName, key))
                {
                    sectionTable[key] = ConvertDrawResolutionScaleToToml(rawValue);
                    continue;
                }

                var definition = XeniaConfigSchema.AllFields.FirstOrDefault(field =>
                    string.Equals(field.Section, sectionName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase));

                sectionTable[key] = definition != null
                    ? ConvertToTomlObject(rawValue, definition)
                    : rawValue ?? string.Empty;
            }
        }

        EnsureDrawResolutionScaleTomlTypes(root);
        File.WriteAllText(activePath, Toml.FromModel(root));
        Log.Info($"Applied Xenia custom config to '{activePath}'.");
    }

    public static string? ReadTomlValueAsString(TomlTable root, string section, string key)
    {
        if (!root.TryGetValue(section, out var sectionObj) || sectionObj is not TomlTable sectionTable)
            return null;

        if (!sectionTable.TryGetValue(key, out var value) || value == null)
            return null;

        return value switch
        {
            bool boolean => boolean ? "true" : "false",
            string text => text,
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            double floating => floating.ToString(CultureInfo.InvariantCulture),
            float single => single.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static object ConvertToTomlObject(string? rawValue, XeniaConfigFieldDefinition definition)
    {
        if (rawValue == null)
            return string.Empty;

        return definition.Kind switch
        {
            XeniaConfigValueKind.Boolean => bool.TryParse(rawValue, out var boolean) && boolean,
            XeniaConfigValueKind.Integer => long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                ? integer
                : 0L,
            XeniaConfigValueKind.Float => double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                ? floating
                : 0d,
            XeniaConfigValueKind.GpuAdapterIndex => long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var adapter)
                ? adapter
                : -1L,
            _ => rawValue
        };
    }

    private static bool TryGetOverride(XeniaCustomConfigDocument document, string section, string key, out string? value)
    {
        value = null;
        if (!document.Overrides.TryGetValue(section, out var sectionOverrides))
            return false;

        return sectionOverrides.TryGetValue(key, out value);
    }

    private static string ComposeKey(string section, string key) => $"{section}.{key}";

    private static void AddDrawResolutionScaleValues(
        IDictionary<string, string?> values,
        XeniaCustomConfigDocument overrides,
        TomlTable? root)
    {
        foreach (var key in new[] { DrawResolutionScaleXKey, DrawResolutionScaleYKey })
        {
            var composite = ComposeKey(GpuSection, key);
            if (TryGetOverride(overrides, GpuSection, key, out var overrideValue))
                values[composite] = overrideValue;
            else
                values[composite] = root != null ? ReadTomlValueAsString(root, GpuSection, key) : null;
        }
    }

    private static void AddDrawResolutionScaleOverrides(
        XeniaCustomConfigDocument document,
        IReadOnlyDictionary<string, string?> currentValues,
        IReadOnlyDictionary<string, string?> templateValues)
    {
        var scaleXKey = ComposeKey(GpuSection, DrawResolutionScaleXKey);
        var scaleYKey = ComposeKey(GpuSection, DrawResolutionScaleYKey);
        currentValues.TryGetValue(scaleXKey, out var currentX);
        templateValues.TryGetValue(scaleXKey, out var templateX);

        if (string.Equals(NormalizeValue(currentX), NormalizeValue(templateX), StringComparison.Ordinal))
            return;

        if (!document.Overrides.TryGetValue(GpuSection, out var section))
        {
            section = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            document.Overrides[GpuSection] = section;
        }

        currentValues.TryGetValue(scaleYKey, out var currentY);
        section[DrawResolutionScaleXKey] = currentX;
        section[DrawResolutionScaleYKey] = currentY ?? currentX;
    }

    private static string? NormalizeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool IsDrawResolutionScaleKey(string section, string key) =>
        string.Equals(section, GpuSection, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(key, DrawResolutionScaleXKey, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(key, DrawResolutionScaleYKey, StringComparison.OrdinalIgnoreCase));

    private static long ConvertDrawResolutionScaleToToml(string? rawValue)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale))
            scale = DrawResolutionScaleMin;

        return Math.Clamp(scale, DrawResolutionScaleMin, DrawResolutionScaleMax);
    }

    private static void EnsureDrawResolutionScaleTomlTypes(TomlTable root)
    {
        if (!root.TryGetValue(GpuSection, out var sectionObj) || sectionObj is not TomlTable sectionTable)
            return;

        foreach (var key in new[] { DrawResolutionScaleXKey, DrawResolutionScaleYKey })
        {
            if (!sectionTable.TryGetValue(key, out var value) || value == null)
                continue;

            sectionTable[key] = value switch
            {
                long => value,
                int integer => (long)integer,
                string text => ConvertDrawResolutionScaleToToml(text),
                _ => ConvertDrawResolutionScaleToToml(value.ToString())
            };
        }
    }

    private static void BackupFile(string path)
    {
        var backupPath = $"{path}.bak";
        try
        {
            File.Copy(path, backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to backup '{path}' to '{backupPath}'.", ex);
        }
    }

    private static string CreateMinimalDefaultToml() =>
        """
        [GPU]
        gpu = "any"
        vsync = true
        framerate_limit = 0

        [Display]
        fullscreen = false

        [General]
        apply_patches = true

        [UI]
        window_size_x = 1280
        window_size_y = 720
        """;
}
