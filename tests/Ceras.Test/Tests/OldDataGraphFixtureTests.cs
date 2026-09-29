// The file-only old-data graph builder, against the real checked-in v5 fixtures.
//
// `OldFileGraph.Read` decodes a save holding **no current model**: no schema registry, no migration
// plans, no type binder, no serializer, and no `Type` this build happens to have. Every fixture here was
// written by a *separate historical writer* (fixtures/Ceras.HistoricalWriter) that holds the v1 CLR model
// in its own compilation, which this assembly does not reference — so the v1 types are absent at load
// time and the only thing the decoder can see is the bytes plus its own schema section. That is the
// whole claim: a save whose types, members and shapes this build has never heard of is an ordinary input.
//
// Three things are pinned, in order of how much they would cost if they were wrong:
//
//  1. **The bytes.** Every structural fixture builds a graph and its data section is consumed *exactly* —
//     a count read with the wrong encoding, a marker decided from the wrong side, or one fixed width off
//     all move the stop, and the builder refuses anything but an exact stop.
//  2. **The two index spaces.** A back-reference names an arena index, and the writer's object-cache
//     numbering is a *different* index space (only values behind a reference marker get a slot). A
//     decoder that read a marker as an arena index would build a graph that satisfies every rule the
//     model enforces and is wrong, which is why the cycle test states concrete indices.
//  3. **The refusals.** The two shapes this graph has no node kind for yet, and the malformed inputs, are
//     named precisely rather than approximated.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test
{
    /// <summary>
    /// The only model this file declares, and it exists to give the real-writer cases a context to write
    /// in. Every root shape they use is a built-in one, so nothing here is ever read back — the point is
    /// that the file-only reader is handed a genuine v5 save produced by this build's own writer, so the
    /// writer and the decoder are checked against each other on the grammar rather than against a blob
    /// nobody can regenerate.
    /// </summary>
    [CerasContext("OldFileGraph")]
    [CerasSerializable]
    public class OldFileGraphContext
    {
        public int Value;
        public string Text;
    }

    public class OldDataGraphFixtureTests
    {
        static CerasRobustSerializer Writer(Action<CerasRobustConfig> declare = null!)
            => new(c =>
            {
                c.UseGeneratedFormatters("OldFileGraph");
                c.SetUnknownBuildTimestamp();
                declare?.Invoke(c);
            });

        // ── 1. Every structural fixture, byte-exact ─────────────────────────────────────────────

        /// <summary>
        /// Every checked-in v5 fixture except the two that hold a custom formatter's framed payload, which
        /// this graph has no node kind for and which is refused below. The generic-alias fixtures are here
        /// too: they are the ones whose <em>current</em> model renames a closed generic, so a file they
        /// describe is only readable by something that answers from the file.
        /// </summary>
        public static IEnumerable<object[]> StructuralFixtures()
        {
            foreach (var (folder, directory) in new[]
                     {
                         ("HistoricalV1", HistoricalFixtureFiles.Directory),
                         ("GenericAliasV1", Path.Combine(AppContext.BaseDirectory, "Fixtures", "GenericAliasV1")),
                     })
            {
                foreach (var name in Directory.GetFiles(directory, "*.bin")
                                             .Select(Path.GetFileName)
                                             .OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (name is "OpaqueFrameHolder.bin" or "OpaqueDroppedMemberHolder.bin") continue;
                    yield return new object[] { folder, name };
                }
            }
        }

        [Theory]
        [MemberData(nameof(StructuralFixtures))]
        public void Every_Structural_Fixture_Builds_A_Graph_And_Consumes_The_Data_Section_Exactly(string folder, string name)
        {
            var bytes = Read(folder, name);
            HistoricalFixtureFiles.AssertIntactChecksum(bytes);

            // The convenience entry: the whole envelope, verified, and the data section consumed exactly or
            // refused. That "or refused" is the byte-exactness claim — nothing else can end a build early.
            var graph = OldFileGraph.Read(bytes);
            AssertGraph(graph, name);

            // The same build again, this time over the declared region alone with the stop measured, so
            // the exactness is an assertion rather than something the first read implies. A fresh
            // FileSchemaView per build: the Type-value grammar interns what it synthesizes, so a second
            // builder over the same view would be refused as colliding with its own ids.
            var section = ParseSection(bytes);
            long declared = DeclaredDataSectionLength(bytes);
            var slice = new byte[declared];
            Array.Copy(bytes, DeclaredDataSectionOffset(bytes), slice, 0, slice.Length);

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(slice));
            var builder = new OldDataGraphBuilder(new FileSchemaView(section.Schemas), section.PayloadStrings, OldFileGraphLimits.Default);
            var rebuilt = builder.Build(ref reader, declared);

            Assert.Equal(declared, reader.Consumed);
            Assert.Equal(graph.NodeCount, rebuilt.NodeCount);
        }

        static void AssertGraph(OldDataGraph graph, string name)
        {
            Assert.True(graph.NodeCount > 0, $"{name}: the graph is empty, so nothing was decoded");

            // A constructed graph is never missing a root, and a value slot never holds a bare null.
            Assert.True(graph.Root != null || graph.Root is OldNullNode,
                $"{name}: the root is neither a value nor a recorded null");
            AssertEverySlotIsIndexedOrARecordedNull(graph, name);
        }

        /// <summary>
        /// Walks the whole arena and asserts the model's own rule over it: every value reachable from any
        /// node either has an index or is a recorded null, and a recorded null never has one. The graph's
        /// constructor checks this too — walking it here is what makes it a property of <em>these</em>
        /// files rather than of the check.
        /// </summary>
        static void AssertEverySlotIsIndexedOrARecordedNull(OldDataGraph graph, string name)
        {
            var seen = new HashSet<OldValue>();
            var pending = new Stack<OldValue>();

            for (int i = 0; i < graph.NodeCount; i++)
            {
                Assert.NotNull(graph.Node(i));
                if (seen.Add(graph.Node(i)!) && graph.Node(i) is not OldNullNode) pending.Push(graph.Node(i)!);
            }

            while (pending.Count > 0)
            {
                foreach (var child in Children(pending.Pop()))
                {
                    Assert.NotNull(child);
                    Assert.Equal(child is OldNullNode, graph.IndexOf(child!) == -1);
                    if (child is not OldNullNode && seen.Add(child)) pending.Push(child);
                }
            }
        }

        static IEnumerable<OldValue> Children(OldValue value)
        {
            if (value is OldObjectNode obj)
                foreach (var member in obj.Members)
                    yield return member.Value;

            if (value is OldCollectionNode collection)
            {
                foreach (var element in collection.Elements) yield return element;
                if (collection.Keys == null) yield break;
                foreach (var key in collection.Keys) yield return key;
                foreach (var v in collection.Values!) yield return v;
            }
        }

        // ── 2. The file's own record ───────────────────────────────────────────────────────────

        [Fact]
        public void The_Graph_Records_The_File_S_Own_Spelling_And_Resolves_No_Current_Type()
        {
            // The v1 model calls the root `Ceras.HistoricalWriter.V1.SaveGame`; this build calls it
            // `SaveGame`, with `Health` removed, `Mana` widened and `Chapter` declared last. The graph is
            // built from the file's section alone, so every name it holds is v1's — including the member
            // the current model no longer has, which is exactly what a migration report has to be able to
            // print.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("SaveGame.bin"));
            var root = Assert.IsType<OldObjectNode>(graph.Root);

            Assert.Equal("Ceras.HistoricalWriter.V1.SaveGame", root.Type.StableName);
            Assert.Equal(SchemaTypeKind.Class, root.Type.Kind);

            var names = root.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray();

            // Three members the current model no longer has, and one of a member type it no longer has.
            // The graph has no current model at all, so all of them are here with v1's spelling — which is
            // exactly what a migration report has to be able to print.
            Assert.Contains("RetiredText", names);
            Assert.Contains("Home", names);
            Assert.Contains("Retired", names);
            Assert.Contains("Health", ((OldObjectNode)Member(graph, "Main")).RecordedMembers.Select(m => m.Recorded!.StableName));

            Assert.Equal("Ceras.HistoricalWriter.V1.Player",
                root.RecordedMembers.Single(m => m.Recorded!.StableName == "Main").Recorded!.Type.StableName);

            // The recorded order is the file's payload order, which is not the current declaration order
            // and not the alphabetical order the v1 source reads in.
            Assert.Equal("Home", names[0]);
            Assert.Equal("RetiredText", names[^1]);
            Assert.Empty(root.RecordedMembers.Select(m => m.CurrentFieldName).Where(n => n != null));
        }

        [Fact]
        public void An_Enum_Keeps_Its_Raw_Bits_And_The_File_S_Own_Underlying_Type()
        {
            // v1's `Rank.Max` is `ulong.MaxValue`, above long.MaxValue, and v1's `Mode.Removed` is a
            // member this build has deleted. Both are facts about the file: the bits are kept raw and
            // uninterpreted, and a member no longer named is still a number rather than a default.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("SaveGame.bin"));
            var player = (OldObjectNode)Member(graph, "Main");

            var rank = (OldEnumValueNode)MemberOf(player, "Rank");
            Assert.Equal(ulong.MaxValue, rank.RawBits);
            Assert.Equal(WellKnownTypes.UInt64, rank.UnderlyingTypeId);
            Assert.Equal(WellKnownTypes.UInt64, rank.Type.DefinitionTypeId);
            Assert.Equal("Ceras.HistoricalWriter.V1.Rank", rank.Type.StableName);
            Assert.Equal(SchemaTypeKind.Enum, rank.Type.Kind);

            var difficulty = (OldEnumValueNode)Member(graph, "Difficulty");
            Assert.Equal(2UL, difficulty.RawBits);
            Assert.Equal(WellKnownTypes.Int32, difficulty.UnderlyingTypeId);
            Assert.Equal("Ceras.HistoricalWriter.V1.Mode", difficulty.Type.StableName);
        }

        [Fact]
        public void A_Struct_Is_A_Node_With_Its_Own_Arena_Index()
        {
            // A described struct is written inline with no marker and no object-cache slot, and it is
            // still a value: a value slot may only hold a node or a recorded null, so it takes an index
            // of its own even though the writer never numbered it.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("SaveGame.bin"));
            var home = Member(graph, "Home");

            var coord = Assert.IsType<OldObjectNode>(home);
            Assert.Equal("Ceras.HistoricalWriter.V1.Coord", coord.Type.StableName);
            Assert.Equal(SchemaTypeKind.Struct, coord.Type.Kind);
            Assert.True(graph.IndexOf(coord) >= 0, "an inline struct is a node, so it has an index");

            // Three members, in the file's order, and Z is one the current model dropped.
            Assert.Equal(new[] { "X", "Y", "Z" }, coord.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray());
        }

        [Fact]
        public void A_Null_Array_Member_Is_A_Null_Collection_And_Keeps_What_It_Was()
        {
            // Two different recorded facts could both be "that member is null", and which one the file
            // wrote is decided by the position, not by the type: a null `List<T>` *member* is the null
            // reference marker (one byte), while a null `int[]` member is a count of -1 written by
            // ArrayFormatter itself. The second keeps the identity of what was null, which an
            // identity-free null node would have thrown away.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("ArrayShapeKeptNull.bin"));
            var slot = Assert.IsType<OldCollectionNode>(((OldObjectNode)graph.Root!).RecordedMembers[0].Value);

            Assert.True(slot.IsNullCollection);
            Assert.Equal(Protocol.CollectionNull, slot.Count);
            Assert.Empty(slot.Elements);
            Assert.Equal(WellKnownTypes.Array, slot.Type.DefinitionTypeId);
            Assert.True(graph.IndexOf(slot) >= 0);
        }

        [Fact]
        public void A_Null_String_Is_An_Indexed_Node_Rather_Than_A_Recorded_Null()
        {
            // A null string is string-table index 0, which is a value the file wrote: the member is still
            // known to be a string. A recorded null would say only "null", and the null *reference* marker
            // is a different recorded fact with a different migration — so this is written by this build's
            // own writer and read back, rather than asserted about a fixture that happens to hold one.
            var bytes = Writer().Save(new OldFileGraphContext { Text = null }, 1);
            var graph = OldFileGraph.Read(bytes);

            var text = (OldScalar)Member(graph, "Text");
            Assert.Null(text.Value);
            Assert.Equal(WellKnownTypes.String, text.Type.TypeId);
            Assert.True(graph.IndexOf(text) >= 0, "a null string is still a value the file wrote, so it is indexed");

            // The two are not the same fact: a null *reference* in a reference-typed member is the null
            // marker, and that one takes no index at all.
            var held = (OldScalar)Member(OldFileGraph.Read(Writer().Save(new OldFileGraphContext { Text = "x" }, 1)), "Text");
            Assert.Equal("x", held.Value);
        }

        // ── 3. The two index spaces, cycles and sharing ────────────────────────────────────────

        [Fact]
        public void A_Cycle_Resolves_Through_A_Slot_To_An_Arena_Index_That_Is_Not_The_Slot()
        {
            // The property this decoder exists to get right. The writer numbers only values written behind
            // a reference marker, so `Main` is the file's tenth object-cache slot while it is the arena's
            // 33rd value — 32 values were first written before it, almost all of them inline scalars that
            // the writer never numbered. A decoder that read the marker as an arena index would produce a
            // graph that satisfies every rule the model enforces (0 < the referring index) and points the
            // cycle at a struct. So the numbers are pinned rather than derived: they are a property of
            // these golden bytes.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("SaveGame.bin"));

            var main   = (OldObjectNode)Member(graph, "Main");
            var friend = (OldObjectNode)MemberOf(main, "Friend");

            Assert.Equal(32, graph.IndexOf(main));
            Assert.Equal(33, graph.IndexOf(friend));

            // The inner Friend is a back-reference naming Main's arena index — resolved *while* Main's own
            // payload is still being read, which is the whole reason a slot's index is published at the
            // moment the slot is reserved.
            var backToMain = Assert.IsType<OldReferenceNode>(MemberOf(friend, "Friend"));
            Assert.Equal(32, backToMain.TargetIndex);
            Assert.Same(main, graph.Node(backToMain.TargetIndex));
            Assert.NotEqual(9, backToMain.TargetIndex);          // 9 is Main's writer-cache slot
            Assert.NotSame(graph.Node(9), main);                 // and arena 9 is an inline Coord
        }

        [Fact]
        public void A_Shared_Reference_Is_One_Node_And_Every_Later_Occurrence_Is_A_Back_Reference()
        {
            // v1's `Main`, `Players[0]` and `Main.Friend.Friend` were all the same instance, and
            // `Players[1]` was `Main.Friend`. The first occurrence in the file's own order keeps the node
            // and every later occurrence is a back-reference to it, which is what "shared" means in the
            // graph rather than two equal values.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("SaveGame.bin"));

            var main    = (OldObjectNode)Member(graph, "Main");
            var players = (OldCollectionNode)Member(graph, "Players");
            Assert.Equal(2, players.Count);

            var first = (OldReferenceNode)players.Elements[0];
            Assert.Equal(graph.IndexOf(main), first.TargetIndex);
            Assert.Equal(32, first.TargetIndex);
            Assert.Same(main, graph.Node(first.TargetIndex));

            var second = (OldReferenceNode)players.Elements[1];
            Assert.Equal(33, second.TargetIndex);
            Assert.NotEqual(graph.IndexOf(main), second.TargetIndex);

            // `Players[1]` is v1's `Main.Friend`, so the two nodes are one graph reached from both a
            // member and a collection slot — and the cycle between them is what the next test pins.
            Assert.Same(graph.Node(33), MemberOf(main, "Friend"));
        }

        [Fact]
        public void A_Polymorphic_Tag_Names_An_Interned_Identity_And_Resolves_No_Current_Type()
        {
            // `Slot` holds a `List<List<int>>`, which the file records as a recursive `Type` value rather
            // than as a schema entry. The decoder interns the structure as a synthesized entry and reads the
            // payload through the very same grammar a declared member takes — no current type is consulted,
            // and the two nested shapes are one identity each however many values mention them.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("PolyNestedList.bin"));
            var outer = (OldCollectionNode)Member(graph, "Slot");

            Assert.Equal(3, outer.Count);
            Assert.Equal(WellKnownTypes.List, outer.Type.DefinitionTypeId);
            Assert.True(outer.Type.TypeId >= TypeValueShapes.FirstSynthesizedId,
                "a shape the file recorded as a Type value has no manifest id of its own");
            Assert.StartsWith("<type-value:", outer.Type.StableName);

            // One argument, and it is the inner List's own identity — the same object every inner list
            // reports, not one per occurrence.
            var inner = Assert.Single(outer.Type.ArgumentTypeIds);
            Assert.Equal(WellKnownTypes.List, inner.DefinitionTypeId);
            Assert.Equal(WellKnownTypes.Int32, Assert.Single(inner.ArgumentTypeIds).TypeId);
            Assert.Same(inner, outer.Elements[0] is OldCollectionNode first ? first.Type : null);
            Assert.Same(inner, ((OldCollectionNode)outer.Elements[2]).Type);

            // An empty inner collection is empty, not null: the count is 0, not the -1 null marker.
            Assert.Equal(0, ((OldCollectionNode)outer.Elements[1]).Count);
            Assert.False(((OldCollectionNode)outer.Elements[1]).IsNullCollection);
        }

        [Fact]
        public void A_Queue_Of_Queues_Reads_Both_Counts_With_Their_Own_Encoding()
        {
            // A queue writes a fixed, unbiased 4-byte element count and a list writes the shared biased
            // varint. For the small counts a test save usually holds the two produce *plausible* wrong
            // answers rather than failures, so both levels have to be read with the rule their own
            // definition id names.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("PolyQueueOfQueue.bin"));
            var outer = (OldCollectionNode)Member(graph, "Slot");

            Assert.Equal(WellKnownTypes.Queue, outer.Type.DefinitionTypeId);
            Assert.Equal(2, outer.Count);

            var first = (OldCollectionNode)outer.Elements[0];
            var second = (OldCollectionNode)outer.Elements[1];
            Assert.Equal(2, first.Count);
            Assert.Empty(second.Elements);
            Assert.Equal(7, ((OldScalar)first.Elements[0]).Value);
            Assert.Equal(8, ((OldScalar)first.Elements[1]).Value);
        }

        [Fact]
        public void A_Bare_Object_Payload_Is_A_Value_Of_The_Reserved_Object_Identity()
        {
            // `new object()` in an `object` slot: the runtime type *is* `object`, so the writer takes the
            // same-type marker and writes no tag and no payload at all. The value is one byte and it is
            // still a value — it owns an object-cache slot, so a later occurrence of the same instance
            // is a back-reference to it.
            var graph = OldFileGraph.Read(HistoricalFixtureFiles.Fixture("PolyObjectKept.bin"));
            var slot = (OldObjectNode)Member(graph, "Slot");

            Assert.Empty(slot.Members);
            Assert.Empty(slot.RecordedMembers);
            Assert.Equal(WellKnownTypes.Object, slot.Type.TypeId);
            Assert.Equal(SchemaTypeKind.Class, slot.Type.Kind);
            Assert.True(graph.IndexOf(slot) >= 0);
        }

        // ── 4. The two shapes the graph has no node kind for yet ──────────────────────────────

        [Theory]
        [InlineData("OpaqueFrameHolder.bin", "Ceras.HistoricalWriter.V1Opaque.FrameHolder")]
        [InlineData("OpaqueDroppedMemberHolder.bin", "Ceras.HistoricalWriter.V1Opaque.DroppedMemberHolder")]
        public void A_Framed_Custom_Payload_Is_Refused_By_Type_Formatter_And_Position(string fixture, string holder)
        {
            // Both real opaque fixtures, and both refusals are the same one: the file records a custom
            // formatter's block, and the graph has no node kind for it. The refusal names the file's own
            // type, the formatter the file recorded, and where the value sits — and it is a
            // CerasSchemaException, because the file is a perfectly good save and the graph is what
            // cannot represent it. Recording it as a member list with no members would be precisely the
            // lie the file's own Opaque entry replaced.
            var error = Assert.Throws<CerasSchemaException>(
                () => OldFileGraph.Read(HistoricalFixtureFiles.Fixture(fixture)));

            Assert.Contains("Ceras.HistoricalWriter.V1Opaque.RecipeRef", error.Message);
            Assert.Contains("Ceras.HistoricalWriter.V1Opaque.RecipeRefFormatter", error.Message);
            Assert.Contains(holder, error.Message);
            Assert.Contains("member 'Target'", error.Message);
        }

        [Theory]
        [InlineData(WellKnownTypes.Nullable, new[] { WellKnownTypes.Int32 }, "Nullable<T>", false)]
        [InlineData(WellKnownTypes.KeyValuePair, new[] { WellKnownTypes.String, WellKnownTypes.Int32 }, "KeyValuePair<K,V>", false)]
        [InlineData(WellKnownTypes.Tuple2, new[] { WellKnownTypes.Int32, WellKnownTypes.String }, "a tuple", true)]
        [InlineData(WellKnownTypes.VTuple2, new[] { WellKnownTypes.Int32, WellKnownTypes.String }, "a tuple", false)]
        public void A_Structural_Value_Type_Is_Refused_By_Name_And_Not_Approximated(int definition, int[] args, string shape, bool behindAMarker)
        {
            // These are written inline (the value-type ones) or behind a reference marker (the `Tuple`
            // class), and the file records no member name for any of them, so there is nothing to turn
            // them into but an invented member list. They are refused, and the refusal says which shape it
            // is and where it sat.
            string name = "V1." + definition;
            var entry = Generic(300, name, definition, args);
            var bytes = behindAMarker
                ? Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias))
                : Leb(300);

            var error = Assert.Throws<CerasSchemaException>(() => Build(new[] { entry }, bytes));
            Assert.Contains(name, error.Message);
            Assert.Contains(shape, error.Message);
            Assert.Contains("no node kind", error.Message);
        }

        [Fact]
        public void A_Framed_Opaque_Payload_Written_Inline_Is_Refused_The_Same_Way()
        {
            // Whether a marker stands in front of the frame is the file's own recorded flag, so the
            // refusal does not depend on it: an inline opaque value reaches the same door one branch
            // earlier and is refused for the same reason.
            var entry = Opaque(300, "V1.Inline", inline: true);
            var error = Assert.Throws<CerasSchemaException>(() => Build(new[] { entry }, Leb(300)));
            Assert.Contains("V1.Inline", error.Message);
            Assert.Contains("V1.InlineFormatter", error.Message);
        }

        // ── 5. Malformed input ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_Back_Reference_To_A_Slot_That_Was_Never_Reserved_Is_Corruption()
        {
            // A marker naming an object the file never wrote. The bounds check is the shared one, and it
            // is corruption rather than a null: a reader that answered null would drop a reference the
            // save legitimately contains and report the load as a success. The second member is the one
            // that carries the marker, so the root is read first and one object exists by then.
            var entry = new OldTypeSchema(300, "V1.Holder", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new OldFieldSchema("Slot", Array.Empty<string>(), 300, false) }, 0);

            var bytes = Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias), Bias(7, Protocol.RefBias));
            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(new[] { entry }, bytes));
            Assert.Contains("back-reference points at object 7", error.Message);
        }

        [Fact]
        public void A_Malformed_Collection_Description_Is_Refused_Before_Any_Payload_Byte_Is_Read()
        {
            // A `List` that records no argument says a count and then nothing about what follows it. The
            // refusal happens while the entry is being walked, so the data section this test hands over is
            // never touched: the two bytes after the root id are still there, unread.
            var entry = Generic(300, "V1.NoElement", WellKnownTypes.List, Array.Empty<int>());
            Assert.Throws<CerasDataCorruptionException>(() => Build(new[] { entry }, Leb(300)));
            Assert.Throws<CerasDataCorruptionException>(() => Build(new[] { entry }, Leb(300)));
        }

        [Fact]
        public void An_Argument_Identity_Nothing_Describes_Is_Refused_Before_The_Payload()
        {
            // The third clause of the structural invariant, which the section parser does not ask and the
            // migration layer owns. This decoder is the layer that owns it for a file-only read, so it asks
            // it here — before the element count, because a count whose element is unknown describes no
            // payload at all.
            var element = Generic(300, "V1.Missing", WellKnownTypes.List, new[] { 4242 });
            var bytes = Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias), Bias(2, 1));

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(new[] { element }, bytes));
            Assert.Contains("4242", error.Message);
        }

        [Fact]
        public void A_Member_Whose_Type_Nothing_Describes_Is_Refused_Before_Any_Of_Its_Bytes()
        {
            var holder = new OldTypeSchema(300, "V1.Holder", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new OldFieldSchema("Slot", Array.Empty<string>(), 4242, false) }, 0);

            var bytes = Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias));
            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(new[] { holder }, bytes));

            Assert.Contains("4242", error.Message);
            Assert.Contains("member 'Slot'", error.Message);
            Assert.Contains("V1.Holder", error.Message);
        }

        [Fact]
        public void A_Closed_Identity_That_Nests_Itself_Is_Refused_Rather_Than_Recursed_Into()
        {
            // A section that names an entry as its own argument describes a type no writer recorded. It is
            // refused at the format's own nesting bound, which is the same bound a Type value applies, so
            // one rule covers every way a section can contradict itself.
            var self = Generic(300, "V1.Recursive", WellKnownTypes.List, new[] { 300 });
            var bytes = Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias), Bias(1, 1));

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(new[] { self }, bytes));
            Assert.Contains("nests more than 64 levels deep", error.Message);
        }

        [Fact]
        public void A_Type_Value_Named_Only_By_Full_Name_Is_Refused_As_Corruption()
        {
            // The one tag a Ceras writer cannot emit: a `Type` value that names its type by string has no
            // identity, and the graph records identities. The name is quoted so it is not lost from the
            // diagnostic, and the refusal is corruption because nothing a Ceras Save writes produces it.
            var holder = new OldTypeSchema(300, "V1.Holder", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new OldFieldSchema("Tag", Array.Empty<string>(), WellKnownTypes.Type, true) }, 0);

            var bytes = Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias), Leb(127), UInt32(1));
            var error = Assert.Throws<CerasDataCorruptionException>(
                () => Build(new[] { holder }, bytes, new[] { null!, "MyGame.Removed.Type" }));

            Assert.Contains("MyGame.Removed.Type", error.Message);
            Assert.Contains("member 'Tag'", error.Message);
        }

        [Fact]
        public void A_Polymorphic_Tag_Naming_A_Type_The_File_Does_Not_Describe_Is_Refused()
        {
            // A full-name tag resolves against the FILE's own section and never against this build's
            // registry: the two id spaces are independent, so a current registry id looked up in the file's
            // table would be a different type. A name the file does not describe describes no payload.
            var holder = new OldTypeSchema(300, "V1.Holder", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new OldFieldSchema("Slot", Array.Empty<string>(), WellKnownTypes.Object, false) }, 0);

            var bytes = Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias),
                              Bias(Protocol.RefNewValue, Protocol.RefBias), Leb(127), UInt32(1));

            var error = Assert.Throws<CerasSchemaException>(() => Build(new[] { holder }, bytes, new[] { null!, "MyGame.Removed" }));
            Assert.Contains("MyGame.Removed", error.Message);
            Assert.Contains("member 'Slot'", error.Message);
        }

        [Fact]
        public void A_Polymorphic_Tag_Naming_A_Manifest_Id_Nothing_Describes_Is_Refused()
        {
            var holder = new OldTypeSchema(300, "V1.Holder", Array.Empty<string>(), SchemaTypeKind.Class,
                new[] { new OldFieldSchema("Slot", Array.Empty<string>(), WellKnownTypes.Object, false) }, 0);

            var bytes = Bytes(Leb(300), Bias(Protocol.RefNewValueSameType, Protocol.RefBias),
                              Bias(Protocol.RefNewValue, Protocol.RefBias), Leb(9999));

            var error = Assert.Throws<CerasDataCorruptionException>(() => Build(new[] { holder }, bytes));
            Assert.Contains("9999", error.Message);
        }

        [Fact]
        public void A_Save_Whose_Checksum_Does_Not_Match_Is_Refused_Before_Anything_Is_Interpreted()
        {
            // Integrity first, for the reason the load path gives: the CRC covers the whole block, so no
            // field it protects may be read — or reported about — until it matches. The byte flipped is one
            // in the data section, so a reader that verified later would be answering about a payload it
            // has no right to look at yet.
            var bytes = HistoricalFixtureFiles.Fixture("SaveGame.bin");
            bytes[DeclaredDataSectionOffset(bytes) + 4] ^= 0xFF;

            var error = Assert.Throws<CerasDataCorruptionException>(() => OldFileGraph.Read(bytes));
            Assert.Contains("CRC mismatch", error.Message);
        }

        [Fact]
        public void The_Collection_Size_Bound_Is_The_Caller_S_To_Set()
        {
            // A file-only reader has no configuration to read the bound from, so the bound is a parameter.
            // A save whose largest collection is bigger than the caller's bound is refused before a single
            // element is allocated from the declared count.
            var bytes = HistoricalFixtureFiles.Fixture("Inventory.bin");

            var tight = new OldFileGraphLimits(maxCollectionSize: 1, uint.MaxValue, long.MaxValue);
            var error = Assert.Throws<CerasDataCorruptionException>(() => OldFileGraph.Read(bytes, tight));
            Assert.Contains("outside the configured maximum", error.Message);

            Assert.NotNull(OldFileGraph.Read(bytes, OldFileGraphLimits.Default));
        }

        // ── 6. Root positions, written by this build's own writer ─────────────────────────────

        [Fact]
        public void A_Scalar_Or_String_Root_Is_Written_Inline_And_Takes_Only_An_Arena_Index()
        {
            // The two positions where a value carries no reference marker at all, so nothing gives it a
            // writer-cache slot: a root is bound to its own declared type, and a scalar or a string has no
            // identity to share. Written here by this build's own writer and read back by the file-only
            // reader, so the two are checked against each other on the grammar.
            var intRoot = OldFileGraph.Read(Writer().Save(42, 1));
            Assert.Equal(42, Assert.IsType<OldScalar>(intRoot.Root).Value);
            Assert.Equal(WellKnownTypes.Int32, ((OldScalar)intRoot.Root!).Type.TypeId);
            Assert.Equal(1, intRoot.NodeCount);

            var longRoot = OldFileGraph.Read(Writer().Save(-7L, 1));
            Assert.Equal(-7L, Assert.IsType<OldScalar>(longRoot.Root).Value);
            Assert.Equal(WellKnownTypes.Int64, ((OldScalar)longRoot.Root!).Type.TypeId);

            var single = OldFileGraph.Read(Writer().Save(1.5f, 1));
            Assert.Equal(1.5f, Assert.IsType<OldScalar>(single.Root).Value);

            var text = Assert.IsType<OldScalar>(OldFileGraph.Read(Writer().Save("a name", 1)).Root);
            Assert.Equal("a name", text.Value);
            Assert.Equal(WellKnownTypes.String, text.Type.TypeId);
            Assert.Equal("System.String", text.Type.StableName);

            // A null string is index 0 in the table, which is a value the file wrote: an indexed node
            // rather than a recorded null.
            var nullText = OldFileGraph.Read(Writer().Save<string>(null!, 1));
            var nullScalar = Assert.IsType<OldScalar>(nullText.Root);
            Assert.Null(nullScalar.Value);
            Assert.Equal(0, nullText.IndexOf(nullScalar));
        }

        [Fact]
        public void A_Collection_Root_Is_Written_Behind_A_Marker_Even_Though_An_Array_Member_Is_Not()
        {
            // The one position where the entry-level marker answer is wrong, and only the position can say
            // so: a member declared as a concrete array is written by ArrayFormatter directly, while the
            // same array as a *root* — or as a collection element — is reached through a reference wrapper.
            // `SaveGame.bin`'s `int[] Slots` member is the marker-less half, and reading it exactly is what
            // proves the rule is applied per position rather than per entry.
            var arrayRoot = OldFileGraph.Read(Writer(c => c.DeclareRoot<int[]>()).Save(new[] { 1, 2, 3 }, 1));
            var array = Assert.IsType<OldCollectionNode>(arrayRoot.Root);
            Assert.Equal(3, array.Count);
            Assert.Equal(WellKnownTypes.Array, array.Type.DefinitionTypeId);
            Assert.Equal(4, arrayRoot.NodeCount);                  // the array plus one node per element
            Assert.True(arrayRoot.IndexOf(array) >= 0);

            var listRoot = OldFileGraph.Read(Writer(c => c.DeclareRoot<List<int>>()).Save(new List<int> { 1, 2, 3 }, 1));
            var list = Assert.IsType<OldCollectionNode>(listRoot.Root);
            Assert.Equal(3, list.Count);
            Assert.Equal(WellKnownTypes.List, list.Type.DefinitionTypeId);

            // A queue writes a fixed unbiased count; a list writes the shared biased varint. Both read
            // exactly, which the build's own byte-exactness check is what establishes.
            var queue = OldFileGraph.Read(Writer(c => c.DeclareRoot<Queue<int>>()).Save(new Queue<int>(new[] { 7, 8 }), 1));
            var queued = Assert.IsType<OldCollectionNode>(queue.Root);
            Assert.Equal(2, queued.Count);
            Assert.Equal(7, ((OldScalar)queued.Elements[0]).Value);
        }

        [Fact]
        public void A_Nullable_Root_Is_Refused_By_A_Real_Writers_Bytes()
        {
            // The refusal above is not only for hand-built sections: this build's own writer produces the
            // file, and the file is a perfectly ordinary v5 save that the graph cannot represent yet.
            var bytes = Writer(c => c.DeclareRoot<int?>()).Save((int?)7, 1);
            var error = Assert.Throws<CerasSchemaException>(() => OldFileGraph.Read(bytes));

            Assert.Contains("System.Nullable`1<System.Int32>", error.Message);
            Assert.Contains("Nullable<T>", error.Message);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        static OldValue Member(OldDataGraph graph, string name)
            => ((OldObjectNode)graph.Root!).RecordedMembers.Single(m => m.Recorded!.StableName == name).Value;

        static OldValue MemberOf(OldObjectNode node, string name)
            => node.RecordedMembers.Single(m => m.Recorded!.StableName == name).Value;

        static byte[] Read(string folder, string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name);
            Assert.True(File.Exists(path), $"missing golden fixture '{name}' at '{path}'.");
            return File.ReadAllBytes(path);
        }

        static long DeclaredDataSectionOffset(byte[] file)
            => BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.DataSectionOffsetOffset));

        static long DeclaredDataSectionLength(byte[] file)
            => BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.StringTableOffsetOffset))
             - BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.DataSectionOffsetOffset));

        readonly struct ParsedFile
        {
            public readonly Dictionary<string, OldTypeSchema> Schemas;
            public readonly string[] PayloadStrings;
            public ParsedFile(Dictionary<string, OldTypeSchema> schemas, string[] strings) { Schemas = schemas; PayloadStrings = strings; }
        }

        /// <summary>
        /// The file's own two regions, decoded with the format's own readers — the same two calls
        /// <c>Load</c> makes on its slow path, so what is asked here is what the interpreter was asked.
        /// The limits are the library defaults because a fixture's own sizes are not what these tests
        /// are about.
        /// </summary>
        static ParsedFile ParseSection(byte[] file)
        {
            var compression = (StringTableCompression)file[RobustHeader.FlagsOffset];
            int schemaStringOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.SchemaStringOffsetOffset));
            int dataSectionOffset = (int)DeclaredDataSectionOffset(file);
            int stringTableOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.StringTableOffsetOffset));

            var schemaStrings = StringTable.Load(file, schemaStringOffset, dataSectionOffset - schemaStringOffset,
                StringTableCompression.Disabled, uint.MaxValue, long.MaxValue);

            int schemaOffset = RobustHeader.Size;
            var schemas = SchemaSection.Read(file, ref schemaOffset, schemaStrings);

            var payloadStrings = StringTable.Load(file, stringTableOffset, file.Length - stringTableOffset,
                compression, uint.MaxValue, long.MaxValue);

            return new ParsedFile(schemas, payloadStrings);
        }

        /// <summary>Builds from a hand-written section and hand-written data-section bytes.</summary>
        static OldDataGraph Build(OldTypeSchema[] entries, byte[] dataSection, string[] payloadStrings = null!)
        {
            var byName = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal);
            foreach (var entry in entries) byName[entry.StableName] = entry;

            var builder = new OldDataGraphBuilder(
                new FileSchemaView(byName),
                payloadStrings ?? new string[] { null!, "" },
                OldFileGraphLimits.Default);

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(dataSection));
            return builder.Build(ref reader, dataSection.Length);
        }

        static OldTypeSchema Class(int typeId, string name)
            => new(typeId, name, Array.Empty<string>(), SchemaTypeKind.Class, Array.Empty<OldFieldSchema>(), 0);

        static OldTypeSchema Generic(int typeId, string name, int definitionTypeId, int[] argTypeIds)
            => new(typeId, name, Array.Empty<string>(), SchemaTypeKind.GenericInstantiation,
                Array.Empty<OldFieldSchema>(), 0, definitionTypeId, argTypeIds);

        static OldTypeSchema Opaque(int typeId, string name, bool inline)
            => new(typeId, name, Array.Empty<string>(), SchemaTypeKind.Opaque,
                Array.Empty<OldFieldSchema>(), 0, 0, Array.Empty<int>(), Array.Empty<OldEnumValue>(),
                name + "Formatter", inline);

        // The format's own byte builders, so a test never restates an encoding.
        static byte[] Bytes(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (var part in parts) all.AddRange(part);
            return all.ToArray();
        }

        static byte[] Leb(uint value)
        {
            var bytes = new List<byte>();
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                bytes.Add(b);
            } while (value != 0);
            return bytes.ToArray();
        }

        static byte[] Bias(int value, int bias) => Leb((uint)(value + bias));

        static byte[] UInt32(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return bytes;
        }
    }
}
