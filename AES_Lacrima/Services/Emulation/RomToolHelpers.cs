using System.IO;

namespace AES_Lacrima.Services.Emulation;

public static class RomToolHelpers
{
    public static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            uint[] table = new uint[256];
            const uint poly = 0xEDB88320u;
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        // Compute CRC32 over a stream and restore original position
        public static uint Compute(Stream s)
        {
            const int bufSize = 64 * 1024;
            var buf = new byte[bufSize];
            uint crc = 0xFFFFFFFFu;
            long original = s.Position;
            s.Seek(0, SeekOrigin.Begin);
            int read;
            while ((read = s.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    crc = (crc >> 8) ^ Table[(crc ^ buf[i]) & 0xFF];
            }
            crc ^= 0xFFFFFFFFu;
            s.Seek(original, SeekOrigin.Begin);
            return crc;
        }
    }
}