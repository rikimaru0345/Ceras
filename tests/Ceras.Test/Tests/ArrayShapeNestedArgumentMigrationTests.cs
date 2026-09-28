// Cross-version tests for an array-payload shape change one level *in*: the member keeps its collection
// family on both sides of the version boundary, and a **generic argument** changes between a concrete array
// and a polymorphic slot. `Format/LAYOUT.md` and `ArrayShapeChangeMigrationTests.cs` cover the same policy on
// the member itself; this is the argument tree.
//
// The bytes come from the separate historical writer (fixtures/Ceras.HistoricalWriter), which holds the v1
// models in their own compilations and contexts and which this assembly does not reference. Same-assembly
// round tripping cannot stand in for that: it never enters the interpreter.
//
// The member's own grammar is identical in both builds here — a `List<X>` member is a reference marker, a
// biased count and then the elements either way — so nothing about the member can tell that anything moved.
// What can is the argument tree, because the schema section records every closed generic as its own entry
// with ordered argument type ids, and the current model supplies the CLR types those ids are paired with.
// The files below make that concrete: `NestedListToObject.bin` writes each element as a *bare* biased count
// (declared `int[]`, runtime `int[]`, so ReferenceFormatter takes the same-type marker and no type value),
// while `NestedListFromObject.bin` writes a marker *and* a type value for the very same array.
//
// The tests pin the refusal in both directions, the keyed case (only the value argument moves), the
// two-levels-down case, and — the ones that could plausibly be broken by over-reach — the argument trees
// that must keep loading: an unchanged one, an element-type widening that the reader already coerces, a
// renamed element type, a removed member, and both readers agreeing on bytes this build wrote.
using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using static Ceras.Test.HistoricalFixtureFiles;

namespace Ceras.Test.HistoricalNestedArrayShapeFixtures
{
    // ── The current model ──────────────────────────────────────────────────────────────────────────
    // One current type per v1 type. The renames are what make the layout fingerprint differ, so a load has
    // to take the migration interpreter.

    /// <summary>v1's <c>ListOfArraysCrate</c>: the element was a concrete <c>int[]</c>, it is a declared
    /// <c>object</c> now. Carries the callback counter the "before the payload" test watches.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.ToObject.ListOfArraysCrate")]
    public class ToObjectCrate
    {
        public List<object> Slot;
        public uint Marker;

        /// <summary>Counts the loads that actually reached the payload.</summary>
        public static int BeforeCallbacks;

        [OnBeforeDeserialize]
        public void NoteBefore() => BeforeCallbacks++;
    }

    /// <summary>v1's <c>ListOfObjectsCrate</c>: the element was a declared <c>object</c>, it is a concrete
    /// <c>int[]</c> now.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.FromObject.ListOfObjectsCrate")]
    public class FromObjectCrate
    {
        public List<int[]> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>DictOfArraysCrate</c>: only the <em>value</em> argument moved, so the key is still a
    /// plain string-table index.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.DictToObject.DictOfArraysCrate")]
    public class DictToObjectCrate
    {
        public Dictionary<string, object> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>DeepListCrate</c>: the array is the inner list's element, so it sits two argument
    /// levels down and a single-level comparison would not see it.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.DeepToObject.DeepListCrate")]
    public class DeepToObjectCrate
    {
        public List<List<object>> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>SameListOfArraysCrate</c>: the argument tree is unchanged.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.Same.SameListOfArraysCrate")]
    public class SameListCrate
    {
        public List<int[]> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>SameDictOfArraysCrate</c>: likewise, on the keyed side.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.Same.SameDictOfArraysCrate")]
    public class SameDictCrate
    {
        public Dictionary<string, int[]> Slot;
        public uint Marker;
    }

    /// <summary>The root the hand-assembled malformed-schema files declare: a <c>List&lt;int&gt;</c> plus a
    /// sentinel, so the schema under test is one this build *does* describe apart from the one position being
    /// malformed.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    public class PlainListCrate
    {
        public List<int> Slot;
        public uint Marker;
    }

    /// <summary>A member declared as a concrete <c>int[]</c> — the position whose canonical name the identity
    /// fix is about, and the control for the two collection positions beside it.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    public class DirectArrayCrate
    {
        public int[] Direct;
        public uint Marker;
    }

    /// <summary>v1's <c>ListOfListsCrate</c>: <c>List&lt;List&lt;int&gt;&gt;</c> becoming
    /// <c>List&lt;int[]&gt;</c> — the definition matches, the one argument does not.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.ListToArray.ListOfListsCrate")]
    public class ToArrayCrate
    {
        public List<int[]> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>DictKeyCrate</c>: <c>Dictionary&lt;string, int&gt;</c> becoming
    /// <c>Dictionary&lt;int, int&gt;</c> — argument 0 changed and argument 1 did not.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.DictKeyChange.DictKeyCrate")]
    public class DictKeyCrate
    {
        public Dictionary<int, int> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>DeepArgCrate</c>: <c>List&lt;List&lt;int&gt;&gt;</c> becoming
    /// <c>List&lt;List&lt;long&gt;&gt;</c> — the outer argument unchanged, the inner one moved, so the refusal
    /// can only come from a walk that recurses.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.DeepArgChange.DeepArgCrate")]
    public class DeepArgCrate
    {
        public List<List<long>> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>RemovedArgCrate</c>: the argument type (<c>Vanished</c>) has no counterpart in the
    /// current model at all, so this build cannot name the position. It is <em>not</em> refused — see
    /// <c>RemovedArgumentTypeIsSkippedNotGuessed</c>.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.RemovedArg.RemovedArgCrate")]
    public class RemovedArgCrate
    {
        public List<int> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>SameNestedListCrate</c>: two argument levels, neither an array, and unchanged. The
    /// nested control that reads real values back.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.Same.SameNestedListCrate")]
    public class SameNestedListCrate
    {
        public List<List<int>> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>WidenedListCrate</c>: <c>List&lt;int&gt;</c> becomes <c>List&lt;long&gt;</c>. The
    /// argument's type changes and its shape does not; the reader already coerces each element through the
    /// defined widening set, so this is ordinary same-family evolution that must keep working.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.Widened.WidenedListCrate")]
    public class WidenedListCrate
    {
        public List<long> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>Ping</c>, renamed. The argument's *type* changed; its shape did not.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.Same.Ping")]
    public class Pong
    {
        public int Beat;
    }

    /// <summary>v1's <c>ListOfRenamedCrate</c>: the element type is renamed, and a rename must not reach
    /// the shape comparison at all — it is resolved through the alias, not compared as text.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.Same.ListOfRenamedCrate")]
    public class RenamedArgCrate
    {
        public List<Pong> Slot;
        public uint Marker;
    }

    /// <summary>v1's <c>RemovedListOfArraysCrate</c>: the current model has no slot, so its bytes are
    /// consumed and there is no current argument tree to compare against.</summary>
    [CerasContext("HistoricalNestedShapeCurrent")]
    [CerasSerializable]
    [PreviousTypeName("Ceras.HistoricalWriter.V1Nested.Same.RemovedListOfArraysCrate")]
    public class RemovedCrate
    {
        public uint Marker;
    }

    public class ArrayShapeNestedArgumentMigrationTests : TestBase
    {
        const string Ctx = "HistoricalNestedShapeCurrent";
        const uint Sentinel = 0x3C3C3C3Cu;

        /// <summary>Every fixture this file reads, so each one is pinned by a checked-in file.</summary>
        static readonly string[] AllFixtures =
        {
            "NestedListToObject.bin", "NestedListFromObject.bin", "NestedDictToObject.bin",
            "NestedDeepToObject.bin", "NestedSameList.bin", "NestedSameDict.bin",
            "NestedSameNestedList.bin", "NestedSameListShared.bin", "NestedRenamedArgList.bin",
            "NestedRemovedList.bin", "NestedWidenedList.bin", "NestedListToArray.bin",
            "NestedDictKeyChange.bin", "NestedDeepArgChange.bin", "NestedRemovedArg.bin",
        };

        static int ReadMarker(byte[] payload, ref int offset) => HistoricalFixtureFiles.ReadMarker(payload, ref offset);
        static CerasRobustSerializer Reader() => HistoricalFixtureFiles.MigrationReader(Ctx);
        static CerasRobustSerializer Current() => HistoricalFixtureFiles.CurrentWriter(Ctx);

        // ── The files are what they claim to be ─────────────────────────────────────────────────────

        /// <summary>
        /// Every fixture this file reads is a real old-build file: an intact checksum, a schema section that
        /// holds the <em>old</em> model's names (and not this assembly's), and a layout fingerprint that
        /// cannot match — so a load really has to take the interpreter, and a refusal really is about the
        /// schema rather than about damaged bytes.
        /// </summary>
        [Fact]
        public void Every_Fixture_Is_A_Real_Cross_Version_File()
            => HistoricalFixtureFiles.AssertEveryFixture(AllFixtures, "Ceras.HistoricalWriter.V1Nested", "Ceras.Test.HistoricalNestedArrayShapeFixtures", Ctx);


        [Fact]
        public void The_File_Records_The_Argument_Tree_And_Writes_Elements_By_The_Declared_Argument_Type()
        {
            var dump = CerasRobustSerializer.DumpSchema(Fixture("NestedListToObject.bin"));
            Assert.Contains("System.Collections.Generic.List`1<System.Int32[]>", dump);   // the outer entry
            Assert.Contains("System.Int32[]", dump);                                        // and its argument
            Assert.Contains("def=System.Array", dump);                                      // which is an array entry

            // List<int[]>: the member's own marker, the count, then per element a same-type marker and a
            // *bare* biased count — the declared element type is exactly the runtime type, so there is no
            // type value to write.
            var concrete = Payload(Fixture("NestedListToObject.bin"));
            int offset = 0;
            Assert.Equal(WellKnownTypes.UserTypeBase, (int)SchemaSection.ReadLeb128(concrete, ref offset));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(concrete, ref offset));      // the root
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(concrete, ref offset));      // the list member
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(concrete, ref offset, Protocol.CollectionBias));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(concrete, ref offset));      // element 0
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(concrete, ref offset, Protocol.CollectionBias));
            Assert.Equal(1, SerializerBinary.ReadInt32Fixed(concrete, ref offset));
            Assert.Equal(2, SerializerBinary.ReadInt32Fixed(concrete, ref offset));
            Assert.Equal(Protocol.RefNewValueSameType, ReadMarker(concrete, ref offset));      // element 1
            Assert.Equal(1, SerializerBinary.ReadUInt32Bias(concrete, ref offset, Protocol.CollectionBias));
            Assert.Equal(3, SerializerBinary.ReadInt32Fixed(concrete, ref offset));
            Assert.Equal(unchecked((int)Sentinel), SerializerBinary.ReadInt32Fixed(concrete, ref offset));
            Assert.Equal(concrete.Length, offset);

            // List<object> holding the same arrays: identical until the elements, where each one carries a
            // RefNewValue marker and its concrete type as a Type value, because the declared element type
            // (`object`) is not the runtime type. That byte is the one a `List<int[]>` reader has no place
            // for, and the one a `List<object>` reader cannot do without.
            var poly = Payload(Fixture("NestedListFromObject.bin"));
            offset = 0;
            SchemaSection.ReadLeb128(poly, ref offset);
            ReadMarker(poly, ref offset);
            ReadMarker(poly, ref offset);
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(poly, ref offset, Protocol.CollectionBias));
            Assert.Equal(Protocol.RefNewValue, ReadMarker(poly, ref offset));                   // element 0
            Assert.Equal(WellKnownTypes.Array, (int)SchemaSection.ReadLeb128(poly, ref offset));
            Assert.Equal(WellKnownTypes.Int32, (int)SchemaSection.ReadLeb128(poly, ref offset));
            Assert.Equal(2, SerializerBinary.ReadUInt32Bias(poly, ref offset, Protocol.CollectionBias));
            Assert.Equal(4, SerializerBinary.ReadInt32Fixed(poly, ref offset));
            Assert.Equal(5, SerializerBinary.ReadInt32Fixed(poly, ref offset));
            Assert.Equal(Protocol.RefNewValue, ReadMarker(poly, ref offset));                   // element 1
            Assert.Equal(WellKnownTypes.Array, (int)SchemaSection.ReadLeb128(poly, ref offset));
            Assert.Equal(WellKnownTypes.Int32, (int)SchemaSection.ReadLeb128(poly, ref offset));
            Assert.Equal(3, SerializerBinary.ReadUInt32Bias(poly, ref offset, Protocol.CollectionBias));
            for (int i = 6; i <= 8; i++)
                Assert.Equal(i, SerializerBinary.ReadInt32Fixed(poly, ref offset));
            Assert.Equal(unchecked((int)Sentinel), SerializerBinary.ReadInt32Fixed(poly, ref offset));
            Assert.Equal(poly.Length, offset);
        }

        // ── The refusal ────────────────────────────────────────────────────────────────────────────

        public static IEnumerable<object[]> ChangedArguments()
        {
            // (fixture, how to load it, the declared argument type the file records, the one this build
            // declares, and where in the tree the change sits)
            yield return new object[] { "NestedListToObject.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<ToObjectCrate>(b, 1) ),
                "System.Int32[]", "System.Object", "the member > argument 0" };
            yield return new object[] { "NestedListFromObject.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<FromObjectCrate>(b, 1) ),
                "System.Object", "System.Int32[]", "the member > argument 0" };
            yield return new object[] { "NestedDictToObject.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<DictToObjectCrate>(b, 1) ),
                "System.Int32[]", "System.Object", "the member > argument 1" };
            yield return new object[] { "NestedDeepToObject.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<DeepToObjectCrate>(b, 1) ),
                "System.Int32[]", "System.Object", "the member > argument 0 > argument 0" };
        }

        [Theory]
        [MemberData(nameof(ChangedArguments))]
        public void An_Argument_Whose_Shape_Changed_Is_Refused(string fixture,
            Action<CerasRobustSerializer, byte[]> load, string oldArgType, string newArgType, string position)
        {
            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() => load(Reader(), Fixture(fixture))));

            // The member, and *where in its shape* the change is: a "list of something else" message would
            // not be enough to act on, and neither would one that named the member without saying which
            // argument moved.
            Assert.Contains("'Slot'", refusal.Message);
            Assert.Contains(position, refusal.Message);

            // Both argument types, the marker that moved, and the two-sided grammar.
            Assert.Contains(oldArgType, refusal.Message);
            Assert.Contains(newArgType, refusal.Message);
            Assert.Contains("reference marker", refusal.Message);

            // The remedy, and the honest statement of where the check can run.
            Assert.Contains("[PreviousName]", refusal.Message);
            Assert.Contains("[OnAfterDeserialize]", refusal.Message);
            Assert.Contains("load-time check", refusal.Message);
        }

        [Fact]
        public void The_Refusal_Names_The_Declaring_Type_The_File_Recorded()
        {
            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<ToObjectCrate>(Fixture("NestedListToObject.bin"), 1)));

            Assert.StartsWith("Cannot migrate the field 'Slot' of 'Ceras.HistoricalWriter.V1Nested.ToObject.", refusal.Message);
        }

        /// <summary>
        /// The refusal happens while the old-to-current schema is validated, so no root payload is
        /// interpreted. The witness is an <c>[OnBeforeDeserialize]</c> on the current root: it runs the moment
        /// the root's payload is read, so one run would mean the reader had already started.
        /// </summary>
        [Fact]
        public void The_Refusal_Happens_Before_The_Root_Payload_Is_Interpreted()
        {
            ToObjectCrate.BeforeCallbacks = 0;

            Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<ToObjectCrate>(Fixture("NestedListToObject.bin"), 1)));

            Assert.Equal(0, ToObjectCrate.BeforeCallbacks);
        }

        /// <summary>
        /// A structural argument that is no longer the same current CLR type. The definition still matches,
        /// so <c>TypeIdsCompatible</c> accepts every one of these and only the argument walk sees them; the
        /// failure each replaces is a bare reflection <see cref="ArgumentException"/> from assigning the
        /// file's closed generic to the current member, not a desync.
        /// </summary>
        public static IEnumerable<object[]> ChangedArgumentTypes()
        {
            // (fixture, how to load it, the position, the type the file records, the one this build declares)
            yield return new object[] { "NestedWidenedList.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<WidenedListCrate>(b, 1) ),
                "the member > argument 0", "System.Int32", "System.Int64" };
            yield return new object[] { "NestedListToArray.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<ToArrayCrate>(b, 1) ),
                "the member > argument 0", "System.Collections.Generic.List`1<System.Int32>", "System.Int32[]" };
            yield return new object[] { "NestedDictKeyChange.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<DictKeyCrate>(b, 1) ),
                "the member > argument 0", "System.String", "System.Int32" };
            // A *nested* argument that changed: the file's outer argument is `List<int>`, this build's is
            // `List<long>`. It is reported at the outer position, and that is not a limitation of the walk —
            // a closed generic's identity *includes* its arguments, so a change at any depth shows at the
            // first level that differs, which here is the first. (The array rule is the one whose recursion
            // is load-bearing: `List<List<int[]>>` against `List<List<object>>` agrees on array-ness at the
            // outer level and differs at the inner one — `NestedDeepToObject.bin`.)
            yield return new object[] { "NestedDeepArgChange.bin",
                (Action<CerasRobustSerializer, byte[]>)( (s, b) => s.Load<DeepArgCrate>(b, 1) ),
                "the member > argument 0",
                "System.Collections.Generic.List`1<System.Int32>", "System.Collections.Generic.List`1<System.Int64>" };
        }

        [Theory]
        [MemberData(nameof(ChangedArgumentTypes))]
        public void An_Argument_That_Is_Not_The_Same_Current_Type_Is_Refused(string fixture,
            Action<CerasRobustSerializer, byte[]> load, string position, string oldArgType, string newArgType)
        {
            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() => load(Reader(), Fixture(fixture))));

            // The member, the position, and both types — the three things a caller needs to decide what to
            // rename. A change that is *also* an array-payload move is reported by that check first, since it
            // is the more specific of the two (`List<List<int>>` against `List<int[]>` is both), and either
            // message names the same three things; the wording that tells them apart is asserted below.
            Assert.Contains("'Slot'", refusal.Message);
            Assert.Contains(position, refusal.Message);
            Assert.Contains(oldArgType, refusal.Message);
            Assert.Contains(newArgType, refusal.Message);
        }

        /// <summary>
        /// The wording that tells an identity refusal apart from an array-payload one: it is about a
        /// position naming two different types rather than about a marker moving, it points at a rename — which
        /// these checks accept, because an alias resolves to the very type the current declaration has — and
        /// it is honest that Ceras's defined conversions apply to a <em>direct</em> member and not to a
        /// collection's argument, so a caller reading it is not sent looking for a widening that will not
        /// happen.
        /// </summary>
        [Fact]
        public void The_Identity_Refusal_Points_At_A_Rename_And_Says_Which_Conversions_Still_Apply()
        {
            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<WidenedListCrate>(Fixture("NestedWidenedList.bin"), 1)));

            Assert.Contains("do not name the same type", refusal.Message);
            Assert.Contains("[PreviousTypeName]", refusal.Message);
            Assert.Contains("direct", refusal.Message);
            Assert.Contains("Int32 -> Int64", refusal.Message);
            Assert.Contains("load-time", refusal.Message);
        }

        // ── What must keep working ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The nested argument trees that load <em>and</em> read back as written, all through the migration
        /// reader on a real old-build file: an element array unchanged, the same on the keyed side, a
        /// repeated array element, two argument levels with no array anywhere, and a renamed element type.
        /// A walk that compared argument types instead of identities, or that stopped at the outermost level,
        /// would fail here; so would a canonical-name parser that resolved an array to a different
        /// <see cref="Type"/> than the one declared, which is what the first two rows used to hit.
        /// </summary>
        public static IEnumerable<object[]> LoadableArguments()
        {
            // (fixture, how to load it through the interpreter, how to write the same shape with this build,
            //  how to read the slot back, and what must be true of it)
            yield return new object[] { "NestedSameList.bin",
                (Func<CerasRobustSerializer, byte[], object>)( (s, b) => s.Load<SameListCrate>(b, 1) ),
                (Func<CerasRobustSerializer, uint, byte[]>)((w, v) => w.Save(new SameListCrate
                    { Slot = new List<int[]> { new[] { 1, 2 }, new[] { 3 } }, Marker = Sentinel }, v)),
                (Func<object, object>)( o => ((SameListCrate)o).Slot ),
                (Func<object, bool>)( c => { var l = (List<int[]>)c; return l.Count == 2 && l[0][0] == 1 && l[0][1] == 2 && l[1].Length == 1 && l[1][0] == 3; }) };
            yield return new object[] { "NestedSameDict.bin",
                (Func<CerasRobustSerializer, byte[], object>)( (s, b) => s.Load<SameDictCrate>(b, 1) ),
                (Func<CerasRobustSerializer, uint, byte[]>)((w, v) => w.Save(new SameDictCrate
                    { Slot = new Dictionary<string, int[]> { ["a"] = new[] { 9 }, ["b"] = new[] { 10, 11 } }, Marker = Sentinel }, v)),
                (Func<object, object>)( o => ((SameDictCrate)o).Slot ),
                (Func<object, bool>)( c => { var d = (Dictionary<string, int[]>)c; return d.Count == 2 && d["a"].Length == 1 && d["b"][0] == 10 && d["b"][1] == 11; }) };
            yield return new object[] { "NestedSameListShared.bin",
                (Func<CerasRobustSerializer, byte[], object>)( (s, b) => s.Load<SameListCrate>(b, 1) ),
                (Func<CerasRobustSerializer, uint, byte[]>)((w, v) =>
                {
                    // The *same* instance twice, as the fixture holds: a second element that merely has the
                    // same contents would be written by value where the file has a back-reference.
                    var shared = new[] { 7, 8 };
                    return w.Save(new SameListCrate { Slot = new List<int[]> { shared, shared }, Marker = Sentinel }, v);
                }),
                (Func<object, object>)( o => ((SameListCrate)o).Slot ),
                (Func<object, bool>)( c => { var l = (List<int[]>)c; return l.Count == 2 && ReferenceEquals(l[0], l[1]) && l[0][0] == 7; }) };
            yield return new object[] { "NestedSameNestedList.bin",
                (Func<CerasRobustSerializer, byte[], object>)( (s, b) => s.Load<SameNestedListCrate>(b, 1) ),
                (Func<CerasRobustSerializer, uint, byte[]>)((w, v) => w.Save(new SameNestedListCrate
                    { Slot = new List<List<int>> { new List<int> { 41, 42 }, new List<int>() }, Marker = Sentinel }, v)),
                (Func<object, object>)( o => ((SameNestedListCrate)o).Slot ),
                (Func<object, bool>)( c => { var l = (List<List<int>>)c; return l.Count == 2 && l[0][0] == 41 && l[0][1] == 42 && l[1].Count == 0; }) };
            yield return new object[] { "NestedRenamedArgList.bin",
                (Func<CerasRobustSerializer, byte[], object>)( (s, b) => s.Load<RenamedArgCrate>(b, 1) ),
                (Func<CerasRobustSerializer, uint, byte[]>)((w, v) => w.Save(new RenamedArgCrate
                    { Slot = new List<Pong> { new Pong { Beat = 33 } }, Marker = Sentinel }, v)),
                (Func<object, object>)( o => ((RenamedArgCrate)o).Slot ),
                (Func<object, bool>)( c => { var l = (List<Pong>)c; return l.Count == 1 && l[0].Beat == 33; }) };
        }

        [Theory]
        [MemberData(nameof(LoadableArguments))]
        public void An_Unchanged_Argument_Tree_Loads_On_The_Interpreter_And_Keeps_Its_Wire_Format(string fixture,
            Func<CerasRobustSerializer, byte[], object> load, Func<CerasRobustSerializer, uint, byte[]> save,
            Func<object, object> slotOf, Func<object, bool> check)
        {
            // 1. The migration interpreter, on the real old-build file.
            object crate = load(Reader(), Fixture(fixture));

            // The sentinel first: it is the witness that the member consumed exactly its own bytes, i.e. that
            // every element was read at the grammar the file used rather than the current one's. Every crate
            // in the table declares `Marker` as a `uint`, so one reflection read covers all of them.
            Assert.Equal(Sentinel, (uint)crate.GetType().GetField("Marker")!.GetValue(crate)!);
            Assert.True(check(slotOf(crate)), $"the interpreter did not read the slot of {fixture} back as written");

            // 2. This build's own output, which claims this context's layout — so the generated fast path is
            //    its reader — and is byte-for-byte what the v1 writer produced for the same shape once the
            //    root type id is left out (the two contexts number their roots differently, and that is the
            //    only thing about the two files that may differ). A marker or an element encoding that moved
            //    would show up here as a byte difference.
            var writer = Current();
            var bytes = save(writer, 1);
            Assert.Equal(writer.Config.SchemaRegistry.ComputeFingerprint(),
                BitConverter.ToUInt64(bytes, RobustHeader.FingerprintOffset));

            var oldPayload = Payload(Fixture(fixture));
            var newPayload = Payload(bytes);
            int oldOffset = 0, newOffset = 0;
            SchemaSection.ReadLeb128(oldPayload, ref oldOffset);
            SchemaSection.ReadLeb128(newPayload, ref newOffset);
            Assert.Equal(oldPayload[oldOffset..], newPayload[newOffset..]);

            // 3. And the generated reader and the interpreter agree on those same bytes.
            var slow = (byte[])bytes.Clone();
            slow[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(slow);
            Assert.True(check(slotOf(load(writer, bytes))), $"the generated reader did not read the {fixture} shape back as written");
            Assert.True(check(slotOf(load(writer, slow))), $"the interpreter did not re-read this build's {fixture} shape as written");
        }

        /// <summary>
        /// An argument type this build has <b>removed</b> is refused, not guessed at — and not by the
        /// argument walk, which never sees it: <c>CerasRobustSerializer.BuildIdToType</c> resolves every id
        /// the file's schema section describes before the plans are built, and a name neither the manifest nor
        /// the configured binder can resolve fails there, still with no payload interpreted. The refusal names
        /// the type, so the caller knows which one went away.
        /// <para>
        /// That is the better of the two answers and it is worth stating why it is not the answer the checks
        /// give: a type that is gone entirely cannot be migrated to, and a member declared as it has no
        /// counterpart. The one case the checks do leave alone is a name the configured
        /// <c>TypeBinder</c> resolves but the manifest does not carry — an external type bound by name — where
        /// the reader consumes the position structurally.
        /// </para>
        /// </summary>
        [Fact]
        public void An_Argument_Type_This_Build_Has_Removed_Is_Refused_By_Name()
        {
            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<RemovedArgCrate>(Fixture("NestedRemovedArg.bin"), 1)));

            Assert.Contains("Ceras.HistoricalWriter.V1Nested.RemovedArg.Vanished", refusal.Message);
        }

        [Fact]
        public void A_Removed_Member_Holding_Arrays_Still_Leaves_The_Following_Field_Aligned()
        {
            // The removed-field path has no current declaration and therefore no current argument tree: the
            // member is skipped by the file's own grammar, and the count and both elements have to be
            // consumed for the fixed-width sentinel to hold the written number. This is the case that proves
            // the walk only ever starts from a <em>mapped</em> member.
            Assert.Equal(Sentinel, Reader().Load<RemovedCrate>(Fixture("NestedRemovedList.bin"), 1).Marker);
        }

        // ── Malformed schema: ids nothing describes, and entries that name themselves ─────────────────

        /// <summary>
        /// A position the file's own schema does not describe — an id that is neither a well-known type nor
        /// an entry in its schema section — is refused, not skipped. A real writer only ever records ids
        /// from its own manifest, so this is a doctored or truncated file, and a reader has to assume the
        /// position takes no bytes, which is exactly the silent desync the checks exist to prevent.
        /// <para>
        /// Hand-assembled on purpose: no writer produces it. The member is a <c>List&lt;int&gt;</c> — a shape
        /// this build describes and would otherwise accept — with its single argument's id replaced by one
        /// nothing describes.
        /// </para>
        /// </summary>
        [Fact]
        public void An_Argument_Id_Nothing_Describes_Is_Refused_Not_Skipped()
        {
            var bytes = FileWithUndescribableArgumentId(argumentTypeId: 50);

            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<PlainListCrate>(bytes, 1)));

            Assert.Contains("'Slot'", refusal.Message);
            Assert.Contains("the member > argument 0", refusal.Message);
            Assert.Contains("type id 50", refusal.Message);
            Assert.Contains("nothing describes how that value is written", refusal.Message);
        }

        /// <summary>
        /// The same refusal from the other walk, on a <em>member</em> rather than an argument: the member's
        /// own type id is one nothing describes. The two walks share the classifier, so neither can treat "I
        /// don't know what this is" as "so it is fine".
        /// </summary>
        [Fact]
        public void A_Member_Id_Nothing_Describes_Is_Refused_Too()
        {
            var bytes = FileWithUndescribableMemberTypeId(memberTypeId: 3000);

            var refusal = Assert.IsType<CerasSchemaException>(Record.Exception(() =>
                Reader().Load<PlainListCrate>(bytes, 1)));

            Assert.Contains("type id 3000", refusal.Message);
        }

        /// <summary>
        /// A schema entry that names <b>itself</b> as its own argument is malformed too, and it must not
        /// hang. It does not need a depth refusal to be safe, and it is worth saying why rather than leaving
        /// it to the bound: the walk pairs the file's argument <em>i</em> with the current declaration's
        /// argument <em>i</em>, so its depth is bounded by how deeply nested the <em>current</em> type is, not
        /// by what the file claims. A self-cycle therefore runs out of current arguments after at most as many
        /// levels as the type is nested, and is compared honestly at every one of them.
        /// </summary>
        [Fact]
        public void A_Schema_Entry_That_Names_Itself_Is_Handled_And_Does_Not_Hang()
        {
            var bytes = FileWithSelfReferentialArgument();

            var error = Record.Exception(() => Reader().Load<PlainListCrate>(bytes, 1));

            // Whatever the outcome, it is a typed one and the payload is not silently accepted: the checks
            // compare the self-referential position against the current argument, and `List<int>` against
            // `List<int>` agrees, so this one loads. The sentinel is what says the loop terminated cleanly.
            if (error == null)
            {
                var crate = Reader().Load<PlainListCrate>(bytes, 1);
                Assert.Equal(Sentinel, crate.Marker);
                Assert.Equal(new[] { 61, 62 }, crate.Slot);
            }
            else
            {
                Assert.IsType<CerasSchemaException>(error);
            }
        }

        /// <summary>
        /// A valid robust file whose root is a <c>List&lt;int&gt;</c> member followed by a <c>uint</c>
        /// sentinel, with the schema section's argument ids supplied by the caller. Hand-assembled because
        /// none of these shapes can be produced by a writer, and written through the same
        /// <see cref="CerasSerializer.FixedArrayBufferWriter"/> the rest of the suite uses so there is no
        /// second buffer writer to keep in step.
        /// </summary>
        static byte[] FileWithUndescribableArgumentId(int argumentTypeId)
            => FileWithUndescribableIds(ArgumentTypeId, argumentTypeId);

        static byte[] FileWithUndescribableMemberTypeId(int memberTypeId)
            => FileWithUndescribableIds(memberTypeId, argumentTypeId: WellKnownTypes.Int32);

        /// <summary>
        /// A valid robust file whose root declares a <c>List&lt;int&gt;</c> member followed by a <c>uint</c>
        /// sentinel, with the schema section's member type id and the member's single argument type id
        /// supplied by the caller. Hand-assembled because no writer produces these shapes, and written
        /// through the same <see cref="CerasSerializer.FixedArrayBufferWriter"/> the rest of the suite uses so
        /// there is no second buffer writer here to keep in step.
        /// <para>
        /// Two entries: the root, and the member's own <c>List&lt;T&gt;</c> — the file must describe the member
        /// it points at, or the load fails earlier for a different reason. The payload is a real one
        /// (marker, count, two elements, sentinel) so that a refusal is about the schema and not about
        /// reading a value of no bytes.
        /// </para>
        /// </summary>
        static byte[] FileWithUndescribableIds(int memberTypeId, int argumentTypeId)
        {
            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            var schema = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            SchemaSection.WriteLeb128(schema, 2);                      // the root, and the member's own entry
            SchemaSection.WriteLeb128(schema, (uint)RootTypeId);
            SchemaSection.WriteInternedString(schema, RootName, intern);
            SchemaSection.WriteLeb128(schema, 0);                      // no previous type names
            schema.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; schema.Advance(1);
            SchemaSection.WriteLeb128(schema, 2);                      // Slot, then Marker
            WriteMember(schema, intern, "Slot", memberTypeId, monomorphic: true);
            WriteMember(schema, intern, "Marker", WellKnownTypes.UInt32, monomorphic: true);
            SchemaSection.WriteLeb128(schema, 0);                      // no fixed byte size
            WriteListArgumentEntry(schema, intern, ArgumentTypeId, argumentTypeId);

            int schemaStringOffset = schema.Offset;
            intern.Flush(ref schema.Buffer, ref schema.Offset);
            int dataStart = schema.Offset;

            var payload = new CerasSerializer.FixedArrayBufferWriter(schema.Buffer, schema.Offset);
            SchemaSection.WriteLeb128(payload, (uint)RootTypeId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);   // the root
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);   // the list
            SerializerBinary.WriteUInt32Bias(payload, 2, Protocol.CollectionBias);
            SerializerBinary.WriteInt32Fixed(payload, 61);
            SerializerBinary.WriteInt32Fixed(payload, 62);
            SerializerBinary.WriteInt32Fixed(payload, unchecked((int)Sentinel));

            // The string table starts where the data section ends, so its offset is captured *before* the
            // flush rather than after it.
            int stringTableStart = payload.Offset;
            new StringInternTable().Flush(ref payload.Buffer, ref payload.Offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(payload.Buffer, payload.Offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return payload.Buffer[..payload.Offset];
        }

        /// <summary>The same file with the member's argument id pointing at the argument entry's own id, so
        /// the schema section is self-referential.</summary>
        static byte[] FileWithSelfReferentialArgument() => FileWithUndescribableIds(ArgumentTypeId, ArgumentTypeId);

        /// <summary>One member entry: name, no previous names, type id, and the monomorphic flag.</summary>
        static void WriteMember(IBufferWriter<byte> schema, StringInternTable intern, string name, int typeId, bool monomorphic)
        {
            SchemaSection.WriteInternedString(schema, name, intern);
            SchemaSection.WriteLeb128(schema, 0);
            SchemaSection.WriteLeb128(schema, (uint)typeId);
            schema.GetSpan(1)[0] = monomorphic ? (byte)1 : (byte)0;
            schema.Advance(1);
        }

        /// <summary>A <c>List&lt;T&gt;</c> manifest entry: definition id, one argument, and the Class tail
        /// (no members, then the fixed byte size).</summary>
        static void WriteListArgumentEntry(IBufferWriter<byte> schema, StringInternTable intern, uint id, int argumentTypeId)
        {
            SchemaSection.WriteLeb128(schema, id);
            SchemaSection.WriteInternedString(schema, "System.Collections.Generic.List`1", intern);
            SchemaSection.WriteLeb128(schema, 0);
            schema.GetSpan(1)[0] = (byte)SchemaTypeKind.GenericInstantiation; schema.Advance(1);
            SchemaSection.WriteLeb128(schema, (uint)WellKnownTypes.List);
            SchemaSection.WriteLeb128(schema, 1);
            SchemaSection.WriteLeb128(schema, (uint)argumentTypeId);
            SchemaSection.WriteLeb128(schema, 0);
            SchemaSection.WriteLeb128(schema, 0);
        }

        /// <summary>The manifest ids the hand-assembled files give their two entries.</summary>
        const int RootTypeId = WellKnownTypes.UserTypeBase, ArgumentTypeId = WellKnownTypes.UserTypeBase + 1;

        /// <summary>The current model this test file's own root is: a <c>List&lt;int&gt;</c> plus a sentinel, so
        /// the hand-assembled schema can be a shape this build does describe.</summary>
        const string RootName = "Ceras.Test.HistoricalNestedArrayShapeFixtures.PlainListCrate";

        // ── The declared-type identity fix: an array resolves to the Type it is declared as ───────────

        /// <summary>
        /// The three shapes whose canonical name contains an array — a member declared as one, a collection
        /// element, a dictionary value — written and read by <em>this</em> build, through both readers, with
        /// the sentinel asserted for each.
        /// <para>
        /// These did not work before: <c>TypeIdentity.TryParseCanonicalName</c> built a rank-1 array with
        /// <c>MakeArrayType(1)</c>, a <see cref="Type"/> distinct from <c>typeof(T[])</c> and not assignable
        /// to it, so the declaration resolved from a file's own name could not be assigned to the field it
        /// belonged to and reflection refused with a bare <see cref="ArgumentException"/>. No format changed
        /// to fix it: the canonical name for rank 1 is still <c>T[]</c>, and only the <see cref="Type"/> that
        /// name is parsed into did.
        /// </para>
        /// </summary>
        public static IEnumerable<object[]> DeclaredArrayShapes()
        {
            // (where, how to write the shape, how to read the slot back, what must be true of it)
            yield return new object[] { "a member declared as one",
                (Func<CerasRobustSerializer, byte[]>)( w => w.Save(new DirectArrayCrate
                    { Direct = new[] { 1, 2, 3 }, Marker = Sentinel }, 1) ),
                (Func<CerasRobustSerializer, byte[], object>)( (w, b) => ((DirectArrayCrate)w.Load<DirectArrayCrate>(b, 1)).Direct ),
                (Func<object, bool>)( a => { var x = (int[])a; return x.Length == 3 && x[0] == 1 && x[1] == 2 && x[2] == 3; }) };
            yield return new object[] { "a collection element",
                (Func<CerasRobustSerializer, byte[]>)( w => w.Save(new SameListCrate
                    { Slot = new List<int[]> { new[] { 4, 5 } }, Marker = Sentinel }, 1) ),
                (Func<CerasRobustSerializer, byte[], object>)( (w, b) => ((SameListCrate)w.Load<SameListCrate>(b, 1)).Slot ),
                (Func<object, bool>)( a => { var l = (List<int[]>)a; return l.Count == 1 && l[0].Length == 2 && l[0][0] == 4 && l[0][1] == 5; }) };
            yield return new object[] { "a dictionary value",
                (Func<CerasRobustSerializer, byte[]>)( w => w.Save(new SameDictCrate
                    { Slot = new Dictionary<string, int[]> { ["k"] = new[] { 6 } }, Marker = Sentinel }, 1) ),
                (Func<CerasRobustSerializer, byte[], object>)( (w, b) => ((SameDictCrate)w.Load<SameDictCrate>(b, 1)).Slot ),
                (Func<object, bool>)( a => { var d = (Dictionary<string, int[]>)a; return d.Count == 1 && d["k"].Length == 1 && d["k"][0] == 6; }) };
        }

        [Theory]
        [MemberData(nameof(DeclaredArrayShapes))]
        public void A_Declared_Array_Round_Trips_Through_Both_Readers_With_No_Format_Change(string where,
            Func<CerasRobustSerializer, byte[]> save, Func<CerasRobustSerializer, byte[], object> read,
            Func<object, bool> check)
        {
            var writer = Current();
            var bytes = save(writer);

            // This file claims this context's own layout, so the generated fast path is its reader and the
            // fingerprint is the writer's own.
            Assert.Equal(writer.Config.SchemaRegistry.ComputeFingerprint(),
                BitConverter.ToUInt64(bytes, RobustHeader.FingerprintOffset));

            // The same bytes again through the interpreter.
            var slow = (byte[])bytes.Clone();
            slow[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(slow);

            Assert.True(check(read(writer, bytes)), $"the generated reader did not read {where} back as written");
            Assert.True(check(read(writer, slow)), $"the interpreter did not read {where} back as written");

            // The sentinel too, in both files: it is fixed-width and written after the array, so it holds the
            // written number only if the array's own bytes were consumed exactly and nothing was read twice —
            // which is what makes the values above trustworthy when they happen to be right by accident.
            foreach (var payload in new[] { bytes, slow })
            {
                var data = Payload(payload);
                Assert.Equal(unchecked((int)Sentinel), BitConverter.ToInt32(data, data.Length - 4));
            }
        }

        /// <summary>
        /// A repeated <em>nested</em> identity: the same array in two elements of a <c>List&lt;int[]&gt;</c>.
        /// An element goes through <c>ReferenceFormatter</c>, which registers the array in the writer's
        /// object cache, so the second element is a back-reference and the reader must resolve it to the very
        /// same instance. A member declared as a concrete <c>T[]</c> is the opposite case —
        /// <c>ArrayFormatter</c> writes it by value and registers nothing — so this also pins that the two
        /// positions are not collapsed into one. The last load is a real old-build file through the migration
        /// interpreter, so the identity cache the file implies is the historical reader's to honour as well.
        /// </summary>
        [Fact]
        public void The_Same_Array_In_Two_Elements_Comes_Back_As_One_Instance_On_Both_Readers()
        {
            var writer = Current();
            var shared = new[] { 7, 8 };
            var bytes = writer.Save(new SameListCrate { Slot = new List<int[]> { shared, shared }, Marker = Sentinel }, 1);

            var slow = (byte[])bytes.Clone();
            slow[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(slow);

            var loads = new Func<SameListCrate>[]
            {
                () => writer.Load<SameListCrate>(bytes, 1),
                () => writer.Load<SameListCrate>(slow, 1),
                () => Reader().Load<SameListCrate>(Fixture("NestedSameListShared.bin"), 1),
            };

            foreach (var load in loads)
            {
                var loaded = load();
                Assert.Equal(Sentinel, loaded.Marker);
                Assert.Equal(2, loaded.Slot.Count);
                Assert.Same(loaded.Slot[0], loaded.Slot[1]);
                Assert.Equal(new[] { 7, 8 }, loaded.Slot[0]);
            }
        }

    }
}
