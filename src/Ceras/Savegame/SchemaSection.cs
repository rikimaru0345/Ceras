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
            var registered = new List<(Type type, int id, SchemaTypeInfo info)>(SavegameSchemaRegistry.All());
            var typeToId = new Dictionary<Type, int>();
            var clrNameToId = new Dictionary<string, int>(StringComparer.Ordinal);
            
            foreach (var (type, id, _) in registered)
            {
                if (type == null) continue;
                typeToId[type] = id;
                clrNameToId[type.FullName ?? type.Name] = id;
            }

            WriteLeb128(ref buffer, ref offset, (uint)registered.Count);

            foreach (var (type, id, info) in registered)
            {
                WriteLeb128(ref buffer, ref offset, (uint)id);
                WriteInlineString(ref buffer, ref offset, info.StableName);

                WriteLeb128(ref buffer, ref offset, (uint)(info.PreviousTypeNames?.Length ?? 0));
                if (info.PreviousTypeNames != null)
                {
                    foreach (var prev in info.PreviousTypeNames)
                        WriteInlineString(ref buffer, ref offset, prev);
                }

                EnsureCapacity(ref buffer, offset + 1);
                buffer[offset++] = (byte)info.Kind;

                switch (info.Kind)
                {
                    case SchemaTypeKind.Enum:
                        int underlyingId = ResolveFieldTypeId(info.UnderlyingTypeName, clrNameToId);
                        if (underlyingId == WellKnownTypes.Null) underlyingId = WellKnownTypes.Int32;
                        WriteLeb128(ref buffer, ref offset, (uint)underlyingId);
                        WriteLeb128(ref buffer, ref offset, (uint)(info.EnumValues?.Length ?? 0));
                        if (info.EnumValues != null)
                        {
                            foreach (var ev in info.EnumValues)
                            {
                                WriteInlineString(ref buffer, ref offset, ev.Name);
                                EnsureCapacity(ref buffer, offset + 8);
                                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), ev.Value);
                                offset += 8;
                            }
                        }
                        break;

                    case SchemaTypeKind.Struct:
                    case SchemaTypeKind.Class:
                        WriteLeb128(ref buffer, ref offset, (uint)(info.Fields?.Length ?? 0));
                        if (info.Fields != null)
                        {
                            foreach (var field in info.Fields)
                            {
                                WriteInlineString(ref buffer, ref offset, field.StableName);

                                WriteLeb128(ref buffer, ref offset, (uint)(field.PreviousNames?.Length ?? 0));
                                if (field.PreviousNames != null)
                                {
                                    foreach (var pn in field.PreviousNames)
                                        WriteInlineString(ref buffer, ref offset, pn);
                                }

                                int fieldTypeId = ResolveFieldTypeId(field.TypeName, clrNameToId);
                                WriteLeb128(ref buffer, ref offset, (uint)fieldTypeId);

                                EnsureCapacity(ref buffer, offset + 1);
                                buffer[offset++] = (byte)(field.IsMonomorphic ? 1 : 0);
                            }
                        }
                        WriteLeb128(ref buffer, ref offset, (uint)info.FixedByteSize);
                        break;

                    case SchemaTypeKind.GenericInstantiation:
                        int defId = ResolveFieldTypeId(info.UnderlyingTypeName, clrNameToId);
                        WriteLeb128(ref buffer, ref offset, (uint)defId);
                        WriteLeb128(ref buffer, ref offset, (uint)(info.ArgTypeNames?.Length ?? 0));
                        if (info.ArgTypeNames != null)
                        {
                            foreach (var argName in info.ArgTypeNames)
                            {
                                int argId = ResolveFieldTypeId(argName, clrNameToId);
                                WriteLeb128(ref buffer, ref offset, (uint)argId);
                            }
                        }
                        break;
                }
            }

            return typeToId;
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
                "System.Array"    => WellKnownTypes.Array,
                "System.Collections.Generic.List`1" => WellKnownTypes.List,
                _                 => -1,
            };
        }

        static int ResolveFieldTypeId(string typeName, Dictionary<string, int> clrNameToId)
        {
            int wk = ResolveWellKnownId(typeName);
            if (wk >= 0) return wk;

            if (typeName != null && clrNameToId.TryGetValue(typeName, out int id))
                return id;

            return WellKnownTypes.Null;
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
