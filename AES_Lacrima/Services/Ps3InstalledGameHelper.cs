using System;
using System.IO;

namespace AES_Lacrima.Services
{
    internal static class Ps3InstalledGameHelper
    {
        public static bool IsInstalledGameFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                if (!Directory.Exists(path))
                    return false;

                return !string.IsNullOrWhiteSpace(GetPreferredIconPath(path));
            }
            catch
            {
                return false;
            }
        }

        public static string? GetPreferredIconPath(string? path)
            => FindArtworkPath(path, ["icon0.png", "ICON0.PNG"]);

        public static string? GetPreferredBackCoverPath(string? path)
            => FindArtworkPath(path, ["pic1.png", "PIC1.PNG"]);

        private static string? FindArtworkPath(string? path, string[] fileNames)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                foreach (var candidateDirectory in GetCandidateDirectories(path))
                {
                    if (!Directory.Exists(candidateDirectory))
                        continue;

                    foreach (var fileName in fileNames)
                    {
                        var candidatePath = Path.Combine(candidateDirectory, fileName);
                        if (File.Exists(candidatePath))
                            return candidatePath;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string[] GetCandidateDirectories(string path)
        {
            var root = path.Trim();
            return new[]
            {
                root,
                Path.Combine(root, "PS3_GAME"),
                Path.Combine(root, "PS3_GAME", "USRDIR"),
                Path.Combine(root, "USRDIR")
            };
        }
    }
}
