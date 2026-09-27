// Cross-version coverage for the Queue<T>/Stack<T> element count.
//
// QueueFormatter/StackFormatter write the count through the injected IFormatter<int>, i.e. the fixed
// 4-byte little-endian int (Int32Formatter), unbiased and with no null marker. Every *other* collection
// - ArrayFormatter, and CollectionFormatter for List/HashSet/Dictionary and collection subclasses -
// writes WriteUInt32Bias(count, 1), where -1 is a null collection. The interpreter used to read *every*
// collection count with the biased varint, which does not fail loudly: it decodes a plausible number,
// consumes the wrong number of bytes and desynchronises everything after it. These tests pin decoded
// *values*, never the byte layout, so they keep holding whichever way the writer eventually encodes the
// count; the invariant is "the interpreter decodes what the writer wrote".
//
// The split is genuine: a V1 context writes the files, a V2 context reads them with different type ids, a
// different fingerprint (which is what selects the interpreter at all) and different stable names reached
// through [PreviousTypeName], and every V2 type also differs in shape from its V1 counterpart. The
// retained graph holds every built-in collection at once, so "the queue and the stack are read
// differently from List/array/Dictionary, and none of the others moved" is a single assertion set.
//
// Routes: the retained *read* (ReadCollectionShape, also at collection-element level through
// List<Queue<bool>>), the removed *skip* (a removed reference-typed member is consumed by reading its
// reference marker and dispatching on the value's type, i.e. the polymorphic skipper's count read), and
// the un-describable-identity consume route. SkipCollectionPayload's element-count read is the one site a
// Queue/Stack cannot reach - their names always resolve, so the payload always goes through the marker
// first - so its fix is defence in depth. Fixtures/HistoricalV1 is deliberately not extended: its
// regeneration is owned one change at a time. A real cross-version file for a built-in closed generic in
// an `object` slot - a `Type` value rather than a schema type tag - does now exist, in the
// HistoricalV1Poly context; see HistoricalPolymorphicFixtureTests, whose PolyQueueOfQueue fixture pins
// the plain count at a *second* level of nesting. That shape is deliberately not pinned here as well, so
// this file stays a same-assembly pair and keeps its two reader paths comparable.
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.QueueStackCount
{
    namespace V1
    {
        /// <summary>
        /// Every built-in collection in one graph, so a wrong count in any of them shows up as a wrong
        /// value rather than only as a wrong trailing field. v2 keeps all of them and drops LegacyOnly.
        /// </summary>
        [CerasContext("QueueStackCountV1")] [CerasSerializable]
        public class RetainedHolder
        {
            public List<bool> L;
            public Dictionary<string, bool> D;
            public bool[] A;
            public List<Queue<bool>> Nested;     // a queue as a collection *element*, not a member
            public Queue<bool> Q;
            public Stack<bool> S;
            public int Head;
            public int Tail;
            public int LegacyOnly;              // gone in v2: this is what makes the two layouts differ
        }

        /// <summary>Holds both collections; v2 has neither, so their payloads have to be skipped exactly.</summary>
        [CerasContext("QueueStackCountV1")] [CerasSerializable]
        public class RemovedHolder
        {
            public int Head;
            public Queue<bool> Q;
            public Stack<bool> S;
            public int Tail;
        }
    }

    namespace V2
    {
        [PreviousTypeName("Ceras.Test.QueueStackCount.V1.RetainedHolder")]
        [CerasContext("QueueStackCountV2")] [CerasSerializable]
        public class RetainedHolder
        {
            public List<bool> L;
            public Dictionary<string, bool> D;
            public bool[] A;
            public List<Queue<bool>> Nested;
            public Queue<bool> Q;
            public Stack<bool> S;
            public int Head;
            public int Tail;
        }

        [PreviousTypeName("Ceras.Test.QueueStackCount.V1.RemovedHolder")]
        [CerasContext("QueueStackCountV2")] [CerasSerializable]
        public class RemovedHolder
        {
            public int Head;
            public int Tail;
        }

        /// <summary>
        /// Carrier for the hand-built file below. A built-in closed collection in a real <c>object</c> member
        /// is written with a Type <em>value</em> rather than a schema type tag, which the interpreter's
        /// polymorphic reader decodes as its own (recursive) grammar — a separate concern from the count
        /// encoding, and covered for real cross-version files by HistoricalPolymorphicFixtureTests. That is
        /// deliberately not pinned here, so this shape has no V1 twin and no checked-in fixture: the point
        /// of this class is that a *schema-described* queue identity this build cannot name is still
        /// consumed by its own count encoding.
        /// </summary>
        [CerasContext("QueueStackCountV2")] [CerasSerializable]
        public class PolymorphicSlotHolder
        {
            public int Head;
            public object Q;
            public int Tail;
        }
    }

    public class QueueStackCountEncodingTests : TestBase
    {
        const string V1Ctx = "QueueStackCountV1";
        const string V2Ctx = "QueueStackCountV2";

        static CerasRobustSerializer Writer() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(V1Ctx));
        static CerasRobustSerializer Reader() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(V2Ctx));
        static byte[] Save<T>(T value) => Writer().Save(value, 1);
        static ulong FingerprintOf(byte[] bytes) => BitConverter.ToUInt64(bytes, Ceras.RobustHeader.FingerprintOffset);

        // Counts chosen so a *biased* read of them is visibly wrong, never accidentally right:
        //   0   -> fixed 00 00 00 00 | biased 0x00 decodes to -1, the biased encoding's *null collection*
        //   1   -> fixed 01 00 00 00 | biased 0x01 decodes to 0, i.e. an empty collection
        //   3   -> fixed 03 00 00 00 | biased 0x03 decodes to 2, one element short
        //   200 -> fixed C8 00 00 00 | biased spans two bytes and decodes to 71
        static bool[] Pattern(int n)
        {
            var a = new bool[n];
            for (int i = 0; i < n; i++) a[i] = (i % 3) != 2;
            return a;
        }

        /// <summary>A stack whose top-first enumeration is exactly <paramref name="enumeration"/>.</summary>
        static Stack<bool> StackEnumeratingAs(params bool[] enumeration)
        {
            var s = new Stack<bool>();
            for (int i = enumeration.Length - 1; i >= 0; i--) s.Push(enumeration[i]);
            return s;
        }

        static void AssertQueue(bool[] e, Queue<bool> a) => Assert.Equal(e, Assert.IsType<Queue<bool>>(a).ToArray());
        static void AssertStack(bool[] e, Stack<bool> a) => Assert.Equal(e, Assert.IsType<Stack<bool>>(a).ToArray());

        [Fact]
        public void The_Two_Sides_Really_Are_Different_Layouts()
        {
            // Otherwise every test below would silently degrade into a same-version round trip that never
            // enters the interpreter, and the count encoding would never be exercised at all.
            var bytes = Save(new V1.RemovedHolder { Head = 1, Q = new Queue<bool>(), S = new Stack<bool>(), Tail = 2 });

            var writer = Writer();
            writer.Load<V1.RemovedHolder>(bytes, 1);                    // seals, so its fingerprint is final
            Assert.Equal(writer.Config.SchemaRegistry.ComputeFingerprint(), FingerprintOf(bytes));

            var reader = Reader();
            reader.Load<V2.RemovedHolder>(bytes, 1);
            Assert.NotEqual(reader.Config.SchemaRegistry.ComputeFingerprint(), FingerprintOf(bytes));

            // The file's schema section is what the interpreter maps onto the current model, so the two
            // model sets must not share a stable name - otherwise [PreviousTypeName] would do nothing.
            var dump = CerasRobustSerializer.DumpSchema(bytes);
            Assert.Contains("QueueStackCount.V1.RemovedHolder", dump);
            Assert.DoesNotContain("QueueStackCount.V2.RemovedHolder", dump);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(200)]
        [InlineData(1000)]
        public void A_Retained_Graph_Migrates_With_Its_Queue_And_Stack_Element_Counts(int count)
        {
            // List/array/Dictionary keep the biased count with its -1 null marker; the queue and the stack
            // do not. If the fix were "read every collection count the fixed way" this is the test that
            // would notice, because the other shapes would all desynchronise.
            var list = new List<bool>(Pattern(count + 1));
            var array = Pattern(count + 2);
            var dict = Enumerable.Range(0, count).ToDictionary(i => "k" + i, i => (i % 2) == 0);
            var nested = new List<Queue<bool>> { new Queue<bool>(Pattern(count)), new Queue<bool>() };
            var queue = Pattern(count);
            var stack = Pattern(count + 1);

            var loaded = Reader().Load<V2.RetainedHolder>(Save(new V1.RetainedHolder
            {
                L = list, D = dict, A = array, Nested = nested, Q = new Queue<bool>(queue),
                S = StackEnumeratingAs(stack), Head = 11, Tail = 4242, LegacyOnly = 0x5A5A,
            }), 1);

            AssertQueue(queue, loaded.Q);
            AssertStack(stack, loaded.S);
            // Both rules in one payload: the outer list's count is biased, each queue element's is not.
            AssertQueue(queue, loaded.Nested[0]);
            Assert.Empty(Assert.IsType<Queue<bool>>(loaded.Nested[1]));
            Assert.Equal(list, loaded.L);
            Assert.Equal(array, loaded.A);
            Assert.Equal(count, loaded.D.Count);
            for (int i = 0; i < count; i++) Assert.Equal((i % 2) == 0, loaded.D["k" + i]);
            Assert.Equal(11, loaded.Head);
            // The trailing field is the alignment witness, and the count-0 case is the sharpest one in the
            // set: read as a biased varint its leading 0x00 decodes to -1, so an empty queue would have
            // silently become null and three of its four count bytes would have been eaten as a field.
            Assert.Equal(4242, loaded.Tail);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(200)]
        public void Removed_Queue_And_Stack_Leave_The_Trailing_Field_Aligned(int count)
        {
            var loaded = Reader().Load<V2.RemovedHolder>(Save(new V1.RemovedHolder
            {
                Head = 31, Q = new Queue<bool>(Pattern(count)), S = StackEnumeratingAs(Pattern(count + 1)), Tail = 1234,
            }), 1);

            Assert.Equal(31, loaded.Head);
            Assert.Equal(1234, loaded.Tail);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(200)]
        public void A_Matching_Layout_Reads_Identically_Through_Both_Readers(int count)
        {
            // The control for "the fix is targeted at the interpreter and agrees with the writer": the same
            // bytes are read twice, once by the generated fast path (the writer's own counterpart) and once
            // by the interpreter with the fingerprint flipped. Both must produce the same object, so the
            // interpreter decodes exactly what the writer wrote.
            var serializer = Writer();
            var bytes = serializer.Save(new V1.RetainedHolder
            {
                L = new List<bool>(Pattern(count)), D = new Dictionary<string, bool> { ["x"] = true },
                A = Pattern(count + 1), Nested = new List<Queue<bool>> { new Queue<bool>(Pattern(count + 1)) },
                Q = new Queue<bool>(Pattern(count)), S = StackEnumeratingAs(Pattern(count + 2)),
                Head = 5, Tail = 606, LegacyOnly = 7,
            }, 1);

            foreach (var loaded in new[]
            {
                serializer.Load<V1.RetainedHolder>(bytes, 1),
                serializer.Load<V1.RetainedHolder>(SlowPath(bytes), 1),
            })
            {
                AssertQueue(Pattern(count), loaded.Q);
                AssertStack(Pattern(count + 2), loaded.S);
                AssertQueue(Pattern(count + 1), loaded.Nested[0]);
                Assert.Equal(Pattern(count), loaded.L);
                Assert.Equal(Pattern(count + 1), loaded.A);
                Assert.True(loaded.D["x"]);
                Assert.Equal(5, loaded.Head);
                Assert.Equal(606, loaded.Tail);
                Assert.Equal(7, loaded.LegacyOnly);
            }
        }

        // The last route: a queue identity the current build cannot describe, so there is no instance to
        // fill and the payload has to be consumed to keep the stream aligned. A real save cannot reach it
        // for Queue/Stack - their stable names are BCL names and always resolve - which is why the file is
        // hand-built: the schema entry carries a name no assembly here can satisfy, while the definition
        // and argument ids are exactly what the writer would have written.
        const int LegacyRootId = 900, LegacyQueueId = 901, LegacyHead = 4711, LegacyTail = 0x0BADF00D;

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(5)]
        public void An_Undescribable_Queue_Payload_Is_Consumed_By_Its_Own_Count_Encoding(int elements)
        {
            var loaded = Reader().Load<V2.PolymorphicSlotHolder>(BuildFileWithUndescribableQueue(elements), 1);

            // No value can be produced for a shape the build cannot describe, but the bytes must still be
            // accounted for, and the trailing field is the witness for it.
            Assert.Null(loaded.Q);
            Assert.Equal(LegacyHead, loaded.Head);
            Assert.Equal(LegacyTail, loaded.Tail);
        }

        /// <summary>
        /// A minimal but otherwise valid robust file, assembled straight into the file buffer: a class
        /// schema whose middle member is a generic instantiation named after a type this build does not
        /// have, then the payload - the member's marker, its type tag, and the queue's own 4-byte count.
        /// </summary>
        static byte[] BuildFileWithUndescribableQueue(int elements)
        {
            var buffer = new byte[512];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            var schema = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            SchemaSection.WriteLeb128(schema, 2);       // the root + the queue instantiation

            // The root: Head (int), Q (the unresolvable queue), Tail (int), named after the *current* type
            // so the load's root identity check resolves it.
            SchemaSection.WriteLeb128(schema, LegacyRootId);
            SchemaSection.WriteInternedString(schema, "Ceras.Test.QueueStackCount.V2.PolymorphicSlotHolder", intern);
            SchemaSection.WriteLeb128(schema, 0);
            schema.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; schema.Advance(1);
            SchemaSection.WriteLeb128(schema, 3);
            foreach (var (name, typeId) in new[]
            {
                ("Head", WellKnownTypes.Int32), ("Q", LegacyQueueId), ("Tail", WellKnownTypes.Int32),
            })
            {
                SchemaSection.WriteInternedString(schema, name, intern);
                SchemaSection.WriteLeb128(schema, 0);
                SchemaSection.WriteLeb128(schema, (uint)typeId);
                schema.GetSpan(1)[0] = 0; schema.Advance(1);   // not monomorphic: a marker is written for it
            }
            SchemaSection.WriteLeb128(schema, 0);

            // A GenericInstantiation is reconstructed from its definition id and argument ids, so the file
            // stays loadable while the current manifest has no collection type to map the name onto.
            SchemaSection.WriteLeb128(schema, LegacyQueueId);
            SchemaSection.WriteInternedString(schema, "Legacy.Gone.Queue`1<System.Boolean>", intern);
            SchemaSection.WriteLeb128(schema, 0);
            schema.GetSpan(1)[0] = (byte)SchemaTypeKind.GenericInstantiation; schema.Advance(1);
            SchemaSection.WriteLeb128(schema, (uint)WellKnownTypes.Queue);
            SchemaSection.WriteLeb128(schema, 1);
            SchemaSection.WriteLeb128(schema, (uint)WellKnownTypes.Bool);
            SchemaSection.WriteLeb128(schema, 0);
            SchemaSection.WriteLeb128(schema, 0);

            int schemaStringOffset = schema.Offset;
            intern.Flush(ref schema.Buffer, ref schema.Offset);
            int dataStart = schema.Offset;

            var payload = new CerasSerializer.FixedArrayBufferWriter(schema.Buffer, schema.Offset);
            SchemaSection.WriteLeb128(payload, LegacyRootId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteInt32Fixed(payload, LegacyHead);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValue, Protocol.RefBias);
            SchemaSection.WriteLeb128(payload, LegacyQueueId);                     // the type tag
            SerializerBinary.WriteInt32Fixed(payload, elements);                   // the queue's own count
            for (int i = 0; i < elements; i++) SerializerBinary.WriteByte(payload, (byte)(i % 2));
            SerializerBinary.WriteInt32Fixed(payload, LegacyTail);                 // the alignment witness

            int stringTableStart = payload.Offset;
            new StringInternTable().Flush(ref payload.Buffer, ref payload.Offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(payload.Buffer, payload.Offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return payload.Buffer[..payload.Offset];
        }
    }
}
