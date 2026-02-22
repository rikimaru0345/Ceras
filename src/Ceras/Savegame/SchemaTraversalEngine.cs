using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ceras;

namespace Ceras.Savegame
{
    // ReferenceFormatter constants (Bias=5)
    //   Null             = -1  →  wire uint 4
    //   NewValue         = -2  →  wire uint 3
    //   NewValueSameType = -3  →  wire uint 2
    //   ExternalObject   = -4  →  wire uint 1
    //   InlineType       = -5  →  wire uint 0
    //   BackRef(N)        = N  →  wire uint N+5

    sealed class SchemaTraversalEngine
    {
        const int RefBias             = 5;
        const int RefNull             = -1;
        const int RefNewValue         = -2;
        const int RefNewValueSameType = -3;

        readonly Dictionary<string, OldTypeSchema>     _oldByName;
        readonly Dictionary<int, OldTypeSchema>        _oldById;
        readonly Dictionary<string, TypeMigrationPlan> _plans;
        readonly string[]                              _stringTable;
        readonly uint                                  _saveGameVersion;
        readonly List<object>                          _objectCache = new();

        public SchemaTraversalEngine(
            Dictionary<string, OldTypeSchema> oldSchemas,
            string[]                          stringTable,
            uint                              saveGameVersion)
        {
            _oldByName       = oldSchemas;
            _stringTable     = stringTable;
            _saveGameVersion = saveGameVersion;

            _oldById = new Dictionary<int, OldTypeSchema>();
            foreach (var s in oldSchemas.Values)
                _oldById.TryAdd(s.TypeId, s);

            _plans = MigrationPlanBuilder.BuildAll(oldSchemas);
        }

        // ── Public entry point ────────────────────────────────────────────────

        public T ReadRoot<T>(byte[] data, ref int offset)
        {
            object obj = ReadClassValue(typeof(T).Name, data, ref offset);
            // Also try stable name lookup via registry
            if (obj == null && !_oldByName.ContainsKey(typeof(T).Name))
            {
                // look up using stable name from registry
                SavegameSchemaRegistry.TryGetInfo(typeof(T), out var info);
                if (info != null)
                    obj = ReadClassValueByStableName(info.StableName, data, ref offset);
            }
            if (obj == null) return default;
            return (T)obj;
        }

        // ── Core dispatch ─────────────────────────────────────────────────────

        object ReadValue(int typeId, byte[] data, ref int offset)
        {
            switch (typeId)
            {
                case WellKnownTypes.Null:    return null;
                case WellKnownTypes.Bool:    { bool v = data[offset] != 0; offset += 1; return v; }
                case WellKnownTypes.Byte:    { byte v = data[offset]; offset += 1; return v; }
                case WellKnownTypes.SByte:   { sbyte v = (sbyte)data[offset]; offset += 1; return v; }
                case WellKnownTypes.Char:
                {
                    char v = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                    offset += 2; return v;
                }
                case WellKnownTypes.Int16:
                {
                    short v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));
                    offset += 2; return v;
                }
                case WellKnownTypes.UInt16:
                {
                    ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                    offset += 2; return v;
                }
                case WellKnownTypes.Int32:   return SerializerBinary.ReadInt32(data, ref offset);
                case WellKnownTypes.UInt32:  return SerializerBinary.ReadUInt32(data, ref offset);
                case WellKnownTypes.Single:  return SerializerBinary.ReadFloat32Fixed(data, ref offset);
                case WellKnownTypes.Int64:   return SerializerBinary.ReadInt64(data, ref offset);
                case WellKnownTypes.UInt64:  return SerializerBinary.ReadUInt64(data, ref offset);
                case WellKnownTypes.Double:
                {
                    double v = BitConverter.Int64BitsToDouble(
                        BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset)));
                    offset += 8; return v;
                }
                case WellKnownTypes.Decimal:
                {
                    int lo  = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                    int mid = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
                    int hi  = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 8));
                    int fl  = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 12));
                    offset += 16;
                    return new decimal(new[] { lo, mid, hi, fl });
                }
                case WellKnownTypes.String:
                {
                    int idx = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                    offset += 4;
                    return ReadStringFromTable(idx);
                }
                case WellKnownTypes.DateTime:
                {
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
                    offset += 8;
                    return new DateTime(ticks, DateTimeKind.Utc);
                }
                case WellKnownTypes.TimeSpan:
                {
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
                    offset += 8;
                    return new TimeSpan(ticks);
                }
                case WellKnownTypes.Guid:
                {
                    var g = new Guid(data.AsSpan(offset, 16));
                    offset += 16; return g;
                }
                default:
                {
                    if (typeId >= WellKnownTypes.UserTypeBase)
                        return ReadUserType(typeId, data, ref offset);
                    SkipValue(typeId, data, ref offset);
                    return null;
                }
            }
        }

        void SkipValue(int typeId, byte[] data, ref int offset)
        {
            int fixedSize = WellKnownTypes.GetFixedSize(typeId);
            if (fixedSize > 0) { offset += fixedSize; return; }

            switch (typeId)
            {
                case WellKnownTypes.Int32:
                    SerializerBinary.ReadInt32(data, ref offset);
                    return;
                case WellKnownTypes.UInt32:
                    SerializerBinary.ReadUInt32(data, ref offset);
                    return;
                case WellKnownTypes.Int64:
                    SerializerBinary.ReadInt64(data, ref offset);
                    return;
                case WellKnownTypes.UInt64:
                    SerializerBinary.ReadUInt64(data, ref offset);
                    return;
                case WellKnownTypes.Object:
                    SkipClassRef(data, ref offset);
                    return;
                default:
                    if (typeId >= WellKnownTypes.UserTypeBase)
                        SkipUserType(typeId, data, ref offset);
                    return;
            }
        }

        // ── User type dispatch ────────────────────────────────────────────────

        object ReadUserType(int typeId, byte[] data, ref int offset)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema))
                return null;

            return oldSchema.Kind switch
            {
                SchemaTypeKind.Struct => ReadStructValue(oldSchema, data, ref offset),
                SchemaTypeKind.Class  => ReadClassValue(oldSchema.StableName, data, ref offset),
                SchemaTypeKind.Enum   => ReadEnumValue(oldSchema, data, ref offset),
                _                     => null,
            };
        }

        void SkipUserType(int typeId, byte[] data, ref int offset)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema)) return;

            switch (oldSchema.Kind)
            {
                case SchemaTypeKind.Struct: SkipStructFields(oldSchema, data, ref offset); break;
                case SchemaTypeKind.Class:  SkipClassRef(data, ref offset);                break;
                case SchemaTypeKind.Enum:   SkipEnumValue(oldSchema, data, ref offset);    break;
            }
        }

        // ── Enum ─────────────────────────────────────────────────────────────

        object ReadEnumValue(OldTypeSchema oldSchema, byte[] data, ref int offset)
        {
            int underlying = oldSchema.DefinitionTypeId == 0
                ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            return ReadValue(underlying, data, ref offset);
        }

        void SkipEnumValue(OldTypeSchema oldSchema, byte[] data, ref int offset)
        {
            int underlying = oldSchema.DefinitionTypeId == 0
                ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            SkipValue(underlying, data, ref offset);
        }

        // ── Struct ───────────────────────────────────────────────────────────

        object ReadStructValue(OldTypeSchema oldSchema, byte[] data, ref int offset)
        {
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type currentType = plan?.CurrentType;

            object obj = currentType != null
                ? RuntimeHelpers.GetUninitializedObject(currentType)
                : null;

            ReadAndApplyFields(oldSchema, plan, obj, data, ref offset);

            if (plan != null && plan.HasChanges && plan.OnAfterDeserialize != null && obj != null)
                FireCallback(obj, plan, plan.AddedFieldNames, CollectSkippedFields(plan));

            return obj;
        }

        void SkipStructFields(OldTypeSchema oldSchema, byte[] data, ref int offset)
        {
            foreach (var f in oldSchema.Fields)
                SkipValue(f.TypeId, data, ref offset);
        }

        // ── Class (with ref marker) ───────────────────────────────────────────

        object ReadClassValue(string stableName, byte[] data, ref int offset)
        {
            return ReadClassValueByStableName(stableName, data, ref offset);
        }

        object ReadClassValueByStableName(string stableName, byte[] data, ref int offset)
        {
            int marker = SerializerBinary.ReadUInt32Bias(data, ref offset, RefBias);

            if (marker == RefNull) return null;

            if (marker >= 0)
            {
                // Back-reference
                return marker < _objectCache.Count ? _objectCache[marker] : null;
            }

            // NewValue or NewValueSameType
            if (marker == RefNewValue)
                SkipTypeInfo(data, ref offset);

            _oldByName.TryGetValue(stableName, out var oldSchema);
            _plans.TryGetValue(stableName, out var plan);

            Type currentType = plan?.CurrentType;
            object obj = currentType != null
                ? RuntimeHelpers.GetUninitializedObject(currentType)
                : null;

            int cacheIndex = _objectCache.Count;
            _objectCache.Add(obj);

            if (oldSchema != null)
                ReadAndApplyFields(oldSchema, plan, obj, data, ref offset);

            _objectCache[cacheIndex] = obj;

            if (plan != null && plan.HasChanges && plan.OnAfterDeserialize != null && obj != null)
                FireCallback(obj, plan, plan.AddedFieldNames, CollectSkippedFields(plan));

            return obj;
        }

        void SkipClassRef(byte[] data, ref int offset)
        {
            int marker = SerializerBinary.ReadUInt32Bias(data, ref offset, RefBias);
            if (marker == RefNull) return;
            if (marker >= 0)      return; // back-ref

            if (marker == RefNewValue)
                SkipTypeInfo(data, ref offset);
            // Cannot skip fields without knowing the type — best-effort
        }

        // ── Field read/skip ───────────────────────────────────────────────────

        void ReadAndApplyFields(
            OldTypeSchema     oldSchema,
            TypeMigrationPlan plan,
            object            target,
            byte[]            data,
            ref int           offset)
        {
            var mappings = plan?.Mappings ?? BuildSkipMappings(oldSchema);
            foreach (var mapping in mappings)
            {
                if (mapping.NewField == null)
                {
                    SkipValue(mapping.OldField.TypeId, data, ref offset);
                }
                else
                {
                    object value = ReadValue(mapping.OldField.TypeId, data, ref offset);
                    if (target != null)
                    {
                        object coerced = Coerce(value, mapping.Coercion, mapping.NewField.FieldType);
                        mapping.NewField.SetValue(target, coerced);
                    }
                }
            }
        }

        static FieldMapping[] BuildSkipMappings(OldTypeSchema old)
        {
            var m = new FieldMapping[old.Fields.Length];
            for (int i = 0; i < old.Fields.Length; i++)
                m[i] = new FieldMapping { OldField = old.Fields[i] };
            return m;
        }

        // ── Coercion ─────────────────────────────────────────────────────────

        static object Coerce(object value, CoercionKind kind, Type targetType)
        {
            if (kind == CoercionKind.None) return value;
            return kind switch
            {
                CoercionKind.Int32ToInt64   => value is int i  ? (object)(long)i    : value,
                CoercionKind.Int16ToInt32   => value is short s ? (object)(int)s    : value,
                CoercionKind.Int32ToSingle  => value is int i2  ? (object)(float)i2 : value,
                CoercionKind.Int32ToDouble  => value is int i3  ? (object)(double)i3 : value,
                CoercionKind.SingleToDouble => value is float f  ? (object)(double)f : value,
                _                           => value,
            };
        }

        // ── Type info skipping ────────────────────────────────────────────────

        void SkipTypeInfo(byte[] data, ref int offset)
        {
            int mode = SerializerBinary.ReadUInt32Bias(data, ref offset, 5);
            if (mode == -2) // NewSingle: type name follows
                SerializerBinary.ReadString(data, ref offset);
            else if (mode == -3) // NewGeneric
            {
                SkipTypeInfo(data, ref offset); // base type
                byte argCount = SerializerBinary.ReadByte(data, ref offset);
                for (int i = 0; i < argCount; i++)
                    SkipTypeInfo(data, ref offset);
            }
        }

        // ── String table ─────────────────────────────────────────────────────

        string ReadStringFromTable(int index)
        {
            // _stringTable is 1-based (index 0 = null, from StringTable.Load)
            if (index == 0) return null;
            return (uint)index < (uint)_stringTable.Length ? _stringTable[index] : null;
        }

        // ── OnAfterDeserialize callback ───────────────────────────────────────

        static HashSet<string> CollectSkippedFields(TypeMigrationPlan plan)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in plan.Mappings)
                if (m.NewField == null && m.OldField != null)
                    set.Add(m.OldField.StableName);
            return set;
        }

        void FireCallback(
            object            obj,
            TypeMigrationPlan plan,
            string[]          addedFields,
            HashSet<string>   skippedFields)
        {
            if (plan.OnAfterDeserialize == null) return;

            var addedSet = new HashSet<string>(addedFields, StringComparer.Ordinal);
            var ctx = new MigrationContext(
                wasMigrated:    true,
                oldGameVersion: _saveGameVersion,
                skippedFields:  skippedFields,
                addedFields:    addedSet);

            plan.OnAfterDeserialize.Invoke(obj, new object[] { ctx });
        }
    }
}
