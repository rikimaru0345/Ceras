// TODO P0 0.8.9: `Try*`/`Can*` predicates that throw.
//
// `WellKnownTypes.TryResolveCollectionShape` used to raise `CerasSchemaException` for a Queue<>/Stack<>
// subclass, and it was reached from `SchemaSection.CanAutoRegister` — itself a `Can*` that raised — so a
// method whose name promised a boolean raised from inside `SchemaRegistry.Seal()` (mid-`Save`), and again
// from the migration decoder mid-decode. Failing loudly is right; the names were not.
//
// The contract pinned here:
//  * the classifier is *total*: it answers with a reason and never throws, so it can be asked about any
//    type without a guard (this file calls it on every loaded type to prove that);
//  * both fail-loud sites keep failing loudly, with the *same* sentence for the same defect, so the
//    bake and the decoder cannot describe one problem two different ways;
//  * the bake-time error names the declaring type and the member that reached the unsupported shape; the
//    decode-time error names the file's stable name, and is a schema error rather than a corruption
//    error — the bytes are irrelevant there, it is this build's model that cannot describe the shape;
//  * every *supported* collection shape still resolves, bakes and decodes, with a field after it to
//    prove the stream stayed aligned.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.CollectionShapes
{
    // ── The shapes under test ───────────────────────────────────────────────────────────────

    public class CsrPlainQueue : Queue<int> { }
    public class CsrPlainStack : Stack<int> { }
    public class CsrPlainList : List<int> { }
    public class CsrPlainSet : HashSet<int> { }
    public class CsrPlainDict : Dictionary<string, int> { }

    /// <summary>
    /// No public parameterless constructor: Ceras builds a <c>List&lt;int&gt;</c> and wraps it. Used by
    /// the classifier tests only, never by the end-to-end round trip: the runtime
    /// <c>CollectionFormatterResolver</c> hands a <c>ReadOnlyCollection&lt;T&gt;</c> <b>subclass</b> the
    /// dedicated <c>ReadOnlyCollectionFormatter&lt;T&gt;</c>, which builds the built-in wrapper instead of
    /// the subclass, so writing one fails in the reference wrapper. That is a formatter-selection
    /// defect, not a shape-classification one, and it is outside this item.
    /// </summary>
    public class CsrWrappedList : ReadOnlyCollection<int>
    {
        public CsrWrappedList(IList<int> inner) : base(inner) { }
    }

    // One context per rejected shape: a shape refused at bake time poisons the context it is
    // registered in, so an unsupported shape never shares a context with a supported one.

    [CerasContext("CsrSealQueue")]
    [CerasSerializable]
    public class CsrQueueMemberHolder
    {
        public CsrPlainQueue Q;
        public int After;
    }

    [CerasContext("CsrSealStack")]
    [CerasSerializable]
    public class CsrStackMemberHolder
    {
        public CsrPlainStack S;
        public int After;
    }

    /// <summary>
    /// The decode counterpart's model: the current member is an <c>object</c>, so the *file* decides
    /// which collection shape is being read. That is what makes the unsupported shape reachable
    /// through a real <c>Load</c> — the current manifest can never contain a Queue/>/Stack<> subclass,
    /// because the bake refuses it (see the two holders above).
    /// </summary>
    [CerasContext("CsrDecode")]
    [CerasSerializable]
    public class CsrDecodeHolder
    {
        public object Items;
        public int After;
    }

    /// <summary>
    /// The shapes the format describes today. No <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> *member* here:
    /// their formatters write the element count as a raw 4-byte int instead of the documented biased
    /// varint (see <c>Format/LAYOUT.md</c>), so a save containing one cannot be read back by the
    /// migration interpreter. That is a wire-format/reader disagreement of its own, unrelated to shape
    /// classification, and the shape itself is covered by the classifier tests above.
    /// </summary>
    [CerasContext("CsrSupported")]
    [CerasSerializable]
    public class CsrSupportedHolder
    {
        public List<int> List;
        public Dictionary<string, int> Dict;
        public HashSet<int> Set;
        public CsrPlainList PlainList;
        public CsrPlainSet PlainSet;
        public CsrPlainDict PlainDict;
        public int After;
    }

    public class CollectionShapeRejectionTests : TestBase
    {
        const string SealQueueCtx = "CsrSealQueue";
        const string SealStackCtx = "CsrSealStack";
        const string DecodeCtx = "CsrDecode";
        const string SupportedCtx = "CsrSupported";

        static CerasRobustSerializer New(string context) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));

        static string Name(Type type) => TypeIdentity.GetCanonicalName(type);

        // ── 1. The classifier answers; it does not raise ───────────────────────────────────

        /// <summary>
        /// The defect itself: a <c>Try*</c> that raises. A caller that merely wants to know whether a
        /// shape is describable must be able to ask without a try/catch, and the answer for a
        /// Queue<>/Stack<> subclass is "no, and here is why" — not an exception out of a predicate.
        /// </summary>
        [Fact]
        public void Queue_And_Stack_Subclasses_Are_Reported_By_The_Predicate_Not_Thrown_From_It()
        {
            foreach (var type in new[] { typeof(CsrPlainQueue), typeof(CsrPlainStack) })
            {
                var escaped = Record.Exception(() =>
                    WellKnownTypes.TryResolveCollectionShape(type, out Type def, out Type[] args, out CollectionShapeRejection rejection));

                // Nothing escaped the predicate at all.
                Assert.Null(escaped);

                Assert.False(WellKnownTypes.TryResolveCollectionShape(type, out var resolvedDef, out var resolvedArgs, out var rejection));
                Assert.Equal(CollectionShapeRejection.UnsupportedQueueOrStack, rejection);
                Assert.Null(resolvedDef);
                Assert.Null(resolvedArgs);

                // And the reason is actionable, not just a flag.
                string reason = WellKnownTypes.DescribeCollectionShapeRejection(type, rejection);
                Assert.Contains(Name(type), reason);
                Assert.Contains("Queue", reason);
                Assert.Contains("Stack", reason);
            }
        }

        /// <summary>
        /// The counterweight: every shape Ceras does support still resolves to its definition plus
        /// ordered arguments, and reports no rejection. Without this the rejection above could be
        /// satisfied by refusing everything.
        /// </summary>
        [Fact]
        public void Every_Supported_Shape_Still_Resolves_To_Its_Definition_And_Arguments()
        {
            var expected = new (Type Type, Type Definition, Type[] Args)[]
            {
                (typeof(List<int>),                  typeof(List<>),                  new[] { typeof(int) }),
                (typeof(Dictionary<string, int>),     typeof(Dictionary<,>),           new[] { typeof(string), typeof(int) }),
                (typeof(HashSet<int>),                typeof(HashSet<>),                new[] { typeof(int) }),
                (typeof(Queue<int>),                  typeof(Queue<>),                  new[] { typeof(int) }),
                (typeof(Stack<int>),                  typeof(Stack<>),                  new[] { typeof(int) }),
                (typeof(CsrPlainList),                typeof(List<>),                   new[] { typeof(int) }),
                (typeof(CsrPlainSet),                 typeof(HashSet<>),                new[] { typeof(int) }),
                (typeof(CsrPlainDict),                typeof(Dictionary<,>),           new[] { typeof(string), typeof(int) }),
                (typeof(CsrWrappedList),              typeof(List<>),                   new[] { typeof(int) }),
            };

            foreach (var (type, definition, args) in expected)
            {
                Assert.True(WellKnownTypes.TryResolveCollectionShape(type, out var def, out var gotArgs, out var rejection),
                    $"{Name(type)} should resolve");
                Assert.Equal(CollectionShapeRejection.None, rejection);
                Assert.Equal(definition, def);
                Assert.Equal(args, gotArgs);
            }
        }

        /// <summary>
        /// A type that is not a describable collection shape is not an error: it simply is not one,
        /// and the reason stays <c>None</c> so callers can tell "nothing shape-specific to report"
        /// from a real rejection. An array is described as <c>T[]</c> by its own structural schema, so
        /// it is not a collection shape either.
        /// </summary>
        [Fact]
        public void Non_Collection_Types_Report_No_Rejection()
        {
            foreach (var type in new Type[] { null, typeof(int), typeof(string), typeof(int[]), typeof(CsrDecodeHolder) })
            {
                Assert.False(WellKnownTypes.TryResolveCollectionShape(type, out var def, out var args, out var rejection),
                    $"{type?.Name ?? "<null>"} is not a collection shape");
                Assert.Equal(CollectionShapeRejection.None, rejection);
                Assert.Null(def);
                Assert.Null(args);
            }
        }

        /// <summary>
        /// Every rejection value has an actionable sentence, and it names the type it is about — a
        /// message built for a diagnostic must never throw a second exception while reporting the
        /// first (so a null type is safe too, it degrades to a placeholder).
        /// </summary>
        [Fact]
        public void Every_Rejection_Describes_Itself_And_Names_The_Type()
        {
            foreach (CollectionShapeRejection rejection in Enum.GetValues(typeof(CollectionShapeRejection)))
            {
                string withType = WellKnownTypes.DescribeCollectionShapeRejection(typeof(CsrPlainQueue), rejection);
                Assert.False(string.IsNullOrWhiteSpace(withType));
                Assert.Contains(Name(typeof(CsrPlainQueue)), withType);

                // A diagnostic must be buildable for a type the reader could not resolve.
                Assert.False(string.IsNullOrWhiteSpace(WellKnownTypes.DescribeCollectionShapeRejection(null, rejection)));
            }
        }

        /// <summary>
        /// The two predicates that decide what Ceras can write have to agree: anything the built-in
        /// collection formatter serves must either resolve to a describable shape or report a specific
        /// rejection. A bare <c>false</c> for such a type is the dangerous case — the bake would report
        /// it as an ordinary undeclared type and tell the user to declare a type no manifest can
        /// describe. (The <c>UninferrableElementType</c> rejection exists for exactly that case; while
        /// the two predicates agree it is unreachable, and this test is what says so.)
        /// </summary>
        [Fact]
        public void Every_Shape_The_Collection_Formatter_Serves_Either_Resolves_Or_Reports_A_Rejection()
        {
            // Touch the assemblies whose BCL collection types carry the interesting shapes, so the
            // sweep below is not empty even when the test host has loaded little.
            _ = typeof(List<>).Assembly;
            _ = typeof(ReadOnlyCollection<int>).Assembly;
            _ = typeof(Queue<>).Assembly;
            _ = typeof(CsrPlainList).Assembly;

            int served = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }

                foreach (var type in types)
                {
                    if (!WellKnownTypes.IsCollectionFormatterShape(type)) continue;
                    served++;

                    if (WellKnownTypes.TryResolveCollectionShape(type, out _, out _, out var rejection))
                        Assert.Equal(CollectionShapeRejection.None, rejection);
                    else
                        Assert.NotEqual(CollectionShapeRejection.None, rejection);
                }
            }

            // A sweep that found nothing would pass vacuously, so require it to have found something.
            Assert.True(served > 0, "the sweep saw no collection shapes at all");
        }

        // ── 2. The bake (seal) still refuses the shape, and names the member ───────────────

        [Fact]
        public void Queue_Subclass_Member_Is_Refused_At_Seal_Naming_The_Declaring_Type_And_Member()
        {
            // Constructing the serializer (which resolves every generated formatter) must not be what
            // fails: the refusal belongs to the bake, and it leaves the manifest unsealed rather than
            // half-frozen, so nothing later can read a partially baked schema.
            var s = New(SealQueueCtx);
            Assert.False(s.Config.SchemaRegistry.IsSealed);

            var ex = Assert.Throws<CerasSchemaException>(
                () => s.Save(new CsrQueueMemberHolder { Q = new CsrPlainQueue(), After = 1 }, 1));

            Assert.False(s.Config.SchemaRegistry.IsSealed);
            Assert.Contains(Name(typeof(CsrQueueMemberHolder)), ex.Message);
            Assert.Contains("field 'Q'", ex.Message);
            Assert.Contains(WellKnownTypes.DescribeCollectionShapeRejection(
                typeof(CsrPlainQueue), CollectionShapeRejection.UnsupportedQueueOrStack), ex.Message);
        }

        [Fact]
        public void Stack_Subclass_Member_Is_Refused_At_Seal_Naming_The_Declaring_Type_And_Member()
        {
            var s = New(SealStackCtx);

            var ex = Assert.Throws<CerasSchemaException>(
                () => s.Save(new CsrStackMemberHolder { S = new CsrPlainStack(), After = 1 }, 1));

            Assert.Contains(Name(typeof(CsrStackMemberHolder)), ex.Message);
            Assert.Contains("field 'S'", ex.Message);
            Assert.Contains(WellKnownTypes.DescribeCollectionShapeRejection(
                typeof(CsrPlainStack), CollectionShapeRejection.UnsupportedQueueOrStack), ex.Message);
        }

        /// <summary>
        /// One defect, one sentence. The bake refuses the shape when it is asked to describe it
        /// directly (a declared root) and when it is reached through a member; the decoder refuses the
        /// same shape mid-decode. All three must carry the same reason text, so a user reading whichever
        /// error they hit gets the same explanation and the same fix.
        /// </summary>
        [Fact]
        public void The_Three_Fail_Loud_Sites_Report_One_Reason()
        {
            string reason = WellKnownTypes.DescribeCollectionShapeRejection(
                typeof(CsrPlainQueue), CollectionShapeRejection.UnsupportedQueueOrStack);

            var fromDeclaration = Record.Exception(() =>
                New(SupportedCtx).Config.SchemaRegistry.DeclareRoot(typeof(CsrPlainQueue)));
            var fromMemberBake = Record.Exception(() =>
                New(SealQueueCtx).Save(new CsrQueueMemberHolder { Q = new CsrPlainQueue() }, 1));
            var fromDecode = Record.Exception(() =>
                New(DecodeCtx).Load<CsrDecodeHolder>(
                    LegacyCollectionMemberFile(typeof(CsrPlainQueue), "System.Collections.Generic.Queue`1", 2), 1));

            Assert.IsType<CerasSchemaException>(fromDeclaration);
            Assert.IsType<CerasSchemaException>(fromMemberBake);
            Assert.IsType<CerasSchemaException>(fromDecode);

            Assert.Contains(reason, fromDeclaration.Message);
            Assert.Contains(reason, fromMemberBake.Message);
            Assert.Contains(reason, fromDecode.Message);
        }

        // ── 3. The migration decoder refuses the same shape, and says whose fault it is ────

        /// <summary>
        /// A save that records a collection payload for a type this build cannot describe. The bytes
        /// are irrelevant — the count is a perfectly good count, and it is the *current* model that has
        /// no describable shape — so this must be a <see cref="CerasSchemaException"/> and not a
        /// <see cref="CerasDataCorruptionException"/>, which would send the user hunting for bad data.
        /// The two are siblings, so asserting the exact type is what pins the distinction.
        /// </summary>
        [Fact]
        public void Decoding_A_Collection_Payload_For_A_Queue_Subclass_Is_A_Schema_Error()
        {
            var s = New(DecodeCtx);
            var file = LegacyCollectionMemberFile(typeof(CsrPlainQueue), "System.Collections.Generic.Queue`1", count: 2);

            var ex = Record.Exception(() => s.Load<CsrDecodeHolder>(file, 1));

            var schema = Assert.IsType<CerasSchemaException>(ex);
            Assert.Contains(Name(typeof(CsrPlainQueue)), schema.Message);   // the file's stable name for the value
            Assert.Contains("Queue<>/Stack<>", schema.Message);             // why it cannot be described
            Assert.Contains("ICollection<T>", schema.Message);             // what to do instead
        }

        [Fact]
        public void Decoding_A_Collection_Payload_For_A_Stack_Subclass_Is_A_Schema_Error()
        {
            var s = New(DecodeCtx);
            var file = LegacyCollectionMemberFile(typeof(CsrPlainStack), "System.Collections.Generic.Stack`1", count: 1);

            var schema = Assert.IsType<CerasSchemaException>(
                Record.Exception(() => s.Load<CsrDecodeHolder>(file, 1)));

            Assert.Contains(Name(typeof(CsrPlainStack)), schema.Message);
            Assert.Contains("Queue<>/Stack<>", schema.Message);
        }

        /// <summary>
        /// The counterpart, through the identical file builder: the same decoder, the same payload
        /// grammar and the same member — a shape Ceras *can* describe still decodes, and the field after
        /// it still reads, so the refusal above is specific to the unsupported shape and not a general
        /// "the interpreter cannot read collections" failure.
        /// </summary>
        [Fact]
        public void Decoding_A_Collection_Payload_For_A_Supported_Subclass_Still_Works_And_Stays_Aligned()
        {
            var s = New(DecodeCtx);
            var file = LegacyCollectionMemberFile(typeof(CsrPlainSet), "System.Collections.Generic.HashSet`1", count: 2, elements: new[] { 4, 5 });

            var clone = s.Load<CsrDecodeHolder>(file, 1);

            var set = Assert.IsType<CsrPlainSet>(clone.Items);
            Assert.Equal(new[] { 4, 5 }, set.OrderBy(x => x));
            Assert.Equal(4242, clone.After);   // the field after the collection: the stream stayed aligned
        }

        // ── 4. The supported shapes end to end, on both readers ───────────────────────────

        [Fact]
        public void Supported_Collection_Shapes_Still_Bake_And_RoundTrip_On_Both_Paths()
        {
            var s = New(SupportedCtx);
            var original = new CsrSupportedHolder
            {
                List = new List<int> { 1, 2 },
                Dict = new Dictionary<string, int> { ["a"] = 3 },
                Set = new HashSet<int> { 4 },
                PlainList = new CsrPlainList { 5, 6 },
                PlainSet = new CsrPlainSet { 7 },
                PlainDict = new CsrPlainDict { ["b"] = 8 },
                After = 99,
            };

            var bytes = s.Save(original, 1);

            foreach (var clone in new[] { s.Load<CsrSupportedHolder>(bytes, 1), s.Load<CsrSupportedHolder>(SlowPath(bytes), 1) })
            {
                Assert.Equal(new[] { 1, 2 }, clone.List);
                Assert.Equal(3, clone.Dict["a"]);
                Assert.Equal(new[] { 4 }, clone.Set);
                Assert.Equal(new[] { 5, 6 }, clone.PlainList);
                Assert.Equal(new[] { 7 }, clone.PlainSet);
                Assert.Equal(8, clone.PlainDict["b"]);
                Assert.Equal(99, clone.After);
            }
        }

        /// <summary>
        /// A type that is neither declared nor a describable shape is still an ordinary declaration
        /// bug, and still gets the declaration guidance — the classification refactor must not have
        /// turned "not in the manifest" into "Ceras cannot describe this shape". (The same test exists
        /// in <c>SchemaReproTests</c>; it is repeated here because this is the code path that changed.)
        /// </summary>
        [Fact]
        public void An_Undeclared_Ordinary_Type_Still_Reports_As_A_Declaration_Problem()
        {
            // No generated manifest: the registry holds exactly the one hand-written entry below, so
            // the leaf is a real CLR type (the bake resolves it by canonical name) that the context
            // never declared.
            var s = new CerasRobustSerializer(c =>
                c.SchemaRegistry.Register(typeof(CsrDeclaredOnlyHolder), new SchemaTypeInfo(
                    Name(typeof(CsrDeclaredOnlyHolder)), Array.Empty<string>(), SchemaTypeKind.Class,
                    new[]
                    {
                        new SchemaFieldInfo("Leaf", Array.Empty<string>(), Name(typeof(CsrUndeclaredLeaf)), true),
                        new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
                    },
                    0)));

            var ex = Assert.Throws<CerasSchemaException>(() => s.Save(new CsrDeclaredOnlyHolder(), 1));

            Assert.Contains(Name(typeof(CsrDeclaredOnlyHolder)), ex.Message);
            Assert.Contains("field 'Leaf'", ex.Message);
            Assert.Contains(Name(typeof(CsrUndeclaredLeaf)), ex.Message);
            Assert.Contains("CerasKnownType", ex.Message);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────

        /// <summary>The root of the declaration test: a hand-written schema, never generated.</summary>
        public class CsrDeclaredOnlyHolder
        {
            public object Leaf;
            public int After;
        }

        /// <summary>A real type in this build that the context above never declared.</summary>
        public class CsrUndeclaredLeaf { public int Value; }

        /// <summary>
        /// A complete robust file whose schema section is written by hand: the root
        /// <see cref="CsrDecodeHolder"/> whose <c>Items</c> member is recorded as
        /// <paramref name="collectionType"/> described as <c>generic definition + Int32</c>, exactly
        /// what a writer with that shape would have recorded. The fingerprint is 0, so loading it takes
        /// the migration interpreter, and the stable name in the file is what resolves the old entry to
        /// the current CLR type — which is how an unsupported shape reaches the decoder at all (a
        /// current manifest can never contain one: the bake refuses it).
        /// <para>
        /// The payload is <c>root id | root marker | member marker | count | elements | After</c>, so a
        /// decode that stops short of <c>After</c> is visibly wrong.
        /// </para>
        /// </summary>
        static byte[] LegacyCollectionMemberFile(Type collectionType, string definitionName, int count, int[] elements = null)
        {
            var oldRegistry = new CerasRobustConfig().SchemaRegistry;
            oldRegistry.Register(typeof(CsrDecodeHolder), new SchemaTypeInfo(
                Name(typeof(CsrDecodeHolder)), Array.Empty<string>(), SchemaTypeKind.Class,
                new[]
                {
                    new SchemaFieldInfo("Items", Array.Empty<string>(), Name(collectionType), false),
                    new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
                },
                0));
            // An explicit schema, so the "old" build describes the shape even though no Ceras build
            // ever could: this is the file a build without the Queue/>/Stack<> restriction would write.
            oldRegistry.Register(collectionType, new SchemaTypeInfo(
                Name(collectionType), Array.Empty<string>(), SchemaTypeKind.GenericInstantiation,
                Array.Empty<SchemaFieldInfo>(), 0, underlyingTypeName: definitionName,
                argTypeNames: new[] { "System.Int32" }));
            oldRegistry.TryGetId(typeof(CsrDecodeHolder), out int holderId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);
            var intern = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, oldRegistry, intern);
            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)holderId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);  // the holder
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);  // Items
            SerializerBinary.WriteUInt32Bias(payload, count, 1);                                     // element count, bias +1
            var values = elements ?? Enumerable.Range(11, count).ToArray();                            // exactly `count` of them
            Assert.Equal(count, values.Length);
            foreach (var element in values)
                SerializerBinary.WriteInt32Fixed(payload, element);
            SerializerBinary.WriteInt32Fixed(payload, 4242);                                          // After

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;

            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }
    }
}
