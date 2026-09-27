using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.BuiltinShapes
{
    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsStructuralHolder
    {
        public int? Maybe;
        public (int, string) VT;
        public KeyValuePair<string, int> Kvp;
        public int After;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsNullableOnlyHolder
    {
        public int? Maybe;
        public int After;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsTupleOnlyHolder
    {
        public (int, string) VT;
        public int After;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsKvpOnlyHolder
    {
        public KeyValuePair<string, int> Kvp;
        public int After;
    }

    /// <summary>
    /// TODO 0.5 item 3: nullable, tuple/value-tuple, KeyValuePair and generic user value/reference types
    /// must carry a representation schema (definition + ordered arguments), because a reserved generic
    /// definition ID alone does not describe an instance. Coverage is exercised both with the current CLR
    /// type available (fast + interpreter) and with the old CLR type absent (an independently built file
    /// schema whose stable name/definition does not resolve to a CLR type in this build).
    /// </summary>
    public class BuiltinStructuralShapeTests : TestBase
    {
        const string Ctx = "BuiltinShapes";

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        static byte[] SlowPath(byte[] bytes)
        {
            var copy = (byte[])bytes.Clone();
            copy[RobustHeader.FingerprintOffset] ^= 0xFF;
            FixRobustChecksum(copy);
            return copy;
        }

        static void AssertAllMembers(BsStructuralHolder clone)
        {
            Assert.NotNull(clone);
            Assert.Equal(5, clone.Maybe);
            Assert.Equal((7, "x"), clone.VT);
            Assert.Equal("k", clone.Kvp.Key);
            Assert.Equal(9, clone.Kvp.Value);
            Assert.Equal(3, clone.After);
        }

        [Fact]
        public void Structural_Members_RoundTrip_Both_Paths()
        {
            var s = New();
            var holder = new BsStructuralHolder { Maybe = 5, VT = (7, "x"), Kvp = new KeyValuePair<string, int>("k", 9), After = 3 };
            var bytes = s.Save(holder, 1);

            AssertAllMembers(s.Load<BsStructuralHolder>(bytes, 1));
            AssertAllMembers(s.Load<BsStructuralHolder>(SlowPath(bytes), 1));
        }

        [Fact]
        public void Nullable_Null_RoundTrips_Both_Paths()
        {
            var s = New();
            var bytes = s.Save(new BsNullableOnlyHolder { Maybe = null, After = 11 }, 1);
            foreach (var clone in new[] { s.Load<BsNullableOnlyHolder>(bytes, 1), s.Load<BsNullableOnlyHolder>(SlowPath(bytes), 1) })
            {
                Assert.Null(clone.Maybe);
                Assert.Equal(11, clone.After);
            }
        }

        [Fact]
        public void Structural_Schema_Describes_Ordered_Arguments()
        {
            var s = New();
            s.Save(new BsStructuralHolder { Maybe = 1, VT = (1, "a"), Kvp = new KeyValuePair<string, int>("a", 1) }, 1);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof((int, string)), out var tuple));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, tuple.Kind);
            Assert.Equal("System.ValueTuple`2", tuple.UnderlyingTypeName);
            Assert.Equal(new[] { "System.Int32", "System.String" }, tuple.ArgTypeNames);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(int?), out var nullable));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, nullable.Kind);
            Assert.Equal("System.Nullable`1", nullable.UnderlyingTypeName);
            Assert.Equal(new[] { "System.Int32" }, nullable.ArgTypeNames);

            Assert.True(s.Config.SchemaRegistry.TryGetInfo(typeof(KeyValuePair<string, int>), out var kvp));
            Assert.Equal(SchemaTypeKind.GenericInstantiation, kvp.Kind);
            Assert.Equal("System.Collections.Generic.KeyValuePair`2", kvp.UnderlyingTypeName);
            Assert.Equal(new[] { "System.String", "System.Int32" }, kvp.ArgTypeNames);
        }

        [Fact]
        public void Generic_User_Value_And_Reference_Types_RoundTrip_Both_Paths()
        {
            var s = New();
            var holder = new BsGenericHolder { Value = new BsGenericValue<int> { Item = 4 }, Ref = new BsGenericRef<string> { Item = "r" }, After = 6 };
            var bytes = s.Save(holder, 1);
            foreach (var clone in new[] { s.Load<BsGenericHolder>(bytes, 1), s.Load<BsGenericHolder>(SlowPath(bytes), 1) })
            {
                Assert.NotNull(clone);
                Assert.NotNull(clone.Value);
                Assert.Equal(4, clone.Value.Item);
                Assert.NotNull(clone.Ref);
                Assert.Equal("r", clone.Ref.Item);
                Assert.Equal(6, clone.After);
            }
        }

        // ── Old CLR type absent: decode a hand-built file whose structural shape is independent ──

        [Fact]
        public void Nullable_Member_Decodes_With_Old_Type_Absent()
        {
            // A file schema for the real BsNullableOnlyHolder root whose 'Maybe' field references a
            // Nullable-like structural shape whose *names* do not resolve in this build: only the
            // definition id + argument id are available, exactly the old-type-absent case. The decoder
            // must consume the field by shape (definition + argument) and keep the following field aligned
            // rather than defaulting or desyncing (TODO 0.5 item 3).
            var bytes = BuildLegacyFile(BsNullableOnlyHolderId, "Ceras.Test.BuiltinShapes.BsNullableOnlyHolder", new[]
            {
                new SchemaFieldInfo("Maybe", Array.Empty<string>(), "Legacy.Nullable`1<System.Int32>", true),
                new SchemaFieldInfo("After", Array.Empty<string>(), "System.Int32", true),
            }, extraSchemas: new[]
            {
                ("Legacy.Nullable`1<System.Int32>", WellKnownTypes.Nullable, new[] { WellKnownTypes.Int32 }),
            }, payload: w =>
            {
                SerializerBinary.WriteByte(w, 1);          // presence
                SerializerBinary.WriteInt32(w, 77);        // int element
                SerializerBinary.WriteInt32(w, 4242);      // After
            });

            var loaded = New().Load<BsNullableOnlyHolder>(bytes, 1);
            Assert.NotNull(loaded);
            Assert.Equal(4242, loaded.After);
        }

        // The interpreter's own id for the root must match this, but ValidateStoredRootType accepts a
        // resolved stored type identical to the requested type, so any id works as long as the class
        // entry's stable name resolves to the requested CLR type.
        const int BsNullableOnlyHolderId = 1234;

        /// <summary>
        /// Encodes a robust file with a custom schema where some field/argument identities do not resolve
        /// to any CLR type in the current build (the old-type-absent case). The deliberately wrong
        /// fingerprint forces the interpreter. The header/CRC/etc. are all valid, so only the decoder is
        /// exercised.
        /// </summary>
        static byte[] BuildLegacyFile(int oldRootId, string rootStableName, SchemaFieldInfo[] fields,
            (string name, int definitionId, int[] argIds)[] extraSchemas, Action<ArrayBufferWriter<byte>> payload)
        {
            var buffer = new byte[2048];
            int offset = 0;
            RobustHeader.WritePlaceholder(ref buffer, ref offset, CerasRobustSerializer.FormatVersion, 1, 0);

            var intern = new StringInternTable();
            var body = new ArrayBufferWriter<byte>();

            // Root entry.
            WriteSchemaEntry(body, oldRootId, rootStableName, fields, intern);

            // Extra generic-instantiations (structural shapes) with unresolvable names.
            int id = oldRootId + 1;
            foreach (var extra in extraSchemas)
            {
                WriteGenericSchemaEntry(body, id++, extra.name, extra.definitionId, extra.argIds, intern);
            }

            var countWriter = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(countWriter, (uint)(1 + extraSchemas.Length));
            var countBytes = countWriter.WrittenSpan.ToArray();
            var bodyBytes = body.WrittenSpan.ToArray();

            SerializerBinary.EnsureCapacity(ref buffer, offset, countBytes.Length + bodyBytes.Length);
            countBytes.CopyTo(buffer.AsSpan(offset));
            offset += countBytes.Length;
            bodyBytes.CopyTo(buffer.AsSpan(offset));
            offset += bodyBytes.Length;

            int schemaStringOffset = offset;
            intern.Flush(ref buffer, ref offset);
            int dataStart = offset;

            var payloadWriter = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(payloadWriter, (uint)oldRootId);
            SerializerBinary.WriteUInt32Bias(payloadWriter, Protocol.RefNewValueSameType, Protocol.RefBias);
            payload(payloadWriter);
            var data = payloadWriter.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            offset += data.Length;

            int stringTableStart = offset;
            new StringInternTable().Flush(ref buffer, ref offset, StringTableCompression.Disabled);
            RobustHeader.FinalizeHeader(buffer, offset, 0, schemaStringOffset, dataStart, stringTableStart, 0UL);
            return buffer[..offset];
        }

        static void WriteSchemaEntry(ArrayBufferWriter<byte> body, int id, string stableName, SchemaFieldInfo[] fields, StringInternTable intern)
        {
            SchemaSection.WriteLeb128(body, (uint)id);
            SchemaSection.WriteInternedString(body, stableName, intern);
            SchemaSection.WriteLeb128(body, 0);
            body.GetSpan(1)[0] = (byte)SchemaTypeKind.Class; body.Advance(1);
            SchemaSection.WriteLeb128(body, (uint)fields.Length);
            foreach (var f in fields)
            {
                SchemaSection.WriteInternedString(body, f.StableName, intern);
                SchemaSection.WriteLeb128(body, 0);
                SchemaSection.WriteLeb128(body, (uint)ResolveFieldTypeId(f.TypeName));
                body.GetSpan(1)[0] = (byte)(f.IsMonomorphic ? 1 : 0); body.Advance(1);
            }
            SchemaSection.WriteLeb128(body, 0);
        }

        static void WriteGenericSchemaEntry(ArrayBufferWriter<byte> body, int id, string stableName, int definitionId, int[] argIds, StringInternTable intern)
        {
            SchemaSection.WriteLeb128(body, (uint)id);
            SchemaSection.WriteInternedString(body, stableName, intern);
            SchemaSection.WriteLeb128(body, 0);
            body.GetSpan(1)[0] = (byte)SchemaTypeKind.GenericInstantiation; body.Advance(1);
            SchemaSection.WriteLeb128(body, (uint)definitionId);
            SchemaSection.WriteLeb128(body, (uint)argIds.Length);
            foreach (var a in argIds) SchemaSection.WriteLeb128(body, (uint)a);
            SchemaSection.WriteLeb128(body, 0);
            SchemaSection.WriteLeb128(body, 0);
        }

        static int ResolveFieldTypeId(string name) => name switch
        {
            "System.Int32" => WellKnownTypes.Int32,
            "System.String" => WellKnownTypes.String,
            "Legacy.Nullable`1<System.Int32>" => 1235, // matches BuildLegacyFile's extra schema id
            _ => WellKnownTypes.Object,
        };
    }

    [CerasSerializable]
    public class BsGenericValue<T>
    {
        public T Item;
    }

    [CerasSerializable]
    public class BsGenericRef<T>
    {
        public T Item;
    }

    [CerasContext("BuiltinShapes")]
    [CerasSerializable]
    public class BsGenericHolder
    {
        public BsGenericValue<int> Value;
        public BsGenericRef<string> Ref;
        public int After;
    }
}
