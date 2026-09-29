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

        /// <summary>
        /// For <see cref="SchemaTypeKind.Opaque"/>: the name the file recorded for the custom formatter
        /// that wrote this type's values, which is the canonical name of that formatter's CLR type. The
        /// migration reader compares it with the formatter this build would use, because a different
        /// formatter's bytes are a different payload (see <c>Format/LAYOUT.md</c>).
        /// </summary>
        public string OpaqueFormatterName { get; }

        /// <summary>
        /// For <see cref="SchemaTypeKind.Opaque"/>: the file's own record of whether a reference marker
        /// stands in front of the frame. Read from the file, never re-derived from the current CLR
        /// type, for the same reason the rest of the payload grammar is: only the file knows how it was
        /// written.
        /// </summary>
        public bool OpaqueValueWrittenInline { get; }

        internal OldTypeSchema(int typeId, string stableName, string[] previousNames,
            SchemaTypeKind kind, OldFieldSchema[] fields, int fixedByteSize,
            int definitionTypeId = 0, int[] argTypeIds = null!, OldEnumValue[] enumValues = null!,
            string opaqueFormatterName = "", bool opaqueValueWrittenInline = false)
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
            OpaqueFormatterName = opaqueFormatterName ?? "";
            OpaqueValueWrittenInline = opaqueValueWrittenInline;
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
                else if (info.Kind == SchemaTypeKind.Opaque)
                {
                    // The formatter's identity is a schema name like any other: it has to be in the
                    // table before the entry that indexes it is written.
                    internTable.GetIndex(info.OpaqueFormatterName);
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

                    case SchemaTypeKind.Opaque:
                        // The whole entry, and it is deliberately that small: a formatter name (so a
                        // reader can tell whose bytes these are) and one flag (so a skipper can tell
                        // whether a reference marker stands in front of the frame). There is no member
                        // list and no fixed size — the payload is a length-prefixed block, so its width
                        // is declared per value, in the payload, by the writer.
                        WriteInternedString(entryWriter, info.OpaqueFormatterName, internTable);
                        entryWriter.GetSpan(1)[0] = (byte)(info.OpaqueValueWrittenInline ? 1 : 0);
                        entryWriter.Advance(1);
                        break;

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
        /// Fail loud: a name that cannot be resolved, or that resolves to a CLR type which is
        /// neither registered nor a supported structural/built-in shape, is a
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

            // The registry is the whole lookup: step 2 of it is the very Type.GetType call this used to
            // repeat here as a "legacy/assembly-qualified" fallback, which could therefore only ever hand
            // back the null step 2 had already produced. A miss is final, and the actionable refusal below
            // is what a member name nothing can resolve has to reach — including one the runtime refuses
            // to *parse* rather than decline, such as the canonical name of a three-or-more-element tuple.
            registry.TryResolveType(clrName, out var type);

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
                "from a context root, or declare it with [assembly: CerasKnownType<...>], then rebuild. " +
                "A type from another assembly that Ceras has no built-in representation for — a framework type such as " +
                "System.Uri, System.Version or System.Numerics.BigInteger — is a different case: no declaration and no custom " +
                "IFormatter<T> in a robust context can make it serializable, so store a serializable form of the value instead " +
                "(a string, a byte[], another type the format describes), exclude the member, or use the reflection-based " +
                "CerasSerializer. The source generator reports that case at compile time (CERAS029), long before the first Save.");
        }

        /// <summary>
        /// True for the built-in shapes the generator deliberately does not emit formatters/schemas for:
        /// well-known primitives, one-dimensional arrays, the built-in collections and
        /// <c>KeyValuePair</c> (structural schemas), closed instantiations of the other reserved generic
        /// definitions, collection subclasses served by the built-in collection formatter, and enums.
        /// Everything else must be declared in the generated manifest.
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

        /// <summary>
        /// The reserved id a canonical type name stands for, or -1 when it stands for none.
        /// <para>
        /// A delegation to <see cref="WellKnownTypes.TryGetIdByCanonicalName"/> rather than a second
        /// spelling of the reserved table: the bake resolves every member and argument name through here,
        /// and <c>SchemaRegistry</c> refuses a manifest entry that claims one of these names, so a name
        /// that meant two different ids in two tables would be a defect in only one of them.
        /// </para>
        /// </summary>
        static int ResolveWellKnownId(string typeName)
            => WellKnownTypes.TryGetIdByCanonicalName(typeName, out int id) ? id : -1;

        public static Dictionary<string, OldTypeSchema> Read(byte[] buffer, ref int offset, string[] stringTable)
        {
            var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset);
            var reader = new SequenceReader<byte>(seq);
            var result = Read(ref reader, stringTable);
            offset += (int)reader.Consumed;
            return result;
        }

        /// <summary>
        /// Reads a file's schema section and holds it to the structural invariant
        /// (<see cref="StructuralShape"/>) before returning it, so a caller can never be handed an entry
        /// whose definition or arguments describe no payload. The refusal is a
        /// <see cref="Ceras.Exceptions.CerasDataCorruptionException"/>: the section contradicts itself,
        /// which is a property of the bytes alone and needs nothing from this build's model.
        /// </summary>
        /// <remarks>
        /// <c>ReadUnvalidated</c> is the raw parse, for a diagnostic that has to be able to *show* a section
        /// the load path refuses — see <c>CerasRobustSerializer.DumpSchema</c>. Nothing that interprets a
        /// payload may use it.
        /// </remarks>
        public static Dictionary<string, OldTypeSchema> Read(ref SequenceReader<byte> reader, string[] stringTable)
        {
            var parsed = new List<OldTypeSchema>();
            var byName = ReadUnvalidated(ref reader, stringTable, parsed);

            RequireDescribableEntries(parsed);
            return byName;
        }

        /// <summary>
        /// The raw parse, with no structural validation. <paramref name="parsed"/> receives every entry in
        /// the order it appeared, including one a later entry overwrote under the same name: the invariant
        /// is a property of the bytes, and a name the section contradicts is just as broken whether or not
        /// a reader happens to look it up.
        /// </summary>
        internal static Dictionary<string, OldTypeSchema> ReadUnvalidated(
            ref SequenceReader<byte> reader, string[] stringTable, List<OldTypeSchema> parsed)
        {
            var byName = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal);

            // Every schema entry costs at least a few bytes; bound the declared count against the bytes
            // that actually remain so a corrupt count cannot drive a huge allocation.
            long remaining = reader.Sequence.Length - reader.Consumed;
            uint count = ReadLeb128(ref reader);
            if (count > (uint)remaining)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt schema section: declared {count} entries but only {remaining} bytes remain.");

            // Scratch space for one enum value's eight raw little-endian bytes, hoisted out of the
            // loop over entries: a stackalloc inside a loop reserves fresh stack on every iteration,
            // so a file whose schema declares many entries could grow the frame without bound.
            // Reuse is byte-for-byte identical to a per-entry allocation — the span is exactly the
            // eight bytes TryCopyTo fills, and it is filled (or the read throws) before every
            // ReadUInt64LittleEndian, so no value ever observes a byte left by a previous entry.
            Span<byte> rawEnumValue = stackalloc byte[8];

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

                    case SchemaTypeKind.Opaque:
                    {
                        // The mirror of the encoder's Opaque case, and the only two facts the entry
                        // carries. A truncated or unrecognised flag byte is corruption here for the
                        // same reason the field flags are: the section is the file's own description, and
                        // a reader that guessed which of the two it was looking at would account for the
                        // wrong number of bytes.
                        string formatterName = ReadInternedString(ref reader, stringTable);
                        if (!reader.TryRead(out byte opaqueFlags))
                            throw new Ceras.Exceptions.CerasDataCorruptionException(
                                "Corrupt schema section: unexpected end while reading an opaque payload's flags.");
                        if ((opaqueFlags & ~1) != 0)
                            throw new Ceras.Exceptions.CerasDataCorruptionException(
                                $"Corrupt schema section: entry {i}, the opaque payload entry '{stableName}' has unknown flag bits 0x{opaqueFlags:X2}. " +
                                "Only bit 0 (the value is written inline, with no reference marker in front of it) is defined.");
                        schema = new OldTypeSchema(typeId, stableName, prevNames, kind, null!, 0, 0, null!, null!,
                            formatterName, (opaqueFlags & 1) != 0);
                        break;
                    }

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
                parsed.Add(schema);
            }

            return byName;
        }

        /// <summary>
        /// The earliest door for a malformed collection/array/tuple description: a section whose own entries
        /// contradict it cannot be read safely, and both clauses asked here need nothing but the section
        /// itself — the argument count against the definition it names, and the definition against the ids
        /// the section records. Both are asked before the root type id is read, so nothing in the payload has
        /// been interpreted at all.
        /// <para>
        /// The third clause, an argument identity nothing describes, is deliberately <em>not</em> asked
        /// here: that one is the migration layer's question, and the per-member walk there reports it with
        /// the member and the position inside its shape named, which is the more useful message. See
        /// <c>MigrationPlanBuilder.BuildAll</c>.
        /// </para>
        /// </summary>
        internal static void RequireDescribableEntries(List<OldTypeSchema> parsed)
        {
            var describedIds = new HashSet<int>();
            foreach (var entry in parsed) describedIds.Add(entry.TypeId);

            for (int i = 0; i < parsed.Count; i++)
            {
                var verdict = StructuralShape.InspectShape(parsed[i]);
                if (!verdict.IsDefect) verdict = StructuralShape.InspectDefinition(parsed[i], describedIds.Contains);
                if (verdict.IsDefect)
                    throw StructuralShape.CorruptSection(parsed[i], verdict, i);
            }
        }

        /// <summary>
        /// Reads a leb128 count and rejects it when it exceeds the bytes that actually remain, so a
        /// corrupt count cannot trigger an oversized allocation (see ProtocolValidationTests). Each element costs at
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

        /// <summary>
        /// Reads a LEB128 that has to be a <b>type id</b> — a type-value tag, a manifest type id, a
        /// definition id or an argument id.
        /// <para>
        /// <see cref="ReadLeb128(ref SequenceReader{byte})"/> is a <c>uint</c> and accepts five bytes, so it can legitimately return
        /// a value above <see cref="int.MaxValue"/>. Casting that to <c>int</c> yields a <em>negative</em>
        /// number, and every id space in the format starts at 0 with nothing above <see cref="int.MaxValue"/>
        /// — a negative id then reads as a small well-known id, which is a different type entirely, and the
        /// decoder goes on to misinterpret the rest of the payload. The value is therefore rejected here,
        /// where the description of what was being read is still known, instead of downstream.
        /// </para>
        /// </summary>
        internal static int ReadTypeIdLeb128(ref SequenceReader<byte> reader, string description)
        {
            uint raw = ReadLeb128(ref reader);
            if (raw > int.MaxValue)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: {description} decodes to {raw}, which is not a type id. Type ids are never negative, " +
                    $"so a value above {int.MaxValue} is a corrupt field rather than an id.");
            return (int)raw;
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
