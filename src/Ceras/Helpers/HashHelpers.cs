using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
    internal static partial class HashHelpers
    {
        internal static readonly int[] SizeOneIntArray = new int[1];

        static readonly uint[] Crc32Table = CreateCrc32Table();

        static uint[] CreateCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                    if ((entry & 1) == 1)
                        entry = (entry >> 1) ^ 0xedb88320u;
                    else
                        entry >>= 1;
                table[i] = entry;
            }
            return table;
        }

        internal static uint CalculateCrc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xffffffffu;
            foreach (byte b in data)
                crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xff];
            return crc ^ 0xffffffffu;
        }

        /// <summary>
        /// CRC32 over <paramref name="data"/> where the <paramref name="zeroLength"/> bytes starting at
        /// <paramref name="zeroStart"/> are fed to the hash as zero. Used by the robust header, whose
        /// checksum is stored in the middle of the buffer and is therefore zero during computation.
        /// </summary>
        internal static uint CalculateCrc32WithZeroedRange(ReadOnlySpan<byte> data, int zeroStart, int zeroLength)
        {
            uint crc = 0xffffffffu;
            for (int i = 0; i < zeroStart; i++)
                crc = (crc >> 8) ^ Crc32Table[(crc ^ data[i]) & 0xff];
            for (int i = 0; i < zeroLength; i++)
                crc = (crc >> 8) ^ Crc32Table[crc & 0xff];
            for (int i = zeroStart + zeroLength; i < data.Length; i++)
                crc = (crc >> 8) ^ Crc32Table[(crc ^ data[i]) & 0xff];
            return crc ^ 0xffffffffu;
        }

        internal static int PowerOf2(int v)
        {
            if ((v & (v - 1)) == 0) return v;
            int i = 2;
            while (i < v) i <<= 1;
            return i;
        }
    }
}
