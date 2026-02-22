using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ceras;

namespace Ceras.Savegame
{
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
            string stableName = ResolveOldStableName(typeof(T));
            object obj = ReadUserTypeByStableName(stableName, typeof(T), data, ref offset);
            if (obj == null) return default;
            return (T)obj;
        }

        string ResolveOldStableName(Type t)
        {
            if (SavegameSchemaRegistry.TryGetInfo(t, out var info))
            {
                if (_oldByName.ContainsKey(info.StableName))
                    return info.StableName;
                foreach (var prev in info.PreviousTypeNames)
                    if (_oldByName.ContainsKey(prev))
                        return prev;
            }
            return t.Name; 
        }

        // ── Core dispatch ─────────────────────────────────────────────────────

        object ReadValue(int typeId, Type targetType, byte[] data, ref int offset)
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
                case WellKnownTypes.Object:
                    return ReadPolymorphicValue(targetType, data, ref offset);
                default:
                {
                    if (typeId >= WellKnownTypes.UserTypeBase)
                        return ReadUserType(typeId, targetType, data, ref offset);
                    SkipValue(typeId, data, ref offset);
                    return null;
                }
            }
        }

        object ReadPolymorphicValue(Type targetType, byte[] data, ref int offset)
        {
            int marker = SerializerBinary.ReadUInt32Bias(data, ref offset, RefBias);

            if (marker == RefNull) return null;

            if (marker >= 0)
            {
                // Back-reference
                return marker < _objectCache.Count ? _objectCache[marker] : null;
            }

            // NewValue or NewValueSameType
            int typeId;
            if (marker == RefNewValue)
            {
                // Polymorphic: read type-ID
                typeId = (int)SchemaSection.ReadLeb128(data, ref offset);
            }
            else
            {
                // Monomorphic (same as target type)
                typeId = GetOldTypeId(targetType);
            }

            if (!_oldById.TryGetValue(typeId, out var oldSchema))
            {
                // Unknown type-ID, we might be able to skip if we were in skip mode
                return null;
            }

            return ReadUserTypeInstance(oldSchema, targetType, data, ref offset);
        }

        object ReadUserType(int typeId, Type targetType, byte[] data, ref int offset)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema))
                return null;

            if (oldSchema.Kind == SchemaTypeKind.Enum)
            {
                _plans.TryGetValue(oldSchema.StableName, out var plan);
                return ReadEnumValue(oldSchema, plan, data, ref offset);
            }

            if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
                return ReadGenericInstantiation(oldSchema, targetType, data, ref offset);

            return ReadPolymorphicValue(targetType ?? GetCurrentType(oldSchema.StableName), data, ref offset);
        }

        object ReadUserTypeByStableName(string stableName, Type targetType, byte[] data, ref int offset)
        {
            if (stableName != null && _oldByName.TryGetValue(stableName, out var oldSchema))
                return ReadUserType(oldSchema.TypeId, targetType, data, ref offset);

            // Cannot find schema for this type, best we can do is try to skip it if we knew the type-ID
            // But we don't. This is a critical failure for the root object.
            return null;
        }

        object ReadUserTypeInstance(OldTypeSchema oldSchema, Type targetType, byte[] data, ref int offset)
        {
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type currentType = plan?.CurrentType ?? targetType;

            if (oldSchema.Kind == SchemaTypeKind.Struct)
            {
                object structObj = currentType != null ? RuntimeHelpers.GetUninitializedObject(currentType) : null;
                ReadAndApplyFields(oldSchema, plan, structObj, data, ref offset);
                return structObj;
            }

            // Class
            object obj = currentType != null ? RuntimeHelpers.GetUninitializedObject(currentType) : null;
            int cacheIndex = _objectCache.Count;
            _objectCache.Add(obj);

            ReadAndApplyFields(oldSchema, plan, obj, data, ref offset);

            _objectCache[cacheIndex] = obj;

            if (plan != null && plan.HasChanges && plan.OnAfterDeserialize != null && obj != null)
                FireCallback(obj, plan, plan.AddedFieldNames, CollectSkippedFields(plan));

            return obj;
        }

        object ReadGenericInstantiation(OldTypeSchema oldSchema, Type targetType, byte[] data, ref int offset)
        {
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Array)
                return ReadArray(oldSchema, targetType, data, ref offset);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.List)
                return ReadList(oldSchema, targetType, data, ref offset);
            
            return null;
        }

        object ReadArray(OldTypeSchema oldSchema, Type targetType, byte[] data, ref int offset)
        {
            uint count = SerializerBinary.ReadUInt32(data, ref offset);
            Type elementType = targetType?.IsArray == true ? targetType.GetElementType() : null;
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;

            if (elementType == null || oldElemTypeId == null) 
            { 
                SkipElements(count, oldElemTypeId, data, ref offset); 
                return null; 
            }

            var arr = Array.CreateInstance(elementType, (int)count);
            for (int i = 0; i < count; i++)
            {
                object val = ReadValue(oldElemTypeId.Value, elementType, data, ref offset);
                arr.SetValue(val, i);
            }
            return arr;
        }

        object ReadList(OldTypeSchema oldSchema, Type targetType, byte[] data, ref int offset)
        {
            uint count = SerializerBinary.ReadUInt32(data, ref offset);
            Type elementType = null;
            if (targetType?.IsGenericType == true)
            {
                var args = targetType.GetGenericArguments();
                if (args.Length == 1) elementType = args[0];
            }
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;

            if (elementType == null || oldElemTypeId == null) 
            { 
                SkipElements(count, oldElemTypeId, data, ref offset); 
                return null; 
            }

            var list = (System.Collections.IList)Activator.CreateInstance(targetType);
            for (int i = 0; i < count; i++)
            {
                object val = ReadValue(oldElemTypeId.Value, elementType, data, ref offset);
                list.Add(val);
            }
            return list;
        }

        void SkipElements(uint count, int? elemTypeId, byte[] data, ref int offset)
        {
            if (elemTypeId == null) return;
            for (int i = 0; i < count; i++)
                SkipValue(elemTypeId.Value, data, ref offset);
        }

        void SkipValue(int typeId, byte[] data, ref int offset)
        {
            if (_oldById.TryGetValue(typeId, out var s) && s.FixedByteSize > 0)
            {
                offset += s.FixedByteSize;
                return;
            }

            int fixedSize = WellKnownTypes.GetFixedSize(typeId);
            if (fixedSize > 0) { offset += fixedSize; return; }

            switch (typeId)
            {
                case WellKnownTypes.Bool:
                case WellKnownTypes.Int32:
                    SerializerBinary.ReadInt32(data, ref offset);
                    return;
                case WellKnownTypes.UInt32:
                    SerializerBinary.ReadUInt32(data, ref offset);
                    return;
                case WellKnownTypes.Object:
                    SkipPolymorphicValue(WellKnownTypes.Object, data, ref offset);
                    return;
                default:
                    if (typeId >= WellKnownTypes.UserTypeBase)
                        SkipUserType(typeId, data, ref offset);
                    return;
            }
        }

        void SkipUserType(int typeId, byte[] data, ref int offset)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema)) return;

            if (oldSchema.FixedByteSize > 0)
            {
                offset += oldSchema.FixedByteSize;
                return;
            }

            switch (oldSchema.Kind)
            {
                case SchemaTypeKind.Struct:
                    foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, data, ref offset);
                    break;
                case SchemaTypeKind.Class:
                    SkipPolymorphicValue(typeId, data, ref offset);
                    break;
                case SchemaTypeKind.Enum:
                    SkipEnumValue(oldSchema, data, ref offset);
                    break;
                case SchemaTypeKind.GenericInstantiation:
                    if (oldSchema.DefinitionTypeId == WellKnownTypes.Array || oldSchema.DefinitionTypeId == WellKnownTypes.List)
                    {
                        uint count = SerializerBinary.ReadUInt32(data, ref offset);
                        int? elemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        SkipElements(count, elemTypeId, data, ref offset);
                    }
                    break;
            }
        }

        void SkipPolymorphicValue(int expectedTypeId, byte[] data, ref int offset)
        {
            int marker = SerializerBinary.ReadUInt32Bias(data, ref offset, RefBias);
            if (marker == RefNull) return;
            if (marker >= 0)      return; 

            int typeId;
            if (marker == RefNewValue)
                typeId = (int)SchemaSection.ReadLeb128(data, ref offset);
            else
                typeId = expectedTypeId;

            if (_oldById.TryGetValue(typeId, out var oldSchema))
            {
                _objectCache.Add(null);
                if (oldSchema.Kind == SchemaTypeKind.Enum)
                {
                    SkipEnumValue(oldSchema, data, ref offset);
                }
                else if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
                {
                    if (oldSchema.DefinitionTypeId == WellKnownTypes.Array || oldSchema.DefinitionTypeId == WellKnownTypes.List)
                    {
                        uint count = SerializerBinary.ReadUInt32(data, ref offset);
                        int? elemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        SkipElements(count, elemTypeId, data, ref offset);
                    }
                }
                else
                {
                    foreach (var f in oldSchema.Fields)
                        SkipValue(f.TypeId, data, ref offset);
                }
            }
        }

        int GetOldTypeId(Type type)
        {
            if (type == null) return WellKnownTypes.Null;
            if (WellKnownTypes.TryGetId(type, out int id)) return id;

            if (SavegameSchemaRegistry.TryGetInfo(type, out var info))
            {
                if (_oldByName.TryGetValue(info.StableName, out var s))
                    return s.TypeId;
                foreach (var prev in info.PreviousTypeNames)
                    if (_oldByName.TryGetValue(prev, out s))
                        return s.TypeId;
            }
            return WellKnownTypes.Null;
        }

        Type GetCurrentType(string stableName)
        {
            SavegameSchemaRegistry.TryResolveType(stableName, out var t);
            return t;
        }

        // ── Enum ─────────────────────────────────────────────────────────────

        object ReadEnumValue(OldTypeSchema oldSchema, TypeMigrationPlan plan, byte[] data, ref int offset)
        {
            int underlyingId = oldSchema.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            object rawValue = ReadValue(underlyingId, null, data, ref offset);

            if (plan?.EnumValueMap != null && plan.CurrentType != null)
            {
                long val = Convert.ToInt64(rawValue);
                if (plan.EnumValueMap.TryGetValue(val, out long mapped))
                    return Enum.ToObject(plan.CurrentType, mapped);
                
                // Value no longer exists in current enum. Return default (0).
                return Enum.ToObject(plan.CurrentType, 0L);
            }

            return rawValue;
        }

        void SkipEnumValue(OldTypeSchema oldSchema, byte[] data, ref int offset)
        {
            int underlying = oldSchema.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            SkipValue(underlying, data, ref offset);
        }

        // ── Field read/skip ───────────────────────────────────────────────────

        void ReadAndApplyFields(OldTypeSchema oldSchema, TypeMigrationPlan plan, object target, byte[] data, ref int offset)
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
                    object value = ReadValue(mapping.OldField.TypeId, mapping.NewField.FieldType, data, ref offset);
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

        string ReadStringFromTable(int index)
        {
            if (index == 0) return null;
            return (uint)index < (uint)_stringTable.Length ? _stringTable[index] : null;
        }

        static HashSet<string> CollectSkippedFields(TypeMigrationPlan plan)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in plan.Mappings)
                if (m.NewField == null && m.OldField != null)
                    set.Add(m.OldField.StableName);
            return set;
        }

        void FireCallback(object obj, TypeMigrationPlan plan, string[] addedFields, HashSet<string> skippedFields)
        {
            if (plan.OnAfterDeserialize == null) return;
            var addedSet = new HashSet<string>(addedFields ?? Array.Empty<string>(), StringComparer.Ordinal);
            var ctx = new MigrationContext(true, _saveGameVersion, skippedFields, addedSet);
            plan.OnAfterDeserialize.Invoke(obj, new object[] { ctx });
        }
    }
}
