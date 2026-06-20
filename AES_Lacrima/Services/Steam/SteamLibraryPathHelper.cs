using System;
using System.IO;

namespace AES_Lacrima.Services.Steam;

internal static class SteamLibraryPathHelper
{
    public static string NormalizeLibraryRoot(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            return libraryRoot;

        try
        {
            var fullPath = Path.GetFullPath(libraryRoot);
            var linkTarget = Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true);
            return linkTarget?.FullName ?? fullPath;
        }
        catch
        {
            return libraryRoot;
        }
    }
}
