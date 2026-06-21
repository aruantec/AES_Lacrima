using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Readers;

namespace AES_Controls.Helpers;

/// <summary>
/// Picks the primary ROM entry inside zip/7z/rar archives for hash-based title lookup.
/// </summary>
public static class RomArchiveInspectionHelper
{
    private static readonly HashSet<string> RomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".z64", ".v64", ".n64", ".rom", ".bin", ".iso", ".img", ".sfc", ".smc",
        ".nes", ".unf", ".unif", ".fds", ".md", ".gen", ".smd", ".32x", ".sg",
        ".gb", ".gbc", ".gba", ".sms", ".gg"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar"
    };

    public static bool IsRomArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return ArchiveExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>
    /// Extracts the largest likely ROM entry to a temp file. Caller must delete <paramref name="tempRomPath"/>.
    /// </summary>
    public static bool TryExtractPrimaryRomEntry(
        string archivePath,
        out string tempRomPath,
        out string entryDisplayPath)
    {
        tempRomPath = string.Empty;
        entryDisplayPath = string.Empty;

        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return false;

        try
        {
            using var archive = OpenArchive(archivePath);
            var entry = archive.Entries
                .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => new { Entry = e, Extension = Path.GetExtension(e.Key!) })
                .Where(e => !string.IsNullOrWhiteSpace(e.Extension) && RomExtensions.Contains(e.Extension))
                .OrderByDescending(e => e.Entry.Size)
                .Select(e => e.Entry)
                .FirstOrDefault();

            if (entry == null)
            {
                entry = archive.Entries
                    .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.Key))
                    .Where(e => !string.IsNullOrWhiteSpace(Path.GetExtension(e.Key)))
                    .OrderByDescending(e => e.Size)
                    .FirstOrDefault();
            }

            if (entry == null || string.IsNullOrWhiteSpace(entry.Key))
                return false;

            var extension = Path.GetExtension(entry.Key);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".rom";

            tempRomPath = Path.Combine(Path.GetTempPath(), $"aesrom_{Guid.NewGuid():N}{extension}");
            using (var entryStream = entry.OpenEntryStream())
            using (var outFs = File.Create(tempRomPath))
                entryStream.CopyTo(outFs);

            entryDisplayPath = $"{archivePath}::{entry.Key.Replace('\\', '/')}";
            return true;
        }
        catch
        {
            TryDeleteTemp(tempRomPath);
            tempRomPath = string.Empty;
            entryDisplayPath = string.Empty;
            return false;
        }
    }

    private static IArchive OpenArchive(string archivePath)
    {
        var ext = Path.GetExtension(archivePath);
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return ZipArchive.OpenArchive(archivePath);

        if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            return SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);

        if (ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            return RarArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);

        throw new NotSupportedException($"Unsupported ROM archive format '{ext}'.");
    }

    public static void TryDeleteTemp(string? tempRomPath)
    {
        if (string.IsNullOrWhiteSpace(tempRomPath))
            return;

        try
        {
            if (File.Exists(tempRomPath))
                File.Delete(tempRomPath);
        }
        catch
        {
        }
    }
}
