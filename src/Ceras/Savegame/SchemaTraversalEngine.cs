using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ceras;
using Ceras.Helpers;

namespace Ceras.Savegame
{
    sealed class SchemaTraversalEngine
    {
        const int RefBias             = 5;
        const int RefNull             = -1;
        const int RefNewValue         = -2;
        const int RefNewValueSameType = -3;
        const int RefExternalObject   = -4;
        const int RefInlineType       = -5;

        readonly Dictionary<string, OldTypeSchema>     _oldByName;
        readonly Dictionary<int, OldTypeSchema>        _oldById;
        readonly Dictionary<string, TypeMigrationPlan> _plans;
        readonly string[]                              _stringTable;
        readonly uint                                  _saveGameVersion;
        readonly CerasSerializer                       _serializer;
        readonly SchemaRegistry                        _registry;
        readonly List<object>                          _objectCache = new();

        public SchemaTraversalEngine(
            Dictionary<string, OldTypeSchema> oldSchemas,
            string[]                          stringTable,
            uint                              saveGameVersion,
            CerasSerializer                   serializer)
        {
            _oldByName       = oldSchemas;
            _stringTable     = stringTable;
            _saveGameVersion = saveGameVersion;
            _serializer      = serializer;
            _registry        = serializer.Config.Savegame.SchemaRegistry;

            _oldById = new Dictionary<int, OldTypeSchema>();
            foreach (var s in oldSchemas.Values)
                _oldById.TryAdd(s.TypeId, s);

            _plans = MigrationPlanBuilder.BuildAll(oldSchemas, _registry);
        }

        // ── Public entry point ────────────────────────────────────────────────

        public T ReadRoot<T>(byte[] data, ref int offset)
        {
            var sequence = new ReadOnlySequence<byte>(data, offset, data.Length - offset);
            var reader = new SequenceReader<byte>(sequence);
            
            T result = ReadRoot<T>(ref reader);
            
            offset += (int)reader.Consumed;
            return result;
        }

        public T ReadRoot<T>(ref SequenceReader<byte> reader)
        {
            string stableName = ResolveOldStableName(typeof(T));
            object obj = ReadUserTypeByStableName(stableName, typeof(T), ref reader);
            if (obj == null) return default;
            return (T)obj;
        }

        string ResolveOldStableName(Type t)
        {
            if (_registry.TryGetInfo(t, out var info))
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

        object ReadValue(int typeId, Type targetType, ref SequenceReader<byte> reader)
        {
            switch (typeId)
            {
                case WellKnownTypes.Null:    return null;
                case WellKnownTypes.Bool:    return SerializerBinary.ReadByte(ref reader) != 0;
                case WellKnownTypes.Byte:    return SerializerBinary.ReadByte(ref reader);
                case WellKnownTypes.SByte:   return (sbyte)SerializerBinary.ReadByte(ref reader);
                case WellKnownTypes.Char:    return (char)SerializerBinary.ReadInt16Fixed(ref reader);
                case WellKnownTypes.Int16:   return SerializerBinary.ReadInt16Fixed(ref reader);
                case WellKnownTypes.UInt16:  return (ushort)SerializerBinary.ReadInt16Fixed(ref reader);
                case WellKnownTypes.Int32:   return SerializerBinary.ReadInt32(ref reader);
                case WellKnownTypes.UInt32:  return SerializerBinary.ReadUInt32(ref reader);
                case WellKnownTypes.Single:  return SerializerBinary.ReadFloat32Fixed(ref reader);
                case WellKnownTypes.Int64:   return SerializerBinary.ReadInt64Fixed(ref reader);
                case WellKnownTypes.UInt64:  return (ulong)SerializerBinary.ReadInt64Fixed(ref reader);
                case WellKnownTypes.Double:  return SerializerBinary.ReadDouble64Fixed(ref reader);
                case WellKnownTypes.Decimal:
                {
                    int lo = SerializerBinary.ReadInt32Fixed(ref reader);
                    int mid = SerializerBinary.ReadInt32Fixed(ref reader);
                    int hi = SerializerBinary.ReadInt32Fixed(ref reader);
                    int fl = SerializerBinary.ReadInt32Fixed(ref reader);
                    return new decimal(new[] { lo, mid, hi, fl });
                }
                case WellKnownTypes.String:
                {
                    int idx = SerializerBinary.ReadInt32Fixed(ref reader);
                    return ReadStringFromTable(idx);
                }
                case WellKnownTypes.DateTime:
                    return new DateTime(SerializerBinary.ReadInt64Fixed(ref reader), DateTimeKind.Utc);
                case WellKnownTypes.TimeSpan:
                    return new TimeSpan(SerializerBinary.ReadInt64Fixed(ref reader));
                case WellKnownTypes.Guid:
                {
                    byte[] b = new byte[16];
                    reader.TryCopyTo(b);
                    reader.Advance(16);
                    return new Guid(b);
                }
                case WellKnownTypes.Type:
                {
                    uint id = SchemaSection.ReadLeb128(ref reader);
                    if (id == WellKnownTypes.Null) return null;
                    var idToType = SavegameContext.IdToType;
                    if (idToType != null && idToType.TryGetValue((int)id, out var t)) return t;
                    return null;
                }
                case WellKnownTypes.Object:
                    return ReadPolymorphicValue(targetType, ref reader);
                default:
                {
                    if (typeId >= WellKnownTypes.UserTypeBase)
                        return ReadUserType(typeId, targetType, ref reader);
                    SkipValue(typeId, ref reader);
                    return null;
                }
            }
        }

        object ReadPolymorphicValue(Type targetType, ref SequenceReader<byte> reader)
        {
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, RefBias);
            if (marker == RefNull) return null;
            if (marker >= 0) return marker < _objectCache.Count ? _objectCache[marker] : null;

            if (marker == RefExternalObject)
            {
                int externalId = SerializerBinary.ReadInt32Fixed(ref reader);
                if (_serializer?.Config.ExternalObjectResolver != null)
                {
                    _serializer.Config.ExternalObjectResolver.Resolve(externalId, out object obj);
                    return obj;
                }
                return null;
            }

            if (marker == RefInlineType)
            {
                return ReadValue(WellKnownTypes.Type, typeof(Type), ref reader);
            }

            int typeId;
            if (marker == RefNewValue)
                typeId = (int)SchemaSection.ReadLeb128(ref reader);
            else
                typeId = GetOldTypeId(targetType);

            if (!_oldById.TryGetValue(typeId, out var oldSchema)) return null;
            return ReadUserTypeInstance(oldSchema, targetType, ref reader);
        }

        object ReadUserType(int typeId, Type targetType, ref SequenceReader<byte> reader)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema)) return null;
            if (oldSchema.Kind == SchemaTypeKind.Enum)
            {
                _plans.TryGetValue(oldSchema.StableName, out var plan);
                return ReadEnumValue(oldSchema, plan, ref reader);
            }
            if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
                return ReadGenericInstantiation(oldSchema, targetType, ref reader);

            return ReadPolymorphicValue(targetType ?? GetCurrentType(oldSchema.StableName), ref reader);
        }

        object ReadUserTypeByStableName(string stableName, Type targetType, ref SequenceReader<byte> reader)
        {
            if (stableName != null && _oldByName.TryGetValue(stableName, out var oldSchema))
                return ReadUserType(oldSchema.TypeId, targetType, ref reader);
            return null;
        }

        object ReadUserTypeInstance(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type currentType = plan?.CurrentType ?? targetType;

            if (oldSchema.Kind == SchemaTypeKind.Struct)
            {
                object structObj = currentType != null ? RuntimeHelpers.GetUninitializedObject(currentType) : null;
                ReadAndApplyFields(oldSchema, plan, structObj, ref reader);
                return structObj;
            }

            object obj = currentType != null ? RuntimeHelpers.GetUninitializedObject(currentType) : null;
            int cacheIndex = _objectCache.Count;
            _objectCache.Add(obj);
            ReadAndApplyFields(oldSchema, plan, obj, ref reader);
            _objectCache[cacheIndex] = obj;

            if (plan != null && plan.HasChanges && plan.OnAfterDeserialize != null && obj != null)
                FireCallback(obj, plan, plan.AddedFieldNames, CollectSkippedFields(plan));

            return obj;
        }

        object ReadGenericInstantiation(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Array) return ReadArray(oldSchema, targetType, ref reader);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.List) return ReadList(oldSchema, targetType, ref reader);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Dictionary) return ReadDictionary(oldSchema, targetType, ref reader);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.HashSet) return ReadHashSet(oldSchema, targetType, ref reader);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Queue) return ReadQueue(oldSchema, targetType, ref reader);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Stack) return ReadStack(oldSchema, targetType, ref reader);
            return null;
        }

        object ReadArray(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            uint count = SerializerBinary.ReadUInt32(ref reader);
            Type elementType = targetType?.IsArray == true ? targetType.GetElementType() : null;
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements(count, oldElemTypeId, ref reader); return null; }

            var arr = Array.CreateInstance(elementType, (int)count);
            for (int i = 0; i < count; i++) arr.SetValue(ReadValue(oldElemTypeId.Value, elementType, ref reader), i);
            return arr;
        }

        object ReadList(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            uint count = SerializerBinary.ReadUInt32(ref reader);
            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements(count, oldElemTypeId, ref reader); return null; }

            var list = (System.Collections.IList)Activator.CreateInstance(targetType);
            for (int i = 0; i < count; i++) list.Add(ReadValue(oldElemTypeId.Value, elementType, ref reader));
            return list;
        }

        object ReadDictionary(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            uint count = SerializerBinary.ReadUInt32(ref reader);
            Type keyType = targetType?.FindClosedArg(typeof(IDictionary<,>), 0);
            Type valType = targetType?.FindClosedArg(typeof(IDictionary<,>), 1);
            int? oldKeyTid = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            int? oldValTid = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;

            if (keyType == null || valType == null || oldKeyTid == null || oldValTid == null)
            {
                for (int i = 0; i < count; i++) { SkipValue(oldKeyTid ?? WellKnownTypes.Object, ref reader); SkipValue(oldValTid ?? WellKnownTypes.Object, ref reader); }
                return null;
            }

            var dict = (System.Collections.IDictionary)Activator.CreateInstance(targetType);
            for (int i = 0; i < count; i++) dict.Add(ReadValue(oldKeyTid.Value, keyType, ref reader), ReadValue(oldValTid.Value, valType, ref reader));
            return dict;
        }

        object ReadHashSet(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            uint count = SerializerBinary.ReadUInt32(ref reader);
            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements(count, oldElemTypeId, ref reader); return null; }

            var set = Activator.CreateInstance(targetType);
            var addMethod = targetType.GetMethod("Add", new[] { elementType });
            for (int i = 0; i < count; i++) addMethod.Invoke(set, new[] { ReadValue(oldElemTypeId.Value, elementType, ref reader) });
            return set;
        }

        object ReadQueue(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            uint count = SerializerBinary.ReadUInt32(ref reader);
            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements(count, oldElemTypeId, ref reader); return null; }

            var queue = Activator.CreateInstance(targetType);
            var enqueueMethod = targetType.GetMethod("Enqueue", new[] { elementType });
            for (int i = 0; i < count; i++) enqueueMethod.Invoke(queue, new[] { ReadValue(oldElemTypeId.Value, elementType, ref reader) });
            return queue;
        }

        object ReadStack(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            uint count = SerializerBinary.ReadUInt32(ref reader);
            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements(count, oldElemTypeId, ref reader); return null; }

            var values = new object[count];
            for (int i = 0; i < count; i++) values[i] = ReadValue(oldElemTypeId.Value, elementType, ref reader);
            var stack = Activator.CreateInstance(targetType);
            var pushMethod = targetType.GetMethod("Push", new[] { elementType });
            for (int i = (int)count - 1; i >= 0; i--) pushMethod.Invoke(stack, new[] { values[i] });
            return stack;
        }

        void SkipElements(uint count, int? elemTypeId, ref SequenceReader<byte> reader)
        {
            if (elemTypeId == null) return;
            for (int i = 0; i < count; i++) SkipValue(elemTypeId.Value, ref reader);
        }

        void SkipValue(int typeId, ref SequenceReader<byte> reader)
        {
            if (_oldById.TryGetValue(typeId, out var s) && s.FixedByteSize > 0) { reader.Advance(s.FixedByteSize); return; }
            int fixedSize = WellKnownTypes.GetFixedSize(typeId);
            if (fixedSize > 0) { reader.Advance(fixedSize); return; }

            switch (typeId)
            {
                case WellKnownTypes.Bool:
                case WellKnownTypes.Int32: SerializerBinary.ReadInt32(ref reader); return;
                case WellKnownTypes.UInt32: SerializerBinary.ReadUInt32(ref reader); return;
                case WellKnownTypes.Object: SkipPolymorphicValue(WellKnownTypes.Object, ref reader); return;
                default:
                    if (typeId >= WellKnownTypes.UserTypeBase) SkipUserType(typeId, ref reader);
                    return;
            }
        }

        void SkipUserType(int typeId, ref SequenceReader<byte> reader)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema)) return;
            if (oldSchema.FixedByteSize > 0) { reader.Advance(oldSchema.FixedByteSize); return; }

            switch (oldSchema.Kind)
            {
                case SchemaTypeKind.Struct: foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, ref reader); break;
                case SchemaTypeKind.Class: SkipPolymorphicValue(typeId, ref reader); break;
                case SchemaTypeKind.Enum: SkipEnumValue(oldSchema, ref reader); break;
                case SchemaTypeKind.GenericInstantiation:
                    if (oldSchema.DefinitionTypeId == WellKnownTypes.Array || oldSchema.DefinitionTypeId == WellKnownTypes.List || 
                        oldSchema.DefinitionTypeId == WellKnownTypes.HashSet || oldSchema.DefinitionTypeId == WellKnownTypes.Queue || 
                        oldSchema.DefinitionTypeId == WellKnownTypes.Stack)
                    {
                        uint count = SerializerBinary.ReadUInt32(ref reader);
                        int? elemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        SkipElements(count, elemTypeId, ref reader);
                    }
                    else if (oldSchema.DefinitionTypeId == WellKnownTypes.Dictionary)
                    {
                        uint count = SerializerBinary.ReadUInt32(ref reader);
                        int? kId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        int? vId = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;
                        for (int i = 0; i < count; i++) { if (kId.HasValue) SkipValue(kId.Value, ref reader); if (vId.HasValue) SkipValue(vId.Value, ref reader); }
                    }
                    break;
            }
        }

        void SkipPolymorphicValue(int expectedTypeId, ref SequenceReader<byte> reader)
        {
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, RefBias);
            if (marker == RefNull || marker >= 0) return;
            if (marker == RefExternalObject) { SerializerBinary.ReadInt32Fixed(ref reader); return; }
            if (marker == RefInlineType) { SkipValue(WellKnownTypes.Type, ref reader); return; }

            int typeId = marker == RefNewValue ? (int)SchemaSection.ReadLeb128(ref reader) : expectedTypeId;
            if (_oldById.TryGetValue(typeId, out var oldSchema))
            {
                _objectCache.Add(null);
                if (oldSchema.Kind == SchemaTypeKind.Enum) SkipEnumValue(oldSchema, ref reader);
                else if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
                {
                    uint count = SerializerBinary.ReadUInt32(ref reader);
                    if (oldSchema.DefinitionTypeId == WellKnownTypes.Dictionary)
                    {
                        int? kId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        int? vId = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;
                        for (int i = 0; i < count; i++) { if (kId.HasValue) SkipValue(kId.Value, ref reader); if (vId.HasValue) SkipValue(vId.Value, ref reader); }
                    }
                    else
                    {
                        int? elemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        SkipElements(count, elemTypeId, ref reader);
                    }
                }
                else foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, ref reader);
            }
        }

        int GetOldTypeId(Type type)
        {
            if (type == null) return WellKnownTypes.Null;
            if (WellKnownTypes.TryGetId(type, out int id)) return id;
            if (_registry.TryGetInfo(type, out var info))
            {
                if (_oldByName.TryGetValue(info.StableName, out var s)) return s.TypeId;
                foreach (var prev in info.PreviousTypeNames) if (_oldByName.TryGetValue(prev, out s)) return s.TypeId;
            }
            return WellKnownTypes.Null;
        }

        Type GetCurrentType(string stableName) { _registry.TryResolveType(stableName, out var t); return t; }

        object ReadEnumValue(OldTypeSchema oldSchema, TypeMigrationPlan plan, ref SequenceReader<byte> reader)
        {
            int underlyingId = oldSchema.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            object rawValue = ReadValue(underlyingId, null, ref reader);
            if (plan?.EnumValueMap != null && plan.CurrentType != null)
            {
                long val = Convert.ToInt64(rawValue);
                return plan.EnumValueMap.TryGetValue(val, out long mapped) ? Enum.ToObject(plan.CurrentType, mapped) : Enum.ToObject(plan.CurrentType, 0L);
            }
            return rawValue;
        }

        void SkipEnumValue(OldTypeSchema oldSchema, ref SequenceReader<byte> reader)
        {
            int underlying = oldSchema.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            SkipValue(underlying, ref reader);
        }

        void ReadAndApplyFields(OldTypeSchema oldSchema, TypeMigrationPlan plan, object target, ref SequenceReader<byte> reader)
        {
            var mappings = plan?.Mappings ?? BuildSkipMappings(oldSchema);
            foreach (var mapping in mappings)
            {
                if (mapping.NewField == null) SkipValue(mapping.OldField.TypeId, ref reader);
                else
                {
                    object value = ReadValue(mapping.OldField.TypeId, mapping.NewField.FieldType, ref reader);
                    if (target != null) mapping.NewField.SetValue(target, Coerce(value, mapping.Coercion, mapping.NewField.FieldType));
                }
            }
        }

        static FieldMapping[] BuildSkipMappings(OldTypeSchema old)
        {
            var m = new FieldMapping[old.Fields.Length];
            for (int i = 0; i < old.Fields.Length; i++) m[i] = new FieldMapping { OldField = old.Fields[i] };
            return m;
        }

        static object Coerce(object value, CoercionKind kind, Type targetType)
        {
            if (kind == CoercionKind.None) return value;
            return kind switch
            {
                CoercionKind.Int32ToInt64 => value is int i ? (object)(long)i : value,
                CoercionKind.Int16ToInt32 => value is short s ? (object)(int)s : value,
                CoercionKind.Int32ToSingle => value is int i2 ? (object)(float)i2 : value,
                CoercionKind.Int32ToDouble => value is int i3 ? (object)(double)i3 : value,
                CoercionKind.SingleToDouble => value is float f ? (object)(double)f : value,
                _ => value,
            };
        }

        string ReadStringFromTable(int index) => (index == 0) ? null : ((uint)index < (uint)_stringTable.Length ? _stringTable[index] : null);

        static HashSet<string> CollectSkippedFields(TypeMigrationPlan plan)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in plan.Mappings) if (m.NewField == null && m.OldField != null) set.Add(m.OldField.StableName);
            return set;
        }

        void FireCallback(object obj, TypeMigrationPlan plan, string[] addedFields, HashSet<string> skippedFields)
        {
            if (plan.OnAfterDeserialize == null) return;
            var ctx = new MigrationContext(true, _saveGameVersion, skippedFields, new HashSet<string>(addedFields ?? Array.Empty<string>(), StringComparer.Ordinal));
            plan.OnAfterDeserialize.Invoke(obj, new object[] { ctx });
        }
    }
}
