using System;
using System.Buffers.Binary;
using Ceras.Format;

namespace Ceras.Benchmarks
{
    /// <summary>
    /// The section offsets of one robust save, read through the library's own header codec.
    /// <para>
    /// Every offset here comes from <see cref="RobustHeader"/> (a friend-assembly internal), and the
    /// checksum is recomputed with the library's own <c>HashHelpers.CalculateCrc32WithZeroedRange</c>.
    /// A benchmark that restated "44 = fingerprint, 52 = checksum, 56 = header size" as literals
    /// would keep producing plausible numbers after a format change while measuring the wrong bytes,
    /// which is exactly the failure this type exists to remove.
    /// </para>
    /// </summary>
    public readonly struct RobustEnvelope
    {
        public long SchemaStringTableOffset { get; }
        public long DataSectionOffset { get; }
        public long StringTableOffset { get; }
        public ulong Fingerprint { get; }
        public StringTableCompression Compression { get; }

        public int HeaderSize => RobustHeader.Size;
        public int FingerprintOffset => RobustHeader.FingerprintOffset;
        public int ChecksumOffset => RobustHeader.ChecksumOffset;
        public int ChecksumSize => RobustHeader.ChecksumSize;

        RobustEnvelope(long schemaStringTableOffset, long dataSectionOffset, long stringTableOffset,
            ulong fingerprint, StringTableCompression compression)
        {
            SchemaStringTableOffset = schemaStringTableOffset;
            DataSectionOffset = dataSectionOffset;
            StringTableOffset = stringTableOffset;
            Fingerprint = fingerprint;
            Compression = compression;
        }

        public static RobustEnvelope Read(byte[] data)
        {
            RobustHeader.Validate(data);
            RobustHeader.Read(data, out _, out byte flags, out long schemaStringTableOffset,
                out long dataSectionOffset, out long stringTableOffset, out ulong fingerprint);

            return new RobustEnvelope(schemaStringTableOffset, dataSectionOffset, stringTableOffset,
                fingerprint, (StringTableCompression)flags);
        }

        public int HeaderLength => (int)SchemaStringTableOffset - HeaderSize;
        public int SchemaStringTableLength => (int)DataSectionOffset - (int)SchemaStringTableOffset;
        public int DataLength => (int)StringTableOffset - (int)DataSectionOffset;
        public int StringTableLength(int totalLength) => totalLength - (int)StringTableOffset;

        /// <summary>
        /// The loader's own integrity check, over the exact region it covers. Returns the stored
        /// checksum next to the recomputed one so a caller can assert they agree.
        /// </summary>
        public (uint Stored, uint Computed) VerifyChecksum(byte[] data)
        {
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ChecksumOffset));
            uint computed = Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(data, ChecksumOffset, ChecksumSize);
            return (stored, computed);
        }

        /// <summary>The stored CRC32 of <paramref name="data"/>, for comparing against <see cref="VerifyChecksum"/>.</summary>
        public static uint Checksum(byte[] data)
            => Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(data, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize);
    }
}
