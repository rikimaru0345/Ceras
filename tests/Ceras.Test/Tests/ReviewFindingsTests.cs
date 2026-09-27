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
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    public class RfPre05List : List<int> { }

    [CerasContext("RfHeader")]
    [CerasSerializable]
    public class RfPre05Holder
    {
        public RfPre05List Items;
        public int After;
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

    // ── Context: a retyped scalar field, and the raw-path decimal containers ─────────────

    /// <summary>
    /// The current build declares <see cref="Value"/> as <c>decimal</c> with a field initializer, so a
    /// value that is silently dropped is distinguishable from one that was migrated.
    /// </summary>
    [CerasSerializable]
    public class RfRetypedScalar
    {
        public decimal Value = 7m;
        public int After;
    }

    [CerasSerializable]
    public class RfInt64Model
    {
        public long Value;
        public int After;
    }

    [CerasContext("RfRetype")]
    [CerasSerializable]
    public class RfRetypeRoots
    {
        public RfRetypedScalar Retyped;
        public RfInt64Model Widening;
    }

    public struct RfDecimalBox
    {
        public decimal Amount;
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
            var slow = s.Load<RfSharedTupleHolder>(SlowPath(bytes), 1);
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

            var slow = s.Load<RfSharedValueTupleHolder>(SlowPath(bytes), 1);
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

            var ex = Record.Exception(() => s.Load<RfReadOnlyCollectionHolder>(SlowPath(bytes), 1));
            if (ex != null)
            {
                Assert.IsAssignableFrom<CerasException>(ex);
            }
            else
            {
                var slow = s.Load<RfReadOnlyCollectionHolder>(SlowPath(bytes), 1);
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

            var ex = Record.Exception(() => s.Load<RfReadOnlyDictionaryHolder>(SlowPath(bytes), 1));
            if (ex != null)
            {
                Assert.IsAssignableFrom<CerasException>(ex);
            }
            else
            {
                var slow = s.Load<RfReadOnlyDictionaryHolder>(SlowPath(bytes), 1);
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

        // ── Finding: a retyped scalar field was classified as *removed* and lost its value ──────
        //
        // On the migration path DetectCoercion only knows five widening conversions, so everything else
        // fell through to TypeIdsCompatible, which accepts a matching well-known id (or object) only. A
        // double -> decimal (or long -> DateTimeOffset, int -> short, ...) retyping was therefore
        // classified as a *removed* field: the value was skipped and the new field kept its declared
        // default, observable only through MigrationContext.SkippedFields — silent data loss on a valid
        // save. The load must now refuse the file and name the field and both types.
        [Fact]
        public void Retyped_Scalar_Field_Is_Rejected_Not_Silently_Dropped()
        {
            // A hand-built legacy file: 'Value' was stored as System.Double (42.5), while the current CLR
            // type declares it as a decimal initialized to 7. No historical CLR type is needed.
            var bytes = BuildLegacyFile(
                typeof(RfRetypedScalar),
                "Ceras.Test.ReviewFindings.RfRetypedScalar",
                new[]
                {
                    new SchemaFieldInfo("Value", Array.Empty<string>(), "System.Double", true),
                    new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
                },
                payload =>
                {
                    SerializerBinary.WriteDouble64Fixed(payload, 42.5);
                    SerializerBinary.WriteInt32Fixed(payload, 3);
                });

            var s = New("RfRetype");

            var ex = Assert.Throws<CerasSchemaException>(() => s.Load<RfRetypedScalar>(bytes, 1));
            Assert.Contains("Value", ex.Message, StringComparison.Ordinal);      // the field
            Assert.Contains("System.Double", ex.Message, StringComparison.Ordinal); // what the file stores
            Assert.Contains("System.Decimal", ex.Message, StringComparison.Ordinal); // what the code declares

            // A retype Ceras *can* widen still migrates (and the following field proves alignment).
            var widening = BuildLegacyFile(
                typeof(RfInt64Model),
                "Ceras.Test.ReviewFindings.RfInt64Model",
                new[]
                {
                    new SchemaFieldInfo("Value", Array.Empty<string>(), "System.Int32", true),
                    new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
                },
                payload =>
                {
                    SerializerBinary.WriteInt32Fixed(payload, 42);
                    SerializerBinary.WriteInt32Fixed(payload, 3);
                });

            var migrated = s.Load<RfInt64Model>(widening, 1);
            Assert.Equal(42L, migrated.Value);
            Assert.Equal(3, migrated.After);
        }

        // ── Finding: a pre-0.5 file described a collection member as an empty class schema ─────
        //
        // Before the format learned to describe collection shapes structurally, such a member was
        // recorded as a class schema with no fields while the payload was a count plus elements. Reading
        // it as a field-less class consumes nothing, so the interpreter desynchronized silently: a
        // following `int` member decoded as 1027 instead of 9, with no error at all. Such saves are not
        // migratable, and the interpreter has to say so instead of returning garbage.
        [Fact]
        public void Pre05_Empty_Class_Schema_For_A_Collection_Is_Rejected()
        {
            var bytes = BuildPre05CollectionFile();

            var ex = Assert.Throws<CerasSchemaException>(() => New("RfHeader").Load<RfPre05Holder>(bytes, 1));
            Assert.Contains("RfPre05List", ex.Message, StringComparison.Ordinal);
            Assert.Contains("desynchronize", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // The two shapes whose schema says "a List<>/Dictionary<,> payload" but whose writers did not
        // write one: ReadOnlyCollection<T> was dumped field-by-field through the reflection fallback
        // (its private items/count fields) and ReadOnlyDictionary<K,V> wrote an unbiased count plus whole
        // KeyValuePair values. Both now write the payload their schema describes, which is what makes the
        // migration path able to read them at all; these pins fix those bytes.
        [Fact]
        public void ReadOnlyCollection_Writes_The_List_Payload_Its_Schema_Describes()
        {
            var s = New("RfNoCtorCollection");
            var bytes = s.Save(new RfReadOnlyCollectionHolder
            {
                Items = new ReadOnlyCollection<int>(new List<int> { 1, 7 }),
                After = 5,
            }, 1);

            var payload = PayloadOf(bytes);
            int offset = SkipRootIdAndMarker(payload);

            // reference marker (bias 5 + RefNewValueSameType) then the element count with bias 1
            Assert.Equal(5 + Protocol.RefNewValueSameType, payload[offset++]);
            Assert.Equal(3, payload[offset++]);                 // varint(count + bias) for 2 elements
            Assert.Equal(1, BitConverter.ToInt32(payload, offset));
            Assert.Equal(7, BitConverter.ToInt32(payload, offset + 4));
            Assert.Equal(5, BitConverter.ToInt32(payload, offset + 8)); // the following member
            Assert.Equal(17, payload.Length);

            // The instance itself is still rebuilt as a real ReadOnlyCollection wrapping a List<int>.
            var loaded = s.Load<RfReadOnlyCollectionHolder>(SlowPath(bytes), 1);
            Assert.IsType<ReadOnlyCollection<int>>(loaded.Items);
            Assert.Equal(new[] { 1, 7 }, loaded.Items);
            Assert.Equal(5, loaded.After);
        }

        [Fact]
        public void ReadOnlyDictionary_Writes_The_Dictionary_Payload_Its_Schema_Describes()
        {
            var s = New("RfNoCtorCollection");
            var bytes = s.Save(new RfReadOnlyDictionaryHolder
            {
                Items = new ReadOnlyDictionary<string, int>(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }),
                After = 6,
            }, 1);

            var payload = PayloadOf(bytes);
            int offset = SkipRootIdAndMarker(payload);
            Assert.Equal(5 + Protocol.RefNewValueSameType, payload[offset++]);
            Assert.Equal(3, payload[offset++]); // varint(count + bias) for 2 entries

            // Two entries, each a 4-byte string-table index then the int value, then the following member.
            Assert.Equal(1, BitConverter.ToInt32(payload, offset + 4));
            Assert.Equal(2, BitConverter.ToInt32(payload, offset + 12));
            Assert.Equal(6, BitConverter.ToInt32(payload, offset + 16));

            var loaded = s.Load<RfReadOnlyDictionaryHolder>(SlowPath(bytes), 1);
            Assert.IsType<ReadOnlyDictionary<string, int>>(loaded.Items);
            Assert.Equal(2, loaded.Items.Count);
            Assert.Equal(1, loaded.Items["a"]);
            Assert.Equal(2, loaded.Items["b"]);
            Assert.Equal(6, loaded.After);
        }

        static byte[] PayloadOf(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out long dataSectionOffset,
                out long stringTableOffset, out _);
            return new ReadOnlySpan<byte>(bytes, (int)dataSectionOffset, (int)(stringTableOffset - dataSectionOffset)).ToArray();
        }

        /// <summary>Skips the root type id and the root's reference marker, returning the offset after them.</summary>
        static int SkipRootIdAndMarker(byte[] payload)
        {
            int offset = 0;
            SchemaSection.ReadLeb128(payload, ref offset);
            return offset + 1;
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

            var slowBytes = SlowPath(corrupt);
            var slowEx = Record.Exception(() => s.Load<T>(slowBytes, 1));
            Assert.IsType<CerasDataCorruptionException>(slowEx);
        }

        /// <summary>
        /// A file shaped like a pre-0.5 save: the collection member is described by an empty class schema
        /// while its payload holds what the built-in collection formatter writes.
        /// </summary>
        static byte[] BuildPre05CollectionFile()
        {
            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(typeof(RfPre05Holder), new SchemaTypeInfo(
                "Ceras.Test.ReviewFindings.RfPre05Holder", Array.Empty<string>(), SchemaTypeKind.Class,
                new[]
                {
                    new SchemaFieldInfo("Items", Array.Empty<string>(), "Ceras.Test.ReviewFindings.RfPre05List", false),
                    new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
                }, 0));
            oldRegistry.Register(typeof(RfPre05List), new SchemaTypeInfo(
                "Ceras.Test.ReviewFindings.RfPre05List", Array.Empty<string>(),
                SchemaTypeKind.Class, Array.Empty<SchemaFieldInfo>(), 0));
            oldRegistry.TryGetId(typeof(RfPre05Holder), out int holderId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);
            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)holderId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteUInt32Bias(payload, 2, 1);      // the collection payload the old file holds
            SerializerBinary.WriteInt32Fixed(payload, 4);
            SerializerBinary.WriteInt32Fixed(payload, 5);
            SerializerBinary.WriteInt32Fixed(payload, 9);         // After

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;
            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }

        /// <summary>
        /// Encodes a complete robust file (header + schema + one class payload) for a schema that
        /// differs from the current generated manifest: the deliberately wrong fingerprint forces the
        /// migration interpreter, and the stable name resolves the old schema to the current CLR type.
        /// </summary>
        static byte[] BuildLegacyFile(Type clrType, string stableName, SchemaFieldInfo[] oldFields,
            Action<ArrayBufferWriter<byte>> writePayload)
        {
            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(clrType, new SchemaTypeInfo(stableName, Array.Empty<string>(),
                SchemaTypeKind.Class, oldFields, 0));
            oldRegistry.TryGetId(clrType, out int oldTypeId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)oldTypeId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            writePayload(payload);

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;

            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }
    }

    /// <summary>
    /// Regression pins: contracts that are already implemented and must not drift. These pass
    /// today; they are here because the review found them unpinned.
    /// </summary>
    public class ReviewFindingsPins : TestBase
    {
        // The raw (non-robust) serializer switched decimal from ReinterpretFormatter<decimal>
        // (raw CLR memory: _flags, _hi, _lo, _mid) to DecimalFormatter (decimal.GetBits order).
        // The new layout is the one Format/LAYOUT.md and the migration interpreter already
        // specified, but nothing pinned the raw path, so the change was silent. Worse, the reinterpret
        // fast path stayed enabled for decimal itself (decimal.IsPrimitive is true), so a decimal[]
        // element, a KeyValuePair<decimal,_> and any struct containing a decimal were still blitted
        // from CLR memory — the same value written two different ways depending on whether the user
        // wrote [] or List<>, and endian-dependent besides. decimal is now excluded from
        // IsBlittableType, so every shape below writes the documented GetBits layout.
        [Fact]
        public void Raw_Decimal_Uses_The_Documented_GetBits_Layout()
        {
            var raw = new CerasFastSerializer();
            var expected = GetBitsBytes(42.5m);

            // The scalar, and the array that used to be blitted element-wise.
            Assert.Equal(expected, raw.Serialize(42.5m));
            Assert.Equal(42.5m, raw.Deserialize<decimal>(expected));

            var array = raw.Serialize(new[] { 42.5m });
            Assert.Equal(16 + 2, array.Length);                // root reference marker + count (varint) + 16 payload bytes
            Assert.Equal(expected, array.Skip(2).ToArray()); // the element itself
            Assert.Equal(42.5m, raw.Deserialize<decimal[]>(array)[0]);

            // A struct containing a decimal, and a KeyValuePair<decimal,_> (a dictionary key used to be
            // blitted together with 4 bytes of trailing padding), must not be reinterpreted either.
            var rawWithFallback = new CerasFastSerializer(c => c.AllowReflectionFallback = true);

            var box = rawWithFallback.Serialize(new RfDecimalBox { Amount = 42.5m });
            Assert.Equal(expected, box);
            Assert.Equal(new RfDecimalBox { Amount = 42.5m }, rawWithFallback.Deserialize<RfDecimalBox>(box));

            var pair = rawWithFallback.Serialize(new KeyValuePair<decimal, int>(42.5m, 3));
            Assert.Equal(20, pair.Length);
            Assert.Equal(expected, pair.Take(16).ToArray());  // the key
            Assert.Equal(new KeyValuePair<decimal, int>(42.5m, 3), rawWithFallback.Deserialize<KeyValuePair<decimal, int>>(pair));

            // The shapes that already agreed must keep agreeing.
            Assert.Equal(expected, raw.Serialize(new List<decimal> { 42.5m }).Skip(2).ToArray());
            Assert.Equal(expected, raw.Serialize(new Dictionary<decimal, int> { { 42.5m, 3 } }).Skip(2).Take(16).ToArray());
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
