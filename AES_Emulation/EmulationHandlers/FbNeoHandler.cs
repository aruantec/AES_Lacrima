using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AES_Emulation.EmulationHandlers;

public sealed class FbNeoHandler : EmulatorHandlerBase
{
    public static FbNeoHandler Instance { get; } = new();

    private FbNeoHandler()
    {
    }

    public override string HandlerId => "fbneo";

    public override string SectionKey => "FBN";

    public override string SectionTitle => "Final Burn Neo";

    public override string DisplayName => "FBNeo";

    public override bool HideUntilCaptured => true;

    public override bool ForceUseTargetClientAreaCapture => true;

    public override int CaptureStartupDelayMs => 1000;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "FBNeo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Final Burn Neo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "FBN", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        var gameName = GetFbNeoGameName(romPath);
        if (!string.IsNullOrWhiteSpace(gameName))
            startInfo.ArgumentList.Add(gameName);

        startInfo.ArgumentList.Add("-w");
        return startInfo;
    }

    private static string? GetFbNeoGameName(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return null;

        try
        {
            if (Directory.Exists(romPath))
            {
                var gameFile = FindFbNeoGameFileInDirectory(romPath);
                if (!string.IsNullOrWhiteSpace(gameFile))
                    return Path.GetFileNameWithoutExtension(gameFile);

                return Path.GetFileName(romPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return Path.GetFileNameWithoutExtension(romPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFbNeoGameFileInDirectory(string directory)
    {
        try
        {
            var candidateExtensions = new[] { ".zip", ".7z", ".dat", ".chd", ".cue", ".iso", ".rom" };
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                                 .Where(path => candidateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            if (files.Count == 0)
                return null;

            return files.Count == 1 ? Path.GetFileName(files[0]) : Path.GetFileName(files.First());
        }
        catch
        {
            return null;
        }
    }
}
