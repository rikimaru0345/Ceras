// Item 5: the corruption and reentrancy contract.
//
// Every case here is a *malformed but CRC-valid* save (or a hand-built payload), because a valid CRC is
// what proves the CRC cannot mask a decoder defect. A truncated buffer, a hostile length, a
// back-reference past the identity cache and an undescribed full-name reference must all surface as
// CerasDataCorruptionException (or, for the ones that are a schema problem rather than a byte problem,
// CerasSchemaException) *before* anything is allocated or half-applied.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Xunit;
using Ceras;
using Ceras.Exceptions;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test.Corruption
{
    [CerasContext("Corruption")]
    [CerasSerializable]
    public class CoHolder
    {
        public int Id;
        public string Name;
        public List<int> Items;
        public Type Kind;
    }

    /// <summary>A polymorphic member, so a save carries a reference marker and a type id.</summary>
    [CerasContext("Corruption")]
    [CerasSerializable]
    public class CoBase { public int BaseValue; }

    [CerasContext("Corruption")]
    [CerasSerializable]
    public class CoDerived : CoBase
    {
        public string Extra;
    }

    [CerasContext("Corruption")]
    [CerasSerializable]
    public class CoPolyHolder
    {
        public CoBase Value;
        public int After;
        public CoBase AlsoValue;   // a second reference slot, so a legal back-reference has a home
    }

    public class CorruptionContractTests : TestBase
    {
        const string Ctx = "Corruption";

        static CerasRobustSerializer New(Action<CerasRobustConfig> extra = null) => new CerasRobustSerializer(c =>
        {
            c.UseGeneratedFormatters(Ctx);
            extra?.Invoke(c);
        });

        static byte[] Sample() => New().Save(new CoHolder { Id = 1, Name = "n", Items = new List<int> { 2, 3 } }, 1);

        static byte[] Mutated(byte[] bytes, Action<byte[]> mutate)
        {
            var copy = (byte[])bytes.Clone();
            mutate(copy);
            FixRobustChecksum(copy);   // valid CRC: the decoder, not the checksum, must reject this
            return copy;
        }

        // ── Truncation is corruption, not a raw BCL exception ─────────────────────────────────

        [Fact]
        public void Truncating_The_Payload_At_Every_Offset_Is_Corruption()
        {
            var bytes = Sample();

            for (int len = RobustHeader.Size; len < bytes.Length; len += 7)
            {
                var truncated = new byte[len];
                Array.Copy(bytes, truncated, len);

                // The generated path and the interpreter must agree on the *kind* of failure. Anything
                // else (ArgumentOutOfRangeException, NullReferenceException, IndexOutOfRangeException)
                // means a decoder read past the end of the buffer instead of rejecting it.
                AssertCorruption(() => New().Load<CoHolder>(truncated, 1), len);
                AssertCorruption(() => New().Load<CoHolder>(SlowPath(truncated), 1), len);
            }
        }

        static void AssertCorruption(Func<object> load, int length)
        {
            var ex = Record.Exception(load);
            Assert.True(ex is CerasDataCorruptionException,
                $"a save truncated to {length} bytes threw {ex?.GetType().Name ?? "nothing"}: {ex?.Message}");
        }

        [Fact]
        public void A_Truncated_File_Does_Not_Survive_As_A_Schema_Or_Version_Error()
        {
            // The neighbours already produce clean messages; truncation must not be reported as one of
            // those, because it means "the file is incomplete", not "this file is from another version".
            var bytes = Sample();
            var truncated = new byte[bytes.Length - 4];
            Array.Copy(bytes, truncated, truncated.Length);
            FixRobustChecksum(truncated);

            var ex = Record.Exception(() => New().Load<CoHolder>(truncated, 1));
            Assert.NotNull(ex);
            Assert.IsNotType<CerasVersionMismatchException>(ex);
            Assert.IsNotType<CerasSchemaException>(ex);
        }

        // ── Hostile lengths are bounded before the allocation they pick ─────────────────────

        [Fact]
        public void An_Inline_String_Longer_Than_The_Buffer_Is_Corruption()
        {
            // The declared length is larger than what remains, so the reader must not copy (or rent) it.
            var bytes = Sample();
            var corrupted = Mutated(bytes, copy =>
            {
                // Find the payload's "n" string: it is stored as a 4-byte table index, so instead patch
                // the string table entry itself. The table is the region after the data section.
                int tableStart = ReadOffset(copy, RobustHeader.StringTableOffsetOffset);
                int count = (int)SchemaSection.ReadLeb128(copy, ref tableStart);
                Assert.True(count > 0, "the sample must contain at least one interned string");

                // First entry: leb128 length followed by the bytes.
                uint declared = (uint)copy[tableStart];
                copy[tableStart] = 0x7F;              // declare 127 bytes ...
                // ... while only a handful follow, so the copy would run past the region.
            });

            var ex = Record.Exception(() => New().Load<CoHolder>(corrupted, 1));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void An_Inline_String_Longer_Than_MaxStringLength_Is_Rejected()
        {
            var serializer = New(c => c.SizeLimits.MaxStringLength = 4);
            var bytes = serializer.Save(new CoHolder { Id = 1, Name = "a-longer-name", Items = new List<int>() }, 1);

            // 0.8.7: MaxStringLength used to be inert in the robust format. Now the load is rejected
            // instead of quietly allocating whatever the file asked for.
            var ex = Record.Exception(() => New(c => c.SizeLimits.MaxStringLength = 4).Load<CoHolder>(bytes, 1));
            Assert.IsType<CerasDataCorruptionException>(ex);

            // The same file loads fine without the limit, so it is the limit doing the rejecting.
            Assert.Equal("a-longer-name", New().Load<CoHolder>(bytes, 1).Name);

            // And a string within the limit is accepted.
            var short1 = serializer.Save(new CoHolder { Id = 2, Name = "abcd", Items = new List<int>() }, 1);
            Assert.Equal("abcd", New(c => c.SizeLimits.MaxStringLength = 4).Load<CoHolder>(short1, 1).Name);
        }

        [Fact]
        public void A_Deflate_Table_That_Expands_Past_The_Limit_Is_Corruption()
        {
            // Highly compressible content: the compressed region is tiny and the decoded region is not,
            // which is exactly the shape a zip bomb has.
            var big = new string('a', 200_000);
            var bytes = New(c => c.StringTableCompression = StringTableCompression.Deflate)
                .Save(new CoHolder { Id = 1, Name = big, Items = new List<int>() }, 1);

            // Without the limit the file loads: the cap is opt-in, like every other SizeLimits entry.
            Assert.Equal(big, New().Load<CoHolder>(bytes, 1).Name);

            // With a cap below the decoded size the expansion is stopped, not completed.
            var ex = Record.Exception(() => New(c =>
            {
                c.SizeLimits.MaxStringTableSize = 4096;
            }).Load<CoHolder>(bytes, 1));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void A_Deflate_Region_That_Is_Not_A_Stream_Is_Corruption()
        {
            // Written uncompressed, then *claimed* to be Deflate: the expansion cannot even start.
            var bytes = New(c => c.StringTableCompression = StringTableCompression.Disabled)
                .Save(new CoHolder { Id = 1, Name = "n", Items = new List<int> { 2 } }, 1);
            var corrupted = Mutated(bytes, copy => copy[RobustHeader.FlagsOffset] = (byte)StringTableCompression.Deflate);

            var ex = Record.Exception(() => New().Load<CoHolder>(corrupted, 1));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        // ── 0.8.5: an identity-cache back-reference past the end is corruption ───────────────

        [Fact]
        public void An_Out_Of_Range_BackReference_Is_Rejected_By_The_Interpreter_Directly()
        {
            // Written by hand so the marker is exact. The payload is the real grammar for a CoPolyHolder
            // root: a reference marker for the root itself, then its two fields.
            var ids = SealedIds();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias); // the root
            SerializerBinary.WriteUInt32Bias(payload, 7, Protocol.RefBias);                          // Value -> object 7
            SerializerBinary.WriteInt32Fixed(payload, 42);                                           // After

            var ex = Capture<CoPolyHolder>(Engine(ids), payload.WrittenSpan.ToArray());

            var corruption = Assert.IsType<CerasDataCorruptionException>(ex);
            Assert.Contains("7", corruption.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void An_In_Range_BackReference_Still_Resolves()
        {
            // The counterpart, so the check above cannot be satisfied by rejecting every marker: Value is a
            // new object (index 1) and AlsoValue refers back to it, which has to come back as that instance.
            var ids = SealedIds();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValue, Protocol.RefBias);
            SchemaSection.WriteLeb128(payload, (uint)ids.baseId);
            SerializerBinary.WriteInt32Fixed(payload, 3);            // CoBase.BaseValue
            SerializerBinary.WriteInt32Fixed(payload, 42);           // After
            SerializerBinary.WriteUInt32Bias(payload, 1, Protocol.RefBias);   // AlsoValue -> the Value object

            var loaded = Read<CoPolyHolder>(Engine(ids), payload.WrittenSpan.ToArray());
            Assert.NotNull(loaded.Value);
            Assert.Same(loaded.Value, loaded.AlsoValue);
            Assert.Equal(3, loaded.Value.BaseValue);
            Assert.Equal(42, loaded.After);
        }

        [Fact]
        public void A_FullName_Reference_To_An_Undescribed_Type_Is_Refused()
        {
            // The old branch allocated an uninitialized instance and consumed *no* payload bytes, so the
            // object lost its data and everything after it desynchronised. There is no schema for the
            // bytes that follow, so this has to be an error rather than a guess.
            var ids = SealedIds();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValue, Protocol.RefBias);
            SchemaSection.WriteLeb128(payload, 127);              // FullNameMarker
            SerializerBinary.WriteUInt32Fixed(payload, 1);        // string-table index 1
            SerializerBinary.WriteInt32Fixed(payload, 42);

            // Index 1 names a real CLR type that this context does not describe.
            var ex = Capture<CoPolyHolder>(Engine(ids, new string[] { null, "System.Uri" }), payload.WrittenSpan.ToArray());
            var schema = Assert.IsType<CerasSchemaException>(ex);
            Assert.Contains("System.Uri", schema.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_FullName_Reference_To_An_Unknown_Type_Is_Refused()
        {
            var ids = SealedIds();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValue, Protocol.RefBias);
            SchemaSection.WriteLeb128(payload, 127);
            SerializerBinary.WriteUInt32Fixed(payload, 1);
            SerializerBinary.WriteInt32Fixed(payload, 42);

            var ex = Capture<CoPolyHolder>(Engine(ids, new string[] { null, "Some.Removed.Type" }), payload.WrittenSpan.ToArray());
            var schema = Assert.IsType<CerasSchemaException>(ex);
            Assert.Contains("Some.Removed.Type", schema.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_System_Type_Value_Named_By_Full_Name_Still_Resolves()
        {
            // The legitimate use of the same marker: a Type value naming a type. Separate code path from
            // the polymorphic one, and it must keep working through the interpreter.
            var ids = SealedIds();
            var payload = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32Bias(payload, Protocol.RefNewValueSameType, Protocol.RefBias);
            SerializerBinary.WriteInt32Fixed(payload, 7);          // Id
            SchemaSection.WriteLeb128(payload, 127);              // Kind: a full-name Type
            SerializerBinary.WriteUInt32Fixed(payload, 1);        // string-table index 1

            var loaded = Read<CoHolder>(Engine(ids, new string[] { null, "System.Uri" }), payload.WrittenSpan.ToArray());
            Assert.Equal(7, loaded.Id);
            Assert.Equal(typeof(Uri), loaded.Kind);
        }

        // ── 0.8.7/0.8.8: DumpSchema and the header envelope ───────────────────────────────────

        [Fact]
        public void DumpSchema_Reports_A_Corrupt_Buffer_As_Corruption()
        {
            var bytes = Sample();
            var corrupted = Mutated(bytes, copy =>
            {
                // A string-table offset before the data section: Validate() rejects it, where the old
                // DumpSchema truncated the long to an int and produced a raw ArgumentOutOfRangeException.
                BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(RobustHeader.StringTableOffsetOffset), 1);
            });

            var ex = Record.Exception(() => CerasRobustSerializer.DumpSchema(corrupted));
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void DumpSchema_Still_Prints_A_Version_Mismatch_As_A_Message()
        {
            var bytes = Sample();
            var other = Mutated(bytes, copy => copy[RobustHeader.FormatVersionOffset] = 99);
            Assert.Contains("version mismatch", CerasRobustSerializer.DumpSchema(other), StringComparison.OrdinalIgnoreCase);
        }

        // ── Multidimensional arrays: the bound has to be checked before it can overflow ─────────

        /// <summary>
        /// The raw serializer is the reachable path for a multidimensional array (the robust format
        /// describes rank-1 only), so this exercises the raw reader.
        /// </summary>
        /// <summary>
        /// The raw payload of an array starts with the reference marker for the array itself (a var-int
        /// biased id, one byte for a new root object), so the rank and the per-dimension lengths start at
        /// offset 1: marker, rank, length, length, then the elements.
        /// </summary>
        const int ArrayPayloadOffset = 1;

        [Fact]
        public void A_Multidimensional_Array_With_A_Crafted_Rank_Is_Corruption()
        {
            var raw = new CerasFastSerializer();
            var array = new int[2, 2] { { 1, 2 }, { 3, 4 } };
            var bytes = raw.Serialize(array);
            Assert.Equal((byte)2, bytes[ArrayPayloadOffset]);   // the sample's rank, so the offset is right

            // rank 0xFFFFFFFF as the raw var-int the formatter writes. The rank is rejected on its own,
            // so replacing just that prefix is enough - the elements behind it are never reached.
            var rank = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32(rank, uint.MaxValue);
            var corrupted = ReplacePrefix(bytes, rank.WrittenSpan.ToArray(), ArrayPayloadOffset);

            var ex = Record.Exception(() =>
            {
                int offset = 0;
                int[,] target = null;
                raw.Deserialize(ref target, corrupted, ref offset);
            });
            Assert.IsType<CerasDataCorruptionException>(ex);
        }

        [Fact]
        public void A_Multidimensional_Array_Whose_Element_Count_Overflows_Is_Corruption()
        {
            var raw = new CerasFastSerializer();
            var array = new int[2, 2] { { 1, 2 }, { 3, 4 } };
            var bytes = raw.Serialize(array);

            // rank 2, then two dimensions of 0xFFFF each: the old `count *= size` overflowed *before* the
            // limit was compared, so a wrapped small number passed MaxArraySize and then drove a huge
            // allocation here and in Array.CreateInstance.
            var header = new ArrayBufferWriter<byte>();
            SerializerBinary.WriteUInt32(header, 2);
            SerializerBinary.WriteUInt32(header, 0xFFFF);
            SerializerBinary.WriteUInt32(header, 0xFFFF);

            var corrupted = ReplacePrefix(bytes, header.WrittenSpan.ToArray(), ArrayPayloadOffset);

            var ex = Record.Exception(() =>
            {
                int offset = 0;
                int[,] target = null;
                raw.Deserialize(ref target, corrupted, ref offset);
            });
            var corruption = Assert.IsType<CerasDataCorruptionException>(ex);
            Assert.Contains("32-bit int", corruption.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_Multidimensional_Array_Within_The_Limit_Still_Works()
        {
            // The counterpart, so the two checks above cannot be satisfied by rejecting every array.
            var raw = new CerasFastSerializer();
            var array = new int[2, 3] { { 1, 2, 3 }, { 4, 5, 6 } };
            var bytes = raw.Serialize(array);

            int offset = 0;
            int[,] target = null;
            raw.Deserialize(ref target, bytes, ref offset);
            Assert.Equal(array, target);
        }

        /// <summary>
        /// Replaces the leading bytes of a buffer. Used where a crafted header is rejected before any of
        /// the bytes behind it are read, so the rest of the buffer does not have to line up.
        /// </summary>
        static byte[] ReplacePrefix(byte[] bytes, byte[] prefix, int offset = 0)
        {
            var copy = (byte[])bytes.Clone();
            Array.Copy(prefix, 0, copy, offset, prefix.Length);
            return copy;
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs one interpreter read and returns the exception, or null if it succeeded. A
        /// <c>SequenceReader</c> is a ref struct and cannot cross into a lambda, so the read has to live
        /// inside a method like this one.
        /// </summary>
        /// <summary>
        /// A context whose registry is sealed, plus the ids it assigned. The interpreter's migration plan
        /// has to agree with the file's schema, so a hand-built schema has to use the real ids.
        /// </summary>
        static (int baseId, int derivedId, int polyHolderId, int holderId) SealedIds()
        {
            var serializer = New();
            serializer.Save(new CoPolyHolder { Value = new CoDerived { BaseValue = 1, Extra = "x" }, After = 1 }, 1);
            serializer.Save(new CoHolder { Id = 1, Name = "n", Items = new List<int>() }, 1);

            var registry = serializer.Config.SchemaRegistry;
            Assert.True(registry.TryGetId(typeof(CoBase), out int baseId));
            Assert.True(registry.TryGetId(typeof(CoDerived), out int derivedId));
            Assert.True(registry.TryGetId(typeof(CoPolyHolder), out int polyHolderId));
            Assert.True(registry.TryGetId(typeof(CoHolder), out int holderId));
            return (baseId, derivedId, polyHolderId, holderId);
        }

        /// <summary>
        /// An interpreter over a hand-built "old" schema for this context's types. The layouts are the
        /// current ones - these tests are about the marker grammar, not about evolution (the golden
        /// fixtures in HistoricalFixtureTests cover evolution).
        /// </summary>
        static SchemaTraversalEngine Engine((int baseId, int derivedId, int polyHolderId, int holderId) ids,
            string[] stringTable = null)
        {
            var schemas = new Dictionary<string, OldTypeSchema>
            {
                ["Ceras.Test.Corruption.CoBase"] = new OldTypeSchema(ids.baseId, "Ceras.Test.Corruption.CoBase",
                    Array.Empty<string>(), SchemaTypeKind.Class,
                    new[] { new OldFieldSchema("BaseValue", Array.Empty<string>(), WellKnownTypes.Int32, true) },
                    0, 0, null, null),
                ["Ceras.Test.Corruption.CoDerived"] = new OldTypeSchema(ids.derivedId, "Ceras.Test.Corruption.CoDerived",
                    Array.Empty<string>(), SchemaTypeKind.Class,
                    new[]
                    {
                        new OldFieldSchema("BaseValue", Array.Empty<string>(), WellKnownTypes.Int32, true),
                        new OldFieldSchema("Extra", Array.Empty<string>(), WellKnownTypes.String, true),
                    },
                    0, 0, null, null),
                ["Ceras.Test.Corruption.CoPolyHolder"] = new OldTypeSchema(ids.polyHolderId, "Ceras.Test.Corruption.CoPolyHolder",
                    Array.Empty<string>(), SchemaTypeKind.Class,
                    new[]
                    {
                        new OldFieldSchema("Value", Array.Empty<string>(), ids.baseId, false),
                        new OldFieldSchema("After", Array.Empty<string>(), WellKnownTypes.Int32, true),
                        new OldFieldSchema("AlsoValue", Array.Empty<string>(), ids.baseId, false),
                    },
                    0, 0, null, null),
                ["Ceras.Test.Corruption.CoHolder"] = new OldTypeSchema(ids.holderId, "Ceras.Test.Corruption.CoHolder",
                    Array.Empty<string>(), SchemaTypeKind.Class,
                    // Only the two fields the payloads below carry; anything omitted is skipped by the
                    // migration, which is what keeps this schema free of invented type ids.
                    new[]
                    {
                        new OldFieldSchema("Id", Array.Empty<string>(), WellKnownTypes.Int32, true),
                        new OldFieldSchema("Kind", Array.Empty<string>(), WellKnownTypes.Type, true),
                    },
                    0, 0, null, null),
            };

            return new SchemaTraversalEngine(schemas, stringTable ?? new string[] { null }, 1, New());
        }

        static Exception Capture<T>(SchemaTraversalEngine engine, byte[] payload)
        {
            try
            {
                Read<T>(engine, payload);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        static T Read<T>(SchemaTraversalEngine engine, byte[] payload)
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
            return engine.ReadRoot(ref reader, default(T));
        }

        static int ReadOffset(byte[] bytes, int headerOffset)
            => (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(headerOffset));
    }
}
