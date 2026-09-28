// A rank > 1 array in a *polymorphic* slot must be refused by a robust Save.
//
// The robust format describes only rank-1 arrays, and everywhere that is enforced it is enforced where the
// array's own shape is visible: the structural schema (`SchemaRegistry.TryBuildStructuralSchema`), the
// generator for a declared member (`CERAS027`), the interpreter on read, and `DeclareRoot`. A polymorphic
// slot has no such place. The member declares only a base type, so the only record of the concrete type is
// the `Type` value behind its reference marker — whose array form is (Array marker, element type): no rank,
// no dimensions, the reader rebuilding it through `MakeArrayType()`, unconditionally rank 1.
//
// Before this, `Save` accepted the value and the array came back as a one-dimensional array over the same
// flattened elements: a wrong shape, with nothing in the save or the load saying so. The exact-consumption
// check then usually noticed the length mismatch and reported the *save* as corrupt, a wrong diagnosis of a
// save that was itself wrong. So the write is refused, before any byte of the value, with the same typed
// and actionable error the other sites give; recording the rank in the type value is Milestone 1 work.
//
// Pinned here: every position a rank > 1 array can reach is refused (an `object` member, a `System.Array`
// base-class member, an `object` element of a declared collection, an `object` behind another `object`, and
// a `Type` value); the refusal is a `CerasSchemaException` naming the runtime type, its rank, the slot, why
// that slot cannot carry it and what to use instead; nothing is committed, so the next save is still correct;
// rank 1 and jagged arrays through those slots still round-trip on *both* readers (a `System.Array`-declared
// member was the one slot the interpreter could not follow — see `SystemArraySlotMigrationTests`); the raw
// serializer still round-trips multidimensional arrays; the declared-shape refusals are unchanged; and a
// hand-written same-version file that already carries one does not load quietly on either reader.
//
// Not pinned here: no framework *interface* can be the slot at all. A member declared as `IList`,
// `IEnumerable` or any other type the format cannot describe is a compile-time `CERAS029` (pinned in
// `GeneratorDiagnosticsTests`), so the rows above are complete by construction. `System.Array` is the one
// non-`object` reference type a Ceras manifest admits that an array is assignable to.
using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.PolymorphicArrays
{
    // ── Slots a rank > 1 array can actually be written through ─────────────────────────────────

    /// <summary>An <c>object</c> member: the ordinary polymorphic slot.</summary>
    [CerasContext("PolyArrayGuard")]
    [CerasSerializable]
    public class PmagObjectSlot
    {
        public object Value;
        public int After;
    }

    /// <summary>A <see cref="Array"/>-declared member: the base-class form of the same thing.</summary>
    [CerasContext("PolyArrayGuard")]
    [CerasSerializable]
    public class PmagArrayBaseSlot
    {
        public Array Value;
        public int After;
    }

    /// <summary>
    /// An <c>object</c> *element* of a declared collection: a different writer path from a member, because
    /// the element goes through <c>CollectionFormatter&lt;List&lt;object&gt;, object&gt;</c>, which resolves
    /// the reference wrapper itself — a guard placed only on the member path would miss it.
    /// </summary>
    [CerasContext("PolyArrayGuard")]
    [CerasSerializable]
    public class PmagObjectList
    {
        public List<object> Items;
        public int After;
    }

    [CerasContext("PolyArrayGuard")]
    [CerasSerializable]
    public class PmagInner
    {
        public object Nested;
        public int InnerAfter;
    }

    /// <summary>A second <c>object</c> hop: the value sits behind another polymorphic value.</summary>
    [CerasContext("PolyArrayGuard")]
    [CerasSerializable]
    public class PmagNestedObjectSlot
    {
        public object Inner;
        public int After;
    }

    /// <summary>
    /// Two polymorphic members, so a save can register the object held by the first and then be refused by
    /// the second: the shape in which a half-finished save used to leave the object cache holding an object
    /// the next save could reference but the next reader would never be given.
    /// </summary>
    [CerasContext("PolyArrayGuard")]
    [CerasSerializable]
    public class PmagTwoSlotHolder
    {
        public PmagInner First;
        public object Second;
        public int After;
    }

    /// <summary>A <see cref="Type"/> member: written by the same encoder, and ambiguous the same way.</summary>
    [CerasContext("PolyArrayGuard")]
    [CerasSerializable]
    public class PmagTypeSlot
    {
        public Type Value;
        public int After;
    }

    public class PolymorphicMultidimensionalArrayTests : TestBase
    {
        const string Ctx = "PolyArrayGuard";

        static CerasRobustSerializer New(string context = Ctx) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));

        static string Name(Type type) => TypeIdentity.GetCanonicalName(type);

        /// <summary>The actionable half of the message, read from the guard rather than repeated here, so a
        /// reworded advice cannot leave a passing test behind.</summary>
        static string Advice => MultidimensionalArrayGuard.Advice;

        /// <summary>
        /// The innermost exception: a refusal raised while writing a nested value is wrapped by the
        /// enclosing object's error context (<c>ReferenceFormatter</c> adds the declaring type), so the type
        /// a caller needs to catch is the one at the bottom of the chain.
        /// </summary>
        static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }

        /// <summary>
        /// Asserts a same-version <c>Save</c> was refused, by this one refusal and with what a caller
        /// needs: which runtime value, which slot, and what to do instead.
        /// </summary>
        static CerasSchemaException AssertRefused(Exception thrown, Type value, int rank, string slot)
        {
            Assert.NotNull(thrown);
            var schema = Assert.IsType<CerasSchemaException>(Innermost(thrown!));
            Assert.Contains(Name(value), schema.Message);              // the runtime type
            Assert.Contains($"rank {rank}", schema.Message);            // and the rank that cannot be recorded
            Assert.Contains(slot, schema.Message);                      // and the slot it came through
            Assert.Contains("only the element type", schema.Message);   // and why that slot cannot carry it
            Assert.Contains(Advice, schema.Message);                    // and what to do instead
            return schema;
        }

        static CerasSchemaException AssertRefused(Exception thrown, Array value, string slot)
            => AssertRefused(thrown, value.GetType(), value.Rank, slot);

        /// <summary>
        /// Saves once, then reads the same bytes on <b>both</b> readers — the matching-layout one and the
        /// migration interpreter — and hands each clone to <paramref name="check"/>. Going through one
        /// helper is what keeps "both readers" from quietly becoming "the reader a same-version save uses".
        /// </summary>
        static void BothReaders<T>(CerasRobustSerializer s, T value, Action<T> check)
        {
            var bytes = s.Save(value, 1);
            check(s.Load<T>(bytes, 1));
            check(s.Load<T>(SlowPath(bytes), 1));
        }

        // ── The refusal: every position a rank > 1 array can reach ──────────────────────────────

        /// <summary>
        /// The bug, one row per rank and element type. <c>Save</c> used to succeed and hand back a rank-1
        /// array over the same flattened elements; the assertion is on the <c>Save</c>, because that is
        /// where the decision is — the load cannot make it, the rank is not in the bytes.
        /// </summary>
        public static TheoryData<Array> RankedValues => new()
        {
            new int[,] { { 1, 2 }, { 3, 4 } },
            new int[,,] { { { 1, 2 } }, { { 3, 4 } }, { { 5, 6 } } },
            new string[,] { { "a", "b" }, { "c", "d" } },
        };

        [Theory]
        [MemberData(nameof(RankedValues))]
        public void AMultidimensionalArrayInAnObjectSlot_IsRefusedBySave(Array value)
            => AssertRefused(Record.Exception(() => New().Save(new PmagObjectSlot { Value = value, After = 42 }, 1)),
                value, Name(typeof(object)));

        [Theory]
        [MemberData(nameof(RankedValues))]
        public void AMultidimensionalArrayInABaseClassSlot_IsRefusedBySave(Array value)
            => AssertRefused(Record.Exception(() => New().Save(new PmagArrayBaseSlot { Value = value, After = 42 }, 1)),
                value, Name(typeof(Array)));

        [Theory]
        [MemberData(nameof(RankedValues))]
        public void AMultidimensionalArrayAsACollectionElement_IsRefusedBySave(Array value)
            => AssertRefused(Record.Exception(() => New().Save(new PmagObjectList { Items = new List<object> { value }, After = 42 }, 1)),
                value, Name(typeof(object)));

        [Theory]
        [MemberData(nameof(RankedValues))]
        public void AMultidimensionalArrayBehindAnotherPolymorphicSlot_IsRefusedBySave(Array value)
        {
            // Two hops, and the refusal has to survive the error context the outer object adds.
            var root = new PmagNestedObjectSlot { Inner = new PmagInner { Nested = value, InnerAfter = 7 }, After = 42 };
            AssertRefused(Record.Exception(() => New().Save(root, 1)), value, Name(typeof(object)));
        }

        /// <summary>
        /// A <c>Type</c> member holding <c>typeof(int[,])</c> is refused by the same encoder, and it is
        /// ambiguous for the same reason: a type value is (Array marker, element type) with no rank, so
        /// <c>typeof(int[,])</c> used to be written, load, and come back as <c>typeof(int[])</c> — a
        /// successful save whose loaded value was the wrong type. No bytes and no tag change; the rank-1
        /// form of a <c>Type</c> value is untouched (see <see cref="ARank1TypeValue_StillRoundTrips"/>).
        /// </summary>
        [Theory]
        [InlineData(typeof(int[,]), 2)]
        [InlineData(typeof(string[,,]), 3)]
        public void ATypeValueForAMultidimensionalArray_IsRefused_Too(Type value, int rank)
            => AssertRefused(Record.Exception(() => New().Save(new PmagTypeSlot { Value = value, After = 42 }, 1)),
                value, rank, "a polymorphic slot or a System.Type value");

        // ── Nothing is committed by a refused save ──────────────────────────────────────────────

        /// <summary>
        /// The reference caches are per-operation state that a mid-save exception used to leave populated
        /// (<c>CerasSerializer.Serialize</c> cleared them only on the success path). A later save reusing one
        /// of those objects then wrote a bare back-reference to an id the reader was never given — a save
        /// produced successfully that could not be read ("Object cache does not contain an object with the
        /// ID: n"). The guard cannot undo the registrations of the objects written <em>before</em> the
        /// failing value, so what this pins is the property that makes "fail before Save" safe rather than a
        /// trap: a refused save leaves the serializer able to produce a correct, loadable save.
        /// </summary>
        [Fact]
        public void ARefusedSave_LeavesNoState_ThatWouldCorruptTheNextSave()
        {
            var s = New();
            var shared = new PmagInner { Nested = new[] { 1, 2 }, InnerAfter = 3 };

            // Registered as the first polymorphic member, then the second one is refused.
            var bad = Record.Exception(() => s.Save(new PmagTwoSlotHolder
            {
                First = shared,
                Second = new int[,] { { 1, 2 }, { 3, 4 } },
                After = 42,
            }, 1));
            Assert.IsType<CerasSchemaException>(Innermost(bad!));

            // The same object instance as the refused save, now where the format can describe it.
            var clone = s.Load<PmagTwoSlotHolder>(s.Save(new PmagTwoSlotHolder { First = shared, Second = new[] { 5, 6 }, After = 99 }, 1), 1);
            Assert.Equal(99, clone.After);
            Assert.Equal(3, Assert.IsType<PmagInner>(clone.First).InnerAfter);
            Assert.Equal(new[] { 1, 2 }, Assert.IsType<int[]>(clone.First.Nested));
            Assert.Equal(new[] { 5, 6 }, Assert.IsType<int[]>(clone.Second));
        }

        // ── The controls: what must keep working ────────────────────────────────────────────────

        /// <summary>
        /// Rank 1 through the very same slots, on both readers, with a following field on every one, so a
        /// refusal that was really "skip the value and carry on" would show up as a wrong <c>After</c>. A
        /// jagged array is included: rank 1, and the shape the advice points at.
        /// </summary>
        [Fact]
        public void ARank1Array_StillRoundTripsThroughEveryOneOfThoseSlots_OnBothReaders()
        {
            var s = New();
            int[] rank1 = { 1, 2, 3, 4 };
            int[][] jagged = { new[] { 1, 2 }, new[] { 3 } };

            BothReaders(s, new PmagObjectSlot { Value = rank1, After = 1 }, c =>
            {
                Assert.Equal(rank1, Assert.IsType<int[]>(c.Value));
                Assert.Equal(1, c.After);
            });

            BothReaders(s, new PmagArrayBaseSlot { Value = rank1, After = 2 }, c =>
            {
                Assert.Equal(rank1, Assert.IsType<int[]>(c.Value));
                Assert.Equal(2, c.After);
            });

            // A jagged array through the same slot, which adds a second `Array` type value at a level
            // of its own, behind its own marker.
            BothReaders(s, new PmagArrayBaseSlot { Value = jagged, After = 6 }, c =>
            {
                var loaded = Assert.IsType<int[][]>(c.Value);
                Assert.Equal(new[] { 1, 2 }, loaded[0]);
                Assert.Equal(new[] { 3 }, loaded[1]);
                Assert.Equal(6, c.After);
            });

            BothReaders(s, new PmagObjectList { Items = new List<object> { rank1 }, After = 3 }, c =>
            {
                Assert.Equal(rank1, Assert.IsType<int[]>(Assert.Single(c.Items)));
                Assert.Equal(3, c.After);
            });

            BothReaders(s, new PmagNestedObjectSlot { Inner = new PmagInner { Nested = rank1, InnerAfter = 4 }, After = 5 }, c =>
            {
                var inner = Assert.IsType<PmagInner>(c.Inner);
                Assert.Equal(rank1, Assert.IsType<int[]>(inner.Nested));
                Assert.Equal(4, inner.InnerAfter);
                Assert.Equal(5, c.After);
            });

            BothReaders(s, new PmagObjectSlot { Value = jagged, After = 7 }, c =>
            {
                var loaded = Assert.IsType<int[][]>(c.Value);
                Assert.Equal(new[] { 1, 2 }, loaded[0]);
                Assert.Equal(new[] { 3 }, loaded[1]);
                Assert.Equal(7, c.After);
            });
        }

        /// <summary>A rank-1 <c>Type</c> value is untouched by the same guard.</summary>
        [Fact]
        public void ARank1TypeValue_StillRoundTrips()
        {
            var s = New();
            foreach (Type value in new[] { typeof(int[]), typeof(int[][]), typeof(List<int>) })
            {
                var clone = s.Load<PmagTypeSlot>(s.Save(new PmagTypeSlot { Value = value, After = 42 }, 1), 1);
                Assert.Equal(value, clone.Value);
                Assert.Equal(42, clone.After);
            }
        }

        /// <summary>
        /// The raw (fast, transient) serializer names the full CLR type, rank included, so a multidimensional
        /// array through a polymorphic slot is fine there and must stay fine: the guard fires only while a
        /// robust write context is pushed.
        /// </summary>
        [Fact]
        public void TheRawFastSerializer_StillRoundTripsAMultidimensionalArray()
        {
            var raw = new CerasFastSerializer(c => c.AllowReflectionFallback = true);

            foreach (Array value in new Array[] { new int[,] { { 1, 2 }, { 3, 4 } }, new int[,,] { { { 1 } }, { { 2 } } } })
            {
                var clone = raw.Deserialize<PmagObjectSlot>(raw.Serialize(new PmagObjectSlot { Value = value, After = 42 }));
                var loaded = Assert.IsAssignableFrom<Array>(clone!.Value);
                Assert.Equal(value.GetType(), loaded.GetType());   // the rank, preserved
                Assert.Equal(value, loaded);
                Assert.Equal(42, clone.After);
            }

            // And as a Type value, which the raw TypeFormatter records by name rather than by element type.
            var typeClone = raw.Deserialize<PmagTypeSlot>(raw.Serialize(new PmagTypeSlot { Value = typeof(int[,]), After = 7 }));
            Assert.Equal(typeof(int[,]), typeClone.Value);
            Assert.Equal(7, typeClone.After);
        }

        /// <summary>
        /// The refusals that already existed for a <em>declared</em> rank-1-only shape are unchanged, and
        /// still report the same actionable half of the message.
        /// </summary>
        [Fact]
        public void TheDeclaredShapeRefusals_AreUnchanged()
        {
            var declareRoot = Assert.Throws<CerasSchemaException>(() => new SchemaRegistry().DeclareRoot(typeof(int[,])));
            Assert.Contains(Name(typeof(int[,])), declareRoot.Message);
            Assert.Contains("rank 2", declareRoot.Message);
            Assert.Contains("only one-dimensional arrays", declareRoot.Message);
            Assert.Contains(Advice, declareRoot.Message);

            var structural = Assert.Throws<CerasSchemaException>(() => SchemaRegistry.TryBuildStructuralSchema(typeof(int[,,]), out _));
            Assert.Contains(Name(typeof(int[,,])), structural.Message);
            Assert.Contains("rank 3", structural.Message);
            Assert.Contains(Advice, structural.Message);
        }

        // ── A file that already carries one never loads as a faithful graph ──────────────────────

        /// <summary>
        /// A same-version save (this build's own schema section, so the matching-layout reader runs) whose
        /// payload holds a rank-2 array behind a rank-1 type value — the bytes an unguarded writer produced,
        /// which no Ceras writer can produce any more. There is no rank in those bytes, so the reader cannot
        /// recover the shape; the contract is only that such a file is never handed back as a
        /// plausible-looking graph, on either reader. The assertion is deliberately just "does not succeed
        /// quietly": the exact-consumption check is a detector, not a recovery, and a reader that recognised
        /// the payload would throw here too.
        /// </summary>
        [Fact]
        public void AFileCarryingARank2ArrayInAPolymorphicSlot_DoesNotLoadQuietly()
        {
            var s = New();

            // The counterpart through the identical builder, so the refusal cannot be satisfied by a
            // malformed header, schema section or fingerprint: a rank-1 payload in the same grammar is
            // this build's own layout and reads back exactly, on both readers.
            var control = HandWrittenFileWithArrayOfRank(s, rank: 1);
            var expected = s.Load<PmagObjectSlot>(control, 1);
            Assert.Equal(new[] { 1, 2, 3, 4 }, Assert.IsType<int[]>(expected.Value));
            Assert.Equal(42, expected.After);
            Assert.Null(Record.Exception(() => s.Load<PmagObjectSlot>(SlowPath(control), 1)));

            var rank2 = HandWrittenFileWithArrayOfRank(s, rank: 2);
            Assert.NotNull(Record.Exception(() => s.Load<PmagObjectSlot>(rank2, 1)));
            Assert.NotNull(Record.Exception(() => s.Load<PmagObjectSlot>(SlowPath(rank2), 1)));
        }

        /// <summary>
        /// Reuses a real save's schema section (and therefore its layout fingerprint) and replaces only the
        /// data section with hand-written bytes: <c>root id | root marker | After | NewValue marker | Array |
        /// Int32 | array payload</c>. For rank 1 the payload is <c>ArrayFormatter&lt;int&gt;</c>'s (a biased
        /// element count, then the elements); above 1 it is <c>MultiDimensionalArrayFormatter</c>'s (fixed
        /// rank, one fixed length per dimension, then the flattened elements) — which is what a rank-1
        /// reader misreads as a biased element count. The type value is the rank-1 form either way.
        /// </summary>
        static byte[] HandWrittenFileWithArrayOfRank(CerasRobustSerializer s, int rank)
        {
            var good = s.Save(new PmagObjectSlot { Value = new[] { 1, 2 }, After = 42 }, 1);
            RobustHeader.Read(good, out _, out _, out long schemaStringOffset, out long dataSectionOffset, out _, out ulong fingerprint);

            int rootIdOffset = (int)dataSectionOffset;
            int rootId = (int)SchemaSection.ReadLeb128(good, ref rootIdOffset);
            // The data section is replaced wholesale, so the one byte whose value the layout fixes — the
            // root's reference marker — is read out of the real save rather than assumed.
            Assert.Equal(Protocol.RefNewValueSameType + Protocol.RefBias, good[rootIdOffset]);

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)rootId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias); // the root
            SerializerBinary.WriteInt32Fixed(payload, 42);                                            // After
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValue, Protocol.RefBias);         // Value: not declared as object
            SchemaSection.WriteLeb128(payload, (uint)WellKnownTypes.Array);                           // the rank-1 type value
            SchemaSection.WriteLeb128(payload, (uint)WellKnownTypes.Int32);

            int[] elements = { 1, 2, 3, 4 };
            if (rank == 1)
            {
                SerializerBinary.WriteUInt32Bias(payload, elements.Length, Protocol.CollectionBias);
            }
            else
            {
                SerializerBinary.WriteUInt32(payload, (uint)rank);
                for (int d = 0; d < rank; d++)
                    SerializerBinary.WriteUInt32(payload, 2);   // 2^rank == 4 == the element count
            }
            foreach (int element in elements)
                SerializerBinary.WriteInt32Fixed(payload, element);

            var data = payload.WrittenSpan.ToArray();
            var buffer = new byte[(int)dataSectionOffset + data.Length + 16];
            good.AsSpan(0, (int)dataSectionOffset).CopyTo(buffer);   // header + schema + schema strings
            data.CopyTo(buffer, (int)dataSectionOffset);
            int offset = (int)dataSectionOffset + data.Length;

            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            // The header flag names how the payload table above is stored, so it is the one that flush
            // used rather than the one the source save happened to use.
            RobustHeader.FinalizeHeader(buffer, offset, (byte)StringTableCompression.Disabled,
                schemaStringOffset, dataSectionOffset, stringTableStart, fingerprint);
            return buffer[..offset];
        }
    }
}
