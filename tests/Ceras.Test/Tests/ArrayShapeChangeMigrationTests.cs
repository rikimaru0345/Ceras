// Cross-version tests for a member whose *declared type* changed between a concrete array and a
// polymorphic slot — `int[] Slot` becoming `Array`/`object`/`List<>`/`SomeClass`, or the reverse. The
// argument-level version of the same policy, one level in, is
// `ArrayShapeNestedArgumentMigrationTests.cs`.
//
// The bytes come from the *separate historical writer* (fixtures/Ceras.HistoricalWriter), which holds the v1
// CLR models in their own compilations and contexts and which this assembly does not reference, so the v1
// types are absent at load time. Same-assembly round tripping cannot stand in for that: it never enters the
// interpreter, which is the only thing that could ever follow a changed declaration.
//
// Why these need their own fixtures: the two declarations do not start at the same byte. A member declared
// as a concrete `T[]` is written by ArrayFormatter as a biased element count with no reference marker and no
// object-cache registration, while a member declared `System.Array` or `object` goes through
// ReferenceFormatter and is preceded by a marker. So there is no offset at which the file's member can be
// read as the current one, and the interpreter has to pick a side: it picked the *current* member's grammar,
// so the file's count was read as a reference marker, every member after the slot decoded from the wrong
// offset, and the load only failed at the very end in the exact-consumption check, reporting unread bytes
// instead of the declaration change that caused them. (The generated fast path is never reached for such a
// file — a changed member type changes the layout fingerprint — so there is no second reader that could
// have agreed.)
//
// The refusal is therefore made at the point the old-to-current plans are built, before the root type id is
// even read. These tests pin that placement (no root callback runs, and a corrupted data section produces
// the identical message), both directions of the change, and — just as importantly — the shapes that must
// keep working, and that the wire format of those shapes has not moved.
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
using static Ceras.Test.HistoricalFixtureFiles;

namespace Ceras.Test.HistoricalArrayShapeFixtures
{
    // ── The current model ──────────────────────────────────────────────────────────────────────────
    // Every type is renamed relative to v1 and reached through [PreviousTypeName], which is also what makes
    // the layout fingerprint differ, so a load has to take the migration interpreter. Each v1 file gets its
    // own current type, one to one: an id resolves to exactly one current type, so two current types cannot
    // claim one old name. They all live in one context here, which does not disturb that — see
    // "Why one context per rejected type" on the refusal tests.

    /// <summary>v1's <c>ConcreteArrayCrate</c>: the old member was a concrete <c>int[]</c>, this one is a
    /// polymorphic <c>object</c> slot.</summary>
    [CerasContext("HistoricalArrayShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArrayShape.ToObject.ConcreteArrayCrate")]
    public class PolymorphicCrate
    {
        public object Slot;
        public uint Marker;

        /// <summary>Counts the loads that actually reached the payload. The refusal is asserted to happen
        /// before this can run, which is the observable half of "before the payload is interpreted".</summary>
        public static int BeforeCallbacks;

        [OnBeforeDeserialize]
        public void NoteBefore() => BeforeCallbacks++;
    }

    /// <summary>v1's <c>ArraySlotCrate</c>: the old member was the abstract <c>System.Array</c>, this one is
    /// a concrete <c>int[]</c>.</summary>
    [CerasContext("HistoricalArrayShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArrayShape.FromArray.ArraySlotCrate")]
    public class ConcreteCrate
    {
        public int[] Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>ObjectSlotCrate</c>: the old member was a declared <c>object</c>, this one is a
    /// concrete <c>int[]</c>. A separate type from <see cref="ConcreteCrate"/> so the other polymorphic
    /// identity is pinned by its own file rather than by inspection.</summary>
    [CerasContext("HistoricalArrayShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArrayShape.FromObject.ObjectSlotCrate")]
    public class FromObjectCrate
    {
        public int[] Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>SameArrayCrate</c>: the array-ness is unchanged, so this one has to load. The
    /// control for "the refusal is about the shape, not about the file".</summary>
    [CerasContext("HistoricalArrayShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArrayShapeSame.SameArrayCrate")]
    public class KeptCrate
    {
        public int[] Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>RemovedArrayCrate</c>: the current model has no slot, so its bytes are consumed.</summary>
    [CerasContext("HistoricalArrayShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArrayShapeSame.RemovedArrayCrate")]
    public class RemovedCrate
    {
        public uint Marker;
    }

    /// <summary>v1's <c>JaggedSlotCrate</c>: still a polymorphic <c>System.Array</c> member, so the rows
    /// stay levels of one polymorphic value rather than becoming declared array members.</summary>
    [CerasContext("HistoricalArrayShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArrayShapeSame.JaggedSlotCrate")]
    public class JaggedCrate
    {
        public Array Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>ObjectToListCrate</c>: the old member was a declared <c>object</c> and this one is
    /// a declared <c>List&lt;int&gt;</c>. No array is involved on either side, so this is <em>not</em> the
    /// refused shape — it is here to pin where this policy stops (see the test that uses it).</summary>
    [CerasContext("HistoricalArrayShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1ArrayShape.ToList.ObjectToListCrate")]
    public class FromListCrate
    {
        public List<int> Slot;
        public uint Marker;
    }

    public class ArrayShapeChangeMigrationTests : TestBase
    {
        const string Ctx = "HistoricalArrayShapeCurrent";
        const uint Sentinel = 0xC3C3C3C3u;

        /// <summary>Every fixture this file reads, so each one is pinned by a checked-in file.</summary>
        static readonly string[] AllFixtures =
        {
            "ArrayShapeToObject.bin", "ArrayShapeFromArray.bin", "ArrayShapeFromObject.bin",
            "ArrayShapeKept.bin", "ArrayShapeKeptNull.bin", "ArrayShapeRemoved.bin",
            "ArrayShapeJaggedSlot.bin", "ArrayShapeToList.bin",
        };

        /// <summary>The migration reader, and the only one the cross-version tests use.</summary>
        static CerasRobustSerializer Reader() => HistoricalFixtureFiles.MigrationReader(Ctx);

        /// <summary>A serializer over the <em>current</em> model, for the matching-layout controls.</summary>
        static CerasRobustSerializer Current() => HistoricalFixtureFiles.CurrentWriter(Ctx);


        /// <summary>The data section of a save: the root type id and the root's payload, nothing else.</summary>


        // ── The files are what they claim to be ─────────────────────────────────────────────────────

        /// <summary>
        /// Every fixture this file reads is a real old-build file: an intact checksum, a schema section that
        /// holds the <em>old</em> model's names (and not this assembly's), and a layout fingerprint that
        /// cannot match — so a load really has to take the interpreter, and a refusal really is about the
        /// schema rather than about damaged bytes.
        /// </summary>
        [Fact]
        public void Every_Fixture_Is_A_Real_Cross_Version_File()
            => HistoricalFixtureFiles.AssertEveryFixture(AllFixtures, "Ceras.HistoricalWriter.V1ArrayShape", "Ceras.Test.HistoricalArrayShapeFixtures", Ctx);


        [Fact]
        public void A_Declared_Array_Member_Is_A_Manifest_Entry_Not_A_Well_Known_Array_Id()
        {
            // The concrete array is described *structurally*, as an entry with its own id, while the two
            // polymorphic declarations are the well-known ids — which the schema section has no room for.
            var dump = CerasRobustSerializer.DumpSchema(Fixture("ArrayShapeToObject.bin"));
            Assert.Contains("GENERIC", dump);
            Assert.Contains("System.Int32[]", dump);
            Assert.Contains("def=System.Array", dump);
        }

        /// <summary>
        /// The payload of the concrete-array crate, byte by byte: the root's id and its own marker, then the
        /// member as a <b>bare biased count</b> and the elements, then the sentinel. There is no second
        /// marker — which is what the current model's <c>object Slot</c> would look for.
        /// </summary>
        [Fact]
        public void A_Declared_Array_Member_Really_Has_No_Reference_Marker_In_Front_Of_Its_Count()
        {
            var payload = Payload(Fixture("ArrayShapeToObject.bin"));
            int offset = 0;
            Assert.Equal(WellKnownTypes.UserTypeBase, (int)SchemaSection.ReadLeb128(payload, ref offset));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));   // the root's own
            Assert.Equal(3, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            for (int i = 4; i <= 6; i++)
                Assert.Equal(i, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(unchecked((int)Sentinel), SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(payload.Length, offset);
        }

        /// <summary>The mirror of the above for a declared <c>System.Array</c> member: a marker, then the
        /// concrete type as a <c>Type</c> value, then the count. The current model's <c>int[] Slot</c> has
        /// nowhere to put either of those.</summary>
        [Fact]
        public void A_Declared_System_Array_Member_Really_Does_Start_With_A_Reference_Marker()
        {
            var payload = Payload(Fixture("ArrayShapeFromArray.bin"));
            int offset = 0;
            Assert.Equal(WellKnownTypes.UserTypeBase, (int)SchemaSection.ReadLeb128(payload, ref offset));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));   // the root's own
            Assert.Equal(Protocol.RefNewValue, ReadMarker(payload, ref offset));           // the slot
            Assert.Equal(WellKnownTypes.Array, (int)SchemaSection.ReadLeb128(payload, ref offset));
            Assert.Equal(WellKnownTypes.Int32, (int)SchemaSection.ReadLeb128(payload, ref offset));
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            Assert.Equal(7, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(8, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(unchecked((int)Sentinel), SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(payload.Length, offset);
        }

        /// <summary>A null concrete array is the biased count <c>-1</c> — the single byte <c>00</c> — and
        /// not a reference marker. Read as a polymorphic slot that same byte is <see cref="Protocol.RefInlineType"/>,
        /// so the two grammars differ even for the value that carries no data at all, and the null fixture is
        /// the one that would expose a rule keyed on the value rather than on the declaration.</summary>
        [Fact]
        public void A_Null_Array_Is_The_Biased_Minus_One_Not_A_Reference_Marker()
        {
            var payload = Payload(Fixture("ArrayShapeKeptNull.bin"));
            int offset = 0;
            SchemaSection.ReadLeb128(payload, ref offset);
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));
            Assert.Equal(-1, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            Assert.Equal(unchecked((int)Sentinel), SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(payload.Length, offset);

            // The same byte read the other way round: the two encodings really are interchangeable-looking,
            // so a reader that had to guess would guess wrong here in a way no later field would reveal.
            int slotOffset = 0;
            SchemaSection.ReadLeb128(payload, ref slotOffset);
            ReadMarker(payload, ref slotOffset);                        // the root's own marker
            Assert.Equal(Protocol.RefInlineType, ReadMarker(payload, ref slotOffset));
        }

        // ── The refusal ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One row per direction, so neither is pinned by symmetry with the other: the fixture, the member,
        /// how to load it as the current root, the declared type the <em>file</em> records for the member and
        /// the declared type <em>this build</em> has for it.
        /// <para>
        /// <b>Why one context per rejected direction</b> (see <c>fixtures/Ceras.HistoricalWriter</c>). The
        /// migration plans are built for every type the file's schema section describes, not only for the
        /// ones the payload reaches, and a rejected change aborts the whole load — the pre-existing
        /// behaviour of the general "no defined conversion" refusal, and the right one, since a file whose
        /// schema cannot be migrated should be refused as a whole rather than depending on which of its types
        /// the payload happened to touch. It also means a manifest holding two rejected types could only
        /// ever report whichever of them the iteration reached first, so each direction has a context and a
        /// file of its own. The current model below needs no such split: one id resolves to one current type,
        /// and each v1 type has exactly one.
        /// </para>
        /// </summary>
        public static IEnumerable<object[]> RefusedChanges()
        {
            yield return new object[] { "ArrayShapeToObject.bin", "Slot",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<PolymorphicCrate>(b, 1) ),
                "System.Int32[]", "System.Object" };
            yield return new object[] { "ArrayShapeFromArray.bin", "Slot",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<ConcreteCrate>(b, 1) ),
                "System.Array", "System.Int32[]" };
            yield return new object[] { "ArrayShapeFromObject.bin", "Slot",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<FromObjectCrate>(b, 1) ),
                "System.Object", "System.Int32[]" };
        }

        [Theory]
        [MemberData(nameof(RefusedChanges))]
        public void A_Member_Whose_Declared_Type_Changed_Shape_Is_Refused(string fixture, string member,
            Action<CerasRobustSerializer, byte[]> load, string oldMemberType, string newMemberType)
        {
            var error = Record.Exception(() => load(Reader(), Fixture(fixture)));

            // The exact type, because a schema disagreement the caller can act on and bytes that are not a
            // Ceras save are different contracts in this codebase. A reader that had interpreted the payload
            // first would have failed somewhere else, and differently.
            var refusal = Assert.IsType<CerasSchemaException>(error);

            // Both member types are named, so the message identifies the change rather than describing a
            // symptom of it, and the member itself.
            Assert.Contains($"'{member}'", refusal.Message);
            Assert.Contains(oldMemberType, refusal.Message);
            Assert.Contains(newMemberType, refusal.Message);
            Assert.Contains("reference marker", refusal.Message);

            // The remedy is stated as something the caller can do: keep the array on the wire under its own
            // stable name, and build the new value explicitly in a migration step.
            Assert.Contains("[PreviousName]", refusal.Message);
            Assert.Contains("[OnAfterDeserialize]", refusal.Message);

            // …and it does not pretend the generator could have found this. Only the save file says how the
            // old member was written, so the check is a load-time one.
            Assert.Contains("load-time check", refusal.Message);
        }

        /// <summary>The declaring type is named by the name <em>the file</em> used, which is what makes the
        /// message traceable back to the build that wrote the save rather than to this one.</summary>
        [Fact]
        public void The_Refusal_Names_The_Declaring_Type_The_File_Recorded()
        {
            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<PolymorphicCrate>(Fixture("ArrayShapeToObject.bin"), 1)));

            Assert.StartsWith("Cannot migrate the field 'Slot' of 'Ceras.HistoricalWriter.V1ArrayShape.ToObject.", refusal.Message);
        }

        /// <summary>
        /// The refusal happens while the old-to-current schema is validated, so no root payload is interpreted
        /// at all. The witness is an <c>[OnBeforeDeserialize]</c> on the current root: it runs the moment the
        /// root's payload is read, so one run would mean the reader had already started.
        /// </summary>
        [Fact]
        public void The_Refusal_Happens_Before_The_Root_Payload_Is_Interpreted()
        {
            PolymorphicCrate.BeforeCallbacks = 0;

            Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<PolymorphicCrate>(Fixture("ArrayShapeToObject.bin"), 1)));

            Assert.Equal(0, PolymorphicCrate.BeforeCallbacks);
        }

        /// <summary>
        /// The same witness, without needing a callback: overwrite the whole data section of a refused file
        /// with bytes that cannot be a payload and re-checksum it. A reader that interpreted the payload first
        /// would have something to say about those bytes; the refusal is a property of the two declarations,
        /// so the message has to come out identical.
        /// </summary>
        [Fact]
        public void The_Refusal_Does_Not_Depend_On_The_Payload_At_All()
        {
            var original = Fixture("ArrayShapeToObject.bin");
            var expected = Assert.IsType<CerasSchemaException>(
                Record.Exception(() => Reader().Load<PolymorphicCrate>(original, 1))).Message;

            RobustHeader.Read(original, out _, out _, out _, out long dataStart, out long stringTable, out _);
            var garbled = (byte[])original.Clone();
            for (long i = dataStart; i < stringTable; i++)
                garbled[i] = 0xA5;
            FixRobustChecksum(garbled);

            Assert.Equal(expected,
                Assert.IsType<CerasSchemaException>(
                    Record.Exception(() => Reader().Load<PolymorphicCrate>(garbled, 1))).Message);
        }

        /// <summary>A refused load leaves the serializer usable: the same reader that refused the changed file
        /// loads an allowed one afterwards, so the refusal is not a poisoned state.</summary>
        [Fact]
        public void A_Refused_Load_Leaves_The_Serializer_Usable()
        {
            var reader = Reader();
            Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                reader.Load<PolymorphicCrate>(Fixture("ArrayShapeToObject.bin"), 1)));

            var kept = reader.Load<KeptCrate>(Fixture("ArrayShapeKept.bin"), 1);
            Assert.Equal(new[] { 1, 2, 3 }, kept.Slot);
            Assert.Equal(Sentinel, kept.Marker);
        }

        // ── What must keep working ─────────────────────────────────────────────────────────────────

        [Fact]
        public void A_Member_That_Is_An_Array_On_Both_Sides_Still_Loads()
        {
            var crate = Reader().Load<KeptCrate>(Fixture("ArrayShapeKept.bin"), 1);

            // The sentinel first: it is the witness that the slot consumed exactly its own bytes, i.e. that
            // the member was read by the array grammar and not by the polymorphic one.
            Assert.Equal(Sentinel, crate.Marker);
            Assert.Equal(new[] { 1, 2, 3 }, crate.Slot);
        }

        [Fact]
        public void A_Null_Array_Member_Still_Loads()
        {
            var crate = Reader().Load<KeptCrate>(Fixture("ArrayShapeKeptNull.bin"), 1);

            Assert.Null(crate.Slot);
            Assert.Equal(Sentinel, crate.Marker);
        }

        [Fact]
        public void A_Removed_Array_Member_Still_Leaves_The_Following_Field_Aligned()
        {
            // The skip path has no current declaration that could disagree with the file's, so this is not the
            // refused shape at all: the biased count and the three elements have to be consumed for the
            // fixed-width sentinel to hold the written number.
            var crate = Reader().Load<RemovedCrate>(Fixture("ArrayShapeRemoved.bin"), 1);
            Assert.Equal(Sentinel, crate.Marker);
        }

        [Fact]
        public void A_Jagged_Array_In_A_Polymorphic_Slot_Still_Loads()
        {
            // The rows are levels of one polymorphic value, not declared array members, so nothing about the
            // member's declared type changed and nothing here may be refused. The empty row is what a
            // flatten-and-rebuild reader cannot fake.
            var crate = Reader().Load<JaggedCrate>(Fixture("ArrayShapeJaggedSlot.bin"), 1);

            Assert.Equal(Sentinel, crate.Marker);
            var rows = Assert.IsType<int[][]>(crate.Slot);
            Assert.Equal(3, rows.Length);
            Assert.Equal(new[] { 1, 2 }, rows[0]);
            Assert.Empty(rows[1]);
            Assert.Equal(new[] { 3 }, rows[2]);
        }

        // ── The matching-layout controls: this build's own bytes, and the wire format they keep ─────

        /// <summary>
        /// The control for "the interpreter decodes exactly what the writer wrote", on the shapes that are
        /// allowed — each written by <em>this</em> build so the fingerprint matches and the generated fast path
        /// is the reader, then read again through the interpreter with the fingerprint flipped. A cross-version
        /// fixture cannot serve as this control, because its fingerprint never matches.
        /// <para>
        /// The wire format is asserted here too, because "no format change for an allowed shape" is only
        /// meaningful against the bytes: the schema section must still record the member's declared-type
        /// identity, and the payload must be exactly what the v1 writer produced for the same shape once the
        /// root type id is left out (the two contexts number their roots differently, and that is the only
        /// thing about the two files that may differ). A marker introduced in front of an allowed concrete
        /// array member, or a schema entry that stopped being a manifest array instantiation, would show up
        /// here.
        /// </para>
        /// </summary>
        public static IEnumerable<object[]> AllowedShapes()
        {
            // (fixture, the current value that produces the same shape, the member entry it must record, how
            //  to read the slot back, and what must be true of it)
            //
            // The saves are spelled as delegates rather than handed a value, because a value typed `object`
            // would be written as an `object` root — marker and all — rather than as the model's own type.
            yield return new object[] { "ArrayShapeKept.bin",
                (Func<CerasRobustSerializer, byte[]>)( w => w.Save(new KeptCrate { Slot = new[] { 1, 2, 3 }, Marker = Sentinel }, 1) ),
                "[System.Int32[]]  Mono  Slot",
                (Func<byte[], object>)( b => ((KeptCrate)Current().Load<KeptCrate>(b, 1)).Slot ),
                (Func<object, bool>)( c => { var a = (int[])c; return a.Length == 3 && a[0] == 1 && a[2] == 3; }) };
            yield return new object[] { "ArrayShapeKeptNull.bin",
                (Func<CerasRobustSerializer, byte[]>)( w => w.Save(new KeptCrate { Slot = null, Marker = Sentinel }, 1) ),
                "[System.Int32[]]  Mono  Slot",
                (Func<byte[], object>)( b => ((KeptCrate)Current().Load<KeptCrate>(b, 1)).Slot ),
                (Func<object, bool>)( c => c == null) };
            yield return new object[] { "ArrayShapeJaggedSlot.bin",
                (Func<CerasRobustSerializer, byte[]>)( w => w.Save(new JaggedCrate
                    { Slot = new[] { new[] { 1, 2 }, new int[0], new[] { 3 } }, Marker = Sentinel }, 1) ),
                "[System.Array]  Poly  Slot",
                (Func<byte[], object>)( b => ((JaggedCrate)Current().Load<JaggedCrate>(b, 1)).Slot ),
                (Func<object, bool>)( c => { var r = (int[][])c; return r.Length == 3 && r[0].Length == 2 && r[1].Length == 0 && r[2][0] == 3; }) };
        }

        [Theory]
        [MemberData(nameof(AllowedShapes))]
        public void An_Allowed_Shape_Keeps_Its_Wire_Format_And_Reads_The_Same_Through_Both_Readers(
            string fixture, Func<CerasRobustSerializer, byte[]> save, string memberEntry,
            Func<byte[], object> readSlot, Func<object, bool> check)
        {
            var writer = Current();
            var bytes = save(writer);

            // This file claims this context's own layout, so the generated fast path is its reader.
            Assert.Equal(writer.Config.SchemaRegistry.ComputeFingerprint(),
                BitConverter.ToUInt64(bytes, RobustHeader.FingerprintOffset));
            Assert.Contains(memberEntry, CerasRobustSerializer.DumpSchema(bytes));

            // The same bytes the v1 writer produced for the same shape, after the root type id.
            var oldPayload = Payload(Fixture(fixture));
            var newPayload = Payload(bytes);
            int oldOffset = 0, newOffset = 0;
            SchemaSection.ReadLeb128(oldPayload, ref oldOffset);
            SchemaSection.ReadLeb128(newPayload, ref newOffset);
            Assert.Equal(oldPayload[oldOffset..], newPayload[newOffset..]);

            // And both readers agree on them — the interpreter this time, through the flipped fingerprint.
            var slow = (byte[])bytes.Clone();
            slow[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(slow);

            Assert.True(check(readSlot(bytes)), $"the generated reader did not read the {fixture} shape back as written");
            Assert.True(check(readSlot(slow)), $"the interpreter did not read this build's {fixture} shape as written");
        }

    }
}
