using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
                if (Directory.Exists(sceSysDirectory))
                {
                    if (File.Exists(Path.Combine(sceSysDirectory, "param.sfo")))
                        return true;
                }

                return File.Exists(Path.Combine(path, "eboot.bin")) ||
                       File.Exists(Path.Combine(path, "icon0.png"));
            }
            catch
            {
                return false;
            }
        }

        public static string? GetTitleId(string? path)
        {
            var sfoPath = GetParamSfoPath(path);
            return sfoPath != null ? TryReadValueFromParamSfo(sfoPath, "TITLE_ID") : null;
        }

        public static string? GetTitleName(string? path)
        {
            var sfoPath = GetParamSfoPath(path);
            return sfoPath != null ? TryReadValueFromParamSfo(sfoPath, "TITLE") : null;
        }

        public static string? GetVersion(string? path)
        {
            var sfoPath = GetParamSfoPath(path);
            return sfoPath != null ? TryReadValueFromParamSfo(sfoPath, "APP_VER") : null;
        }

        private static string? GetParamSfoPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var normalizedPath = Path.GetFullPath(path);
                var directSfo = Path.Combine(normalizedPath, "sce_sys", "param.sfo");
                if (File.Exists(directSfo))
                    return directSfo;

                if (!Directory.Exists(normalizedPath))
                    return null;

                foreach (var candidate in Directory.EnumerateDirectories(normalizedPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var candidateSfo = Path.Combine(candidate, "sce_sys", "param.sfo");
                    if (File.Exists(candidateSfo))
                        return candidateSfo;
                }

                foreach (var candidate in Directory.EnumerateDirectories(normalizedPath, "*", SearchOption.AllDirectories))
                {
                    var candidateSfo = Path.Combine(candidate, "sce_sys", "param.sfo");
                    if (File.Exists(candidateSfo))
                        return candidateSfo;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string? TryReadValueFromParamSfo(string paramSfoPath, string keyName)
        {
            try
            {
                using var stream = File.OpenRead(paramSfoPath);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

                if (stream.Length < 20)
                    return null;

                var magic = reader.ReadUInt32();
                if (magic != 0x46535000) // \0PSF
                    return null;

                _ = reader.ReadUInt32(); // version
                var keyTableStart = reader.ReadUInt32();
                var dataTableStart = reader.ReadUInt32();
                var entryCount = reader.ReadUInt32();

                for (var index = 0u; index < entryCount; index++)
                {
                    var entryOffset = 20 + (index * 16);
                    if (entryOffset + 16 > stream.Length)
                        break;

                    stream.Position = entryOffset;

                    var keyOffset = reader.ReadUInt16();
                    _ = reader.ReadByte(); // data format
                    _ = reader.ReadByte(); // data type / padding
                    var dataLength = reader.ReadUInt32();
                    _ = reader.ReadUInt32(); // data size in file / max length
                    var dataOffset = reader.ReadUInt32();

                    var key = ReadNullTerminatedAscii(stream, keyTableStart + keyOffset);
                    if (!string.Equals(key, keyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (dataLength == 0)
                        return null;

                    var value = ReadNullTerminatedUtf8(stream, dataTableStart + dataOffset, (int)dataLength);
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim('\0', ' ', '\t', '\r', '\n');
                }
            }
            catch
            {
            }

            return null;
        }

        private static string ReadNullTerminatedAscii(Stream stream, uint offset)
        {
            var currentPos = stream.Position;
            stream.Position = offset;
            var bytes = new List<byte>();
            int b;
            while ((b = stream.ReadByte()) > 0)
                bytes.Add((byte)b);

            stream.Position = currentPos;
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static string ReadNullTerminatedUtf8(Stream stream, uint offset, int maxLength)
        {
            var currentPos = stream.Position;
            stream.Position = offset;
            var bytes = new byte[maxLength];
            var read = stream.Read(bytes, 0, maxLength);
            var actualLength = Array.IndexOf(bytes, (byte)0);
            if (actualLength < 0)
                actualLength = read;

            stream.Position = currentPos;
            return Encoding.UTF8.GetString(bytes, 0, actualLength);
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