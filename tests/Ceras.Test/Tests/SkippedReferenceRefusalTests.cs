// A back-reference into a member the current model no longer has.
//
// The writer's object cache holds one slot per object it has *written*, and the migration interpreter
// has to keep its index space in step with that even where it builds nothing: a value at a removed
// position is skipped, but the writer still reserved a slot for it, so every later back-reference
// marker in the file is numbered as though the object existed. Skipping therefore *reserves* a slot
// and never publishes it.
//
// That reservation used to be indistinguishable from a published null, so a retained member naming
// the skipped object resolved the in-range slot, read back null, and the load reported success with
// the stream still perfectly aligned: the worst possible answer, because nothing is out of place and
// a reference the save legitimately contains is simply gone.
//
// A slot whose first occurrence was skipped is a distinct state, and `IdentityCache.Resolve` names it
// as the unsupported cross-reference it is (`CerasSchemaException`) rather than answering null. The
// cases here pin that boundary from both sides, because both halves of it are load-bearing:
//
//   * every way the *skip* path reserves (a Type-value named value, a manifest id, a bare `object`
//     behind a same-type marker) is refused, because a real writer produces each of them;
//   * everything else is untouched — a reserved-but-unpublished slot is what a cycle resolves
//     through, a skipped value nothing references is still fully accounted for, and a cycle *inside*
//     a skipped subtree is a reference between two values the save is throwing away, which costs
//     the reader nothing and must not be reported.
//
// The writer legitimately produces the whole shape: a real `SharedCrate` holding one `List<int>` in
// three members, saved with the real writer, is read here through an old schema in which only the
// first of the three survives, so the bytes under test are bytes Ceras itself wrote.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.SkippedReferenceFixtures
{
    /// <summary>
    /// The model the interpreter builds. <c>Abandoned</c> is deliberately <b>not</b> a member here:
    /// that is the whole point — the file has it, this build does not, so the migration skips it.
    /// </summary>
    [CerasContext("SkippedReference")]
    [CerasSerializable]
    public class MigratedCrate
    {
        public object Kept;
        public object Also;
        public object Tail;
        public uint Marker;
    }

    /// <summary>
    /// The same crate as the writer had it, with the member that has since been removed. Saving one
    /// of these is what produces the bytes the tests read back, so the graph under test is a graph
    /// Ceras wrote rather than a hand-assembled fiction.
    /// <para>
    /// <c>Tail</c> is a fourth reference member so a *retained* member can be ordered <em>after</em>
    /// the abandoned one: a member is written in the registry's member order, not in declaration order,
    /// and only a member behind the abandoned one can carry the back-reference that makes the graph the
    /// bug needs. Two of the three retained members referring at the same value is not the point here;
    /// the point is that one retained member, written after the abandoned one, refers back to it.
    /// </para>
    /// </summary>
    [CerasContext("SkippedReference")]
    [CerasSerializable]
    public class SharedCrate
    {
        public object Abandoned;
        public object Kept;
        public object Also;
        public object Tail;
        public uint Marker;
    }

    /// <summary>
    /// A user class, so the removed first occurrence is a manifest id rather than a Type value. Its
    /// <c>Next</c> member is what a cycle is written through.
    /// </summary>
    [CerasContext("SkippedReference")]
    [CerasSerializable]
    public class Node
    {
        public int Value;
        public Node Next;
    }

    public class SkippedReferenceRefusalTests : TestBase
    {
        public const string Ctx = "SkippedReference";

        /// <summary>The writer's own object-cache index, cross-checked against its bytes below.</summary>
        const int RootSlot = 0;

        /// <summary>The slot the abandoned member's <em>first</em> occurrence reserved.</summary>
        const int AbandonedSlot = 1;

        const uint Sentinel = 0xA5A5A5A5;

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        // ── (a) a retained member that refers back to a removed one ───────────────────────────────

        [Fact]
        public void A_Retained_Member_Referring_To_An_Abandoned_List_First_Occurrence_Is_Refused()
        {
            var engine = Engine((OldCrate(), typeof(MigratedCrate)));

            // Root (slot 0), then Abandoned: a new List<int>, which the skip path accounts for and
            // reserves a slot for without ever building it, then Kept: a back-reference to that slot.
            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)          // the root, slot 0
                .Ref(Protocol.RefNewValue)                  // Abandoned
                .TypeValue(WellKnownTypes.List, WellKnownTypes.Int32)
                .Count(2).Int(10).Int(20)
                .Back(AbandonedSlot)                          // Kept -> the removed List<int>
                .Back(AbandonedSlot)                          // Also -> the same one
                .Back(AbandonedSlot)                          // Tail -> the same one
                .UInt(Sentinel);                            // Marker

            var ex = Assert.Throws<CerasSchemaException>(() => Read<MigratedCrate>(engine, payload.Bytes));

            // Typed, and it says which slot could not be honoured: the index is the writer's own, so
            // the diagnostic points at the object in the save rather than at a decoder internal.
            Assert.Contains($"object {AbandonedSlot}", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no longer has", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Retained_Member_Referring_To_An_Abandoned_Class_First_Occurrence_Is_Refused()
        {
            // The same graph with a manifest id in front of the removed value instead of a Type value:
            // a second skip reservation site, reached through SkipUserType.
            const int nodeId = WellKnownTypes.UserTypeBase + 1;
            var engine = Engine((OldCrate(), typeof(MigratedCrate)), (OldNode(nodeId), typeof(Node)));

            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)          // the root, slot 0
                .Ref(Protocol.RefNewValue)                  // Abandoned
                .TypeId(nodeId)                             // a Node
                .Int(7)                                     // Node.Value
                .Ref(Protocol.RefNull)                      // Node.Next
                .Back(AbandonedSlot)                          // Kept -> the removed Node
                .Back(AbandonedSlot)                          // Also -> the same one
                .Back(AbandonedSlot)                          // Tail -> the same one
                .UInt(Sentinel);                            // Marker

            var ex = Assert.Throws<CerasSchemaException>(() => Read<MigratedCrate>(engine, payload.Bytes));
            Assert.Contains($"object {AbandonedSlot}", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Retained_Member_Referring_To_An_Abandoned_Bare_Object_Is_Refused()
        {
            // `new object()` takes the same-type marker and writes no payload at all, so this is the
            // third skip reservation site and the smallest shape of all: one byte per occurrence.
            var engine = Engine((OldCrate(), typeof(MigratedCrate)));

            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)          // the root, slot 0
                .Ref(Protocol.RefNewValueSameType)          // Abandoned: a bare `object`, no payload
                .Back(AbandonedSlot)                          // Kept -> it
                .Back(AbandonedSlot)                          // Also -> it
                .Back(AbandonedSlot)                          // Tail -> it
                .UInt(Sentinel);                            // Marker

            var ex = Assert.Throws<CerasSchemaException>(() => Read<MigratedCrate>(engine, payload.Bytes));
            Assert.Contains($"object {AbandonedSlot}", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Writers_Own_Bytes_For_That_Graph_Are_Refused_Rather_Than_Silently_Loaded()
        {
            // The same shape, but every byte is the real writer's: one List<int> shared by the removed
            // member and by both retained ones. Nothing here is assembled by hand, so this is also the
            // evidence that a writer legitimately produces a graph the interpreter cannot materialise.
            var writer = New();
            var shared = new List<int> { 10, 20 };
            var bytes = writer.Save(new SharedCrate
            {
                Abandoned = shared,      // written first, and skipped by this build
                Kept = shared,
                Also = shared,
                Tail = shared,         // written after Abandoned, so its back-reference is the one that matters
                Marker = Sentinel,
            }, 1);

            var payload = PayloadSection(bytes);

            // The arithmetic the payload really uses, decoded back out of it rather than assumed: the
            // root is slot 0, the shared List<int> is slot 1, and the members written after the removed
            // one are biased references to *that* — the slot the skip reserves and never publishes.
            // The decode walks the file's own member order, so this also pins which slot is which.
            var decoded = DecodeSharedCratePayload(payload);
            Assert.Equal(Protocol.RefNewValueSameType, decoded.RootMarker);
            Assert.Equal(Protocol.RefNewValue, decoded.NewValueMarker);
            Assert.Equal(WellKnownTypes.List, decoded.NewValueTypeTag);
            Assert.Equal(new[] { 10, 20 }, decoded.NewValueElements);
            Assert.Equal(AbandonedSlot, decoded.FirstBackReference);
            Assert.Equal(AbandonedSlot, decoded.SecondBackReference);
            Assert.Equal(AbandonedSlot, decoded.ThirdBackReference);
            Assert.Equal(Sentinel, decoded.Marker);

            // A hand-built old schema from the writer's own registered member list, in which only
            // `Abandoned` is unmapped - the one member this build no longer has.
            var engine = Engine((OldCrateFromRegistry(writer, typeof(SharedCrate)), typeof(MigratedCrate)));

            // The engine reads the root's payload; the loader, not the engine, consumes the root's type
            // id in front of it, so that is stripped here exactly as CerasRobustSerializer strips it.
            int rootIdEnd = 0;
            SchemaSection.ReadLeb128(payload, ref rootIdEnd);

            var ex = Assert.Throws<CerasSchemaException>(() => Read<MigratedCrate>(engine, payload[rootIdEnd..]));
            Assert.Contains($"object {AbandonedSlot}", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Writers_Own_Bytes_Still_Load_When_Nothing_Left_References_The_Abandoned_Slot()
        {
            // The control for the refusal above, and the only place the two are distinguishable: the
            // file still has an abandoned member, the interpreter still skips it and still reserves the
            // slot the writer reserved — and the retained members after it are fine, because none of
            // them refers back to that slot. A blanket "this load skipped something" check would refuse
            // this file; a per-slot check does not, and the values here prove the rest of the walk
            // (two shared references, an independent object, the sentinel) is still exactly right.
            var writer = New();
            var shared = new List<int> { 10, 20 };
            var bytes = writer.Save(new SharedCrate
            {
                Abandoned = new List<int> { 1 },        // the file's first occurrence, skipped by this build
                Kept = new List<int> { 42 },            // every other member carries an object of its own,
                Also = shared,                          // so no retained member refers back to that slot
                Tail = shared,
                Marker = Sentinel,
            }, 1);

            var payload = PayloadSection(bytes);
            var engine = Engine((OldCrateFromRegistry(writer, typeof(SharedCrate)), typeof(MigratedCrate)));

            int rootIdEnd = 0;
            SchemaSection.ReadLeb128(payload, ref rootIdEnd);

            var loaded = Read<MigratedCrate>(engine, payload[rootIdEnd..]);
            Assert.Equal(new[] { 42 }, Assert.IsType<List<int>>(loaded.Kept));
            Assert.Equal(new[] { 10, 20 }, Assert.IsType<List<int>>(loaded.Also));
            Assert.Same(loaded.Also, loaded.Tail);      // a shared reference that is *not* into the skipped slot
            Assert.Equal(Sentinel, loaded.Marker);
        }

        [Fact]
        public void A_File_That_Loads_End_To_End_Refuses_It_Instead_Of_Loading_A_Null()
        {
            // The whole path a caller takes: a valid robust file, the real loader, the interpreter
            // reached because the file's schema section describes a member the model does not have.
            var bytes = FileWithAbandonedMember(w => w
                .Ref(Protocol.RefNewValue)
                .TypeValue(WellKnownTypes.List, WellKnownTypes.Int32)
                .Count(1).Int(99));

            var ex = Assert.Throws<CerasSchemaException>(() => New().Load<MigratedCrate>(bytes, 1));
            Assert.Contains($"object {AbandonedSlot}", ex.Message, StringComparison.Ordinal);
        }

        // ── (b) what must keep working: a first occurrence that <em>is</em> materialized ───────────

        [Fact]
        public void A_Retained_First_Occurrence_And_Its_BackReference_Still_Roundtrip()
        {
            const int nodeId = WellKnownTypes.UserTypeBase + 1;
            var engine = Engine((OldCrate(), typeof(MigratedCrate)), (OldNode(nodeId), typeof(Node)));

            // No removed value at all: `Abandoned` is a null, which reserves nothing, so Kept's new Node
            // is the first occurrence in the file and owns the slot `Also` refers back to.
            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)
                .Ref(Protocol.RefNull)                      // Abandoned: nothing
                .Ref(Protocol.RefNewValue).TypeId(nodeId)   // Kept -> the new Node, slot 1
                .Int(7)
                .Ref(Protocol.RefNull)                      // Node.Next
                .Back(1)                                    // Also -> that same Node
                .Back(1)                                    // Tail -> that same Node
                .UInt(Sentinel);

            var loaded = Read<MigratedCrate>(engine, payload.Bytes);

            var node = Assert.IsType<Node>(loaded.Kept);
            Assert.Equal(7, node.Value);
            Assert.Null(node.Next);
            Assert.Same(node, loaded.Also);            // one object, not two equal ones
            Assert.Same(node, loaded.Tail);
            Assert.Equal(Sentinel, loaded.Marker);
        }

        [Fact]
        public void The_Slots_Are_Numbered_Exactly_As_The_Writer_Numbers_Them()
        {
            // The control for the refusal above: a back-reference to slot 0 is a back-reference to
            // the root, and it resolves to that instance. If the reader's numbering were off by one
            // anywhere, this is what would say so - the refusals are not "every marker throws".
            var engine = Engine((OldCrate(), typeof(MigratedCrate)));

            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)          // the root, slot 0
                .Ref(Protocol.RefNull)                      // Abandoned: nothing
                .Back(RootSlot)                             // Kept -> the root
                .Back(RootSlot)                             // Also -> the root
                .Back(RootSlot)                             // Tail -> the root
                .UInt(Sentinel);

            var loaded = Read<MigratedCrate>(engine, payload.Bytes);
            Assert.Same(loaded, loaded.Kept);
            Assert.Same(loaded, loaded.Also);
            Assert.Same(loaded, loaded.Tail);
            Assert.Equal(Sentinel, loaded.Marker);
        }

        [Fact]
        public void A_Cycle_Through_A_Retained_Object_Still_Resolves_To_It()
        {
            // A self-reference is the other reserved-but-unpublished slot: Kept is reserved before its
            // members are read, so Node.Next naming the object being read resolves to that instance.
            // It is a null placeholder for an instant, never a skipped occurrence, and must survive.
            const int nodeId = WellKnownTypes.UserTypeBase + 1;
            var engine = Engine((OldCrate(), typeof(MigratedCrate)), (OldNode(nodeId), typeof(Node)));

            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)
                .Ref(Protocol.RefNull)                      // Abandoned: nothing
                .Ref(Protocol.RefNewValue).TypeId(nodeId)   // Kept -> the new Node, slot 1
                .Int(3)                                     // Node.Value
                .Back(1)                                    // Node.Next -> the Node being read
                .Back(1)                                    // Also -> the same one
                .Back(1)                                    // Tail -> the same one
                .UInt(Sentinel);

            var loaded = Read<MigratedCrate>(engine, payload.Bytes);
            var node = Assert.IsType<Node>(loaded.Kept);
            Assert.Equal(3, node.Value);
            Assert.Same(node, node.Next);
            Assert.Same(node, loaded.Also);
            Assert.Same(node, loaded.Tail);
            Assert.Equal(Sentinel, loaded.Marker);
        }

        // ── (c) a removed value nothing refers to is still fully accounted for ────────────────────

        [Fact]
        public void An_Abandoned_First_Occurrence_Nothing_References_Leaves_The_Members_After_It_Aligned()
        {
            // The reserve exists to keep the writer's index space in step, and a removed member is
            // still a member: its marker, its type value, its count and every element have to be
            // consumed. `Marker` is the witness - a wrong skip shows up as a wrong number.
            var engine = Engine((OldCrate(), typeof(MigratedCrate)));

            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)          // the root, slot 0
                .Ref(Protocol.RefNewValue)                  // Abandoned: a List<int> nothing refers to
                .TypeValue(WellKnownTypes.List, WellKnownTypes.Int32)
                .Count(3).Int(1).Int(2).Int(3)
                .Ref(Protocol.RefNewValue)                  // Kept: its own new object, slot 2
                .TypeValue(WellKnownTypes.List, WellKnownTypes.Int32)
                .Count(1).Int(4)
                .Back(2)                                    // Also -> that one
                .Back(2)                                    // Tail -> that one
                .UInt(Sentinel);

            var loaded = Read<MigratedCrate>(engine, payload.Bytes);
            Assert.Equal(new[] { 4 }, Assert.IsType<List<int>>(loaded.Kept));
            Assert.Same(loaded.Kept, loaded.Also);
            Assert.Same(loaded.Kept, loaded.Tail);
            Assert.Equal(Sentinel, loaded.Marker);        // the stream is still exactly where it should be
        }

        [Fact]
        public void A_File_Whose_Abandoned_Member_Nothing_References_Still_Loads()
        {
            // The removed List<int> is the file's first occurrence and nothing refers back to it, so the
            // whole file loads. The retained members carry their own value instead — a fresh object in
            // slot 2, which is exactly the case a blanket "this load saw a skip" check would refuse.
            var bytes = FileWithAbandonedMember(w => w
                .Ref(Protocol.RefNewValue)
                .TypeValue(WellKnownTypes.List, WellKnownTypes.Int32)
                .Count(2).Int(5).Int(6),
                writeKept: w => w
                    .Ref(Protocol.RefNewValue)                                     // Kept: its own object, slot 2
                    .TypeValue(WellKnownTypes.List, WellKnownTypes.Int32)
                    .Count(1).Int(77)
                    .Back(2)                                                       // Also -> that one
                    .Back(2));                                                     // Tail -> that one

            var crate = New().Load<MigratedCrate>(bytes, 1);
            Assert.Equal(new[] { 77 }, Assert.IsType<List<int>>(crate.Kept));
            Assert.Same(crate.Kept, crate.Also);
            Assert.Same(crate.Kept, crate.Tail);
            Assert.Equal(Sentinel, crate.Marker);
        }

        // ── (d) a cycle inside a skipped subtree is not an error ──────────────────────────────────

        [Fact]
        public void A_Cycle_Inside_An_Abandoned_Subtree_Is_Not_Reported()
        {
            // Both ends of this cycle are inside the member being discarded, so the reference cannot
            // cost the caller anything: there is no value left to be wrong. The skipper accounts for
            // a back-reference marker by consuming it, which is why this stays quiet.
            const int nodeId = WellKnownTypes.UserTypeBase + 1;
            var engine = Engine((OldCrate(), typeof(MigratedCrate)), (OldNode(nodeId), typeof(Node)));

            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)          // the root, slot 0
                .Ref(Protocol.RefNewValue)                  // Abandoned: a Node, slot 1
                .TypeId(nodeId)
                .Int(5)                                     // Node.Value
                .Back(AbandonedSlot)                          // Node.Next -> itself
                .Ref(Protocol.RefNewValue)                  // Kept: its own new object, slot 2
                .TypeValue(WellKnownTypes.List, WellKnownTypes.Int32)
                .Count(1).Int(8)
                .Back(2)                                    // Also -> that one
                .Back(2)                                    // Tail -> that one
                .UInt(Sentinel);

            var loaded = Read<MigratedCrate>(engine, payload.Bytes);
            Assert.Equal(new[] { 8 }, Assert.IsType<List<int>>(loaded.Kept));
            Assert.Same(loaded.Kept, loaded.Also);
            Assert.Same(loaded.Kept, loaded.Tail);
            Assert.Equal(Sentinel, loaded.Marker);
        }

        // ── the two typed failures stay distinct ─────────────────────────────────────────────────

        [Fact]
        public void An_Out_Of_Range_BackReference_Is_Still_Corruption_And_Not_A_Schema_Problem()
        {
            // The skipped-slot refusal is about a save this build's model cannot express. A marker
            // past the end of the cache is not that: no writer emits it, so it stays corruption.
            var engine = Engine((OldCrate(), typeof(MigratedCrate)));

            var payload = new Payload()
                .Ref(Protocol.RefNewValueSameType)
                .Ref(Protocol.RefNull)                      // Abandoned: nothing
                .Back(7)                                    // Kept -> a slot no writer ever reserved
                .Back(RootSlot)
                .Back(RootSlot)
                .UInt(Sentinel);

            Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => Read<MigratedCrate>(engine, payload.Bytes)));
        }

        // ── the writer's own model, member for member ───────────────────────────────────────────

        /// <summary>
        /// The old schema for <see cref="MigratedCrate"/>'s file, carrying the member the current
        /// model does not have. <c>Abandoned</c> is a plain <c>object</c> member, which is the shape
        /// that makes the file's removed position a polymorphic slot — a reference marker followed by
        /// the value's own type.
        /// </summary>
        static OldTypeSchema OldCrate() => new OldTypeSchema(
            WellKnownTypes.UserTypeBase,
            TypeIdentity.GetCanonicalName(typeof(MigratedCrate)),
            Array.Empty<string>(),
            SchemaTypeKind.Class,
            new[]
            {
                new OldFieldSchema("Abandoned", Array.Empty<string>(), WellKnownTypes.Object, isMonomorphic: false),
                new OldFieldSchema("Kept",    Array.Empty<string>(), WellKnownTypes.Object, isMonomorphic: false),
                new OldFieldSchema("Also",    Array.Empty<string>(), WellKnownTypes.Object, isMonomorphic: false),
                new OldFieldSchema("Tail",    Array.Empty<string>(), WellKnownTypes.Object, isMonomorphic: false),
                new OldFieldSchema("Marker",  Array.Empty<string>(), WellKnownTypes.UInt32, isMonomorphic: true),
            },
            0);

        /// <summary>
        /// The same file's <c>Node</c> entry, in which <c>Next</c> is a reference to the very type it
        /// lives on — which is what makes a cycle writable and skippable.
        /// </summary>
        static OldTypeSchema OldNode(int typeId) => new OldTypeSchema(
            typeId,
            TypeIdentity.GetCanonicalName(typeof(Node)),
            Array.Empty<string>(),
            SchemaTypeKind.Class,
            new[]
            {
                new OldFieldSchema("Value", Array.Empty<string>(), WellKnownTypes.Int32, isMonomorphic: true),
                new OldFieldSchema("Next",  Array.Empty<string>(), typeId, isMonomorphic: false),
            },
            0);

        /// <summary>
        /// The old schema for a real <see cref="SharedCrate"/>, built from the registry's own
        /// description of it. Taking the member list from the registry rather than restating it is
        /// what makes the writer's payload order and this walk the same order, so the bytes the real
        /// writer produced are read by the same walk a hand-built schema would drive.
        /// </summary>
        static OldTypeSchema OldCrateFromRegistry(CerasRobustSerializer serializer, Type written)
        {
            var registry = serializer.Config.SchemaRegistry;
            Assert.True(registry.TryGetInfo(written, out var info), $"'{written}' is not in this context's manifest.");

            var fields = info.Fields.Select(f =>
            {
                var clr = written.GetField(f.StableName, System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(clr);
                int id = WellKnownTypes.TryGetId(clr.FieldType, out int wellKnown) ? wellKnown
                    : registry.TryGetId(clr.FieldType, out int manifest) ? manifest
                    : throw new Xunit.Sdk.XunitException($"'{clr.FieldType}' has no id in this context.");
                return new OldFieldSchema(f.StableName, f.PreviousNames, id, f.IsMonomorphic);
            }).ToArray();

            return new OldTypeSchema(
                WellKnownTypes.UserTypeBase,
                TypeIdentity.GetCanonicalName(typeof(MigratedCrate)),
                Array.Empty<string>(),
                SchemaTypeKind.Class,
                fields,
                0);
        }

        /// <summary>
        /// An engine over hand-built old schemas. Every entry gets a plan whose mappings are decided
        /// by name against the current type, so a member the current type does not have is a member
        /// the migration skips — the same decision <c>MigrationPlanBuilder</c> makes, taken here
        /// directly so a test can state the file's shape member for member.
        /// </summary>
        static SchemaTraversalEngine Engine(params (OldTypeSchema Old, Type Current)[] entries)
        {
            var schemas = new Dictionary<string, OldTypeSchema>();
            var plans = new Dictionary<string, TypeMigrationPlan>();

            foreach (var (old, current) in entries)
            {
                schemas[old.StableName] = old;
                plans[old.StableName] = new TypeMigrationPlan
                {
                    CurrentType = current,
                    Mappings = old.Fields.Select(f => new FieldMapping
                    {
                        OldField = f,
                        NewField = current.GetField(f.StableName),
                        Coercion = CoercionKind.None,
                    }).ToArray(),
                    AddedFieldNames = Array.Empty<string>(),
                };
            }

            // Index 0 is a placeholder because the string table is 1-based; none of these payloads
            // names a string, so it is never read.
            return new SchemaTraversalEngine(schemas, new string[] { null }, 1, New(), plans);
        }

        /// <summary>
        /// One interpreter read. A <c>SequenceReader</c> is a ref struct and cannot cross into a
        /// lambda, so the read lives here rather than in the assertion.
        /// </summary>
        static T Read<T>(SchemaTraversalEngine engine, byte[] payload)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            return engine.ReadRoot<T>(ref reader, default(T));
        }

        /// <summary>The declared data section of a real save: the root's type id and its payload.</summary>
        static byte[] PayloadSection(byte[] file)
        {
            int dataStart = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.DataSectionOffsetOffset));
            int dataEnd = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(RobustHeader.StringTableOffsetOffset));
            return file[dataStart..dataEnd];
        }

        /// <summary>
        /// Decodes the payload a real <see cref="SharedCrate"/> save carries, so the test can state the
        /// writer's actual markers and slot indices instead of an assumed byte offset. The values are
        /// read with the *reader's* helpers, which is what makes this a statement about the writer's
        /// output rather than a second guess at the encoding.
        /// <para>
        /// The three retained members are read as three consecutive biased markers rather than by name:
        /// every one of them is a back-reference to the same object in the graph under test, and the
        /// point of the assertions above is the slot they name, not which member wrote it.
        /// </para>
        /// </summary>
        static (int RootMarker, int NewValueMarker, int NewValueTypeTag, int[] NewValueElements,
                int FirstBackReference, int SecondBackReference, int ThirdBackReference, uint Marker)
            DecodeSharedCratePayload(byte[] payload)
        {
            int cursor = 0;
            SchemaSection.ReadLeb128(payload, ref cursor);                                   // the root's type id
            int rootMarker = SerializerBinary.ReadUInt32Bias(payload, ref cursor, Protocol.RefBias);
            int newValueMarker = SerializerBinary.ReadUInt32Bias(payload, ref cursor, Protocol.RefBias);

            int tag = (int)SchemaSection.ReadLeb128(payload, ref cursor);                  // a List<int> type value
            Assert.Equal(1, (int)SchemaSection.ReadLeb128(payload, ref cursor));             // one argument
            Assert.Equal(WellKnownTypes.Int32, (int)SchemaSection.ReadLeb128(payload, ref cursor));

            int count = SerializerBinary.ReadUInt32Bias(payload, ref cursor, Protocol.CollectionBias);
            var elements = new int[count];
            for (int i = 0; i < count; i++) elements[i] = SerializerBinary.ReadInt32Fixed(payload, ref cursor);

            int first = SerializerBinary.ReadUInt32Bias(payload, ref cursor, Protocol.RefBias);
            int second = SerializerBinary.ReadUInt32Bias(payload, ref cursor, Protocol.RefBias);
            int third = SerializerBinary.ReadUInt32Bias(payload, ref cursor, Protocol.RefBias);
            uint marker = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(cursor));
            cursor += sizeof(uint);

            // The writer never pads, so the data section ends with the root's last byte. If the
            // arithmetic above were off by anything, the section would not add up here.
            Assert.Equal(payload.Length, cursor);

            return (rootMarker, newValueMarker, tag, elements, first, second, third, marker);
        }

        // ── hand-assembled files ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// A complete, valid robust file whose root schema describes five members — <c>Abandoned</c>,
        /// <c>Kept</c>, <c>Also</c>, <c>Tail</c> and <c>Marker</c> — while the current model
        /// (<see cref="MigratedCrate"/>) has only the last four. The payload is the root's type id and
        /// marker, the abandoned member written by <paramref name="writeAbandoned"/>, the three retained
        /// reference members written by <paramref name="writeKept"/>, then the sentinel.
        /// <para>
        /// The retained members default to back-references to the slot the abandoned member reserved,
        /// which is the graph under test; a test that needs them to point elsewhere (or nowhere) writes
        /// them itself.
        /// </para>
        /// <para>
        /// Hand-assembled because the file has to describe a member the current model does not have,
        /// and no writer in this assembly does: the whole point is that the file is old. The
        /// fingerprint is left at zero, so the loader takes the migration interpreter.
        /// </para>
        /// </summary>
        static byte[] FileWithAbandonedMember(Action<Payload> writeAbandoned, Action<Payload> writeKept = null)
        {
            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            var schema = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            SchemaSection.WriteLeb128(schema, 1);                       // one schema entry: the root
            SchemaSection.WriteLeb128(schema, (uint)WellKnownTypes.UserTypeBase);
            SchemaSection.WriteInternedString(schema, TypeIdentity.GetCanonicalName(typeof(MigratedCrate)), intern);
            SchemaSection.WriteLeb128(schema, 0);                       // no previous type names
            schema.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; schema.Advance(1);
            SchemaSection.WriteLeb128(schema, 5);                       // Abandoned, Kept, Also, Tail, Marker
            WriteMember(schema, intern, "Abandoned", WellKnownTypes.Object, monomorphic: false);
            WriteMember(schema, intern, "Kept", WellKnownTypes.Object, monomorphic: false);
            WriteMember(schema, intern, "Also", WellKnownTypes.Object, monomorphic: false);
            WriteMember(schema, intern, "Tail", WellKnownTypes.Object, monomorphic: false);
            WriteMember(schema, intern, "Marker", WellKnownTypes.UInt32, monomorphic: true);
            SchemaSection.WriteLeb128(schema, 0);                       // no fixed byte size

            int schemaStringOffset = schema.Offset;
            intern.Flush(ref schema.Buffer, ref schema.Offset);
            int dataStart = schema.Offset;

            var payload = new Payload(schema.Buffer, schema.Offset);
            payload.TypeId(WellKnownTypes.UserTypeBase);               // the root's stored type id
            payload.Ref(Protocol.RefNewValueSameType);                  // the root itself, slot 0
            writeAbandoned(payload);                                     // Abandoned, slot 1
            (writeKept ?? (p => { p.Back(AbandonedSlot); p.Back(AbandonedSlot); p.Back(AbandonedSlot); }))(payload);
            payload.UInt(Sentinel);                                    // Marker

            int stringTableStart = payload.Offset;
            var emptyStrings = new StringInternTable();
            emptyStrings.Flush(ref payload.Writer.Buffer, ref payload.Writer.Offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(payload.Writer.Buffer, payload.Writer.Offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return payload.Bytes;
        }

        /// <summary>One member entry of a hand-assembled schema section.</summary>
        static void WriteMember(IBufferWriter<byte> schema, StringInternTable intern, string name, int typeId, bool monomorphic)
        {
            SchemaSection.WriteInternedString(schema, name, intern);
            SchemaSection.WriteLeb128(schema, 0);                       // no previous names
            SchemaSection.WriteLeb128(schema, (uint)typeId);
            schema.GetSpan(1)[0] = monomorphic ? (byte)1 : (byte)0;
            schema.Advance(1);
        }

        /// <summary>
        /// The payload grammar, spelled out. Every value here is the writer's own encoding, so a test
        /// states the bytes it means rather than the index it hopes the reader will use.
        /// </summary>
        sealed class Payload
        {
            public readonly CerasSerializer.FixedArrayBufferWriter Writer;

            public Payload() : this(null, 0) { }

            public Payload(byte[] buffer, int offset) => Writer = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);

            public int Offset => Writer.Offset;

            public byte[] Buffer => Writer.Buffer;

            public byte[] Bytes => Writer.Buffer[..Writer.Offset];

            /// <summary>A reference marker: null, new value, new value of the same type, or inline type.</summary>
            public Payload Ref(int marker)
            {
                SerializerBinary.WriteUInt32Bias(Writer, marker, Protocol.RefBias);
                return this;
            }

            /// <summary>A back-reference to the object the writer's cache holds in <paramref name="slot"/>.</summary>
            public Payload Back(int slot) => Ref(slot);

            public Payload TypeId(int id)
            {
                SchemaSection.WriteLeb128(Writer, (uint)id);
                return this;
            }

            /// <summary>A <c>Type</c> value: the definition tag, then each argument as its own value.</summary>
            public Payload TypeValue(int definition, params int[] arguments)
            {
                TypeId(definition);
                TypeId(arguments.Length);
                foreach (var argument in arguments) TypeId(argument);
                return this;
            }

            /// <summary>A collection's biased element count, the encoding every collection but Queue/Stack uses.</summary>
            public Payload Count(int count)
            {
                SerializerBinary.WriteUInt32Bias(Writer, count, Protocol.CollectionBias);
                return this;
            }

            public Payload Int(int value)
            {
                SerializerBinary.WriteInt32Fixed(Writer, value);
                return this;
            }

            public Payload UInt(uint value)
            {
                SerializerBinary.WriteUInt32Fixed(Writer, value);
                return this;
            }
        }
    }
}
