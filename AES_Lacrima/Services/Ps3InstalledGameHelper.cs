using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AES_Controls.Helpers;
using AES_Core.IO;

namespace AES_Lacrima.Services
{
    internal static class Ps3InstalledGameHelper
    {
        private const string GameIdBootPrefix = "%RPCS3_GAMEID%:";

        private static readonly Regex TitleIdPathRegex = new(
            @"\b(BLUS|BLES|BLJM|BLJS|NPUB|NPEB|NPJB|NPUJ|NPUX)\d{5}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        public static string? GetPreferredBootPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var normalizedPath = path.Trim();
                if (File.Exists(normalizedPath))
                    return normalizedPath;

                if (!Directory.Exists(normalizedPath))
                    return null;

                foreach (var candidateDirectory in GetCandidateDirectories(normalizedPath))
                {
                    var ebootPath = Path.Combine(candidateDirectory, "EBOOT.BIN");
                    if (File.Exists(ebootPath))
                        return ebootPath;

                    var ebootPathLower = Path.Combine(candidateDirectory, "eboot.bin");
                    if (File.Exists(ebootPathLower))
                        return ebootPathLower;
                }
            }
            catch
            {
            }

            return null;
        }

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

        public static string? GetTitleId(string? path) => ResolveTitleId(path);

        public static string? ResolveTitleId(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalizedPath = path.Trim();

            if (normalizedPath.StartsWith(GameIdBootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var bootTitleId = normalizedPath[GameIdBootPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(bootTitleId))
                    return bootTitleId;
            }

            try
            {
                foreach (var candidateDirectory in GetCandidateDirectories(normalizedPath))
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

            var cachedTitleId = TryReadTitleIdFromMetadataCache(normalizedPath);
            if (!string.IsNullOrWhiteSpace(cachedTitleId))
                return cachedTitleId;

            return TryExtractTitleIdFromPath(normalizedPath);
        }

        private static string? TryReadTitleIdFromMetadataCache(string path)
        {
            try
            {
                var cacheId = BinaryMetadataHelper.GetCacheId(path);
                var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                return string.IsNullOrWhiteSpace(metadata?.Ps3TitleId) ? null : metadata.Ps3TitleId.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryExtractTitleIdFromPath(string path)
        {
            foreach (var segment in EnumeratePathSegments(path))
            {
                var match = TitleIdPathRegex.Match(segment);
                if (match.Success)
                    return match.Value.ToUpperInvariant();
            }

            return null;
        }

        private static IEnumerable<string> EnumeratePathSegments(string path)
        {
            var current = path;
            while (!string.IsNullOrWhiteSpace(current))
            {
                yield return Path.GetFileName(current);
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                    yield break;
                current = parent;
            }
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

        public static string? GetVersion(string? path)
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

                    var version = TryReadValueFromParamSfo(paramSfoPath, "APP_VER");
                    if (!string.IsNullOrWhiteSpace(version))
                        return version;
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
