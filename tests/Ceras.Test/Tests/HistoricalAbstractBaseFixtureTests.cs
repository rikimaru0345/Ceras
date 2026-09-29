// Cross-version tests for a **renamed base type that this build declares abstract (or as an interface)**.
//
// The bytes come from the separate historical writer (fixtures/Ceras.HistoricalWriter,
// V1AbstractBaseModel.cs, context `HistoricalV1Abstract`), which this assembly does not reference. So the
// v1 CLR types are absent at load time and this is a real cross-version read: the file's own schema section
// is decoded and mapped onto the model below.
//
// Why these and not the inherited-member fixtures in `HistoricalInheritedMemberFixtureTests.cs`:
//
//  * A base type that is **abstract or an interface** reaches the manifest through a different
//    registration than a plain class. The source generator emits a *generated* schema for a type it can
//    write a formatter for — and that schema carries the author's `[PreviousTypeName]` — but a field-less
//    *placeholder* `SchemaTypeInfo` for an interface or abstract type it cannot
//    (`CerasFormatterGenerator.AppendMinimalRegistration`, sharing its predicate with
//    `ManifestNames.EmitsMinimalPlaceholderSchema`). "We finally made the base abstract" and "the contract
//    interface got a better name" are both ordinary refactors, and both land on that second path.
//  * The file's **schema section records every type registered in its context**, instantiated or not, so
//    both bases are in the file even though *no value in the payload has either base's runtime type*:
//    every value is a `Wraith`, a `Golem` or a `Sentinel`, and a polymorphic slot records the concrete
//    type. The base's own entry is therefore a pure identity question — can this build resolve a name it
//    never sees a value for — and it is answered by `CerasRobustSerializer.BuildIdToType`, before the root
//    type id and before any payload byte.
//  * The **second** base is an interface, so the two minimum registrations are pinned on the same file:
//    one for a type that became abstract and one for a type that stayed abstract, and a fix that only
//    covered classes would leave the second half broken.
//  * **Retained data is retained.** `HitPoints` is renamed *and* widened on the base, and `Faction` is
//    removed from it, so a base that failed to resolve could not be papered over: the values below are the
//    ones a working reader gets, and the sentinel after every member is what turns a mis-decoded slot into
//    a wrong number rather than a load that happens to succeed.
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

namespace Ceras.Test.HistoricalAbstractBaseFixtures
{
    // ── The current model ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The value the current model declares for the field v1 never wrote. Named so the declaration and the
    /// assertion that reads it back cannot drift apart, and deliberately not zero: "the declared default"
    /// and "nothing was assigned" are the same observation for a field that defaults to zero, and only one
    /// of them is a contract. It lives on a type nothing serializes, because a namespace cannot hold a
    /// field and putting the constant on <see cref="Wisp"/> would add a member to a type that exists only
    /// to be read by a test.
    /// </summary>
    static class CurrentModel
    {
        public const int DeclaredDread = 100;
    }

    /// <summary>
    /// v1's <c>Threat</c>, renamed <b>and</b> turned abstract. <c>HitPoints</c> is renamed to
    /// <c>Rating</c> and widened to a <c>long</c> at the same time; <c>Faction</c> is gone, so the bytes
    /// it wrote have to be skipped.
    /// <para>
    /// <b>abstract</b> is the whole point, not a stylistic choice: a generated manifest registers an
    /// abstract or interface type with a placeholder schema, and that registration is the one that used to
    /// pass <c>previousTypeNames: Array.Empty&lt;string&gt;()</c>. A file that names this type therefore
    /// had no name this build could resolve, and the load was refused before a payload byte was read. The
    /// reader-side counterpart of that is the file's own
    /// <c>The_File_Names_Two_Bases_This_Build_Declares_Abstract_And_Carries_Their_Renames</c>.
    /// </para>
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Abstract.Threat")]
    [CerasContext("HistoricalAbstractCurrent")]
    [CerasSerializable]
    public abstract class Menace
    {
        /// <summary>Renamed <em>and</em> widened (<c>int</c> to <c>long</c>) in the current model.</summary>
        [PreviousName("HitPoints")]
        public long Rating;

        // v1's Faction is gone; its bytes have to be skipped on the way to the derived member.
    }

    /// <summary>
    /// v1's <c>Wraith</c>, and the instance the file writes twice. The callback hands the migration report
    /// to <see cref="Probe"/> so the tests can read what the migration said it read and what it did not; it
    /// runs once per <em>instance</em>, and this one instance is written twice, so the report is the same
    /// for both references — which is the point, because the claim is about the type's mapping rather than
    /// about one object.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Abstract.Wraith")]
    [CerasContext("HistoricalAbstractCurrent")]
    [CerasSerializable]
    public class Wisp : Menace
    {
        public string Name;

        /// <summary>
        /// Added in the current model. It takes the default its own declaration gives it, on purpose: the
        /// value has to be a real number so a reader that allocated the object without running its
        /// constructor is distinguishable from one that did — and it sits on the one instance the file wrote
        /// twice, so the default and the sharing are checked together.
        /// </summary>
        public int Dread = CurrentModel.DeclaredDread;

        [OnAfterDeserialize]
        public void After(MigrationContext context) => Probe.Last = context;
    }

    /// <summary>v1's <c>Golem</c>, so the polymorphic slots hold two different concrete type ids.</summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Abstract.Golem")]
    [CerasContext("HistoricalAbstractCurrent")]
    [CerasSerializable]
    public class Colossus : Menace
    {
        public int Charge;
    }

    /// <summary>
    /// v1's <c>ITargetable</c>: an interface in v1 and an interface here, renamed. It declares no members,
    /// because an interface cannot — so the rename exists only as an identity, and a manifest that drops
    /// the alias leaves the file's entry with nothing to resolve to. The second half of the same
    /// placeholder-registration story, pinned on the same file.
    /// </summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Abstract.ITargetable")]
    [CerasContext("HistoricalAbstractCurrent")]
    public interface ITarget
    {
    }

    /// <summary>v1's <c>Sentinel</c>, the one implementation of <see cref="ITarget"/> the file holds.</summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Abstract.Sentinel")]
    [CerasContext("HistoricalAbstractCurrent")]
    [CerasSerializable]
    public class Warden : ITarget
    {
        public int Armour;
    }

    /// <summary>v1's save root, with the two bases in the three positions they were reached from.</summary>
    [PreviousTypeName("Ceras.HistoricalWriter.V1Abstract.Battle")]
    [CerasContext("HistoricalAbstractCurrent")]
    [CerasSerializable]
    public class Encounter
    {
        public Menace Leader;
        public List<Menace> Wave;
        public ITarget Focus;
        public uint Marker;
    }

    public class HistoricalAbstractBaseFixtureTests : TestBase
    {
        const string Ctx = "HistoricalAbstractCurrent";
        const string OldNamespace = "Ceras.HistoricalWriter.V1Abstract";
        const string CurrentNamespace = "Ceras.Test.HistoricalAbstractBaseFixtures";

        /// <summary>What v1 wrote at the end of the battle: the witness that every member before it was
        /// consumed exactly.</summary>
        const uint BattleSentinel = 0xAB57AB57u;

        /// <summary>Every fixture this file reads, so the file itself is pinned by a checked-in one.</summary>
        static readonly string[] AllFixtures = { "AbstractBaseRename.bin" };

        static CerasRobustSerializer Reader() => MigrationReader(Ctx);
        static int ReadMarker(byte[] payload, ref int offset) => HistoricalFixtureFiles.ReadMarker(payload, ref offset);

        /// <summary>A fixed-width unsigned value, which is how a string is written (a table index).</summary>
        static uint ReadUInt32(byte[] payload, ref int offset)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            offset += 4;
            return value;
        }

        // ── The file is what it claims to be ───────────────────────────────────────────────────────

        /// <summary>
        /// Every fixture here is a real old-build file: an intact checksum, a schema section holding the
        /// <em>old</em> model's names and not this assembly's, and a layout fingerprint that cannot match —
        /// so a load really takes the interpreter, and the values below really are produced by migrating the
        /// file's own section.
        /// </summary>
        [Fact]
        public void The_Fixture_Is_A_Real_Cross_Version_File()
            => HistoricalFixtureFiles.AssertEveryFixture(AllFixtures, OldNamespace, CurrentNamespace, Ctx);

        /// <summary>
        /// Both halves of the file's shape in one place, because the value assertions below depend on both:
        /// the section records two base types this build has **removed by renaming** and re-declared as
        /// abstract (one as a class, one as an interface), and no value in the payload has either base's
        /// runtime type. A fixture that instantiated a base would pass the load on the *value* path and
        /// prove nothing about the identity path, and a base that stayed a plain class would pass it
        /// through the generated registration, which has always carried the alias.
        /// </summary>
        [Fact]
        public void The_File_Names_Two_Bases_This_Build_Declares_Abstract_And_Carries_Their_Renames()
        {
            var file = CerasRobustSerializer.DumpSchema(Fixture("AbstractBaseRename.bin"));

            // The two bases have entries of their own, at v1's spelling and v1's widths...
            Assert.Contains($"{OldNamespace}.Threat", file);
            Assert.Contains("[System.Int32]  Mono  HitPoints", file);
            Assert.Contains("[System.String]  Mono  Faction", file);
            Assert.Contains($"{OldNamespace}.ITargetable", file);

            // ...while every value in the payload is one of the three concrete types. Nothing anywhere in
            // the file instantiates a base, which is the whole point: the base's entry is reached through
            // the *schema section*, and only the identity path can decide it.
            Assert.Contains($"{OldNamespace}.Wraith", file);
            Assert.Contains($"{OldNamespace}.Golem", file);
            Assert.Contains($"{OldNamespace}.Sentinel", file);

            // The derived entry carries the base's members too, because the writers walk the chain — so
            // the file's payload for a Wraith is a base `int`, a base `string` and its own `string`.
            Assert.Contains("[System.String]  Mono  Name", file);

            // And none of this build's names, so the v1 CLR types really are absent.
            Assert.DoesNotContain(CurrentNamespace, file);
            Assert.DoesNotContain("Rating", file);

            // ── And this build's half: both bases keep the rename they were given ────────────────────
            // The alias line is the load-bearing part of each entry, and it is what the placeholder
            // registration used to emit as Array.Empty<string>(). It is asserted here, on the manifest a
            // load would bake, and not only through a successful load: an alias that is missing is a
            // manifest defect whatever the payload happens to contain.
            var current = Reader().DumpSchema();

            Assert.Contains($"{CurrentNamespace}.Menace", current);
            Assert.Contains("prev: Ceras.HistoricalWriter.V1Abstract.Threat", current);
            Assert.Contains($"{CurrentNamespace}.ITarget", current);
            Assert.Contains("prev: Ceras.HistoricalWriter.V1Abstract.ITargetable", current);
        }

        /// <summary>
        /// The payload, byte by byte, as the old build wrote it. Every number in the value assertions below
        /// is read from here, and the walk ends exactly on the last byte.
        /// <para>
        /// Two things are worth reading off it. A polymorphic slot writes a reference marker and then a
        /// <b>concrete</b> type id, so the payload names <c>Sentinel</c>/<c>Wraith</c>/<c>Golem</c> and
        /// never <c>Threat</c> — which is why the two bases are an identity question and these slots are
        /// not. And the members are in the file's own order, not the declaration order: the three
        /// polymorphic members sort by their declared type's canonical name and the <c>uint Marker</c>
        /// last, so <c>Focus</c> — declared third — is the first member in the file.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Payload_Is_Exactly_What_V1_Wrote()
        {
            var payload = Payload(Fixture("AbstractBaseRename.bin"));
            int offset = 0;

            // The root: the file's own type id, then the root's reference marker. The declared type and the
            // runtime type are the same, so the writer takes the same-type marker and writes no type value.
            Assert.Equal(128, (int)SchemaSection.ReadLeb128(payload, ref offset));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));

            // ── Focus: the interface slot, and the first member in the file's order ──────────────────
            Assert.Equal(Protocol.RefNewValue, ReadMarker(payload, ref offset));
            Assert.Equal(130, (int)SchemaSection.ReadLeb128(payload, ref offset));                    // Sentinel
            Assert.Equal(12, SerializerBinary.ReadInt32Fixed(payload, ref offset));                   // Armour

            // ── Leader: the Wraith, with the base's members ahead of its own ─────────────────────────
            Assert.Equal(Protocol.RefNewValue, ReadMarker(payload, ref offset));
            Assert.Equal(132, (int)SchemaSection.ReadLeb128(payload, ref offset));                    // Wraith
            Assert.Equal(unchecked((int)0x01020304), SerializerBinary.ReadInt32Fixed(payload, ref offset));  // HitPoints
            Assert.Equal(1u, ReadUInt32(payload, ref offset));            // "undead", by table index
            Assert.Equal(2u, ReadUInt32(payload, ref offset));            // "Ayla"

            // ── Wave: the same instance, then the Golem ──────────────────────────────────────────────
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(payload, ref offset));
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(payload, ref offset, Protocol.CollectionBias));
            // A back-reference, not a second copy. 2 is the writer's cache index of the wraith: the root
            // took 0 and the interface slot's sentinel took 1, so the leader is 2. The two index spaces
            // (the writer's cache and this reader's) are different, so this is only meaningful together
            // with the order the two preceding members were written in.
            Assert.Equal(2, ReadMarker(payload, ref offset));
            Assert.Equal(Protocol.RefNewValue, ReadMarker(payload, ref offset));
            Assert.Equal(129, (int)SchemaSection.ReadLeb128(payload, ref offset));                    // Golem
            // Golem's own members follow the same rule the base's do — sorted by type name, then member
            // name — so the two `int`s come in alphabetical order and the `string` last.
            Assert.Equal(42, SerializerBinary.ReadInt32Fixed(payload, ref offset));                   // Charge
            Assert.Equal(-7, SerializerBinary.ReadInt32Fixed(payload, ref offset));                   // HitPoints
            Assert.Equal(3u, ReadUInt32(payload, ref offset));            // "earth"

            // The sentinel, written after all four members.
            Assert.Equal(unchecked((int)BattleSentinel), SerializerBinary.ReadInt32Fixed(payload, ref offset));
            Assert.Equal(payload.Length, offset);

            // The three string indices above are the file's own payload table, in first-write order, so
            // they are checked against it rather than against an assumption about it.
            var strings = PayloadStrings(Fixture("AbstractBaseRename.bin"));
            Assert.Equal("undead", strings[1]);
            Assert.Equal("Ayla", strings[2]);
            Assert.Equal("earth", strings[3]);
        }

        // ── What the reader makes of it ────────────────────────────────────────────────────────────

        /// <summary>
        /// The load itself. The point of the file is the two base *identities*, so this is where they are
        /// finally cashed in: a reader that could not resolve <c>V1Abstract.Threat</c> or
        /// <c>V1Abstract.ITargetable</c> refused the file before reading this, and one that resolved them
        /// but resolved them to the wrong type would produce a plausible encounter and a wrong sentinel.
        /// </summary>
        [Fact]
        public void The_Battle_Loads_With_Every_Value_The_File_Wrote()
        {
            var encounter = Reader().Load<Encounter>(Fixture("AbstractBaseRename.bin"), 1);

            // The sentinel first, for the reason the byte walk gives: it is fixed-width and written after
            // every member, so it holds the written number only if each of them consumed exactly its own
            // bytes — including the three polymorphic slots, which is where a lost base identity would
            // have shifted everything after it.
            Assert.Equal(BattleSentinel, encounter.Marker);

            // Leader: the concrete type the file recorded, through the renamed-and-widened base member.
            var leader = Assert.IsType<Wisp>(encounter.Leader);
            Assert.Equal("Ayla", leader.Name);
            Assert.Equal(0x01020304L, leader.Rating);   // v1: int 0x01020304, declared on the base

            // Wave: the same instance, then a different concrete type through the same base.
            Assert.Equal(2, encounter.Wave.Count);
            var golem = Assert.IsType<Colossus>(encounter.Wave[1]);
            Assert.Equal(-7L, golem.Rating);            // a negative value through the widening
            Assert.Equal(42, golem.Charge);

            // Focus: the renamed interface's implementation, which is the only value of a Sentinel.
            var warden = Assert.IsType<Warden>(encounter.Focus);
            Assert.Equal(12, warden.Armour);
        }

        /// <summary>
        /// The sharing survives the migration, and so does the <b>declared default</b> of a field v1 never
        /// wrote — two claims that are checked together because they land on the same object. v1 wrote one
        /// wraith, so <c>Wave[0]</c> is a back-reference in the file and the reader has to resolve it to
        /// the very object <c>Leader</c> reached first; a reader that resolved the polymorphic slot to a
        /// fresh object every time would pass every value assertion above.
        /// </summary>
        [Fact]
        public void The_Instance_The_File_Wrote_Once_Comes_Back_As_One_Object_With_Its_Declared_Default()
        {
            var encounter = Reader().Load<Encounter>(Fixture("AbstractBaseRename.bin"), 1);

            var leader = Assert.IsType<Wisp>(encounter.Leader);
            Assert.Same(leader, encounter.Wave[0]);
            Assert.NotSame(leader, encounter.Wave[1]);
            Assert.Equal(0x01020304L, encounter.Wave[0].Rating);

            // v1 never wrote `Dread`, so the value is the one this build's own construction gives the
            // object — a field initializer, not `default(int)`. A zero here would mean the object was
            // allocated without running its constructor, which is the other construction path the
            // interpreter has (a value type, or a reference type with a `[CerasConstructor]`).
            Assert.Equal(CurrentModel.DeclaredDread, leader.Dread);
            Assert.Equal(CurrentModel.DeclaredDread, Assert.IsType<Wisp>(encounter.Wave[0]).Dread);
        }

        /// <summary>
        /// What the migration <em>reports</em>, which is a different claim from the value: <c>Rating</c> was
        /// found under its old name on the base, so it is neither skipped nor reported as added, and
        /// <c>Faction</c> is named as dropped so an application can reason about the loss. A reader that
        /// got the rename wrong in the direction of "not found" would report <c>HitPoints</c> as skipped
        /// and <c>Rating</c> as added while still loading and still producing the right number.
        /// </summary>
        [Fact]
        public void The_Migration_Reports_The_Inherited_Rename_And_Names_The_Removed_Base_Member()
        {
            Probe.Reset();
            Reader().Load<Encounter>(Fixture("AbstractBaseRename.bin"), 1);

            // The report comes off a Wisp, whose entry carries the base's members, so this is the derived
            // type's own claim rather than the abstract base's (no value of which is ever built).
            var report = Probe.Last;
            Assert.NotNull(report);
            Assert.True(report!.WasMigrated);

            // The base's removed member is named as dropped…
            Assert.Contains("Faction", report.SkippedFields);
            // …and the base's renamed member is not: it was found under `HitPoints`.
            Assert.DoesNotContain("HitPoints", report.SkippedFields);
            Assert.DoesNotContain("Rating", report.SkippedFields);

            // Exactly one field is genuinely new, and it is the one this build declares.
            Assert.Contains("Dread", report.AddedFields);
            Assert.DoesNotContain("Rating", report.AddedFields);
            Assert.DoesNotContain("HitPoints", report.AddedFields);
        }

        /// <summary>
        /// A whole current-build save over the same model, through the **generated** reader, so the two
        /// halves of the claim are on one fixture: this build's own manifest carries the base identities, and
        /// a matching fingerprint really does take the fast path over the same graph shape the cross-version
        /// read above had to migrate. Without it a reader bug in the fast path over an abstract base would
        /// not be visible from this file at all, since the file's fingerprint can never match.
        /// </summary>
        [Fact]
        public void The_Current_Model_Round_Trips_Through_The_Generated_Reader()
        {
            var writer = CurrentWriter(Ctx);
            var bytes = writer.Save(new Encounter
            {
                Leader = new Wisp { Name = "Ayla", Rating = 5, Dread = 7 },
                Wave = new List<Menace> { new Colossus { Rating = -1, Charge = 3 } },
                Focus = new Warden { Armour = 9 },
                Marker = BattleSentinel,
            }, 1);

            var loaded = writer.Load<Encounter>(bytes, 1);
            var leader = Assert.IsType<Wisp>(loaded.Leader);

            Assert.Equal(BattleSentinel, loaded.Marker);
            Assert.Equal("Ayla", leader.Name);
            Assert.Equal(5L, leader.Rating);
            Assert.Equal(7, leader.Dread);
            Assert.Equal(3, Assert.IsType<Colossus>(loaded.Wave[0]).Charge);
            Assert.Equal(9, Assert.IsType<Warden>(loaded.Focus).Armour);
        }

        // ── The file-only reader, on the same file ──────────────────────────────────────────────────

        /// <summary>
        /// The old-data graph builder over the same bytes, with no current model at all, has to reach the
        /// same facts from the file's own section: the base's members under v1's names and at v1's widths,
        /// on the derived node, and the shared wraith as one node with the second occurrence a
        /// back-reference. That is the reader which will take over <c>Load</c>'s slow path, so a shape it
        /// cannot build would otherwise be discovered only on the day the switch is flipped. (Every
        /// structural fixture is already covered generically for byte-exact consumption by
        /// <c>OldDataGraphFixtureTests</c>; what is pinned here are the claims specific to this file's
        /// shape.)
        /// </summary>
        [Fact]
        public void The_File_Only_Graph_Records_The_Old_Spelling_On_The_Derived_Node()
        {
            var graph = OldFileGraph.Read(Fixture("AbstractBaseRename.bin"));
            var root = Assert.IsType<OldObjectNode>(graph.Root);
            Assert.Equal($"{OldNamespace}.Battle", root.Type.StableName);

            // The wraith: an `int` named `HitPoints` and a `string` named `Faction`, both declared on the
            // base, plus its own `Name` — with no current name for any of them, because this reader
            // resolves no current type.
            var leader = Assert.IsType<OldObjectNode>(Member(graph, "Leader"));
            Assert.Equal($"{OldNamespace}.Wraith", leader.Type.StableName);
            Assert.Equal(new[] { "HitPoints", "Faction", "Name" },
                leader.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray());
            var hitPoints = Assert.IsType<OldScalar>(MemberOf(leader, "HitPoints"));
            Assert.Equal(WellKnownTypes.Int32, hitPoints.Type.TypeId);
            Assert.Equal(unchecked((int)0x01020304), (int)hitPoints.Value!);
            Assert.All(leader.RecordedMembers, m => Assert.Null(m.CurrentFieldName));

            // The sharing: `Wave[0]` is a back-reference to the very node `Leader` reached first. The
            // writer's object-cache numbering and the graph's arena index are different index spaces, so
            // this is the property that says the two are not the same number by accident.
            var wave = Assert.IsType<OldCollectionNode>(Member(graph, "Wave"));
            Assert.Equal(2, wave.Count);
            var back = Assert.IsType<OldReferenceNode>(wave.Elements[0]);
            Assert.Equal(graph.IndexOf(leader), back.TargetIndex);
            Assert.Same(leader, graph.Node(back.TargetIndex));

            // The interface slot, and the sentinel, so the graph stopped exactly where the file ends.
            var focus = Assert.IsType<OldObjectNode>(Member(graph, "Focus"));
            Assert.Equal($"{OldNamespace}.Sentinel", focus.Type.StableName);
            Assert.Equal(BattleSentinel, (uint)((OldScalar)Member(graph, "Marker")).Value!);
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

    /// <summary>
    /// Where the last <c>[OnAfterDeserialize]</c> left its report. A public field on a non-serialized type
    /// rather than a nullable one: the test project compiles with nullable annotations off, so a <c>?</c>
    /// here would be CS8632. Left null by <see cref="Reset"/> until a callback hands one over, which is
    /// what says "no instance was built" with a <c>!</c> at the point of use.
    /// </summary>
    public static class Probe
    {
        public static MigrationContext Last;
        public static void Reset() => Last = null;
    }
}
