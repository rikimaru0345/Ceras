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

    public class CollectionShapeRejectionTests : TestBase
    {
        const string DecodeCtx = "CsrDecode";

        static CerasRobustSerializer New(string context) => new CerasRobustSerializer(c => c.UseGeneratedFormatters(context));
        static string Name(Type type) => TypeIdentity.GetCanonicalName(type);

        /// <summary>The one sentence every fail-loud site uses for the same defect.</summary>
        static string UnsupportedReason(Type type) => WellKnownTypes.DescribeCollectionShapeRejection(
            type, CollectionShapeRejection.UnsupportedQueueOrStack);

        // ── The classifier: answered, never raised ────────────────────────────────────────

        /// <summary>
        /// Every row is asked through <c>Record.Exception</c> first, so "does not raise" is asserted for
        /// the supported rows too. <c>definitionName == null</c> means "no shape", and the expected
        /// rejection (by name: the enum is internal, and a test class cannot expose it in a public
        /// signature) distinguishes a shape Ceras refuses (<c>UnsupportedQueueOrStack</c>) from a type that
        /// simply is not a collection shape (<c>None</c>) — the difference the bake acts on.
        /// </summary>
        public static TheoryData<Type, string, string> Shapes => new()
        {
            { typeof(List<int>),               "System.Collections.Generic.List`1",       "None" },
            { typeof(Dictionary<string, int>), "System.Collections.Generic.Dictionary`2", "None" },
            { typeof(HashSet<int>),            "System.Collections.Generic.HashSet`1",   "None" },
            { typeof(CsrPlainList),            "System.Collections.Generic.List`1",       "None" },
            { typeof(CsrPlainSet),             "System.Collections.Generic.HashSet`1",   "None" },
            { typeof(CsrPlainDict),            "System.Collections.Generic.Dictionary`2", "None" },
            { typeof(CsrWrappedList),          "System.Collections.Generic.List`1",       "None" },
            { typeof(CsrPlainQueue),           null, "UnsupportedQueueOrStack" },
            { typeof(CsrPlainStack),           null, "UnsupportedQueueOrStack" },
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
