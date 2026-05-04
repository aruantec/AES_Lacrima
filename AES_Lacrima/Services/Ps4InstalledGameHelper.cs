using System;
using System.IO;

namespace AES_Lacrima.Services
{
    internal static class Ps4InstalledGameHelper
    {
        public static bool IsInstalledGameFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                if (!Directory.Exists(path))
                    return false;

                var sceSysDirectory = Path.Combine(path, "sce_sys");
                if (!Directory.Exists(sceSysDirectory))
                    return false;

                return File.Exists(Path.Combine(path, "eboot.bin")) ||
                       File.Exists(Path.Combine(sceSysDirectory, "param.sfo")) ||
                       File.Exists(Path.Combine(sceSysDirectory, "icon0.png")) ||
                       File.Exists(Path.Combine(path, "icon0.png"));
            }
            catch
            {
                return false;
            }
        }

        public static string? GetPreferredIconPath(string? path)
        {
            if (!IsInstalledGameFolder(path))
                return null;

            try
            {
                var sceSysIconPath = Path.Combine(path!, "sce_sys", "icon0.png");
                if (File.Exists(sceSysIconPath))
                    return sceSysIconPath;

                var rootIconPath = Path.Combine(path!, "icon0.png");
                return File.Exists(rootIconPath) ? rootIconPath : null;
            }
            catch
            {
                return null;
            }
        }
    }
}