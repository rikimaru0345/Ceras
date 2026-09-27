// Cross-version tests for a value whose concrete runtime type the file records as a recursive System.Type
// *value* rather than a schema type id — which is what a closed built-in generic, an array or a declared
// `object` in a polymorphic slot is. The bytes come from a *separate historical writer*
// (fixtures/Ceras.HistoricalWriter) holding the v1 CLR model in its own compilation and context, which this
// assembly does not reference, so the v1 types are absent at load time. Same-assembly round tripping cannot
// stand in for that: it never enters the interpreter, which is the only thing under test here.
//
// Every crate ends in a fixed-width uint sentinel written *after* the slot (member order puts
// "System.Object" before "System.UInt32"), so a mis-decoded slot shows up as a silently wrong number rather
// than as a load that happens to succeed; the theories assert it once per case. Cases are grouped by the
// grammar they exercise rather than one method per fixture, and the hand-assembled files at the end cover
// shapes no writer produces, so those cannot come from a fixture at all.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Ceras.Helpers;

[assembly: CerasKnownType<List<Ceras.Test.HistoricalPolymorphicFixtures.Waypoint>>("HistoricalPolyCurrent")]
[assembly: CerasKnownType<Ceras.Test.HistoricalPolymorphicFixtures.Box<int>>("HistoricalPolyCurrent")]

namespace Ceras.Test.HistoricalPolymorphicFixtures
{
    // ── The current model ──────────────────────────────────────────────────────────────────────────
    // Every type is renamed relative to v1 (and reached through [PreviousTypeName]), and the removals are
    // what make the two layouts genuinely differ, so the fingerprint cannot match and the load has to take
    // the migration interpreter rather than the generated fast path.

    /// <summary>v1's <c>PolyNode</c>, renamed. It is a <c>List</c> element inside a polymorphic slot, so
    /// only the argument id in the slot's type value can resolve it.</summary>
    [CerasContext("HistoricalPolyCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Poly.PolyNode")]
    public class Waypoint
    {
        public int X;
        public int Y;
        public string Label;
    }

    /// <summary>v1's <c>PolyBeast</c>, renamed: the control whose tag is a manifest id, not a type value.</summary>
    [CerasContext("HistoricalPolyCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Poly.PolyBeast")]
    public class Creature
    {
        public int Hit;
    }

    /// <summary>
    /// A user generic class in a slot: also a manifest id, and the control for "a generic instantiation
    /// the schema section describes with members". Deliberately <b>not</b> renamed — the generator carries
    /// a <c>[PreviousTypeName]</c> from the open definition onto a closed instantiation unchanged, so the
    /// old file's name for <c>Box&lt;int&gt;</c> (ending in <c>&lt;System.Int32&gt;</c>) would never match
    /// and a renamed instantiation is unresolvable from an old file. That is a pre-existing gap needing a
    /// generator change, so it is left visible here rather than hidden behind a fixture.
    /// </summary>
    [CerasContext("HistoricalPolyCurrent")]
    [CerasSerializable]
    public class Box<T>
    {
        public int Tag;
        public T Value;
    }

    [CerasContext("HistoricalPolyCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Poly.PolyCrate")]
    public class Crate
    {
        public object Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>PolyEmptyCrate</c>: the current model has no slot, so its bytes are consumed.</summary>
    [CerasContext("HistoricalPolyCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Poly.PolyEmptyCrate")]
    public class EmptyCrate
    {
        public uint Marker;
    }

    /// <summary>v1's <c>PolyMixedCrate</c>: three members then, two now, so only the middle one is consumed.</summary>
    [CerasContext("HistoricalPolyCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Poly.PolyMixedCrate")]
    public class MixedCrate
    {
        public object Head;
        public uint Marker;
    }

    public class HistoricalPolymorphicFixtureTests : TestBase
    {
        const string Ctx = "HistoricalPolyCurrent";
        const uint Sentinel = 0xA5A5A5A5u;

        /// <summary>The current model's canonical name, as the hand-assembled files below declare it.</summary>
        const string RootName = "Ceras.Test.HistoricalPolymorphicFixtures.Crate";

        /// <summary>The manifest ids the hand-assembled files give their schema entries.</summary>
        const int RootTypeId = WellKnownTypes.UserTypeBase, UserGenericId = RootTypeId + 1, UserTypeId = RootTypeId + 2;

        // The stable names the hand-assembled user-generic entries use. They are the *current* model's own
        // names on purpose: a schema entry whose name resolves to nothing aborts the whole load while the
        // file's id map is built, so those cases are about the payload grammar, not name resolution.
        static readonly string BoxDefinitionName = TypeIdentity.GetCanonicalName(typeof(Box<>));
        static readonly string BoxName = TypeIdentity.GetCanonicalName(typeof(Box<int>));

        /// <summary>Every fixture this file reads, so each one is pinned by a checked-in file.</summary>
        static readonly string[] AllFixtures =
        {
            "PolyNestedList.bin", "PolyJagged.bin", "PolyRenamedNested.bin", "PolyQueueOfQueue.bin",
            "PolyMixedShapes.bin", "PolyUserType.bin", "PolyObjectKept.bin", "PolyRemovedSlot.bin",
            "PolyRemovedNested.bin", "PolyRemovedObjectMid.bin",
        };

        static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1");

        static byte[] Fixture(string name)
        {
            string path = Path.Combine(FixtureDir, name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        /// <summary>The migration reader, and the only one these tests use unless they say otherwise.</summary>
        static CerasRobustSerializer Reader() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        /// <summary>A serializer over the <em>current</em> model, for the matching-layout controls.</summary>
        static CerasRobustSerializer Current() => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            c.SetUnknownBuildTimestamp();
        });

        // ── The files are what they claim to be ─────────────────────────────────────────────────────

        [Fact]
        public void Every_Fixture_Is_An_Old_File_Of_This_Format_With_An_Intact_Checksum()
        {
            foreach (var name in AllFixtures)
            {
                var bytes = Fixture(name);

                // The loader verifies the CRC before it interprets anything, so a green load already implies
                // it — but "the value after the slot is right" only means something if these are the bytes
                // that were written.
                uint stored = BitConverter.ToUInt32(bytes, RobustHeader.ChecksumOffset);
                Assert.Equal(HashHelpers.CalculateCrc32WithZeroedRange(
                    bytes, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize), stored);

                // The file's schema section holds the *old* stable names, so the load really has to map
                // them through [PreviousTypeName] rather than finding the current types by name.
                var dump = CerasRobustSerializer.DumpSchema(bytes);
                Assert.Contains("Ceras.HistoricalWriter.V1Poly", dump);
                Assert.DoesNotContain("Ceras.Test.HistoricalPolymorphicFixtures", dump);
            }
        }

        [Fact]
        public void The_Fixtures_Really_Do_Not_Match_The_Current_Layout()
        {
            // Otherwise every test below would silently degrade into same-version round trips, which never
            // enter the interpreter — and the interpreter is the only thing being fixed here.
            foreach (var (name, load) in new (string, Action<CerasRobustSerializer, byte[]>)[]
                     { ("PolyNestedList.bin", (s, b) => s.Load<Crate>(b, 1)), ("PolyRemovedSlot.bin", (s, b) => s.Load<EmptyCrate>(b, 1)) })
            {
                var reader = Reader();
                load(reader, Fixture(name));                    // seals, so the fingerprint is final
                Assert.NotEqual(reader.Config.SchemaRegistry.ComputeFingerprint(),
                    BitConverter.ToUInt64(Fixture(name), RobustHeader.FingerprintOffset));
            }
        }

        // ── A retained slot: the read path ──────────────────────────────────────────────────────────

        static object[] Row(string fixture, Action<Crate> check) => new object[] { fixture, check };

        public static IEnumerable<object[]> RetainedSlots()
        {
            // A closed built-in generic: the outer List's argument is itself a whole type value, and each
            // inner one is a reference behind its own marker. An empty inner collection is not a null one.
            yield return Row("PolyNestedList.bin", c =>
            {
                var groups = Assert.IsType<List<List<int>>>(c.Slot);
                Assert.Equal(3, groups.Count);
                Assert.Equal(new[] { 10, 20 }, groups[0]);
                Assert.Empty(groups[1]);
                Assert.Equal(new[] { 30 }, groups[2]);
            });

            // A jagged array: two levels of Array, each behind its own reference marker.
            yield return Row("PolyJagged.bin", c =>
            {
                var rows = Assert.IsType<int[][]>(c.Slot);
                Assert.Equal(3, rows.Length);
                Assert.Equal(new[] { 1, 2 }, rows[0]);
                Assert.Equal(new[] { 3 }, rows[1]);
                Assert.Empty(rows[2]);
            });

            // A renamed user type as a nested *argument*: the type value names v1's PolyNode by the file's
            // own id and the current model calls it Waypoint. Only the file's schema section plus its
            // [PreviousTypeName] alias connects the two.
            yield return Row("PolyRenamedNested.bin", c =>
            {
                var points = Assert.IsType<List<Waypoint>>(c.Slot);
                Assert.Equal(2, points.Count);
                Assert.Equal(1, points[0].X);
                Assert.Equal("start", points[0].Label);
                Assert.Equal(3, points[1].X);
                Assert.Equal("end", points[1].Label);
            });

            // A queue of queues. QueueFormatter writes its count as a plain fixed 4-byte int while every
            // other collection writes a biased count; the rule is per definition, so it has to hold at the
            // second level too. This is the composition with the merged Queue/Stack count fix.
            yield return Row("PolyQueueOfQueue.bin", c =>
            {
                var outer = Assert.IsType<Queue<Queue<int>>>(c.Slot);
                Assert.Equal(2, outer.Count);
                Assert.Equal(new[] { 7, 8 }, outer.ElementAt(0).ToArray());
                Assert.Empty(outer.ElementAt(1));
            });

            // The keyed shape with an array value: a Dictionary of jagged arrays, so a count plus key/value
            // entries whose values are themselves arrays behind markers, and one entry with an empty value.
            yield return Row("PolyMixedShapes.bin", c =>
            {
                var map = Assert.IsType<Dictionary<string, int[][]>>(c.Slot);
                Assert.Equal(2, map.Count);
                Assert.Equal(new[] { new[] { 1, 2 }, new[] { 3 } }, map["row"]);
                Assert.Empty(map["col"]);
            });

            // The control: a value the file's schema section *does* describe, so its tag is a manifest id
            // and nothing about the built-in-generic path applies. It has to keep working unchanged.
            yield return Row("PolyUserType.bin", c => Assert.Equal(17, Assert.IsType<Creature>(c.Slot).Hit));

            // `new object()`'s runtime type *is* System.Object, so the writer takes the same-type marker and
            // writes no tag and no payload: the value is one byte. A reader that read a tag instead would
            // desynchronise the sentinel behind it.
            yield return Row("PolyObjectKept.bin", c =>
            {
                Assert.NotNull(c.Slot);
                Assert.Equal(typeof(object), c.Slot.GetType());
            });
        }

        [Theory]
        [MemberData(nameof(RetainedSlots))]
        public void A_Retained_Slot_Loads_Through_The_Interpreter(string fixture, Action<Crate> check)
        {
            var crate = Reader().Load<Crate>(Fixture(fixture), 1);

            // The sentinel first: it is the witness that the slot consumed exactly its own bytes.
            Assert.Equal(Sentinel, crate.Marker);
            check(crate);
        }

        // ── A removed slot: the skip path ───────────────────────────────────────────────────────────

        static object[] SkipRow(string fixture, Action<CerasRobustSerializer, byte[]> check)
            => new object[] { fixture, check };

        public static IEnumerable<object[]> RemovedSlots()
        {
            // Marker, both nested type values, both counts and every element must be consumed for the
            // fixed-width sentinel to hold the written number.
            yield return SkipRow("PolyRemovedSlot.bin", (s, b) =>
                Assert.Equal(Sentinel, s.Load<EmptyCrate>(b, 1).Marker));

            // A nested shape between a retained class and the sentinel: both are alignment witnesses.
            yield return SkipRow("PolyRemovedNested.bin", (s, b) => AssertMixed(s.Load<MixedCrate>(b, 1), 42));

            // The same skip path for an *empty* payload: `object` has no members, so one byte is all of it.
            yield return SkipRow("PolyRemovedObjectMid.bin", (s, b) => AssertMixed(s.Load<MixedCrate>(b, 1), 7));
        }

        /// <summary>The two witnesses a removed middle member must leave alone: the class before it, the
        /// sentinel after it.</summary>
        static void AssertMixed(MixedCrate crate, int expectedHit)
        {
            Assert.Equal(expectedHit, Assert.IsType<Creature>(crate.Head).Hit);
            Assert.Equal(Sentinel, crate.Marker);
        }

        [Theory]
        [MemberData(nameof(RemovedSlots))]
        public void A_Removed_Slot_Leaves_The_Fields_After_It_Aligned(string fixture, Action<CerasRobustSerializer, byte[]> check)
            => check(Reader(), Fixture(fixture));

        // ── A declared System.Object: an empty payload, with identity ───────────────────────────────

        [Fact]
        public void An_Object_Root_And_Shared_Objects_Behave_Through_Both_Readers()
        {
            var writer = Current();
            var shared = new object();
            var distinct = new object();

            // An `object` root is two bytes: the root type id and the marker. `object` has no members, so
            // there is nothing between them and the exact-consumption check proves it.
            var rootBytes = writer.Save<object>(new object(), 1);
            Assert.Equal(typeof(object), writer.Load<object>(rootBytes, 1).GetType());
            Assert.Equal(typeof(object), writer.Load<object>(SlowPath(rootBytes), 1).GetType());

            // A `List<object>` slot: two *different* objects stay different, the same one twice comes back as
            // one instance, and the list around them is itself a built-in generic read from a type value.
            // The generated reader and the interpreter have to agree on both halves.
            var bytes = writer.Save(new Crate
            {
                Slot = new List<object> { distinct, distinct, shared, shared },
                Marker = Sentinel,
            }, 1);

            foreach (var loaded in new[] { writer.Load<Crate>(bytes, 1), writer.Load<Crate>(SlowPath(bytes), 1) })
            {
                var list = Assert.IsType<List<object>>(loaded.Slot);
                Assert.Equal(typeof(object), list[0].GetType());
                Assert.Same(list[0], list[1]);
                Assert.Same(list[2], list[3]);
                Assert.NotSame(list[0], list[2]);
                Assert.Equal(Sentinel, loaded.Marker);
            }
        }

        // ── Equal type values share one synthesized identity ───────────────────────────────────────

        [Fact]
        public void Many_Repeated_Values_Load_Without_Costing_One_Identity_Each()
        {
            // The interning is by value, so the retained cost of a polymorphic slot's type value is the
            // number of *distinct* nested shapes, not the number of values. A per-occurrence identity would
            // make this save need twenty thousand of them and would eventually be refused as corrupt.
            const int Count = 20_000;                          // well past any per-occurrence bound
            object Shape(int i) => (i % 3) switch
            {
                0 => new List<int> { i },
                1 => new List<List<int>> { new List<int> { i } },
                _ => new[] { i, i + 1 },
            };

            var items = Enumerable.Range(0, Count).Select(Shape).ToList();
            var bytes = Current().Save(new Crate { Slot = items, Marker = Sentinel }, 1);
            var loaded = Current().Load<Crate>(SlowPath(bytes), 1);   // the interpreter, on real writer bytes

            var list = Assert.IsType<List<object>>(loaded.Slot);
            Assert.Equal(Count, list.Count);
            Assert.Equal(Sentinel, loaded.Marker);

            // A sample rather than all twenty thousand: a desync would already have moved the sentinel, and
            // the values are a repeating pattern of three, so the first and last of each shape is decisive.
            foreach (int i in new[] { 0, 1, 2, Count - 3, Count - 2, Count - 1 })
            {
                switch (i % 3)
                {
                    case 0: Assert.Equal(new[] { i }, Assert.IsType<List<int>>(list[i])); break;
                    case 1: Assert.Equal(new[] { i }, Assert.IsType<List<int>>(Assert.IsType<List<List<int>>>(list[i])[0])); break;
                    default: Assert.Equal(new[] { i, i + 1 }, Assert.IsType<int[]>(list[i])); break;
                }
            }
        }

        // ── The matching-layout control ───────────────────────────────────────────────────────────

        public static IEnumerable<object[]> MatchingLayoutCases()
        {
            // (name, slot, check) — the built-in-generic shapes the two readers could most plausibly read
            // differently, written by *this* build so the fingerprint matches and the generated fast path is
            // the reader. A cross-version fixture cannot serve as this control: its fingerprint never
            // matches. The user-type shapes above need no such cross-check — the generated reader for them
            // is the type's own formatter, untouched by this change.
            yield return new object[] { "nested list", new List<List<int>> { new List<int> { 1, 2 }, new List<int>() },
                (Action<object>)(o => { var l = Assert.IsType<List<List<int>>>(o); Assert.Equal(new[] { 1, 2 }, l[0]); Assert.Empty(l[1]); }) };
            yield return new object[] { "jagged", new int[][] { new[] { 3, 4 }, Array.Empty<int>() },
                (Action<object>)(o => { var a = Assert.IsType<int[][]>(o); Assert.Equal(new[] { 3, 4 }, a[0]); Assert.Empty(a[1]); }) };
            yield return new object[] { "dict of jagged", new Dictionary<string, int[][]> { ["k"] = new[] { new[] { 5 } } },
                (Action<object>)(o => Assert.Equal(new[] { new[] { 5 } }, Assert.IsType<Dictionary<string, int[][]>>(o)["k"])) };
            yield return new object[] { "queue of queue", new Queue<Queue<int>>(new[] { new Queue<int>(new[] { 6, 7 }), new Queue<int>() }),
                (Action<object>)(o => { var q = Assert.IsType<Queue<Queue<int>>>(o); Assert.Equal(new[] { 6, 7 }, q.ElementAt(0).ToArray()); Assert.Empty(q.ElementAt(1)); }) };
        }

        [Theory]
        [MemberData(nameof(MatchingLayoutCases))]
        public void A_Matching_Layout_Reads_The_Same_Values_Through_Both_Readers(
            string name, object slot, Action<object> check)
        {
            // The control for "the interpreter decodes exactly what the writer wrote": one set of bytes,
            // read by the generated fast path and again by the interpreter with the fingerprint flipped.
            var writer = Current();
            var bytes = writer.Save(new Crate { Slot = slot, Marker = Sentinel }, 1);

            foreach (var crate in new[] { writer.Load<Crate>(bytes, 1), writer.Load<Crate>(SlowPath(bytes), 1) })
            {
                Assert.Equal(Sentinel, crate.Marker);
                check(crate.Slot);
            }
        }

        // ── Unknown identity and malformed ids ─────────────────────────────────────────────────────
        // A tag or argument the decoder cannot account for has to be *refused*, never read as a value of
        // no bytes: that is what silently desynchronised every field after it.

        /// <summary>A one-argument <c>List&lt;T&gt;</c> type value whose argument tag is <paramref name="tag"/>.</summary>
        static Action<IBufferWriter<byte>> ListOf(uint tag) => w => WriteTags(w, WellKnownTypes.List, 1, tag);

        /// <summary>A <c>List&lt;T&gt;</c> type value whose declared argument count is <paramref name="count"/>,
        /// with no arguments actually written.</summary>
        static Action<IBufferWriter<byte>> ListWithArgCount(uint count) => w => WriteTags(w, WellKnownTypes.List, count);

        /// <summary>A type value of <paramref name="tag"/> followed by the given raw LEB128 tags.</summary>
        static Action<IBufferWriter<byte>> Tagged(uint tag, params uint[] rest) => w => WriteTags(w, tag, rest);

        static void WriteTags(IBufferWriter<byte> w, uint tag, params uint[] rest)
        {
            SchemaSection.WriteLeb128(w, tag);
            foreach (var t in rest) SchemaSection.WriteLeb128(w, t);
        }

        /// <summary>
        /// (why it is impossible, the type value to write, the exception it must raise, what it must say).
        /// Each row is a shape a writer cannot produce, so a fixture cannot supply the bytes.
        /// </summary>
        public static IEnumerable<object[]> MalformedTypeValues()
        {
            // Ids 43-126 are reserved and unassigned; a tag there used to consume nothing at all.
            yield return new object[] { "a reserved, unassigned well-known id",
                Tagged(WellKnownTypes.DateTimeOffset + 1),
                typeof(CerasDataCorruptionException), "no type is assigned to it" };

            // A generic argument and an element type are not optional, so null nested is a corrupt field.
            yield return new object[] { "a null nested argument", ListOf(WellKnownTypes.Null),
                typeof(CerasDataCorruptionException), "is null" };

            // The largest well-known arity is 7 (the tuple family): bounded by the definition, not the bytes.
            yield return new object[] { "an argument count no definition can take", ListWithArgCount(200),
                typeof(CerasDataCorruptionException), "takes 8" };

            // Eight is within every arity, so this is the *bytes left* bound, which also sizes an array.
            yield return new object[] { "an argument count past the end of the payload", ListWithArgCount(8),
                typeof(CerasDataCorruptionException), "bytes remain" };

            // A full name is not an identity: it cannot say how many bytes the payload occupies.
            yield return new object[] { "a full name where an argument identity is needed", ListOf(127),
                typeof(CerasDataCorruptionException), "full name" };

            // A tag is written only when declared != runtime type, and nothing derives from object.
            yield return new object[] { "System.Object as a type tag",
                Tagged(WellKnownTypes.Object),
                typeof(CerasDataCorruptionException), "System.Object" };

            // A type id is never negative: the LEB128 is five bytes wide, and a value above int.MaxValue
            // cast to int is a negative id, which would then read as a small well-known type.
            yield return new object[] { "a tag above int.MaxValue",
                (Action<IBufferWriter<byte>>)(w =>
                    { foreach (byte b in new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 }) { w.GetSpan(1)[0] = b; w.Advance(1); } }),
                typeof(CerasDataCorruptionException), "is not a type id" };

            // A tag at or above the manifest base is a schema id, and only the file's schema section says
            // how such a payload is written.
            yield return new object[] { "a manifest id the file does not describe",
                Tagged(9999),
                typeof(CerasSchemaException), "9999" };

            // The same one level in: a List whose element is a type id nothing describes.
            yield return new object[] { "an argument identity the file does not describe", ListOf(9999),
                typeof(CerasSchemaException), "9999" };
        }

        [Theory]
        [MemberData(nameof(MalformedTypeValues))]
        public void A_Type_Value_The_Decoder_Cannot_Account_For_Is_Refused(
            string why, Action<IBufferWriter<byte>> writeTypeValue, Type exception, string expected)
        {
            // `why` is the case's own description, carried in the theory's arguments so a failure names the
            // shape rather than an index; the assertions are on the exception type and its message.
            byte[] File() => FileWithSlotPayload(writeTypeValue, extraSlotSchemas: null);
            var error = Record.Exception(() => Reader().Load<Crate>(File(), 1));
            Assert.NotNull(error);
            Assert.IsType(exception, error);          // the exact type: these two are different contracts
            Assert.Contains(expected, error.Message);
        }

        [Fact]
        public void A_Generic_Instantiation_Of_A_User_Definition_Is_A_Member_Payload_Not_A_Count()
        {
            // A generic instantiation whose *definition* the well-known set does not name is a user generic
            // class, and its payload is its members. The skipper used to read a member count for it, which
            // consumes the first member's bytes as a count and then that many elements' worth, so every
            // following field came out wrong without failing. Tag is 3 here: read as a biased count that is
            // "2 elements", so the skip would run two ints past the members and eat the sentinel.
            // The shape is hand-assembled because today's generator emits a user generic as a plain class
            // entry; GenericInstantiation is the shape the schema section already allows, and read and skip
            // have to agree on it either way.
            var bytes = FileWithSlotPayload(
                w =>
                {
                    SchemaSection.WriteLeb128(w, (uint)UserGenericId);       // a manifest id, not a type value
                    SerializerBinary.WriteInt32Fixed(w, 3);                 // Tag
                    SerializerBinary.WriteInt32Fixed(w, 5);                 // Value
                },
                extraSlotSchemas: (IBufferWriter<byte> w, StringInternTable intern, uint id) =>
                {
                    // kind = GenericInstantiation, definition = the user type's own id, one argument
                    // (System.Int32), and the two members the payload really has.
                    SchemaSection.WriteLeb128(w, id);
                    SchemaSection.WriteInternedString(w, BoxName, intern);
                    SchemaSection.WriteLeb128(w, 0);
                    w.GetSpan(1)[0] = (byte)SchemaTypeKind.GenericInstantiation; w.Advance(1);
                    SchemaSection.WriteLeb128(w, (uint)UserTypeId);
                    SchemaSection.WriteLeb128(w, 1);
                    SchemaSection.WriteLeb128(w, (uint)WellKnownTypes.Int32);
                    SchemaSection.WriteLeb128(w, 2);
                    WriteMember(w, intern, "Tag", WellKnownTypes.Int32, monomorphic: true);
                    WriteMember(w, intern, "Value", WellKnownTypes.Int32, monomorphic: true);
                    SchemaSection.WriteLeb128(w, 0);

                    // The open definition, so the instantiation's definition id resolves to a real type.
                    SchemaSection.WriteLeb128(w, (uint)UserTypeId);
                    SchemaSection.WriteInternedString(w, BoxDefinitionName, intern);
                    SchemaSection.WriteLeb128(w, 0);
                    w.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; w.Advance(1);
                    SchemaSection.WriteLeb128(w, 0);
                    SchemaSection.WriteLeb128(w, 0);
                });

            var crate = Reader().Load<Crate>(bytes, 1);

            // The instance is built from the members — and the sentinel, the witness, has to survive the
            // read *and* the skip of those same members.
            var box = Assert.IsType<Box<int>>(crate.Slot);
            Assert.Equal(3, box.Tag);
            Assert.Equal(5, box.Value);
            Assert.Equal(Sentinel, crate.Marker);
        }

        // ── Hand-assembled files ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A valid robust file whose root is a two-member class (an <c>object</c> slot and a <c>uint</c>
        /// sentinel), the slot's payload written by <paramref name="writeSlot"/>, and
        /// <paramref name="extraSlotSchemas"/> adding whatever entries that payload's tag refers to.
        /// Hand-assembled on purpose — the bytes under test cannot be produced by a writer — and written
        /// through the same <see cref="CerasSerializer.FixedArrayBufferWriter"/> the rest of the suite uses,
        /// so there is no second buffer writer here to keep in step with anything.
        /// </summary>
        static byte[] FileWithSlotPayload(Action<IBufferWriter<byte>> writeSlot, ExtraSlotSchemas extraSlotSchemas)
        {
            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            var schema = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            SchemaSection.WriteLeb128(schema, (uint)(1 + (extraSlotSchemas == null ? 0 : 2)));
            SchemaSection.WriteLeb128(schema, (uint)RootTypeId);
            SchemaSection.WriteInternedString(schema, RootName, intern);
            SchemaSection.WriteLeb128(schema, 0);                      // no previous type names
            schema.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; schema.Advance(1);
            SchemaSection.WriteLeb128(schema, 2);                      // Slot, then Marker
            WriteMember(schema, intern, "Slot", WellKnownTypes.Object, monomorphic: false);
            WriteMember(schema, intern, "Marker", WellKnownTypes.UInt32, monomorphic: true);
            SchemaSection.WriteLeb128(schema, 0);                      // no fixed byte size
            extraSlotSchemas?.Invoke(schema, intern, (uint)UserGenericId);

            int schemaStringOffset = schema.Offset;
            intern.Flush(ref schema.Buffer, ref schema.Offset);
            int dataStart = schema.Offset;

            var payload = new CerasSerializer.FixedArrayBufferWriter(schema.Buffer, schema.Offset);
            SchemaSection.WriteLeb128(payload, (uint)RootTypeId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValue, Protocol.RefBias);
            writeSlot(payload);
            SerializerBinary.WriteInt32Fixed(payload, unchecked((int)Sentinel));

            int stringTableStart = payload.Offset;
            var emptyStrings = new StringInternTable();
            emptyStrings.Flush(ref payload.Buffer, ref payload.Offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(payload.Buffer, payload.Offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return payload.Buffer[..payload.Offset];
        }

        /// <summary>One member entry of a hand-assembled schema: name, no previous names, type id, and the
        /// monomorphic flag. Used by the root schema here and by the user-generic entries in the test.</summary>
        static void WriteMember(IBufferWriter<byte> schema, StringInternTable intern, string name, int typeId, bool monomorphic)
        {
            SchemaSection.WriteInternedString(schema, name, intern);
            SchemaSection.WriteLeb128(schema, 0);
            SchemaSection.WriteLeb128(schema, (uint)typeId);
            schema.GetSpan(1)[0] = monomorphic ? (byte)1 : (byte)0;
            schema.Advance(1);
        }

        /// <summary>Extra schema entries a hand-assembled payload's type tags refer to.</summary>
        delegate void ExtraSlotSchemas(IBufferWriter<byte> schema, StringInternTable intern, uint firstTypeId);
    }
}
