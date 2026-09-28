// TODO P0 0.8.9: `Try*`/`Can*` predicates that throw.
//
// `WellKnownTypes.TryResolveCollectionShape` used to raise `CerasSchemaException` for a Queue<>/Stack<>
// subclass, and it was reached from `SchemaSection.CanAutoRegister` — itself a `Can*` that raised — so a
// method whose name promised a boolean raised from inside `SchemaRegistry.Seal()` (mid-`Save`) and again
// from the migration decoder mid-decode. Failing loudly is right; the names were not.
//
// Pinned here, and only where the fix lives:
//  * the classifier answers instead of raising — supported shapes resolve to their definition plus ordered
//    arguments, the unsupported ones report a reason, and a type that is not a collection shape reports
//    nothing (all three in one theory, so no row can pass by accident);
//  * the bake still refuses the shape and now names the declaring type *and the member* that reached it;
//  * the decoder still refuses it, as a schema error rather than a corruption error (the bytes are
//    irrelevant — it is this build's model that has no describable shape), naming the name the file used;
//  * a supported shape still decodes, with the field after it left readable.
//
// Two rejections share the file, because they are the same question asked twice: a Queue<>/Stack<>
// subclass, which no formatter can round-trip, and the owner's temporary collection policy
// (Format/UnsupportedCollectionPolicy), which refuses ReadOnlyCollection<T>/ReadOnlyDictionary<TKey,TValue>
// and their subclasses plus every List<T>/Dictionary<TKey,TValue> subclass. Every new row is paired with a
// supported row through the identical file builder or the identical classifier, so the file cannot be
// satisfied by refusing everything.
//
// The rest of the contract is already pinned elsewhere and is not duplicated here:
// `BuiltinCollectionShapeTests` (supported shapes round-trip on both readers, the existing
// Queue/Stack-subclass refusal) and `SchemaReproTests.Bake_FailsLoudly_ForUndeclaredFieldType` (an
// undeclared ordinary type is still a declaration problem, not a shape problem).
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.CollectionShapes
{
    public class CsrPlainQueue : Queue<int> { }
    public class CsrPlainStack : Stack<int> { }
    public class CsrPlainList : List<int> { }
    public class CsrPlainSet : HashSet<int> { }
    public class CsrPlainDict : Dictionary<string, int> { }
    public class CsrWrappedList : ReadOnlyCollection<int> { public CsrWrappedList(IList<int> inner) : base(inner) { } }
    public class CsrPlainReadOnlyDictionary : ReadOnlyDictionary<string, int>
    {
        public CsrPlainReadOnlyDictionary(IDictionary<string, int> inner) : base(inner) { }
    }
    public class CsrDerivedList : CsrPlainList { }

    /// <summary>
    /// An ordinary <c>IReadOnly*</c> implementation whose name has nothing to do with
    /// <see cref="ReadOnlyCollection{T}"/>. These are API signatures, not collection shapes, and the policy
    /// must leave them alone — a name-shaped check would break hundreds of declarations like these.
    /// </summary>
    public class CsrReadOnlyList : IReadOnlyList<int>
    {
        public int Count => 0;
        public int this[int index] => 0;
        public IEnumerator<int> GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class CsrReadOnlyDictionary : IReadOnlyDictionary<string, int>
    {
        public int this[string key] => 0;
        public IEnumerable<string> Keys => Array.Empty<string>();
        public IEnumerable<int> Values => Array.Empty<int>();
        public int Count => 0;
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out int value) { value = 0; return false; }
        public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
            => Enumerable.Empty<KeyValuePair<string, int>>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // One context per rejected shape: a shape refused at bake time poisons the context it is registered
    // in, so an unsupported shape never shares a context with a supported one.

    [CerasContext("CsrSealQueue")]
    [CerasSerializable]
    public class CsrQueueMemberHolder { public CsrPlainQueue Q; public int After; }

    [CerasContext("CsrSealStack")]
    [CerasSerializable]
    public class CsrStackMemberHolder { public CsrPlainStack S; public int After; }

    /// <summary>
    /// The decode side's model: the member is an <c>object</c>, so the *file* decides which collection
    /// shape is read. That is what makes an unsupported shape reachable through a real <c>Load</c> — the
    /// current manifest can never contain one, because the bake refuses it (the two holders above).
    /// </summary>
    [CerasContext("CsrDecode")]
    [CerasSerializable]
    public class CsrDecodeHolder { public object Items; public int After; }

    /// <summary>
    /// The <see cref="Type"/>-value half of the same story: a member whose declared type is
    /// <see cref="Type"/>, so the file records the *type* the slot refers to rather than a value, and a
    /// save written before the policy existed names a type this build still resolves.
    /// </summary>
    [CerasContext("CsrDecodeType")]
    [CerasSerializable]
    public class CsrDecodeTypeHolder { public Type Value; public int After; }

    public class CollectionShapeRejectionTests : TestBase
    {
        const string DecodeCtx = "CsrDecode";

        static CerasRobustSerializer New(string context) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));
        static string Name(Type type) => TypeIdentity.GetCanonicalName(type);

        /// <summary>The one sentence every fail-loud site uses for the same defect.</summary>
        static string UnsupportedReason(Type type) => WellKnownTypes.DescribeCollectionShapeRejection(
            type, CollectionShapeRejection.UnsupportedQueueOrStack);

        /// <summary>The one sentence every fail-loud site uses for the collection policy.</summary>
        static string PolicyReason(Type type) => WellKnownTypes.DescribeCollectionShapeRejection(
            type, CollectionShapeRejection.UnsupportedCollectionPolicy);

        // ── The classifier: answered, never raised ────────────────────────────────────────

        /// <summary>
        /// Every row is asked through <c>Record.Exception</c> first, so "does not raise" is asserted for
        /// the supported rows too. <c>definitionName == null</c> means "no shape", and the expected
        /// rejection (by name: the enum is internal, and a test class cannot expose it in a public
        /// signature) distinguishes a shape Ceras refuses (<c>UnsupportedQueueOrStack</c>,
        /// <c>UnsupportedCollectionPolicy</c>) from a type that simply is not a collection shape
        /// (<c>None</c>) — the difference the bake acts on.
        /// </summary>
        public static TheoryData<Type, string, string> Shapes => new()
        {
            { typeof(List<int>),               "System.Collections.Generic.List`1",       "None" },
            { typeof(Dictionary<string, int>), "System.Collections.Generic.Dictionary`2", "None" },
            { typeof(HashSet<int>),            "System.Collections.Generic.HashSet`1",   "None" },
            { typeof(CsrPlainSet),             "System.Collections.Generic.HashSet`1",   "None" },
            { typeof(Queue<int>),              "System.Collections.Generic.Queue`1",     "None" },
            { typeof(CsrPlainQueue),           null, "UnsupportedQueueOrStack" },
            { typeof(CsrPlainStack),           null, "UnsupportedQueueOrStack" },

            // The policy: the built-in wrappers and every List<>/Dictionary<,> subclass. The exact
            // List<int> / Dictionary<string,int> rows above are the controls that prove the rule is
            // "a derived type", not "derives from List<T>".
            { typeof(CsrPlainList),            null, "UnsupportedCollectionPolicy" },
            { typeof(CsrDerivedList),          null, "UnsupportedCollectionPolicy" },
            { typeof(CsrPlainDict),            null, "UnsupportedCollectionPolicy" },
            { typeof(CsrWrappedList),          null, "UnsupportedCollectionPolicy" },
            { typeof(ReadOnlyCollection<int>), null, "UnsupportedCollectionPolicy" },
            { typeof(ReadOnlyDictionary<string, int>), null, "UnsupportedCollectionPolicy" },
            { typeof(CsrPlainReadOnlyDictionary), null, "UnsupportedCollectionPolicy" },
            { typeof(List<ReadOnlyCollection<int>>), "System.Collections.Generic.List`1", "None" },
            { typeof(ReadOnlyCollection<int>[]),      null, "None" },   // an array is described as T[] by its own structural schema

            // The interfaces whose *names* start with "ReadOnly" are ordinary API, not shapes: nothing
            // here refuses them, which is what keeps an IReadOnlyList<T> signature working.
            { typeof(IReadOnlyList<int>),             null, "None" },
            { typeof(IReadOnlyDictionary<string,int>), null, "None" },
            { typeof(CsrReadOnlyList),                null, "None" },
            { typeof(CsrReadOnlyDictionary),          null, "None" },
            // A closed generic interface is describable exactly like the class is — the policy's answer is
            // about a *class* that derives from a refused built-in, and an interface derives from nothing.
            { typeof(ICollection<int>),               "System.Collections.Generic.List`1", "None" },
            { typeof(IList<int>),                     "System.Collections.Generic.List`1", "None" },
            { typeof(IDictionary<string, int>),        "System.Collections.Generic.Dictionary`2", "None" },

            { typeof(int),                     null, "None" },   // not a collection shape: nothing to report
            { typeof(int[]),                   null, "None" },   // an array is described as T[] by its own structural schema
            { null,                            null, "None" },   // no type at all
        };

        [Theory]
        [MemberData(nameof(Shapes))]
        public void The_Classifier_Answers_And_Never_Raises(Type type, string definitionName, string expected)
        {
            Assert.Null(Record.Exception(() => WellKnownTypes.TryResolveCollectionShape(type, out _, out _, out _)));

            bool resolved = WellKnownTypes.TryResolveCollectionShape(type, out var definition, out var args, out var rejection);

            Assert.Equal(definitionName != null, resolved);
            Assert.Equal(expected, rejection.ToString());
            if (definitionName != null)
            {
                Assert.Equal(definitionName, Name(definition));
                Assert.NotEmpty(args);
            }
            else
            {
                Assert.Null(definition);
                Assert.Null(args);
            }

            // Whatever the reason, it is describable, and it names the type it is about (a diagnostic must
            // never raise a second exception while reporting the first, hence the null-type row).
            string reason = WellKnownTypes.DescribeCollectionShapeRejection(type, rejection);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            if (type != null) Assert.Contains(Name(type), reason);
        }

        /// <summary>
        /// The predicate itself, asked directly for the four families and their exact built-in
        /// counterparts. The classifier's answer is derived from it, so pinning it here pins the rule the
        /// raw serializer and the banned-type table use as well — including the part the policy exists
        /// for: <c>List&lt;int&gt;</c> itself is not refused.
        /// </summary>
        [Fact]
        public void The_Policy_Matches_A_Derived_Type_And_Leaves_The_Exact_BuiltIn_Alone()
        {
            foreach (var refused in new[]
            {
                typeof(ReadOnlyCollection<int>),
                typeof(ReadOnlyDictionary<string, int>),
                typeof(CsrWrappedList),
                typeof(CsrPlainReadOnlyDictionary),
                typeof(CsrPlainList),
                typeof(CsrDerivedList),
                typeof(CsrPlainDict),
            })
            {
                Assert.True(UnsupportedCollectionPolicy.IsUnsupported(refused), $"{Name(refused)} should be refused");
            }

            // The *wrapper* around a refused type is not itself refused — the walk is over the base chain,
            // not over the generic arguments. A List<ReadOnlyCollection<int>> member is refused because of
            // its *argument* (the bake and the generator both descend into it), not because the List is.
            Assert.False(UnsupportedCollectionPolicy.IsUnsupported(typeof(List<ReadOnlyCollection<int>>)));

            foreach (var allowed in new[]
            {
                null, typeof(int), typeof(string), typeof(object), typeof(int[]), typeof(int[,]),
                typeof(List<int>), typeof(Dictionary<string, int>),
                typeof(List<ReadOnlyCollection<int>>),        // the shape is describable; its argument is not
                typeof(CsrPlainSet), typeof(HashSet<int>), typeof(Queue<int>), typeof(Stack<int>),
                typeof(CsrPlainQueue), typeof(CsrPlainStack),  // already refused, but for the *other* reason
                typeof(LinkedList<int>), typeof(SortedSet<int>), typeof(SortedList<string, int>),
                typeof(IReadOnlyList<int>), typeof(IReadOnlyDictionary<string, int>),
                typeof(ICollection<int>), typeof(IDictionary<string, int>),
                typeof(System.Collections.Immutable.ImmutableArray<int>),
            })
            {
                Assert.False(UnsupportedCollectionPolicy.IsUnsupported(allowed), $"{allowed?.Name ?? "null"} should be allowed");
            }
        }

        // ── The bake: still refused, now with the context only this layer has ───────────────

        [Fact]
        public void The_Bake_Refuses_A_Queue_Or_Stack_Subclass_Member_Naming_Its_Declaring_Type_And_Member()
        {
            var cases = new (string Context, object Root, Type Declaring, string Field)[]
            {
                ("CsrSealQueue", new CsrQueueMemberHolder { Q = new CsrPlainQueue(), After = 1 }, typeof(CsrQueueMemberHolder), "Q"),
                ("CsrSealStack", new CsrStackMemberHolder { S = new CsrPlainStack(), After = 1 }, typeof(CsrStackMemberHolder), "S"),
            };

            foreach (var (context, root, declaring, field) in cases)
            {
                // The refusal is the bake's, not formatter resolution's: the manifest is still unsealed
                // after it, so no partially baked schema can be read back.
                var s = New(context);
                var ex = Assert.Throws<CerasSchemaException>(() => s.Save(root, 1));
                Assert.False(s.Config.SchemaRegistry.IsSealed);

                Assert.Contains(Name(declaring), ex.Message);
                Assert.Contains($"field '{field}'", ex.Message);
                Assert.Contains(UnsupportedReason(declaring.GetField(field)!.FieldType), ex.Message);
            }
        }

        // ── The decoder: still refused, and it says whose fault the failure is ──────────────

        [Fact]
        public void The_Decoder_Refuses_A_Collection_Payload_For_An_Unsupported_Shape_As_A_Schema_Error()
        {
            var cases = new (Type Type, string Definition)[]
            {
                (typeof(CsrPlainQueue), "System.Collections.Generic.Queue`1"),
                (typeof(CsrPlainStack), "System.Collections.Generic.Stack`1"),
            };

            foreach (var (type, definition) in cases)
            {
                var file = LegacyCollectionMemberFile(type, definition, count: 2);
                var ex = Record.Exception(() => New(DecodeCtx).Load<CsrDecodeHolder>(file, 1));

                // A sibling of CerasDataCorruptionException, so asserting the exact type is what pins the
                // distinction: the count in the file is perfectly good, it is the model that has no shape.
                var schema = Assert.IsType<CerasSchemaException>(ex);
                Assert.Contains(Name(type), schema.Message);         // the name the file used for the value
                Assert.Contains(UnsupportedReason(type), schema.Message);  // the same reason the bake gives
                Assert.Contains("ICollection<T>", schema.Message);    // what to use instead
            }
        }

        /// <summary>
        /// The same route for the collection policy, through the identical file builder. This is the
        /// legacy-save case: a file written before the policy existed names a shape that still resolves in
        /// this build, and the current manifest can never contain one — so without a refusal on this path
        /// the interpreter would consume the payload and hand back a null (or, for the declared-member
        /// route, an empty wrapper), i.e. a save that loads with no error and has lost its data.
        /// </summary>
        [Fact]
        public void The_Decoder_Refuses_A_Legacy_File_That_Names_A_Collection_The_Policy_Forbids()
        {
            var cases = new (Type Type, string Definition)[]
            {
                (typeof(ReadOnlyCollection<int>), "System.Collections.Generic.List`1"),
                (typeof(CsrWrappedList), "System.Collections.Generic.List`1"),
                (typeof(ReadOnlyDictionary<string, int>), "System.Collections.Generic.Dictionary`2"),
                (typeof(CsrPlainList), "System.Collections.Generic.List`1"),
                (typeof(CsrPlainDict), "System.Collections.Generic.Dictionary`2"),
            };

            foreach (var (type, definition) in cases)
            {
                // The element bytes do not matter: the refusal precedes the element count, which is the
                // point — a decoder that has to read the count before it can decide would have to guess.
                var file = LegacyCollectionMemberFile(type, definition, count: 2, elements: new[] { 4, 5 });

                Exception ex = Record.Exception(() => New(DecodeCtx).Load<CsrDecodeHolder>(file, 1));

                // A typed schema error, never a bare MissingMethodException / ArgumentNullException and
                // never a silent null: the assertion is on the exact type, so a wrapped or untyped failure
                // cannot pass.
                var schema = Assert.IsType<CerasSchemaException>(ex);
                Assert.IsNotType<MissingMethodException>(schema);
                Assert.IsNotType<ArgumentNullException>(schema);
                Assert.Contains(Name(type), schema.Message);       // the name the file used for the value
                Assert.Contains(PolicyReason(type), schema.Message);  // the same reason the bake gives
            }
        }

        [Fact]
        public void The_Decoder_Still_Reads_A_Supported_Collection_Subclass_And_Stays_Aligned()
        {
            // The counterpart through the identical file builder: the same decoder, the same payload
            // grammar and the same member. So the refusal above is specific to the unsupported shape and
            // not "the interpreter cannot read collections".
            var file = LegacyCollectionMemberFile(typeof(CsrPlainSet), "System.Collections.Generic.HashSet`1", count: 2, elements: new[] { 4, 5 });

            var clone = New(DecodeCtx).Load<CsrDecodeHolder>(file, 1);

            Assert.Equal(new[] { 4, 5 }, Assert.IsType<CsrPlainSet>(clone.Items).OrderBy(x => x));
            Assert.Equal(4242, clone.After);   // the field after the collection: the stream stayed aligned
        }

        /// <summary>
        /// A <c>System.Type</c> slot in a legacy file. The write side refuses a refused type by name, so
        /// the only way one reaches the reader is a file written before the policy existed; the read side
        /// has to reach the same verdict, because a serializer that will not write a value must not be the
        /// one place it comes back from.
        /// <para>
        /// The file's schema records the type as an empty class entry under its own stable name, which is
        /// how a build that could not describe it structurally would have written it — and the type value
        /// is that entry's manifest id, exactly what <c>RobustTypeFormatter</c> wrote.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Decoder_Refuses_A_Legacy_Type_Value_Naming_A_Collection_The_Policy_Forbids()
        {
            var file = LegacyTypeValueFile(typeof(ReadOnlyCollection<int>));

            Exception ex = Record.Exception(() => New("CsrDecodeType").Load<CsrDecodeTypeHolder>(file, 1));

            var banned = Assert.IsType<Formatters.BannedTypeException>(ex);
            Assert.Contains("ReadOnlyCollection<T>", banned.Message, StringComparison.Ordinal);
            Assert.IsNotType<MissingMethodException>(banned);
        }

        /// <summary>The control for the refusal above: the same shape of file naming a supported type.</summary>
        [Fact]
        public void The_Decoder_Still_Reads_A_Legacy_Type_Value_Naming_A_Supported_Shape()
        {
            var file = LegacyTypeValueFile(typeof(List<int>));

            var clone = New("CsrDecodeType").Load<CsrDecodeTypeHolder>(file, 1);

            Assert.Equal(typeof(List<int>), clone.Value);
            Assert.Equal(4242, clone.After);
        }

        /// <summary>
        /// A complete robust file whose <see cref="CsrDecodeTypeHolder"/> <c>Value</c> member is a
        /// <see cref="Type"/> field whose recorded type value is a manifest id for
        /// <paramref name="namedType"/>, described as an empty class entry (the only shape a build that
        /// could not describe it structurally would have written). Fingerprint 0, so the load takes the
        /// migration interpreter. Payload: <c>root id | root marker | the type id | After</c>.
        /// </summary>
        static byte[] LegacyTypeValueFile(Type namedType)
        {
            var old = new CerasRobustConfig().SchemaRegistry;
            old.Register(typeof(CsrDecodeTypeHolder), new SchemaTypeInfo(
                Name(typeof(CsrDecodeTypeHolder)), Array.Empty<string>(), SchemaTypeKind.Class,
                new[]
                {
                    new SchemaFieldInfo("Value", Array.Empty<string>(), "System.Type", true),
                    new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
                },
                0));
            old.Register(namedType, new SchemaTypeInfo(
                Name(namedType), Array.Empty<string>(), SchemaTypeKind.Class,
                Array.Empty<SchemaFieldInfo>(), 0));
            old.TryGetId(typeof(CsrDecodeTypeHolder), out int holderId);
            old.TryGetId(namedType, out int namedId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);
            var strings = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, old, strings);
            int schemaStringOffset = offset;
            strings.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)holderId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);   // the holder
            SchemaSection.WriteLeb128(payload, (uint)namedId);                                            // the type value
            SerializerBinary.WriteInt32Fixed(payload, 4242);                                             // After

            var data = payload.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;

            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }

        /// <summary>
        /// A complete robust file with a hand-written schema section: the root <see cref="CsrDecodeHolder"/>
        /// whose <c>Items</c> member is recorded as <paramref name="collectionType"/> described as
        /// <c><paramref name="definitionName"/> + Int32</c>. The fingerprint is 0, so loading it takes the
        /// migration interpreter, and the file's stable name is what resolves the old entry to the current
        /// CLR type — which is how an unsupported shape reaches the decoder at all. The payload is
        /// <c>root id | root marker | member marker | count | elements | After</c>, so a decode that stops
        /// short of <c>After</c> is visibly wrong. (Same shape as the legacy-file builder in
        /// <c>ReviewFindingsTests</c>.)
        /// </summary>
        static byte[] LegacyCollectionMemberFile(Type collectionType, string definitionName, int count, int[] elements = null)
        {
            var old = new CerasRobustConfig().SchemaRegistry;
            old.Register(typeof(CsrDecodeHolder), new SchemaTypeInfo(
                Name(typeof(CsrDecodeHolder)), Array.Empty<string>(), SchemaTypeKind.Class,
                new[]
                {
                    new SchemaFieldInfo("Items", Array.Empty<string>(), Name(collectionType), false),
                    new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
                },
                0));
            // An explicit schema, so the "old" build describes a shape no Ceras build can: the file a build
            // without the Queue<>/Stack<> restriction would have written.
            old.Register(collectionType, new SchemaTypeInfo(
                Name(collectionType), Array.Empty<string>(), SchemaTypeKind.GenericInstantiation,
                Array.Empty<SchemaFieldInfo>(), 0, underlyingTypeName: definitionName,
                argTypeNames: new[] { "System.Int32" }));
            old.TryGetId(typeof(CsrDecodeHolder), out int holderId);

            var buffer = new byte[1024];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);
            var strings = new StringInternTable();
            SchemaSection.Write(ref buffer, ref offset, old, strings);
            int schemaStringOffset = offset;
            strings.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payload = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payload, (uint)holderId);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);  // the holder
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);  // Items
            SerializerBinary.WriteUInt32Bias(payload, count, 1);                                      // element count, bias +1
            foreach (var element in elements ?? Enumerable.Range(11, count).ToArray())
                SerializerBinary.WriteInt32Fixed(payload, element);
            SerializerBinary.WriteInt32Fixed(payload, 4242);                                           // After

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
