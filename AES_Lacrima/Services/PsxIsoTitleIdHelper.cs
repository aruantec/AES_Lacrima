using System;
using System.IO;
using System.Text;

using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.Services
{
    internal static class PsxIsoTitleIdHelper
    {
    private static readonly ILog Log = LogHelper.For(typeof(PsxIsoTitleIdHelper));
        private const int SectorSize = 2048;
        private const int PvdSector = 16;
        private const int PvdOffset = PvdSector * SectorSize;

        private static readonly string[] SerialPrefixes =
        [
            "SLUS", "SCUS", "SLES", "SCES", "SLPS", "SCPS", "SLPM", "SCPM",
            "SLKA", "SCKA", "SLAJ", "SCAJ", "SLEH", "SCEH", "PAPX", "SCZS"
        ];

        public static string? GetTitleId(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            try
            {
                var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

                if (extension == "cue")
                    return GetTitleIdFromCue(filePath);

                if (extension == "m3u")
                    return GetTitleIdFromM3u(filePath);

                if (extension == "pbp")
                    return GetTitleIdFromPbp(filePath);

                return GetTitleIdFromIso(filePath);
            }
            catch
            {
                return null;
            }
        }

        private static string? GetTitleIdFromCue(string cuePath)
        {
            try
            {
                var directory = Path.GetDirectoryName(cuePath);
                var lines = File.ReadAllLines(cuePath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var firstQuote = trimmed.IndexOf('"');
                    var lastQuote = trimmed.LastIndexOf('"');
                    if (firstQuote < 0 || lastQuote <= firstQuote)
                        continue;

                    var binPath = trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    if (!Path.IsPathRooted(binPath) && !string.IsNullOrWhiteSpace(directory))
                        binPath = Path.Combine(directory, binPath);

                    if (File.Exists(binPath))
                        return GetTitleIdFromIso(binPath);
                }
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            return null;
        }

        private static string? GetTitleIdFromM3u(string m3uPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(m3uPath);
                var lines = File.ReadAllLines(m3uPath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                        continue;

                    var romPath = trimmed;
                    if (!Path.IsPathRooted(romPath) && !string.IsNullOrWhiteSpace(directory))
                        romPath = Path.Combine(directory, romPath);

                    if (File.Exists(romPath))
                    {
                        var titleId = GetTitleIdFromIso(romPath);
                        if (!string.IsNullOrWhiteSpace(titleId))
                            return titleId;
                    }
                }
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            return null;
        }

        private static string? GetTitleIdFromPbp(string pbpPath)
        {
            try
            {
                using var stream = new FileStream(pbpPath, FileMode.Open, FileAccess.Read, FileShare.Read, SectorSize, FileOptions.SequentialScan);

                var header = new byte[0x28];
                if (stream.Read(header, 0, header.Length) != header.Length)
                    return null;

                if (header[0] != 0x50 || header[1] != 0x42 || header[2] != 0x50 || header[3] != 0x00)
                    return null;

                var paramSfoOffset = BitConverter.ToUInt32(header, 0x08);
                if (paramSfoOffset >= stream.Length)
                    return null;

                stream.Position = paramSfoOffset;
                var sfoHeader = new byte[20];
                if (stream.Read(sfoHeader, 0, sfoHeader.Length) != sfoHeader.Length)
                    return null;

                if (sfoHeader[0] != 0 || sfoHeader[1] != 'P' || sfoHeader[2] != 'S' || sfoHeader[3] != 'F')
                    return null;

                var keyTableOffset = BitConverter.ToUInt32(sfoHeader, 0x08);
                var dataOffset = BitConverter.ToUInt32(sfoHeader, 0x0C);
                var entryCount = BitConverter.ToUInt32(sfoHeader, 0x10);

                var entrySize = 16;
                var entries = new byte[entryCount * entrySize];
                if (stream.Read(entries, 0, entries.Length) != entries.Length)
                    return null;

                for (var i = 0; i < entryCount; i++)
                {
                    var eoff = i * entrySize;
                    var keyOff = BitConverter.ToUInt16(entries, eoff);
                    var dataLen = BitConverter.ToUInt32(entries, eoff + 0x08);
                    var dataOff = BitConverter.ToUInt32(entries, eoff + 0x0C);

                    var keyPos = paramSfoOffset + keyTableOffset + keyOff;
                    stream.Position = keyPos;
                    var keyBytes = new byte[32];
                    var keyLen = 0;
                    while (keyLen < keyBytes.Length)
                    {
                        var b = stream.ReadByte();
                        if (b <= 0) break;
                        keyBytes[keyLen++] = (byte)b;
                    }
                    var key = Encoding.ASCII.GetString(keyBytes, 0, keyLen);

                    if (key.Equals("TITLE_ID", StringComparison.OrdinalIgnoreCase))
                    {
                        var dataPos = paramSfoOffset + dataOffset + dataOff;
                        stream.Position = dataPos;
                        var valLen = (int)dataLen;
                        var valBytes = new byte[valLen];
                        stream.ReadExactly(valBytes, 0, valLen);
                        var term = Array.IndexOf(valBytes, (byte)0);
                        if (term > 0) valLen = term;
                        var value = Encoding.UTF8.GetString(valBytes, 0, valLen).Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            return null;
        }

        private static string? GetTitleIdFromIso(string isoPath)
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, SectorSize, FileOptions.SequentialScan);

            if (stream.Length < PvdOffset + SectorSize)
                return TryExtractFromFileName(isoPath);

            var pvd = ReadSector(stream, PvdOffset);
            if (pvd == null || pvd[0] != 1)
                return TryExtractFromFileName(isoPath);

            var rootDirEntry = new byte[34];
            Array.Copy(pvd, 156, rootDirEntry, 0, 34);
            var rootExtentLba = BitConverter.ToUInt32(rootDirEntry, 2);
            var rootExtentSize = BitConverter.ToUInt32(rootDirEntry, 10);

            if (rootExtentSize == 0 || rootExtentLba == 0)
                return TryExtractFromFileName(isoPath);

            var rootDirData = ReadSectors(stream, rootExtentLba, rootExtentSize);
            if (rootDirData == null)
                return TryExtractFromFileName(isoPath);

            var systemCnfData = ReadFileFromDirectory(stream, rootDirData, "SYSTEM.CNF");
            if (systemCnfData != null)
            {
                var serial = ParseSystemCnf(systemCnfData);
                if (!string.IsNullOrWhiteSpace(serial))
                    return serial;
            }

            var psxExeData = ReadFileFromDirectory(stream, rootDirData, "PSX.EXE");
            if (psxExeData != null)
            {
                var serial = ScanForSerial(psxExeData, psxExeData.Length);
                if (!string.IsNullOrWhiteSpace(serial))
                    return serial;
            }

            var systemArea = new byte[16 * SectorSize];
            stream.Position = 0;
            var read = stream.Read(systemArea, 0, systemArea.Length);
            if (read >= SectorSize)
            {
                var serial = ScanForSerial(systemArea, read);
                if (!string.IsNullOrWhiteSpace(serial))
                    return serial;
            }

            // Last resort: try to extract from filename
            return TryExtractFromFileName(isoPath);
        }

        private static byte[]? ReadFileFromDirectory(FileStream stream, byte[] dirData, string fileName)
        {
            var pos = 0;
            while (pos < dirData.Length)
            {
                var recLen = dirData[pos];
                if (recLen == 0)
                {
                    var padding = SectorSize - (pos % SectorSize);
                    if (padding < SectorSize)
                        pos += padding;
                    else
                        pos++;
                    continue;
                }

                var extentLba = BitConverter.ToUInt32(dirData, pos + 2);
                var dataSize = BitConverter.ToUInt32(dirData, pos + 10);
                var nameLen = dirData[pos + 32];

                if (nameLen > 0 && pos + 33 + nameLen <= dirData.Length)
                {
                    var name = Encoding.ASCII.GetString(dirData, pos + 33, nameLen);
                    var semiIdx = name.IndexOf(';');
                    if (semiIdx >= 0)
                        name = name[..semiIdx];

                    if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (dataSize == 0 || extentLba == 0)
                            return null;

                        return ReadSectors(stream, extentLba, dataSize);
                    }
                }

                pos += recLen;
            }

            return null;
        }

        private static byte[]? ReadSectors(FileStream stream, uint startLba, uint totalSize)
        {
            // Cap to reasonable size to avoid memory issues
            const uint MaxReadSize = 64 * 1024 * 1024; // 64MB max
            var sizeToRead = Math.Min(totalSize, MaxReadSize);
            var result = new byte[sizeToRead];
            var offset = 0;
            var remaining = (int)sizeToRead;
            var currentLba = startLba;

            while (remaining > 0)
            {
                var toRead = Math.Min(remaining, SectorSize);
                var sectorData = ReadSector(stream, (long)currentLba * SectorSize);
                if (sectorData == null)
                    return null;

                Array.Copy(sectorData, 0, result, offset, toRead);
                offset += toRead;
                remaining -= toRead;
                currentLba++;
            }

            return result;
        }

        private static string? ParseSystemCnf(byte[] data)
        {
            var text = Encoding.ASCII.GetString(data);
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0)
                    continue;

                var key = trimmed[..eqIdx].Trim();
                var value = trimmed[(eqIdx + 1)..].Trim();

                if (!string.Equals(key, "BOOT", StringComparison.OrdinalIgnoreCase))
                    continue;

                var exePath = value;
                if (exePath.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase))
                    exePath = exePath["cdrom:".Length..];

                while (exePath.Length > 0 && (exePath[0] == '/' || exePath[0] == '\\'))
                    exePath = exePath[1..];

                var slashIdx = exePath.LastIndexOf('\\');
                if (slashIdx >= 0)
                    exePath = exePath[(slashIdx + 1)..];

                var semiIdx = exePath.IndexOf(';');
                if (semiIdx >= 0)
                    exePath = exePath[..semiIdx];

                if (string.IsNullOrWhiteSpace(exePath))
                    continue;

                var upper = exePath.ToUpperInvariant();
                foreach (var prefix in SerialPrefixes)
                {
                    var idx = upper.IndexOf(prefix, StringComparison.Ordinal);
                    if (idx < 0)
                        continue;

                    var start = idx;
                    var end = start + 4; // Start after the 4-letter prefix
                    
                    // Collect alphanumeric characters after the prefix
                    while (end < upper.Length && end - start < 16)
                    {
                        var ch = upper[end];
                        if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                            break;
                        end++;
                    }

                    // Serial should be at least 9 characters (e.g., "SLUS-00001")
                    if (end - start >= 9)
                    {
                        var serial = upper[start..end].Trim();
                        serial = serial.Replace('_', '-').Replace('.', '-');
                        return serial;
                    }
                }

                return exePath.ToUpperInvariant();
            }

            return null;
        }

        private static string? ScanForSerial(byte[] data, int length)
        {
            for (var i = 0; i <= length - 9; i++) // Ensure we have at least 9 chars for serial
            {
                if (data[i] != 'S' && data[i] != 's')
                    continue;

                foreach (var prefix in SerialPrefixes)
                {
                    if (i + prefix.Length > length)
                        continue;

                    var match = true;
                    for (var c = 0; c < prefix.Length; c++)
                    {
                        var ch = (char)data[i + c];
                        if (ch != prefix[c] && ch != char.ToLower(prefix[c]))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (!match)
                        continue;

                    // Found a prefix match, now collect the full serial
                    var end = i + prefix.Length;
                    while (end < length && end - i < 16)
                    {
                        var ch = (char)data[end];
                        // Stop at whitespace, null, or invalid characters
                        if (ch == ' ' || ch == '\0' || ch == '\r' || ch == '\n' || ch == (char)0xFF || !char.IsLetterOrDigit(ch))
                            break;
                        end++;
                    }

                    // Serial must be at least 9 characters (e.g., "SLUS-00001")
                    if (end - i >= 9 && end - i <= 16)
                    {
                        var serial = Encoding.ASCII.GetString(data, i, end - i).Trim();
                        serial = serial.Replace('_', '-').Replace('.', '-');
                        return serial.ToUpperInvariant();
                    }
                }
            }

            return null;
        }

        private static byte[]? ReadSector(FileStream stream, long offset)
        {
            try
            {
                var buffer = new byte[SectorSize];
                stream.Position = offset;
                var read = stream.Read(buffer, 0, SectorSize);
                // Return the buffer even if we don't get a full sector, as long as we read something
                return read > 0 ? (read == SectorSize ? buffer : buffer[..read]) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? TryExtractFromFileName(string filePath)
        {
            // Try to extract serial from filename as a last resort
            // Filenames might be like "SLUS-00001 - Game Name.iso"
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            var upper = fileName.ToUpperInvariant();
            foreach (var prefix in SerialPrefixes)
            {
                var idx = upper.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0)
                    continue;

                var start = idx;
                var end = start + 4;

                // Collect alphanumeric characters and hyphens after the prefix
                while (end < upper.Length && end - start < 16)
                {
                    var ch = upper[end];
                    if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                        break;
                    end++;
                }

                // Serial should be at least 9 characters
                if (end - start >= 9)
                {
                    var serial = upper[start..end].Trim();
                    serial = serial.Replace('_', '-').Replace('.', '-');
                    return serial;
                }
            }

            return null;
        }
    }
}
