using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Back-compat wrapper around <see cref="EmulationHashTitleResolver"/> for Genesis.
/// </summary>
internal static class GenesisTitleResolver
{
    public static bool IsGenesisAlbum(string? albumTitle)
        => EmulationHashTitleResolver.IsSupportedAlbum(albumTitle, out var platform) &&
           string.Equals(platform?.ConsoleKey, "GENESIS", StringComparison.OrdinalIgnoreCase);

    public static string? TryResolveOffline(string? filePath)
        => EmulationHashTitleResolver.IsSupportedAlbum("Sega Genesis", out var platform) && platform != null
            ? EmulationHashTitleResolver.TryResolveOffline(filePath, platform)
            : null;

    public static Task<string?> TryResolveFullAsync(string? filePath, CancellationToken cancellationToken)
        => EmulationHashTitleResolver.IsSupportedAlbum("Sega Genesis", out var platform) && platform != null
            ? EmulationHashTitleResolver.TryResolveFullAsync(filePath, platform, cancellationToken)
            : Task.FromResult<string?>(null);

    public static bool NeedsBetterTitle(string? title, string? filePath)
        => EmulationHashTitleResolver.NeedsBetterTitle(title, filePath);
}
