using System;
using System.IO;

namespace AES_Lacrima.Services.Emulation.Switch;

/// <summary>
/// Uses LibHac + Eden/Yuzu keys to read official application titles from control NCAs.
/// </summary>
internal static class SwitchLibHacMetadataReader
{
    public static string? TryReadApplicationTitle(string filePath)
    {
#if NATIVE_AOT
        _ = filePath;
        return null;
#else
        return TryReadApplicationTitleCore(filePath);
#endif
    }

#if !NATIVE_AOT
    private static string? TryReadApplicationTitleCore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        var keysDirectory = SwitchKeysHelper.ResolveEdenKeysDirectory();
        if (keysDirectory == null)
            return null;

        try
        {
            var keySet = LibHac.Common.Keys.ExternalKeyReader.ReadKeyFile(
                keysDirectory,
                "prod.keys",
                "title.keys",
                null,
                LibHac.Common.Keys.KeySet.Mode.Prod);

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension is ".xci" or ".xcz"
                ? TryReadFromXci(keySet, filePath)
                : TryReadFromPackage(keySet, filePath);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadFromPackage(LibHac.Common.Keys.KeySet keySet, string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var storage = LibHac.Tools.FsSystem.StorageExtensions.AsStorage(stream, false);
        using var partition = new LibHac.FsSystem.PartitionFileSystem();
        var initResult = partition.Initialize(storage);
        if (initResult.IsFailure())
            return null;
        return ReadFirstApplicationName(keySet, partition);
    }

    private static string? TryReadFromXci(LibHac.Common.Keys.KeySet keySet, string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var storage = LibHac.Tools.FsSystem.StorageExtensions.AsStorage(stream, false);
        var xci = new LibHac.Tools.Fs.Xci(keySet, storage);
        var secure = xci.OpenPartition(LibHac.Tools.Fs.XciPartitionType.Secure);
        storage.GetSize(out var totalSize);
        var length = Math.Max(0, totalSize - secure.Offset);
        var sub = LibHac.Tools.FsSystem.StorageExtensions.Slice(storage, secure.Offset, length);
        using var partition = new LibHac.FsSystem.PartitionFileSystem();
        var initResult = partition.Initialize(sub);
        if (initResult.IsFailure())
            return null;
        return ReadFirstApplicationName(keySet, partition);
    }

    private static string? ReadFirstApplicationName(
        LibHac.Common.Keys.KeySet keySet,
        LibHac.Fs.Fsa.IFileSystem contentFs)
    {
        using var switchFs = new LibHac.Tools.Fs.SwitchFs(keySet, contentFs, null);
        foreach (var application in switchFs.Applications.Values)
        {
            if (!string.IsNullOrWhiteSpace(application.Name))
                return application.Name.Trim();
        }

        return null;
    }
#endif
}
