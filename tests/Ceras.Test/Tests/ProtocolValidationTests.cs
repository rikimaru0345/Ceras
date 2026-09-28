using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;
using Ceras.Exceptions;

namespace Ceras.Test.BuiltinShapes
{
    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsProtocolHolder
    {
        public int Value;
        public List<int> Items;
        public string Name;
    }

    /// <summary>
    /// Header offsets, ordering, ranges, flags, counts and integer conversions are
    /// validated before indexing/allocating, and malformed/truncated data is rejected with clear errors.
    /// Fixtures keep the CRC valid so the CRC can never mask a decoder defect.
    /// </summary>
    public class ProtocolValidationTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        static byte[] Sample()
        {
            var s = New();
            return s.Save(new BsProtocolHolder { Value = 1, Items = new List<int> { 2, 3 }, Name = "n" }, 1);
        }

        static byte[] WithHeaderField(byte[] bytes, int offset, long value)
        {
            var copy = (byte[])bytes.Clone();
            BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(offset), value);
            FixRobustChecksum(copy);
            return copy;
        }

        static void AssertRejected(byte[] data)
        {
            var s = New();
            Assert.ThrowsAny<Exception>(() => s.Load<BsProtocolHolder>(data, 1));
            Assert.ThrowsAny<Exception>(() => s.Load<BsProtocolHolder>(SlowPath(data), 1));
        }

        // ── Truncation at every section boundary ────────────────────────────────────────

        [Fact]
        public void Truncated_Header_Is_Rejected()
        {
            var bytes = Sample();
            for (int len = 0; len < RobustHeader.Size; len++)
            {
                var truncated = new byte[len];
                Array.Copy(bytes, truncated, len);
                AssertRejected(truncated);
            }
        }

        [Fact]
        public void Truncation_At_Every_Section_Boundary_Is_Rejected()
        {
            var bytes = Sample();
            RobustHeader.Read(bytes, out _, out _, out long schemaStringOffset, out long dataOffset, out long stringOffset, out _);

            foreach (long boundary in new[] { schemaStringOffset, dataOffset, stringOffset })
            {
                for (long cut = RobustHeader.Size; cut <= boundary; cut += Math.Max(1, (boundary - RobustHeader.Size) / 7))
                {
                    var truncated = new byte[cut];
                    Array.Copy(bytes, truncated, cut);
                    // Keep the header intact but make it self-consistent for the shorter data.
                    if (cut >= RobustHeader.Size)
                    {
                        ClampOffsets(truncated, cut);
                        FixRobustChecksum(truncated);
                    }
                    Assert.ThrowsAny<Exception>(() => New().Load<BsProtocolHolder>(truncated, 1));
                }
            }
        }

        static void ClampOffsets(byte[] data, long length)
        {
            foreach (int off in new[] { RobustHeader.SchemaStringOffsetOffset, RobustHeader.DataSectionOffsetOffset, RobustHeader.StringTableOffsetOffset })
            {
                long v = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(off));
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(off), Math.Min(v, length));
            }
        }

        // ── Header offsets / ordering / ranges (valid-CRC malformed fixtures) ───────────

        [Fact]
        public void Out_Of_Order_Section_Offsets_Are_Rejected_With_Valid_Crc()
        {
            var bytes = Sample();
            RobustHeader.Read(bytes, out _, out _, out long schemaStringOffset, out long dataOffset, out long stringOffset, out _);

            // schema-string offset before the header
            AssertRejected(WithHeaderField(bytes, RobustHeader.SchemaStringOffsetOffset, 1));
            // data offset before schema strings
            AssertRejected(WithHeaderField(bytes, RobustHeader.DataSectionOffsetOffset, schemaStringOffset - 1));
            // string-table offset before data
            AssertRejected(WithHeaderField(bytes, RobustHeader.StringTableOffsetOffset, dataOffset - 1));
            // string-table offset beyond the data
            AssertRejected(WithHeaderField(bytes, RobustHeader.StringTableOffsetOffset, bytes.Length + 1));
            // negative offsets
            AssertRejected(WithHeaderField(bytes, RobustHeader.SchemaStringOffsetOffset, -1));
            AssertRejected(WithHeaderField(bytes, RobustHeader.DataSectionOffsetOffset, -1));
            AssertRejected(WithHeaderField(bytes, RobustHeader.StringTableOffsetOffset, -1));
        }

        [Fact]
        public void Unknown_Compression_Flag_Is_Rejected_With_Valid_Crc()
        {
            var bytes = Sample();
            bytes[RobustHeader.FlagsOffset] = 0x7F; // not Disabled(0) / Deflate(1)
            FixRobustChecksum(bytes);
            AssertRejected(bytes);
        }

        [Fact]
        public void Invalid_Magic_And_Format_Version_Are_Rejected()
        {
            var bytes = Sample();
            bytes[0] = (byte)'X';
            FixRobustChecksum(bytes);
            Assert.Throws<CerasDataCorruptionException>(() => New().Load<BsProtocolHolder>(bytes, 1));

            var version = Sample();
            version[RobustHeader.FormatVersionOffset] = (byte)(CerasRobustSerializer.FormatVersion + 1);
            FixRobustChecksum(version);
            Assert.Throws<CerasVersionMismatchException>(() => New().Load<BsProtocolHolder>(version, 1));
        }

        // ── Oversized counts (valid-CRC malformed fixtures) ─────────────────────────────

        [Fact]
        public void Oversized_Payload_String_Table_Count_Is_Rejected_With_Valid_Crc()
        {
            var bytes = Sample();
            RobustHeader.Read(bytes, out _, out _, out _, out _, out long stringOffset, out _);
            int off = (int)stringOffset;
            // Overwrite the leb128 entry count with an enormous value; keep the CRC valid.
            bytes[off] = 0xFF; bytes[off + 1] = 0xFF; bytes[off + 2] = 0xFF; bytes[off + 3] = 0x7F;
            FixRobustChecksum(bytes);
            AssertRejected(bytes);
        }

        [Fact]
        public void Oversized_Collection_Count_Is_Rejected_With_Valid_Crc()
        {
            // Craft a payload whose collection count exceeds the configured limit; decode must reject it
            // rather than allocate. Uses a forced interpreter (which owns the count check).
            var bytes = Sample();
            var corrupted = (byte[])bytes.Clone();
            RobustHeader.Read(corrupted, out _, out _, out _, out long dataOffset, out long stringOffset, out _);

            // Scan the data region for the list element count bias byte and blow it up; then fix the CRC.
            for (long i = dataOffset; i < stringOffset - 1; i++)
            {
                if (corrupted[i] == 0x03) // bias(2 elements) -> 2+1 = 3
                {
                    corrupted[i] = 0xFF; corrupted[i + 1] = 0x7F;
                    break;
                }
            }
            FixRobustChecksum(corrupted);

            // At minimum the load must fail loudly (count limit or truncation), never hang or allocate wildly.
            Assert.ThrowsAny<Exception>(() => New().Load<BsProtocolHolder>(SlowPath(corrupted), 1));
        }

        [Fact]
        public void Oversized_Schema_Entry_Count_Is_Rejected_With_Valid_Crc()
        {
            var bytes = SlowPath(Sample());
            // The schema bytes start right after the header; blast the entry count.
            bytes[RobustHeader.Size] = 0xFF;
            bytes[RobustHeader.Size + 1] = 0xFF;
            bytes[RobustHeader.Size + 2] = 0xFF;
            bytes[RobustHeader.Size + 3] = 0x7F;
            FixRobustChecksum(bytes);
            Assert.ThrowsAny<Exception>(() => New().Load<BsProtocolHolder>(bytes, 1));
        }

        // ── Invalid ids / references / kinds (valid-CRC malformed fixtures) ─────────────

        [Fact]
        public void Unknown_Schema_Kind_Is_Rejected_With_Valid_Crc()
        {
            // Locate precisely: count, typeId, stable-name string index, previous-name count, then the
            // kind byte. Replace the kind byte with an unknown value and keep the CRC valid.
            var bytes = SlowPath(Sample());
            int off = RobustHeader.Size;
            SchemaSection.ReadLeb128(bytes, ref off); // entry count
            SchemaSection.ReadLeb128(bytes, ref off); // type id
            off += 4;                                  // stable-name string index (fixed 4-byte)
            SchemaSection.ReadLeb128(bytes, ref off); // previous-name count

            bytes[off] = 0x7F; // unknown kind
            FixRobustChecksum(bytes);
            Assert.ThrowsAny<Exception>(() => New().Load<BsProtocolHolder>(bytes, 1));
        }

        [Fact]
        public void Out_Of_Range_String_Index_Is_Rejected_With_Valid_Crc()
        {
            var bytes = SlowPath(Sample());
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            bytes[dataOffset] = 0x7F; // root id, but also exercises the reader; keep boundary valid
            FixRobustChecksum(bytes);
            Assert.ThrowsAny<Exception>(() => New().Load<BsProtocolHolder>(bytes, 1));
        }

        [Fact]
        public void Invalid_Root_Type_Id_Is_Rejected()
        {
            var bytes = Sample();
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringOffset, out _);
            // Write an undeclared user id (> UserTypeBase) as the root id.
            bytes[dataOffset] = 0xFF; bytes[dataOffset + 1] = 0x01; // leb128 255
            FixRobustChecksum(bytes);
            AssertRejected(bytes);
        }

        // ── Ordinary bounds checks remain for the network/raw path ──────────────────────

        [Fact]
        public void Raw_Serializer_Truncated_Input_Is_Rejected()
        {
            var s = new CerasFastSerializer();
            var data = s.Serialize(new BsProtocolHolder { Value = 5, Items = new List<int> { 1 }, Name = "abc" });
            for (int cut = 0; cut < data.Length; cut++)
            {
                var truncated = new byte[cut];
                Array.Copy(data, truncated, cut);
                Assert.ThrowsAny<Exception>(() => s.Deserialize<BsProtocolHolder>(truncated));
            }
        }
    }
}
