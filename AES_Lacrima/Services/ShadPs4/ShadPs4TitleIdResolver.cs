using System;
using System.IO;
using System.Text.RegularExpressions;
using AES_Lacrima.Services.Emulation;

using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.Services.ShadPs4;

public static class ShadPs4TitleIdResolver
{
    private static readonly ILog Log = LogHelper.For(typeof(ShadPs4TitleIdResolver));
    private static readonly Regex FolderTitleIdRegex = new(
        @"^(?<id>[A-Z]{2}[A-Z0-9]{3}[A-Z]{2}\d{5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? Resolve(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return null;

        var fromSfo = Ps4InstalledGameHelper.GetTitleId(gamePath);
        if (!string.IsNullOrWhiteSpace(fromSfo))
            return fromSfo.ToUpperInvariant();

        try
        {
            var inspected = RomInspector.Inspect(gamePath, DiscSection.PS4);
            if (!string.IsNullOrWhiteSpace(inspected.GameId))
                return inspected.GameId.ToUpperInvariant();
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        try
        {
            var folderName = Path.GetFileName(gamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var match = FolderTitleIdRegex.Match(folderName);
            if (match.Success)
                return match.Groups["id"].Value.ToUpperInvariant();
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return null;
    }
}
