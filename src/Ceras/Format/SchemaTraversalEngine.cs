using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ceras;
using Ceras.Helpers;

namespace Ceras.Format
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
        readonly uint                                  _robustVersion;
        readonly CerasSerializer                       _serializer;
        readonly SchemaRegistry                        _registry;
        readonly ITypeBinder                           _typeBinder = new SimpleTypeBinder();
        readonly List<object>                          _objectCache = new();
        readonly uint                                  _maxCollectionSize;

        const int FullNameMarker = 127;

        public SchemaTraversalEngine(
            Dictionary<string, OldTypeSchema> oldSchemas,
            string[]                          stringTable,
            uint                              robustVersion,
            CerasSerializer                   serializer)
        {
            _oldByName       = oldSchemas;
            _stringTable     = stringTable;
            _robustVersion   = robustVersion;
            _serializer      = serializer;
            _registry        = serializer.Config.Robust.SchemaRegistry;
            _maxCollectionSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;

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

        public T ReadRoot<T>(ref SequenceReader<byte> reader, T rootObject = default)
        {
            string stableName = ResolveOldStableName(typeof(T));
            object obj = ReadUserTypeByStableName(stableName, typeof(T), ref reader, rootObject);
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
			return t.FullName ?? t.Name;
		}

        // ── Core dispatch ─────────────────────────────────────────────────────

        object ReadValue(int typeId, Type targetType, ref SequenceReader<byte> reader, object existingObject = null)
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
                    return ReadStringFromTable(ref reader);
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
                    var idToType = RobustContext.IdToType;
                    if (idToType != null && idToType.TryGetValue((int)id, out var t)) return t;
                    return null;
                }
                case WellKnownTypes.Object:
                    return ReadPolymorphicValue(WellKnownTypes.Object, targetType, ref reader, existingObject);
                default:
                {
                    if (typeId >= WellKnownTypes.UserTypeBase)
                        return ReadUserType(typeId, targetType, ref reader, existingObject);
                    SkipValue(typeId, ref reader);
                    return null;
                }
            }
        }

        object ReadUserType(int typeId, Type targetType, ref SequenceReader<byte> reader, object existingObject = null)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema)) {
                return null;
            }
            
            if (oldSchema.Kind == SchemaTypeKind.Enum)
            {
                _plans.TryGetValue(oldSchema.StableName, out var plan);
                return ReadEnumValue(oldSchema, targetType, plan, ref reader);
            }

            // If it's a struct, it has no reference marker.
            if (oldSchema.Kind == SchemaTypeKind.Struct)
            {
                return ReadUserTypeDirect(typeId, targetType, ref reader, null, existingObject);
            }

            if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
            {
                _plans.TryGetValue(oldSchema.StableName, out var plan);
                Type currentType = plan?.CurrentType ?? targetType;

                if (currentType != null && currentType.IsValueType && currentType != typeof(object))
                    return ReadUserTypeDirect(typeId, targetType, ref reader, null, existingObject);
            }

            // If it's a class or interface, it HAS a reference marker.
            return ReadPolymorphicValue(typeId, targetType, ref reader, existingObject);
        }

        object ReadUserTypeDirect(int typeId, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            if (!_oldById.TryGetValue(typeId, out var oldSchema)) return null;

            if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
                return ReadGenericInstantiation(oldSchema, targetType, ref reader, cacheIndex, existingObject);

            return ReadUserTypeInstance(oldSchema, targetType, ref reader, cacheIndex, existingObject);
        }

        object ReadPolymorphicValue(int expectedTypeId, Type targetType, ref SequenceReader<byte> reader, object existingObject = null)
        {
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, RefBias);
            if (marker == RefNull) return null;
            if (marker >= 0) {
                return marker < _objectCache.Count ? _objectCache[marker] : null;
            }

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
            else if (marker == RefNewValueSameType)
                typeId = expectedTypeId;
            else
                throw new Ceras.Exceptions.CerasException($"Invalid reference marker {marker}");

            int cacheIndex = _objectCache.Count;
            _objectCache.Add(existingObject);

            if (typeId < WellKnownTypes.UserTypeBase && typeId != FullNameMarker)
            {
                var value = ReadValue(typeId, targetType, ref reader, existingObject);
                _objectCache[cacheIndex] = value;
                return value;
            }

            if (typeId == FullNameMarker)
            {
                string typeName = ReadStringFromTable(ref reader);
                Type type = _typeBinder.GetTypeFromBase(typeName);
                if (type == null)
                {
                    return null;
                }

                // Is it a registered type?
                if (_registry.TryGetId(type, out int actualId))
                {
                    if (_oldById.TryGetValue(actualId, out var registeredSchema))
                    {
                        var obj2 = ReadUserTypeInstance(registeredSchema, targetType, ref reader, cacheIndex, existingObject);
                        _objectCache[cacheIndex] = obj2;
                        return obj2;
                    }
                }

                // If not registered, we can still try to deserialize it if it's a simple type,
                // but for now let's just create an uninitialized object if it's a class.
                object obj = existingObject ?? RuntimeHelpers.GetUninitializedObject(type);
                _objectCache[cacheIndex] = obj;

                if (existingObject == null) {
                    var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (ctor != null)
                        ctor.Invoke(obj, null);
                }

                return obj;
            }

            return ReadUserTypeDirect(typeId, targetType, ref reader, cacheIndex, existingObject);
        }

        object ReadUserTypeByStableName(string stableName, Type targetType, ref SequenceReader<byte> reader, object existingObject = null)
        {
            if (stableName != null && _oldByName.TryGetValue(stableName, out var oldSchema))
            {
                return ReadUserType(oldSchema.TypeId, targetType, ref reader, existingObject);
            }
            return null;
        }

        object ReadUserTypeInstance(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type currentType = plan?.CurrentType ?? targetType;

            if (oldSchema.Kind == SchemaTypeKind.Struct || (currentType != null && currentType.IsValueType))
            {
                object structObj = existingObject ?? CreateInstance(currentType);
                if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = structObj;

                if (TryReadCustomFormatter(currentType, ref structObj, ref reader))
                    return structObj;

                ReadAndApplyFields(oldSchema, plan, structObj, ref reader);
                return structObj;
            }

            // Reference type: allocate uninitialized, then register in cache, then run constructor
            object obj = existingObject ?? RuntimeHelpers.GetUninitializedObject(currentType);
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = obj;

            if (existingObject == null) {
                var ctor = currentType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (ctor != null)
                    ctor.Invoke(obj, null);
            }

            if (TryReadCustomFormatter(currentType, ref obj, ref reader))
            {
                if (plan != null && plan.HasChanges && plan.OnAfterDeserialize != null && obj != null)
                    FireCallback(obj, plan, plan.AddedFieldNames, CollectSkippedFields(plan));
                return obj;
            }

            ReadAndApplyFields(oldSchema, plan, obj, ref reader);

            if (plan != null && plan.HasChanges && plan.OnAfterDeserialize != null && obj != null)
                FireCallback(obj, plan, plan.AddedFieldNames, CollectSkippedFields(plan));

            return obj;
        }


        object CreateInstance(Type type)
        {
            if (type == null) return null;
            if (type.IsValueType) return Activator.CreateInstance(type);

            var typeConfig = _serializer.Config.GetTypeConfig(type, false);
            var tc = typeConfig.TypeConstruction;
            if (tc != null && !tc.HasDataArguments && tc is not ConstructNull)
            {
                var ctor = tc.GetRefFormatterConstructor();
                if (ctor != null) return ctor();
            }

            bool allowFallback = typeConfig.AllowUninitializedObjectFallback ?? _serializer.Config.Advanced.AllowUninitializedObjectFallback;

            if (!allowFallback)
                throw new Ceras.Exceptions.CerasException($"Type '{type.FullName}' has no parameterless constructor and no construction mode is configured. Fallback to GetUninitializedObject is disabled in SerializerConfig.Advanced.AllowUninitializedObjectFallback.");

            return RuntimeHelpers.GetUninitializedObject(type);
        }

        object ReadGenericInstantiation(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Array) return ReadArray(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.List) return ReadList(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Dictionary) return ReadDictionary(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.HashSet) return ReadHashSet(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Queue) return ReadQueue(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Stack) return ReadStack(oldSchema, targetType, ref reader, cacheIndex, existingObject);

            // Arbitrary generic class or struct
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type currentType = plan?.CurrentType ?? targetType;

            if (currentType != null && currentType.IsValueType)
            {
                object structObj = existingObject ?? CreateInstance(currentType);
                if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = structObj;

                if (TryReadCustomFormatter(currentType, ref structObj, ref reader))
                    return structObj;

                ReadAndApplyFields(oldSchema, plan, structObj, ref reader);
                return structObj;
            }

            // Reference type: allocate uninitialized, then register in cache, then run constructor
            object obj = existingObject ?? RuntimeHelpers.GetUninitializedObject(currentType);
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = obj;

            if (existingObject == null) {
                var ctor = currentType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (ctor != null)
                    ctor.Invoke(obj, null);
            }

            if (TryReadCustomFormatter(currentType, ref obj, ref reader))
                return obj;

            ReadAndApplyFields(oldSchema, plan, obj, ref reader);
            return obj;
        }

        object ReadArray(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize) throw new InvalidOperationException("Max size exceeded");

            Type elementType = targetType?.IsArray == true ? targetType.GetElementType() : null;
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements((uint)count, oldElemTypeId, ref reader); return null; }

            var arr = (Array)existingObject;
            if (arr == null || arr.Length != count)
                arr = Array.CreateInstance(elementType, (int)count);

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = arr;
            else _objectCache.Add(arr);

            var coerce = MigrationPlanBuilder.DetectCoercion(oldElemTypeId.Value, elementType);

			for (int i = 0; i < count; i++)
			{
                var val = ReadValue(oldElemTypeId.Value, elementType, ref reader);
                arr.SetValue(Coerce(val, coerce, elementType), i);
            }
            return arr;
        }

        object ReadList(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize) throw new InvalidOperationException("Max size exceeded");

            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements((uint)count, oldElemTypeId, ref reader); return null; }

            var list = (System.Collections.IList)existingObject;
            if (list == null)
                list = (System.Collections.IList)Activator.CreateInstance(targetType);
            else
                list.Clear();

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = list;
            else _objectCache.Add(list);

            var coerce = MigrationPlanBuilder.DetectCoercion(oldElemTypeId.Value, elementType);

			for (int i = 0; i < count; i++)
			{
                var val = ReadValue(oldElemTypeId.Value, elementType, ref reader);
                list.Add(Coerce(val, coerce, elementType));
            }
            return list;
        }

        object ReadDictionary(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize) throw new InvalidOperationException("Max size exceeded");

            Type keyType = targetType?.FindClosedArg(typeof(IDictionary<,>), 0);
            Type valType = targetType?.FindClosedArg(typeof(IDictionary<,>), 1);
            int? oldKeyTid = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            int? oldValTid = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;

            if (keyType == null || valType == null || oldKeyTid == null || oldValTid == null)
            {
                for (int i = 0; i < count; i++) { SkipValue(oldKeyTid ?? WellKnownTypes.Object, ref reader); SkipValue(oldValTid ?? WellKnownTypes.Object, ref reader); }
                return null;
            }

            var dict = (System.Collections.IDictionary)existingObject;
            if (dict == null)
                dict = (System.Collections.IDictionary)Activator.CreateInstance(targetType);
            else
                dict.Clear();

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = dict;
            else _objectCache.Add(dict);

            var kCoerce = MigrationPlanBuilder.DetectCoercion(oldKeyTid.Value, keyType);
            var vCoerce = MigrationPlanBuilder.DetectCoercion(oldValTid.Value, valType);

			for (int i = 0; i < count; i++)
			{
                var k = ReadValue(oldKeyTid.Value, keyType, ref reader);
                var v = ReadValue(oldValTid.Value, valType, ref reader);
                dict.Add(Coerce(k, kCoerce, keyType), Coerce(v, vCoerce, valType));
            }
            return dict;
        }

        object ReadHashSet(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize) throw new InvalidOperationException("Max size exceeded");

            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements((uint)count, oldElemTypeId, ref reader); return null; }

            var set = existingObject;
            if (set == null)
                set = Activator.CreateInstance(targetType);
            else
                targetType.GetMethod("Clear").Invoke(set, null);

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = set;
            else _objectCache.Add(set);

            var addMethod = targetType.GetMethod("Add", new[] { elementType });
            var coerce = MigrationPlanBuilder.DetectCoercion(oldElemTypeId.Value, elementType);

			for (int i = 0; i < count; i++)
			{
                var val = ReadValue(oldElemTypeId.Value, elementType, ref reader);
                addMethod.Invoke(set, new[] { Coerce(val, coerce, elementType) });
            }
            return set;
        }

        object ReadQueue(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize) throw new InvalidOperationException("Max size exceeded");

            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements((uint)count, oldElemTypeId, ref reader); return null; }

            var queue = existingObject;
            if (queue == null)
                queue = Activator.CreateInstance(targetType);
            else
                targetType.GetMethod("Clear").Invoke(queue, null);

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = queue;
            else _objectCache.Add(queue);

            var enqueueMethod = targetType.GetMethod("Enqueue", new[] { elementType });
            var coerce = MigrationPlanBuilder.DetectCoercion(oldElemTypeId.Value, elementType);

			for (int i = 0; i < count; i++)
			{
                var val = ReadValue(oldElemTypeId.Value, elementType, ref reader);
                enqueueMethod.Invoke(queue, new[] { Coerce(val, coerce, elementType) });
            }
            return queue;
        }

        object ReadStack(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize) throw new InvalidOperationException("Max size exceeded");

            Type elementType = targetType?.FindClosedArg(typeof(IEnumerable<>));
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements((uint)count, oldElemTypeId, ref reader); return null; }

            var values = new object[count];
            var coerce = MigrationPlanBuilder.DetectCoercion(oldElemTypeId.Value, elementType);

			for (int i = 0; i < count; i++)
			{
                var val = ReadValue(oldElemTypeId.Value, elementType, ref reader);
                values[i] = Coerce(val, coerce, elementType);
            }

            var stack = existingObject;
            if (stack == null)
                stack = Activator.CreateInstance(targetType);
            else
                targetType.GetMethod("Clear").Invoke(stack, null);

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = stack;
            else _objectCache.Add(stack);

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
                case WellKnownTypes.Bool: reader.Advance(1); return;
                case WellKnownTypes.String: SerializerBinary.ReadUInt32(ref reader); return;
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
                    _plans.TryGetValue(oldSchema.StableName, out var plan);
                    if (plan?.CurrentType != null && plan.CurrentType.IsValueType)
                    {
                        if (oldSchema.DefinitionTypeId == WellKnownTypes.Dictionary)
                        {
                            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
                            if (count == -1) return;
                            int? kId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                            int? vId = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;
                            for (int i = 0; i < count; i++) { if (kId.HasValue) SkipValue(kId.Value, ref reader); if (vId.HasValue) SkipValue(vId.Value, ref reader); }
                        }
                        else if (oldSchema.DefinitionTypeId != 0)
                        {
                            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
                            if (count == -1) return;
                            int? elemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                            SkipElements((uint)count, elemTypeId, ref reader);
                        }
                        else
                        {
                            foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, ref reader);
                        }
                    }
                    else
                    {
                        SkipPolymorphicValue(typeId, ref reader);
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
            _objectCache.Add(null); // Keep cache in sync for new value

            if (typeId < WellKnownTypes.UserTypeBase && typeId != FullNameMarker)
            {
                SkipValue(typeId, ref reader);
                return;
            }

            if (typeId == FullNameMarker)
            {
                ReadStringFromTable(ref reader); // skip name
                // What follows? If it's a polymorphic object, we don't know how to skip it without a schema!
                // But FullNameMarker is only used for non-robust types.
                // For now we just hope it has no fields or we'll get out of sync.
                return;
            }

            if (_oldById.TryGetValue(typeId, out var oldSchema))
            {
                if (oldSchema.Kind == SchemaTypeKind.Enum) SkipEnumValue(oldSchema, ref reader);
                else if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
                {
                    int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
                    if (count == -1) return;
                    if (oldSchema.DefinitionTypeId == WellKnownTypes.Dictionary)
                    {
                        int? kId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        int? vId = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;
                        for (int i = 0; i < count; i++) { if (kId.HasValue) SkipValue(kId.Value, ref reader); if (vId.HasValue) SkipValue(vId.Value, ref reader); }
                    }
                    else
                    {
                        int? elemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                        SkipElements((uint)count, elemTypeId, ref reader);
                    }
                }
                else foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, ref reader);
            }
        }

		Type GetCurrentType(string stableName) { _registry.TryResolveType(stableName, out var t); return t; }

        object ReadEnumValue(OldTypeSchema oldSchema, Type targetType, TypeMigrationPlan plan, ref SequenceReader<byte> reader)
        {
            int underlyingId = oldSchema.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            object rawValue = ReadValue(underlyingId, null, ref reader);
            
            if (plan?.EnumValueMap != null && plan.CurrentType != null)
            {
                long val = Convert.ToInt64(rawValue);
                if (plan.EnumValueMap.TryGetValue(val, out long mapped))
                {
                    return Enum.ToObject(plan.CurrentType, mapped);
                }
            }

            if (targetType != null && targetType.IsEnum)
            {
                var result = Enum.ToObject(targetType, rawValue);
                return result;
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
                if (mapping.NewField == null) {
                    SkipValue(mapping.OldField.TypeId, ref reader);
                }
                else
                {
                    object value = ReadValue(mapping.OldField.TypeId, mapping.NewField.FieldType, ref reader);
                    if (target != null) {
                        var coerced = Coerce(value, mapping.Coercion, mapping.NewField.FieldType);
                        mapping.NewField.SetValue(target, coerced);
                    }
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

        string ReadStringFromTable(ref SequenceReader<byte> reader) => StringTable.ReadString(ref reader, _stringTable);

        static HashSet<string> CollectSkippedFields(TypeMigrationPlan plan)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in plan.Mappings) if (m.NewField == null && m.OldField != null) set.Add(m.OldField.StableName);
            return set;
        }

        void FireCallback(object obj, TypeMigrationPlan plan, string[] addedFields, HashSet<string> skippedFields)
        {
            if (plan.OnAfterDeserialize == null) return;
            var ctx = new MigrationContext(true, _robustVersion, skippedFields, new HashSet<string>(addedFields ?? Array.Empty<string>(), StringComparer.Ordinal));
            plan.OnAfterDeserialize.Invoke(obj, new object[] { ctx });
        }

        bool TryReadCustomFormatter(Type type, ref object obj, ref SequenceReader<byte> reader)
        {
            if (type == null) return false;
            var config = _serializer.Config.GetTypeConfig(type, false);
            if (config == null) return false;

            var formatter = config.CustomFormatter;
            if (formatter == null) return false;

            // Robust formatters are already handled by ReadValue
            if (formatter is RobustStringFormatter || formatter is RobustTypeFormatter) return false;

            // Exclude source-generated formatters, as they rely on the normal object cache and 
            // format expectations that differ from the robust schema traversal stream structure.
            if (formatter.GetType().Namespace == "Ceras.Generated") return false;

            // Use the "Box trick" to call DeserializeUntyped.
            // This works for any formatter that follows the DeserializeUntyped pattern
            // (including FormatterBase<T> and our manual ones).
            var boxType = typeof(StrongBox<>).MakeGenericType(type);
            var box = Activator.CreateInstance(boxType, obj);
            var field = boxType.GetField("Value");

            formatter.DeserializeUntyped(ref reader, box, field);

            obj = field.GetValue(box);
            return true;
        }
    }
}
