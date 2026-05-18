using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AES_Lacrima.Services.Emulation
{
    /// <summary>
    /// Disc type context for specialized extraction logic.
    /// </summary>
    public enum DiscSection
    {
        /// <summary>Auto-detect the disc type (PSX, PS2, or unknown)</summary>
        Auto = 0,
        /// <summary>PlayStation 1 disc - uses BOOT key in SYSTEM.CNF</summary>
        PSX = 1,
        /// <summary>PlayStation 2 disc - uses BOOT2 key in SYSTEM.CNF</summary>
        PS2 = 2,
        /// <summary>Dreamcast disc - uses IP.BIN metadata</summary>
        Dreamcast = 3
    }

    /// <summary>
    /// Main inspector with utilities to detect/normalize ROM headers and inspect disc images.
    /// Relies on external types RomInfo, RomFormat and Crc32 in the same namespace/project.
    /// </summary>
    public static class RomInspector
    {
        private const long FullHashThreshold = 200L * 1024 * 1024; // 200 MB
        private const long NormSha1Threshold = 512L * 1024 * 1024; // 512 MB

        /// <summary>
        /// Inspect a file path or an archive entry (zip).
        /// </summary>
        public static RomInfo Inspect(string path)
        {
            return Inspect(path, DiscSection.Auto);
        }

        /// <summary>
        /// Inspect a file path with explicit disc section context for specialized extraction.
        /// </summary>
        public static RomInfo Inspect(string path, DiscSection section)
        {
            // Check if file exists before attempting to inspect
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new RomInfo { Format = RomFormat.Unknown };

            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".zip")
                return InspectArchive(path);

            if (ext == ".cue")
            {
                try
                {
                    var bin = TryResolveCueBin(path);
                    if (!string.IsNullOrEmpty(bin) && File.Exists(bin))
                    {
                        using var fs = File.Open(bin, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return InspectFromStream(fs, $"{path}::{Path.GetFileName(bin)}", section);
                    }
                }
                catch { /* swallow and fallback */ }
            }

            if (ext == ".gdi")
            {
                try
                {
                    var bin = TryResolveGdiBin(path);
                    if (!string.IsNullOrEmpty(bin) && File.Exists(bin))
                    {
                        using var fs = File.Open(bin, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return InspectFromStream(fs, $"{path}::{Path.GetFileName(bin)}", section);
                    }

                    // NEW: Try to inspect the GDI file itself for embedded disc data
                    using var gdiStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var needle = Encoding.ASCII.GetBytes("CD001");
                    long cd001Pos = StreamIndexOf(gdiStream, needle, Math.Min(gdiStream.Length, 50L * 1024 * 1024));
                    
                    if (cd001Pos >= 0)
                    {
                        // Calculate offset to start of ISO data (CD001 is at sector 16, offset 0x8001)
                        long isoStart = cd001Pos - 0x8001;
                        if (isoStart >= 0)
                        {
                            var offsetStream = new OffsetStream(gdiStream, isoStart);
                            var discInfo = InspectDiscFromStream(offsetStream, path, section);
                            if (!string.IsNullOrEmpty(discInfo.DiscVolumeLabel) || 
                                !string.IsNullOrEmpty(discInfo.SystemCnf) || 
                                !string.IsNullOrEmpty(discInfo.GameId))
                            {
                                return discInfo;
                            }
                        }
                    }

                    var gi = TryExtractInfoFromGdi(path);
                    if (gi != null) return gi;
                }
                catch { /* swallow and fallback */ }
            }

            if (ext == ".cdi")
            {
                try
                {
                    var bin = TryResolveCdiBin(path);
                    if (!string.IsNullOrEmpty(bin) && File.Exists(bin))
                    {
                        using var fs = File.Open(bin, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return InspectFromStream(fs, $"{path}::{Path.GetFileName(bin)}", section);
                    }

                    // NEW: CDI files often have embedded disc data - scan for CD001
                    using var cdiStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var needle = Encoding.ASCII.GetBytes("CD001");
                    long cd001Pos = StreamIndexOf(cdiStream, needle, Math.Min(cdiStream.Length, 50L * 1024 * 1024));
                    
                    if (cd001Pos >= 0)
                    {
                        // Calculate offset to start of ISO data (CD001 is at sector 16, offset 0x8001)
                        long isoStart = cd001Pos - 0x8001;
                        if (isoStart >= 0)
                        {
                            var offsetStream = new OffsetStream(cdiStream, isoStart);
                            var discInfo = InspectDiscFromStream(offsetStream, path);
                            if (!string.IsNullOrEmpty(discInfo.DiscVolumeLabel) || 
                                !string.IsNullOrEmpty(discInfo.SystemCnf) || 
                                !string.IsNullOrEmpty(discInfo.GameId))
                            {
                                return discInfo;
                            }
                        }
                    }

                    var ci = TryExtractInfoFromCdi(path);
                    if (ci != null) return ci;
                    var cdfb = InspectCdiFallback(path);
                    if (cdfb != null) return cdfb;
                }
                catch { /* ignore and fallback to stream inspection */ }
            }

            using var fileStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // CDI heuristic: scan for CD001 inside first 10MB
            if (ext == ".cdi")
            {
                var needle = Encoding.ASCII.GetBytes("CD001");
                long f = StreamIndexOf(fileStream, needle, Math.Min(fileStream.Length, 10L * 1024 * 1024));
                if (f >= 0)
                    return InspectFromStream(fileStream, path, section);
            }

            return InspectFromStream(fileStream, path, section);
        }
        
        private static RomInfo? InspectCdiFallback(string cdiPath)
        {
            try
            {
                var baseDir = Path.GetDirectoryName(cdiPath) ?? ".";
                using var fs = File.OpenRead(cdiPath);

                int scan = (int)Math.Min(2L * 1024 * 1024, fs.Length); // limit to 2MB for perf
                var buf = new byte[scan];
                fs.Seek(0, SeekOrigin.Begin);
                int r = fs.Read(buf, 0, scan);
                if (r <= 0) return null;

                var text = Encoding.ASCII.GetString(buf, 0, r);

                // 1) Look for explicit filename tokens (common image extensions)
                var rxFile = new Regex(@"(?<p>[^\s""',\\/:]{1,200}\.(bin|iso|img|raw|cue))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var m = rxFile.Match(text);
                if (m.Success)
                {
                    var rel = m.Groups["p"].Value.Trim().Trim('"', '\'');
                    var cleaned = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var candidate = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(baseDir, cleaned);

                    // try direct existence and some common alternatives quickly
                    if (File.Exists(candidate)) return Inspect(candidate);
                    var tryExts = new[] { "", ".bin", ".iso", ".img", ".raw" };
                    foreach (var ext in tryExts)
                    {
                        var alt = candidate + ext;
                        if (File.Exists(alt)) return Inspect(alt);
                        var alt2 = Path.Combine(Path.GetDirectoryName(candidate) ?? baseDir, Path.GetFileName(candidate).ToUpperInvariant() + ext);
                        if (File.Exists(alt2)) return Inspect(alt2);
                    }

                    // not found on disk: return minimal hint based on filename
                    var ri = new RomInfo { FilePath = cdiPath, Format = RomFormat.Unknown };
                    var nameOnly = Path.GetFileNameWithoutExtension(rel);
                    if (IsLikelyGameIdFilename(nameOnly)) ri.GameId = nameOnly;
                    else ri.InternalTitle = nameOnly;
                    return ri;
                }

                // 2) Look for "CD001" (ISO PVD marker) in scanned header
                int idx = text.IndexOf("CD001", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var ri = new RomInfo { FilePath = cdiPath, Format = RomFormat.Iso };

                    // attempt to read volume label if PVD lies inside the scanned buffer
                    // PVD logical block: "CD001" is at offset 1 of the 2048-byte PVD; volume ID at offset 0x40 (32 bytes)
                    int pvdStart = idx - 1;
                    int volOff = pvdStart + 0x40;
                    if (volOff >= 0 && volOff + 32 <= r)
                    {
                        ri.DiscVolumeLabel = CleanAsciiText(Encoding.ASCII.GetString(buf, volOff, 32));
                    }

                    // attempt quick game id extract from header text if present (supports PSX and PS2)
                    var clean = CleanAsciiText(text);
                    var idRx = new Regex(@"\b(SLES|SLUS|SCUS|SLPS|SCPS|SLPM|SCPH|SCES|SCUP|SCKA|SCED|SCEO|SCEW|SLJM|SLKA)[\-_]?\d{3,}\b", RegexOptions.IgnoreCase);
                    var idm = idRx.Match(clean);
                    if (idm.Success) ri.GameId = idm.Value.ToUpperInvariant();

                    return ri;
                }
            }
            catch
            {
                // silent fallback - do not disrupt main flow
            }

            return null;
        }

        /// <summary>
        /// Inspect a zip archive and choose the largest candidate ROM entry.
        /// </summary>
        public static RomInfo InspectArchive(string zipPath)
        {
            var archiveHashes = ComputeArchiveHashes(zipPath);

            using var zf = File.OpenRead(zipPath);
            using var za = new ZipArchive(zf, ZipArchiveMode.Read, leaveOpen: false);

            string[] exts = new[] { ".z64", ".v64", ".n64", ".rom", ".bin", ".iso", ".img", ".sfc", ".smc", ".nes", ".md", ".gen", ".smd", ".gb", ".gbc", ".gba", ".sms", ".gg" };
            var entry = za.Entries
                          .Where(e => !string.IsNullOrEmpty(Path.GetExtension(e.FullName)) &&
                                      exts.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()))
                          .OrderByDescending(e => e.Length)
                          .FirstOrDefault();

            if (entry == null)
            {
                // Fallback: pick the largest file that isn't a text file or common metadata
                var candidate = za.Entries
                                  .Where(e => !string.IsNullOrEmpty(Path.GetExtension(e.FullName)))
                                  .OrderByDescending(e => e.Length)
                                  .FirstOrDefault();
                
                if (candidate != null) entry = candidate;
                else return new RomInfo { FilePath = zipPath, Format = RomFormat.Unknown };
            }

            var temp = Path.Combine(Path.GetTempPath(), $"romtools_{Guid.NewGuid():N}{Path.GetExtension(entry.FullName)}");
            try
            {
                using (var entryStream = entry.Open())
                using (var outFs = File.Create(temp))
                    entryStream.CopyTo(outFs);

                using var fs = File.Open(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
                var displayPath = $"{zipPath}::{entry.FullName}";
                var info = InspectFromStream(fs, displayPath);
                info.ArchiveMd5 = archiveHashes.Md5;
                info.ArchiveSha1 = archiveHashes.Sha1;
                info.ArchiveCrc32 = archiveHashes.Crc32;
                return info;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static (string? Md5, string? Sha1, string? Crc32) ComputeArchiveHashes(string path)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var md5 = MD5.Create();
                using var sha1 = SHA1.Create();
                var buffer = new byte[128 * 1024];
                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    md5.TransformBlock(buffer, 0, read, null, 0);
                    sha1.TransformBlock(buffer, 0, read, null, 0);
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                uint crc = RomToolHelpers.Crc32.Compute(stream);

                string? md5Hex = md5.Hash is not null
                    ? BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant()
                    : null;
                string? sha1Hex = sha1.Hash is not null
                    ? BitConverter.ToString(sha1.Hash).Replace("-", "").ToLowerInvariant()
                    : null;

                return (md5Hex, sha1Hex, $"0x{crc:X8}");
            }
            catch
            {
                return (null, null, null);
            }
        }

        /// <summary>
        /// Inspect a seekable stream for ROM/disc hints and basic hashes.
        /// </summary>
        private static RomInfo InspectFromStream(Stream fs, string displayPath, DiscSection section = DiscSection.Auto)
        {
            if (!fs.CanSeek)
                throw new ArgumentException("InspectFromStream requires a seekable stream");

            var info = new RomInfo { FilePath = displayPath };

            const int headerBufSize = 64 * 1024; // covers PVD at 0x8000
            int toRead = (int)Math.Min(headerBufSize, fs.Length);
            var headerBuf = new byte[toRead];
            fs.Seek(0, SeekOrigin.Begin);
            int got = fs.Read(headerBuf, 0, toRead);
            if (got < 4) return info;

            var normHeader = DetectAndNormalizeHeader(headerBuf, got, out RomFormat detectedFormat);
            info.Format = detectedFormat;

            // Extract internal title based on format
            if (info.Format == RomFormat.N64 || info.Format == RomFormat.Z64 || info.Format == RomFormat.V64)
            {
                if (normHeader.Length >= 0x20 + 20)
                {
                    var titleBytes = normHeader.Skip(0x20).Take(20).ToArray();
                    info.InternalTitle = Encoding.ASCII.GetString(titleBytes).TrimEnd('\0', ' ');
                }
            }
            else if (info.Format == RomFormat.Snes)
            {
                info.InternalTitle = ExtractSnesTitle(fs);
            }
            else if (info.Format == RomFormat.Nes)
            {
                // NES doesn't have an internal title in the ROM header, but we've identified it as NES
            }
            else if (info.Format == RomFormat.Genesis)
            {
                info.InternalTitle = ExtractGenesisTitle(fs);
            }
            else if (info.Format == RomFormat.Gba)
            {
                info.InternalTitle = ExtractGbaTitle(fs);
            }
            else if (info.Format == RomFormat.Gbx)
            {
                info.InternalTitle = ExtractGbxTitle(fs);
            }

            // Sanitize extracted title
            if (!string.IsNullOrEmpty(info.InternalTitle))
            {
                info.InternalTitle = CleanAsciiText(info.InternalTitle);
                if (!IsValidTitle(info.InternalTitle))
                    info.InternalTitle = null;
            }

            // If ISO detected by header heuristics, perform specialized disc inspection
            if (info.Format == RomFormat.Iso)
            {
                var isoInfo = InspectDiscFromStream(fs, displayPath, section);
                info.DiscVolumeLabel = isoInfo.DiscVolumeLabel;
                info.SystemCnf = isoInfo.SystemCnf;
                info.BootFilePath = isoInfo.BootFilePath;
                info.GameId = isoInfo.GameId;
                info.Md5 = isoInfo.Md5;
                info.Sha1 = isoInfo.Sha1;
                info.Crc32 = isoInfo.Crc32;
                return info;
            }

            // Heuristic disc inspection for common disc extensions or large files (covers MODE2 2048 BINs)
            var ext = Path.GetExtension(displayPath).ToLowerInvariant();
            var discExts = new[] { ".bin", ".iso", ".img", ".cdi", ".gdi" };
            if (discExts.Contains(ext) || fs.Length >= 0x8000)
            {
                var isoInfo = InspectDiscFromStream(fs, displayPath, section);
                if (!string.IsNullOrEmpty(isoInfo.SystemCnf) ||
                    !string.IsNullOrEmpty(isoInfo.DiscVolumeLabel) ||
                    !string.IsNullOrEmpty(isoInfo.GameId))
                {
                    info.Format = RomFormat.Iso;
                    info.DiscVolumeLabel = isoInfo.DiscVolumeLabel;
                    info.SystemCnf = isoInfo.SystemCnf;
                    info.BootFilePath = isoInfo.BootFilePath;
                    info.GameId = isoInfo.GameId;
                    info.Md5 = isoInfo.Md5;
                    info.Sha1 = isoInfo.Sha1;
                    info.Crc32 = isoInfo.Crc32;
                    return info;
                }
            }

            // Header CRCs at offsets 0x10/0x14 (big-endian stored in normalized header)
            if (normHeader.Length >= 0x18 + 4 && (info.Format == RomFormat.N64 || info.Format == RomFormat.Z64 || info.Format == RomFormat.V64))
            {
                info.HeaderCrc1 = ReadUInt32BE(normHeader, 0x10);
                info.HeaderCrc2 = ReadUInt32BE(normHeader, 0x14);
            }

            // Full-file hashes only for reasonably small files
            if (fs.Length <= FullHashThreshold)
            {
                fs.Seek(0, SeekOrigin.Begin);
                using var md5 = MD5.Create();
                using var sha1 = SHA1.Create();
                info.Md5 = BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                fs.Seek(0, SeekOrigin.Begin);
                info.Sha1 = BitConverter.ToString(sha1.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                fs.Seek(0, SeekOrigin.Begin);
                info.Crc32 = $"0x{RomToolHelpers.Crc32.Compute(fs):X8}";
            }

            if (string.IsNullOrEmpty(info.Crc32))
            {
                fs.Seek(0, SeekOrigin.Begin);
                info.Crc32 = $"0x{RomToolHelpers.Crc32.Compute(fs):X8}";
            }

            // Normalized SHA1 when reasonable
            if (fs.Length <= NormSha1Threshold)
            {
                fs.Seek(0, SeekOrigin.Begin);
                info.NormSha1 = ComputeNormalizedSha1(fs, detectedFormat);
            }

            return info;
        }

        /// <summary>
        /// Inspect ISO/BIN content without reading entire file; supports 2048 and 2352 sector sizes and header shifts.
        /// </summary>
        private static RomInfo InspectDiscFromStream(Stream fs, string displayPath, DiscSection section = DiscSection.Auto)
        {
            var info = new RomInfo { FilePath = displayPath, Format = RomFormat.Iso };
            if (!fs.CanSeek) return info;

            int[] sectorCandidates = new[] { 2048, 2352 };
            int foundSectorSize = 0;
            const int pvdSectorIndex = 16;
            byte[]? pvd = null;
            int pvdHeaderShift = 0; // shift inside physical sector where PVD data begins

            var needleCd001 = Encoding.ASCII.GetBytes("CD001");

            foreach (var sectorSize in sectorCandidates)
            {
                long pvdOffset = (long)pvdSectorIndex * sectorSize;
                if (fs.Length >= pvdOffset + sectorSize)
                {
                    var tmp = new byte[sectorSize];
                    fs.Seek(pvdOffset, SeekOrigin.Begin);
                    int r = fs.Read(tmp, 0, tmp.Length);
                    if (r == tmp.Length)
                    {
                        int idx = IndexOf(tmp, needleCd001, 0, tmp.Length);
                        if (idx >= 0)
                        {
                            // CD001 found at position idx inside the physical sector
                            pvd = tmp;
                            foundSectorSize = sectorSize;
                            pvdHeaderShift = Math.Max(0, idx - 1); // PVD signature normally at offset 1
                            break;
                        }
                    }
                }
            }

            if (foundSectorSize != 0 && pvd != null)
            {
                try
                {
                    int labelOffInPvd = pvdHeaderShift + 0x28;
                    int avail = Math.Max(0, Math.Min(pvd.Length - labelOffInPvd, 32));
                    if (avail > 0)
                        info.DiscVolumeLabel = Encoding.ASCII.GetString(pvd, labelOffInPvd, avail).Trim('\0', ' ');
                }
                catch { info.DiscVolumeLabel = ""; }

                int rootRecOff = pvdHeaderShift + 0x9C;
                if (rootRecOff + 34 <= pvd.Length)
                {
                    int drLen = pvd[rootRecOff];
                    if (drLen >= 34)
                    {
                        uint extent = BitConverter.ToUInt32(pvd, rootRecOff + 2);
                        uint dataLen = BitConverter.ToUInt32(pvd, rootRecOff + 10);
                        long dirOffsetInStream = (long)extent * foundSectorSize + pvdHeaderShift;
                        int toRead = (int)Math.Min(dataLen, 64 * 1024); // limit read size for directory parsing
                        if (dirOffsetInStream + toRead <= fs.Length)
                        {
                            var dirBuf = new byte[toRead];
                            fs.Seek(dirOffsetInStream, SeekOrigin.Begin);
                            fs.ReadExactly(dirBuf);
                            ParseDirectoryBufferForFiles(dirBuf, info, dirOffsetInStream, fs, foundSectorSize, section);
                        }
                        else if (dirOffsetInStream < fs.Length)
                        {
                            var available = (int)(fs.Length - dirOffsetInStream);
                            var dirBuf = new byte[available];
                            fs.Seek(dirOffsetInStream, SeekOrigin.Begin);
                            fs.ReadExactly(dirBuf);
                            ParseDirectoryBufferForFiles(dirBuf, info, dirOffsetInStream, fs, foundSectorSize, section);
                        }
                    }
                }
            }

            // Fallback: locate SYSTEM.CNF;1 or IP.BIN in first 20MB; if PVD failed, try heuristics
            if (string.IsNullOrEmpty(info.SystemCnf))
            {
                long maxScan = Math.Min(fs.Length, 20L * 1024 * 1024); // limit fallback scan to first 20 MB for speed
                var needleList = new[]
                {
                    Encoding.ASCII.GetBytes("SYSTEM.CNF;1"),
                    Encoding.ASCII.GetBytes("IP.BIN"),
                    Encoding.ASCII.GetBytes("IP.BIN;1")
                };

                long found = -1;
                byte[]? foundNeedle = null;
                foreach (var n in needleList)
                {
                    found = StreamIndexOf(fs, n, maxScan);
                    if (found >= 0) { foundNeedle = n; break; }
                }

                if (found >= 0 && foundNeedle != null)
                {
                    long lookback = 512;
                    long start = Math.Max(0, found - lookback);
                    int len = (int)Math.Min(4096, Math.Min(fs.Length - start, lookback + foundNeedle.Length + 2048));
                    var buf = new byte[len];
                    fs.Seek(start, SeekOrigin.Begin);
                    fs.ReadExactly(buf);

                    // Clean binary blob to printable ASCII
                    var cleaned = CleanAsciiText(buf, buf.Length);
                    if (cleaned.IndexOf("IP.BIN", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        info.SystemCnf = SelectCnfSegment(cleaned);
                        info.BootFilePath ??= ExtractBootFromSystemCnf(info.SystemCnf);
                        if (string.IsNullOrEmpty(info.GameId))
                            info.GameId = ExtractGameIdFromSystemCnf(info.SystemCnf, section);
                        
                        // Try to extract Dreamcast IP.BIN metadata
                        TryExtractIpBinMetadata(fs, found, info);
                    }
                    else if (cleaned.IndexOf("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             cleaned.IndexOf("BOOT", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        info.SystemCnf = SelectCnfSegment(cleaned);
                        info.BootFilePath ??= ExtractBootFromSystemCnf(info.SystemCnf);
                        if (string.IsNullOrEmpty(info.GameId))
                            info.GameId = ExtractGameIdFromSystemCnf(info.SystemCnf, section);
                    }
                    else
                    {
                        // fallback: small cleaned capture around hit
                        int rel = (int)(found - start);
                        int captureStart = Math.Max(0, rel - 32);
                        int captureLen = Math.Min(1024, buf.Length - captureStart);
                        var smallBuf = new byte[captureLen];
                        Array.Copy(buf, captureStart, smallBuf, 0, captureLen);
                        info.SystemCnf = SelectCnfSegment(CleanAsciiText(smallBuf, smallBuf.Length));
                    }
                }
            }
            
            // Additional attempt to find IP.BIN at standard location (sector 0) for Dreamcast
            if (string.IsNullOrEmpty(info.GameId) && string.IsNullOrEmpty(info.InternalTitle))
            {
                TryExtractIpBinFromSector0(fs, info);
            }

            // If IP.BIN is located elsewhere, try searching the stream to catch alternate placements
            if (string.IsNullOrEmpty(info.GameId) && string.IsNullOrEmpty(info.InternalTitle))
            {
                TryExtractIpBinBySearch(fs, info);
            }

            // Only scan for GameId if not already found (fallback only)
            // This scan should be rare since GameId should be extracted from SYSTEM.CNF
            if (string.IsNullOrEmpty(info.GameId))
            {
                // PSX and PS2 serial prefixes
                var idPatterns = new[] { "SLES_", "SLUS_", "SCUS_", "SLPS_", "SCPS_", "SLPM_", "SCPH", "SCES_", "SCUP_", "SCKA_", "SCED_", "SCEO_", "SCEW_", "SLJM_", "SLKA_" };
                foreach (var pat in idPatterns)
                {
                    var p = Encoding.ASCII.GetBytes(pat);
                    // Limit search to first 2MB for speed (instead of 50MB)
                    long pos = StreamIndexOf(fs, p, Math.Min(fs.Length, 2L * 1024 * 1024));
                    if (pos >= 0)
                    {
                        long start = Math.Max(0, pos - 8);
                        int len = (int)Math.Min(64, fs.Length - start);
                        var buf = new byte[len];
                        fs.Seek(start, SeekOrigin.Begin);
                        fs.ReadExactly(buf, 0, len);
                        var s = Encoding.ASCII.GetString(buf);
                        var idx = s.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            int end = idx;
                            while (end < s.Length && !char.IsControl(s[end]) && s[end] != '"' && s[end] != '\'' && s[end] != ' ')
                                end++;
                            string candidate = s.Substring(idx, end - idx).Trim('\0').Trim();
                            if (!string.IsNullOrEmpty(candidate))
                            {
                                // Normalize the game ID format
                                var normalized = NormalizeGameId(candidate);
                                if (!string.IsNullOrEmpty(normalized))
                                {
                                    info.GameId = normalized;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // OPTIMIZATION: Skip hash computation for disc formats where we only need GameId
            // Hash computation is expensive and unnecessary for PSX/Dreamcast metadata extraction
            // Only compute hashes if: 1) file is small, 2) GameId was found, and 3) hashes not yet computed
            bool isDiscFormat = info.Format == RomFormat.Iso || 
                               !string.IsNullOrEmpty(info.SystemCnf) || 
                               !string.IsNullOrEmpty(info.DiscVolumeLabel);
            
            // Skip hashes for large disc files - they're not needed for game ID identification
            if (!isDiscFormat && fs.Length <= FullHashThreshold && string.IsNullOrEmpty(info.Md5))
            {
                fs.Seek(0, SeekOrigin.Begin);
                using var md5 = MD5.Create();
                using var sha1 = SHA1.Create();
                info.Md5 = BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                fs.Seek(0, SeekOrigin.Begin);
                info.Sha1 = BitConverter.ToString(sha1.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                fs.Seek(0, SeekOrigin.Begin);
                info.Crc32 = $"0x{RomToolHelpers.Crc32.Compute(fs):X8}";
            }

            if (string.IsNullOrEmpty(info.Crc32) && !isDiscFormat)
            {
                info.Crc32 = $"0x{RomToolHelpers.Crc32.Compute(fs):X8}";
            }

            // Fallback: if GameId is still null but DiscVolumeLabel looks like a game ID, use it
            // The volume label often contains the PSX serial ID
            if (string.IsNullOrEmpty(info.GameId) && !string.IsNullOrEmpty(info.DiscVolumeLabel))
            {
                var normalized = NormalizeGameId(info.DiscVolumeLabel);
                if (!string.IsNullOrEmpty(normalized))
                {
                    info.GameId = normalized;
                    System.Diagnostics.Debug.WriteLine($"[RomInspector] Applied DiscVolumeLabel fallback: {info.DiscVolumeLabel} → {normalized}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[RomInspector] DiscVolumeLabel fallback failed to normalize: {info.DiscVolumeLabel}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[RomInspector] Fallback conditions not met - GameId: {info.GameId}, DiscVolumeLabel: {info.DiscVolumeLabel}");
            }

            // OPTIMIZATION: Early return for disc formats once GameId is found
            // This avoids expensive hash computation for PSX/Dreamcast which don't need it
            if (!string.IsNullOrEmpty(info.GameId) && isDiscFormat)
            {
                return info;
            }

            return info;
        }

        /// <summary>
        /// Parse directory buffer (ISO9660) and extract SYSTEM.CNF and game id candidates.
        /// </summary>
        private static void ParseDirectoryBufferForFiles(byte[] dirBuf, RomInfo info, long dirOffsetInStream, Stream fs, int physicalSectorSize, DiscSection section = DiscSection.Auto)
        {
            const int logicalSectorSize = 2048; // Directory records are organized in 2048-byte logical blocks
            int idx = 0;
            while (idx + 34 <= dirBuf.Length)
            {
                int recLen = dirBuf[idx];
                if (recLen == 0)
                {
                    int nextSector = ((idx / logicalSectorSize) + 1) * logicalSectorSize;
                    if (nextSector <= idx) break;
                    idx = nextSector;
                    continue;
                }
                if (idx + recLen > dirBuf.Length) break;

                uint extent = BitConverter.ToUInt32(dirBuf, idx + 2);
                uint dataLen = BitConverter.ToUInt32(dirBuf, idx + 10);
                int fileIdLen = dirBuf[idx + 32];
                int fileIdPos = idx + 33;
                if (fileIdPos + fileIdLen <= dirBuf.Length)
                {
                    string fid = Encoding.ASCII.GetString(dirBuf, fileIdPos, fileIdLen).Trim('\0').Trim();
                    var fidNoVer = fid;
                    int sep = fid.IndexOf(';');
                    if (sep >= 0) fidNoVer = fid.Substring(0, sep);

                    if (string.Equals(fid, "SYSTEM.CNF;1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fidNoVer, "SYSTEM.CNF", StringComparison.OrdinalIgnoreCase))
                    {
                        long fileOffset = (long)extent * physicalSectorSize;
                        if (fileOffset < fs.Length)
                        {
                            long originalPos = fs.Position;
                            try
                            {
                                fs.Seek(fileOffset, SeekOrigin.Begin);
                                int toRead = (int)Math.Min(dataLen, 16 * 1024);
                                var fileBuf = new byte[toRead];
                                int r = fs.Read(fileBuf, 0, toRead);
                                
                                // Verify this looks like actual SYSTEM.CNF content (should be text)
                                bool looksLikeText = true;
                                int nullCount = 0;
                                int printableCount = 0;
                                for (int i = 0; i < Math.Min(r, 256); i++)
                                {
                                    byte b = fileBuf[i];
                                    if (b == 0) nullCount++;
                                    else if ((b >= 32 && b <= 126) || b == 9 || b == 10 || b == 13) printableCount++;
                                }
                                
                                // If too many nulls or non-printable chars, probably reading wrong offset
                                if (nullCount > r / 4 || printableCount < r / 2)
                                {
                                    looksLikeText = false;
                                }
                                
                                if (looksLikeText)
                                {
                                    var cleaned = CleanAsciiText(fileBuf, r);
                                    
                                    // Remove any leading garbage characters (like X%1)
                                    cleaned = Regex.Replace(cleaned, @"^[^\w\s=]+", "");
                                    
                                    // Additional validation: SYSTEM.CNF should contain "BOOT" or "cdrom"
                                    if (!string.IsNullOrWhiteSpace(cleaned) &&
                                        (cleaned.IndexOf("BOOT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         cleaned.IndexOf("cdrom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         cleaned.IndexOf("SLUS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         cleaned.IndexOf("SLES", StringComparison.OrdinalIgnoreCase) >= 0))
                                    {
                                        info.SystemCnf = SelectCnfSegment(cleaned);
                                        if (string.IsNullOrEmpty(info.BootFilePath))
                                            info.BootFilePath = ExtractBootFromSystemCnf(info.SystemCnf);
                                        
                                        // Extract game ID from SYSTEM.CNF - this is faster than searching the entire disc
                                        if (string.IsNullOrEmpty(info.GameId))
                                            info.GameId = ExtractGameIdFromSystemCnf(info.SystemCnf, section);
                                    }
                                }
                            }
                            finally
                            {
                                fs.Seek(originalPos, SeekOrigin.Begin);
                            }
                        }
                    }

                    if (IsLikelyGameIdFilename(fidNoVer) && string.IsNullOrEmpty(info.GameId))
                    {
                        // Normalize the game ID format before setting
                        var normalized = NormalizeGameId(fidNoVer);
                        if (!string.IsNullOrEmpty(normalized))
                            info.GameId = normalized;
                    }
                }

                idx += recLen;
            }
        }

        /// <summary>
        /// Extract and normalize PSX/PS2 game ID from SYSTEM.CNF content.
        /// Uses disc section context to prioritize BOOT2 (PS2) or BOOT (PSX) keys appropriately.
        /// Normalizes format: "SLUS_012.01" -> "SLUS-01201"
        /// </summary>
        private static string? ExtractGameIdFromSystemCnf(string? syscnf, DiscSection section = DiscSection.Auto)
        {
            if (string.IsNullOrEmpty(syscnf)) return null;
            
            var lines = syscnf.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());
            
            // For PS2 specifically, check BOOT2 first
            if (section == DiscSection.PS2)
            {
                foreach (var line in lines)
                {
                    var s = line.Trim();
                    if (s.StartsWith("BOOT2", StringComparison.OrdinalIgnoreCase) && s.Contains("="))
                    {
                        var gameId = ExtractSerialFromBootLine(s);
                        if (!string.IsNullOrEmpty(gameId))
                            return gameId;
                    }
                }
                // If no BOOT2 found in PS2 context, try BOOT as fallback
                foreach (var line in lines)
                {
                    var s = line.Trim();
                    if (s.StartsWith("BOOT", StringComparison.OrdinalIgnoreCase) && 
                        !s.StartsWith("BOOT2", StringComparison.OrdinalIgnoreCase) && 
                        s.Contains("="))
                    {
                        var gameId = ExtractSerialFromBootLine(s);
                        if (!string.IsNullOrEmpty(gameId))
                            return gameId;
                    }
                }
            }
            // For PSX specifically, check BOOT first (without BOOT2)
            else if (section == DiscSection.PSX)
            {
                foreach (var line in lines)
                {
                    var s = line.Trim();
                    if (s.StartsWith("BOOT", StringComparison.OrdinalIgnoreCase) && 
                        !s.StartsWith("BOOT2", StringComparison.OrdinalIgnoreCase) && 
                        s.Contains("="))
                    {
                        var gameId = ExtractSerialFromBootLine(s);
                        if (!string.IsNullOrEmpty(gameId))
                            return gameId;
                    }
                }
            }
            // Auto-detect: try BOOT2 first (more specific for PS2), then BOOT
            else
            {
                foreach (var line in lines)
                {
                    var s = line.Trim();
                    if (s.StartsWith("BOOT2", StringComparison.OrdinalIgnoreCase) && s.Contains("="))
                    {
                        var gameId = ExtractSerialFromBootLine(s);
                        if (!string.IsNullOrEmpty(gameId))
                            return gameId;
                    }
                }
                foreach (var line in lines)
                {
                    var s = line.Trim();
                    if (s.StartsWith("BOOT", StringComparison.OrdinalIgnoreCase) && 
                        !s.StartsWith("BOOT2", StringComparison.OrdinalIgnoreCase) && 
                        s.Contains("="))
                    {
                        var gameId = ExtractSerialFromBootLine(s);
                        if (!string.IsNullOrEmpty(gameId))
                            return gameId;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Extract serial from a single BOOT or BOOT2 line.
        /// Helper method for cleaner extraction logic.
        /// </summary>
        private static string? ExtractSerialFromBootLine(string bootLine)
        {
            var parts = bootLine.Split('=', 2);
            if (parts.Length < 2)
                return null;

            var bootPath = parts[1].Trim().Trim('"', '\'');
            
            // Extract filename from path, handling both backslash and forward slash separators
            // and removing the version marker (e.g., ";1")
            var fileName = bootPath.Replace('/', '\\');
            var lastSlash = fileName.LastIndexOf('\\');
            if (lastSlash >= 0)
                fileName = fileName.Substring(lastSlash + 1);
            
            // Remove version marker (e.g., ";1")
            var semiColon = fileName.IndexOf(';');
            if (semiColon >= 0)
                fileName = fileName.Substring(0, semiColon);
            
            fileName = fileName.Trim();
            
            // Normalize: convert SLUS_012.01 to SLUS-01201
            return NormalizeGameId(fileName);
        }

        /// <summary>
        /// Normalize PSX game ID format.
        /// Converts formats like:
        ///   "SLUS_012.01" -> "SLUS-01201"
        ///   "SLES-12345" -> "SLES-12345"
        ///   "SCUS_123" -> "SCUS-00123"
        /// </summary>
        private static string? NormalizeGameId(string? rawId)
        {
            if (string.IsNullOrEmpty(rawId))
                return null;

            // Remove any extra whitespace and dots
            var cleaned = rawId.Trim().Replace(".", "").Replace(";1", "").Trim();
            
            // PSX prefixes (4 letters)
            var psxPrefixes = new[] { "SLES", "SLUS", "SCUS", "SLPS", "SCPS", "SLPM", "SCPH", "SCES" };
            
            // PS2 prefixes (4 letters)
            var ps2Prefixes = new[] { "SCUS", "SCUP", "SCKA", "SCES", "SCED", "SCEO", "SCEW", "SLUS", "SLES", "SLJM", "SLKA" };
            
            // Combined list of all valid prefixes
            var allPrefixes = psxPrefixes.Union(ps2Prefixes).Distinct().ToArray();
            
            if (!allPrefixes.Any(p => cleaned.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return null;

            // Extract prefix and numeric part
            var upperCleaned = cleaned.ToUpperInvariant();
            string prefix = "";
            string numericPart = "";
            
            foreach (var p in allPrefixes)
            {
                if (upperCleaned.StartsWith(p))
                {
                    prefix = p;
                    numericPart = cleaned.Substring(p.Length);
                    break;
                }
            }

            if (string.IsNullOrEmpty(numericPart))
                return null;

            // Remove separator characters (underscore, hyphen, dot)
            numericPart = Regex.Replace(numericPart, @"[_\-\.]", "");
            
            // Ensure only digits remain
            if (!Regex.IsMatch(numericPart, @"^\d+"))
                return null;
            
            // Must have at least 4 digits
            if (numericPart.Length < 4)
                return null;

            // Take first 5 digits and pad to 5 digits
            numericPart = numericPart.Substring(0, Math.Min(5, numericPart.Length)).PadRight(5, '0');

            return $"{prefix}-{numericPart}";
        }

        /// <summary>
        /// Heuristic to find boot path inside cleaned SYSTEM.CNF text.
        /// </summary>
        private static string? ExtractBootFromSystemCnf(string? syscnf)
        {
            if (string.IsNullOrEmpty(syscnf)) return null;
            var lines = syscnf.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());
            foreach (var l in lines)
            {
                var s = l.Trim();
                if (s.StartsWith("BOOT", StringComparison.OrdinalIgnoreCase) && s.Contains("="))
                {
                    var parts = s.Split('=', 2);
                    if (parts.Length >= 2) return parts[1].Trim().Trim('"', '\'');
                }
                if (s.IndexOf("cdrom0:\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("cdrom0:/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int pos = s.IndexOf("cdrom0:", StringComparison.OrdinalIgnoreCase);
                    if (pos >= 0) return s.Substring(pos).Trim().Trim('"', '\'');
                }
                if (s.IndexOf(".iso", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf(".bin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf(";1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>
        /// Heuristic to decide whether a filename looks like a PSX game id.
        /// </summary>
        private static bool IsLikelyGameIdFilename(string fid)
        {
            if (string.IsNullOrEmpty(fid)) return false;
            fid = fid.ToUpperInvariant();
            
            // PSX prefixes with underscore
            var psxPrefixes = new[] { "SLES_", "SLUS_", "SCUS_", "SLPS_", "SCPS_", "SLPM_", "SCPH", "SCES_" };
            
            // PS2 prefixes with underscore
            var ps2Prefixes = new[] { "SCUS_", "SCUP_", "SCKA_", "SCES_", "SCED_", "SCEO_", "SCEW_", "SLUS_", "SLES_", "SLJM_", "SLKA_" };
            
            var allPrefixes = psxPrefixes.Union(ps2Prefixes).Distinct().Concat(new[] { "SLP" }).ToArray();
            
            foreach (var p in allPrefixes)
                if (fid.StartsWith(p)) return true;

            int digits = fid.Count(ch => ch >= '0' && ch <= '9');
            if (digits >= 3 && fid.Length <= 20) return true;
            return false;
        }

        /// <summary>
        /// Chunked stream search for a byte sequence; restores original position.
        /// </summary>
        private static long StreamIndexOf(Stream fs, byte[] needle, long maxSearch)
        {
            if (needle == null || needle.Length == 0) return -1;
            long original = fs.Position;
            try
            {
                fs.Seek(0, SeekOrigin.Begin);
                var buffer = new byte[64 * 1024];
                int overlap = needle.Length - 1;
                var window = new byte[buffer.Length + overlap];
                long totalRead = 0;
                int windowLen = 0;

                while (totalRead < maxSearch)
                {
                    int toRead = (int)Math.Min(buffer.Length, maxSearch - totalRead);
                    int r = fs.Read(buffer, 0, toRead);
                    if (r <= 0) break;

                    if (windowLen > overlap)
                    {
                        Array.Copy(window, windowLen - overlap, window, 0, overlap);
                        Array.Copy(buffer, 0, window, overlap, r);
                        windowLen = overlap + r;
                    }
                    else
                    {
                        Array.Copy(buffer, 0, window, windowLen, r);
                        windowLen += r;
                    }

                    int pos = IndexOf(window, needle, 0, windowLen);
                    if (pos >= 0)
                        return totalRead - (windowLen - r) + pos;

                    totalRead += r;
                }

                return -1;
            }
            finally
            {
                fs.Seek(original, SeekOrigin.Begin);
            }
        }

        /// <summary>
        /// IndexOf with explicit haystack length.
        /// </summary>
        private static int IndexOf(byte[] haystack, byte[] needle, int start, int haystackLen)
        {
            if (needle.Length == 0) return 0;
            for (int i = start; i <= haystackLen - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        /// <summary>
        /// Compute normalized SHA1 for N64 variants without allocating the full normalized file.
        /// </summary>
        private static string ComputeNormalizedSha1(Stream fs, RomFormat fmt)
        {
            fs.Seek(0, SeekOrigin.Begin);
            using var sha1 = SHA1.Create();
            const int chunk = 64 * 1024;
            var buf = new byte[chunk];
            int read;

            if (fmt == RomFormat.Z64 || fmt == RomFormat.Unknown)
            {
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                    sha1.TransformBlock(buf, 0, read, null, 0);
            }
            else if (fmt == RomFormat.V64)
            {
                // pair-swap streaming: produce [b1,b0] for each pair [b0,b1]
                var outBuf = new byte[chunk];
                int leftover = 0;
                while ((read = fs.Read(buf, leftover, buf.Length - leftover)) > 0)
                {
                    int total = leftover + read;
                    int proc = total & ~1; // full pairs
                    int outIdx = 0;
                    for (int i = 0; i < proc; i += 2)
                    {
                        outBuf[outIdx++] = buf[i + 1];
                        outBuf[outIdx++] = buf[i];
                    }
                    if (outIdx > 0)
                        sha1.TransformBlock(outBuf, 0, outIdx, null, 0);

                    leftover = total - proc;
                    if (leftover > 0)
                        Array.Copy(buf, proc, buf, 0, leftover);
                }
            }
            else // RomFormat.N64
            {
                // reverse each 4-byte word streaming [b0,b1,b2,b3] -> [b3,b2,b1,b0]
                var outBuf = new byte[chunk];
                int leftover = 0;
                while ((read = fs.Read(buf, leftover, buf.Length - leftover)) > 0)
                {
                    int total = leftover + read;
                    int proc = total & ~3; // full words
                    int outIdx = 0;
                    for (int i = 0; i < proc; i += 4)
                    {
                        outBuf[outIdx++] = buf[i + 3];
                        outBuf[outIdx++] = buf[i + 2];
                        outBuf[outIdx++] = buf[i + 1];
                        outBuf[outIdx++] = buf[i + 0];
                    }
                    if (outIdx > 0)
                        sha1.TransformBlock(outBuf, 0, outIdx, null, 0);

                    leftover = total - proc;
                    if (leftover > 0)
                        Array.Copy(buf, proc, buf, 0, leftover);
                }
            }

            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha1.Hash!).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Detect and normalize various N64 endian variants and detect ISO header.
        /// </summary>
        private static byte[] DetectAndNormalizeHeader(byte[] raw, int len, out RomFormat format)
        {
            format = RomFormat.Unknown;
            if (len < 4)
                return raw.Take(len).ToArray();

            bool IsZ64(byte[] b, int l) => l >= 4 && b[0] == 0x80 && b[1] == 0x37 && b[2] == 0x12 && b[3] == 0x40;

            if (IsZ64(raw, len))
            {
                format = RomFormat.Z64;
                return raw.Take(len).ToArray();
            }

            var v = raw.Take(len).ToArray();
            SwapPairs(v, len);
            if (IsZ64(v, len))
            {
                format = RomFormat.V64;
                return v;
            }

            var nA = raw.Take(len).ToArray();
            WordSwapVariantA(nA, len);
            if (IsZ64(nA, len))
            {
                format = RomFormat.N64;
                return nA;
            }

            var nB = raw.Take(len).ToArray();
            ReverseWords(nB, len);
            if (IsZ64(nB, len))
            {
                format = RomFormat.N64;
                return nB;
            }

            // ISO detection: look for "CD001" at 0x8000+1
            if (len >= 0x8000 + 6)
            {
                try
                {
                    if (Encoding.ASCII.GetString(raw, 0x8000 + 1, 5) == "CD001")
                    {
                        format = RomFormat.Iso;
                        return raw.Take(len).ToArray();
                    }
                }
                catch { }
            }

            // NES detection
            if (len >= 4 && raw[0] == 0x4E && raw[1] == 0x45 && raw[2] == 0x53 && raw[3] == 0x1A)
            {
                format = RomFormat.Nes;
                return raw.Take(len).ToArray();
            }

            // GBA detection
            if (len >= 0xC0 && raw[0xA0] == 0x24 && raw[0xA1] == 0xFF && raw[0xA2] == 0xAE && raw[0xA3] == 0x51)
            {
                format = RomFormat.Gba;
                return raw.Take(len).ToArray();
            }

            // Gameboy detection
            if (len >= 0x108 && raw[0x104] == 0xCE && raw[0x105] == 0xED && raw[0x106] == 0x66 && raw[0x107] == 0x66)
            {
                format = RomFormat.Gbx;
                return raw.Take(len).ToArray();
            }

            // Genesis detection
            if (len >= 0x104 && raw[0x100] == 0x53 && raw[0x101] == 0x45 && raw[0x102] == 0x47 && raw[0x103] == 0x41)
            {
                format = RomFormat.Genesis;
                return raw.Take(len).ToArray();
            }

            // SNES detection (Check LoROM and HiROM header locations)
            if (IsSnesHeader(raw, 0x7FB0) || IsSnesHeader(raw, 0x7FB0 + 0x200))
            {
                format = RomFormat.Snes;
                return raw.Take(len).ToArray();
            }
            if (IsSnesHeader(raw, 0xFFB0) || IsSnesHeader(raw, 0xFFB0 + 0x200))
            {
                format = RomFormat.Snes;
                return raw.Take(len).ToArray();
            }

            return raw.Take(len).ToArray();
        }

        private static bool IsSnesHeader(byte[] data, int offset)
        {
            if (offset + 0x30 > data.Length) return false;
            
            // Checksum + Complement = 0xFFFF
            ushort complement = BitConverter.ToUInt16(data, offset + 0x2C);
            ushort checksum = BitConverter.ToUInt16(data, offset + 0x2E);
            return (ushort)(checksum + complement) == 0xFFFF && checksum != 0 && checksum != 0xFFFF;
        }

        private static string? ExtractSnesTitle(Stream fs)
        {
            byte[] buf = new byte[0x10200]; // enough to cover HiROM + copier header
            fs.Seek(0, SeekOrigin.Begin);
            int readSize = fs.Read(buf, 0, buf.Length);
            if (readSize < 0x8000) return null;

            int[] headerOffsets = { 0x7FB0, 0x7FB0 + 0x200, 0xFFB0, 0xFFB0 + 0x200 };
            foreach (var offset in headerOffsets)
            {
                if (IsSnesHeader(buf, offset))
                {
                    // Title is 21 bytes starting 0x10 bytes after the header start
                    int titleOffset = offset + 0x10;
                    if (titleOffset + 21 <= readSize)
                    {
                        var titlePart = Encoding.ASCII.GetString(buf, titleOffset, 21);
                        var cleaned = CleanAsciiText(titlePart).Trim();
                        if (IsValidTitle(cleaned)) return cleaned;
                    }
                }
            }
            
            return null;
        }

        private static string? ExtractGbaTitle(Stream fs)
        {
            fs.Seek(0xA0, SeekOrigin.Begin);
            byte[] title = new byte[12];
            fs.ReadExactly(title, 0, 12);
            return Encoding.ASCII.GetString(title).TrimEnd('\0', ' ');
        }

        private static string? ExtractGbxTitle(Stream fs)
        {
            fs.Seek(0x134, SeekOrigin.Begin);
            byte[] title = new byte[16];
            fs.ReadExactly(title, 0, 16);
            return Encoding.ASCII.GetString(title).TrimEnd('\0', ' ');
        }

        private static string? ExtractGenesisTitle(Stream fs)
        {
            fs.Seek(0x150, SeekOrigin.Begin);
            byte[] title = new byte[48];
            fs.ReadExactly(title, 0, 48);
            return Encoding.ASCII.GetString(title).TrimEnd('\0', ' ');
        }

        private static void SwapPairs(byte[] buf, int len)
        {
            for (int i = 0; i + 1 < len; i += 2)
            {
                byte t = buf[i];
                buf[i] = buf[i + 1];
                buf[i + 1] = t;
            }
        }

        private static void WordSwapVariantA(byte[] buf, int len)
        {
            for (int i = 0; i + 3 < len; i += 4)
            {
                byte b0 = buf[i], b1 = buf[i + 1], b2 = buf[i + 2], b3 = buf[i + 3];
                buf[i] = b2;
                buf[i + 1] = b3;
                buf[i + 2] = b0;
                buf[i + 3] = b1;
            }
        }

        private static void ReverseWords(byte[] buf, int len)
        {
            for (int i = 0; i + 3 < len; i += 4)
            {
                byte b0 = buf[i], b1 = buf[i + 1], b2 = buf[i + 2], b3 = buf[i + 3];
                buf[i] = b3;
                buf[i + 1] = b2;
                buf[i + 2] = b1;
                buf[i + 3] = b0;
            }
        }

        private static uint ReadUInt32BE(byte[] buf, int offset)
        {
            if (offset + 4 > buf.Length) return 0u;
            return (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
        }

        /// <summary>
        /// Faster, robust parser to resolve the first referenced track filename in a .gdi file.
        /// </summary>
        private static string? TryResolveGdiBin(string gdiPath)
        {
            try
            {
                var baseDir = Path.GetDirectoryName(gdiPath) ?? ".";
                var lines = File.ReadAllLines(gdiPath);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith(";") || line.All(char.IsWhiteSpace)) continue;

                    // match typical GDI track lines: "<idx> <filename> <type> <start> <end>"
                    var match = Regex.Match(line, @"^\s*\d+\s+(""(?<f>[^""]+)""|(?<f>\S+))", RegexOptions.Compiled);
                    if (!match.Success) continue;
                    var fname = match.Groups["f"].Value;
                    if (string.IsNullOrEmpty(fname)) continue;

                    var cleaned = fname.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var candidate = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(baseDir, cleaned);

                    if (File.Exists(candidate)) return candidate;

                    var tryExts = new[] { "", ".bin", ".iso", ".img", ".raw" };
                    foreach (var ext in tryExts)
                    {
                        var c = candidate;
                        if (!c.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) c = candidate + ext;
                        if (File.Exists(c)) return c;
                    }
                }
            }
            catch { /* ignore parse errors */ }

            return null;
        }

        /// <summary>
        /// Parse a .cue file and resolve the referenced .bin (or .iso/.img) file.
        /// CUE files are cue sheets that reference binary image files containing the actual disc data.
        /// Example CUE content:
        ///   FILE "image.bin" BINARY
        ///   TRACK 01 MODE1/2352
        ///     INDEX 01 00:00:00
        /// </summary>
        /// <summary>
        /// Parse a .cue file and resolve the referenced .bin (or .iso/.img) file.
        /// Handles long filenames with spaces and case-insensitive matching.
        /// </summary>
        private static string? TryResolveCueBin(string cuePath)
        {
            try
            {
                var baseDir = Path.GetDirectoryName(cuePath) ?? ".";
                if (!Directory.Exists(baseDir)) return null;
                
                var lines = File.ReadAllLines(cuePath);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith(";")) continue;

                    // Match FILE directive: FILE "filename" BINARY
                    var match = Regex.Match(line, @"^FILE\s+(""(?<f>[^""]+)""|(?<f>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    if (!match.Success) continue;
                    
                    var fname = match.Groups["f"].Value;
                    if (string.IsNullOrEmpty(fname)) continue;

                    var cleaned = fname.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var candidate = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(baseDir, cleaned);

                    // Try exact match first
                    if (File.Exists(candidate)) 
                        return candidate;

                    // Try with common extensions
                    var tryExts = new[] { "", ".bin", ".iso", ".img", ".raw" };
                    foreach (var ext in tryExts)
                    {
                        var c = candidate;
                        if (!c.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) 
                            c = candidate + ext;
                        if (File.Exists(c)) 
                            return c;
                    }

                    // Fallback: case-insensitive search in directory
                    var fileName = Path.GetFileName(candidate);
                    var result = TryFindFileIgnoreCase(baseDir, fileName);
                    if (!string.IsNullOrEmpty(result)) 
                        return result;

                    // Last resort: scan directory for any .bin file if CUE references a file without extension
                    if (string.IsNullOrEmpty(Path.GetExtension(candidate)))
                    {
                        var binFiles = Directory.EnumerateFiles(baseDir, "*.bin", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(binFiles))
                            return binFiles;
                    }
                }
            }
            catch { /* ignore parse errors */ }

            return null;
        }

        /// <summary>
        /// Find a file in a directory ignoring case differences.
        /// Handles long filenames with spaces.
        /// </summary>
        private static string? TryFindFileIgnoreCase(string directory, string fileName)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return null;

                // Get all files in directory (limit to avoid scanning too many files)
                var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Take(1000) // Safety limit
                    .ToList();

                // Try exact case match first
                var exact = files.FirstOrDefault(f => 
                    string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal));
                if (exact != null)
                    return exact;

                // Try case-insensitive match
                var caseInsensitive = files.FirstOrDefault(f => 
                    string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
                if (caseInsensitive != null)
                    return caseInsensitive;

                // Try with common image extensions if no exact match
                var baseNameNoExt = Path.GetFileNameWithoutExtension(fileName);
                var imageExts = new[] { ".bin", ".iso", ".img", ".raw", ".cue" };
                foreach (var ext in imageExts)
                {
                    var match = files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileNameWithoutExtension(f), baseNameNoExt, StringComparison.OrdinalIgnoreCase) &&
                        f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        return match;
                }
            }
            catch { /* ignore */ }

            return null;
        }

        /// <summary>
        /// If referenced track cannot be found, gather minimal hints from the .gdi file:
        /// pick first referenced filename and try to extract a likely game id from it.
        /// If the referenced file exists and is inspectable, return the full Inspect result.
        /// </summary>
        private static RomInfo? TryExtractInfoFromGdi(string gdiPath)
        {
            try
            {
                var baseDir = Path.GetDirectoryName(gdiPath) ?? ".";
                var lines = File.ReadAllLines(gdiPath);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith(";") || line.All(char.IsWhiteSpace)) continue;

                    var match = Regex.Match(line, @"^\s*\d+\s+(""(?<f>[^""]+)""|(?<f>\S+))", RegexOptions.Compiled);
                    if (!match.Success) continue;
                    var fname = match.Groups["f"].Value;
                    if (string.IsNullOrEmpty(fname)) continue;

                    var cleaned = fname.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var candidate = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(baseDir, cleaned);

                    // Try existing file
                    if (File.Exists(candidate))
                    {
                        using var fs = File.Open(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return InspectFromStream(fs, $"{gdiPath}::{Path.GetFileName(candidate)}");
                    }

                    // set minimal metadata from filename
                    var ri = new RomInfo { FilePath = gdiPath, Format = RomFormat.Unknown };
                    var nameOnly = Path.GetFileNameWithoutExtension(cleaned);
                    if (IsLikelyGameIdFilename(nameOnly))
                        ri.GameId = nameOnly;
                    else
                    {
                        // attempt to pick up a title-like part from filename
                        ri.InternalTitle = nameOnly;
                    }
                    return ri;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Try to find referenced image filename inside a .cdi by scanning the header for ASCII filenames.
        /// Scans a small head-area only for performance.
        /// </summary>
        private static string? TryResolveCdiBin(string cdiPath)
        {
            try
            {
                var baseDir = Path.GetDirectoryName(cdiPath) ?? ".";
                using var fs = File.OpenRead(cdiPath);
                int scan = (int)Math.Min(128 * 1024, fs.Length); // scan first 128KB
                var buf = new byte[scan];
                fs.Seek(0, SeekOrigin.Begin);
                int r = fs.Read(buf, 0, buf.Length);
                if (r <= 0) return null;

                var asAscii = Encoding.ASCII.GetString(buf, 0, r);
                // find common extensions in text
                var rx = new Regex(@"(?<p>[^\\\/\r\n""]{1,200}\.(bin|iso|img|raw|cue))", RegexOptions.IgnoreCase);
                var m = rx.Match(asAscii);
                while (m.Success)
                {
                    var rel = m.Groups["p"].Value.Trim('\0', ' ', '"', '\'');
                    // normalize path
                    var cleaned = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var candidate = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(baseDir, cleaned);
                    if (File.Exists(candidate)) return candidate;

                    // try with common extensions appended
                    var tryExts = new[] { "", ".bin", ".iso", ".img", ".raw" };
                    foreach (var ext in tryExts)
                    {
                        var c = candidate;
                        if (!c.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) c = candidate + ext;
                        if (File.Exists(c)) return c;
                    }

                    m = m.NextMatch();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Extract minimal metadata from a .cdi file by scanning header for filenames and id-like tokens.
        /// Does not extract large payloads; returns a RomInfo with minimal fields when possible.
        /// </summary>
        private static RomInfo? TryExtractInfoFromCdi(string cdiPath)
        {
            try
            {
                var baseDir = Path.GetDirectoryName(cdiPath) ?? ".";
                using var fs = File.OpenRead(cdiPath);
                int scan = (int)Math.Min(128 * 1024, fs.Length); // small scan
                var buf = new byte[scan];
                fs.Seek(0, SeekOrigin.Begin);
                int r = fs.Read(buf, 0, buf.Length);
                if (r <= 0) return null;

                var asAscii = Encoding.ASCII.GetString(buf, 0, r);

                // try to find first filename
                var rx = new Regex(@"(?<p>[^\\\/\r\n""]{1,200}\.(bin|iso|img|raw|cue))", RegexOptions.IgnoreCase);
                var m = rx.Match(asAscii);
                if (m.Success)
                {
                    var rel = m.Groups["p"].Value.Trim('\0', ' ', '"', '\'');
                    var nameOnly = Path.GetFileNameWithoutExtension(rel);
                    var ri = new RomInfo { FilePath = cdiPath, Format = RomFormat.Unknown };
                    if (IsLikelyGameIdFilename(nameOnly))
                        ri.GameId = nameOnly;
                    else
                        ri.InternalTitle = nameOnly;

                    var cleaned = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var candidate = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(baseDir, cleaned);
                    if (File.Exists(candidate))
                    {
                        using var ifs = File.Open(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return InspectFromStream(ifs, $"{cdiPath}::{Path.GetFileName(candidate)}");
                    }

                    return ri;
                }

                // fallback: try to extract volume label or CD001 present
                var needle = "CD001";
                if (asAscii.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var ri = new RomInfo { FilePath = cdiPath, Format = RomFormat.Iso, DiscVolumeLabel = "" };
                    return ri;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Clean binary blob into printable ASCII text (tabs/newlines preserved), collapse repeated spaces.
        /// Replaces non-printable characters with spaces to avoid gibberish like "X%1".
        /// </summary>
        private static string CleanAsciiText(byte[] buf, int len)
        {
            if (buf == null || len <= 0) return string.Empty;
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                byte b = buf[i];
                if (b == 9 || b == 10 || b == 13) // Tab, LF, CR
                    sb.Append((char)b);
                else if (b >= 32 && b <= 126) // printable ASCII
                    sb.Append((char)b);
                else
                    sb.Append(' ');
            }
            var s = Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ");
            s = s.Replace("\r", "").Trim();
            return s;
        }

        /// <summary>
        /// Clean a string by replacing non-printable characters with spaces, collapse repeats.
        /// </summary>
        private static string CleanAsciiText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c == '\t' || c == '\n' || c == '\r' || (c >= 32 && c <= 126))
                    sb.Append(c);
                else
                    sb.Append(' ');
            }
            var s = Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ");
            s = s.Replace("\r", "").Trim();
            return s;
        }

        /// <summary>
        /// Select the most relevant portion of a cleaned SYSTEM.CNF text (prefer BOOT or cdrom lines).
        /// </summary>
        private static string SelectCnfSegment(string cleaned)
        {
            if (string.IsNullOrEmpty(cleaned)) return string.Empty;
            var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToArray();
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.IndexOf("BOOT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    l.IndexOf("cdrom", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int start = Math.Max(0, i - 1);
                    int end = Math.Min(lines.Length - 1, i + 1);
                    return string.Join("\n", lines.Skip(start).Take(end - start + 1));
                }
            }
            var first = string.Join("\n", lines.Take(8));
            return first.Length > 0 ? first : cleaned;
        }

        /// <summary>
        /// Try to extract Dreamcast IP.BIN metadata (Product Number and Game Title).
        /// This method searches near IP.BIN references but is less reliable than sector 0.
        /// </summary>
        private static void TryExtractIpBinMetadata(Stream fs, long ipBinPosition, RomInfo info)
        {
            // Don't use this unreliable method - sector 0 is much more reliable
            // Just leave this as a stub for now
        }

        /// <summary>
        /// Try to extract IP.BIN from sector 0 (standard Dreamcast location).
        /// IP.BIN has a very specific structure starting with hardware identifier.
        /// </summary>
        private static void TryExtractIpBinFromSector0(Stream fs, RomInfo info)
        {
            try
            {
                // For GDI/CDI files accessed through OffsetStream, the position might already be adjusted
                // Try both from current position and from absolute 0
                
                long originalPos = fs.Position;
                
                // Attempt 1: Read from absolute position 0
                TryExtractIpBinFromPosition(fs, 0, info);
                
                // Attempt 2: If that didn't work and stream supports seeking, try first 1MB
                if (string.IsNullOrEmpty(info.GameId) && string.IsNullOrEmpty(info.InternalTitle))
                {
                    // Search for "SEGA SEGAKATANA" or "SEGA ENTERPRISES" in first 1MB
                    long searchLimit = Math.Min(fs.Length, 1024 * 1024);
                    fs.Seek(0, SeekOrigin.Begin);
                    
                    var searchBuf = new byte[Math.Min(64 * 1024, searchLimit)];
                    long scanned = 0;
                    
                    while (scanned < searchLimit)
                    {
                        int toRead = (int)Math.Min(searchBuf.Length, searchLimit - scanned);
                        int read = fs.Read(searchBuf, 0, toRead);
                        if (read <= 0) break;
                        
                        // Look for SEGA signatures
                        var signatures = new[]
                        {
                            Encoding.ASCII.GetBytes("SEGA SEGAKATANA"),
                            Encoding.ASCII.GetBytes("SEGA ENTERPRISES")
                        };
                        
                        foreach (var sig in signatures)
                        {
                            int idx = IndexOf(searchBuf, sig, 0, read);
                            if (idx >= 0)
                            {
                                // Found it! Try extracting from this position
                                long ipBinStart = scanned + idx;
                                TryExtractIpBinFromPosition(fs, ipBinStart, info);
                                
                                if (!string.IsNullOrEmpty(info.GameId) || !string.IsNullOrEmpty(info.InternalTitle))
                                {
                                    fs.Seek(originalPos, SeekOrigin.Begin);
                                    return; // Success!
                                }
                            }
                        }
                        
                        scanned += read;
                    }
                }
                
                fs.Seek(originalPos, SeekOrigin.Begin);
            }
            catch
            {
                // Silent fail
            }
        }

        private static void TryExtractIpBinBySearch(Stream fs, RomInfo info)
        {
            if (!string.IsNullOrEmpty(info.GameId) || !string.IsNullOrEmpty(info.InternalTitle))
                return;

            var patterns = new[] { "IP.BIN", "IP.BIN;1" };
            long maxSearch = Math.Min(fs.Length, 50L * 1024 * 1024);

            foreach (var pattern in patterns)
            {
                var needle = Encoding.ASCII.GetBytes(pattern);
                long pos = StreamIndexOf(fs, needle, maxSearch);
                if (pos < 0) continue;

                long originalPos = fs.Position;
                TryExtractIpBinFromPosition(fs, pos, info);
                fs.Seek(originalPos, SeekOrigin.Begin);
                if (!string.IsNullOrEmpty(info.GameId) || !string.IsNullOrEmpty(info.InternalTitle))
                    return;
            }
        }
        
        /// <summary>
        /// Try to extract IP.BIN data from a specific position in the stream.
        /// </summary>
        private static void TryExtractIpBinFromPosition(Stream fs, long position, RomInfo info)
        {
            try
            {
                if (fs.Length < position + 0x200) return;
                
                fs.Seek(position, SeekOrigin.Begin);
                var ipBuf = new byte[Math.Min(0x8000, (int)(fs.Length - position))];
                int read = fs.Read(ipBuf, 0, ipBuf.Length);
                
                if (read < 0x100) return;
                
                // Verify hardware ID
                var hwId = Encoding.ASCII.GetString(ipBuf, 0, Math.Min(16, read)).TrimEnd();
                bool isValidIpBin = hwId.Contains("SEGA");
                
                if (!isValidIpBin) return;
                
                // Extract Product Number (Game ID) - 10 bytes at 0x40
                if (string.IsNullOrEmpty(info.GameId) && read >= 0x4A)
                {
                    var productBytes = new byte[10];
                    Array.Copy(ipBuf, 0x40, productBytes, 0, 10);
                    var productNum = CleanDreamcastText(productBytes);
                    
                    if (!string.IsNullOrWhiteSpace(productNum) && productNum.Length >= 3)
                    {
                        int validChars = productNum.Count(c => char.IsLetterOrDigit(c) || c == '-' || c == ' ');
                        if (validChars >= 3)
                        {
                            info.GameId = productNum;
                        }
                    }
                }
                
                // Extract Game Title - 128 bytes at 0x80
                if (string.IsNullOrEmpty(info.InternalTitle) && read >= 0x100)
                {
                    var titleBytes = new byte[128];
                    Array.Copy(ipBuf, 0x80, titleBytes, 0, 128);
                    var gameTitle = CleanDreamcastText(titleBytes);
                    
                    if (!string.IsNullOrWhiteSpace(gameTitle) && gameTitle.Length >= 2)
                    {
                        int alphaNum = gameTitle.Count(c => char.IsLetterOrDigit(c));
                        if (alphaNum >= 2)
                        {
                            info.InternalTitle = gameTitle;
                        }
                    }
                }
                
                // Extract Producer - 16 bytes at 0x70
                if (string.IsNullOrEmpty(info.DiscVolumeLabel) && read >= 0x80)
                {
                    var producerBytes = new byte[16];
                    Array.Copy(ipBuf, 0x70, producerBytes, 0, 16);
                    var producer = CleanDreamcastText(producerBytes);
                    
                    if (!string.IsNullOrWhiteSpace(producer) && producer.Length >= 2)
                    {
                        int alphaNum = producer.Count(c => char.IsLetterOrDigit(c));
                        if (alphaNum >= 2)
                        {
                            info.DiscVolumeLabel = producer;
                        }
                    }
                }
            }
            catch
            {
                // Silent fail
            }
        }
        
        /// <summary>
        /// Validate that a string looks like a legitimate Dreamcast Game ID.
        /// Dreamcast IDs follow patterns like: MK-51052, T-1210N, HDR-0176, 6107110 06, etc.
        /// </summary>
        private static bool IsValidDreamcastGameId(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length < 5) return false;
            
            text = text.Trim();
            
            // Pattern 1: Letter prefix with dash and numbers (most common)
            // Examples: MK-51052, T-1210N, HDR-0176
            if (Regex.IsMatch(text, @"^[A-Z]{1,3}-?[0-9]{4,5}[A-Z]?$", RegexOptions.IgnoreCase))
                return true;
            
            // Pattern 2: Just numbers with optional spaces (some games)
            // Examples: 6107110 06, 610711006
            if (Regex.IsMatch(text, @"^[0-9]{5,}\s?[0-9]{0,2}$"))
                return true;
            
            // Pattern 3: Mixed alphanumeric without special characters
            // Must have at least some numbers
            int digits = text.Count(char.IsDigit);
            int letters = text.Count(char.IsLetter);
            int valid = text.Count(c => char.IsLetterOrDigit(c) || c == '-' || c == ' ');
            
            return digits >= 3 && valid >= text.Length * 0.8;
        }

        /// <summary>
        /// Try to extract Dreamcast cover/icon image from the disc.
        /// Dreamcast stores icon data at offset 0x7000-0x9000 in IP.BIN or in 0GDTEX.PVR file.
        /// Returns raw BGRA pixel data that can be used with any UI framework.
        /// </summary>
        private static void TryExtractDreamcastCover(Stream fs, RomInfo info)
        {
            try
            {
                long originalPos = fs.Position;
                
                // Method 1: Try reading from absolute offset 0x7000
                bool success = TryExtractIconFromPosition(fs, 0x7000, info);
                
                // Method 2: If that failed, search for the icon palette signature
                if (!success)
                {
                    // Icon palettes often have distinct patterns - search for likely palette data
                    // Look in the first 64KB for icon data
                    fs.Seek(0, SeekOrigin.Begin);
                    long searchLimit = Math.Min(fs.Length, 64 * 1024);
                    
                    for (long pos = 0; pos < searchLimit - 0x420; pos += 0x100)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        var testBuf = new byte[32];
                        int read = fs.Read(testBuf, 0, 32);
                        
                        if (read == 32)
                        {
                            // Check if this looks like a valid RGB565 palette
                            // Valid palettes should have some color variation
                            bool looksLikePalette = false;
                            int nonZeroCount = 0;
                            
                            for (int i = 0; i < 16; i++)
                            {
                                ushort rgb565 = BitConverter.ToUInt16(testBuf, i * 2);
                                if (rgb565 != 0 && rgb565 != 0xFFFF)
                                    nonZeroCount++;
                            }
                            
                            // A valid palette should have at least 4 distinct colors
                            if (nonZeroCount >= 4)
                            {
                                looksLikePalette = true;
                            }
                            
                            if (looksLikePalette)
                            {
                                success = TryExtractIconFromPosition(fs, pos, info);
                                if (success) break;
                            }
                        }
                    }
                }
                
                fs.Seek(originalPos, SeekOrigin.Begin);
            }
            catch
            {
                // Silent fail - cover extraction is optional
            }
        }
        
        /// <summary>
        /// Try to extract icon data from a specific position in the stream.
        /// </summary>
        private static bool TryExtractIconFromPosition(Stream fs, long position, RomInfo info)
        {
            try
            {
                if (fs.Length < position + 0x420) return false;
                
                fs.Seek(position, SeekOrigin.Begin);
                
                // Read palette (16 colors in RGB565 format)
                var paletteData = new byte[32];
                int paletteRead = fs.Read(paletteData, 0, 32);
                
                if (paletteRead != 32) return false;
                
                // Convert RGB565 palette to RGBA array
                var palette = new uint[16];
                for (int i = 0; i < 16; i++)
                {
                    ushort rgb565 = BitConverter.ToUInt16(paletteData, i * 2);
                    int r = ((rgb565 >> 11) & 0x1F) << 3;
                    int g = ((rgb565 >> 5) & 0x3F) << 2;
                    int b = (rgb565 & 0x1F) << 3;
                    
                    // Expand 5/6 bit color to 8 bits properly
                    r = r | (r >> 5);
                    g = g | (g >> 6);
                    b = b | (b >> 5);
                    
                    // Store as RGBA
                    palette[i] = (uint)((255 << 24) | (r << 16) | (g << 8) | b);
                }
                
                // Read icon bitmap data (32x32, 4 bits per pixel)
                var bitmapData = new byte[512];
                int bitmapRead = fs.Read(bitmapData, 0, 512);
                
                if (bitmapRead != 512) return false;
                
                // Validate bitmap data - check if it's not all zeros or all 0xFF
                int zeroCount = 0;
                int ffCount = 0;
                for (int i = 0; i < 512; i++)
                {
                    if (bitmapData[i] == 0) zeroCount++;
                    if (bitmapData[i] == 0xFF) ffCount++;
                }
                
                // If too much of the bitmap is zeros or 0xFF, it's probably not valid
                if (zeroCount > 400 || ffCount > 400) return false;
                
                // Create 32x32 BGRA pixel array (4 bytes per pixel)
                var pixels = new byte[32 * 32 * 4];
                
                // Decode 4-bit paletted image
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        int byteIndex = (y * 32 + x) / 2;
                        int pixelIndex = (y * 32 + x) % 2;
                        
                        byte pixelByte = bitmapData[byteIndex];
                        int paletteIndex;
                        
                        // 4 bits per pixel, high nibble first
                        if (pixelIndex == 0)
                            paletteIndex = (pixelByte >> 4) & 0x0F;
                        else
                            paletteIndex = pixelByte & 0x0F;
                        
                        uint color = palette[paletteIndex];
                        
                        // Write BGRA pixel
                        int offset = (y * 32 + x) * 4;
                        pixels[offset + 0] = (byte)(color & 0xFF);         // B
                        pixels[offset + 1] = (byte)((color >> 8) & 0xFF);  // G
                        pixels[offset + 2] = (byte)((color >> 16) & 0xFF); // R
                        pixels[offset + 3] = (byte)((color >> 24) & 0xFF); // A
                    }
                }
                
                // Store as CoverImageData
                info.CoverImageData = pixels;
                info.CoverImageWidth = 32;
                info.CoverImageHeight = 32;
                
                return true;
            }
            catch
            {
                return false;
            }
        }



        /// <summary>
        /// Validate that a title looks legitimate (not garbage).
        /// </summary>
        private static bool IsValidTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length < 3) return false;
            
            // Count actual letters and digits
            int alphaNum = text.Count(char.IsLetterOrDigit);
            
            // Should have at least 2 alpha characters and be at least 30% alpha (excluding spaces)
            // Garbage headers often have lots of symbols or nulls turned to spaces.
            return alphaNum >= 2 && (double)alphaNum / text.Length >= 0.3;
        }

        /// <summary>
        /// Clean Dreamcast text by handling various encodings and removing null/control characters.
        /// Dreamcast IP.BIN can contain Shift-JIS, Windows-1252, or ASCII text.
        /// </summary>
        private static string CleanDreamcastText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            
            var sb = new StringBuilder(bytes.Length);
            
            // Simple ASCII-based extraction with proper null termination
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                
                // Stop at null terminator
                if (b == 0) break;
                
                // Include printable ASCII and common extended characters
                if ((b >= 32 && b <= 126) || // Standard printable ASCII
                    (b >= 160 && b <= 255))  // Extended ASCII/Windows-1252
                {
                    sb.Append((char)b);
                }
                else if (b == 9) // Tab
                {
                    sb.Append(' ');
                }
            }
            
            var result = sb.ToString().Trim();
            
            // Replace multiple spaces with single space
            result = Regex.Replace(result, @"\s+", " ");
            
            return result;
        }

        /// <summary>
        /// Helper stream class that provides an offset view of a base stream.
        /// Used to read disc data that starts at a specific offset within container files (CDI/GDI).
        /// </summary>
        private class OffsetStream : Stream
        {
            private readonly Stream _baseStream;
            private readonly long _offset;
            private long _position;

            public OffsetStream(Stream baseStream, long offset)
            {
                _baseStream = baseStream;
                _offset = offset;
                _position = 0;
            }

            public override bool CanRead => _baseStream.CanRead;
            public override bool CanSeek => _baseStream.CanSeek;
            public override bool CanWrite => false;
            public override long Length => Math.Max(0, _baseStream.Length - _offset);

            public override long Position
            {
                get => _position;
                set
                {
                    _position = value;
                    _baseStream.Position = _offset + value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _baseStream.Position = _offset + _position;
                int read = _baseStream.Read(buffer, offset, count);
                _position += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long newPos = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => Length + offset,
                    _ => throw new ArgumentException("Invalid seek origin")
                };
                Position = newPos;
                return _position;
            }

            public override void Flush() => _baseStream.Flush();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}