using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Ceras.Format;
using Ceras.Exceptions;
using Xunit;

namespace Ceras.Test
{
    /// <summary>
    /// The old-data graph's node model, built entirely by hand: nothing here decodes a save, because the
    /// model is what a later builder will fill and these are the rules it has to be able to rely on
    /// before that builder exists.
    /// <para>
    /// The four rules under test (see <c>Format/OldDataGraph.cs</c>) are:
    /// <list type="number">
    /// <item>a value's identity is its arena index;</item>
    /// <item>the arena is closed;</item>
    /// <item>the file's record is immutable, and only the four bindings may change;</item>
    /// <item>a back-reference names a strictly earlier index.</item>
    /// </list>
    /// Three of them are asserted over the <em>public surface</em> rather than over one instance,
    /// because a rule that only holds for the values this file happens to build is not a rule: a settable
    /// property added to a node type next month, or an <c>AddNode</c> added to the graph, has to fail a
    /// test rather than pass one.
    /// </para>
    /// </summary>
    public class OldDataGraphModelTests
    {
        // ── Hand-built inputs ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Stands in for the builder a later step will write: every value is appended in the writer's
        /// first-occurrence order, which is what makes that order the arena's index space — and, since the
        /// graph refuses a bound value with no index, every value a member holds has to come from here.
        /// A recorded null is the deliberate exception: it is a fact, not a node, and takes no index.
        /// </summary>
        sealed class Hand
        {
            readonly List<OldValue> _nodes = new();

            public OldTypeIdentity Int32 { get; } =
                new(WellKnownTypes.Int32, "System.Int32", null, SchemaTypeKind.Struct);

            public OldTypeIdentity Str { get; } =
                new(WellKnownTypes.String, "System.String", null, SchemaTypeKind.Struct);

            /// <summary>Appends a value to the arena, as the builder does, and returns it.</summary>
            public T Append<T>(T value) where T : OldValue
            {
                _nodes.Add(value);
                return value;
            }

            public OldScalar Int(int value) => Append(new OldScalar(Int32, value));

            /// <summary>Appends a back-reference, which is a node in its own right.</summary>
            public OldReferenceNode Ref(int targetIndex) => Append(new OldReferenceNode(targetIndex));

            /// <summary>A recorded null. Not appended: a null is a fact, not an identity.</summary>
            public OldNullNode Null(OldNullReason reason) => new(reason);

            /// <summary>Appends an object with one unbound member per value, in order.</summary>
            public OldObjectNode Object(OldTypeIdentity type, params OldValue[] values)
                => Append(new OldObjectNode(type, values.Select(v => new OldMember(null, v)).ToList()));

            /// <summary>Appends a collection of <paramref name="elements"/>.</summary>
            public OldCollectionNode Collection(OldTypeIdentity element, params OldValue[] elements)
                => Append(new OldCollectionNode(
                    Closed("List", WellKnownTypes.List, element), elements.Length, elements, element));

            public OldDataGraph Build(OldValue root = null) => new(_nodes, root);
        }

        /// <summary>A closed well-known instantiation, the way the file records <c>List&lt;int&gt;</c>.</summary>
        static OldTypeIdentity Closed(string stableName, int definitionTypeId, params OldTypeIdentity[] arguments)
            => new(definitionTypeId, stableName, null, SchemaTypeKind.GenericInstantiation, definitionTypeId, arguments);

        /// <summary>A user type as the file's schema section records one.</summary>
        static OldTypeIdentity Old(string stableName, int typeId = 128)
            => new(typeId, stableName, null, SchemaTypeKind.Class);

        // ── Rule 2: the arena is closed ─────────────────────────────────────────────────────────────

        [Fact]
        public void Arena_ExposesNoAppendPathAtAll()
        {
            // "The arena is closed" has to be structural, so this asserts the *whole* declared public
            // surface of the graph by name: any method added later - Add, Append, Reserve, Insert,
            // RemoveNode, AddNode, anything - fails here rather than quietly opening the arena.
            var methods = typeof(OldDataGraph)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)          // property accessors are properties, not operations
                .Where(m => m.DeclaringType != typeof(object))
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    nameof(OldDataGraph.AddMember),
                    nameof(OldDataGraph.CurrentFieldNameOf),
                    nameof(OldDataGraph.DetachMemberValue),
                    nameof(OldDataGraph.IndexOf),
                    nameof(OldDataGraph.Node),
                    nameof(OldDataGraph.RemoveMember),
                    nameof(OldDataGraph.SetMemberValue),
                    nameof(OldDataGraph.SetRoot),
                },
                methods);
        }

        [Fact]
        public void Arena_ExposesNoMutableCollectionNoGenericMethodAndNoByRefParameter()
        {
            // The three other shapes an append path could take without an obviously named method: a
            // member that hands out or accepts the backing list, a by-ref parameter to write through, and
            // a generic method - which is the signature a late "Add<TNode>" would take.
            var members = typeof(OldDataGraph)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is MethodInfo or PropertyInfo or FieldInfo)
                .ToArray();

            Assert.NotEmpty(members);

            foreach (var member in members)
            {
                var type = member switch
                {
                    MethodInfo m => m.ReturnType,
                    PropertyInfo p => p.PropertyType,
                    FieldInfo f => f.FieldType,
                    _ => throw new InvalidOperationException(),
                };

                Assert.False(IsMutableCollection(type), $"{member.Name} is a mutable collection, which a caller could append to.");

                if (member is not MethodInfo method) continue;

                Assert.False(method.IsGenericMethodDefinition, $"{method.Name} is generic, which is the shape a late append would take.");

                foreach (var parameter in method.GetParameters())
                {
                    Assert.False(parameter.ParameterType.IsByRef, $"{method.Name} takes {parameter.Name} by reference.");
                    Assert.False(IsMutableCollection(parameter.ParameterType), $"{method.Name} takes a mutable collection.");
                }
            }
        }

        [Fact]
        public void Arena_IsBuiltOnlyThroughTheConstructorAndRootHasNoPublicSetter()
        {
            var ctors = typeof(OldDataGraph).GetConstructors();

            Assert.Single(ctors);
            Assert.Equal(new[] { typeof(IReadOnlyList<OldValue>), typeof(OldValue) },
                ctors[0].GetParameters().Select(p => p.ParameterType));

            // Root is one of the four mutable bindings, and SetRoot is the only way to change it: a
            // public setter would be a second, unchecked path to the same mutation.
            var root = typeof(OldDataGraph).GetProperty(nameof(OldDataGraph.Root))!;
            Assert.Null(root.GetSetMethod(nonPublic: false));
            Assert.NotNull(typeof(OldDataGraph).GetMethod(nameof(OldDataGraph.SetRoot)));
        }

        [Fact]
        public void Arena_CopiesTheNodeListItWasGiven()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var b = hand.Int(2);

            var nodes = new List<OldValue> { a, b };
            var graph = new OldDataGraph(nodes, a);

            // The caller's list is the builder's own growing list. Appending to it afterwards must not
            // reach into a graph that has already closed.
            nodes.Add(hand.Int(3));

            Assert.Equal(2, graph.NodeCount);
            Assert.Same(a, graph.Node(0));
            Assert.Same(b, graph.Node(1));
            Assert.Null(graph.Node(2));
        }

        [Fact]
        public void Arena_RefusesASlotThatIsNotAValueWithAnIdentity()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var recordedNull = new OldNullNode(OldNullReason.ReferenceMarker);

            // A null is a fact about a value, not an identity: nothing the writer wrote is a
            // back-reference to a null, so a null must not occupy an index.
            var error = Assert.Throws<ArgumentException>(() => new OldDataGraph(new OldValue[] { a, recordedNull }, a));
            Assert.Contains("null", error.Message, StringComparison.OrdinalIgnoreCase);

            // One value, one index: the same instance in two slots would give it two identities.
            Assert.Throws<ArgumentException>(() => new OldDataGraph(new OldValue[] { a, a }, a));
        }

        [Fact]
        public void Arena_RefusesARootItDoesNotHold()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var foreign = new OldScalar(hand.Int32, 9);

            Assert.Throws<ArgumentException>(() => new OldDataGraph(new OldValue[] { a }, foreign));
        }

        [Fact]
        public void Arena_RefusesAValueInAMemberOrCollectionThatHasNoIndex()
        {
            // Rule 1 over the whole graph, not just the arena: a value in a value slot that is not a node
            // has no identity, so the graph must not be constructible with one.
            var hand = new Hand();
            var foreign = new OldScalar(hand.Int32, 9);
            var obj = new OldObjectNode(Old("Old.Player"), new List<OldMember> { new(null, foreign) });

            var error = Assert.Throws<ArgumentException>(() => new OldDataGraph(new OldValue[] { obj }, obj));
            Assert.Contains("index", error.Message, StringComparison.OrdinalIgnoreCase);

            // A collection's entries are value slots too.
            var hand2 = new Hand();
            var list = new OldCollectionNode(Closed("List", WellKnownTypes.List, hand2.Int32), 1,
                new OldValue[] { new OldScalar(hand2.Int32, 9) }, hand2.Int32);

            Assert.Throws<ArgumentException>(() => new OldDataGraph(new OldValue[] { list }, list));
        }

        /// <summary>
        /// A value kind the model does not declare cannot be walked, and the constructor says so instead
        /// of accepting it. The walk is the model's only list of which kinds hold values, so a kind
        /// missing from it would contribute no children: every value it held would then be reachable in
        /// the graph with no index — the break of rule 1 the arena exists to prevent — and nothing
        /// downstream would notice, because a value with no identity still looks like a value. This is
        /// also what stops the next node kind from being added and quietly escaping validation.
        /// </summary>
        [Fact]
        public void Arena_RefusesAValueKindTheValidationWalkDoesNotKnow()
        {
            var unknown = new UnknownValue();

            var error = Assert.Throws<CerasException>(() => new OldDataGraph(new OldValue[] { unknown }, unknown));
            Assert.Contains(nameof(UnknownValue), error.Message);
            Assert.Contains("ValidateEveryValueIsIndexed", error.Message);
        }

        [Fact]
        public void IndexOf_AnswersNoIndexForAValueThatIsNotInTheArena()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var graph = hand.Build(a);

            Assert.Equal(0, graph.IndexOf(a));
            Assert.Equal(-1, graph.IndexOf(new OldNullNode(OldNullReason.TypeValueNull)));
            Assert.Equal(-1, graph.IndexOf(new OldScalar(hand.Int32, 1)));
            Assert.Throws<ArgumentNullException>(() => graph.IndexOf(null!));
        }

        [Fact]
        public void IndexOf_KeysOnReferenceIdentityNotOnValueEquality()
        {
            var hand = new Hand();
            var a = hand.Int(7);
            var b = hand.Int(7);
            var graph = hand.Build(a);

            // Two values that are indistinguishable by content are two values: were the arena's key
            // value equality, the second would answer with the first one's index, and anything that
            // made two values equal (a future Equals, a rename) would move a value.
            Assert.NotEqual(graph.IndexOf(a), graph.IndexOf(b));
            Assert.Same(a, graph.Node(graph.IndexOf(a)));
            Assert.Same(b, graph.Node(graph.IndexOf(b)));
        }

        // ── Rule 1: identity is the index ───────────────────────────────────────────────────────────

        [Fact]
        public void IndexIdentity_SurvivesRenamingTheTypeAndTheField()
        {
            // The file called the type "Old.Player" and the field "Hp"; this build calls them "Player"
            // and "Health". Every name moves, and the value must not.
            var hand = new Hand();
            var hp = hand.Int(30);
            var player = hand.Object(Old("Old.Player"), hp);
            var graph = hand.Build(player);

            int hpIndex = graph.IndexOf(hp);
            int playerIndex = graph.IndexOf(player);

            player.Members[0].CurrentFieldName = "Health";

            Assert.Equal(playerIndex, graph.IndexOf(player));
            Assert.Equal(hpIndex, graph.IndexOf(hp));
            Assert.Same(player, graph.Node(playerIndex));
            Assert.Same(hp, player.Members[0].Value);

            // The file's record still says what the file said; the rename lives only in the binding.
            Assert.Equal("Old.Player", player.Type.StableName);
            Assert.Equal("Health", player.Members[0].CurrentFieldName);

            // A rename does not merge two values either. An object recorded under a different type name
            // is a different value with its own index, and appending it moves nothing.
            var heroHp = hand.Int(30);
            var hero = hand.Object(Old("Old.Hero"), heroHp);
            var bigger = hand.Build(hero);

            Assert.Equal(3, bigger.IndexOf(hero));
            Assert.Equal(playerIndex, bigger.IndexOf(player));
            Assert.Equal(hpIndex, bigger.IndexOf(hp));
            Assert.Equal(4, bigger.NodeCount);
            Assert.Equal(2, graph.NodeCount);          // the first graph never saw it
        }

        [Fact]
        public void IndexIdentity_IgnoresAValuesPositionInTheMemberList()
        {
            var hand = new Hand();
            var first = hand.Int(1);
            var second = hand.Int(2);
            var obj = hand.Object(Old("Old.Player"), first, second);
            var graph = hand.Build(obj);

            Assert.Equal(0, graph.IndexOf(first));
            Assert.Equal(1, graph.IndexOf(second));
            Assert.Equal(2, graph.IndexOf(obj));

            // Every reshaping a transform does moves values between positions; none of it is an index.
            var removed = graph.RemoveMember(obj, 0);
            graph.AddMember(obj, "Inlined", second);

            Assert.Equal(0, graph.IndexOf(first));
            Assert.Equal(1, graph.IndexOf(second));
            Assert.Equal(2, graph.IndexOf(obj));
            Assert.Same(second, obj.Members[0].Value);
            Assert.Null(OldDataGraph.CurrentFieldNameOf(removed));
        }

        // ── Rule 4: back-reference ordering ────────────────────────────────────────────────────────

        [Fact]
        public void BackReference_MustNameAStrictlyEarlierIndex()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var b = hand.Int(2);

            // Forward, and onto itself: the writer writes a value before anything that can point back at
            // it, so a reference always names an index before its own.
            Assert.Throws<CerasDataCorruptionException>(() =>
                new OldDataGraph(new OldValue[] { a, new OldReferenceNode(2) }, a));

            Assert.Throws<CerasDataCorruptionException>(() =>
                new OldDataGraph(new OldValue[] { a, b, new OldReferenceNode(2) }, a));

            // Backwards is the only order a writer produces.
            var graph = new OldDataGraph(new OldValue[] { a, b, new OldReferenceNode(1) }, a);
            var reference = Assert.IsType<OldReferenceNode>(graph.Node(2));

            Assert.Equal(1, reference.TargetIndex);
            Assert.Same(b, graph.Node(reference.TargetIndex));
        }

        [Fact]
        public void BackReference_ResolvesToTheFirstOccurrenceAndCarriesACycle()
        {
            // The shape the milestone exists for: a parent and a child that point at each other, plus an
            // object that is a *later occurrence* of both. The arena order is the writer's: the parent is
            // written first and takes a slot, the child is written inside it, and the child refers back to
            // the parent rather than writing it again.
            var hand = new Hand();

            var childOfParent = new OldObjectNode(Old("Old.Node"), new List<OldMember>());
            hand.Append(childOfParent);
            int childIndex = hand.Build().IndexOf(childOfParent);

            var parent = hand.Append(new OldObjectNode(Old("Old.Node"), new List<OldMember> { new(null, childOfParent) }));
            int parentIndex = hand.Build().IndexOf(parent);
            childOfParent.Members.Add(new OldMember(null, hand.Ref(parentIndex)));   // child -> parent

            // A later occurrence of each: two more members, both back-references.
            var repeat = hand.Append(new OldObjectNode(Old("Old.Node"), new List<OldMember>
            {
                new(null, hand.Ref(parentIndex)),   // the parent again
                new(null, hand.Ref(childIndex)),    // the child again
            }));

            var graph = hand.Build(parent);

            Assert.Same(parent, graph.Node(parentIndex));
            Assert.Same(childOfParent, graph.Node(childIndex));

            // The cycle is real: the parent's child is the node, and that node's member is the
            // back-reference that names the parent.
            Assert.Same(childOfParent, parent.Members[0].Value);
            var backToParent = (OldReferenceNode)childOfParent.Members[0].Value;
            Assert.Same(parent, graph.Node(backToParent.TargetIndex));

            // And the repeat names both first occurrences, not copies of them.
            Assert.Same(parent, graph.Node(((OldReferenceNode)repeat.Members[0].Value).TargetIndex));
            Assert.Same(childOfParent, graph.Node(((OldReferenceNode)repeat.Members[1].Value).TargetIndex));
        }

        [Fact]
        public void BackReference_OrderingCannotBeBrokenByTheMutationVocabulary()
        {
            // Rule 4 rests on rule 2 and rule 3 together: the arena cannot grow, and a target index is
            // immutable, so no re-pointing can ever produce a forward reference. The whole vocabulary is
            // walked here rather than argued.
            var hand = new Hand();
            var a = hand.Int(1);
            var b = hand.Int(2);
            var reference = hand.Ref(0);
            var obj = hand.Object(Old("Old.Player"), reference);
            var graph = hand.Build(obj);

            graph.RemoveMember(obj, 0);
            graph.AddMember(obj, "Moved", reference);
            graph.SetMemberValue(obj, 0, b);
            graph.SetMemberValue(obj, 0, reference);
            graph.SetRoot(reference);

            int referenceIndex = graph.IndexOf(reference);
            Assert.True(reference.TargetIndex < referenceIndex,
                $"A back-reference must name an earlier index, not {reference.TargetIndex} from {referenceIndex}.");
        }

        [Fact]
        public void BackReference_RefusesANegativeTarget()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new OldReferenceNode(-1));
        }

        // ── Rule 3: the file's record is immutable ─────────────────────────────────────────────────

        [Fact]
        public void FileRecord_HasNoSetterAnywhereInTheModel()
        {
            // Exactly two settable properties exist in the whole model, and both are bindings. A new one
            // is a new mutable fact about the file's record, so it has to be argued for in a test.
            var settable = ModelTypes()
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(p => p.GetSetMethod(nonPublic: false) != null)
                .Select(p => p.DeclaringType!.Name + "." + p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "OldMember.CurrentFieldName", "OldMember.Value" }, settable);
        }

        [Fact]
        public void FileRecord_CollectionsAreCopiesAndCannotBeWrittenThrough()
        {
            var previousNames = new List<string> { "OldName" };
            var arguments = new List<OldTypeIdentity> { new(WellKnownTypes.Int32, "System.Int32", null, SchemaTypeKind.Struct) };
            var identity = new OldTypeIdentity(128, "Old.Player", previousNames, SchemaTypeKind.Class, 0, arguments);
            var field = new OldFieldIdentity("Hp", previousNames, identity);
            var recordedMembers = new List<OldMember> { new(field, new OldScalar(identity, 1)) };
            var obj = new OldObjectNode(identity, recordedMembers);
            var elements = new List<OldValue> { new OldScalar(identity, 1) };
            var keys = new List<OldValue> { new OldScalar(identity, 1) };
            var values = new List<OldValue> { new OldScalar(identity, 2) };
            var dictionary = new OldCollectionNode(Closed("Dictionary", WellKnownTypes.Dictionary, identity, identity),
                1, elements, identity, keys, values, identity, identity);

            // The caller's lists are the builder's own; the graph holds copies.
            previousNames.Add("AddedAfterwards");
            arguments.Add(new OldTypeIdentity(WellKnownTypes.Int64, "System.Int64", null, SchemaTypeKind.Struct));
            recordedMembers.Clear();
            elements.Clear();
            keys.Clear();
            values.Clear();

            Assert.Single(identity.PreviousNames);
            Assert.Single(identity.ArgumentTypeIds);
            Assert.Single(field.PreviousNames);
            Assert.Single(obj.RecordedMembers);
            Assert.Single(dictionary.Elements);
            Assert.Single(dictionary.Keys!);
            Assert.Single(dictionary.Values!);

            // Get-only is not the same as immutable: the stored list has to be a read-only view too.
            Assert.Throws<NotSupportedException>(() => ((IList<string>)identity.PreviousNames).Add("x"));
            Assert.Throws<NotSupportedException>(() => ((IList<OldTypeIdentity>)identity.ArgumentTypeIds).Add(identity));
            Assert.Throws<NotSupportedException>(() => ((IList<OldMember>)obj.RecordedMembers).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<OldValue>)dictionary.Elements).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<OldValue>)dictionary.Keys!).Clear());
        }

        [Fact]
        public void RecordedMembers_AreTheAuditTrailAndAreNeverEdited()
        {
            var hand = new Hand();
            var hp = hand.Int(30);
            var name = hand.Int(1);
            var player = hand.Object(Old("Old.Player"), hp, name);
            var graph = hand.Build(player);

            var removed = graph.RemoveMember(player, 0);              // the field is gone from the current type
            var added = graph.AddMember(player, "Inlined", name);     // a value the file recorded elsewhere

            // The file's record of what *this* object held is untouched by either: the removed member is
            // still in it, and the synthesized one never was.
            Assert.Equal(2, player.RecordedMembers.Count);
            Assert.Same(removed, player.RecordedMembers[0]);
            Assert.Null(removed.Recorded);                            // this fixture bound nothing
            Assert.Null(added.Recorded);
            Assert.NotSame(added, player.RecordedMembers[1]);

            Assert.Equal(2, player.Members.Count);
            Assert.Same(added, player.Members[1]);
            Assert.Equal("Inlined", OldDataGraph.CurrentFieldNameOf(added));
        }

        [Fact]
        public void CollectionNode_KeepsTheFilesOwnElementIdentity()
        {
            // A save whose List<Player> member is now a List<Actor>: the graph must still be able to say
            // what the file said, and must not answer with the current model's type.
            var fileSaysPlayer = Old("Old.Player");
            var list = new OldCollectionNode(Closed("List<Old.Player>", WellKnownTypes.List, fileSaysPlayer), 1,
                new OldValue[] { new OldScalar(fileSaysPlayer, 1) }, fileSaysPlayer);

            Assert.Same(fileSaysPlayer, list.ElementType);
            Assert.Equal(WellKnownTypes.List, list.Type.DefinitionTypeId);
            Assert.Null(list.KeyType);
            Assert.Null(list.Keys);
            Assert.False(list.IsNullCollection);
            Assert.Equal(1, list.Count);
        }

        [Fact]
        public void CollectionNode_RefusesHalfADictionary()
        {
            var identity = Old("Old.Player");
            var element = new OldScalar(identity, 1);

            // Keys and values are one fact about one dictionary. Half of it is a record the file never
            // wrote, and reading it as "a dictionary with no values" would be silent data loss. The
            // collection node itself is never built here — the constructor is the only door, and it is
            // this door.
            Assert.Throws<ArgumentException>(() => new OldCollectionNode(
                Closed("Dictionary", WellKnownTypes.Dictionary, identity, identity), 1,
                Array.Empty<OldValue>(), identity, keys: new OldValue[] { element }));

            Assert.Throws<ArgumentException>(() => new OldCollectionNode(
                Closed("Dictionary", WellKnownTypes.Dictionary, identity, identity), 1,
                Array.Empty<OldValue>(), identity, values: new OldValue[] { element }));

            Assert.Throws<ArgumentException>(() => new OldCollectionNode(
                Closed("Dictionary", WellKnownTypes.Dictionary, identity, identity), 2,
                Array.Empty<OldValue>(), identity,
                new OldValue[] { element, element }, new OldValue[] { element }));
        }

        [Fact]
        public void CollectionNode_RefusesACountTheWriterCannotHaveWritten()
        {
            var identity = Old("Old.Player");

            Assert.Throws<ArgumentOutOfRangeException>(() => new OldCollectionNode(
                Closed("List", WellKnownTypes.List, identity), -2, Array.Empty<OldValue>(), identity));

            // A queue and a stack write an unbiased count, so -1 is a corrupt count there and never a
            // null collection. That is what makes "impossible for queue/stack" structural.
            var queue = new OldCollectionNode(Closed("Queue", WellKnownTypes.Queue, identity), 0,
                Array.Empty<OldValue>(), identity);
            Assert.False(queue.IsNullCollection);

            Assert.Throws<ArgumentOutOfRangeException>(() => new OldCollectionNode(
                Closed("Queue", WellKnownTypes.Queue, identity), -1, Array.Empty<OldValue>(), identity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OldCollectionNode(
                Closed("Stack", WellKnownTypes.Stack, identity), -1, Array.Empty<OldValue>(), identity));

            // A -1 count says no elements follow, so elements beside it are a record the file never made.
            Assert.Throws<ArgumentException>(() => new OldCollectionNode(
                Closed("List", WellKnownTypes.List, identity), -1, new OldValue[] { new OldScalar(identity, 1) }, identity));
        }

        [Fact]
        public void EnumNode_KeepsRawBitsAndTheFilesUnderlyingTypeId()
        {
            var enumType = new OldTypeIdentity(129, "Old.Direction", null, SchemaTypeKind.Enum, WellKnownTypes.Int32);
            var node = new OldEnumValueNode(enumType, ulong.MaxValue, WellKnownTypes.Int32);

            // Bits above long.MaxValue survive, and they read back as the FILE's underlying type - not as
            // whatever the current enum's underlying type happens to be after a widening.
            Assert.Equal(ulong.MaxValue, node.RawBits);
            Assert.Equal(WellKnownTypes.Int32, node.UnderlyingTypeId);
            Assert.Equal(WellKnownTypes.Int32, node.Type.DefinitionTypeId);
        }

        [Fact]
        public void TypeValueNode_KeepsTheFilesSpellingEvenWhenNothingResolvesIt()
        {
            var node = new OldTypeValueNode(Old("Old.Gone"), null, "MyGame.Removed.Type");

            // A removed type's name is the only trace the file left of it, and a migration report has to
            // be able to print it. The current type is a fact about this build and stays out of the
            // identity.
            Assert.Equal("MyGame.Removed.Type", node.RecordedFullName);
            Assert.Null(node.CurrentType);
            Assert.Equal("Old.Gone", node.Type.StableName);
        }

        [Fact]
        public void TypeValueNode_CarriesAClosedIdentityRecursively()
        {
            // List<List<int>> is List, one argument, List, one argument, Int32: the writer's Type value
            // grammar is recursive, and the recorded identity has to be too.
            var listOfLists = new OldTypeValueNode(
                Closed("List<List<System.Int32>>", WellKnownTypes.List,
                    Closed("List<System.Int32>", WellKnownTypes.List,
                        new OldTypeIdentity(WellKnownTypes.Int32, "System.Int32", null, SchemaTypeKind.Struct))),
                typeof(System.Collections.Generic.List<System.Collections.Generic.List<int>>));

            var inner = Assert.Single(listOfLists.Type.ArgumentTypeIds);
            Assert.Equal(WellKnownTypes.List, inner.DefinitionTypeId);
            var element = Assert.Single(inner.ArgumentTypeIds);
            Assert.Equal(WellKnownTypes.Int32, element.TypeId);
        }

        /// <summary>
        /// A framed payload, and the four facts the file states about one: the type's identity, the
        /// formatter's name, whether a marker stood in front of the frame, and the length the frame
        /// declared — plus the bytes themselves, copied so the node does not alias the caller's buffer.
        /// </summary>
        [Fact]
        public void OpaqueNode_KeepsTheFilesOwnRecordAndCopiesTheBlock()
        {
            var identity = new OldTypeIdentity(128, "Old.Ref", null, SchemaTypeKind.Opaque);
            var bytes = new byte[] { 0xA2, 0x01, 0x00, 0x00, 0x00 };

            var node = new OldOpaqueNode(identity, "Old.RefFormatter", writtenInline: true, declaredLength: bytes.Length, bytes);

            Assert.Same(identity, node.Type);
            Assert.Equal(SchemaTypeKind.Opaque, node.Type.Kind);
            Assert.Equal("Old.RefFormatter", node.FormatterName);
            Assert.True(node.WrittenInline);
            Assert.Equal(5, node.DeclaredLength);
            Assert.Equal(bytes, node.Payload.ToArray());

            // The copy is the point: the caller is the builder, and its buffer is the save file.
            Array.Fill(bytes, (byte)0xFF);
            Assert.Equal(new byte[] { 0xA2, 0x01, 0x00, 0x00, 0x00 }, node.Payload.ToArray());

            // Get-only is not the same as immutable, and the property type is the other half: a byte[], a
            // Memory<byte> or even a ReadOnlyMemory<byte> here would be a door a caller could write the
            // file's record through, the last one via MemoryMarshal.TryGetArray.
            var payload = typeof(OldOpaqueNode).GetProperty(nameof(OldOpaqueNode.Payload))!;
            Assert.Equal(typeof(IReadOnlyList<byte>), payload.PropertyType);
            Assert.Null(payload.GetSetMethod(nonPublic: false));
        }

        /// <summary>
        /// The payload is bytes, and bytes are the one thing a public API can hand out in a shape a caller
        /// can write through: a <c>byte[]</c> is not read-only at all, and a
        /// <see cref="ReadOnlyMemory{T}"/> of bytes yields its array to <c>MemoryMarshal.TryGetArray</c>.
        /// So this is a claim about every member of the node, not about the one property — a node whose
        /// payload were typed as a writable buffer again would have to fail here rather than pass.
        /// </summary>
        [Fact]
        public void OpaqueNode_ExposesNoWritableByteBufferThroughAnyPublicMember()
        {
            foreach (var member in typeof(OldOpaqueNode)
                         .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => m is PropertyInfo or MethodInfo or FieldInfo))
            {
                foreach (var type in NamedTypesOf(member))
                {
                    Assert.False(IsWritableByteBuffer(type),
                        $"{member.Name} exposes {type}, which a caller could write the file's record through.");
                }
            }
        }

        /// <summary>
        /// The two ways a byte payload could be reached for writing through a list-shaped property, both
        /// refused by the read-only view over the copy: the mutable interfaces the wrapper implements, and
        /// a cast back to the array behind it. Neither changes the node's bytes.
        /// </summary>
        [Fact]
        public void OpaqueNode_CannotBeWrittenThroughAsAMutableList()
        {
            var identity = new OldTypeIdentity(128, "Old.Ref", null, SchemaTypeKind.Opaque);
            var bytes = new byte[] { 0xA2, 0x01, 0x00, 0x00, 0x00 };
            var node = new OldOpaqueNode(identity, "Old.RefFormatter", false, bytes.Length, bytes);

            var mutable = (IList<byte>)node.Payload;
            Assert.Throws<NotSupportedException>(() => mutable[0] = 0xFF);
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
            Assert.Throws<NotSupportedException>(() => mutable.Add(0xFF));

            // And there is no array behind it to cast back to: the wrapper is the object, not the array.
            Assert.IsType<ReadOnlyCollection<byte>>(node.Payload);
            Assert.Throws<InvalidCastException>(() => (byte[])(object)node.Payload);

            Assert.Equal(new byte[] { 0xA2, 0x01, 0x00, 0x00, 0x00 }, node.Payload.ToArray());
        }

        /// <summary>Every type a member names, one level deep, so a generic argument cannot smuggle one in.</summary>
        static IEnumerable<Type> NamedTypesOf(MemberInfo member)
        {
            var declared = member switch
            {
                PropertyInfo p => p.PropertyType,
                MethodInfo m   => m.ReturnType,
                FieldInfo f    => f.FieldType,
                _              => null,
            };

            if (declared == null) yield break;

            yield return declared;
            if (!declared.IsGenericType) yield break;
            foreach (var argument in declared.GetGenericArguments()) yield return argument;
        }

        /// <summary>
        /// A shape that writes: the array itself, a span or memory over it, the read-only memory that
        /// <c>MemoryMarshal.TryGetArray</c> unwraps, a segment into it, or the mutable list interfaces.
        /// </summary>
        static bool IsWritableByteBuffer(Type type)
            => type == typeof(byte[])
            || type == typeof(Memory<byte>)
            || type == typeof(ReadOnlyMemory<byte>)
            || type == typeof(ArraySegment<byte>)
            || type == typeof(IList<byte>)
            || type == typeof(ICollection<byte>);

        /// <summary>
        /// The records a Ceras writer cannot have produced, refused at the only door there is — the
        /// constructor. A declared length is written from the number of bytes the formatter produced, so
        /// it is never negative and always matches the block in front of it, and a formatter's canonical
        /// name is a CLR type name, so it is never empty.
        /// </summary>
        [Fact]
        public void OpaqueNode_RefusesARecordTheWriterCouldNotHaveProduced()
        {
            var identity = new OldTypeIdentity(128, "Old.Ref", null, SchemaTypeKind.Opaque);

            // The length is the only thing that accounts for a block, so the two are one record.
            Assert.Throws<ArgumentException>(() => new OldOpaqueNode(identity, "Old.RefFormatter", false, 4, new byte[] { 1, 2, 3 }));
            Assert.Throws<ArgumentException>(() => new OldOpaqueNode(identity, "Old.RefFormatter", false, 2, new byte[] { 1, 2, 3 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OldOpaqueNode(identity, "Old.RefFormatter", false, -1, Array.Empty<byte>()));

            // An entry with no formatter name attributes the block to nothing, so there is nothing to
            // record. Zero, though, is a real length: a formatter is free to have written nothing.
            var error = Assert.Throws<ArgumentException>(() => new OldOpaqueNode(identity, "", false, 0, Array.Empty<byte>()));
            Assert.Contains("no formatter", error.Message);
            Assert.NotNull(new OldOpaqueNode(identity, "Old.RefFormatter", false, 0, Array.Empty<byte>()));

            Assert.Throws<ArgumentNullException>(() => new OldOpaqueNode(null, "Old.RefFormatter", false, 0, Array.Empty<byte>()));
            Assert.Throws<ArgumentNullException>(() => new OldOpaqueNode(identity, null!, false, 0, Array.Empty<byte>()));

            // A null payload is not an empty block: a caller that passed nothing has failed to pass one.
            Assert.Throws<ArgumentNullException>(() => new OldOpaqueNode(identity, "Old.RefFormatter", false, 0, null!));
        }

        /// <summary>
        /// An empty block is a value, and it is wrapped like any other — a shared empty array behind an
        /// <c>IReadOnlyList&lt;byte&gt;</c> would be the one payload a caller could cast back to a writable
        /// <c>byte[]</c>, so the node allocates even when there is nothing to copy.
        /// </summary>
        [Fact]
        public void OpaqueNode_WrapsAnEmptyBlockRatherThanHandingOutAnArray()
        {
            var identity = new OldTypeIdentity(128, "Old.Ref", null, SchemaTypeKind.Opaque);
            var node = new OldOpaqueNode(identity, "Old.RefFormatter", false, 0, Array.Empty<byte>());

            Assert.Equal(0, node.DeclaredLength);
            Assert.IsType<ReadOnlyCollection<byte>>(node.Payload);
            Assert.Throws<InvalidCastException>(() => (byte[])(object)node.Payload);
        }

        // ── Rule 3: the bindings do change ──────────────────────────────────────────────────────────

        [Fact]
        public void Bindings_AreTheOnlyMutablePart()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var b = hand.Int(2);
            var obj = hand.Object(Old("Old.Player"), a);
            var graph = hand.Build(obj);

            var member = obj.Members[0];
            Assert.False(member.IsRetained);

            member.CurrentFieldName = "Player";
            Assert.True(member.IsRetained);
            Assert.Equal("Player", OldDataGraph.CurrentFieldNameOf(member));

            graph.SetMemberValue(obj, 0, b);
            Assert.Same(b, member.Value);

            // Detaching takes the value out and leaves the member in place, bound to nothing. This is
            // what reparenting needs: the arena is closed, so a value can only be moved, and the member
            // it came from has to be left holding a fact of its own.
            var taken = graph.DetachMemberValue(obj, 0);
            Assert.Same(b, taken);
            Assert.Single(obj.Members);
            Assert.True(member.IsRetained);
            var detached = Assert.IsType<OldNullNode>(member.Value);
            Assert.Equal(OldNullReason.MemberDetached, detached.Reason);
            Assert.Equal(-1, graph.IndexOf(detached));

            graph.RemoveMember(obj, 0);
            Assert.Empty(obj.Members);
            Assert.Single(obj.RecordedMembers);
        }

        [Fact]
        public void Bindings_RefuseAValueWithNoIndex()
        {
            var hand = new Hand();
            var obj = hand.Object(Old("Old.Player"), hand.Int(1));
            var graph = hand.Build(obj);

            // A value that is not in the arena has no identity, so it could never be the target of a
            // back-reference. The closed arena is what makes that true, and this is where it is enforced
            // rather than discovered by a materializer later.
            var foreign = new OldScalar(hand.Int32, 9);
            Assert.Throws<ArgumentException>(() => graph.SetMemberValue(obj, 0, foreign));
            Assert.Throws<ArgumentException>(() => graph.AddMember(obj, "New", foreign));
            Assert.Throws<ArgumentException>(() => graph.SetRoot(foreign));

            // A recorded null is the one value with no index, and it is bindable for exactly that reason.
            var recordedNull = new OldNullNode(OldNullReason.NullablePresenceByte);
            graph.SetMemberValue(obj, 0, recordedNull);
            Assert.Same(recordedNull, obj.Members[0].Value);
        }

        [Fact]
        public void Bindings_RefuseAnObjectFromAnotherGraph()
        {
            var one = new Hand();
            var oneObject = one.Object(Old("Old.Player"), one.Int(1));
            var oneGraph = one.Build(oneObject);

            var two = new Hand();
            var twoObject = two.Object(Old("Old.Player"), two.Int(2));
            var twoGraph = two.Build(twoObject);

            // Structurally identical, different graphs. Bindings are per-graph, and accepting this would
            // put a value with no index into the first graph through the back door.
            Assert.Throws<ArgumentException>(() => oneGraph.AddMember(twoObject, "New", twoObject));
            Assert.Throws<ArgumentException>(() => oneGraph.SetMemberValue(twoObject, 0, twoObject));
            Assert.Throws<ArgumentException>(() => oneGraph.RemoveMember(twoObject, 0));
            Assert.Throws<ArgumentException>(() => oneGraph.DetachMemberValue(twoObject, 0));
            Assert.Throws<ArgumentException>(() => oneGraph.SetRoot(twoObject));
        }

        [Fact]
        public void Bindings_RefuseASecondMemberForOneCurrentField()
        {
            var hand = new Hand();
            var obj = hand.Object(Old("Old.Player"), hand.Int(1), hand.Int(2));
            var name = hand.Int(3);
            var graph = hand.Build(obj);

            obj.Members[0].CurrentFieldName = "Health";
            graph.AddMember(obj, "Name", name);

            // Two members claiming one current field would leave a materializer with two candidates and
            // no way to choose, so it is refused rather than resolved arbitrarily later.
            var error = Assert.Throws<ArgumentException>(() => graph.AddMember(obj, "Health", obj.Members[0].Value));
            Assert.Contains("Health", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Bindings_RefuseAMemberIndexOutsideTheProjection()
        {
            var hand = new Hand();
            var only = hand.Int(1);
            var spare = hand.Int(2);
            var obj = hand.Object(Old("Old.Player"), only);
            var graph = hand.Build(obj);

            Assert.Throws<ArgumentOutOfRangeException>(() => graph.SetMemberValue(obj, 1, spare));
            Assert.Throws<ArgumentOutOfRangeException>(() => graph.RemoveMember(obj, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => graph.DetachMemberValue(obj, 7));

            // The refusals changed nothing.
            Assert.Single(obj.Members);
            Assert.Same(only, obj.Members[0].Value);
        }

        [Fact]
        public void Root_IsAMutableBindingAndNullIsAValueTheSaveCanCarry()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var b = hand.Int(2);
            var graph = hand.Build(a);

            Assert.Same(a, graph.Root);

            graph.SetRoot(b);
            Assert.Same(b, graph.Root);

            // A constructed graph is never missing a root, so a null root means exactly one thing: the
            // save's root value was null. It is not "nothing was decoded".
            graph.SetRoot(null);
            Assert.Null(graph.Root);
            Assert.Equal(2, graph.NodeCount);
        }

        [Fact]
        public void Node_AnswersNullOutsideTheArena()
        {
            var hand = new Hand();
            var a = hand.Int(1);
            var graph = hand.Build(a);

            Assert.Same(a, graph.Node(0));
            Assert.Null(graph.Node(-1));
            Assert.Null(graph.Node(1));
            Assert.Null(graph.Node(int.MaxValue));
            Assert.Equal(1, graph.NodeCount);
        }

        // ── A null is a recorded fact with a reason ────────────────────────────────────────────────

        [Fact]
        public void Null_IsRecordedWithTheReasonItWasWritten()
        {
            var hand = new Hand();

            // Three ways the file says "null" with no type identity, one way it says it with one, and
            // the one way this build says it. The three untyped ones are facts and take no index; the two
            // typed ones are values like any other, because the file still said what they were.
            var nullReference = hand.Null(OldNullReason.ReferenceMarker);
            var nullNullable = hand.Null(OldNullReason.NullablePresenceByte);
            var nullTypeValue = hand.Null(OldNullReason.TypeValueNull);
            var nullString = hand.Append(new OldScalar(hand.Str, null));            // string-table index 0
            var nullList = hand.Append(new OldCollectionNode(
                Closed("List<Old.Player>", WellKnownTypes.List, Old("Old.Player")),
                Protocol.CollectionNull, Array.Empty<OldValue>(), Old("Old.Player")));

            var obj = hand.Object(Old("Old.Player"), nullReference, nullNullable, nullTypeValue, nullString, nullList);
            var graph = hand.Build(obj);

            var detached = graph.DetachMemberValue(obj, 4);
            Assert.NotNull(detached);

            // Every one of them is distinguishable from every other, by kind and by reason.
            Assert.Equal(OldNullReason.ReferenceMarker, ((OldNullNode)obj.Members[0].Value).Reason);
            Assert.Equal(OldNullReason.NullablePresenceByte, ((OldNullNode)obj.Members[1].Value).Reason);
            Assert.Equal(OldNullReason.TypeValueNull, ((OldNullNode)obj.Members[2].Value).Reason);
            Assert.Equal(OldNullReason.MemberDetached, ((OldNullNode)obj.Members[4].Value).Reason);

            // A null string is a scalar with a null payload, not a null reference: the file wrote a table
            // index there, and it still knows the member was a string.
            var scalar = Assert.IsType<OldScalar>(obj.Members[3].Value);
            Assert.Null(scalar.Value);
            Assert.Equal(WellKnownTypes.String, scalar.Type.TypeId);
            Assert.True(graph.IndexOf(scalar) >= 0);

            // A null collection keeps the identity of *what* was null, which an identity-free null node
            // would have thrown away - and it is impossible for a queue or a stack.
            Assert.True(nullList.IsNullCollection);
            Assert.Equal(Protocol.CollectionNull, nullList.Count);
            Assert.Empty(nullList.Elements);
            Assert.Equal(WellKnownTypes.List, nullList.Type.DefinitionTypeId);
            Assert.True(graph.IndexOf(nullList) >= 0);

            // The invariant the node kind exists for: a value slot never holds a bare null, and a value
            // has no index exactly when it is a recorded null.
            foreach (var member in obj.Members)
            {
                Assert.NotNull(member.Value);
                Assert.Equal(member.Value is OldNullNode, graph.IndexOf(member.Value) == -1);
            }
        }

        [Fact]
        public void NullCollection_IsNotAPlainNullReference()
        {
            // The distinction the node kind exists for: a member that is null because the file wrote a
            // reference marker, and a member that is null because the file wrote a -1 count for a
            // List<Dictionary<...>>, are two different recorded facts with two different migrations.
            var hand = new Hand();
            var elementType = Closed("Dictionary<Old.String,Old.Int32>", WellKnownTypes.Dictionary,
                hand.Str, hand.Int32);
            var listOfDictionaries = Closed("List<Dictionary<Old.String,Old.Int32>>", WellKnownTypes.List, elementType);

            var nullCollection = hand.Append(new OldCollectionNode(listOfDictionaries, Protocol.CollectionNull,
                Array.Empty<OldValue>(), elementType));
            var nullReference = hand.Null(OldNullReason.ReferenceMarker);

            var obj = hand.Object(Old("Old.Player"), nullCollection, nullReference);
            hand.Build();

            Assert.IsType<OldCollectionNode>(obj.Members[0].Value);
            Assert.IsType<OldNullNode>(obj.Members[1].Value);
            Assert.True(nullCollection.IsNullCollection);

            // The null collection still says what it was, all the way down.
            var dictionary = Assert.Single(nullCollection.Type.ArgumentTypeIds);
            Assert.Equal(WellKnownTypes.Dictionary, dictionary.DefinitionTypeId);
            Assert.Equal(WellKnownTypes.String, dictionary.ArgumentTypeIds[0].TypeId);
            Assert.Equal(WellKnownTypes.Int32, dictionary.ArgumentTypeIds[1].TypeId);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A value kind no node type derives from: what a new node kind looks like before the graph is taught about it.</summary>
        sealed class UnknownValue : OldValue
        {
        }

        static IEnumerable<Type> ModelTypes() => new[]
        {
            typeof(OldTypeIdentity),
            typeof(OldFieldIdentity),
            typeof(OldScalar),
            typeof(OldEnumValueNode),
            typeof(OldTypeValueNode),
            typeof(OldObjectNode),
            typeof(OldCollectionNode),
            typeof(OldOpaqueNode),
            typeof(OldReferenceNode),
            typeof(OldNullNode),
            typeof(OldMember),
            typeof(OldDataGraph),
        };

        /// <summary>Anything a caller could append to, as opposed to a read-only view.</summary>
        static bool IsMutableCollection(Type type)
        {
            if (type == typeof(string)) return false;
            if (type.IsArray) return true;
            if (type == typeof(IList) || type == typeof(ICollection)) return true;
            if (!type.IsGenericType) return false;

            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(IList<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(List<>)
                || definition == typeof(HashSet<>)
                || definition == typeof(Queue<>)
                || definition == typeof(Dictionary<,>);
        }
    }
}
