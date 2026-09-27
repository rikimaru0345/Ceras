using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ceras.Format
{
    // ── Old-schema data model (used by migration engine) ─────────────────────

    public sealed class OldTypeSchema
    {
        public int            TypeId        { get; }
        public string         StableName    { get; }
        public string[]       PreviousNames { get; }
        public SchemaTypeKind Kind          { get; }
        public OldFieldSchema[] Fields      { get; }
        public OldEnumValue[] EnumValues    { get; }
        public int FixedByteSize            { get; }
        public int DefinitionTypeId { get; }
        public int[] ArgTypeIds { get; }

        internal OldTypeSchema(int typeId, string stableName, string[] previousNames,
            SchemaTypeKind kind, OldFieldSchema[] fields, int fixedByteSize,
            int definitionTypeId = 0, int[] argTypeIds = null!, OldEnumValue[] enumValues = null!)
        {
            TypeId        = typeId;
            StableName    = stableName ?? "";
            PreviousNames = previousNames ?? Array.Empty<string>();
            Kind          = kind;
            Fields        = fields   ?? Array.Empty<OldFieldSchema>();
            FixedByteSize = fixedByteSize;
            DefinitionTypeId = definitionTypeId;
            ArgTypeIds    = argTypeIds ?? Array.Empty<int>();
            EnumValues    = enumValues ?? Array.Empty<OldEnumValue>();
        }
    }

    public sealed class OldFieldSchema
    {
        public string   StableName    { get; }
        public string[] PreviousNames { get; }
        public int      TypeId        { get; }
        public bool     IsMonomorphic { get; }

        internal OldFieldSchema(string stableName, string[] previousNames, int typeId, bool isMonomorphic)
        {
            StableName    = stableName ?? "";
            PreviousNames = previousNames ?? Array.Empty<string>();
            TypeId        = typeId;
            IsMonomorphic = isMonomorphic;
        }
    }

    /// <summary>One name→value entry read back from a file's schema. <see cref="Value"/> is raw bits.</summary>
    public sealed class OldEnumValue
    {
        public string Name { get; }
        public ulong Value { get; }
        internal OldEnumValue(string name, ulong value) { Name = name ?? ""; Value = value; }
    }

    public static class SchemaSection
    {
        public static Dictionary<Type, int> Write(ref byte[] buffer, ref int offset, SchemaRegistry registry, StringInternTable internTable)
        {
            var typeToId = new Dictionary<Type, int>();
            var queue = new Queue<(Type type, int id, SchemaTypeInfo info)>();
            
            foreach (var entry in registry.All())
            {
                typeToId[entry.type] = entry.id;
                queue.Enqueue(entry);
            }

            // Phase 1: Discovery (populate internTable and discover all types)
            var discoveryQueue = new Queue<(Type type, int id, SchemaTypeInfo info)>(queue);
            var discoveryProcessed = new HashSet<int>();

            while (discoveryQueue.Count > 0)
            {
                var (type, id, info) = discoveryQueue.Dequeue();
                if (!discoveryProcessed.Add(id)) continue;

                internTable.GetIndex(info.StableName);
                if (info.PreviousTypeNames != null)
                    foreach (var prev in info.PreviousTypeNames) internTable.GetIndex(prev);

                if (info.Kind == SchemaTypeKind.Enum)
                {
                    ResolveIdForDiscovery(info.UnderlyingTypeName, typeToId, discoveryQueue, registry, info.StableName, "enum underlying type");
                    if (info.EnumValues != null)
                        foreach (var ev in info.EnumValues) internTable.GetIndex(ev.Name);
                }
                else if (info.Kind == SchemaTypeKind.GenericInstantiation)
                {
                    ResolveIdForDiscovery(info.UnderlyingTypeName, typeToId, discoveryQueue, registry, info.StableName, "generic definition");
                    if (info.ArgTypeNames != null)
                        for (int i = 0; i < info.ArgTypeNames.Length; i++)
                            ResolveIdForDiscovery(info.ArgTypeNames[i], typeToId, discoveryQueue, registry, info.StableName, $"generic argument {i}");
                    
                    if (info.Fields != null)
                    {
                        foreach (var field in info.Fields)
                        {
                            internTable.GetIndex(field.StableName);
                            if (field.PreviousNames != null)
                                foreach (var pn in field.PreviousNames) internTable.GetIndex(pn);
                            ResolveIdForDiscovery(field.TypeName, typeToId, discoveryQueue, registry, info.StableName, $"field '{field.StableName}'");
                        }
                    }
                }
                else if (info.Fields != null)
                {
                    foreach (var field in info.Fields)
                    {
                        internTable.GetIndex(field.StableName);
                        if (field.PreviousNames != null)
                            foreach (var pn in field.PreviousNames) internTable.GetIndex(pn);
                        
                        // Discover the field type!
                        var fieldId = ResolveIdForDiscovery(field.TypeName, typeToId, discoveryQueue, registry, info.StableName, $"field '{field.StableName}'");
                    }
                }
            }

            // Phase 2: Actual writing (WITHOUT the string table, CerasRobustSerializer handles that)
            var entryWriter = new ArrayBufferWriter<byte>(4096);
            int finalCount = 0;
            
            queue.Clear();
            foreach (var kv in typeToId.OrderBy(x => x.Value))
                if (registry.TryGetInfo(kv.Key, out var info))
                    queue.Enqueue((kv.Key, kv.Value, info));

            var alreadyProcessed = new HashSet<int>();
            while (queue.Count > 0)
            {
                var (type, id, info) = queue.Dequeue();
                if (!alreadyProcessed.Add(id)) continue;
                finalCount++;

                WriteLeb128(entryWriter, (uint)id);
                WriteInternedString(entryWriter, info.StableName, internTable);

                WriteLeb128(entryWriter, (uint)(info.PreviousTypeNames?.Length ?? 0));
                if (info.PreviousTypeNames != null)
                    foreach (var prev in info.PreviousTypeNames)
                        WriteInternedString(entryWriter, prev, internTable);

                entryWriter.GetSpan(1)[0] = (byte)info.Kind;
                entryWriter.Advance(1);

                switch (info.Kind)
                {
                    case SchemaTypeKind.Enum:
                        int underlyingId = ResolveId(info.UnderlyingTypeName, typeToId, queue, registry, info.StableName, "enum underlying type");
                        WriteLeb128(entryWriter, (uint)underlyingId);
                        WriteLeb128(entryWriter, (uint)(info.EnumValues?.Length ?? 0));
                        if (info.EnumValues != null)
                        {
                            foreach (var ev in info.EnumValues)
                            {
                                WriteInternedString(entryWriter, ev.Name, internTable);
                                // Raw bits, eight bytes little-endian: the enum's underlying type id says how to read them.
                                BinaryPrimitives.WriteUInt64LittleEndian(entryWriter.GetSpan(8), ev.Value);
                                entryWriter.Advance(8);
                            }
                        }
                        break;

                    case SchemaTypeKind.GenericInstantiation:
                        int defId = ResolveId(info.UnderlyingTypeName, typeToId, queue, registry, info.StableName, "generic definition");
                        WriteLeb128(entryWriter, (uint)defId);
                        WriteLeb128(entryWriter, (uint)(info.ArgTypeNames?.Length ?? 0));
                        if (info.ArgTypeNames != null)
                            for (int i = 0; i < info.ArgTypeNames.Length; i++)
                                WriteLeb128(entryWriter, (uint)ResolveId(info.ArgTypeNames[i], typeToId, queue, registry, info.StableName, $"generic argument {i}"));
                        
                        goto case SchemaTypeKind.Class;

                    case SchemaTypeKind.Struct:
                    case SchemaTypeKind.Class:
                        WriteLeb128(entryWriter, (uint)(info.Fields?.Length ?? 0));
                        if (info.Fields != null)
                        {
                            foreach (var field in info.Fields)
                            {
                                WriteInternedString(entryWriter, field.StableName, internTable);
                                WriteLeb128(entryWriter, (uint)(field.PreviousNames?.Length ?? 0));
                                if (field.PreviousNames != null)
                                    foreach (var pn in field.PreviousNames)
                                        WriteInternedString(entryWriter, pn, internTable);

                                int fieldTypeId = ResolveId(field.TypeName, typeToId, queue, registry, info.StableName, $"field '{field.StableName}'");
                                WriteLeb128(entryWriter, (uint)fieldTypeId);
                                entryWriter.GetSpan(1)[0] = (byte)(field.IsMonomorphic ? 1 : 0);
                                entryWriter.Advance(1);
                            }
                        }
                        WriteLeb128(entryWriter, (uint)info.FixedByteSize);
                        break;
                }
            }

            var finalWriter = new ArrayBufferWriter<byte>();
            WriteLeb128(finalWriter, (uint)finalCount);
            finalWriter.Write(entryWriter.WrittenSpan);

            var result = finalWriter.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, result.Length);
            result.CopyTo(buffer.AsSpan(offset));
            offset += result.Length;

            return typeToId;
        }

        /// <summary>
        /// Resolves a canonical field/argument type name to a schema type id during baking.
        /// <para>
        /// Fail loud (TODO Phase 3.4): a name that cannot be resolved, or that resolves to a CLR type
        /// which is neither registered nor a supported structural/built-in shape, is a
        /// declaration/identity bug and must not silently degrade to an empty schema or the null
        /// type. The hard error names the declaring type, the field/position, and the unresolved
        /// identity, and surfaces from <see cref="SchemaRegistry.Seal"/> (bake time), never mid-save.
        /// </para>
        /// </summary>
        static int ResolveId(string clrName, Dictionary<Type, int> typeToId, Queue<(Type, int, SchemaTypeInfo)> queue, SchemaRegistry registry,
            string declaringTypeName, string memberDescription)
        {
            if (clrName == null)
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot bake the schema for type '{declaringTypeName}': its {memberDescription} has a null type name. " +
                    "This is a declaration/identity bug; recompile so the generator emits a canonical type name.");

            int wk = ResolveWellKnownId(clrName);
            if (wk >= 0) return wk;

            if (!registry.TryResolveType(clrName, out var type) || type == null)
            {
                // Fallback: try to resolve the type manually (legacy/assembly-qualified shapes).
                type = Type.GetType(clrName);
            }

            if (type == null)
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot bake the schema for type '{declaringTypeName}': its {memberDescription} uses the type name '{clrName}', " +
                    "which does not resolve to any CLR type in this build. This is a declaration/identity bug: make sure the type " +
                    "still exists and is declared in this compilation (with [CerasSerializable]/[CerasContext] or CerasKnownType), then rebuild.");

            if (!registry.TryGetId(type, out int id))
            {
                if (!TryGetAutoRegistration(type, out var rejection))
                    throw UndescribableMemberType(type, clrName, declaringTypeName, memberDescription, rejection);

                // Structural BCL shapes (arrays, built-in collections, KeyValuePair, tuples, Nullable)
                // are intentionally skipped by the generator and registered structurally on first use.
                registry.Register(type, null!);
                if (!registry.TryGetId(type, out id))
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot bake the schema for type '{declaringTypeName}': its {memberDescription} is of type '{clrName}', " +
                        "but registering that structural type did not produce a schema type id. This is an internal Ceras bug.");
            }

            if (!typeToId.ContainsKey(type))
            {
                typeToId[type] = id;
                if (registry.TryGetInfo(type, out var info))
                    queue.Enqueue((type, id, info));
            }
            return id;
        }

        /// <summary>
        /// The bake-time error for a member (or argument) type that is neither declared in the context's
        /// manifest nor describable structurally.
        /// <para>
        /// A collection shape Ceras writes but cannot describe gets the reason it cannot be described
        /// (<see cref="WellKnownTypes.DescribeCollectionShapeRejection"/>); every other type is an
        /// ordinary undeclared type, where the only fix is to declare it. Reporting from here rather than
        /// from the predicate is deliberate: this is the only layer that knows which declaring type and
        /// member reached the unsupported shape, so it is the only place the error can name them.
        /// </para>
        /// </summary>
        static Ceras.Exceptions.CerasSchemaException UndescribableMemberType(
            Type type, string clrName, string declaringTypeName, string memberDescription, CollectionShapeRejection rejection)
        {
            // The reason sentence already names the type, so only the context it was reached through is
            // added here.
            if (rejection != CollectionShapeRejection.None)
                return new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot bake the schema for type '{declaringTypeName}': its {memberDescription} is of a type Ceras cannot describe. " +
                    WellKnownTypes.DescribeCollectionShapeRejection(type, rejection));

            return new Ceras.Exceptions.CerasSchemaException(
                $"Cannot bake the schema for type '{declaringTypeName}': its {memberDescription} is of type '{clrName}', " +
                "but that type is not part of the selected serialization context's manifest (and it is not a supported " +
                "structural/BCL type). Declare the type with [CerasSerializable]/[CerasContext(\"name\")], make it reachable " +
                "from a context root, or declare it with [assembly: CerasKnownType<...>], then rebuild.");
        }

        /// <summary>
        /// True for the built-in shapes the generator deliberately does not emit formatters/schemas for:
        /// well-known primitives, one-dimensional arrays, the built-in collections and
        /// <c>KeyValuePair</c> (structural schemas), closed instantiations of the other reserved generic
        /// definitions, collection subclasses served by the built-in collection formatter, and enums.
        /// Everything else must be declared in the generated manifest (TODO Phase 2.5/3.4).
        /// <para>
        /// A pure predicate — it never throws. A collection shape Ceras writes but cannot describe comes
        /// back as <paramref name="rejection"/>, and <see cref="ResolveId"/> turns that into the
        /// <c>CerasSchemaException</c> that names the declaring type and member. (The one exception is
        /// <see cref="SchemaRegistry.TryBuildStructuralSchema"/>, the bake's own fail-loud entry point, which
        /// raises for the shapes the format cannot represent at all — a multidimensional array, and the
        /// <c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> subclass this predicate reports first.)
        /// </para>
        /// </summary>
        static bool TryGetAutoRegistration(Type type, out CollectionShapeRejection rejection)
        {
            if (type == null)
            {
                rejection = CollectionShapeRejection.None;
                return false;
            }

            if (WellKnownTypes.IsWellKnown(type))
            {
                rejection = CollectionShapeRejection.None;
                return true;
            }

            // Classified before the structural bake, and not after it: the bake rejects exactly the shapes
            // a collection classification reports a reason for (a Queue<>/Stack<> subclass), and an error
            // that names neither the declaring type nor the member is not actionable. Collection
            // subclasses that *are* describable are picked up below by the bake's own structural schema.
            WellKnownTypes.TryResolveCollectionShape(type, out _, out _, out rejection);
            if (rejection != CollectionShapeRejection.None)
                return false;

            if (SchemaRegistry.TryBuildStructuralSchema(type, out _))
            {
                rejection = CollectionShapeRejection.None;
                return true;
            }

            if (type.IsGenericType && !type.IsGenericTypeDefinition &&
                WellKnownTypes.TryGetId(type.GetGenericTypeDefinition(), out _))
            {
                rejection = CollectionShapeRejection.None;
                return true;
            }

            // Enum schemas are fully derivable from CLR metadata at runtime, so an enum that the
            // generator skipped (e.g. blacklisted) can still be declared structurally.
            if (type.IsEnum)
            {
                rejection = CollectionShapeRejection.None;
                return true;
            }

            // Not a collection shape Ceras rejects on its own account: an ordinary undeclared type.
            return false;
        }

        static int ResolveIdForDiscovery(string clrName, Dictionary<Type, int> typeToId, Queue<(Type, int, SchemaTypeInfo)> queue, SchemaRegistry registry,
            string declaringTypeName, string memberDescription)
        {
            return ResolveId(clrName, typeToId, queue, registry, declaringTypeName, memberDescription);
        }

        static int ResolveWellKnownId(string typeName)
        {
            if (typeName == null) return -1;
            return typeName switch
            {
                "System.Boolean"  => WellKnownTypes.Bool,
                "System.Byte"     => WellKnownTypes.Byte,
                "System.SByte"    => WellKnownTypes.SByte,
                "System.Char"     => WellKnownTypes.Char,
                "System.Int16"    => WellKnownTypes.Int16,
                "System.UInt16"   => WellKnownTypes.UInt16,
                "System.Int32"    => WellKnownTypes.Int32,
                "System.UInt32"   => WellKnownTypes.UInt32,
                "System.Single"   => WellKnownTypes.Single,
                "System.Int64"    => WellKnownTypes.Int64,
                "System.UInt64"   => WellKnownTypes.UInt64,
                "System.Double"   => WellKnownTypes.Double,
                "System.Decimal"  => WellKnownTypes.Decimal,
                "System.String"   => WellKnownTypes.String,
                "System.Object"   => WellKnownTypes.Object,
                "System.DateTime" => WellKnownTypes.DateTime,
                "System.DateTimeOffset" => WellKnownTypes.DateTimeOffset,
                "System.TimeSpan" => WellKnownTypes.TimeSpan,
                "System.Guid"     => WellKnownTypes.Guid,
                "System.Type"     => WellKnownTypes.Type,
                "System.Array"    => WellKnownTypes.Array,
                "System.Collections.Generic.List`1" => WellKnownTypes.List,
                "System.Collections.Generic.Dictionary`2" => WellKnownTypes.Dictionary,
                "System.Collections.Generic.HashSet`1" => WellKnownTypes.HashSet,
                "System.Collections.Generic.Queue`1" => WellKnownTypes.Queue,
                "System.Collections.Generic.Stack`1" => WellKnownTypes.Stack,
                "System.Nullable`1" => WellKnownTypes.Nullable,
                "System.Collections.Generic.KeyValuePair`2" => WellKnownTypes.KeyValuePair,
                "System.Tuple`1" => WellKnownTypes.Tuple1,
                "System.Tuple`2" => WellKnownTypes.Tuple2,
                "System.Tuple`3" => WellKnownTypes.Tuple3,
                "System.Tuple`4" => WellKnownTypes.Tuple4,
                "System.Tuple`5" => WellKnownTypes.Tuple5,
                "System.Tuple`6" => WellKnownTypes.Tuple6,
                "System.Tuple`7" => WellKnownTypes.Tuple7,
                "System.ValueTuple`1" => WellKnownTypes.VTuple1,
                "System.ValueTuple`2" => WellKnownTypes.VTuple2,
                "System.ValueTuple`3" => WellKnownTypes.VTuple3,
                "System.ValueTuple`4" => WellKnownTypes.VTuple4,
                "System.ValueTuple`5" => WellKnownTypes.VTuple5,
                "System.ValueTuple`6" => WellKnownTypes.VTuple6,
                "System.ValueTuple`7" => WellKnownTypes.VTuple7,
                _                 => -1,
            };
        }

        public static Dictionary<string, OldTypeSchema> Read(byte[] buffer, ref int offset, string[] stringTable)
        {
            var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset);
            var reader = new SequenceReader<byte>(seq);
            var result = Read(ref reader, stringTable);
            offset += (int)reader.Consumed;
            return result;
        }

        public static Dictionary<string, OldTypeSchema> Read(ref SequenceReader<byte> reader, string[] stringTable)
        {
            var byName = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal);

            // Every schema entry costs at least a few bytes; bound the declared count against the bytes
            // that actually remain so a corrupt count cannot drive a huge allocation (TODO 0.6 item 8).
            long remaining = reader.Sequence.Length - reader.Consumed;
            uint count = ReadLeb128(ref reader);
            if (count > (uint)remaining)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt schema section: declared {count} entries but only {remaining} bytes remain.");

            for (uint i = 0; i < count; i++)
            {
                int    typeId     = (int)ReadLeb128(ref reader);
                string stableName = ReadInternedString(ref reader, stringTable);
                uint prevCount = ReadBoundedCount(ref reader, nameof(prevCount));
                var  prevNames = new string[prevCount];
                for (uint j = 0; j < prevCount; j++) prevNames[j] = ReadInternedString(ref reader, stringTable);

                if (!reader.TryRead(out byte kindByte))
                    throw new Ceras.Exceptions.CerasDataCorruptionException("Corrupt schema section: unexpected end while reading a type's kind byte.");
                var kind = (SchemaTypeKind)kindByte;

                OldTypeSchema schema;
                switch (kind)
                {
                    case SchemaTypeKind.Enum:
                        int underlyingId = (int)ReadLeb128(ref reader);
                        uint valCount = ReadBoundedCount(ref reader, nameof(valCount));
                        var vals = new OldEnumValue[valCount];
                        Span<byte> rawEnumValue = stackalloc byte[8];
                        for (uint j = 0; j < valCount; j++)
                        {
                            string name = ReadInternedString(ref reader, stringTable);
                            // Eight raw bytes, little-endian: the underlying type id says how to read them.
                            if (!reader.TryCopyTo(rawEnumValue))
                                throw new Ceras.Exceptions.CerasDataCorruptionException("Corrupt schema section: unexpected end while reading an enum value.");
                            reader.Advance(8);
                            vals[j] = new OldEnumValue(name, BinaryPrimitives.ReadUInt64LittleEndian(rawEnumValue));
                        }
                        schema = new OldTypeSchema(typeId, stableName, prevNames, kind, null!, 0, underlyingId, null!, vals);
                        break;

                    case SchemaTypeKind.Struct:
                    case SchemaTypeKind.Class:
                    case SchemaTypeKind.GenericInstantiation:
                        int defId = 0;
                        var argIds = Array.Empty<int>();
                        if (kind == SchemaTypeKind.GenericInstantiation)
                        {
                            defId = (int)ReadLeb128(ref reader);
                            uint argCount = ReadBoundedCount(ref reader, nameof(argCount));
                            argIds = new int[argCount];
                            for (uint j = 0; j < argCount; j++) argIds[j] = (int)ReadLeb128(ref reader);
                        }

                        uint fieldCount = ReadBoundedCount(ref reader, nameof(fieldCount));
                        var fields = new OldFieldSchema[fieldCount];
                        for (uint j = 0; j < fieldCount; j++)
                        {
                            string fStable = ReadInternedString(ref reader, stringTable);
                            uint fpCount = ReadBoundedCount(ref reader, nameof(fpCount));
                            var fpNames = new string[fpCount];
                            for (uint k = 0; k < fpCount; k++) fpNames[k] = ReadInternedString(ref reader, stringTable);
                            int fTypeId = (int)ReadLeb128(ref reader);
                            if (!reader.TryRead(out byte flags))
                                throw new Ceras.Exceptions.CerasDataCorruptionException("Corrupt schema section: unexpected end while reading a field's flags.");
                            fields[j] = new OldFieldSchema(fStable, fpNames, fTypeId, (flags & 1) != 0);
                        }
                        int fixedSize = (int)ReadLeb128(ref reader);
                        schema = new OldTypeSchema(typeId, stableName, prevNames, kind, fields, fixedSize, defId, argIds);
                        break;

                    default:
                        throw new Ceras.Exceptions.CerasDataCorruptionException(
                            $"Corrupt schema section: type '{stableName}' has unknown kind {kindByte}.");
                }

                byName[stableName] = schema;
                foreach (var pn in prevNames) if (pn != null) byName[pn] = schema;
            }
            return byName;
        }

        /// <summary>
        /// Reads a leb128 count and rejects it when it exceeds the bytes that actually remain, so a
        /// corrupt count cannot trigger an oversized allocation (TODO 0.6 item 8). Each element costs at
        /// least one byte, so remaining-bytes is a sound upper bound.
        /// </summary>
        static uint ReadBoundedCount(ref SequenceReader<byte> reader, string description)
        {
            long remaining = reader.Sequence.Length - reader.Consumed;
            uint count = ReadLeb128(ref reader);
            if (count > (uint)remaining)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt schema section: declared {description} = {count} but only {remaining} bytes remain.");
            return count;
        }

        internal static void WriteLeb128(ref byte[] buffer, ref int offset, uint value)
        {
            var writer = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            WriteLeb128(writer, value);
            buffer = writer.Buffer;
            offset = writer.Offset;
        }

        internal static void WriteLeb128(IBufferWriter<byte> writer, uint value)
        {
            var span = writer.GetSpan(5);
            int written = 0;
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                span[written++] = b;
            } while (value != 0);
            writer.Advance(written);
        }

        internal static uint ReadLeb128(byte[] buffer, ref int offset)
        {
            var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset);
            var reader = new SequenceReader<byte>(seq);
            uint result = ReadLeb128(ref reader);
            offset += (int)reader.Consumed;
            return result;
        }

        internal static uint ReadLeb128(ref SequenceReader<byte> reader)
        {
            uint result = 0;
            int  shift  = 0;
            while (true)
            {
                if (!reader.TryRead(out byte b))
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        "Corrupt data: a length or count field runs past the end of the buffer (truncated payload).");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 35)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        "Corrupt data: a length or count field is not a valid LEB128 (more than 5 bytes).");
            }
        }

        internal static void WriteInternedString(IBufferWriter<byte> writer, string s, StringInternTable internTable)
        {
            int index = internTable.GetIndex(s);
            SerializerBinary.WriteUInt32Fixed(writer, (uint)index);
        }

        internal static string ReadInternedString(ref SequenceReader<byte> reader, string[] stringTable)
        {
            return StringTable.ReadString(ref reader, stringTable);
        }

        internal static void WriteInlineString(IBufferWriter<byte> writer, string s)
        {
            if (s == null) s = string.Empty;
            int byteCount = Encoding.UTF8.GetByteCount(s);
            WriteLeb128(writer, (uint)byteCount);
            var span = writer.GetSpan(byteCount);
            Encoding.UTF8.GetBytes(s, span);
            writer.Advance(byteCount);
        }

        /// <summary>
        /// Reads a length-prefixed UTF-8 string. <paramref name="maxByteCount"/> is the caller's
        /// configured limit: the declared length is checked against it *before* anything is allocated,
        /// and against the bytes that actually remain, so a corrupt or hostile length can neither pick
        /// an allocation size nor run the reader past the end of the buffer.
        /// </summary>
        internal static string ReadInlineString(ref SequenceReader<byte> reader, int maxByteCount = int.MaxValue)
        {
            uint byteCount = ReadLeb128(ref reader);
            if (byteCount == 0) return string.Empty;

            // uint -> int is only safe once the value is known to fit, which is what the limit gives us.
            if (byteCount > (uint)maxByteCount)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: a string of {byteCount} bytes exceeds the configured limit of {maxByteCount} bytes.");

            int length = (int)byteCount;
            if (reader.Remaining < length)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: a string declares {length} bytes but only {reader.Remaining} remain (truncated payload).");

            if (reader.UnreadSpan.Length >= length)
            {
                var s = Encoding.UTF8.GetString(reader.UnreadSpan.Slice(0, length));
                reader.Advance(length);
                return s;
            }
            else
            {
                byte[] temp = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (!reader.TryCopyTo(temp.AsSpan(0, length)))
                        throw new Ceras.Exceptions.CerasDataCorruptionException(
                            "Corrupt data: a string could not be copied out of the buffer (truncated payload).");
                    var s = Encoding.UTF8.GetString(temp, 0, length);
                    reader.Advance(length);
                    return s;
                }
                finally { ArrayPool<byte>.Shared.Return(temp); }
            }
        }
    }
}
