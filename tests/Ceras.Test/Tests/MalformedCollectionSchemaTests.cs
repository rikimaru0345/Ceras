// Two findings about a collection schema that records no element type arguments. The first is a non-finding,
// the second is what this file pins.
//
// 1. NOT a defect: a *removed* `List<bool>` member does not corrupt a save written by this version. The
//    normal skip path advances one byte per element, which is exactly what `BoolFormatter` wrote, and
//    `A_Valid_Collection_Entry_Behind_A_Removed_Member_Is_Skipped_Byte_Exactly` proves it — the trailing field
//    holds. `bool`'s recorded width is deliberately left alone for that reason: the skip answers the `Bool` id
//    from its own switch, so no width would change a byte or a skipped value, and the width is not where the
//    real defect is.
//
// 2. A real defect: a hand-registered or doctored collection schema that records no element type arguments. A
//    descriptor like that was admitted by `SchemaRegistry.Register` (the documented public forge) and
//    written into the schema section verbatim, and a file carrying one failed or desynchronized in the
//    migration read/skip paths:
//
//   * `List<bool>` with no arguments — read: a `CerasSchemaException` whose text blamed the *element* type
//     for having no fixed size, which is an accident of `bool` not being in the fixed-size table rather
//     than a check; skip: the count was consumed and then nothing at all, so every field after it decoded
//     from the wrong offset;
//   * `List<int>` with no arguments — read: **no error at all**; the three stored elements came back as
//     three zeros, because `int` does have a fixed width, so the stream stayed "aligned" and the data was
//     simply gone;
//   * `Dictionary<K,V>` with only its key recorded — read: a bare `ArgumentNullException` out of the BCL's
//     `IDictionary.Add`, fed a value the decoder had silently replaced with null;
//   * a nested `List<List<int>>` whose *inner* entry recorded no argument — read: a bare
//     `ArgumentException` out of `IList.Add`;
//   * a definition or an argument identity nothing describes — skip: a desync, whether or not a member
//     referenced the entry.
//
// Every one of those is fixed by a single structural invariant (`Ceras.Format.StructuralShape`): a
// `GenericInstantiation` entry describes a payload only if its definition is one the format lays out with
// exactly that definition's argument count, and every identity it records is one the file describes. It is
// asked at the four doors through which such an entry can arrive — the authoring forge, the section parser,
// the migration plan builder (which both `Load` and the engine's own constructor pass through) and the
// `Type`-value grammar — always before a payload byte is interpreted. That is what makes the interpreter's
// read and skip paths unable to disagree about it: neither can reach one.
//
// Every malformed row is paired with a valid control built by the *same* builder with the *same* payload
// bytes, so the suite cannot be satisfied by refusing everything. Every file carries a valid header checksum
// (recomputed by `RobustHeader.FinalizeHeader`), so what is under test is the schema rule and not the
// integrity check, and the trailing sentinel in every payload is the alignment witness.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.MalformedCollectionSchemas
{
    // One context per model: a context's manifest is finite and sealed on first use, and these tests
    // deliberately build hand-written files, so nothing here shares a context with a neighbour.

    [CerasContext("McsBool")]
    [CerasSerializable]
    public class McsBoolHolder { public List<bool> Items; public int After; }

    [CerasContext("McsInt")]
    [CerasSerializable]
    public class McsIntHolder { public List<int> Items; public int After; }

    [CerasContext("McsDict")]
    [CerasSerializable]
    public class McsDictHolder { public Dictionary<string, int> Items; public int After; }

    [CerasContext("McsArray")]
    [CerasSerializable]
    public class McsArrayHolder { public int[] Items; public int After; }

    [CerasContext("McsNested")]
    [CerasSerializable]
    public class McsNestedHolder { public List<List<int>> Items; public int After; }

    /// <summary>A polymorphic slot, so a file can put a collection behind a <c>Type</c> value instead of in
    /// a declared member — the fourth door, whose entries no schema section records.</summary>
    [CerasContext("McsSlot")]
    [CerasSerializable]
    public class McsSlotHolder { public object Items; public int After; }

    /// <summary>The <em>skip</em> side: the model has no collection member at all, so a file that has one
    /// is skipped by its own grammar. This is the route that used to consume a count and then nothing.</summary>
    [CerasContext("McsRemoved")]
    [CerasSerializable]
    public class McsRemovedHolder { public int After; }

    public class MalformedCollectionSchemaTests : TestBase
    {
        const string BoolCtx = "McsBool", IntCtx = "McsInt", DictCtx = "McsDict";
        const string ArrayCtx = "McsArray", NestedCtx = "McsNested", SlotCtx = "McsSlot", RemovedCtx = "McsRemoved";

        static CerasRobustSerializer New(string context) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));

        static string Name(Type type) => TypeIdentity.GetCanonicalName(type);

        /// <summary>The trailing field, in every payload below. A decode that stops short of it, or that
        /// consumes the wrong number of bytes, moves it — that is the whole witness.</summary>
        const int Sentinel = 4242;

        /// <summary>The manifest ids a hand-assembled file gives its entries.</summary>
        const int RootId = WellKnownTypes.UserTypeBase, EntryId = WellKnownTypes.UserTypeBase + 1, InnerEntryId = WellKnownTypes.UserTypeBase + 2;

        /// <summary>Neither a well-known id nor an entry in any section these files build.</summary>
        const int UndescribedId = 4242;

        // ── Door 1: the authoring forge ────────────────────────────────────────────────────────

        // (the registered type, the definition its descriptor names, the arguments it records, the
        //  arguments that definition takes)
        public static TheoryData<Type, string, int, int> MalformedDescriptors => new()
        {
            { typeof(List<bool>),                 "System.Collections.Generic.List`1",        0, 1 },  // no element type
            { typeof(List<bool>),                 "System.Collections.Generic.List`1",        2, 1 },  // one too many
            { typeof(Dictionary<string, int>),    "System.Collections.Generic.Dictionary`2",   1, 2 },  // only the key
            { typeof(int[]),                      "System.Array",                            0, 1 },  // no element type
            { typeof(int?),                       "System.Nullable`1",                       0, 1 },  // no element type
            { typeof(KeyValuePair<string, int>),  "System.Collections.Generic.KeyValuePair`2", 1, 2 },  // only the key
        };

        /// <summary>
        /// The invariant at the point a user can still fix it. <see cref="SchemaRegistry.Register"/> with an
        /// explicit descriptor is the documented public forge — "it is how a historical file's shape is
        /// described by hand" — and it is also the one door through which a descriptor that records no
        /// element type enters the format. A declaration bug in application code is a
        /// <see cref="CerasSchemaException"/> raised at the registration itself, not a wrong value on some
        /// later load.
        /// </summary>
        [Theory]
        [MemberData(nameof(MalformedDescriptors))]
        public void A_Hand_Written_Descriptor_That_Omits_A_Definition_Argument_Is_Refused_Where_It_Is_Written(
            Type type, string definition, int recorded, int required)
        {
            var registry = new SchemaRegistry();

            var ex = Assert.Throws<CerasSchemaException>(() => registry.Register(type, Descriptor(type, definition, recorded)));
            Assert.False(registry.IsSealed);

            Assert.Contains(Name(type), ex.Message);   // which registration
            Assert.Contains(definition, ex.Message);   // which definition
            Assert.Contains($"{recorded} argument{(recorded == 1 ? "" : "s")}", ex.Message);
            Assert.Contains($"exactly {required}", ex.Message);   // what it takes instead
        }

        /// <summary>
        /// The control for the refusal above, through the identical builder: the same types and the same
        /// definitions, with the definitions' own argument counts. They register, and the derived
        /// descriptors still do too — so the rule is "a definition's own arguments", not "no explicit
        /// descriptor".
        /// </summary>
        [Fact]
        public void A_Hand_Written_Descriptor_With_The_Definitions_Own_Argument_Count_Registers()
        {
            var registry = new SchemaRegistry();

            registry.Register(typeof(List<bool>), Descriptor(typeof(List<bool>), "System.Collections.Generic.List`1", 1));
            registry.Register(typeof(Dictionary<string, int>), Descriptor(typeof(Dictionary<string, int>), "System.Collections.Generic.Dictionary`2", 2));

            Assert.True(registry.TryGetId(typeof(List<bool>), out _));
            Assert.True(registry.TryGetId(typeof(Dictionary<string, int>), out _));

            // The derived paths, which every manifest in the suite actually goes through: an array and a
            // closed collection are described from the CLR type, so they cannot produce a malformed entry.
            Assert.True(SchemaRegistry.TryBuildStructuralSchema(typeof(int[]), out var arraySchema));
            Assert.Equal(new[] { "System.Int32" }, arraySchema.ArgTypeNames);
            Assert.True(SchemaRegistry.TryBuildStructuralSchema(typeof(List<bool>), out var listSchema));
            Assert.Equal(new[] { "System.Boolean" }, listSchema.ArgTypeNames);
        }

        /// <summary>
        /// A descriptor naming a definition the format does not lay out is left alone, because its arity is
        /// not a fact about the format. A hand-written descriptor is how a <em>user generic</em> — or a type
        /// this build does not even have — is described, and refusing those would refuse the forge's
        /// documented purpose.
        /// </summary>
        [Fact]
        public void A_Descriptor_Of_A_Definition_The_Format_Does_Not_Lay_Out_Is_Not_An_Arity_Question()
        {
            var registry = new SchemaRegistry();

            // Neither name resolves to a CLR type in this build, which is the point: a historical file's
            // shape may name something this one has not got, and a user generic's arity is its own.
            registry.Register(typeof(SchemaTypeKind), Descriptor(typeof(SchemaTypeKind), "Legacy.Gone.Box`2", 2));
            registry.Register(typeof(SchemaTypeKind), Descriptor(typeof(SchemaTypeKind), "Legacy.Vanished.Set`1", 0));

            Assert.True(registry.TryGetId(typeof(SchemaTypeKind), out _));
        }

        static SchemaTypeInfo Descriptor(Type type, string definition, int argumentCount)
            => new SchemaTypeInfo(
                stableName: Name(type),
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.GenericInstantiation,
                fields: Array.Empty<SchemaFieldInfo>(),
                fixedByteSize: 0,
                underlyingTypeName: definition,
                argTypeNames: Enumerable.Repeat("System.Int32", argumentCount).ToArray());

        // ── The shapes the defect is reachable through ─────────────────────────────────────────

        /// <summary>
        /// One row per shape. Every row is a shape this build writes <em>with</em> its arguments, and each
        /// carries both the entry as a writer would write it and the same entry with those arguments
        /// removed — so the valid and the malformed file differ in nothing but that list, which is what makes
        /// the pair a control rather than two unrelated fixtures.
        /// </summary>
        public sealed class MalformedShape
        {
            /// <summary>The stable name of the entry that is malformed (the inner one, for a nested row).</summary>
            public string MalformedEntryName;
            /// <summary>The definition name of the malformed entry, as the refusal quotes it.</summary>
            public string MalformedDefinitionName;
            public int RecordedArguments;
            public int RequiredArguments;
            /// <summary>Whether a member declared as this shape is written behind a reference marker. A
            /// declared concrete array is not (<c>ArrayFormatter</c> writes it by value); every collection
            /// is.</summary>
            public bool MemberIsMarked = true;
            public string[] PayloadStrings = Array.Empty<string>();
            public Action<Section, int> WriteValidEntries;
            public Action<Section, int> WriteMalformedEntries;
            /// <summary>The count and the elements/entries, exactly as the writer wrote them.</summary>
            public Action<CerasSerializer.FixedArrayBufferWriter, int[]> WriteCollection;
            /// <summary>The current model a file for this row is read as when the member is kept. Carried on
            /// the row so the builder and the control cannot disagree about which CLR type a file is for.</summary>
            public Type KeptRoot;
            public string KeptContext;
            public Func<CerasRobustSerializer, byte[], object> LoadKept;
            public Func<CerasRobustSerializer, byte[], object> LoadRemoved;
            public Func<object, object> Items;
            public Func<object, int> After;
            /// <summary>Asserts what the valid file really stores, element by element.</summary>
            public Action<object> CheckItems;
        }

        public static IEnumerable<object[]> MalformedShapes()
        {
            // List<bool> — the shape the review singled out. A bool element is one byte, so the payload is
            // the count and three raw bytes and nothing else. Note that the skip path for a *valid* entry
            // was always right (one byte per element); only the malformed entry was wrong.
            yield return new object[]
            {
                Shape(
                    malformedEntry: "System.Collections.Generic.List`1<System.Boolean>",
                    malformedDefinition: "System.Collections.Generic.List`1",
                    recorded: 0, required: 1,
                    valid: (s, id) => s.Generic(id, "System.Collections.Generic.List`1<System.Boolean>", WellKnownTypes.List, WellKnownTypes.Bool),
                    malformed: (s, id) => s.Generic(id, "System.Collections.Generic.List`1<System.Boolean>", WellKnownTypes.List),
                    collection: (w, keys) =>
                    {
                        SerializerBinary.WriteUInt32Bias(w, 3, Protocol.CollectionBias);
                        w.Write(new byte[] { 1, 0, 1 });
                    },
                    keptRoot: typeof(McsBoolHolder), keptContext: BoolCtx,
                    loadKept: (s, f) => s.Load<McsBoolHolder>(f, 1),
                    items: o => ((McsBoolHolder)o).Items,
                    after: o => ((McsBoolHolder)o).After,
                    checkItems: items => Assert.Equal(new[] { true, false, true }, Assert.IsType<List<bool>>(items))),
            };

            // List<int> — the silent-data-loss row: int has a fixed width, so the skip stayed "aligned" and
            // the read produced zeros with no error at all.
            yield return new object[]
            {
                Shape(
                    malformedEntry: "System.Collections.Generic.List`1<System.Int32>",
                    malformedDefinition: "System.Collections.Generic.List`1",
                    recorded: 0, required: 1,
                    valid: (s, id) => s.Generic(id, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List, WellKnownTypes.Int32),
                    malformed: (s, id) => s.Generic(id, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List),
                    collection: (w, keys) =>
                    {
                        SerializerBinary.WriteUInt32Bias(w, 3, Protocol.CollectionBias);
                        SerializerBinary.WriteInt32Fixed(w, 11);
                        SerializerBinary.WriteInt32Fixed(w, 22);
                        SerializerBinary.WriteInt32Fixed(w, 33);
                    },
                    keptRoot: typeof(McsIntHolder), keptContext: IntCtx,
                    loadKept: (s, f) => s.Load<McsIntHolder>(f, 1),
                    items: o => ((McsIntHolder)o).Items,
                    after: o => ((McsIntHolder)o).After,
                    checkItems: items => Assert.Equal(new[] { 11, 22, 33 }, Assert.IsType<List<int>>(items))),
            };

            // Dictionary<K,V> with only its key recorded: the read path handed the BCL a null key. The
            // *valid* entry records String+Int32, so its key is a 4-byte string-table index and the
            // hand-written entries match the keys the current model declares.
            yield return new object[]
            {
                Shape(
                    malformedEntry: "System.Collections.Generic.Dictionary`2<System.String,System.Int32>",
                    malformedDefinition: "System.Collections.Generic.Dictionary`2",
                    recorded: 1, required: 2,
                    valid: (s, id) => s.Generic(id, "System.Collections.Generic.Dictionary`2<System.String,System.Int32>",
                        WellKnownTypes.Dictionary, WellKnownTypes.String, WellKnownTypes.Int32),
                    malformed: (s, id) => s.Generic(id, "System.Collections.Generic.Dictionary`2<System.String,System.Int32>",
                        WellKnownTypes.Dictionary, WellKnownTypes.String),
                    payloadStrings: new[] { "a", "b" },
                    collection: (w, keys) =>
                    {
                        SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                        SerializerBinary.WriteUInt32Fixed(w, unchecked((uint)keys[0])); SerializerBinary.WriteInt32Fixed(w, 7);
                        SerializerBinary.WriteUInt32Fixed(w, unchecked((uint)keys[1])); SerializerBinary.WriteInt32Fixed(w, 9);
                    },
                    keptRoot: typeof(McsDictHolder), keptContext: DictCtx,
                    loadKept: (s, f) => s.Load<McsDictHolder>(f, 1),
                    items: o => ((McsDictHolder)o).Items,
                    after: o => ((McsDictHolder)o).After,
                    checkItems: items =>
                    {
                        var d = Assert.IsType<Dictionary<string, int>>(items);
                        Assert.Equal(2, d.Count);
                        Assert.Equal(7, d["a"]);
                        Assert.Equal(9, d["b"]);
                    }),
            };

            // A declared concrete array: the same "one element argument" rule, under a definition that is
            // not a generic type at all — and the one shape whose member is written *without* a reference
            // marker, so the payload starts at the count.
            yield return new object[]
            {
                Shape(
                    malformedEntry: "System.Int32[]",
                    malformedDefinition: "System.Array",
                    recorded: 0, required: 1,
                    marked: false,
                    valid: (s, id) => s.Generic(id, "System.Int32[]", WellKnownTypes.Array, WellKnownTypes.Int32),
                    malformed: (s, id) => s.Generic(id, "System.Int32[]", WellKnownTypes.Array),
                    collection: (w, keys) =>
                    {
                        SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                        SerializerBinary.WriteInt32Fixed(w, 11);
                        SerializerBinary.WriteInt32Fixed(w, 12);
                    },
                    keptRoot: typeof(McsArrayHolder), keptContext: ArrayCtx,
                    loadKept: (s, f) => s.Load<McsArrayHolder>(f, 1),
                    items: o => ((McsArrayHolder)o).Items,
                    after: o => ((McsArrayHolder)o).After,
                    checkItems: items => Assert.Equal(new[] { 11, 12 }, Assert.IsType<int[]>(items))),
            };

            // A nested malformed definition/arity: the outer entry is well formed and the inner one is
            // not, so nothing about the member points at the defect. This is the row that shows the rule
            // is entry-scoped rather than member-scoped, and — through the removed-member variant — the
            // row that shows why that matters.
            yield return new object[]
            {
                Shape(
                    malformedEntry: "System.Collections.Generic.List`1<System.Int32>",
                    malformedDefinition: "System.Collections.Generic.List`1",
                    recorded: 0, required: 1,
                    valid: (s, id) =>
                    {
                        s.Generic(id, "System.Collections.Generic.List`1<System.Collections.Generic.List`1<System.Int32>>",
                            WellKnownTypes.List, InnerEntryId);
                        s.Generic(InnerEntryId, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List, WellKnownTypes.Int32);
                    },
                    malformed: (s, id) =>
                    {
                        s.Generic(id, "System.Collections.Generic.List`1<System.Collections.Generic.List`1<System.Int32>>",
                            WellKnownTypes.List, InnerEntryId);
                        s.Generic(InnerEntryId, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List);
                    },
                    collection: (w, keys) =>
                    {
                        // An inner List<int> is itself a reference type, so each element stands behind its
                        // own reference marker before its count — the same rule as the member itself.
                        SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                        SameType(w);
                        SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                        SerializerBinary.WriteInt32Fixed(w, 11); SerializerBinary.WriteInt32Fixed(w, 12);
                        SameType(w);
                        SerializerBinary.WriteUInt32Bias(w, 1, Protocol.CollectionBias);
                        SerializerBinary.WriteInt32Fixed(w, 21);
                    },
                    keptRoot: typeof(McsNestedHolder), keptContext: NestedCtx,
                    loadKept: (s, f) => s.Load<McsNestedHolder>(f, 1),
                    items: o => ((McsNestedHolder)o).Items,
                    after: o => ((McsNestedHolder)o).After,
                    checkItems: items =>
                    {
                        var l = Assert.IsType<List<List<int>>>(items);
                        Assert.Equal(2, l.Count);
                        Assert.Equal(new[] { 11, 12 }, l[0]);
                        Assert.Equal(new[] { 21 }, l[1]);
                    }),
            };
        }

        static MalformedShape Shape(
            string malformedEntry, string malformedDefinition, int recorded, int required,
            Action<Section, int> valid, Action<Section, int> malformed,
            Action<CerasSerializer.FixedArrayBufferWriter, int[]> collection,
            Type keptRoot, string keptContext, Func<CerasRobustSerializer, byte[], object> loadKept,
            Func<object, object> items, Func<object, int> after, Action<object> checkItems,
            bool marked = true, string[] payloadStrings = null)
            => new MalformedShape
            {
                MalformedEntryName = malformedEntry,
                MalformedDefinitionName = malformedDefinition,
                RecordedArguments = recorded,
                RequiredArguments = required,
                MemberIsMarked = marked,
                PayloadStrings = payloadStrings ?? Array.Empty<string>(),
                WriteValidEntries = valid,
                WriteMalformedEntries = malformed,
                WriteCollection = collection,
                KeptRoot = keptRoot,
                KeptContext = keptContext,
                LoadKept = loadKept,
                LoadRemoved = (s, f) => s.Load<McsRemovedHolder>(f, 1),
                Items = items,
                After = after,
                CheckItems = checkItems,
            };

        // ── The controls: the very same file with the entry a writer would have written ──────────

        /// <summary>
        /// Every row's control, through the identical builder and the identical payload: the collection
        /// member is <b>kept</b> in the current model and the file describes the shape as this build writes
        /// it, so the stored elements come back and the sentinel holds. Without this the refusals below
        /// could be satisfied by a builder that simply produces unreadable files.
        /// </summary>
        [Theory]
        [MemberData(nameof(MalformedShapes))]
        public void A_Valid_Collection_Entry_Still_Reads_Its_Elements_And_The_Sentinel(MalformedShape shape)
        {
            var file = File(shape, shape.WriteValidEntries);
            var loaded = shape.LoadKept(New(shape.KeptContext), file);

            shape.CheckItems(shape.Items(loaded));
            Assert.Equal(Sentinel, shape.After(loaded));
        }

        /// <summary>
        /// The same valid entry, but the current model no longer has the member — so the value is
        /// <em>skipped</em> rather than read. This is the control for the skip-side refusals, and it is also
        /// the pin for the bool half of the finding in the file header: a valid <c>List&lt;bool&gt;</c> behind a
        /// removed member advances one byte per element and leaves the trailing field exactly where the writer
        /// put it. No change to <c>bool</c>'s recorded width was needed or made for that, and this row is what
        /// would fail if one were wrong in either direction.
        /// </summary>
        [Theory]
        [MemberData(nameof(MalformedShapes))]
        public void A_Valid_Collection_Entry_Behind_A_Removed_Member_Is_Skipped_Byte_Exactly(MalformedShape shape)
        {
            var file = File(shape, shape.WriteValidEntries, into: typeof(McsRemovedHolder));

            var loaded = Assert.IsType<McsRemovedHolder>(shape.LoadRemoved(New(RemovedCtx), file));

            Assert.Equal(Sentinel, loaded.After);
        }

        // ── The refusals, on both directions ────────────────────────────────────────────────────

        /// <summary>
        /// Doors 2 and 3, on the <b>read</b> path of a member the current model still has. Each of these
        /// used to fail differently and none of them said why: a message about the element type, a silent
        /// load with zeros, or a bare BCL exception. All are now one typed refusal that names the entry, its
        /// definition and both counts.
        /// </summary>
        [Theory]
        [MemberData(nameof(MalformedShapes))]
        public void A_Malformed_Collection_Entry_Is_Refused_On_The_Read_Path_Of_A_Kept_Member(MalformedShape shape)
        {
            var file = File(shape, shape.WriteMalformedEntries);
            AssertValidChecksum(file);   // the integrity check is not what refuses this; see the helper
            var serializer = New(shape.KeptContext);

            var ex = Record.Exception(() => shape.LoadKept(serializer, file));

            AssertSectionRefusal(ex, shape.MalformedEntryName, shape.MalformedDefinitionName, shape.RecordedArguments, shape.RequiredArguments);
        }

        /// <summary>
        /// The same malformed entry on the <b>skip</b> path: the current model has no such member, so the
        /// value is skipped by the file's own grammar. This is the route that used to consume the element
        /// count and then nothing at all, leaving every field after it at the wrong offset — something the
        /// exact-consumption detector could only notice as a count of unread bytes, saying nothing about
        /// why. The refusal is deliberately identical to the read path's: the entry is unreadable in both
        /// directions, and neither direction is asked to guess.
        /// </summary>
        [Theory]
        [MemberData(nameof(MalformedShapes))]
        public void A_Malformed_Collection_Entry_Is_Refused_On_The_Skip_Path_Of_A_Removed_Member(MalformedShape shape)
        {
            var file = File(shape, shape.WriteMalformedEntries, into: typeof(McsRemovedHolder));
            AssertValidChecksum(file);
            var serializer = New(RemovedCtx);

            var ex = Record.Exception(() => shape.LoadRemoved(serializer, file));

            AssertSectionRefusal(ex, shape.MalformedEntryName, shape.MalformedDefinitionName, shape.RecordedArguments, shape.RequiredArguments);
        }

        /// <summary>
        /// The refusal is a section-level one, so it is a damaged-file error, and it is a *typed* one: the
        /// assertions on the exact type are what rule out the bare reflection exceptions this replaced.
        /// A sibling of <see cref="CerasSchemaException"/>, so the type itself is what distinguishes the
        /// two classifications.
        /// </summary>
        static void AssertSectionRefusal(Exception ex, string entryName, string definitionName, int recorded, int required)
        {
            // The exact type is the whole point of this assertion. Each of these rows used to escape as one
            // of the types it rules out rather than as anything Ceras said: a `List<bool>`/nested-list as
            // `ArgumentException` out of `IList.Add`, a `Dictionary` as `ArgumentNullException` out of the
            // BCL's `IDictionary.Add`, a bare `IndexOutOfRangeException` or `NullReferenceException` from a
            // reader fed zero elements. `Assert.IsType` is exact, so naming them here is what keeps the
            // promise visible rather than adding a second, weaker check.
            var corrupt = Assert.IsType<CerasDataCorruptionException>(ex);

            // Names the offending entry, its definition and both counts, so the diagnosis is actionable.
            Assert.Contains(entryName, corrupt.Message);
            Assert.Contains(definitionName, corrupt.Message);
            Assert.Contains($"{recorded} argument{(recorded == 1 ? "" : "s")}", corrupt.Message);
            Assert.Contains($"exactly {required}", corrupt.Message);

            // Says what kind of fault this is, and rules out bit rot — the checksum covers these bytes and
            // it already matched, so the only available evidence is the section's own inconsistency.
            Assert.Contains("Corrupt schema section", corrupt.Message);
            Assert.Contains("checksum already matched", corrupt.Message);
        }

        // ── The identity clauses ───────────────────────────────────────────────────────────────

        /// <summary>
        /// A definition id nothing describes. There is no grammar for such an entry at all, so it is
        /// refused as a damaged section — the same classification as the arity rows and for the same
        /// reason: the section contradicts itself, whatever this build's model happens to be. Both
        /// directions are covered, because the read path would have read the collection's count as a member
        /// count and the skip path would have consumed nothing per element.
        /// </summary>
        [Theory]
        [InlineData(true)]     // a kept member
        [InlineData(false)]    // a removed member: the skip route
        public void A_Definition_Identity_Nothing_Describes_Is_Refused_On_Both_Paths(bool kept)
        {
            var root = kept ? typeof(McsIntHolder) : typeof(McsRemovedHolder);
            var context = kept ? IntCtx : RemovedCtx;

            var file = File(
                root: root,
                writeEntries: (s) =>
                {
                    s.Class(RootId, Name(root), ("Items", EntryId), ("After", WellKnownTypes.Int32));
                    // A List<Int32> entry whose *definition* is an id the section never describes. Its own
                    // argument is a well-known id, so only the definition clause can catch this.
                    s.Generic(EntryId, "System.Collections.Generic.List`1<System.Int32>", UndescribedId, WellKnownTypes.Int32);
                },
                writePayload: (w, _) =>
                {
                    SchemaSection.WriteLeb128(w, RootId);
                    SameType(w);
                    SameType(w);
                    SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                    SerializerBinary.WriteInt32Fixed(w, 11);
                    SerializerBinary.WriteInt32Fixed(w, 12);
                    SerializerBinary.WriteInt32Fixed(w, Sentinel);
                });

            AssertValidChecksum(file);

            var ex = Record.Exception(() =>
            {
                if (kept) New(context).Load<McsIntHolder>(file, 1);
                else New(context).Load<McsRemovedHolder>(file, 1);
            });

            var corrupt = Assert.IsType<CerasDataCorruptionException>(ex);
            Assert.Contains("System.Collections.Generic.List`1<System.Int32>", corrupt.Message);
            Assert.Contains($"definition type id {UndescribedId}", corrupt.Message);
        }

        /// <summary>
        /// An argument id nothing describes, behind a member the application has since <em>removed</em>. The
        /// member-scoped walk deliberately does not look at a removed member, so without the section-level
        /// clause the skip consumed the element count and then nothing at all.
        /// <para>
        /// The kept case is not repeated here: the existing member walk already reports it with the member
        /// and the position inside its shape named, which is the better message
        /// (<c>ArrayShapeNestedArgumentMigrationTests.An_Argument_Id_Nothing_Describes_Is_Refused_Not_Skipped</c>),
        /// and it is asked first precisely so this clause does not pre-empt it.
        /// </para>
        /// </summary>
        [Fact]
        public void An_Argument_Identity_Nothing_Describes_Is_Refused_Behind_A_Removed_Member()
        {
            var file = File(
                root: typeof(McsRemovedHolder),
                writeEntries: (s) =>
                {
                    s.Class(RootId, Name(typeof(McsRemovedHolder)), ("Items", EntryId), ("After", WellKnownTypes.Int32));
                    s.Generic(EntryId, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List, UndescribedId);
                },
                writePayload: (w, _) =>
                {
                    SchemaSection.WriteLeb128(w, RootId);
                    SameType(w);
                    SameType(w);
                    SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                    SerializerBinary.WriteInt32Fixed(w, 11);
                    SerializerBinary.WriteInt32Fixed(w, 12);
                    SerializerBinary.WriteInt32Fixed(w, Sentinel);
                });

            var ex = Record.Exception(() => New(RemovedCtx).Load<McsRemovedHolder>(file, 1));

            // A schema error, not a corruption one: the definition is one the format lays out, and the only
            // defect is an identity the file's own id space does not contain — which is a question about
            // what this file can be read as, not about the section contradicting itself.
            var schema = Assert.IsType<CerasSchemaException>(ex);
            Assert.Contains("System.Collections.Generic.List`1<System.Int32>", schema.Message);
            Assert.Contains("argument 0", schema.Message);
            Assert.Contains($"type id {UndescribedId}", schema.Message);
            Assert.Contains("before the root type id", schema.Message);
        }

        /// <summary>
        /// Both refusals are decided while the old-to-current schema is validated, which is *before* the
        /// stored root type id is read. A file whose payload names a root type this build does not declare
        /// is therefore still refused for its schema — a different message, from a different check, that
        /// would otherwise be the one to fire. The two orders are distinguished by the message alone, which
        /// is why this is a separate test rather than a comment.
        /// </summary>
        [Fact]
        public void The_Entry_Refusal_Precedes_The_Stored_Root_Type_Check()
        {
            var file = File(
                root: typeof(McsRemovedHolder),
                writeEntries: (s) =>
                {
                    s.Class(RootId, Name(typeof(McsRemovedHolder)), ("Items", EntryId), ("After", WellKnownTypes.Int32));
                    s.Generic(EntryId, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List, UndescribedId);
                },
                writePayload: (w, _) =>
                {
                    // A root id the section does not describe, so the root check would say "not declared in
                    // this context" if it were the one to fire.
                    SchemaSection.WriteLeb128(w, UndescribedId);
                    SameType(w);
                    SameType(w);
                    SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                    SerializerBinary.WriteInt32Fixed(w, 11);
                    SerializerBinary.WriteInt32Fixed(w, 12);
                    SerializerBinary.WriteInt32Fixed(w, Sentinel);
                });

            var ex = Record.Exception(() => New(RemovedCtx).Load<McsRemovedHolder>(file, 1));

            // The structural fact, and *not* the root check's own wording, which is what a load that read
            // the stored root id first would have produced.
            var schema = Assert.IsType<CerasSchemaException>(ex);
            Assert.Contains("argument 0", schema.Message);
            Assert.DoesNotContain("does not match the requested load type", schema.Message);
        }

        // ── The diagnostic still works on the file it refuses ────────────────────────────────

        /// <summary>
        /// <c>CerasRobustSerializer.DumpSchema</c> is what a caller reaches for when a load was refused,
        /// so it has to work on precisely the file the load path refuses — and to say why, rather than
        /// throw the same exception or quietly hide it. A dump that could not show a broken section would
        /// be useless on the one input it is needed for.
        /// </summary>
        [Fact]
        public void The_Schema_Dump_Shows_A_Section_A_Load_Refuses_And_Says_Why()
        {
            var file = File(
                root: typeof(McsRemovedHolder),
                writeEntries: (s) =>
                {
                    s.Class(RootId, Name(typeof(McsRemovedHolder)), ("Items", EntryId), ("After", WellKnownTypes.Int32));
                    s.Generic(EntryId, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List);
                },
                writePayload: (w, _) =>
                {
                    SchemaSection.WriteLeb128(w, RootId);
                    SameType(w);
                    SameType(w);
                    SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                    SerializerBinary.WriteInt32Fixed(w, 11);
                    SerializerBinary.WriteInt32Fixed(w, 12);
                    SerializerBinary.WriteInt32Fixed(w, Sentinel);
                });

            // The load refuses it…
            Assert.IsType<CerasDataCorruptionException>(
                Record.Exception(() => New(RemovedCtx).Load<McsRemovedHolder>(file, 1)));

            // …and the dump still lists the section, with the same reason attached to the entry.
            var dump = CerasRobustSerializer.DumpSchema(file);
            Assert.Contains(Name(typeof(McsRemovedHolder)), dump);
            Assert.Contains("System.Collections.Generic.List`1<System.Int32>", dump);
            Assert.Contains("REFUSED BY LOAD", dump);
            Assert.Contains("records 0 arguments", dump);
            Assert.Contains("exactly 1", dump);

            // The control: a file whose section is sound produces no such comment, so the marker cannot
            // be satisfied by a dump that always says it.
            var sound = File(
                root: typeof(McsRemovedHolder),
                writeEntries: (s) =>
                {
                    s.Class(RootId, Name(typeof(McsRemovedHolder)), ("Items", EntryId), ("After", WellKnownTypes.Int32));
                    s.Generic(EntryId, "System.Collections.Generic.List`1<System.Int32>", WellKnownTypes.List, WellKnownTypes.Int32);
                },
                writePayload: (w, _) =>
                {
                    SchemaSection.WriteLeb128(w, RootId);
                    SameType(w);
                    SameType(w);
                    SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                    SerializerBinary.WriteInt32Fixed(w, 11);
                    SerializerBinary.WriteInt32Fixed(w, 12);
                    SerializerBinary.WriteInt32Fixed(w, Sentinel);
                });
            Assert.DoesNotContain("REFUSED BY LOAD", CerasRobustSerializer.DumpSchema(sound));
            Assert.Equal(Sentinel, New(RemovedCtx).Load<McsRemovedHolder>(sound, 1).After);
        }

        // ── Door 3 as the interpreter's own boundary ───────────────────────────────────────────

        /// <summary>
        /// The interpreter door, with no <c>Load</c> and no section parser in the way: a caller that drives
        /// <see cref="SchemaTraversalEngine"/> with schemas it built itself goes through
        /// <c>MigrationPlanBuilder.BuildAll</c> in the engine's constructor, and that is where the invariant
        /// is asked. The refusal therefore happens while the engine is being built — before a reader
        /// exists, so before any payload byte could be interpreted in either direction.
        /// </summary>
        [Fact]
        public void The_Interpreter_Itself_Refuses_A_Malformed_Collection_Entry_It_Was_Handed_Directly()
        {
            var serializer = New(IntCtx);
            serializer.Save(new McsIntHolder { Items = new List<int> { 1 }, After = Sentinel }, 1);   // seal + bake

            const string listName = "System.Collections.Generic.List`1<System.Int32>";

            var malformed = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal)
            {
                [Name(typeof(McsIntHolder))] = new OldTypeSchema(RootId, Name(typeof(McsIntHolder)),
                    Array.Empty<string>(), SchemaTypeKind.Class,
                    new[]
                    {
                        new OldFieldSchema("Items", Array.Empty<string>(), EntryId, false),
                        new OldFieldSchema("After", Array.Empty<string>(), WellKnownTypes.Int32, true),
                    },
                    0, 0, null, null),
                [listName] = new OldTypeSchema(EntryId, listName, Array.Empty<string>(),
                    SchemaTypeKind.GenericInstantiation, Array.Empty<OldFieldSchema>(), 0,
                    WellKnownTypes.List, Array.Empty<int>(), null),
            };

            var ex = Assert.Throws<CerasSchemaException>(
                () => new SchemaTraversalEngine(malformed, new string[] { null }, 1, serializer));
            Assert.Contains(listName, ex.Message);
            Assert.Contains("records 0 arguments", ex.Message);
            Assert.Contains("exactly 1", ex.Message);

            // The control: the identical map with the element type recorded builds an engine, which is what
            // makes the refusal above about the missing argument and not about hand-built schemas.
            var valid = new Dictionary<string, OldTypeSchema>(malformed, StringComparer.Ordinal)
            {
                [listName] = new OldTypeSchema(EntryId, listName, Array.Empty<string>(),
                    SchemaTypeKind.GenericInstantiation, Array.Empty<OldFieldSchema>(), 0,
                    WellKnownTypes.List, new[] { WellKnownTypes.Int32 }, null),
            };
            Assert.NotNull(new SchemaTraversalEngine(valid, new string[] { null }, 1, serializer));
        }

        // ── Door 4: the Type value ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The fourth door: an entry synthesized mid-decode from a file's own <c>Type</c> value, which no
        /// schema section records. A <c>Type</c> value naming <c>List</c> with an argument count other than
        /// one names no type at all, and the payload that follows it cannot be counted. On the read path
        /// this used to surface as a bare reflection <c>ArgumentException</c> out of
        /// <c>MakeGenericType</c>; on the skip path, which never closes a CLR type, it would have consumed
        /// nothing per element and desynchronized the rest of the save.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void A_Type_Value_That_Names_A_Definition_With_The_Wrong_Argument_Count_Is_Refused(int declaredCount)
        {
            var ex = Record.Exception(() => New(SlotCtx).Load<McsSlotHolder>(SlotFile(declaredCount, withCollectionPayload: false), 1));

            var corrupt = Assert.IsType<CerasDataCorruptionException>(ex);   // exact, not the ArgumentException it replaced
            Assert.Contains("Corrupt System.Type payload", corrupt.Message);
            Assert.Contains("'List`1' declares", corrupt.Message);
            Assert.Contains("takes exactly 1", corrupt.Message);
        }

        /// <summary>The control for the refusal above: the same file with the definition's own count, which
        /// decodes the collection and leaves the trailing field aligned.</summary>
        [Fact]
        public void A_Type_Value_That_Names_A_Definition_With_Its_Own_Argument_Count_Still_Decodes()
        {
            var loaded = New(SlotCtx).Load<McsSlotHolder>(SlotFile(1, withCollectionPayload: true), 1);

            Assert.Equal(new[] { 11, 12 }, Assert.IsType<List<int>>(loaded.Items));
            Assert.Equal(Sentinel, loaded.After);
        }

        /// <summary>
        /// A hand-assembled file whose root's <c>object</c> member holds a <c>List&lt;int&gt;</c> named by a
        /// <c>Type</c> value with <paramref name="declaredCount"/> generic arguments. The slot is the whole
        /// of the polymorphic grammar here: a reference marker, the type value, then the collection's own
        /// payload.
        /// </summary>
        static byte[] SlotFile(int declaredCount, bool withCollectionPayload)
        {
            return File(
                root: typeof(McsSlotHolder),
                writeEntries: (s) => s.Class(RootId, Name(typeof(McsSlotHolder)),
                    ("Items", WellKnownTypes.Object), ("After", WellKnownTypes.Int32)),
                writePayload: (w, _) =>
                {
                    SchemaSection.WriteLeb128(w, RootId);
                    SameType(w);
                    SerializerBinary.WriteUInt32Bias(w, Protocol.RefNewValue, Protocol.RefBias);
                    SchemaSection.WriteLeb128(w, (uint)WellKnownTypes.List);
                    SchemaSection.WriteLeb128(w, (uint)declaredCount);
                    SchemaSection.WriteLeb128(w, (uint)WellKnownTypes.Int32);
                    if (withCollectionPayload)
                    {
                        SerializerBinary.WriteUInt32Bias(w, 2, Protocol.CollectionBias);
                        SerializerBinary.WriteInt32Fixed(w, 11);
                        SerializerBinary.WriteInt32Fixed(w, 12);
                    }
                    SerializerBinary.WriteInt32Fixed(w, Sentinel);
                });
        }

        // ── The hand-assembled file ───────────────────────────────────────────────────────────

        /// <summary>
        /// Writes one schema entry. The section is assembled byte by byte rather than baked from a
        /// <see cref="SchemaRegistry"/>, which is the point: a malformed entry cannot be produced by the
        /// encoder any more (door 1 refuses it), so a file carrying one has to be written by hand — and a
        /// hand-written section is also the honest way to say "this file is not something a Ceras Save
        /// emitted".
        /// </summary>
        public sealed class Section
        {
            public CerasSerializer.FixedArrayBufferWriter Writer;
            public readonly StringInternTable Intern = new();
            public int Count;

            /// <summary>A class entry: name, no previous names, the member list, then the fixed byte size.</summary>
            public void Class(int id, string name, params (string Name, int TypeId)[] fields)
            {
                SchemaSection.WriteLeb128(Writer, (uint)id);
                SchemaSection.WriteInternedString(Writer, name, Intern);
                SchemaSection.WriteLeb128(Writer, 0);
                Writer.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; Writer.Advance(1);
                SchemaSection.WriteLeb128(Writer, (uint)fields.Length);
                foreach (var (member, typeId) in fields) Member(member, typeId);
                SchemaSection.WriteLeb128(Writer, 0);
                Count++;
            }

            /// <summary>A <c>GenericInstantiation</c> entry: the definition id, the ordered argument ids,
            /// then the class tail (no members, then the fixed byte size).</summary>
            public void Generic(int id, string name, int definitionId, params int[] argIds)
            {
                SchemaSection.WriteLeb128(Writer, (uint)id);
                SchemaSection.WriteInternedString(Writer, name, Intern);
                SchemaSection.WriteLeb128(Writer, 0);
                Writer.GetSpan(1)[0] = (byte)SchemaTypeKind.GenericInstantiation; Writer.Advance(1);
                SchemaSection.WriteLeb128(Writer, (uint)definitionId);
                SchemaSection.WriteLeb128(Writer, (uint)argIds.Length);
                foreach (var a in argIds) SchemaSection.WriteLeb128(Writer, (uint)a);
                SchemaSection.WriteLeb128(Writer, 0);
                SchemaSection.WriteLeb128(Writer, 0);
                Count++;
            }

            void Member(string name, int typeId)
            {
                SchemaSection.WriteInternedString(Writer, name, Intern);
                SchemaSection.WriteLeb128(Writer, 0);
                SchemaSection.WriteLeb128(Writer, (uint)typeId);
                Writer.GetSpan(1)[0] = 0; Writer.Advance(1);   // not monomorphic: a marker is written for it
            }
        }

        static void SameType(IBufferWriter<byte> w)
            => SerializerBinary.WriteUInt32Bias(w, Protocol.RefNewValueSameType, Protocol.RefBias);

        /// <summary>A complete robust file with a hand-written schema section and a valid header checksum
        /// (<c>FinalizeHeader</c> recomputes the CRC over the whole block, so the integrity check passes and
        /// what a test then refuses is the schema rule and nothing else).</summary>
        static byte[] File(Type root, Action<Section> writeEntries, Action<CerasSerializer.FixedArrayBufferWriter, int[]> writePayload,
            string[] payloadStrings = null)
        {
            var buffer = new byte[2048];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var section = new Section { Writer = new CerasSerializer.FixedArrayBufferWriter(buffer, offset) };
            int countAt = section.Writer.Offset;
            SchemaSection.WriteLeb128(section.Writer, 0);   // patched below: only the builders know the count
            writeEntries(section);
            buffer = section.Writer.Buffer; offset = section.Writer.Offset;
            buffer[countAt] = checked((byte)section.Count);   // one byte: a section this small never reaches 128

            int schemaStringOffset = offset;
            section.Intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            // The payload string table is interned before the payload is written, so a hand-written entry
            // can name a key by the index the writer's own table would have given it.
            var payloadTable = new StringInternTable();
            var keyIndices = (payloadStrings ?? Array.Empty<string>()).Select(payloadTable.Intern).ToArray();

            var payload = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            writePayload(payload, keyIndices);
            buffer = payload.Buffer; offset = payload.Offset;

            int stringTableStart = offset;
            payloadTable.Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }

        /// <summary>The file for one row: the collection member <em>kept</em> in the current model, and the
        /// entry written either as a writer would write it or with its definition's arguments removed.</summary>
        static byte[] File(MalformedShape shape, Action<Section, int> writeEntry, Type into = null)
        {
            Type root = into ?? shape.KeptRoot;

            return File(
                root: root,
                payloadStrings: shape.PayloadStrings,
                writeEntries: (s) =>
                {
                    s.Class(RootId, Name(root), ("Items", EntryId), ("After", WellKnownTypes.Int32));
                    writeEntry(s, EntryId);
                },
                writePayload: (w, keys) =>
                {
                    SchemaSection.WriteLeb128(w, RootId);
                    SameType(w);                                        // the root, through its reference wrapper
                    if (shape.MemberIsMarked) SameType(w);              // and the member's, if it has one
                    shape.WriteCollection(w, keys);
                    SerializerBinary.WriteInt32Fixed(w, Sentinel);
                });
        }

        static uint StoredCrc(byte[] file)
            => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(RobustHeader.ChecksumOffset));

        /// <summary>
        /// The header checksum is intact. Asserted on every file a refusal is claimed for, because the whole
        /// classification rests on it: the CRC covers the schema section too, so a section that contradicts
        /// itself in a file whose checksum is valid was not produced by a <c>Save</c> — it is damaged or
        /// hand-assembled, and the message says exactly that.
        /// </summary>
        static void AssertValidChecksum(byte[] file)
        {
            var copy = (byte[])file.Clone();
            var computed = Ceras.Helpers.HashHelpers.CalculateCrc32WithZeroedRange(
                copy, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize);
            Assert.Equal(StoredCrc(file), computed);
        }
    }
}
