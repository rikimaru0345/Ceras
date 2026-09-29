// The old-data graph's positional node kinds, against the real checked-in v5 fixtures a *separate*
// historical writer produced (fixtures/Ceras.HistoricalWriter, context `HistoricalV1Positional`).
//
// The three shapes here are the ones the format describes *structurally* rather than by member list: a
// `Nullable<T>`, a `KeyValuePair<K,V>` and the `Tuple`/`ValueTuple` families. For all three the file
// records no name for anything it holds — a nullable's value, a pair's key and value, a tuple's items
// are positions — so the claim under test is that the graph records positions and nothing else, and that
// it does not quietly turn them into an invented member list.
//
// The properties pinned, in the order they would cost:
//
//  1. **The bytes.** These fixtures join the byte-exactness sweep in OldDataGraphFixtureTests, so every
//     one is decoded and its data section consumed *exactly*.
//  2. **Positions, not names.** `Items` is indexed, `Items[i]` holds what the file's own argument
//     identity i says it is, and no node in the model gained a `Key`, a `Value`, an `Item1` or a `Rest`.
//  3. **The two nulls are different facts.** A nullable's presence byte of 0 and a null reference marker
//     both appear in one fixture, because only the first is a `Nullable`.
//  4. **The family split.** A `ValueTuple` is a struct and a `Tuple` is a class, so one of them is
//     shareable and the other is not — pinned by resolving each occurrence to the value it stands for,
//     because a decoder that read a writer-cache slot as an arena index would produce a graph
//     satisfying every rule and be wrong.
//  5. **A cycle through a position with no name.** The one place a cycle can close through a value the
//     file recorded no member for.
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras.Format;

namespace Ceras.Test
{
    public class OldPositionalGraphTests
    {
        // ── 1. A nullable is a presence byte, and the two nulls are different facts ───────────────

        [Fact]
        public void A_Present_And_An_Absent_Nullable_Are_Different_Kinds_Of_Record()
        {
            // `Absent` is `int?` with no value and `Present` is `int?` with one. The file writes one
            // presence byte for each, and nothing after the byte that is 0 — so the absent one is a
            // recorded fact about a value rather than an identity for one, and takes no arena index.
            // That is what makes it an OldNullNode and not a node with a missing value: a value with no
            // index could never be the target of a back-reference, and nothing can point at one here.
            var graph = Fixture("PositionalNullable.bin");
            var crate = Root(graph);

            var absent = (OldNullNode)Member(crate, "Absent");
            Assert.Equal(OldNullReason.NullablePresenceByte, absent.Reason);
            Assert.Equal(-1, graph.IndexOf(absent));
            Assert.IsNotType<OldNullableNode>(Member(crate, "Absent"));

            // The present one is a node, and the value it holds is a node of its own with its own index.
            // Two indices rather than one: the nullable is a value the file wrote, and so is what is
            // inside it, and rule 1 says a value's identity is its own index.
            var present = Assert.IsType<OldNullableNode>(Member(crate, "Present"));
            Assert.Equal("System.Nullable`1<System.Int32>", present.Type.StableName);
            Assert.True(graph.IndexOf(present) >= 0, "a present nullable is a value the file wrote, so it is indexed");

            var seven = Assert.IsType<OldScalar>(present.Value);
            Assert.Equal(7, seven.Value);
            Assert.True(graph.IndexOf(seven) >= 0, "the value a nullable holds is a value in its own right");
            Assert.NotEqual(graph.IndexOf(present), graph.IndexOf(seven));
        }

        [Fact]
        public void ANullableOverAStructHoldsAWholeMemberList()
        {
            // The payload of a nullable's value is whatever that type's own formatter writes, which is
            // why the position of that value has to be decided rather than assumed: `Span` is a struct,
            // so its payload is its two members and not a fixed width the reader could step over.
            var crate = Root(Fixture("PositionalNullable.bin"));

            Assert.IsType<OldNullNode>(Member(crate, "AbsentSpan"));

            var present = Assert.IsType<OldNullableNode>(Member(crate, "PresentSpan"));
            Assert.Equal("System.Nullable`1<Ceras.HistoricalWriter.V1Positional.Span>", present.Type.StableName);

            var span = Assert.IsType<OldObjectNode>(present.Value);
            Assert.Equal(new[] { "X", "Y" }, span.RecordedMembers.Select(m => m.Recorded!.StableName).ToArray());
            Assert.Equal(3, ((OldScalar)span.RecordedMembers[0].Value).Value);
            Assert.Equal(4, ((OldScalar)span.RecordedMembers[1].Value).Value);
        }

        [Fact]
        public void ANullCollectionMemberIsTheNullMarkerAndNotAPresenceByte()
        {
            // `AbsentList` is `List<int>` — C# has no `Nullable<List<int>>`, since `List<int>?` is
            // `List<int>` with an annotation and no runtime representation — so the writer wrote a null
            // reference marker for it. Both nulls are in this one file precisely so the two are told
            // apart by something other than a comment: a presence byte of 0 is
            // `NullablePresenceByte` and a null marker is `ReferenceMarker`, and a materializer that
            // treated them as the same record would fold two different migrations into one.
            var crate = Root(Fixture("PositionalNullable.bin"));

            Assert.Equal(OldNullReason.ReferenceMarker, Assert.IsType<OldNullNode>(Member(crate, "AbsentList")).Reason);
            Assert.Equal(OldNullReason.NullablePresenceByte, Assert.IsType<OldNullNode>(Member(crate, "Absent")).Reason);

            // And the present one really is a collection, written with a bare count because a
            // collection reached through a reference wrapper carries the marker, not the element type.
            var list = Assert.IsType<OldCollectionNode>(Member(crate, "PresentList"));
            Assert.Equal(2, list.Count);
            Assert.Equal(new object[] { 5, 6 }, list.Elements.Cast<OldScalar>().Select(s => s.Value).ToArray());
        }

        [Fact]
        public void ANullableIsRecordedInPositionsThatAreNotObjectMembersToo()
        {
            // A collection element and a dictionary value reach a value through the writer's element
            // formatter, so a nullable there is a presence byte in exactly the same grammar — and it is
            // the element's own recorded null that says absent, not a marker the reader has to guess at.
            var crate = Root(Fixture("PositionalNullable.bin"));

            var elements = Assert.IsType<OldCollectionNode>(Member(crate, "Elements"));
            Assert.Equal(3, elements.Count);
            Assert.Equal(8, Assert.IsType<OldScalar>(Assert.IsType<OldNullableNode>(elements.Elements[0]).Value).Value);
            Assert.Equal(OldNullReason.NullablePresenceByte, Assert.IsType<OldNullNode>(elements.Elements[1]).Reason);
            Assert.Equal(9, Assert.IsType<OldScalar>(Assert.IsType<OldNullableNode>(elements.Elements[2]).Value).Value);

            var named = Assert.IsType<OldCollectionNode>(Member(crate, "Named"));
            Assert.Equal(10, Assert.IsType<OldScalar>(Assert.IsType<OldNullableNode>(named.Values![0]).Value).Value);
            Assert.Equal(OldNullReason.NullablePresenceByte, Assert.IsType<OldNullNode>(named.Values[1]).Reason);
        }

        // ── 2. A pair and a tuple are positions, and no member name was invented ──────────────────

        [Fact]
        public void AKeyValuePairHoldsTwoPositionsAndNoKeyOrValueName()
        {
            // The file wrote a key and then a value with no count in front of them and no name beside
            // either. `Items` is indexed and nothing is named: which shape this was is already in the
            // type, whose definition id is the format's own `KeyValuePair` id and whose ordered argument
            // identities say what each position holds. A `Key` and a `Value` property would be two names
            // the writer never wrote, which is the same invention an `OldMember` list would be.
            var crate = Root(Fixture("PositionalKeyValuePair.bin"));
            var pair = Assert.IsType<OldPositionalNode>(Member(crate, "Pair"));

            Assert.Equal(WellKnownTypes.KeyValuePair, pair.Type.DefinitionTypeId);
            Assert.Equal(2, pair.Items.Count);
            Assert.Equal(pair.Type.ArgumentTypeIds.Count, pair.Items.Count);

            Assert.Equal("k", ((OldScalar)pair.Items[0]).Value);
            Assert.Equal(11, ((OldScalar)pair.Items[1]).Value);

            // No named position anywhere on the kind: the two names the CLR has would be exactly the
            // invention this node kind exists to avoid.
            Assert.Empty(typeof(OldPositionalNode).GetProperties()
                                     .Select(p => p.Name)
                                     .Where(n => n is "Key" or "Value" or "Rest" or "Item1"));
        }

        [Fact]
        public void BothOfAPairsPositionsGoThroughTheWritersReferenceFormatter()
        {
            // `ClassKey`'s key is a class and its value is a collection, and both take a reference
            // marker — a pair's key and value are the writer's *reference* formatter, not its specific
            // one, unlike a nullable's value. So the positions hold ordinary values with indices of
            // their own rather than inline records.
            var crate = Root(Fixture("PositionalKeyValuePair.bin"));
            var pair = Assert.IsType<OldPositionalNode>(Member(crate, "ClassKey"));

            var key = Assert.IsType<OldObjectNode>(pair.Items[0]);
            Assert.Equal(12, ((OldScalar)key.RecordedMembers.Single().Value).Value);

            var value = Assert.IsType<OldCollectionNode>(pair.Items[1]);
            Assert.Equal(1, value.Count);
            Assert.Equal(13, ((OldScalar)value.Elements[0]).Value);
        }

        [Fact]
        public void ANullStringKeyIsAnIndexedScalarAndNotARecordedNull()
        {
            // The second pair in `Pairs` has a null string key. A null string is string-table index 0:
            // the file wrote a value, and it still says the position was a string — which an
            // identity-free null would have thrown away. So it is an indexed `OldScalar` with a null
            // payload, and it is *not* the `ReferenceMarker` that the absent nullable beside it is.
            var crate = Root(Fixture("PositionalKeyValuePair.bin"));
            var pairs = Assert.IsType<OldCollectionNode>(Member(crate, "Pairs"));

            var second = Assert.IsType<OldPositionalNode>(pairs.Elements[1]);
            var nullKey = Assert.IsType<OldScalar>(second.Items[0]);
            Assert.Equal(WellKnownTypes.String, nullKey.Type.TypeId);
            Assert.Null(nullKey.Value);
        }

        [Fact]
        public void ATupleHoldsItsItemsInOrderWithNoItemName()
        {
            var crate = Root(Fixture("PositionalTuple.bin"));
            var tuple = Assert.IsType<OldPositionalNode>(Member(crate, "ValueTuplePair"));

            Assert.Equal(WellKnownTypes.VTuple2, tuple.Type.DefinitionTypeId);
            Assert.Equal(2, tuple.Items.Count);
            Assert.Equal(1, ((OldScalar)tuple.Items[0]).Value);
            Assert.Equal("vt", ((OldScalar)tuple.Items[1]).Value);

            // `Item1`/`Item2` are the CLR's names for these positions; the file has no such names, and
            // the ordered argument identities are what says which position is which.
            Assert.Equal(new[] { WellKnownTypes.Int32, WellKnownTypes.String },
                         tuple.Type.ArgumentTypeIds.Select(a => a.TypeId).ToArray());
        }

        [Fact]
        public void ANullableInsideATupleOrAPairIsJustAPresenceByteInThatPosition()
        {
            // `Nested` is a `KeyValuePair<int?,int?>` and `TuplePair` is a `Tuple<int,string>`; the
            // nested pair is the position that carries a nullable. Nested shapes compose positionally:
            // one position holds a nullable, which holds a value.
            var crate = Root(Fixture("PositionalKeyValuePair.bin"));
            var nested = Assert.IsType<OldPositionalNode>(Member(crate, "Nested"));

            Assert.Equal(14, Assert.IsType<OldScalar>(Assert.IsType<OldNullableNode>(nested.Items[0]).Value).Value);
            Assert.Equal(OldNullReason.NullablePresenceByte, Assert.IsType<OldNullNode>(nested.Items[1]).Reason);
        }

        // ── 3. The family split: a ValueTuple is a struct, a Tuple is a class ─────────────────────

        [Fact]
        public void TwoEqualValueTuplesAreTwoValuesAndATupleIsOne()
        {
            // The pair of facts the format splits on. A `ValueTuple` is a struct: written inline, with
            // no reference marker and no object-cache slot, so the writer wrote both members out in full
            // and they are two distinct values with two distinct indices. A `Tuple` is a class: behind a
            // marker and registered, so its second occurrence is a back-reference to the first.
            var graph = Fixture("PositionalValueTupleRepeat.bin");
            var crate = Root(graph);

            var first  = Assert.IsType<OldPositionalNode>(Member(crate, "First"));
            var second = Assert.IsType<OldPositionalNode>(Member(crate, "Second"));

            Assert.NotSame(first, second);
            Assert.NotEqual(graph.IndexOf(first), graph.IndexOf(second));

            // Equal by every recorded fact, which is the point: the file recorded two occurrences and
            // neither was a reference to the other, so the graph must not merge them on content.
            Assert.Equal(first.Type.StableName, second.Type.StableName);
            Assert.Equal(((OldScalar)first.Items[0]).Value, ((OldScalar)second.Items[0]).Value);
            Assert.Equal(((OldScalar)first.Items[1]).Value, ((OldScalar)second.Items[1]).Value);

            // The same instance through a member and a collection element. Which of the three holders of
            // it the file wrote first is decided by the *writer's* member order and is not this graph's
            // business, so the claim is made over all three rather than over a named one: exactly one of
            // them is the value and the other two are back-references to it.
            var sharedGraph = Fixture("PositionalTuple.bin");
            var shared = Root(sharedGraph);

            var holders = new[]
            {
                Member(shared, "TuplePair"),
                Member(shared, "SharedAgain"),
                ((OldCollectionNode)Member(shared, "Tuples")).Elements[0],
            };

            var value = Value(sharedGraph, holders[0]);
            Assert.Same(value, Value(sharedGraph, holders[1]));
            Assert.Same(value, Value(sharedGraph, holders[2]));

            // Exactly one of the three is that value, and the other two are references to it: the file
            // wrote it out once, which is what sharing means in the graph rather than three equal values.
            int direct = holders.Count(v => v is OldPositionalNode);
            int referenced = holders.Count(v => v is OldReferenceNode);
            Assert.Equal(1, direct);
            Assert.Equal(2, referenced);

            int valueIndex = sharedGraph.IndexOf(value);
            foreach (var holder in holders.Where(v => v is OldReferenceNode))
                Assert.Equal(valueIndex, ((OldReferenceNode)holder).TargetIndex);

            // A back-reference is a node in its own right, so it has an index of its own — one strictly
            // later than the value it names, which is rule 4.
            foreach (var holder in holders.Where(v => v is OldReferenceNode))
                Assert.True(sharedGraph.IndexOf((OldValue)holder) > valueIndex);
        }

        [Fact]
        public void ASharedClassTupleInACollectionResolvesToTheSameNode()
        {
            // The same class tuple again, this time through a collection element rather than a second
            // member. The element position is a reference position, so the writer registered the tuple
            // and the element is a back-reference to the same node — a decoder that read the marker as
            // an arena index would satisfy every rule the graph enforces and name the wrong value.
            var graph = Fixture("PositionalTuple.bin");
            var crate = Root(graph);

            var tuples = Assert.IsType<OldCollectionNode>(Member(crate, "Tuples"));
            Assert.Equal(2, tuples.Count);

            // Whichever holder the writer's member order put first, `Tuples[0]` and the two members all
            // reach the same value: this element resolves to the node the others reach too.
            var viaMember = (OldValue)Member(crate, "TuplePair");
            var viaMemberAgain = (OldValue)Member(crate, "SharedAgain");
            var viaElement = (OldValue)tuples.Elements[0];

            var value = Value(graph, viaElement);
            Assert.IsType<OldPositionalNode>(value);
            Assert.Same(value, Value(graph, viaMember));
            Assert.Same(value, Value(graph, viaMemberAgain));

            // The element that is *not* shared is written out in full, with its own index — a
            // `Tuple` in a reference position is registered, but only if it is actually repeated.
            var distinct = Assert.IsType<OldPositionalNode>(tuples.Elements[1]);
            Assert.NotSame(value, distinct);
            Assert.True(graph.IndexOf(distinct) >= 0);
            Assert.Equal(2, ((OldScalar)distinct.Items[0]).Value);
            Assert.Equal("other", ((OldScalar)distinct.Items[1]).Value);
        }

        [Fact]
        public void AClassTupleTakesAnArenaIndexOfItsOwnAndAStructTupleTakesOneToo()
        {
            // Both families are values and both are indexed — a node is a value the file wrote whether or
            // not the writer gave it an object-cache slot. The slot decides *sharing*, not identity: an
            // inline struct has an arena index and no cache id at all, which is exactly the difference
            // between the two index spaces this graph keeps apart.
            var graph = Fixture("PositionalTuple.bin");
            var crate = Root(graph);

            var structTuple = Assert.IsType<OldPositionalNode>(Member(crate, "ValueTuplePair"));
            var positionalClassTuple = Assert.IsType<OldPositionalNode>(Value(graph, Member(crate, "TuplePair")));

            Assert.True(graph.IndexOf(structTuple) >= 0, "an inline struct is a value, so it takes an index");
            Assert.True(graph.IndexOf(positionalClassTuple) >= 0);
            Assert.NotEqual(graph.IndexOf(structTuple), graph.IndexOf(positionalClassTuple));

            // And the two really are different shapes to the file, which is what decides the difference
            // above: the struct is written inline, the class behind a reference marker.
            Assert.Equal(WellKnownTypes.VTuple2, structTuple.Type.DefinitionTypeId);
            Assert.Equal(WellKnownTypes.Tuple2, positionalClassTuple.Type.DefinitionTypeId);
        }

        // ── 4. A cycle through a position the file recorded no name for ───────────────────────────

        [Fact]
        public void AKeyValuePairKeyAndATupleItemAreWhereACycleCanClose()
        {
            // The property that makes these shapes different from a nullable's: a pair's key, a pair's
            // value and a tuple's items are all written through the writer's *reference* formatter, so a
            // class in one of them is registered and can be named again later. The file closed a
            // two-node cycle through a pair's key and through a tuple's item, and the graph has to come
            // back with both directions intact.
            //
            // `b` is *found* rather than assumed at an index. Which of the two nodes is arena 0, and
            // what any given index happens to be, is decided by the file's member order — the writer's
            // sort — and a member-order change in a later build would move it. What must hold is that
            // the graph reaches both nodes and that every reference among them names a strictly earlier
            // one, which is what the constructor enforces.
            var graph = Fixture("PositionalCycle.bin");
            var a = Root(graph);

            var aPair = Assert.IsType<OldPositionalNode>(MemberOf(a, "Pair"));
            var b = Value(graph, aPair.Items[0]);
            Assert.IsType<OldObjectNode>(b);
            Assert.NotSame(a, b);
            Assert.Equal((object)2u, ((OldScalar)MemberOf((OldObjectNode)b, "Sentinel")).Value);

            // One of the two occurrences of each position is the value and the other is a reference to it;
            // which one the writer's member order made first is not this graph's business, so the
            // claim is that both directions resolve to the same two nodes.
            var bPair = Assert.IsType<OldPositionalNode>(MemberOf((OldObjectNode)b, "Pair"));
            Assert.Same(b, Value(graph, aPair.Items[0]));
            Assert.Same(a, Value(graph, bPair.Items[0]));

            // The same through a tuple's item, which is the other reference position.
            var aTuple = Assert.IsType<OldPositionalNode>(MemberOf(a, "Tup"));
            var bTuple = Assert.IsType<OldPositionalNode>(MemberOf((OldObjectNode)b, "Tup"));
            Assert.Same(b, Value(graph, aTuple.Items[0]));
            Assert.Same(a, Value(graph, bTuple.Items[0]));

            // The cycle itself, stated as reachability rather than as a count: `a` and `b` are each other's
            // reachable value through a position the file recorded no member name for, and the four
            // positions between them hold two values, not four copies of two.
            Assert.Same(a, Value(graph, bPair.Items[0]));
            Assert.Same(b, Value(graph, aPair.Items[0]));
            Assert.NotSame(a, b);

            var positions = new[] { aPair.Items[0], bPair.Items[0], aTuple.Items[0], bTuple.Items[0] };
            Assert.Equal(2, positions.Select(p => graph.IndexOf(Value(graph, p))).Distinct().Count());

            // Every reference names a strictly earlier index, which is the rule the graph's own
            // constructor enforces over the arena the builder produced — and the one a decoder that
            // read a writer-cache slot as an arena index would break while still satisfying every other
            // rule, since both index spaces are small here and can coincide by accident.
            foreach (var reference in Enumerate<OldReferenceNode>(graph))
                Assert.True(reference.TargetIndex < graph.IndexOf(reference),
                    $"a back-reference at arena index {graph.IndexOf(reference)} named {reference.TargetIndex}");

            // The two index spaces really are different here, and this is where they show it: a
            // positional value written *inline* — a `KeyValuePair` or a `ValueTuple`, both structs — takes
            // an arena index and no object-cache slot at all. So an arena index exists that no writer
            // slot corresponds to, which is why a slot number must never be read as one.
            var inlinePositional = Enumerate<OldPositionalNode>(graph)
                .Where(p => p.Type.DefinitionTypeId is WellKnownTypes.KeyValuePair)
                .ToArray();
            Assert.Equal(2, inlinePositional.Length);                      // a.Pair and b.Pair
            Assert.All(inlinePositional, p => Assert.True(graph.IndexOf(p) >= 0));
        }

        [Fact]
        public void ACycleCannotCloseThroughANullable()
        {
            // The other half of the same property, and it is why the two kinds are not interchangeable.
            // A `Nullable<T>` is a struct: written inline, with no object-cache slot, and its inner value
            // is written with the *specific* formatter, so no marker stands in front of it whatever its
            // type is. Nothing inside a nullable can name an earlier index, so a cycle through one is not
            // merely unpinned — it is unrepresentable in the format.
            var graph = Fixture("PositionalNullable.bin");
            foreach (var nullable in Enumerate<OldNullableNode>(graph))
                Assert.IsNotType<OldReferenceNode>(nullable.Value);
        }

        // ── 5. The polymorphic and Type-value doors ──────────────────────────────────────────────

        [Fact]
        public void AClassTupleInAPolymorphicSlotTakesItsIdentityFromTheTypeValue()
        {
            // A polymorphic slot records its concrete runtime type as a recursive `System.Type` value, so
            // this tuple has no entry in the file's schema section: its identity is *synthesized* from
            // the type value, which is why the stable name is the type value's own diagnostic spelling
            // and the id is above every manifest id.
            var graph = Fixture("PositionalPolyTuple.bin");
            var crate = Root(graph);

            var tuple = Assert.IsType<OldPositionalNode>(Member(crate, "Slot"));
            Assert.Equal(WellKnownTypes.Tuple2, tuple.Type.DefinitionTypeId);
            Assert.True(tuple.Type.TypeId >= TypeValueShapes.FirstSynthesizedId,
                "a shape the file recorded as a Type value has no manifest id of its own");
            Assert.StartsWith("<type-value:", tuple.Type.StableName);
            Assert.Equal(22, ((OldScalar)tuple.Items[0]).Value);
            Assert.Equal("pt", ((OldScalar)tuple.Items[1]).Value);

            // The sentinel is still the number the writer wrote, which is what says every byte of the
            // polymorphic payload was consumed exactly.
            Assert.Equal(0x2B2B2B2Bu, ((OldScalar)Member(crate, "Sentinel")).Value);
        }

        [Fact]
        public void AStructTupleInAPolymorphicSlotIsStillRecordedPositionally()
        {
            var graph = Fixture("PositionalPolyValueTuple.bin");
            var crate = Root(graph);

            var tuple = Assert.IsType<OldPositionalNode>(Member(crate, "Slot"));
            Assert.Equal(WellKnownTypes.VTuple2, tuple.Type.DefinitionTypeId);
            Assert.Equal(21, ((OldScalar)tuple.Items[0]).Value);
            Assert.Equal("pv", ((OldScalar)tuple.Items[1]).Value);
            Assert.Equal(0x1A1A1A1Au, ((OldScalar)Member(crate, "Sentinel")).Value);
        }

        // ── 6. There is no Rest: a tail is a position holding a tuple of its own ─────────────────

        [Fact]
        public void ATupleTailIsOneMorePositionAndNotAnEighthArgument()
        {
            // The CLR spells a tuple of more than seven elements as a *seven*-argument type whose seventh
            // argument is itself a tuple, and the format's reserved id table stops at arity seven — so no
            // writer can emit an eighth argument for the model to hold. There is no `Rest` field and no
            // `Rest` node kind: the tail is item 6, and it holds a tuple like any other position.
            //
            // (An eight-argument `ValueTuple` does exist as a CLR type and is rejected by the writer's
            // own resolver, so it can never appear in a save; a file carrying one would be describing a
            // shape nothing in this format writes.)
            var graph = Fixture("PositionalTupleWithTail.bin");
            var root = Assert.IsType<OldPositionalNode>(graph.Root);

            Assert.Equal(WellKnownTypes.VTuple7, root.Type.DefinitionTypeId);
            Assert.Equal(7, root.Items.Count);
            Assert.Equal(7, root.Type.ArgumentTypeIds.Count);

            Assert.Equal(new object[] { 31, 32, 33, 34, 35, 36 },
                         root.Items.Take(6).Cast<OldScalar>().Select(s => s.Value).ToArray());

            var tail = Assert.IsType<OldPositionalNode>(root.Items[6]);
            Assert.Equal(WellKnownTypes.VTuple2, tail.Type.DefinitionTypeId);
            Assert.Equal(new object[] { 37, 38 }, tail.Items.Cast<OldScalar>().Select(s => s.Value).ToArray());

            // The tail's own identity is the file's, and it nests: a tail holding a tuple holding an int
            // is three identities deep in the tree, which is what a closed identity records.
            var tailArgument = root.Type.ArgumentTypeIds[6];
            Assert.Equal(WellKnownTypes.VTuple2, tailArgument.DefinitionTypeId);
            Assert.Same(tailArgument, tail.Type);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────

        static OldDataGraph Fixture(string name) => OldFileGraph.Read(HistoricalFixtureFiles.Fixture(name));

        static OldObjectNode Root(OldDataGraph graph) => Assert.IsType<OldObjectNode>(graph.Root);

        static OldValue Member(OldObjectNode node, string name) => MemberOf(node, name);

        static OldValue MemberOf(OldObjectNode node, string name)
            => node.RecordedMembers.Single(m => m.Recorded!.StableName == name).Value;

        /// <summary>Every value of one kind anywhere in the arena, so a claim about a shape is checked
        /// over the whole graph rather than over the one place the test happened to look.</summary>
        static IEnumerable<T> Enumerate<T>(OldDataGraph graph) where T : OldValue
        {
            for (int i = 0; i < graph.NodeCount; i++)
                if (graph.Node(i) is T found)
                    yield return found;
        }

        /// <summary>
        /// The value an occurrence stands for: a back-reference resolved to the node it names, and
        /// anything else itself. Which occurrence of a shared value the writer put first is decided by the
        /// <em>writer's</em> member order, so a claim about sharing is made over the resolved values
        /// rather than over a named member — a reader must not depend on which holder the file happened
        /// to write first.
        /// </summary>
        static OldValue Value(OldDataGraph graph, OldValue occurrence)
            => occurrence is OldReferenceNode reference ? graph.Node(reference.TargetIndex)! : occurrence;
    }
}
