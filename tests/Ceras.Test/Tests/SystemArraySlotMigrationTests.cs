// Cross-version tests for a member whose declared type is the abstract `System.Array` base class.
//
// The bytes come from the *separate historical writer* (fixtures/Ceras.HistoricalWriter), which holds the
// v1 CLR model in its own compilation and context (`HistoricalV1ArraySlot`) and which this assembly does
// not reference, so the v1 types are absent at load time. Same-assembly round tripping cannot stand in
// for that: it never enters the interpreter, which is the only thing under test here.
//
// Why this slot needed its own fixtures: `System.Array` as a *declared* type is the well-known id 16,
// which is also the `Array` tag inside a `Type` value and the `Array` definition id of a
// `GenericInstantiation` entry, and it is a third, different identity from a member declared as a concrete
// `T[]` (a manifest entry written by `ArrayFormatter` with no reference marker in front of it). The
// interpreter used to answer "an id below the manifest base I do not recognize" for it, which consumes
// nothing: the slot came back null with its marker, its `Type` value, its element count and its elements
// still in the stream, and the trailing sentinel — the witness below — came out as a wrong number (or the
// load was rejected outright by the exact-consumption check).
//
// Every crate ends in a fixed-width `uint` sentinel written *after* the slot (member order puts
// "System.Array" before "System.UInt32"), so a mis-decoded slot shows up as a silently wrong number rather
// than as a load that happens to succeed. The same-layout cases at the end are the matching-layout control
// for the same shapes, written by *this* build, so both readers are pinned against one set of bytes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Ceras.Helpers;

namespace Ceras.Test.HistoricalArraySlotFixtures
{
    // ── The current model ──────────────────────────────────────────────────────────────────────────
    // Every type is renamed relative to v1 (and reached through [PreviousTypeName]). The rename is what
    // makes the layout fingerprint differ, so the load has to take the migration interpreter rather than
    // the generated fast path; the removals are what make the two layouts genuinely differ as well.

    [CerasContext("HistoricalArraySlotCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArraySlot.ArrayCrate")]
    public class Crate
    {
        public Array Slot;
        public uint Marker;
    }

    [CerasContext("HistoricalArraySlotCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArraySlot.ArrayPair")]
    public class Pair
    {
        public Array First;
        public Array Second;
        public uint Marker;
    }

    /// <summary>v1's <c>ArrayEmptyCrate</c>: the current model has no slot, so its bytes are consumed.</summary>
    [CerasContext("HistoricalArraySlotCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArraySlot.ArrayEmptyCrate")]
    public class EmptyCrate
    {
        public uint Marker;
    }

    // ── Same-layout controls, in the same context so they cannot shift the fixture's ids ────────────
    // A declared System.Array is not discoverable by walking member types, but nothing has to be declared
    // for it: the concrete array type is recorded in the *payload* as a Type value, not in the manifest.
    // (The context is named on the type, like every other type here.)

    /// <summary>A declared <c>System.Array</c> element of a declared collection, and a jagged array whose
    /// element type is itself <c>System.Array</c>: two positions the same declared-type id reaches that
    /// are not a member.</summary>
    [CerasContext("HistoricalArraySlotCurrent")]
    [CerasSerializable]
    public class Nesting
    {
        public List<Array> Items;
        public Array[] Rows;
        public uint Marker;
    }

    public class SystemArraySlotMigrationTests : TestBase
    {
        const string Ctx = "HistoricalArraySlotCurrent";
        const uint Sentinel = 0x5A5A5A5Au;

        /// <summary>Every fixture this file reads, so each one is pinned by a checked-in file.</summary>
        static readonly string[] AllFixtures =
        {
            "ArraySlotInts.bin", "ArraySlotJagged.bin", "ArraySlotStrings.bin", "ArraySlotEmpty.bin",
            "ArraySlotNull.bin", "ArraySlotShared.bin", "ArraySlotRemoved.bin",
        };

        static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "HistoricalV1");

        static byte[] Fixture(string name)
        {
            string path = Path.Combine(FixtureDir, name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'. " +
                "Run fixtures/Ceras.HistoricalWriter to regenerate it (see its README.md).");
            return File.ReadAllBytes(path);
        }

        /// <summary>The migration reader, and the only one the fixture tests use.</summary>
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
                Assert.Contains("Ceras.HistoricalWriter.V1ArraySlot", dump);
                Assert.DoesNotContain("Ceras.Test.HistoricalArraySlotFixtures", dump);
            }
        }

        [Fact]
        public void The_Fixtures_Really_Do_Not_Match_The_Current_Layout()
        {
            // Otherwise every test below would silently degrade into same-version round trips, which never
            // enter the interpreter — and the interpreter is the only thing being fixed here.
            foreach (var (name, load) in new (string, Action<CerasRobustSerializer, byte[]>)[]
                     { ("ArraySlotInts.bin", (s, b) => s.Load<Crate>(b, 1)), ("ArraySlotRemoved.bin", (s, b) => s.Load<EmptyCrate>(b, 1)) })
            {
                var reader = Reader();
                load(reader, Fixture(name));                    // seals, so the fingerprint is final
                Assert.NotEqual(reader.Config.SchemaRegistry.ComputeFingerprint(),
                    BitConverter.ToUInt64(Fixture(name), RobustHeader.FingerprintOffset));
            }
        }

        [Fact]
        public void The_Slot_Is_Recorded_As_The_Declared_Type_System_Array_And_Not_As_A_Concrete_Array()
        {
            // The distinction the whole fix rests on, asserted on the bytes rather than described in a
            // comment: the member's declared type is the *well-known* id 16, and a member declared as a
            // concrete `int[]` would instead be a manifest `GenericInstantiation` entry. 16 is also the
            // `Array` tag inside the `Type` value that follows the marker, so the same number appears twice
            // in one member, meaning two different things.
            var dump = CerasRobustSerializer.DumpSchema(Fixture("ArraySlotInts.bin"));
            Assert.Contains("[System.Array]", dump);
            Assert.DoesNotContain("System.Int32[]", dump);

            // …and the payload is: the root's own marker, then the slot's RefNewValue marker, the rank-1
            // `Type` value (Array, Int32), the biased element count, the elements, then the sentinel.
            var payload = Payload(Fixture("ArraySlotInts.bin"));
            int offset = 0;
            Assert.Equal(WellKnownTypes.UserTypeBase, (int)SchemaSection.ReadLeb128(payload, ref offset));   // the root id
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));                     // the root's marker
            Assert.Equal(Protocol.RefNewValue, ReadMarker(payload, ref offset));                             // the slot: not declared as its own type
            Assert.Equal(WellKnownTypes.Array, (int)SchemaSection.ReadLeb128(payload, ref offset));          // the concrete type: an array
            Assert.Equal(WellKnownTypes.Int32, (int)SchemaSection.ReadLeb128(payload, ref offset));          // of what
            Assert.Equal(3, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));  // three elements
            for (int i = 1; i <= 3; i++)
                Assert.Equal(i * 10, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(unchecked((int)Sentinel), SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(payload.Length, offset);
        }

        /// <summary>A reference marker: a biased varint, exactly as <c>SerializerBinary.ReadUInt32Bias</c> reads it.</summary>
        static int ReadMarker(byte[] payload, ref int offset)
            => SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.RefBias);

        /// <summary>The data section of a save: the root type id and the root's payload, nothing else.</summary>
        static byte[] Payload(byte[] bytes)
        {
            RobustHeader.Read(bytes, out _, out _, out _, out long dataStart, out long stringTable, out _);
            return bytes.Skip((int)dataStart).Take((int)(stringTable - dataStart)).ToArray();
        }

        // ── A retained slot: the read path ──────────────────────────────────────────────────────────

        static object[] Row(string fixture, Action<Crate> check) => new object[] { fixture, check };

        public static IEnumerable<object[]> RetainedSlots()
        {
            // The ordinary case: a marker, a rank-1 type value, a count, the elements.
            yield return Row("ArraySlotInts.bin", c => Assert.Equal(new[] { 10, 20, 30 }, Assert.IsType<int[]>(c.Slot)));

            // A jagged array: a second `Array` tag at a level of its own, behind a second marker, so the
            // rows have to be rebuilt element by element. The empty row and the differing lengths are what
            // a flatten-and-rebuild reader cannot fake.
            yield return Row("ArraySlotJagged.bin", c =>
            {
                var rows = Assert.IsType<int[][]>(c.Slot);
                Assert.Equal(3, rows.Length);
                Assert.Equal(new[] { 1, 2 }, rows[0]);
                Assert.Empty(rows[1]);
                Assert.Equal(new[] { 3 }, rows[2]);
            });

            // A non-primitive element: the element identity is in the type value, not in the slot, and the
            // elements are payload string-table indices.
            yield return Row("ArraySlotStrings.bin", c => Assert.Equal(new[] { "left", "right" }, Assert.IsType<string[]>(c.Slot)));

            // An empty array is not a null one: a marker, the type value, and a count of zero.
            yield return Row("ArraySlotEmpty.bin", c => Assert.Empty(Assert.IsType<int[]>(c.Slot)));

            // A null array is the reference marker alone — no type value, no count, no elements.
            yield return Row("ArraySlotNull.bin", c => Assert.Null(c.Slot));
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

        [Fact]
        public void The_Same_Array_In_Two_Slots_Comes_Back_As_One_Instance()
        {
            // The reference wrapper registers the array in the writer's object cache, so the second slot is
            // a back-reference to the first. That is the difference from a member declared as a concrete
            // `T[]`, which `ArrayFormatter` writes by value and never registers: a reader that reserved no
            // cache slot here would resolve the back-reference to whatever happened to be at index 0.
            var pair = Reader().Load<Pair>(Fixture("ArraySlotShared.bin"), 1);

            Assert.Equal(new[] { 5, 6 }, Assert.IsType<int[]>(pair.First));
            Assert.Same(pair.First, pair.Second);
            Assert.Equal(Sentinel, pair.Marker);
        }

        // ── A removed slot: the skip path ───────────────────────────────────────────────────────────

        [Fact]
        public void A_Removed_Slot_Leaves_The_Fields_After_It_Aligned()
        {
            // The marker, the type value, the count and all three elements have to be consumed for the
            // fixed-width sentinel to hold the written number. The skipped shape is *not* the one the
            // concrete-array fixtures skip: a declared `T[]` member has no marker in front of its count,
            // while a declared `System.Array` member has a marker and a type value, so the two skips must
            // not be collapsed into one.
            var crate = Reader().Load<EmptyCrate>(Fixture("ArraySlotRemoved.bin"), 1);
            Assert.Equal(Sentinel, crate.Marker);
        }

        // ── The matching-layout control: one set of bytes, both readers ─────────────────────────────

        public static IEnumerable<object[]> MatchingLayoutCases()
        {
            // (name, slot, check) — the shapes the two readers could most plausibly read differently, written
            // by *this* build so the fingerprint matches and the generated fast path is the reader. A
            // cross-version fixture cannot serve as this control: its fingerprint never matches.
            yield return new object[] { "ints", new[] { 1, 2, 3 }, (Action<object>)(o => Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(o))) };
            yield return new object[] { "strings", new[] { "a", "b" }, (Action<object>)(o => Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(o))) };
            yield return new object[] { "empty", new int[0], (Action<object>)(o => Assert.Empty(Assert.IsType<int[]>(o))) };
            yield return new object[] { "null", null, (Action<object>)(o => Assert.Null(o)) };
            yield return new object[] { "jagged", new[] { new[] { 1, 2 }, new int[0], new[] { 3 } },
                (Action<object>)(o => { var a = Assert.IsType<int[][]>(o); Assert.Equal(new[] { 1, 2 }, a[0]); Assert.Empty(a[1]); Assert.Equal(new[] { 3 }, a[2]); }) };
        }

        /// <summary>The label a matching-layout case's slot must carry, so the table above cannot drift.
        /// A null slot is a case of its own, and it is a plain null reference: the test project builds with
        /// nullable annotations off, so the parameter carries no annotation (a `?` here would be CS8632).</summary>
        static string LabelFor(object slot) => slot switch
        {
            null => "null",
            int[] a when a.Length == 0 => "empty",
            int[] => "ints",
            string[] => "strings",
            int[][] => "jagged",
            _ => slot.GetType().Name,      // an unlisted shape fails the assertion above, which is the point
        };

        [Theory]
        [MemberData(nameof(MatchingLayoutCases))]
        public void A_Matching_Layout_Reads_The_Same_Values_Through_Both_Readers(
            string name, object slot, Action<object> check)
        {
            var writer = Current();
            var bytes = writer.Save(new Crate { Slot = slot as Array, Marker = Sentinel }, 1);

            // The label is asserted against the value rather than merely carried along, so a row whose label
            // and slot disagree is caught here, and a failure below names the shape from either side.
            Assert.Equal(name, LabelFor(slot));

            foreach (var crate in new[] { writer.Load<Crate>(bytes, 1), writer.Load<Crate>(SlowPath(bytes), 1) })
            {
                Assert.Equal(Sentinel, crate.Marker);
                check(crate.Slot);
            }
        }

        [Fact]
        public void A_Declared_Array_Element_And_A_Jagged_Row_Also_Go_Through_Both_Readers()
        {
            // The same declared-type id reached as a collection *element* (where the element formatter is
            // the reference wrapper again) and as a jagged row. Both positions used to be read as a value of
            // no bytes, so both had to be wrong in the same way the member was.
            var writer = Current();
            var bytes = writer.Save(new Nesting
            {
                Items = new List<Array> { new[] { 1, 2 }, null, new int[0] },
                Rows = new Array[] { new[] { 3, 4 }, new[] { new[] { 5 } } },
                Marker = Sentinel,
            }, 1);

            foreach (var nesting in new[] { writer.Load<Nesting>(bytes, 1), writer.Load<Nesting>(SlowPath(bytes), 1) })
            {
                Assert.Equal(3, nesting.Items.Count);
                Assert.Equal(new[] { 1, 2 }, Assert.IsType<int[]>(nesting.Items[0]));
                Assert.Null(nesting.Items[1]);
                Assert.Empty(Assert.IsType<int[]>(nesting.Items[2]));

                Assert.Equal(2, nesting.Rows.Length);
                Assert.Equal(new[] { 3, 4 }, Assert.IsType<int[]>(nesting.Rows[0]));
                Assert.Equal(new[] { new[] { 5 } }, Assert.IsType<int[][]>(nesting.Rows[1]));

                Assert.Equal(Sentinel, nesting.Marker);
            }
        }

        [Fact]
        public void A_System_Array_Root_Goes_Through_Both_Readers()
        {
            // A root declared as `System.Array` carries the same declared-type id as a member that does, and
            // the writer reaches it through the same `ReferenceFormatter<Array>` — so it has to be read the
            // same way, not through the manifest-entry path a concrete `T[]` root takes.
            var writer = new CerasRobustSerializer(c =>
            {
                c.UseGeneratedFormatters(Ctx);
                c.SetUnknownBuildTimestamp();
                c.DeclareRoot<Array>();
            });

            var bytes = writer.Save<Array>(new[] { 1, 2, 3 }, 1);

            foreach (var loaded in new[] { writer.Load<Array>(bytes, 1), writer.Load<Array>(SlowPath(bytes), 1) })
                Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(loaded));
        }

        // ── Reuse: an existing instance is filled in place, like every other reference member ─────────

        [Fact]
        public void An_Existing_Array_In_The_Slot_Is_Filled_In_Place_On_Both_Readers()
        {
            // `Load(ref value, …)` seeds each reference member from the instance already there, and the
            // generated reference reader reuses it when the runtime type matches. The interpreter's array
            // reader has to make the same decision, and a slot that reserved the writer's object-cache entry
            // must still do it: reuse is a construction contract, not a shortcut that skips the payload.
            var writer = Current();
            var bytes = writer.Save(new Crate { Slot = new[] { 1, 2, 3 }, Marker = Sentinel }, 1);

            var fast = new Crate { Slot = new int[3] };
            var slow = new Crate { Slot = new int[3] };
            var fastRef = fast;
            var slowRef = slow;
            writer.Load(ref fastRef, bytes, 1);
            writer.Load(ref slowRef, SlowPath(bytes), 1);

            Assert.Same(fast.Slot, fastRef.Slot);
            Assert.Same(slow.Slot, slowRef.Slot);
            Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(slowRef.Slot));
            Assert.Equal(Sentinel, slowRef.Marker);
        }

        // ── What is still refused ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ARankGreaterThanOneArray_Is_Still_Refused_By_Save_Through_This_Slot()
        {
            // The fix is read-side only. A rank > 1 array cannot travel through a slot that records nothing
            // but its element type, and the write is still where that is decided — nothing here may make the
            // interpreter look like a place where the rank could be recovered, because it cannot be: the
            // type value has no rank in it.
            var thrown = Record.Exception(() => Current().Save(
                new Crate { Slot = new int[,] { { 1, 2 }, { 3, 4 } }, Marker = Sentinel }, 1));

            var refusal = Assert.IsType<CerasSchemaException>(Innermost(thrown!));
            Assert.Contains(TypeIdentity.GetCanonicalName(typeof(int[,])), refusal.Message);
            Assert.Contains("rank 2", refusal.Message);
            Assert.Contains(TypeIdentity.GetCanonicalName(typeof(Array)), refusal.Message);
            Assert.Contains(MultidimensionalArrayGuard.Advice, refusal.Message);
        }

        static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
