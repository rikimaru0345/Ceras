// Cross-version tests for an **inherited** member that is renamed and widened, and for a fixed-size
// struct whose own member width changed while the struct is a collection element.
//
// The bytes come from the separate historical writer (fixtures/Ceras.HistoricalWriter,
// V1InheritedMemberModel.cs, contexts `HistoricalV1Inherited` and `HistoricalV1InheritedNarrow`), which
// this assembly does not reference. So the v1 CLR types are absent at load time and these are real
// cross-version reads: the file's own schema section is decoded and mapped onto the model below, rather
// than this version's schema read by this version's reader.
//
// Why these two and not the ones the earlier fixture files already carry:
//
//  * An **inherited** rename. v1's `Power` is declared on a base type that the file never instantiates: the
//    only values in the payload are `Soldier`s, so the base's schema entry is reached through the derived
//    type and resolved through a rename with no concrete value to help. The reader's member map has to
//    walk the base chain for this; `InheritedMemberMigrationTests` proves the map does, against a file it
//    assembles by hand, and nothing proved it against a file a writer produced. The same base's own entry
//    has to resolve, which is a second and quite different requirement — see <see cref="Stat"/>.
//  * A **widening inside a fixed-size struct that is a collection element**. `Ammo.Count` goes from `int`
//    to `long`, so the declared byte size of the struct this build writes (12) is not the file's (8) —
//    and a struct element is a bare run of bytes with no marker and no length, so nothing in the stream
//    could recover from advancing by the wrong width. `Coord` in `SaveGame.bin` covers a *removal* in
//    that position, which keeps both widths equal.
//  * **Added field on an instantiated type, preserving sharing.** `Morale` is new and carries a declared
//    default, and `Leader`/`Squad[0]` were one instance: the default has to be the one this build's
//    construction gives, and it has to be the *same* object on both references.
//  * The **refused** direction, in the same nested position: the struct's member narrowed to a `short`,
//    which is not one of the five conversions Ceras performs. Its own context, because the migration plans
//    are built for every type a file's section describes.
//
// The last test also runs the file-only graph builder over the same bytes, so the shape the migration will
// eventually take over `Load`'s slow path is checked against a real writer's file now rather than on the
// day the switch is flipped.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using static Ceras.Test.HistoricalFixtureFiles;

namespace Ceras.Test.HistoricalInheritedFixtures
{
    // ── The current model ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The value the current model declares for the field v1 never wrote. Named so the declaration and
    /// the assertion that reads it back cannot drift apart. A namespace cannot hold a field, and putting
    /// the constant on <see cref="Fighter"/> itself would add a member to a serialized type that exists
    /// only to be read by a test, so it lives here — on a type nothing serializes.
    /// </summary>
    static class CurrentModel
    {
        public const int DeclaredMorale = 100;
    }

    /// <summary>
    /// v1's <c>Unit</c>: a base type this build renames, holding the member that is renamed
    /// (<c>Power</c> to <c>Rating</c>) and widened (<c>int</c> to <c>long</c>) at the same time. v1's
    /// <c>Armour</c> is gone, so the four bytes it wrote have to be skipped.
    /// <para>
    /// A plain class, like v1's, and not <c>abstract</c> — which is a limitation of the format rather than
    /// a choice. A generated manifest registers an <b>abstract or interface</b> type with a placeholder
    /// schema whose <c>previousTypeNames</c> is <c>Array.Empty&lt;string&gt;()</c> and whose
    /// <c>fields</c> is empty (the source generator's <c>AppendMinimalRegistration</c>), so a user-declared
    /// <c>[PreviousTypeName]</c> on such a type is dropped on the way into the manifest. A file that
    /// describes a renamed abstract base then has no name this build can resolve, and
    /// <c>CerasRobustSerializer.BuildIdToType</c> hands the name to the configured <c>ITypeBinder</c>,
    /// whose contract is to <i>throw</i> rather than answer null — so the load is refused with
    /// "Error resolving type with stable name '…': Cannot find type …" instead of leaving the entry
    /// unmapped the way its own comment says a removed type should be. This is a real defect, reported
    /// rather than pinned here: a test that asserted the refusal would make the defect look like a
    /// contract, and the two causes live in production code this change does not touch.
    /// </para>
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Inherited.Unit")]
    [CerasContext("HistoricalInheritedCurrent")]
    [CerasSerializable]
    public class Stat
    {
        [PreviousName("Power")]
        public long Rating;
    }

    /// <summary>
    /// v1's <c>Soldier</c>. <c>Morale</c> is new: the default is a real number rather than zero on
    /// purpose, because "the declared default" and "nothing was assigned" are the same observation for a
    /// field that defaults to zero, and only one of them is a contract.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Inherited.Soldier")]
    [CerasContext("HistoricalInheritedCurrent")]
    [CerasSerializable]
    public class Fighter : Stat
    {
        public string Name;

        /// <summary>Added in the current model; takes the default its own declaration gives it.</summary>
        public int Morale = CurrentModel.DeclaredMorale;

        /// <summary>
        /// Hands the migration report to the probe, so the tests can read what the migration said it read
        /// and what it did not. It runs once per <em>instance</em>, so the report is the same for both
        /// soldiers — which is the point: the claim is about the type's mapping, not about one object.
        /// </summary>
        [OnAfterDeserialize]
        public void After(MigrationContext context) => Probe.Last = context;
    }

    /// <summary>
    /// Where the last <c>[OnAfterDeserialize]</c> left its report. A public field on a non-serialized type
    /// rather than a nullable one: the test project compiles with nullable annotations off, so a
    /// <c>?</c> here would be CS8632. Left null by <see cref="Reset"/> until a callback hands one over,
    /// which is what says "no instance was built" with a <c>!</c> at the point of use.
    /// </summary>
    public static class Probe
    {
        public static MigrationContext Last;
        public static void Reset() => Last = null;
    }

    /// <summary>v1's <c>Ammo</c>: the same struct, with <c>Count</c> widened to <c>long</c>.</summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Inherited.Ammo")]
    [CerasContext("HistoricalInheritedCurrent")]
    [CerasSerializable]
    public struct Ammo
    {
        public long Count;   // v1: int
        public int Kind;
    }

    /// <summary>v1's save root, unchanged apart from the two element types it names.</summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Inherited.Roster")]
    [CerasContext("HistoricalInheritedCurrent")]
    [CerasSerializable]
    public class Roster
    {
        public Fighter Leader;
        public Dictionary<string, Ammo> Reserve;
        public List<Ammo> Loadout;
        public List<Fighter> Squad;
        public uint Marker;
    }

    /// <summary>
    /// v1's <c>Weight</c> on the direction that must be refused: <c>Grams</c> is narrowed to a
    /// <c>short</c>. Ceras performs five widening conversions and this is not one of them, so a save
    /// holding the file has to be rejected by name instead of having the value dropped.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1InheritedNarrow.Weight")]
    [CerasContext("HistoricalInheritedCurrent")]
    [CerasSerializable]
    public struct Weight
    {
        public short Grams;  // v1: int
        public int Kind;
    }

    /// <summary>
    /// v1's crate, whose only member is a collection of the refused struct. The callback counter is what
    /// proves the refusal happens before a payload byte is interpreted.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1InheritedNarrow.Crate")]
    [CerasContext("HistoricalInheritedCurrent")]
    [CerasSerializable]
    public class Crate
    {
        public List<Weight> Loads;
        public uint Marker;

        public static int BeforeCallbacks;

        [OnBeforeDeserialize]
        public void NoteBefore() => BeforeCallbacks++;
    }

    public class HistoricalInheritedMemberFixtureTests : TestBase
    {
        const string Ctx = "HistoricalInheritedCurrent";
        const string OldNamespace = "Ceras.HistoricalWriter.V1Inherited";
        const string CurrentNamespace = "Ceras.Test.HistoricalInheritedFixtures";

        /// <summary>What v1 wrote at the end of the roster: the witness that every member before it was
        /// consumed exactly.</summary>
        const uint RosterSentinel = 0x5EED5EEDu;

        /// <summary>The same for the refused crate, so the file is checkable even though it never loads.</summary>
        const uint CrateSentinel = 0x0DDBA11u;

        /// <summary>Every fixture this file reads, so each one is pinned by a checked-in file.</summary>
        static readonly string[] AllFixtures = { "InheritedWiden.bin", "InheritedNarrowRefused.bin" };

        static CerasRobustSerializer Reader() => MigrationReader(Ctx);
        static int ReadMarker(byte[] payload, ref int offset) => HistoricalFixtureFiles.ReadMarker(payload, ref offset);

        /// <summary>A fixed-width unsigned value, which is how a string is written (a table index).</summary>
        static uint ReadUInt32(byte[] payload, ref int offset)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            offset += 4;
            return value;
        }

        // ── The files are what they claim to be ─────────────────────────────────────────────────────

        /// <summary>
        /// Every fixture here is a real old-build file: an intact checksum, a schema section holding the
        /// <em>old</em> model's names and not this assembly's, and a layout fingerprint that cannot match —
        /// so a load really takes the interpreter, and the refusal below really is about the schema rather
        /// than about damaged bytes.
        /// </summary>
        [Fact]
        public void Every_Fixture_Is_A_Real_Cross_Version_File()
            => HistoricalFixtureFiles.AssertEveryFixture(AllFixtures, OldNamespace, CurrentNamespace, Ctx);

        /// <summary>
        /// The section records v1's base type as a type of its own, with an <c>int</c> member called
        /// <c>Power</c> — while this build declares that member on a renamed base as a <c>long</c> called
        /// <c>Rating</c>. That is the whole claim of this file in one place, and both halves of it are
        /// stated here because they are what the value assertions below depend on: a fixture that recorded
        /// a <c>long</c> would pass them without ever exercising the coercion, and a member this build
        /// already called <c>Rating</c> would pass them without ever exercising the rename.
        /// </summary>
        [Fact]
        public void The_File_Records_An_Int_On_A_Base_Type_This_Build_Declares_As_A_Long()
        {
            var file = CerasRobustSerializer.DumpSchema(Fixture("InheritedWiden.bin"));

            // The base type has an entry of its own, even though no value in the file has that runtime
            // type, and it records the members it declares — at v1's widths.
            Assert.Contains($"{OldNamespace}.Unit", file);
            Assert.Contains("[System.Int32]  Mono  Power", file);
            Assert.Contains("[System.Int32]  Mono  Armour", file);

            // And the derived entry carries the base's members too, because the writers walk the chain:
            // the file's payload for a Soldier is those two `int`s and a string, in the order below.
            Assert.Contains($"{OldNamespace}.Soldier", file);
            Assert.Contains("[System.String]  Mono  Name", file);

            // The struct element, at the width the file used. Its two `int` members are eight bytes; this
            // build's `long`+`int` is twelve, and a struct element is a bare run of bytes with no marker
            // and no length, so nothing in the stream could recover from the difference.
            Assert.Contains($"STRUCT  [", file);
            Assert.Contains($"{OldNamespace}.Ammo", file);
            Assert.Contains("[System.Int32]  Mono  Count", file);

            // None of this build's names, so the v1 CLR types really are absent and every value below was
            // produced by migrating the file's own section.
            Assert.DoesNotContain(CurrentNamespace, file);
            Assert.DoesNotContain("Rating", file);
            Assert.DoesNotContain("Morale", file);

            // ── And the other half: what this build declares for the same two types ──────────────────
            // Read from a fresh reader, so this is the manifest a load would bake and the entries below are
            // the ones the migration is asked to map onto. The alias line is the load-bearing part: it is
            // how the file's `Unit` entry finds this type at all.
            var current = Reader().DumpSchema();

            Assert.Contains($"{CurrentNamespace}.Stat", current);
            Assert.Contains("prev: Ceras.HistoricalWriter.V1Inherited.Unit", current);
            Assert.Contains("[System.Int64]  Mono  Rating", current);
        }

        /// <summary>
        /// The payload, byte by byte, as the old build wrote it. Every number in the value assertions
        /// below is read from here, and the walk ends exactly on the last byte: a reader that consumed
        /// this file by its current model's widths would stop early or late, and the sentinel written
        /// after every collection is what turns that into a wrong number rather than a silent success.
        /// </summary>
        [Fact]
        public void The_Payload_Is_Exactly_What_V1_Wrote()
        {
            var payload = Payload(Fixture("InheritedWiden.bin"));
            int offset = 0;

            // The root: the file's own type id, then the root's reference marker. The declared type and the
            // runtime type are the same, so the writer takes the same-type marker and writes no type value.
            Assert.Equal(129, (int)SchemaSection.ReadLeb128(payload, ref offset));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));

            // ── Leader: the Soldier, and the file's member order ────────────────────────────────────
            // The members are NOT in base-chain order and not in declaration order: the writers sort by
            // (member type name, member name, declaring type), so the two `int`s of the *base* come first
            // (Armour before Power) and the derived `string` last. A reader that walked the base chain in
            // its own order would take 55 for Power and 0x12345678 for Armour.
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));
            Assert.Equal(55, SerializerBinary.ReadInt32Fixed(payload, ref offset));              // v1's Armour, now removed
            Assert.Equal(unchecked((int)0x12345678), SerializerBinary.ReadInt32Fixed(payload, ref offset));  // v1's Power
            Assert.Equal(1u, ReadUInt32(payload, ref offset));            // "Ayla", by table index

            // ── Reserve: a keyed position, and a struct value written inline ──────────────────────
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            Assert.Equal(2u, ReadUInt32(payload, ref offset));            // "slugs"
            Assert.Equal(300, SerializerBinary.ReadInt32Fixed(payload, ref offset));            // Count, four bytes
            Assert.Equal(1, SerializerBinary.ReadInt32Fixed(payload, ref offset));              // Kind
            Assert.Equal(3u, ReadUInt32(payload, ref offset));            // "shells"
            Assert.Equal(1200, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(2, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            // Note what is *not* here: no reference marker and no length in front of a struct value. The
            // element is exactly the four bytes per member the file's section describes, which is why the
            // current model's wider struct could not be substituted for it.

            // ── Loadout: the same struct on the list position, including a negative value ──────────
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            Assert.Equal(120, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(7, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(-4, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(9, SerializerBinary.ReadInt32Fixed(payload, ref offset));

            // ── Squad: the same instance as Leader, then a second one ────────────────────────────
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            Assert.Equal(1, ReadMarker(payload, ref offset));                                  // a back-reference, not a second copy
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));       // the second soldier
            Assert.Equal(12, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(-9, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(4u, ReadUInt32(payload, ref offset));            // "Bryn"

            // The sentinel, written after all four members.
            Assert.Equal(unchecked((int)RosterSentinel), SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(payload.Length, offset);

            // The three string indices above are the file's own payload table, in first-write order, so
            // they are checked against it rather than against an assumption about it.
            var strings = PayloadStrings(Fixture("InheritedWiden.bin"));
            Assert.Equal("Ayla", strings[1]);
            Assert.Equal("slugs", strings[2]);
            Assert.Equal("shells", strings[3]);
            Assert.Equal("Bryn", strings[4]);
        }

        // ── What the reader makes of it ────────────────────────────────────────────────────────────

        /// <summary>
        /// The load itself, with every old value pinned against the current value it becomes. The
        /// inherited rename and the widening are asserted on <b>both</b> objects, because the file holds
        /// three references to two: a reader that read the inherited member once and left the other
        /// soldier at its declared default would still report success.
        /// </summary>
        [Fact]
        public void An_Inherited_Renamed_And_Widened_Member_Is_Read_On_Every_Instance()
        {
            var roster = Reader().Load<Roster>(Fixture("InheritedWiden.bin"), 1);

            // The sentinel first, for the reason the byte walk gives: it is fixed-width and written after
            // every collection, so it holds the written number only if each of them consumed exactly its
            // own bytes.
            Assert.Equal(RosterSentinel, roster.Marker);

            // Leader — the first occurrence, and the one v1's `Power` was read for.
            Assert.Equal("Ayla", roster.Leader.Name);
            Assert.Equal(0x12345678L, roster.Leader.Rating);   // v1: int 0x12345678, declared on the base

            // Squad[0] is the same instance; Squad[1] is a second object with its own values.
            Assert.Equal(2, roster.Squad.Count);
            Assert.Equal("Bryn", roster.Squad[1].Name);
            Assert.Equal(-9L, roster.Squad[1].Rating);          // a negative value through the widening

            // The same inherited member on the shared instance, reached through the collection.
            Assert.Equal(0x12345678L, roster.Squad[0].Rating);
        }

        /// <summary>
        /// Sharing survives all three changes at once. <c>Leader</c> and <c>Squad[0]</c> were one instance
        /// in v1, so a reader that resolved the polymorphic second occurrence to a fresh object — or that
        /// read it as a value and let the widening build a second one — would still produce a plausible
        /// roster. The added field is on that one object, so its declared default is asserted on both
        /// references to it.
        /// </summary>
        [Fact]
        public void The_Added_Field_Takes_Its_Declared_Default_And_The_Instance_Stays_Shared()
        {
            Probe.Reset();
            var roster = Reader().Load<Roster>(Fixture("InheritedWiden.bin"), 1);

            Assert.Same(roster.Leader, roster.Squad[0]);
            Assert.NotSame(roster.Leader, roster.Squad[1]);

            // v1 never wrote `Morale`, so the value is the one this build's own construction gives the
            // object — a field initializer, not `default(int)`. A zero here would mean the object was
            // allocated without running its constructor, which is the other construction path the
            // interpreter has (a value type, or a reference type with a `[CerasConstructor]`).
            Assert.Equal(CurrentModel.DeclaredMorale, roster.Leader.Morale);
            Assert.Equal(CurrentModel.DeclaredMorale, roster.Squad[0].Morale);
            Assert.Equal(CurrentModel.DeclaredMorale, roster.Squad[1].Morale);

            // What the migration *reports* is the other half of the claim, and it is a different claim from
            // the value: `Rating` was found under its old name on the base, so it is not an added field,
            // and `Armour` is named as dropped so an application can reason about the loss. A reader that
            // got the rename wrong in the direction of "not found" would report `Rating` as added and
            // `Power` as skipped while still loading.
            var report = Probe.Last;
            Assert.NotNull(report);
            Assert.True(report!.WasMigrated);
            Assert.Contains("Armour", report.SkippedFields);
            Assert.DoesNotContain("Power", report.SkippedFields);
            Assert.Contains("Morale", report.AddedFields);
            Assert.DoesNotContain("Rating", report.AddedFields);
        }

        /// <summary>
        /// The widened struct member on both collection positions, element by element. The file's struct is
        /// eight bytes and this build's is twelve, and a struct element is a bare run of bytes with
        /// nothing in the stream that could recover from the difference — so the second element and the
        /// sentinel after it are the actual evidence, and the first element's values only mean something
        /// because of them.
        /// </summary>
        [Fact]
        public void A_Widened_Struct_Member_Comes_Back_On_The_List_And_The_Keyed_Position()
        {
            var roster = Reader().Load<Roster>(Fixture("InheritedWiden.bin"), 1);

            Assert.Equal(2, roster.Loadout.Count);
            Assert.Equal(120L, roster.Loadout[0].Count);
            Assert.Equal(7, roster.Loadout[0].Kind);
            Assert.Equal(-4L, roster.Loadout[1].Count);   // a negative value through the widening
            Assert.Equal(9, roster.Loadout[1].Kind);

            Assert.Equal(2, roster.Reserve.Count);
            Assert.Equal(300L, roster.Reserve["slugs"].Count);
            Assert.Equal(1, roster.Reserve["slugs"].Kind);
            Assert.Equal(1200L, roster.Reserve["shells"].Count);
            Assert.Equal(2, roster.Reserve["shells"].Kind);

            // And the sentinel still holds, so the twelve-byte struct this build would have written was
            // not what the reader advanced by.
            Assert.Equal(RosterSentinel, roster.Marker);
        }

        // ── The direction that must be refused ──────────────────────────────────────────────────────

        /// <summary>
        /// The same nested position, with the struct's member narrowed instead of widened.
        /// <c>int</c> to <c>short</c> is not one of the five conversions Ceras performs, and the value
        /// has to be refused rather than dropped: a reader that treated the member as removed would
        /// deliver a crate of zeroed weights and report success.
        /// <para>
        /// The refusal names the struct's own member and not the crate's collection, because that is
        /// where the change is: the element type is only ever reached as a collection argument, so there
        /// is no crate member to point at. That is the useful answer — it says which *type* to fix — and
        /// the set of conversions that would have been accepted is in the message so a caller is not sent
        /// looking for a widening that would have worked.
        /// </para>
        /// </summary>
        [Fact]
        public void A_Struct_Element_Whose_Member_Changed_To_An_Undefined_Conversion_Is_Refused_By_Name()
        {
            var bytes = Fixture("InheritedNarrowRefused.bin");

            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() => Reader().Load<Crate>(bytes, 1)));

            // The file's own type and member, the two types between them, and what to do about it.
            Assert.StartsWith("Cannot migrate the field 'Grams' of 'Ceras.HistoricalWriter.V1InheritedNarrow.Weight'", refusal.Message);
            Assert.Contains("'System.Int32'", refusal.Message);
            Assert.Contains("'System.Int16'", refusal.Message);
            Assert.Contains("no defined conversion", refusal.Message);
            Assert.Contains("[Exclude]", refusal.Message);
            Assert.Contains("Int32 -> Int64", refusal.Message);
        }

        /// <summary>
        /// The refusal happens while the old-to-current schema is validated, so no root payload is
        /// interpreted. The witness is an <c>[OnBeforeDeserialize]</c> on the current root: it runs the
        /// moment the root's payload is read, so one run would mean the reader had already started. The
        /// file is a perfectly good save — it is this build's model of it that cannot be migrated — so
        /// saying so before interpreting anything is the difference between a fixable diagnostic and a
        /// desynchronized stream.
        /// </summary>
        [Fact]
        public void The_Refusal_Happens_Before_The_Root_Payload_Is_Interpreted()
        {
            Crate.BeforeCallbacks = 0;

            Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<Crate>(Fixture("InheritedNarrowRefused.bin"), 1)));

            Assert.Equal(0, Crate.BeforeCallbacks);
        }

        /// <summary>
        /// The refused file is still a well-formed v5 save, checked the way a caller would check one that
        /// no model can load: an intact checksum and a data section that holds exactly the values and
        /// nothing else. Without this the refusal above could be a corrupted file rather than a schema
        /// verdict.
        /// </summary>
        [Fact]
        public void The_Refused_File_Is_Still_A_Well_Formed_Save()
        {
            var payload = Payload(Fixture("InheritedNarrowRefused.bin"));
            int offset = 0;

            Assert.Equal(128, (int)SchemaSection.ReadLeb128(payload, ref offset));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));   // the root
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));   // Loads
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            Assert.Equal(1250, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(1, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(7, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(2, SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(unchecked((int)CrateSentinel), SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(payload.Length, offset);
        }

        // ── The file-only reader, on the same file ──────────────────────────────────────────────────

        /// <summary>
        /// The old-data graph builder, over the same bytes and with no current model at all, has to reach
        /// the same facts from the file's own section: the inherited member under v1's name and at v1's
        /// width, and the shared soldier as one node with the second occurrence a back-reference. That is
        /// the reader which will take over <c>Load</c>'s slow path, so a shape it cannot build would
        /// otherwise be discovered only on the day the switch is flipped. (Every structural fixture is
        /// already covered generically for byte-exact consumption by <c>OldDataGraphFixtureTests</c>; what
        /// is pinned here are the claims that are specific to this file's shape.)
        /// </summary>
        [Fact]
        public void The_File_Only_Graph_Records_The_Old_Spelling_And_Keeps_The_Sharing()
        {
            var graph = OldFileGraph.Read(Fixture("InheritedWiden.bin"));
            var root = Assert.IsType<OldObjectNode>(graph.Root);
            Assert.Equal($"{OldNamespace}.Roster", root.Type.StableName);

            // The soldier: an `int` named `Power`, declared on the base, and the removed `Armour` beside
            // it — with no current name for any of them, because this reader resolves no current type.
            var leader = Assert.IsType<OldObjectNode>(Member(graph, "Leader"));
            Assert.Equal($"{OldNamespace}.Soldier", leader.Type.StableName);
            Assert.Equal(new[] { "Armour", "Power", "Name" },
                leader.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray());
            var power = Assert.IsType<OldScalar>(MemberOf(leader, "Power"));
            Assert.Equal(WellKnownTypes.Int32, power.Type.TypeId);
            Assert.Equal(unchecked((int)0x12345678), (int)power.Value!);
            Assert.All(leader.RecordedMembers, m => Assert.Null(m.CurrentFieldName));

            // The sharing: `Squad[0]` is a back-reference to the very node `Leader` reached first. The
            // writer's object-cache numbering and the graph's arena index are different index spaces, so
            // this is the property that says the two are not the same number by accident.
            var squad = Assert.IsType<OldCollectionNode>(Member(graph, "Squad"));
            Assert.Equal(2, squad.Count);
            var back = Assert.IsType<OldReferenceNode>(squad.Elements[0]);
            Assert.Equal(graph.IndexOf(leader), back.TargetIndex);
            Assert.Same(leader, graph.Node(back.TargetIndex));

            // The struct elements, on both collection positions, still as inline nodes of the file's own
            // eight-byte shape.
            var loadout = Assert.IsType<OldCollectionNode>(Member(graph, "Loadout"));
            Assert.Equal(120, (int)((OldScalar)MemberOf((OldObjectNode)loadout.Elements[0], "Count")).Value!);
            Assert.Equal(-4, (int)((OldScalar)MemberOf((OldObjectNode)loadout.Elements[1], "Count")).Value!);

            var reserve = Assert.IsType<OldCollectionNode>(Member(graph, "Reserve"));
            Assert.NotNull(reserve.Keys);
            Assert.Equal(2, reserve.Keys!.Count);
            Assert.Equal(1200, (int)((OldScalar)MemberOf((OldObjectNode)reserve.Values![1], "Count")).Value!);

            // And the sentinel, so the graph stopped exactly where the file ends.
            Assert.Equal(RosterSentinel, (uint)((OldScalar)Member(graph, "Marker")).Value!);
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────────

        static OldValue Member(OldDataGraph graph, string name)
            => ((OldObjectNode)graph.Root!).RecordedMembers.Single(m => m.Recorded!.StableName == name).Value;

        static OldValue MemberOf(OldObjectNode node, string name)
            => node.RecordedMembers.Single(m => m.Recorded!.StableName == name).Value;

        /// <summary>The file's own payload string table, read with the format's own reader.</summary>
        static string[] PayloadStrings(byte[] file)
        {
            var compression = (StringTableCompression)file[RobustHeader.FlagsOffset];
            int tableOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.StringTableOffsetOffset));
            return StringTable.Load(file, tableOffset, file.Length - tableOffset, compression, uint.MaxValue, long.MaxValue);
        }
    }
}
