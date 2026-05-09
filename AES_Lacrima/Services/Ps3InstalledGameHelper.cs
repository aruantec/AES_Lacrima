using System;
using System.IO;
using System.Linq;
using System.Text;

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

                return !string.IsNullOrWhiteSpace(GetPreferredIconPath(path)) ||
                       !string.IsNullOrWhiteSpace(GetTitleId(path));
            }
            catch
            {
                return false;
            }
        }

        public static string? GetTitleId(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                foreach (var candidateDirectory in GetCandidateDirectories(path))
                {
                    var paramSfoPath = Path.Combine(candidateDirectory, "PARAM.SFO");
                    if (!File.Exists(paramSfoPath))
                        continue;

                    var titleId = TryReadTitleIdFromParamSfo(paramSfoPath);
                    if (!string.IsNullOrWhiteSpace(titleId))
                        return titleId;
                }
            }
            catch
            {
            }

            return null;
        }

        public static string? GetTitleName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                foreach (var candidateDirectory in GetCandidateDirectories(path))
                {
                    var paramSfoPath = Path.Combine(candidateDirectory, "PARAM.SFO");
                    if (!File.Exists(paramSfoPath))
                        continue;

                    var titleName = TryReadValueFromParamSfo(paramSfoPath, "TITLE");
                    if (!string.IsNullOrWhiteSpace(titleName))
                        return titleName;
                }
            }
            catch
            {
            }

            return null;
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

        private static string? TryReadTitleIdFromParamSfo(string paramSfoPath)
            => TryReadValueFromParamSfo(paramSfoPath, "TITLE_ID");

        private static string? TryReadValueFromParamSfo(string paramSfoPath, string keyName)
        {
            try
            {
                using var stream = File.OpenRead(paramSfoPath);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

                if (stream.Length < 20)
                    return null;

                var magic = reader.ReadUInt32();
                if (magic != 0x46535000)
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
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string ReadNullTerminatedAscii(Stream stream, uint position)
        {
            if (position >= stream.Length)
                return string.Empty;

            stream.Position = position;

            using var buffer = new MemoryStream();
            while (stream.Position < stream.Length)
            {
                var value = stream.ReadByte();
                if (value <= 0)
                    break;

                buffer.WriteByte((byte)value);
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        private static string ReadNullTerminatedUtf8(Stream stream, uint position, int maxLength)
        {
            if (position >= stream.Length || maxLength <= 0)
                return string.Empty;

            stream.Position = position;
            var bytes = new byte[Math.Min(maxLength, (int)(stream.Length - position))];
            var read = stream.Read(bytes, 0, bytes.Length);
            if (read <= 0)
                return string.Empty;

            var terminatorIndex = Array.IndexOf(bytes, (byte)0, 0, read);
            if (terminatorIndex >= 0)
                read = terminatorIndex;

            return Encoding.UTF8.GetString(bytes, 0, read).Trim();
        }
    }
}
