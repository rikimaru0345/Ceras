using System;
using System.Buffers;
using Ceras.Format;
using Xunit;

namespace Ceras.Test
{
    public class StringTableTests
    {
        // ── StringInternTable — intern behaviour ──────────────────────────────

        [Fact]
        public void Intern_Null_ReturnsZero()
        {
            var table = new StringInternTable();
            Assert.Equal(0, table.Intern(null));
        }

        [Fact]
        public void Intern_FirstString_ReturnsOne()
        {
            var table = new StringInternTable();
            Assert.Equal(1, table.Intern("hello"));
        }

        [Fact]
        public void Intern_SameString_ReturnsSameIndex()
        {
            var table = new StringInternTable();
            int a = table.Intern("same");
            int b = table.Intern("same");
            Assert.Equal(a, b);
        }

        [Fact]
        public void Intern_DifferentStrings_ReturnDifferentIndices()
        {
            var table = new StringInternTable();
            int a = table.Intern("alpha");
            int b = table.Intern("beta");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Intern_IndicesAreConsecutive()
        {
            var table = new StringInternTable();
            int a = table.Intern("one");
            int b = table.Intern("two");
            int c = table.Intern("three");
            Assert.Equal(1, a);
            Assert.Equal(2, b);
            Assert.Equal(3, c);
        }

        [Fact]
        public void Count_ReflectsInterned_NonNull_Strings()
        {
            var table = new StringInternTable();
            table.Intern(null);
            table.Intern("a");
            table.Intern("b");
            table.Intern("a"); // duplicate
            Assert.Equal(2, table.Count);
        }

        [Fact]
        public void Intern_EmptyString_IsDistinctFromNull()
        {
            var table = new StringInternTable();
            int nullIdx  = table.Intern(null);
            int emptyIdx = table.Intern("");
            Assert.NotEqual(nullIdx, emptyIdx);
            Assert.Equal(0, nullIdx);
            Assert.Equal(1, emptyIdx);
        }

        // ── WriteIndex (Manual VarInt Index) ─────────────────────────────────

        static void WriteVarIntIndex(IBufferWriter<byte> writer, StringInternTable table, string s)
        {
            int index = table.GetIndex(s);
            SerializerBinary.WriteUInt32(writer, (uint)index);
        }

        [Fact]
        public void WriteIndex_Null_WritesOneZeroByte()
        {
            var table  = new StringInternTable();
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
            WriteVarIntIndex(writer, table, null);
            var buf = writer.WrittenSpan;
            Assert.Equal(1, buf.Length);
            Assert.Equal(0, buf[0]);
        }

        [Fact]
        public void WriteIndex_AdvancesOffsetByVarIntSize()
        {
            var table  = new StringInternTable();
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
            WriteVarIntIndex(writer, table, "test");
            Assert.Equal(1, writer.WrittenCount); // Index 1 is 1 byte
        }

        // ── Flush / Load round-trip ───────────────────────────────────────────

        static (string[] loaded, int tableStart) FlushAndLoad(StringInternTable intern, StringTableCompression compression = StringTableCompression.Disabled)
        {
            var buf    = new byte[1024];
            int offset = 0;
            int start  = intern.Flush(ref buf, ref offset, compression);
            var loaded = StringTable.Load(buf, start, offset - start, compression);
            return (loaded, start);
        }

        [Fact]
        public void FlushLoad_EmptyTable_ReturnsArrayOfLengthOne()
        {
            var table = new StringInternTable();
            var (loaded, _) = FlushAndLoad(table);
            // Only element is table[0] = null
            Assert.Single(loaded);
            Assert.Null(loaded[0]);
        }

        [Fact]
        public void FlushLoad_Deflate_RoundTrip()
        {
            var intern = new StringInternTable();
            intern.Intern("deflate_me");
            intern.Intern("and_this_too");

            var (loaded, _) = FlushAndLoad(intern, StringTableCompression.Deflate);

            Assert.Equal(3, loaded.Length);
            Assert.Null(loaded[0]);
            Assert.Equal("deflate_me", loaded[1]);
            Assert.Equal("and_this_too", loaded[2]);
        }

        [Fact]
        public void FlushLoad_NullIndex_IsNull()
        {
            var intern = new StringInternTable();
            intern.Intern("irrelevant");
            var (table, _) = FlushAndLoad(intern);
            Assert.Null(table[0]);
        }

        [Fact]
        public void FlushLoad_StringsRoundTrip()
        {
            var intern = new StringInternTable();
            int idx1 = intern.Intern("hello");
            int idx2 = intern.Intern("world");

            var (table, _) = FlushAndLoad(intern);

            Assert.Equal("hello", table[idx1]);
            Assert.Equal("world", table[idx2]);
        }

        [Fact]
        public void FlushLoad_UnicodeSurrogates_RoundTrip()
        {
            var intern = new StringInternTable();
            string emoji = "🎮🗡️";
            int idx = intern.Intern(emoji);

            var (table, _) = FlushAndLoad(intern);

            Assert.Equal(emoji, table[idx]);
        }

        [Fact]
        public void FlushLoad_EmptyString_RoundTrips()
        {
            var intern = new StringInternTable();
            int idx = intern.Intern("");

            var (table, _) = FlushAndLoad(intern);

            Assert.Equal("", table[idx]);
        }

        [Fact]
        public void FlushLoad_ManyStrings_AllRoundTrip()
        {
            var intern  = new StringInternTable();
            var indices = new int[100];
            for (int i = 0; i < 100; i++)
                indices[i] = intern.Intern($"string_{i}");

            var (table, _) = FlushAndLoad(intern);

            for (int i = 0; i < 100; i++)
                Assert.Equal($"string_{i}", table[indices[i]]);
        }

        [Fact]
        public void FlushLoad_TableOffset_IsHonoured()
        {
            // Write some garbage before the table to ensure the offset is used correctly
            var intern = new StringInternTable();
            intern.Intern("positioned");

            var buf    = new byte[512];
            int offset = 42; // non-zero start
            int start  = intern.Flush(ref buf, ref offset);

            Assert.Equal(42, start);

            var table = StringTable.Load(buf, start, offset - start);
            Assert.Equal("positioned", table[1]);
        }

        // ── Full end-to-end: WriteIndex → Flush → Load → ReadString ───────────

        [Fact]
        public void WriteIndex_Then_ReadString_RoundTrips()
        {
            var intern = new StringInternTable();

            // Simulate writing some string values inline
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
            WriteVarIntIndex(writer, intern, null);
            WriteVarIntIndex(writer, intern, "Excalibur");
            WriteVarIntIndex(writer, intern, "Mjolnir");
            WriteVarIntIndex(writer, intern, "Excalibur"); // duplicate

            // Flush string table into a separate buffer
            var tableBuf    = new byte[256];
            int tableOffset = 0;
            int tableStart  = intern.Flush(ref tableBuf, ref tableOffset);
            var table       = StringTable.Load(tableBuf, tableStart, tableOffset - tableStart);

            // Now read back all the inline indices
            var reader = new System.Buffers.SequenceReader<byte>(new System.Buffers.ReadOnlySequence<byte>(writer.WrittenMemory));
            Assert.Null(   StringTable.ReadString(ref reader, table));
            Assert.Equal("Excalibur", StringTable.ReadString(ref reader, table));
            Assert.Equal("Mjolnir",   StringTable.ReadString(ref reader, table));
            Assert.Equal("Excalibur", StringTable.ReadString(ref reader, table));
            Assert.Equal(4, reader.Consumed); // 4 indices × 1 byte each (indices 0, 1, 2, 1)
        }

        // ── ReadString — bounds checking ──────────────────────────────────────

        [Fact]
        public void ReadString_OutOfRange_Throws()
        {
            var table = new string[] { null, "only" }; // index 0=null, 1="only"
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32(writer, 99); // out of range
            var reader = new System.Buffers.SequenceReader<byte>(new System.Buffers.ReadOnlySequence<byte>(writer.WrittenMemory));
            
            try
            {
                StringTable.ReadString(ref reader, table);
                Assert.Fail("Should have thrown");
            }
            catch (InvalidOperationException ex)
            {
                Assert.Equal("String index out of range", ex.Message);
            }
        }

        // ── Buffer growth ─────────────────────────────────────────────────────

        [Fact]
        public void WriteIndex_WithTinyBuffer_Succeeds()
        {
            var intern = new StringInternTable();
            var writer = new System.Buffers.ArrayBufferWriter<byte>(1); // tiny
            WriteVarIntIndex(writer, intern, "grow me");
            Assert.Equal(1, writer.WrittenCount);
        }

        [Fact]
        public void Flush_WithTinyBuffer_GrowsAndSucceeds()
        {
            var intern = new StringInternTable();
            intern.Intern("hello");
            intern.Intern("world");
            var buf    = new byte[1];
            int offset = 0;
            intern.Flush(ref buf, ref offset);
            Assert.True(offset > 0);
        }
    }
}
