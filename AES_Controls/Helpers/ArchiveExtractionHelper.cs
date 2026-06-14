using System.Diagnostics;
using System.IO.Compression;
using log4net;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

using AES_Core.Logging;

namespace AES_Controls.Helpers;

/// <summary>
/// Extracts common emulator update archives without requiring external tools.
/// </summary>
public static class ArchiveExtractionHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(ArchiveExtractionHelper));

    public static void ExtractArchive(string archivePath, string extractDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, extractDirectory, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            Extract7z(archivePath, extractDirectory);
            return;
        }

        throw new InvalidOperationException($"Unsupported archive format: {Path.GetExtension(archivePath)}");
    }

    private static void Extract7z(string archivePath, string extractDirectory)
    {
        Directory.CreateDirectory(extractDirectory);

        try
        {
            Extract7zWithSharpCompress(archivePath, extractDirectory);
            return;
        }
        catch (Exception ex)
        {
            Log.Warn("Managed .7z extraction failed; trying system tools.", ex);
        }

        if (TryExtract7zWithSystemTool(archivePath, extractDirectory))
            return;

        var message = OperatingSystem.IsWindows()
            ? "Unable to extract .7z archive. Install 7-Zip (7z.exe) or ensure tar supports 7z extraction."
            : "Unable to extract .7z archive. Install p7zip-full (7z) or p7zip (7za), or retry after updating the app.";

        throw new InvalidOperationException(message);
    }

    private static void Extract7zWithSharpCompress(string archivePath, string extractDirectory)
    {
        using var archive = SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
        using var reader = archive.ExtractAllEntries();
        reader.WriteAllToDirectory(extractDirectory, new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true
        });
    }

    private static bool TryExtract7zWithSystemTool(string archivePath, string extractDirectory)
    {
        foreach (var tool in Get7zToolCandidates())
        {
            var args = tool.StartsWith("tar", StringComparison.OrdinalIgnoreCase)
                ? $"-xf \"{archivePath}\" -C \"{extractDirectory}\""
                : $"x -y \"{archivePath}\" -o\"{extractDirectory}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process == null)
                    continue;

                process.WaitForExit();
                if (process.ExitCode == 0)
                    return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"System .7z extraction tool '{tool}' unavailable.", ex);
            }
        }

        return false;
    }

    private static IEnumerable<string> Get7zToolCandidates()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "tar.exe", "7z.exe" };

        return new[]
        {
            "7z",
            "7za",
            "7zz",
            "/usr/bin/7z",
            "/usr/bin/7za",
            "/usr/bin/7zz",
            "tar"
        };
    }
}
