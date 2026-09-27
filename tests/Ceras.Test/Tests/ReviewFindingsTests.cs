// ─────────────────────────────────────────────────────────────────────────────
// REVIEW FINDINGS — these tests document defects found while reviewing the
// P0 0.5.x (built-in shapes) and P0 0.6.7 (producer build timestamp) work.
//
// THEY ARE EXPECTED TO FAIL until the corresponding defect is fixed. Nothing in
// this file is an oversight: each [Fact] states the contract the code is
// supposed to honour, not the behaviour it happens to have today. Run only
// these with:
//
//     dotnet test tests/Ceras.Test -f net10.0 --filter "FullyQualifiedName~ReviewFindings"
//
// The "Regression pins" region at the bottom of the file is the opposite: those
// tests pass today and exist to keep already-agreed contracts from drifting.
//
// Every context is isolated on purpose: a shape that is rejected at bake time
// poisons the whole context it is registered in, so no unsupported shape shares
// a context with a supported one.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.ReviewFindings
{
    // ── Context: shared reference-typed Tuple ───────────────────────────────────────────

    [CerasContext("RfTuple")]
    [CerasSerializable]
    public class RfSharedTupleHolder
    {
        public Tuple<int, string> First;
        public Tuple<int, string> Second;
        public int After;
    }

    [CerasContext("RfTuple")]
    [CerasSerializable]
    public class RfSharedValueTupleHolder
    {
        public ValueTuple<int, string> First;
        public ValueTuple<int, string> Second;
        public int After;
    }

    // ── Context: collections the built-in collection formatter can write but the
    //        migration interpreter cannot construct (no public parameterless ctor) ──────

    [CerasContext("RfNoCtorCollection")]
    [CerasSerializable]
    public class RfReadOnlyCollectionHolder
    {
        public ReadOnlyCollection<int> Items;
        public int After;
    }

    [CerasContext("RfNoCtorCollection")]
    [CerasSerializable]
    public class RfReadOnlyDictionaryHolder
    {
        public ReadOnlyDictionary<string, int> Items;
        public int After;
    }

    // ── Context: the two scalars newly declared as supported fixed-width shapes ────────

    [CerasContext("RfHeader")]
    [CerasSerializable]
    public class RfHeaderHolder
    {
        public int Value;
    }

    // ── Context: strict version of the finite-context rule for closed user generics ────

    [CerasSerializable]
    public class RfPair<K, V>
    {
        public K Key;
        public V Value;
    }

    [CerasContext("RfFiniteContext")]
    [CerasSerializable]
    public class RfPairRoot
    {
        public RfPair<int, string> Declared;
    }

    /// <summary>
    /// TODO review findings. One region per defect; the defect is restated in the test's
    /// doc comment so the failure message does not have to carry it.
    /// </summary>
    public class ReviewFindingsTests : TestBase
    {
        // ── Finding: a shared reference-typed Tuple loses its identity on the migration path ──
        //
        // A class-shaped value written through ReferenceFormatter<Tuple<...>> IS registered in
        // the writer's object cache, so the file refers back to it by index. The interpreter
        // reserves the matching slot (ReadPolymorphicValue) but ReadTuple never fills it, so
        // every back-reference to a tuple decodes as null. ValueTuple<> is a struct and is
        // unaffected — the asymmetry is what makes this a bug rather than a design choice.
        [Fact]
        public void Shared_Tuple_Is_Preserved_On_The_Migration_Path()
        {
            var s = New("RfTuple");
            var shared = new Tuple<int, string>(42, "shared");
            var bytes = s.Save(new RfSharedTupleHolder { First = shared, Second = shared, After = 7 }, 1);

            // Precondition: the matching-layout path already preserves the reference.
            var fast = s.Load<RfSharedTupleHolder>(bytes, 1);
            Assert.Same(fast.First, fast.Second);
            Assert.Equal(7, fast.After);

            // The migration interpreter must preserve it too.
            var slow = s.Load<RfSharedTupleHolder>(MigrationPath(bytes), 1);
            Assert.NotNull(slow.Second);
            Assert.Same(slow.First, slow.Second);
            Assert.Equal(42, slow.Second.Item1);
            Assert.Equal("shared", slow.Second.Item2);
            Assert.Equal(7, slow.After);
        }

        [Fact]
        public void Shared_ValueTuple_Is_Preserved_On_The_Migration_Path()
        {
            // Same shape as above for the struct form, which is written without a reference
            // marker and is not registered by the writer. Pins the ValueTuple side of the
            // contract so a fix for the Tuple case cannot regress it.
            var s = New("RfTuple");
            var shared = new ValueTuple<int, string>(42, "shared");
            var bytes = s.Save(new RfSharedValueTupleHolder { First = shared, Second = shared, After = 7 }, 1);

            Assert.Equal(shared, s.Load<RfSharedValueTupleHolder>(bytes, 1).First);

            var slow = s.Load<RfSharedValueTupleHolder>(MigrationPath(bytes), 1);
            Assert.Equal(shared, slow.First);
            Assert.Equal(shared, slow.Second);
            Assert.Equal(7, slow.After);
        }

        // ── Finding: a collection with no public parameterless constructor is admitted, then
        //        fails on the migration path with a raw reflection exception ──────────────
        //
        // TryResolveCollectionShape describes these as List<>/Dictionary<> payloads (which is
        // accurate) and the matching-layout path reads them fine, so the shape is supported.
        // ReadList/ReadDictionary then call Activator.CreateInstance(targetType) and surface
        // MissingMethodException. Either the interpreter must construct them (for example by
        // building the backing List/Dictionary and wrapping it), or the bake must reject the
        // shape with an actionable Ceras error. Both outcomes satisfy this test; a raw
        // MissingMethodException from deep inside the decoder does not.
        [Fact]
        public void ReadOnlyCollection_Member_Does_Not_Surface_A_Raw_Reflection_Exception_When_Migrating()
        {
            var s = New("RfNoCtorCollection");
            var original = new RfReadOnlyCollectionHolder
            {
                Items = new ReadOnlyCollection<int>(new List<int> { 1, 7 }),
                After = 5,
            };

            // Precondition: the shape is supported on the matching-layout path.
            var bytes = s.Save(original, 1);
            var fast = s.Load<RfReadOnlyCollectionHolder>(bytes, 1);
            Assert.Equal(5, fast.After);
            Assert.Equal(new[] { 1, 7 }, fast.Items);

            var ex = Record.Exception(() => s.Load<RfReadOnlyCollectionHolder>(MigrationPath(bytes), 1));
            if (ex != null)
            {
                Assert.IsAssignableFrom<CerasException>(ex);
            }
            else
            {
                var slow = s.Load<RfReadOnlyCollectionHolder>(MigrationPath(bytes), 1);
                Assert.Equal(5, slow.After);
                Assert.Equal(new[] { 1, 7 }, slow.Items);
            }
        }

        [Fact]
        public void ReadOnlyDictionary_Member_Does_Not_Surface_A_Raw_Reflection_Exception_When_Migrating()
        {
            var s = New("RfNoCtorCollection");
            var original = new RfReadOnlyDictionaryHolder
            {
                Items = new ReadOnlyDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }),
                After = 6,
            };

            var bytes = s.Save(original, 1);
            var fast = s.Load<RfReadOnlyDictionaryHolder>(bytes, 1);
            Assert.Equal(6, fast.After);
            Assert.Equal(2, fast.Items.Count);

            var ex = Record.Exception(() => s.Load<RfReadOnlyDictionaryHolder>(MigrationPath(bytes), 1));
            if (ex != null)
            {
                Assert.IsAssignableFrom<CerasException>(ex);
            }
            else
            {
                var slow = s.Load<RfReadOnlyDictionaryHolder>(MigrationPath(bytes), 1);
                Assert.Equal(6, slow.After);
                Assert.Equal(2, slow.Items.Count);
            }
        }

        // ── Finding: declared fixed-width scalars trust their bytes ─────────────────────
        //
        // decimal (well-known id 13, 16 bytes) and DateTimeOffset (id 42, 10 bytes) are now
        // advertised as fully supported fixed-width scalars, so their payload must be validated
        // before a CLR instance is constructed from it, exactly like every other decoder
        // (TODO 0.6 item 8). Today the raw BCL constructors throw straight out of the decoder:
        // new DateTimeOffset(ticks, offset) rejects an out-of-range offset, and new decimal(bits)
        // rejects an impossible scale/sign word.
        [Fact]
        public void Corrupt_DateTimeOffset_Offset_Is_Reported_As_Data_Corruption()
        {
            var s = New("RfHeader");

            // A valid file whose 2 offset bytes say +32767 minutes.
            var bytes = s.Save(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), 1);
            int start = PayloadStart(bytes);
            Assert.Equal(10, PayloadLength(bytes));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(start + 8), short.MaxValue);
            FixRobustChecksum(bytes); // valid CRC: only the decoder can catch this

            AssertRejectedOnBothPaths(s, bytes, () => s.Load<DateTimeOffset>(bytes, 1));
        }

        [Fact]
        public void Corrupt_DateTimeOffset_Ticks_Are_Reported_As_Data_Corruption()
        {
            var s = New("RfHeader");
            var bytes = s.Save(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), 1);
            int start = PayloadStart(bytes);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(start), long.MaxValue); // past DateTime.MaxValue
            FixRobustChecksum(bytes);

            AssertRejectedOnBothPaths(s, bytes, () => s.Load<DateTimeOffset>(bytes, 1));
        }

        [Fact]
        public void Corrupt_Decimal_Flags_Are_Reported_As_Data_Corruption()
        {
            var s = New("RfHeader");
            var bytes = s.Save(42.5m, 1);
            int start = PayloadStart(bytes);
            Assert.Equal(16, PayloadLength(bytes));

            // The 4th word is decimal.GetBits' flags word (scale in bits 16-23, sign in bit 31).
            // Scale 31 does not exist, so new decimal(bits) rejects it.
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(start + 12), 31 << 16);
            FixRobustChecksum(bytes);

            AssertRejectedOnBothPaths(s, bytes, () => s.Load<decimal>(bytes, 1));
        }

        [Fact]
        public void Corrupt_Decimal_Reserved_Flags_Bits_Are_Reported_As_Data_Corruption()
        {
            var s = New("RfHeader");
            var bytes = s.Save(42.5m, 1);
            int start = PayloadStart(bytes);

            // Bits 6-30 of the flags word are reserved and must be zero.
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(start + 12), 1 << 6);
            FixRobustChecksum(bytes);

            AssertRejectedOnBothPaths(s, bytes, () => s.Load<decimal>(bytes, 1));
        }

        // ── Finding: a serializer built from a config does not learn its owning assembly ──
        //
        // SetOwningAssembly is only called from the Action<CerasRobustConfig> constructor. The
        // internal (CerasRobustConfig) constructor leaves the owning assembly unset, so
        // BuildTimestamp silently degrades to "unknown" and no build-ordering check runs.
        [Fact]
        public void Serializer_Built_From_A_Config_Still_Knows_Its_Build_Timestamp()
        {
            long expected = BuildInfo.GetProducerBuildTimestamp(typeof(ReviewFindingsTests).Assembly);
            Assert.True(expected > 0, "this assembly must declare [assembly: CerasBuildTimestamp] for the test to mean anything");

            var config = new CerasRobustConfig();
            config.UseGeneratedFormatters("RfHeader");
            var s = new CerasRobustSerializer(config);

            Assert.True(s.Config.BuildTimestampKnown, "a config-built serializer silently fell back to the unknown-build mode");
            Assert.Equal(expected, s.Config.BuildTimestamp);

            // And the value is what actually lands in the header.
            var bytes = s.Save(new RfHeaderHolder { Value = 1 }, 1);
            Assert.Equal(expected, BitConverter.ToInt64(bytes, RobustHeader.BuildTimestampOffset));
        }

        // ── Finding: header integrity is checked after the fields it protects ───────────
        //
        // The build-timestamp and compression-flag checks run before the CRC check, so a
        // corrupted timestamp byte is reported as "produced by a newer build" and a corrupted
        // flags byte as "unknown compression flag". Corruption must be reported as corruption.
        // (Grouped separately from the defects above: this is a diagnostics/ordering contract
        // rather than a wrong result — drop it if the current order is considered acceptable.)
        [Fact]
        public void Corrupt_Build_Timestamp_Field_Is_Reported_As_Data_Corruption()
        {
            var s = New("RfHeader");
            var bytes = s.Save(new RfHeaderHolder { Value = 1 }, 1);

            // A single corrupted timestamp byte, CRC deliberately left stale.
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(RobustHeader.BuildTimestampOffset), long.MaxValue);

            var ex = Record.Exception(() => s.Load<RfHeaderHolder>(bytes, 1));
            Assert.NotNull(ex);
            Assert.IsAssignableFrom<CerasDataCorruptionException>(ex);
            Assert.Contains("CRC", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Corrupt_Compression_Flag_Field_Is_Reported_As_Data_Corruption()
        {
            var s = New("RfHeader");
            var bytes = s.Save(new RfHeaderHolder { Value = 1 }, 1);

            // An unknown compression flag, CRC deliberately left stale.
            bytes[RobustHeader.FlagsOffset] = 0x7F;

            var ex = Record.Exception(() => s.Load<RfHeaderHolder>(bytes, 1));
            Assert.NotNull(ex);
            Assert.IsAssignableFrom<CerasDataCorruptionException>(ex);
            Assert.Contains("CRC", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── Finding: an undeclared type is reported as a bare CerasException ─────────────
        //
        // Every other declaration/bake failure in the same code path (SchemaSection.ResolveId,
        // SchemaRegistry, WellKnownTypes) throws CerasSchemaException, so a caller can catch
        // "you forgot to declare a type / the layout is wrong" specifically instead of
        // string-matching a message. UndeclaredType() returns a plain CerasException, so that
        // distinction is not available. (Strict version of the weak ThrowsAny assertion in
        // BuiltinFiniteContextTests; this also pins the message.)
        [Fact]
        public void Undeclared_Closed_User_Generic_Throws_An_Actionable_Schema_Error()
        {
            var s = New("RfFiniteContext");

            // RfPair<int,string> is reachable from the context root, so it is declared.
            var ok = s.Save(new RfPairRoot { Declared = new RfPair<int, string> { Key = 1, Value = "v" } }, 1);
            Assert.Equal("v", s.Load<RfPairRoot>(ok, 1).Declared.Value);

            // RfPair<int,int> is neither reachable nor declared: knowing the open generic must
            // not authorize every closed combination.
            var ex = Assert.Throws<CerasSchemaException>(() => s.Save(new RfPair<int, int> { Key = 1, Value = 2 }, 1));
            Assert.Contains("RfPair", ex.Message, StringComparison.Ordinal);
            Assert.Contains("context", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────

        static CerasRobustSerializer New(string context) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));

        /// <summary>
        /// Forces the migration interpreter (mismatching layout fingerprint) while keeping the
        /// file's CRC valid, so only the interpreter's behaviour is under test.
        /// </summary>
        static byte[] MigrationPath(byte[] bytes)
        {
            var copy = (byte[])bytes.Clone();
            copy[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(copy);
            return copy;
        }

        /// <summary>Byte offset of the root value (just past the leading root type id).</summary>
        static int PayloadStart(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out long dataOffset, out long stringTableOffset, out _);
            int offset = (int)dataOffset;
            SchemaSection.ReadLeb128(bytes, ref offset); // root type id
            return offset;
        }

        static int PayloadLength(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out _, out long stringTableOffset, out _);
            return (int)stringTableOffset - PayloadStart(bytes);
        }

        /// <summary>
        /// Both readers must reject a corrupt scalar payload as data corruption: the
        /// matching-layout formatter and the migration interpreter share the same contract.
        /// </summary>
        static void AssertRejectedOnBothPaths<T>(CerasRobustSerializer s, byte[] corrupt, Func<T> fastLoad)
        {
            var fastEx = Record.Exception(() => fastLoad());
            Assert.IsType<CerasDataCorruptionException>(fastEx);

            var slowBytes = MigrationPath(corrupt);
            var slowEx = Record.Exception(() => s.Load<T>(slowBytes, 1));
            Assert.IsType<CerasDataCorruptionException>(slowEx);
        }
    }

    /// <summary>
    /// Regression pins: contracts that are already implemented and must not drift. These pass
    /// today; they are here because the review found them unpinned.
    /// </summary>
    public class ReviewFindingsPins : TestBase
    {
        // The raw (non-robust) serializer switched decimal from ReinterpretFormatter<decimal>
        // (raw CLR memory: flags word first) to DecimalFormatter (decimal.GetBits order). The
        // new layout is the one Format/LAYOUT.md and the migration interpreter already
        // specified, but nothing pinned the raw path, so the change was silent.
        [Fact]
        public void Raw_Decimal_Uses_The_Documented_GetBits_Layout()
        {
            var raw = new CerasFastSerializer();

            var value = 42.5m;
            var bytes = raw.Serialize(value);

            Assert.Equal(16, bytes.Length);
            Assert.Equal(GetBitsBytes(value), bytes);
            Assert.Equal(value, raw.Deserialize<decimal>(bytes));
        }

        [Fact]
        public void Raw_DateTimeOffset_Uses_Ten_Bytes_Ticks_Then_Offset_Minutes()
        {
            var raw = new CerasFastSerializer();
            var value = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(330));

            var bytes = raw.Serialize(value);
            Assert.Equal(10, bytes.Length);

            var expected = new byte[10];
            BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(0), value.Ticks);
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(8), (short)value.Offset.TotalMinutes);
            Assert.Equal(expected, bytes);
            Assert.Equal(value, raw.Deserialize<DateTimeOffset>(bytes));
        }

        static byte[] GetBitsBytes(decimal value)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            var result = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0), bits[0]);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), bits[1]);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), bits[2]);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), bits[3]);
            return result;
        }
    }
}
