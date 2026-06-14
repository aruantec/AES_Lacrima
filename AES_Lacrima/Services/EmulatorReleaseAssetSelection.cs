using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AES_Lacrima.Services;

/// <summary>
/// Picks release assets that match the host CPU architecture on Linux.
/// </summary>
public static class EmulatorReleaseAssetSelection
{
    public static Architecture ResolveHostArchitecture()
    {
        var processArchitecture = RuntimeInformation.ProcessArchitecture;
        if (processArchitecture is Architecture.X64 or Architecture.Arm64 or Architecture.X86)
            return processArchitecture;

        return RuntimeInformation.OSArchitecture;
    }

    public static bool MatchesHostLinuxAssetArchitecture(string assetName) =>
        MatchesLinuxAssetArchitecture(assetName, ResolveHostArchitecture());

    public static bool MatchesLinuxAssetArchitecture(string assetName, Architecture architecture)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return false;

        var hasArm = IsArm64AssetName(assetName);
        var hasX64 = IsX64AssetName(assetName);
        if (!hasArm && !hasX64)
            return true;

        return architecture switch
        {
            Architecture.Arm64 => hasArm,
            Architecture.X64 or Architecture.X86 => hasX64,
            _ => !hasArm && !hasX64,
        };
    }

    public static bool IsConflictingLinuxAssetArchitecture(string assetName, Architecture architecture)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return false;

        var hasArm = IsArm64AssetName(assetName);
        var hasX64 = IsX64AssetName(assetName);
        if (!hasArm && !hasX64)
            return false;

        return architecture switch
        {
            Architecture.Arm64 => hasX64,
            Architecture.X64 or Architecture.X86 => hasArm,
            _ => false,
        };
    }

    public static T? SelectFirstLinuxAsset<T>(
        IEnumerable<T> assets,
        Func<T, string> getName,
        Func<T, bool> predicate)
    {
        var list = assets as IReadOnlyList<T> ?? assets.ToList();
        if (list.Count == 0)
            return default;

        var architecture = ResolveHostArchitecture();

        var match = list.FirstOrDefault(asset =>
            predicate(asset) && MatchesLinuxAssetArchitecture(getName(asset), architecture));
        if (match != null)
            return match;

        match = list.FirstOrDefault(asset =>
            predicate(asset) && !IsConflictingLinuxAssetArchitecture(getName(asset), architecture));
        if (match != null)
            return match;

        return list.FirstOrDefault(predicate);
    }

    public static bool IsArm64AssetName(string assetName)
    {
        var lower = assetName.ToLowerInvariant();
        return lower.Contains("aarch64", StringComparison.Ordinal) ||
               lower.Contains("arm64", StringComparison.Ordinal) ||
               lower.Contains("armv8", StringComparison.Ordinal);
    }

    public static bool IsX64AssetName(string assetName)
    {
        if (IsArm64AssetName(assetName))
            return false;

        var lower = assetName.ToLowerInvariant();
        return lower.Contains("x86_64", StringComparison.Ordinal) ||
               lower.Contains("x86-64", StringComparison.Ordinal) ||
               lower.Contains("amd64", StringComparison.Ordinal) ||
               lower.Contains("x64", StringComparison.Ordinal) ||
               lower.Contains("win64", StringComparison.Ordinal) ||
               lower.Contains("intel64", StringComparison.Ordinal);
    }

    public static string ResolveLinuxLibretroBuildbotArchDirectory() =>
        ResolveHostArchitecture() switch
        {
            Architecture.Arm64 => "aarch64",
            _ => "x86_64",
        };

    public static bool IsNonLinuxDesktopReleaseAssetName(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return true;

        return assetName.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
               assetName.Contains("macos", StringComparison.OrdinalIgnoreCase) ||
               assetName.Contains("android", StringComparison.OrdinalIgnoreCase) ||
               assetName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
               assetName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
               assetName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEdenLinuxAppImageAssetName(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName) || IsNonLinuxDesktopReleaseAssetName(assetName))
            return false;

        return assetName.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
               assetName.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) &&
               !assetName.EndsWith(".AppImage.zsync", StringComparison.OrdinalIgnoreCase);
    }

    public static T? SelectEdenLinuxAsset<T>(
        IEnumerable<T> assets,
        Func<T, string> getName) where T : class
    {
        var candidates = assets
            .Where(asset => IsEdenLinuxAppImageAssetName(getName(asset)))
            .ToList();
        if (candidates.Count == 0)
            return null;

        return SelectFirstLinuxAsset(
                   candidates,
                   getName,
                   asset => getName(asset).Contains("gcc-standard", StringComparison.OrdinalIgnoreCase))
               ?? SelectFirstLinuxAsset(
                   candidates,
                   getName,
                   asset => getName(asset).Contains("clang-pgo", StringComparison.OrdinalIgnoreCase))
               ?? SelectFirstLinuxAsset(
                   candidates,
                   getName,
                   _ => true);
    }
}
