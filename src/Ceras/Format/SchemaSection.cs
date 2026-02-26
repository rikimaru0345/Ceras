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
            SchemaTypeKind kind, OldFieldSchema[] fields, int fixedByteSize)
        {
            TypeId        = typeId;
            StableName    = stableName ?? "";
            PreviousNames = previousNames ?? Array.Empty<string>();
            Kind          = kind;
            Fields        = fields   ?? Array.Empty<OldFieldSchema>();
            EnumValues    = Array.Empty<OldEnumValue>();
            ArgTypeIds    = Array.Empty<int>();
            FixedByteSize = fixedByteSize;
        }

        internal OldTypeSchema(int typeId, string stableName, string[] previousNames,
            int underlyingTypeId, OldEnumValue[] enumValues)
        {
            TypeId          = typeId;
            StableName      = stableName ?? "";
            PreviousNames   = previousNames ?? Array.Empty<string>();
            Kind            = SchemaTypeKind.Enum;
            DefinitionTypeId = underlyingTypeId;
            EnumValues      = enumValues ?? Array.Empty<OldEnumValue>();
            Fields          = Array.Empty<OldFieldSchema>();
            ArgTypeIds       = Array.Empty<int>();
        }

        internal OldTypeSchema(int typeId, string stableName, string[] previousNames,
            int definitionTypeId, int[] argTypeIds)
        {
            TypeId           = typeId;
            StableName       = stableName ?? "";
            PreviousNames    = previousNames ?? Array.Empty<string>();
            Kind             = SchemaTypeKind.GenericInstantiation;
            DefinitionTypeId = definitionTypeId;
            ArgTypeIds       = argTypeIds ?? Array.Empty<int>();
            Fields           = Array.Empty<OldFieldSchema>();
            EnumValues       = Array.Empty<OldEnumValue>();
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

    public sealed class OldEnumValue
    {
        public string Name  { get; }
        public long   Value { get; }
        internal OldEnumValue(string name, long value) { Name = name ?? ""; Value = value; }
    }

    public static class SchemaSection
    {
        public static Dictionary<Type, int> Write(ref byte[] buffer, ref int offset, SchemaRegistry registry, StringInternTable internTable)
        {
            var typeToId = new Dictionary<Type, int>();
            var queue = new Queue<(Type type, int id, SchemaTypeInfo info)>();
            var alreadyProcessed = new HashSet<int>();

            foreach (var entry in registry.All())
            {
                typeToId[entry.type] = entry.id;
                queue.Enqueue(entry);
            }

            var entryWriter = new ArrayBufferWriter<byte>(4096);
            int finalCount = 0;

            while (queue.Count > 0)
            {
                var (type, id, info) = queue.Dequeue();
                if (!alreadyProcessed.Add(id)) continue;
                finalCount++;

                WriteLeb128(entryWriter, (uint)id);
                WriteInternedString(entryWriter, info.StableName, internTable);

                WriteLeb128(entryWriter, (uint)(info.PreviousTypeNames?.Length ?? 0));
                if (info.PreviousTypeNames != null)
                {
                    foreach (var prev in info.PreviousTypeNames)
                        WriteInternedString(entryWriter, prev, internTable);
                }

                entryWriter.GetSpan(1)[0] = (byte)info.Kind;
                entryWriter.Advance(1);

                switch (info.Kind)
                {
                    case SchemaTypeKind.Enum:
                        int underlyingId = ResolveId(info.UnderlyingTypeName, typeToId, queue, registry);
                        if (underlyingId == WellKnownTypes.Null) underlyingId = WellKnownTypes.Int32;
                        WriteLeb128(entryWriter, (uint)underlyingId);
                        WriteLeb128(entryWriter, (uint)(info.EnumValues?.Length ?? 0));
                        if (info.EnumValues != null)
                        {
                            foreach (var ev in info.EnumValues)
                            {
                                WriteInternedString(entryWriter, ev.Name, internTable);
                                BinaryPrimitives.WriteInt64LittleEndian(entryWriter.GetSpan(8), ev.Value);
                                entryWriter.Advance(8);
                            }
                        }
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

                                int fieldTypeId = ResolveId(field.TypeName, typeToId, queue, registry);
                                WriteLeb128(entryWriter, (uint)fieldTypeId);
                                entryWriter.GetSpan(1)[0] = (byte)(field.IsMonomorphic ? 1 : 0);
                                entryWriter.Advance(1);
                            }
                        }
                        WriteLeb128(entryWriter, (uint)info.FixedByteSize);
                        break;

                    case SchemaTypeKind.GenericInstantiation:
                        int defId = ResolveId(info.UnderlyingTypeName, typeToId, queue, registry);
                        WriteLeb128(entryWriter, (uint)defId);
                        WriteLeb128(entryWriter, (uint)(info.ArgTypeNames?.Length ?? 0));
                        if (info.ArgTypeNames != null)
                            foreach (var argName in info.ArgTypeNames)
                                WriteLeb128(entryWriter, (uint)ResolveId(argName, typeToId, queue, registry));
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

        static int ResolveId(string clrName, Dictionary<Type, int> typeToId, Queue<(Type, int, SchemaTypeInfo)> queue, SchemaRegistry registry)
        {
            if (clrName == null) return WellKnownTypes.Null;

            int wk = ResolveWellKnownId(clrName);
            if (wk >= 0) return wk;

            // Console.WriteLine($"DEBUG: SchemaSection.ResolveId('{clrName}')");

            if (registry.TryResolveType(clrName, out var type))
            {
                if (registry.TryGetId(type, out int id))
                {
                    if (!typeToId.ContainsKey(type))
                    {
                        typeToId[type] = id;
                        if (registry.TryGetInfo(type, out var info))
                            queue.Enqueue((type, id, info));
                    }
                    return id;
                }
            }

            try
            {
                var t = Type.GetType(clrName);
                if (t != null)
                {
                    if (registry.TryGetId(t, out int id))
                    {
                        if (!typeToId.ContainsKey(t))
                        {
                            typeToId[t] = id;
                            if (registry.TryGetInfo(t, out var info))
                                queue.Enqueue((t, id, info));
                        }
                        return id;
                    }
                }
            }
            catch (Exception e)
            {
                throw new Exception($"Error in SchemaSection.ResolveId for '{clrName}': {e.Message}", e);
            }

            return WellKnownTypes.Null;
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
            uint count = ReadLeb128(ref reader);
            for (uint i = 0; i < count; i++)
            {
                int    typeId     = (int)ReadLeb128(ref reader);
                string stableName = ReadInternedString(ref reader, stringTable);
                uint prevCount = ReadLeb128(ref reader);
                var  prevNames = new string[prevCount];
                for (uint j = 0; j < prevCount; j++) prevNames[j] = ReadInternedString(ref reader, stringTable);

                reader.TryRead(out byte kindByte);
                var kind = (SchemaTypeKind)kindByte;

                OldTypeSchema schema;
                switch (kind)
                {
                    case SchemaTypeKind.Enum:
                        int underlyingId = (int)ReadLeb128(ref reader);
                        uint valCount = ReadLeb128(ref reader);
                        var vals = new OldEnumValue[valCount];
                        for (uint j = 0; j < valCount; j++)
                        {
                            string name = ReadInternedString(ref reader, stringTable);
                            reader.TryReadLittleEndian(out long val);
                            vals[j] = new OldEnumValue(name, val);
                        }
                        schema = new OldTypeSchema(typeId, stableName, prevNames, underlyingId, vals);
                        break;

                    case SchemaTypeKind.Struct:
                    case SchemaTypeKind.Class:
                        uint fieldCount = ReadLeb128(ref reader);
                        var fields = new OldFieldSchema[fieldCount];
                        for (uint j = 0; j < fieldCount; j++)
                        {
                            string fStable = ReadInternedString(ref reader, stringTable);
                            uint fpCount = ReadLeb128(ref reader);
                            var fpNames = new string[fpCount];
                            for (uint k = 0; k < fpCount; k++) fpNames[k] = ReadInternedString(ref reader, stringTable);
                            int fTypeId = (int)ReadLeb128(ref reader);
                            reader.TryRead(out byte flags);
                            fields[j] = new OldFieldSchema(fStable, fpNames, fTypeId, (flags & 1) != 0);
                        }
                        int fixedSize = (int)ReadLeb128(ref reader);
                        schema = new OldTypeSchema(typeId, stableName, prevNames, kind, fields, fixedSize);
                        break;

                    case SchemaTypeKind.GenericInstantiation:
                        int defId = (int)ReadLeb128(ref reader);
                        uint argCount = ReadLeb128(ref reader);
                        var argIds = new int[argCount];
                        for (uint j = 0; j < argCount; j++) argIds[j] = (int)ReadLeb128(ref reader);
                        schema = new OldTypeSchema(typeId, stableName, prevNames, defId, argIds);
                        break;

                    default: throw new InvalidOperationException("Unknown kind");
                }
                byName[stableName] = schema;
                foreach (var pn in prevNames) if (pn != null) byName[pn] = schema;
            }
            return byName;
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
                if (!reader.TryRead(out byte b)) throw new InvalidOperationException("End of sequence");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 35) throw new InvalidOperationException("LEB128 too long");
            }
        }

        internal static void WriteInternedString(IBufferWriter<byte> writer, string s, StringInternTable internTable)
        {
            int index = internTable.GetIndex(s);
            SerializerBinary.WriteUInt32(writer, (uint)index);
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

        internal static string ReadInlineString(ref SequenceReader<byte> reader)
        {
            uint byteCount = ReadLeb128(ref reader);
            if (byteCount == 0) return string.Empty;
            if (reader.UnreadSpan.Length >= byteCount)
            {
                var s = Encoding.UTF8.GetString(reader.UnreadSpan.Slice(0, (int)byteCount));
                reader.Advance((int)byteCount);
                return s;
            }
            else
            {
                byte[] temp = ArrayPool<byte>.Shared.Rent((int)byteCount);
                try
                {
                    reader.TryCopyTo(temp.AsSpan(0, (int)byteCount));
                    var s = Encoding.UTF8.GetString(temp, 0, (int)byteCount);
                    reader.Advance((int)byteCount);
                    return s;
                }
                finally { ArrayPool<byte>.Shared.Return(temp); }
            }
        }
    }
}
