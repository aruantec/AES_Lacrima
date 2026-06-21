using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;

namespace AES_Lacrima.Services.Emulation;

internal sealed class EmulationOnlineCoverResult
{
    public required byte[] Bytes { get; init; }
    public required string Source { get; init; }
    public string? ResolvedTitle { get; init; }
}

/// <summary>
/// Resolves emulation box art via Hasheous, LibRetro thumbnails, leaving web search as the caller's fallback.
/// </summary>
internal static class EmulationOnlineCoverResolver
{
    private static readonly ILog Log = LogHelper.For(typeof(EmulationOnlineCoverResolver));

    public static async Task<EmulationOnlineCoverResult?> TryResolveCoverAsync(
        MediaItem item,
        string? albumName,
        CancellationToken cancellationToken)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FileName))
            return null;

        var romPath = EmulationCoverCacheHelper.ResolveRomPathForCache(item.FileName);
        var section = EmulationDiscSectionResolver.Resolve(albumName ?? item.Album, romPath);
        var romInfo = await Task.Run(() =>
        {
            var info = RomInspector.Inspect(romPath, section);
            return info;
        }, cancellationToken).ConfigureAwait(false);

        HasheousMatch? hasheousMatch = null;
        if (HasheousLookupService.BuildLookupPayload(romInfo) != null)
        {
            hasheousMatch = await HasheousLookupService.TryLookupAsync(romInfo, cancellationToken).ConfigureAwait(false);
            if (hasheousMatch != null)
            {
                Log.Debug($"Hasheous matched '{item.FileName}' to '{hasheousMatch.Name}'.");

                if (!string.IsNullOrWhiteSpace(hasheousMatch.TheGamesDbId))
                {
                    var tgdbBytes = await TheGamesDbCoverService
                        .TryDownloadCoverAsync(hasheousMatch.TheGamesDbId, cancellationToken)
                        .ConfigureAwait(false);
                    if (tgdbBytes != null)
                    {
                        return new EmulationOnlineCoverResult
                        {
                            Bytes = tgdbBytes,
                            Source = "Hasheous/TheGamesDB",
                            ResolvedTitle = hasheousMatch.Name
                        };
                    }
                }
            }
        }

        var titleCandidates = BuildTitleCandidates(item, romInfo, hasheousMatch?.Name);
        var libRetroBytes = await LibRetroThumbnailCoverService
            .TryDownloadCoverAsync(albumName ?? item.Album, hasheousMatch?.PlatformName, titleCandidates, cancellationToken)
            .ConfigureAwait(false);
        if (libRetroBytes != null)
        {
            return new EmulationOnlineCoverResult
            {
                Bytes = libRetroBytes,
                Source = "LibRetro",
                ResolvedTitle = hasheousMatch?.Name
            };
        }

        return null;
    }

    internal static IReadOnlyList<string> BuildTitleCandidates(MediaItem item, RomInfo romInfo, string? hasheousName)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSource(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            foreach (var variant in LibRetroThumbnailCoverService.ExpandTitleVariants(value.Trim()))
            {
                if (string.IsNullOrWhiteSpace(variant) || !seen.Add(variant))
                    continue;

                ordered.Add(variant);
            }
        }

        AddSource(hasheousName);
        AddSource(romInfo.InternalTitle);
        AddSource(item.Title);

        if (!string.IsNullOrWhiteSpace(item.FileName))
        {
            var stem = Path.GetFileNameWithoutExtension(item.FileName);
            AddSource(stem);
            AddSource(StripRomReleaseTokens(stem));
        }

        return ordered;
    }

    internal static bool ShouldApplyResolvedTitle(MediaItem item, string resolvedTitle)
    {
        if (string.IsNullOrWhiteSpace(resolvedTitle))
            return false;

        if (string.IsNullOrWhiteSpace(item.Title))
            return true;

        if (string.Equals(item.Title, resolvedTitle, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(item.FileName))
        {
            var stem = Path.GetFileNameWithoutExtension(item.FileName);
            if (string.Equals(item.Title, stem, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return item.Title.IndexOf(' ') < 0 && resolvedTitle.IndexOf(' ') >= 0;
    }

    private static string StripRomReleaseTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var stripped = title
            .Replace('_', ' ')
            .Replace('.', ' ')
            .Replace('!', ' ');

        foreach (var token in new[] { "(USA)", "(Europe)", "(Japan)", "(World)", "[!", "[a]", "[b]", "[h]", "[t]" })
            stripped = stripped.Replace(token, " ", StringComparison.OrdinalIgnoreCase);

        return string.Join(' ',
            stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
