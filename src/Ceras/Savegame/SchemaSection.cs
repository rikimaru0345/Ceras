using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ceras.Savegame
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
        public static Dictionary<Type, int> Write(ref byte[] buffer, ref int offset)
        {
            var typeToId = new Dictionary<Type, int>();
            var queue = new Queue<(Type type, int id, SchemaTypeInfo info)>();
            var alreadyProcessed = new HashSet<int>();

            // 1. Initial set from registry
            foreach (var entry in SavegameSchemaRegistry.All())
            {
                typeToId[entry.type] = entry.id;
                queue.Enqueue(entry);
            }

            // We need to write the count FIRST, but we don't know it until we've 
            // discovered all generic instantiations. So we write to a temp buffer.
            var tempBuf = new byte[4096];
            int tempOff = 0;
            int finalCount = 0;

            while (queue.Count > 0)
            {
                var (type, id, info) = queue.Dequeue();
                if (!alreadyProcessed.Add(id)) continue;
                finalCount++;

                WriteLeb128(ref tempBuf, ref tempOff, (uint)id);
                WriteInlineString(ref tempBuf, ref tempOff, info.StableName);

                WriteLeb128(ref tempBuf, ref tempOff, (uint)(info.PreviousTypeNames?.Length ?? 0));
                if (info.PreviousTypeNames != null)
                {
                    foreach (var prev in info.PreviousTypeNames)
                        WriteInlineString(ref tempBuf, ref tempOff, prev);
                }

                EnsureCapacity(ref tempBuf, tempOff + 1);
                tempBuf[tempOff++] = (byte)info.Kind;

                switch (info.Kind)
                {
                    case SchemaTypeKind.Enum:
                        int underlyingId = ResolveId(info.UnderlyingTypeName, typeToId, queue);
                        if (underlyingId == WellKnownTypes.Null) underlyingId = WellKnownTypes.Int32;
                        WriteLeb128(ref tempBuf, ref tempOff, (uint)underlyingId);
                        WriteLeb128(ref tempBuf, ref tempOff, (uint)(info.EnumValues?.Length ?? 0));
                        if (info.EnumValues != null)
                        {
                            foreach (var ev in info.EnumValues)
                            {
                                WriteInlineString(ref tempBuf, ref tempOff, ev.Name);
                                EnsureCapacity(ref tempBuf, tempOff + 8);
                                BinaryPrimitives.WriteInt64LittleEndian(tempBuf.AsSpan(tempOff), ev.Value);
                                tempOff += 8;
                            }
                        }
                        break;

                    case SchemaTypeKind.Struct:
                    case SchemaTypeKind.Class:
                        WriteLeb128(ref tempBuf, ref tempOff, (uint)(info.Fields?.Length ?? 0));
                        if (info.Fields != null)
                        {
                            foreach (var field in info.Fields)
                            {
                                WriteInlineString(ref tempBuf, ref tempOff, field.StableName);

                                WriteLeb128(ref tempBuf, ref tempOff, (uint)(field.PreviousNames?.Length ?? 0));
                                if (field.PreviousNames != null)
                                {
                                    foreach (var pn in field.PreviousNames)
                                        WriteInlineString(ref tempBuf, ref tempOff, pn);
                                }

                                int fieldTypeId = ResolveId(field.TypeName, typeToId, queue);
                                WriteLeb128(ref tempBuf, ref tempOff, (uint)fieldTypeId);

                                EnsureCapacity(ref tempBuf, tempOff + 1);
                                tempBuf[tempOff++] = (byte)(field.IsMonomorphic ? 1 : 0);
                            }
                        }
                        WriteLeb128(ref tempBuf, ref tempOff, (uint)info.FixedByteSize);
                        break;

                    case SchemaTypeKind.GenericInstantiation:
                        int defId = ResolveId(info.UnderlyingTypeName, typeToId, queue);
                        WriteLeb128(ref tempBuf, ref tempOff, (uint)defId);
                        WriteLeb128(ref tempBuf, ref tempOff, (uint)(info.ArgTypeNames?.Length ?? 0));
                        if (info.ArgTypeNames != null)
                        {
                            foreach (var argName in info.ArgTypeNames)
                            {
                                int argId = ResolveId(argName, typeToId, queue);
                                WriteLeb128(ref tempBuf, ref tempOff, (uint)argId);
                            }
                        }
                        break;
                }
            }

            // 2. Write real count then flush temp buffer
            WriteLeb128(ref buffer, ref offset, (uint)finalCount);
            EnsureCapacity(ref buffer, offset + tempOff);
            tempBuf.AsSpan(0, tempOff).CopyTo(buffer.AsSpan(offset));
            offset += tempOff;

            return typeToId;
        }

        static int ResolveId(string clrName, Dictionary<Type, int> typeToId, Queue<(Type, int, SchemaTypeInfo)> queue)
        {
            if (clrName == null) return WellKnownTypes.Null;

            int wk = ResolveWellKnownId(clrName);
            if (wk >= 0) return wk;

            if (SavegameSchemaRegistry.TryResolveType(clrName, out var type))
            {
                if (SavegameSchemaRegistry.TryGetId(type, out int id))
                {
                    if (!typeToId.ContainsKey(type))
                    {
                        typeToId[type] = id;
                        SavegameSchemaRegistry.TryGetInfo(type, out var info);
                        queue.Enqueue((type, id, info));
                    }
                    return id;
                }
            }

            var t = Type.GetType(clrName);
            if (t != null)
            {
                if (SavegameSchemaRegistry.TryGetId(t, out int id))
                {
                    if (!typeToId.ContainsKey(t))
                    {
                        typeToId[t] = id;
                        SavegameSchemaRegistry.TryGetInfo(t, out var info);
                        queue.Enqueue((t, id, info));
                    }
                    return id;
                }
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

        public static Dictionary<string, OldTypeSchema> Read(byte[] buffer, ref int offset)
        {
            var byName = new Dictionary<string, OldTypeSchema>(StringComparer.Ordinal);

            uint count = ReadLeb128(buffer, ref offset);
            for (uint i = 0; i < count; i++)
            {
                int    typeId     = (int)ReadLeb128(buffer, ref offset);
                string stableName = ReadInlineString(buffer, ref offset);

                uint prevCount = ReadLeb128(buffer, ref offset);
                var  prevNames = new string[prevCount];
                for (uint j = 0; j < prevCount; j++)
                    prevNames[j] = ReadInlineString(buffer, ref offset);

                var kind = (SchemaTypeKind)buffer[offset++];

                OldTypeSchema schema;
                switch (kind)
                {
                    case SchemaTypeKind.Enum:
                    {
                        int  underlyingId = (int)ReadLeb128(buffer, ref offset);
                        uint valCount     = ReadLeb128(buffer, ref offset);
                        var  vals         = new OldEnumValue[valCount];
                        for (uint j = 0; j < valCount; j++)
                        {
                            string name = ReadInlineString(buffer, ref offset);
                            long   val  = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset));
                            offset += 8;
                            vals[j] = new OldEnumValue(name, val);
                        }
                        schema = new OldTypeSchema(typeId, stableName, prevNames, underlyingId, vals);
                        break;
                    }

                    case SchemaTypeKind.Struct:
                    case SchemaTypeKind.Class:
                    {
                        uint fieldCount = ReadLeb128(buffer, ref offset);
                        var  fields     = new OldFieldSchema[fieldCount];
                        for (uint j = 0; j < fieldCount; j++)
                        {
                            string fStable = ReadInlineString(buffer, ref offset);

                            uint fpCount = ReadLeb128(buffer, ref offset);
                            var  fpNames = new string[fpCount];
                            for (uint k = 0; k < fpCount; k++)
                                fpNames[k] = ReadInlineString(buffer, ref offset);

                            int  fTypeId = (int)ReadLeb128(buffer, ref offset);
                            bool mono    = (buffer[offset++] & 1) != 0;
                            fields[j] = new OldFieldSchema(fStable, fpNames, fTypeId, mono);
                        }
                        int fixedSize = (int)ReadLeb128(buffer, ref offset);
                        schema = new OldTypeSchema(typeId, stableName, prevNames, kind, fields, fixedSize);
                        break;
                    }

                    case SchemaTypeKind.GenericInstantiation:
                    {
                        int  defId    = (int)ReadLeb128(buffer, ref offset);
                        uint argCount = ReadLeb128(buffer, ref offset);
                        var  argIds   = new int[argCount];
                        for (uint j = 0; j < argCount; j++)
                            argIds[j] = (int)ReadLeb128(buffer, ref offset);
                        schema = new OldTypeSchema(typeId, stableName, prevNames, defId, argIds);
                        break;
                    }

                    default:
                        throw new InvalidOperationException(
                            $"Unknown schema type kind {(int)kind} at buffer offset {offset - 1}.");
                }

                byName[stableName] = schema;
                foreach (var pn in prevNames)
                    if (pn != null) byName[pn] = schema;
            }

            return byName;
        }

        internal static void WriteLeb128(ref byte[] buffer, ref int offset, uint value)
        {
            EnsureCapacity(ref buffer, offset + 5);
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)((value & 0x7F) | 0x80);
                value >>= 7;
            }
            buffer[offset++] = (byte)value;
        }

        internal static uint ReadLeb128(byte[] buffer, ref int offset)
        {
            uint result = 0;
            int  shift  = 0;
            while (true)
            {
                byte b = buffer[offset++];
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
                if (shift >= 35)
                    throw new InvalidOperationException("LEB128 varint is too long (> 5 bytes).");
            }
        }

        internal static void WriteInlineString(ref byte[] buffer, ref int offset, string s)
        {
            if (s is null) s = string.Empty;
            int byteCount = Encoding.UTF8.GetByteCount(s);
            WriteLeb128(ref buffer, ref offset, (uint)byteCount);
            EnsureCapacity(ref buffer, offset + byteCount);
            Encoding.UTF8.GetBytes(s, 0, s.Length, buffer, offset);
            offset += byteCount;
        }

        internal static string ReadInlineString(byte[] buffer, ref int offset)
        {
            uint byteCount = ReadLeb128(buffer, ref offset);
            var  s         = Encoding.UTF8.GetString(buffer, offset, (int)byteCount);
            offset += (int)byteCount;
            return s;
        }

        static void EnsureCapacity(ref byte[] buffer, int required)
        {
            if (required <= buffer.Length)
                return;
            int newSize = Math.Max(buffer.Length * 2, required + 64);
            System.Array.Resize(ref buffer, newSize);
        }
    }
}
