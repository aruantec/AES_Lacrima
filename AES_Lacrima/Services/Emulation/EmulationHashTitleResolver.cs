using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;

namespace AES_Lacrima.Services.Emulation;

internal sealed class EmulationHashTitlePlatform
{
    public required string ConsoleKey { get; init; }
    public required string NoIntroDatabaseFile { get; init; }
    public string? RedumpDatabaseFile { get; init; }
    public required string LogLabel { get; init; }
}

/// <summary>
/// Resolves cartridge/disc emulation titles via No-Intro → Hasheous → Redump → ROM metadata.
/// </summary>
internal static class EmulationHashTitleResolver
{
    private static readonly ILog Log = LogHelper.For(typeof(EmulationHashTitleResolver));
    private static readonly object DatabaseLock = new();

    private static readonly EmulationHashTitlePlatform[] Platforms =
    [
        new()
        {
            ConsoleKey = "GENESIS",
            NoIntroDatabaseFile = "genesis.json",
            RedumpDatabaseFile = "genesis_redump.json",
            LogLabel = "Genesis"
        },
        new()
        {
            ConsoleKey = "NES",
            NoIntroDatabaseFile = "nes.json",
            LogLabel = "NES"
        },
        new()
        {
            ConsoleKey = "GBA",
            NoIntroDatabaseFile = "gba.json",
            LogLabel = "GBA"
        },
        new()
        {
            ConsoleKey = "PSP",
            NoIntroDatabaseFile = "psp.json",
            RedumpDatabaseFile = "psp_redump.json",
            LogLabel = "PSP"
        }
    ];

    private static readonly Dictionary<string, EmulationHashTitlePlatform> PlatformsByKey =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (EmulationHashTitlePlatform Platform, EmulationHashTitleDatabase Database)> NoIntroDatabases =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (EmulationHashTitlePlatform Platform, EmulationHashTitleDatabase Database)> RedumpDatabases =
        new(StringComparer.OrdinalIgnoreCase);

    static EmulationHashTitleResolver()
    {
        foreach (var platform in Platforms)
            PlatformsByKey[platform.ConsoleKey] = platform;
    }

    public static bool IsSupportedAlbum(string? albumTitle, out EmulationHashTitlePlatform? platform)
    {
        platform = null;
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        if (!EmulationConsoleCatalog.TryGetDefinition(albumTitle, out var definition))
            return false;

        if (!PlatformsByKey.TryGetValue(definition.Key, out var resolved))
            return false;

        platform = resolved;
        return true;
    }

    public static bool NeedsBetterTitle(string? title, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (NintendoDiscMetadataHelper.IsFilenameLikeTitle(title, filePath))
            return true;

        return LooksLikeDumpCatalogTitle(title);
    }

    internal static bool LooksLikeDumpCatalogTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var trimmed = title.Trim();
        int openCount = trimmed.Count(c => c == '(');
        if (openCount >= 2)
            return true;

        return trimmed.Contains("(En,", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("(Rev ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("(Asia)", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("(Australia)", StringComparison.OrdinalIgnoreCase);
    }

    public static string? TryResolveOffline(string? filePath, EmulationHashTitlePlatform platform)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var romInfo = InspectRom(filePath, platform.ConsoleKey) ?? new RomInfo { FilePath = filePath };

        if (string.Equals(platform.ConsoleKey, "PSP", StringComparison.OrdinalIgnoreCase))
            return TryResolvePspTitle(romInfo, platform);

        return TryResolveFromNoIntro(romInfo, platform) ??
               NormalizeHeaderTitle(romInfo.InternalTitle);
    }

    public static async Task<string?> TryResolveFullAsync(
        string? filePath,
        EmulationHashTitlePlatform platform,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var romInfo = InspectRom(filePath, platform.ConsoleKey) ?? new RomInfo { FilePath = filePath };

        if (string.Equals(platform.ConsoleKey, "PSP", StringComparison.OrdinalIgnoreCase))
            return TryResolvePspTitle(romInfo, platform);

        var noIntroTitle = TryResolveFromNoIntro(romInfo, platform);
        if (!string.IsNullOrWhiteSpace(noIntroTitle))
            return noIntroTitle;

        if (!string.Equals(platform.ConsoleKey, "PSP", StringComparison.OrdinalIgnoreCase) &&
            HasheousLookupService.BuildLookupPayload(romInfo) != null)
        {
            try
            {
                var hasheousMatch = await HasheousLookupService.TryLookupAsync(romInfo, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(hasheousMatch?.Name))
                {
                    Log.Debug($"Hasheous resolved {platform.LogLabel} title for '{filePath}' to '{hasheousMatch.Name}'.");
                    return hasheousMatch.Name.Trim();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug($"Hasheous {platform.LogLabel} title lookup failed for '{filePath}'.", ex);
            }
        }

        var redumpTitle = TryResolveFromRedump(romInfo, platform);
        if (!string.IsNullOrWhiteSpace(redumpTitle))
            return redumpTitle;

        return NormalizeHeaderTitle(romInfo.InternalTitle);
    }

    public static string? TryResolveFromNoIntro(RomInfo romInfo, EmulationHashTitlePlatform platform)
        => LoadNoIntroDatabase(platform).TryResolve(romInfo);

    public static string? TryResolveFromRedump(RomInfo romInfo, EmulationHashTitlePlatform platform)
    {
        if (string.IsNullOrWhiteSpace(platform.RedumpDatabaseFile))
            return null;

        return LoadRedumpDatabase(platform).TryResolve(romInfo);
    }

    private static RomInfo? InspectRom(string? filePath, string consoleKey)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        if (!File.Exists(filePath))
            return null;

        try
        {
            var section = EmulationDiscSectionResolver.Resolve(consoleKey, filePath);
            return RomInspector.Inspect(filePath, section);
        }
        catch (Exception ex)
        {
            Log.Debug($"ROM inspection failed for '{filePath}'.", ex);
            return null;
        }
    }

    private static string? TryResolvePspTitle(RomInfo romInfo, EmulationHashTitlePlatform platform)
    {
        var sfoTitle = NormalizeHeaderTitle(romInfo.InternalTitle);
        if (!string.IsNullOrWhiteSpace(sfoTitle))
            return sfoTitle;

        return TryResolveFromRedump(romInfo, platform)
            ?? TryResolveFromNoIntro(romInfo, platform);
    }

    private static string? NormalizeHeaderTitle(string? internalTitle)
    {
        if (string.IsNullOrWhiteSpace(internalTitle))
            return null;

        var trimmed = internalTitle.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static EmulationHashTitleDatabase LoadNoIntroDatabase(EmulationHashTitlePlatform platform)
    {
        lock (DatabaseLock)
        {
            if (NoIntroDatabases.TryGetValue(platform.ConsoleKey, out var cached))
                return cached.Database;

            var database = EmulationHashTitleDatabase.Load(
                platform.NoIntroDatabaseFile,
                $"{platform.LogLabel} No-Intro title database");
            NoIntroDatabases[platform.ConsoleKey] = (platform, database);
            return database;
        }
    }

    private static EmulationHashTitleDatabase LoadRedumpDatabase(EmulationHashTitlePlatform platform)
    {
        lock (DatabaseLock)
        {
            if (RedumpDatabases.TryGetValue(platform.ConsoleKey, out var cached))
                return cached.Database;

            var database = EmulationHashTitleDatabase.Load(
                platform.RedumpDatabaseFile!,
                $"{platform.LogLabel} Redump title database");
            RedumpDatabases[platform.ConsoleKey] = (platform, database);
            return database;
        }
    }
}
