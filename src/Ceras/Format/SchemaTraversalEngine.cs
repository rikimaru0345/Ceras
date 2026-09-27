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
        const int RefBias             = Protocol.RefBias;
        const int RefNull             = Protocol.RefNull;
        const int RefNewValue         = Protocol.RefNewValue;
        const int RefNewValueSameType = Protocol.RefNewValueSameType;
        const int RefInlineType       = Protocol.RefInlineType;

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
            _registry        = ((CerasRobustConfig)serializer.Config).SchemaRegistry;
            _maxCollectionSize = serializer.Config.SizeLimits.MaxCollectionSize;

            _oldById = new Dictionary<int, OldTypeSchema>();
            foreach (var s in oldSchemas.Values)
                _oldById.TryAdd(s.TypeId, s);

            _plans = MigrationPlanBuilder.BuildAll(oldSchemas, _registry);

            // Pre-build the callback invokers. The migration context is identical for every instance
            // of a type, so it is constructed once per plan rather than once per object.
            foreach (var plan in _plans.Values)
            {
                var callbackContext = new MigrationContext(
                    true,
                    _robustVersion,
                    CollectSkippedFields(plan),
                    new HashSet<string>(plan.AddedFieldNames ?? Array.Empty<string>(), StringComparer.Ordinal));
                plan.BeforeInvoker = MakeInvoker(plan.OnBeforeDeserialize, plan.OnBeforeDeserializeArgument, callbackContext);
                plan.AfterInvoker = MakeInvoker(plan.OnAfterDeserialize, plan.OnAfterDeserializeArgument, callbackContext);
            }
        }

        static Action<CerasSerializer, object> MakeInvoker(MethodInfo method, CallbackArgumentKind kind, MigrationContext context)
        {
            if (method == null) return null;

            switch (kind)
            {
                case CallbackArgumentKind.Serializer:
                    return (ceras, target) => method.Invoke(target, new object[] { ceras });
                case CallbackArgumentKind.MigrationContext:
                    return (ceras, target) => method.Invoke(target, new object[] { context });
                default:
                    return (ceras, target) => method.Invoke(target, null);
            }
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
            // Roots are declared types, so the stored root id and this CLR type were already
            // validated to match. Dispatch on the encoding shape rather than assuming every root
            // is a user schema: well-known scalars/string/object/Type are never present in the
            // file's schema section, so looking them up by stable name would silently return
            // default (e.g. 0 for a saved int).
            if (WellKnownTypes.TryGetId(typeof(T), out int wellKnownId))
            {
                object wk = ReadValue(wellKnownId, typeof(T), ref reader, rootObject);
                return wk == null ? default : (T)wk;
            }

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
			return TypeIdentity.GetCanonicalName(t);
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
                    // Matches SerializerBinary.WriteDateTime / DateTimeFormatter: the writer persists
                    // DateTime.ToBinary() (ticks plus kind bits), so the interpreter must use FromBinary.
                    // Treating the bits as raw ticks throws for Utc/Local values and loses the kind.
                    return DateTime.FromBinary(SerializerBinary.ReadInt64Fixed(ref reader));
                case WellKnownTypes.TimeSpan:
                    return new TimeSpan(SerializerBinary.ReadInt64Fixed(ref reader));
                case WellKnownTypes.DateTimeOffset:
                    // Matches DateTimeOffsetFormatter / SerializerBinary: ticks then offset minutes.
                    return new DateTimeOffset(
                        SerializerBinary.ReadInt64Fixed(ref reader),
                        TimeSpan.FromMinutes(SerializerBinary.ReadInt16Fixed(ref reader)));
                case WellKnownTypes.Guid:
                {
                    byte[] b = new byte[16];
                    if (!reader.TryCopyTo(b))
                        throw new InvalidOperationException("End of sequence while reading Guid");
                    reader.Advance(16);
                    return new Guid(b);
                }
                case WellKnownTypes.Type:
                    return ReadTypeValue(ref reader);
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

        /// <summary>
        /// Decodes a <see cref="Type"/> value exactly like <c>RobustTypeFormatter.Deserialize</c>:
        /// a well-known definition id is followed by its ordered argument types, and an array id by its
        /// element type. Reconstructing the closed type is required both to return the right value and
        /// to consume the argument bytes so the stream stays aligned.
        /// </summary>
        object ReadTypeValue(ref SequenceReader<byte> reader)
        {
            uint id = SchemaSection.ReadLeb128(ref reader);
            if (id == WellKnownTypes.Null) return null;

            if (id == FullNameMarker)
            {
                string typeName = ReadStringFromTable(ref reader);
                return _typeBinder.GetTypeFromBase(typeName);
            }

            if (id == (uint)WellKnownTypes.Array)
            {
                Type element = (Type)ReadTypeValue(ref reader);
                if (element == null) throw new InvalidOperationException("Array element type is null");
                return element.MakeArrayType();
            }

            if (WellKnownTypes.TryGetType((int)id, out var wellKnown))
            {
                if (wellKnown.IsGenericTypeDefinition)
                {
                    uint argCount = SchemaSection.ReadLeb128(ref reader);
                    var args = new Type[argCount];
                    for (uint i = 0; i < argCount; i++)
                    {
                        args[i] = (Type)ReadTypeValue(ref reader);
                        if (args[i] == null) throw new InvalidOperationException($"Generic argument {i} type is null");
                    }
                    return wellKnown.MakeGenericType(args);
                }
                return wellKnown;
            }

            var idToType = RobustContext.IdToType;
            if (idToType != null && idToType.TryGetValue((int)id, out var resolved)) return resolved;

            return null;
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

            // Reuse is only valid for an exact runtime-type match; a subtype/supertype mismatch must
            // allocate a new instance (matching the generated reference wrapper).
            if (existingObject != null && !IsExactType(existingObject, currentType))
                existingObject = null;

            if (oldSchema.Kind == SchemaTypeKind.Struct || (currentType != null && currentType.IsValueType))
            {
                object structObj = existingObject ?? CreateInstance(currentType);
                if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = structObj;

                FireBeforeCallback(structObj, plan);

                if (TryReadCustomFormatter(currentType, ref structObj, ref reader))
                {
                    FireAfterCallbackInline(structObj, plan);
                    return structObj;
                }

                ReadAndApplyFields(oldSchema, plan, structObj, ref reader);
                FireAfterCallbackInline(structObj, plan);
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

            FireBeforeCallback(obj, plan);

            if (TryReadCustomFormatter(currentType, ref obj, ref reader))
            {
                EnqueueAfterCallback(obj, plan);
                return obj;
            }

            ReadAndApplyFields(oldSchema, plan, obj, ref reader);

            EnqueueAfterCallback(obj, plan);
            return obj;
        }


        static bool IsExactType(object obj, Type type) => type != null && obj.GetType() == type;

        /// <summary>
        /// Reuses <paramref name="existingObject"/> in place when it is the exact target type and has a
        /// usable clear method; otherwise constructs a fresh collection. Never silently appends to a
        /// collection that cannot be cleared.
        /// </summary>
        object ReuseCollection(object existingObject, Type targetType, string clearMethodName, string kind)
        {
            if (existingObject != null && IsExactType(existingObject, targetType))
            {
                var clear = targetType.GetMethod(clearMethodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (clear != null)
                {
                    clear.Invoke(existingObject, null);
                    return existingObject;
                }
            }

            try
            {
                return Activator.CreateInstance(targetType);
            }
            catch (Exception e)
            {
                throw new Ceras.Exceptions.CerasException(
                    $"The {kind} collection type '{targetType?.FullName}' has no usable '{clearMethodName}' method and could not be constructed fresh; " +
                    "Ceras cannot reuse it in place.", e);
            }
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
            if (oldSchema.DefinitionTypeId == WellKnownTypes.KeyValuePair) return ReadKeyValuePair(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Nullable) return ReadNullable(oldSchema, targetType, ref reader);
            if (WellKnownTypes.IsTupleDefinition(oldSchema.DefinitionTypeId)) return ReadTuple(oldSchema, targetType, ref reader, cacheIndex, existingObject);

            // Arbitrary generic class or struct
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type currentType = plan?.CurrentType ?? targetType;

            // A concrete collection shape (built-in collection subclass, or an interface/abstract
            // collection slot bound to a concrete runtime type) is written by the built-in collection
            // formatter as an explicit count plus elements. Read it through the matching reader using the
            // element/key/value identities from this type's own generic arguments, never through the
            // ordinary user-type path (which would consume a reference marker that was never written).
            if (IsCollectionShape(currentType))
                return ReadCollectionShape(currentType, targetType, oldSchema, ref reader, cacheIndex, existingObject);

            if (existingObject != null && !IsExactType(existingObject, currentType))
                existingObject = null;

            if (currentType != null && currentType.IsValueType)
            {
                object structObj = existingObject ?? CreateInstance(currentType);
                if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = structObj;

                FireBeforeCallback(structObj, plan);

                if (TryReadCustomFormatter(currentType, ref structObj, ref reader))
                {
                    FireAfterCallbackInline(structObj, plan);
                    return structObj;
                }

                ReadAndApplyFields(oldSchema, plan, structObj, ref reader);
                FireAfterCallbackInline(structObj, plan);
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

            FireBeforeCallback(obj, plan);

            if (TryReadCustomFormatter(currentType, ref obj, ref reader))
            {
                EnqueueAfterCallback(obj, plan);
                return obj;
            }

            ReadAndApplyFields(oldSchema, plan, obj, ref reader);

            EnqueueAfterCallback(obj, plan);
            return obj;
        }

        /// <summary>
        /// <see cref="Nullable{T}"/> layout: one presence byte, then the element value when present
        /// (matching <c>NullableFormatter&lt;T&gt;</c>). Returns the boxed element value, or null.
        /// </summary>
        object ReadNullable(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader)
        {
            int hasValue = SerializerBinary.ReadByte(ref reader);
            int? innerId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;

            if (hasValue == 0)
                return null;

            Type innerType = targetType == null ? null : Nullable.GetUnderlyingType(targetType);
            if (innerType == null || innerId == null)
            {
                // No current element representation: still consume the element to keep alignment.
                if (innerId.HasValue) SkipValue(innerId.Value, ref reader);
                return null;
            }

            return ReadValue(innerId.Value, innerType, ref reader);
        }

        object ReadKeyValuePair(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            Type keyType = targetType != null && targetType.IsGenericType ? targetType.GetGenericArguments()[0] : null;
            Type valType = targetType != null && targetType.IsGenericType ? targetType.GetGenericArguments()[1] : null;
            int? oldKeyTid = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            int? oldValTid = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;

            if (keyType == null || valType == null || oldKeyTid == null || oldValTid == null)
            {
                SkipValue(oldKeyTid ?? WellKnownTypes.Object, ref reader);
                SkipValue(oldValTid ?? WellKnownTypes.Object, ref reader);
                return null;
            }

            var key = ReadValue(oldKeyTid.Value, keyType, ref reader);
            var val = ReadValue(oldValTid.Value, valType, ref reader);
            object kvp = Activator.CreateInstance(targetType, key, val);
            // KeyValuePair is a value type written with no reference marker and is never registered by
            // the writer, so only fill a slot the writer actually created.
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = kvp;
            return kvp;
        }

        /// <summary>
        /// <c>Tuple</c>/<c>ValueTuple</c> layout: each item in order, written by <c>TupleFormatter</c> /
        /// <c>ValueTupleFormatter</c> with no count/length prefix. The ordered arguments of the old
        /// schema identify each item's type; the current target type (when present) supplies the CLR
        /// representation. A tuple is never registered in the object cache by the writer, so no slot is
        /// created here (TODO 0.5 item 3).
        /// </summary>
        object ReadTuple(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = oldSchema.ArgTypeIds.Length;
            var argTypes = targetType != null && targetType.IsGenericType ? targetType.GetGenericArguments() : null;

            var values = new object[count];
            for (int i = 0; i < count; i++)
            {
                int oldItemId = oldSchema.ArgTypeIds[i];
                Type itemType = argTypes != null && i < argTypes.Length ? argTypes[i] : null;
                values[i] = ReadValue(oldItemId, itemType, ref reader);
            }

            if (targetType == null || argTypes == null)
                return null;

            try
            {
                return Activator.CreateInstance(targetType, values);
            }
            catch (Exception)
            {
                // A tuple whose shape no longer matches (arity change) cannot be reconstructed; the items
                // were still consumed, so the stream stays aligned.
                return null;
            }
        }

        object ReadArray(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            // Multidimensional arrays are written by MultiDimensionalArrayFormatter (rank + per-dimension
            // lengths + flattened elements), which this structural reader does not decode. Reject them
            // loudly rather than misreading the payload as a single length (TODO 0.5).
            if (targetType != null && targetType.IsArray && targetType.GetArrayRank() != 1)
                throw SchemaRegistry.UnsupportedMultidimensionalArray(targetType, "the migration interpreter");

            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize) throw new InvalidOperationException("Max size exceeded");

            Type elementType = targetType?.IsArray == true ? targetType.GetElementType() : null;
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements((uint)count, oldElemTypeId, ref reader); return null; }

            var arr = existingObject as Array;
            if (arr == null || arr.Length != count || (targetType != null && arr.GetType() != targetType))
                arr = Array.CreateInstance(elementType, (int)count);

            // Only fill an object-cache slot that the writer actually created. Monomorphic array
            // members are written by ArrayFormatter with no reference marker and are never
            // registered by the writer, so adding a slot here would shift every later back-reference.
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = arr;

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

            var list = existingObject as System.Collections.IList;
            if (list != null && targetType != null && existingObject.GetType() != targetType)
                list = null;
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

            var dict = existingObject as System.Collections.IDictionary;
            if (dict != null && targetType != null && existingObject.GetType() != targetType)
                dict = null;
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

            var set = ReuseCollection(existingObject, targetType, "Clear", "set");

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

            var queue = ReuseCollection(existingObject, targetType, "Clear", "queue");

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

            var stack = ReuseCollection(existingObject, targetType, "Clear", "stack");

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

        /// <summary>
        /// True when <paramref name="type"/> is a concrete collection written by the built-in collection
        /// formatter (a built-in collection, a collection subclass, or a Queue/Stack), i.e. it should be
        /// read through <see cref="ReadCollectionShape"/> rather than as an ordinary field-based object.
        /// </summary>
        static bool IsCollectionShape(Type type)
            => type != null && !type.IsArray && WellKnownTypes.IsCollectionFormatterShape(type);

        /// <summary>
        /// Reads a collection shape (its actual payload is a count plus elements) using the current
        /// type's own generic arguments to identify the element/key/value types, ignoring whatever
        /// definition id the file recorded (so a rename or an ID/formatter change across contexts still
        /// decodes by shape). Falls back to consuming-and-discarding when no current element type can be
        /// inferred.
        /// </summary>
        object ReadCollectionShape(Type currentType, Type targetType, OldTypeSchema oldSchema, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            if (!WellKnownTypes.TryResolveCollectionShape(currentType, out var shapeDef, out var shapeArgs))
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot decode collection type '{currentType?.FullName}': Ceras could not infer its element/key/value type.");

            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count > _maxCollectionSize)
                throw new Ceras.Exceptions.CerasSchemaException("Max collection size exceeded while decoding a save file.");

            int? oldElem0 = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            int? oldElem1 = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;

            var def = shapeDef.IsGenericTypeDefinition ? shapeDef : shapeDef.GetGenericTypeDefinition();

            if (def == typeof(Dictionary<,>))
                return ReadDictionaryShape(currentType, shapeArgs[0], shapeArgs[1], oldElem0, oldElem1, count, ref reader, cacheIndex, existingObject);
            if (def == typeof(Queue<>))
                return ReadQueueShape(currentType, shapeArgs[0], oldElem0, count, ref reader, cacheIndex, existingObject);
            if (def == typeof(Stack<>))
                return ReadStackShape(currentType, shapeArgs[0], oldElem0, count, ref reader, cacheIndex, existingObject);
            if (def == typeof(HashSet<>))
                return ReadHashSetShape(currentType, shapeArgs[0], oldElem0, count, ref reader, cacheIndex, existingObject);

            // List<>/ICollection<> and everything else collapse to element-ordered refill.
            return ReadListShape(currentType, shapeArgs[0], oldElem0, count, ref reader, cacheIndex, existingObject);
        }

        object ReadListShape(Type currentType, Type elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var list = existingObject as System.Collections.IList;
            if (list != null && currentType != null && existingObject.GetType() != currentType)
                list = null;
            if (list == null)
                list = (System.Collections.IList)Activator.CreateInstance(currentType);
            else
                list.Clear();

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = list;

            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue
                    ? ReadValue(oldElemId.Value, elementType, ref reader)
                    : SkipAndReturnDefault(elementType, ref reader);
                list.Add(Coerce(val, coerce, elementType));
            }
            return list;
        }

        object ReadDictionaryShape(Type currentType, Type keyType, Type valType, int? oldKeyId, int? oldValId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var dict = existingObject as System.Collections.IDictionary;
            if (dict != null && currentType != null && existingObject.GetType() != currentType)
                dict = null;
            if (dict == null)
                dict = (System.Collections.IDictionary)Activator.CreateInstance(currentType);
            else
                dict.Clear();

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = dict;

            var kCoerce = oldKeyId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldKeyId.Value, keyType) : CoercionKind.None;
            var vCoerce = oldValId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldValId.Value, valType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var k = oldKeyId.HasValue ? ReadValue(oldKeyId.Value, keyType, ref reader) : SkipAndReturnDefault(keyType, ref reader);
                var v = oldValId.HasValue ? ReadValue(oldValId.Value, valType, ref reader) : SkipAndReturnDefault(valType, ref reader);
                dict.Add(Coerce(k, kCoerce, keyType), Coerce(v, vCoerce, valType));
            }
            return dict;
        }

        object ReadHashSetShape(Type currentType, Type elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var set = ReuseCollection(existingObject, currentType, "Clear", "set");
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = set;

            var addMethod = currentType.GetMethod("Add", new[] { elementType });
            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                addMethod.Invoke(set, new[] { Coerce(val, coerce, elementType) });
            }
            return set;
        }

        object ReadQueueShape(Type currentType, Type elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var queue = ReuseCollection(existingObject, currentType, "Clear", "queue");
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = queue;

            var enqueueMethod = currentType.GetMethod("Enqueue", new[] { elementType });
            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                enqueueMethod.Invoke(queue, new[] { Coerce(val, coerce, elementType) });
            }
            return queue;
        }

        object ReadStackShape(Type currentType, Type elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var values = new object[count];
            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                values[i] = Coerce(val, coerce, elementType);
            }

            var stack = ReuseCollection(existingObject, currentType, "Clear", "stack");
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = stack;

            var pushMethod = currentType.GetMethod("Push", new[] { elementType });
            for (int i = count - 1; i >= 0; i--) pushMethod.Invoke(stack, new[] { values[i] });
            return stack;
        }

        /// <summary>
        /// No old element identity was recorded: skip a fixed-size element if we can, otherwise fail
        /// loudly rather than desyncing the stream.
        /// </summary>
        object SkipAndReturnDefault(Type elementType, ref SequenceReader<byte> reader)
        {
            throw new Ceras.Exceptions.CerasSchemaException(
                $"Cannot decode a collection of '{elementType?.FullName}': the save file did not record the element type identity.");
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
                case WellKnownTypes.Type: SkipTypeValue(ref reader); return;
                default:
                    if (typeId >= WellKnownTypes.UserTypeBase) SkipUserType(typeId, ref reader);
                    return;
            }
        }

        /// <summary>Skips a <see cref="Type"/> value written by <c>RobustTypeFormatter</c>.</summary>
        void SkipTypeValue(ref SequenceReader<byte> reader)
        {
            uint id = SchemaSection.ReadLeb128(ref reader);
            if (id == WellKnownTypes.Null) return;

            if (id == FullNameMarker)
            {
                ReadStringFromTable(ref reader);
                return;
            }

            if (id == (uint)WellKnownTypes.Array)
            {
                SkipTypeValue(ref reader);
                return;
            }

            if (WellKnownTypes.TryGetType((int)id, out var wellKnown) && wellKnown.IsGenericTypeDefinition)
            {
                uint argCount = SchemaSection.ReadLeb128(ref reader);
                for (uint i = 0; i < argCount; i++)
                    SkipTypeValue(ref reader);
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
                    if (oldSchema.DefinitionTypeId == WellKnownTypes.Nullable)
                    {
                        if (SerializerBinary.ReadByte(ref reader) != 0)
                        {
                            int? innerId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                            if (innerId.HasValue) SkipValue(innerId.Value, ref reader);
                        }
                        break;
                    }

                    if (WellKnownTypes.IsTupleDefinition(oldSchema.DefinitionTypeId))
                    {
                        foreach (var argId in oldSchema.ArgTypeIds) SkipValue(argId, ref reader);
                        break;
                    }

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
                    if (oldSchema.DefinitionTypeId == WellKnownTypes.Nullable)
                    {
                        if (SerializerBinary.ReadByte(ref reader) != 0)
                        {
                            int? innerId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
                            if (innerId.HasValue) SkipValue(innerId.Value, ref reader);
                        }
                        return;
                    }

                    if (WellKnownTypes.IsTupleDefinition(oldSchema.DefinitionTypeId))
                    {
                        foreach (var argId in oldSchema.ArgTypeIds) SkipValue(argId, ref reader);
                        return;
                    }

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
                    // Seed a reference-typed member from its current value so Load(ref existing) reuses
                    // it in place, exactly like the generated reader does. Value types always overwrite.
                    object existing = null;
                    if (target != null && !mapping.NewField.FieldType.IsValueType)
                        existing = mapping.NewField.GetValue(target);

                    // An array-typed member is written by ArrayFormatter with no reference marker (the
                    // declared type is exactly an array), so it must be read through the structural
                    // array reader instead of the polymorphic path.
                    object value;
                    if (mapping.NewField.FieldType.IsArray &&
                        _oldById.TryGetValue(mapping.OldField.TypeId, out var fieldSchema) &&
                        fieldSchema.Kind == SchemaTypeKind.GenericInstantiation &&
                        fieldSchema.DefinitionTypeId == WellKnownTypes.Array)
                    {
                        value = ReadUserTypeDirect(mapping.OldField.TypeId, mapping.NewField.FieldType, ref reader, null, existing);
                    }
                    else
                    {
                        value = ReadValue(mapping.OldField.TypeId, mapping.NewField.FieldType, ref reader, existing);
                    }

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
            if (plan.Mappings == null) return set;
            foreach (var m in plan.Mappings) if (m.NewField == null && m.OldField != null) set.Add(m.OldField.StableName);
            return set;
        }

        void FireBeforeCallback(object obj, TypeMigrationPlan plan)
        {
            if (obj == null || plan?.BeforeInvoker == null) return;
            plan.BeforeInvoker(_serializer, obj);
        }

        /// <summary>
        /// Defers the post-deserialization callback until the whole root graph is populated. Used for
        /// reference types; value types invoke it inline because a boxed struct copy cannot be deferred.
        /// </summary>
        void EnqueueAfterCallback(object obj, TypeMigrationPlan plan)
        {
            if (obj == null || plan?.AfterInvoker == null) return;
            _serializer.EnqueueDeserializeCallback(obj, plan.AfterInvoker);
        }

        void FireAfterCallbackInline(object obj, TypeMigrationPlan plan)
        {
            if (obj == null || plan?.AfterInvoker == null) return;
            plan.AfterInvoker(_serializer, obj);
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
