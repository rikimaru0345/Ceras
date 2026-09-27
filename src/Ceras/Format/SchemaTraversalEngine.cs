using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
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
        readonly ITypeBinder                           _typeBinder;
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

            // 0.8.6: this used to be a fresh SimpleTypeBinder while CerasRobustSerializer resolves the same
            // file's type ids with Config.TypeBinder, so a configured binder was honoured by one half of the
            // interpreter and ignored by the other - the same name could resolve to two different types in
            // one Load. Take the configured one.
            _typeBinder = serializer?.Config?.TypeBinder ?? new SimpleTypeBinder();
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
                case WellKnownTypes.Int32:   return SerializerBinary.ReadInt32Fixed(ref reader);
                case WellKnownTypes.UInt32:  return SerializerBinary.ReadUInt32Fixed(ref reader);
                case WellKnownTypes.Single:  return SerializerBinary.ReadFloat32Fixed(ref reader);
                case WellKnownTypes.Int64:   return SerializerBinary.ReadInt64Fixed(ref reader);
                case WellKnownTypes.UInt64:  return (ulong)SerializerBinary.ReadInt64Fixed(ref reader);
                case WellKnownTypes.Double:  return SerializerBinary.ReadDouble64Fixed(ref reader);
                case WellKnownTypes.Decimal:
                {
                    // Route through SerializerBinary so an impossible scale/sign/reserved-bit word is
                    // reported as data corruption rather than escaping as ArgumentException.
                    return SerializerBinary.ReadDecimal(ref reader);
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
                    // Matches DateTimeOffsetFormatter / SerializerBinary: ticks then offset minutes, and
                    // validates them (an out-of-range value is corruption, not a decoder failure).
                    return SerializerBinary.ReadDateTimeOffset(ref reader);
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
                if (element == null)
                    throw new Ceras.Exceptions.CerasDataCorruptionException("Corrupt System.Type payload: an array type has no element type.");
                return MakeType(() => element.MakeArrayType(), $"array of '{TypeIdentity.GetCanonicalName(element)}'");
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
                        if (args[i] == null)
                            throw new Ceras.Exceptions.CerasDataCorruptionException(
                                $"Corrupt System.Type payload: generic argument {i} of '{wellKnown.Name}' is null.");
                    }
                    return MakeType(() => wellKnown.MakeGenericType(args), wellKnown.Name);
                }
                return wellKnown;
            }

            var idToType = RobustContext.IdToType;
            if (idToType != null && idToType.TryGetValue((int)id, out var resolved)) return resolved;

            return null;
        }

        /// <summary>
        /// Rebuilds a CLR type from decoded identities, reporting an impossible combination (an unknown
        /// generic argument, a violated constraint) as data corruption rather than letting the BCL's
        /// <see cref="ArgumentException"/> escape the decoder.
        /// </summary>
        static Type MakeType(Func<Type> build, string description)
        {
            try
            {
                return build();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt System.Type payload: '{description}' does not name a type this runtime can construct ({e.Message}).", e);
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
            if (marker >= 0)
            {
                // 0.8.5: an index past the end of the identity cache is a corrupt/oversized marker. It used
                // to decode as a legitimate null, which is the one value a caller cannot tell apart from a
                // real null - and it desynchronised nothing, so the rest of the graph decoded "fine".
                if (marker >= _objectCache.Count)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt data: a back-reference points at object {marker}, but only {_objectCache.Count} " +
                        "objects have been read so far.");

                return _objectCache[marker];
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
                // 0.8.5: this used to allocate an uninitialized instance, run the parameterless ctor and
                // return it *without consuming a single payload byte* - silent data loss for the object
                // plus a stream desync for everything after it. A name that resolves to a type the
                // current manifest describes is still readable (it is a real, described type); anything
                // else is rejected, because this engine cannot know how the bytes that follow are laid
                // out and guessing would corrupt the rest of the graph.
                string typeName = ReadStringFromTable(ref reader);
                Type type = ResolveFullName(typeName);
                if (type == null)
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"The save names the type '{typeName}' for a full-name reference, but it does not resolve in this " +
                        "application. A reference to an undescribed type cannot be read: the bytes that follow are laid out by " +
                        "that type's own formatter, and this engine has no schema for it.");

                if (_registry.TryGetId(type, out int actualId) && _oldById.TryGetValue(actualId, out var registeredSchema))
                {
                    var obj = ReadUserTypeInstance(registeredSchema, targetType, ref reader, cacheIndex, existingObject);
                    _objectCache[cacheIndex] = obj;
                    return obj;
                }

                throw new Ceras.Exceptions.CerasSchemaException(
                    $"The save references '{TypeIdentity.GetCanonicalName(type)}' by full name, but that type is not part of the " +
                    "current context's manifest, so its payload cannot be decoded. Declare it in the context " +
                    "([CerasContext]/[CerasKnownType]) and rebuild; loading the save as a type that is merely structurally similar " +
                    "would desynchronise the rest of the graph.");
            }

            return ReadUserTypeDirect(typeId, targetType, ref reader, cacheIndex, existingObject);
        }

        /// <summary>
        /// Resolves a type named by a full-name reference, through the configured binder. A binder reports
        /// "no such type" either by returning null or by throwing (SimpleTypeBinder throws), so both are
        /// normalized to null and the caller decides what an unresolvable name means.
        /// </summary>
        Type ResolveFullName(string typeName)
        {
            try
            {
                return _typeBinder.GetTypeFromBase(typeName);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                return null;
            }
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

            RejectUndescribableShape(oldSchema, currentType);

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

            return ReadReferenceInstance(oldSchema, plan, currentType, ref reader, cacheIndex, existingObject);
        }


        /// <summary>
        /// Reads a reference-typed value, matching the generated reader's construction contract:
        /// <list type="number">
        /// <item>allocate — uninitialized when a parameterized <c>[CerasConstructor]</c> supplies the
        /// values, so the instance is registered in the object cache <em>before</em> the constructor
        /// runs and a cycle through a constructor parameter resolves; otherwise through the configured
        /// construction, falling back to uninitialized;</item>
        /// <item>register the object-cache slot the writer reserved;</item>
        /// <item>run <c>[OnBeforeDeserialize]</c>;</item>
        /// <item>read every member, in payload order, into a local — the generated reader does the same,
        /// because the constructor arguments come out of the payload;</item>
        /// <item>invoke the constructor with the mapped argument values, and only for a fresh instance
        /// (a reused one keeps its state and never runs a constructor or a field initializer);</item>
        /// <item>assign the members, including the ones the constructor also received, exactly as the
        /// generated reader does, so a constructor that rewrites a value cannot change the result;</item>
        /// <item>defer <c>[OnAfterDeserialize]</c> until the whole graph is populated.</item>
        /// </list>
        /// </summary>
        object ReadReferenceInstance(OldTypeSchema oldSchema, TypeMigrationPlan plan, Type currentType,
            ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            // A reused instance is never re-constructed: no constructor, no field initializers.
            var ctorArgs = existingObject == null
                ? ResolveConstructorArguments(currentType, plan)
                : null;

            object obj = ctorArgs != null
                ? RuntimeHelpers.GetUninitializedObject(currentType)
                : existingObject ?? CreateReference(currentType);

            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = obj;

            FireBeforeCallback(obj, plan);

            if (TryReadCustomFormatter(currentType, ref obj, ref reader))
            {
                EnqueueAfterCallback(obj, plan);
                return obj;
            }

            if (ctorArgs != null)
            {
                var values = new object[ctorArgs.Parameters.Length];
                var assignments = new List<(FieldInfo Field, object Value)>();
                ReadAndApplyFields(oldSchema, plan, null, ref reader, ctorArgs, values, assignments);

                // A parameter no serialized member feeds is transient construction input and gets its own
                // declared default, exactly like the generated reader passes default(T).
                for (int i = 0; i < values.Length; i++)
                {
                    if (!ctorArgs.IsMapped(i)) values[i] = ctorArgs.DefaultFor(i);
                }

                try
                {
                    ctorArgs.Constructor.Invoke(obj, values);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    // The constructor is user code, so its failure is the load's failure. Name the type and
                    // the payload's field order so the cause can be found.
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"The [CerasConstructor] of '{TypeIdentity.GetCanonicalName(currentType)}' threw while migrating a save " +
                        $"(payload fields: {DescribeOldFields(plan)}): {e.InnerException.GetType().Name}: {e.InnerException.Message}",
                        e.InnerException);
                }

                foreach (var (field, value) in assignments)
                    field.SetValue(obj, value);
            }
            else
            {
                ReadAndApplyFields(oldSchema, plan, obj, ref reader);
            }

            EnqueueAfterCallback(obj, plan);
            return obj;
        }

        static string DescribeOldFields(TypeMigrationPlan plan)
        {
            if (plan?.Mappings == null || plan.Mappings.Length == 0) return "none";
            return string.Join(", ", plan.Mappings.Select(m => m.OldField?.StableName).Where(n => n != null));
        }

        /// <summary>
        /// Resolves how a type's <c>[CerasConstructor]</c> gets its values while migrating, or null when the
        /// type needs none. The selection order is the generator's (<c>GetBestConstructor</c>): an explicit
        /// <c>[CerasConstructor]</c>, then a parameterless constructor, then the constructor with the most
        /// parameters the serialized members can satisfy — so both readers pick the same constructor for
        /// the same type. Resolved once per type per load and cached on the migration plan.
        /// </summary>
        static CtorArgumentPlan ResolveConstructorArguments(Type type, TypeMigrationPlan plan)
        {
            if (plan == null) return null;
            if (!plan.CtorArgsResolved)
            {
                plan.CtorArgsResolved = true;
                plan.CtorArgs = BuildConstructorArgumentPlan(type, plan);
            }
            return plan.CtorArgs;
        }

        static CtorArgumentPlan BuildConstructorArgumentPlan(Type type, TypeMigrationPlan plan)
        {
            // A value type never takes a [CerasConstructor] on the generated path (the generator only looks
            // for one on reference types), and an interface/abstract type has no instance to construct.
            if (type == null || type.IsValueType || type.IsAbstract || type.IsInterface) return null;
            if (plan.Mappings == null) return null;

            var members = new List<(FieldInfo Field, string StableName)>();
            foreach (var mapping in plan.Mappings)
            {
                if (mapping.NewField == null) continue;
                members.Add((mapping.NewField, StableNameOf(mapping.NewField)));
            }
            if (members.Count == 0) return null;

            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (constructors.Length == 0) return null;

            var chosen = SelectConstructor(constructors, members);
            // A parameterless constructor (or none at all) needs no arguments: the caller runs it as before.
            if (chosen == null || chosen.GetParameters().Length == 0) return null;

            var parameters = chosen.GetParameters();
            var slotByField = new Dictionary<FieldInfo, int>();
            var mapped = new bool[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var match = FindMemberForParameter(parameters[i], members);
                if (match.Field == null) continue; // transient input: its declared default is passed

                if (!slotByField.TryAdd(match.Field, i))
                {
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"Cannot migrate '{TypeIdentity.GetCanonicalName(type)}': two parameters of its [CerasConstructor] both match the " +
                        $"member '{match.StableName}'. This is a construction-mapping bug; rename one of them.");
                }
                mapped[i] = true;
            }

            return new CtorArgumentPlan(chosen, parameters, slotByField, mapped);
        }

        static ConstructorInfo SelectConstructor(ConstructorInfo[] constructors, List<(FieldInfo Field, string StableName)> members)
        {
            // 1. Explicit [CerasConstructor]
            foreach (var ctor in constructors)
            {
                if (ctor.GetCustomAttribute<CerasConstructorAttribute>() != null) return ctor;
            }

            // 2. Parameterless (any accessibility) -> nothing to supply
            foreach (var ctor in constructors)
            {
                if (ctor.GetParameters().Length == 0) return null;
            }

            // 3. Heuristic: the most parameters the serialized members can all satisfy (this is what lets a
            //    primary-constructor type be constructed without the attribute).
            foreach (var ctor in constructors.OrderByDescending(c => c.GetParameters().Length))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 0) continue;

                bool allMapped = true;
                foreach (var parameter in parameters)
                {
                    if (FindMemberForParameter(parameter, members).Field == null) { allMapped = false; break; }
                }
                if (allMapped) return ctor;
            }
            return null;
        }

        static (FieldInfo Field, string StableName) FindMemberForParameter(ParameterInfo parameter, List<(FieldInfo Field, string StableName)> members)
        {
            string wanted = NormalizeName(parameter.Name);
            if (wanted == null) return default;

            foreach (var member in members)
                if (NormalizeName(member.Field.Name) == wanted) return member;
            foreach (var member in members)
                if (NormalizeName(member.StableName) == wanted) return member;
            return default;
        }

        /// <summary>Mirrors <c>TypeHelpers.NormalizeName</c> in the generator: lower-case, one leading underscore dropped.</summary>
        static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var normalized = name.ToLowerInvariant();
            if (normalized.StartsWith("_", StringComparison.Ordinal)) normalized = normalized.Substring(1);
            return normalized;
        }

        /// <summary>The wire name of a field: <c>[CerasStableName]</c>, or the property name for an auto-property.</summary>
        static string StableNameOf(FieldInfo field)
        {
            var stable = field.GetCustomAttribute<CerasStableNameAttribute>();
            if (stable?.Name != null) return stable.Name;

            var name = field.Name;
            const string backingSuffix = ">k__BackingField";
            if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(backingSuffix, StringComparison.Ordinal))
                name = name.Substring(1, name.Length - backingSuffix.Length);
            return name;
        }

        static bool IsExactType(object obj, Type type) => type != null && obj.GetType() == type;

        /// <summary>
        /// The instance handed back to the caller, plus the object whose member actually receives the
        /// elements. They differ for a wrapped shape: <c>ReadOnlyCollection&lt;T&gt;</c> has no
        /// <c>Add</c>, so its elements go into the backing <c>List&lt;T&gt;</c> that was passed to its
        /// constructor while the wrapper itself is what the field receives.
        /// </summary>
        readonly struct CollectionFill
        {
            public readonly object Result;
            public readonly object Target;

            public CollectionFill(object result, object target)
            {
                Result = result;
                Target = target;
            }
        }

        /// <summary>
        /// Produces the collection instance a collection payload is read into, in this order:
        /// <list type="number">
        /// <item>reuse <paramref name="existingObject"/> in place when it is the exact target type and
        /// exposes a usable <c>clearMethodName</c> (never silently append instead);</item>
        /// <item>otherwise construct the shape itself through its public parameterless constructor,
        /// which preserves a collection subclass exactly;</item>
        /// <item>otherwise build the built-in collection the shape describes and construct the shape from
        /// it, which is how a shape with no parameterless constructor
        /// (<c>ReadOnlyCollection&lt;T&gt;</c>(<c>IList&lt;T&gt;</c>),
        /// <c>ReadOnlyDictionary&lt;K,V&gt;</c>(<c>IDictionary&lt;K,V&gt;</c>), a <c>List&lt;T&gt;</c> or
        /// <c>Dictionary&lt;K,V&gt;</c> subclass behind a custom wrapper, …) is restored.</item>
        /// </list>
        /// A shape that satisfies none of those is rejected with an actionable schema error rather than
        /// a raw reflection exception.
        /// </summary>
        CollectionFill CreateCollection(Type collectionType, Type backingDefinition, Type[] backingArgs,
            object existingObject, string clearMethodName, string kind)
        {
            if (existingObject != null && IsExactType(existingObject, collectionType))
            {
                var clear = collectionType.GetMethod(clearMethodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (clear != null)
                {
                    clear.Invoke(existingObject, null);
                    return new CollectionFill(existingObject, existingObject);
                }
            }

            // The shape's own parameterless constructor: exact, and the only way a subclass keeps its type.
            var defaultCtor = collectionType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (defaultCtor != null)
            {
                try
                {
                    var direct = defaultCtor.Invoke(null);
                    if (direct != null)
                        return new CollectionFill(direct, direct);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"The {kind} collection type '{TypeIdentity.GetCanonicalName(collectionType)}' threw from its parameterless constructor " +
                        $"while a save was being migrated: {e.InnerException.Message}", e.InnerException);
                }
            }

            // No parameterless constructor: build the built-in collection this shape describes and wrap it.
            Type backingType = null;
            if (backingDefinition != null && backingArgs != null && backingArgs.Length == backingDefinition.GetGenericArguments().Length
                && Array.TrueForAll(backingArgs, a => a != null))
            {
                backingType = backingDefinition.MakeGenericType(backingArgs);
            }

            object backing = null;
            if (backingType != null && backingType.GetConstructor(Type.EmptyTypes) != null)
                backing = Activator.CreateInstance(backingType);

            if (backing != null)
            {
                var wrappingCtor = FindWrappingConstructor(collectionType, backingType);
                if (wrappingCtor != null)
                {
                    try
                    {
                        return new CollectionFill(wrappingCtor.Invoke(new[] { backing }), backing);
                    }
                    catch (TargetInvocationException e) when (e.InnerException != null)
                    {
                        throw new Ceras.Exceptions.CerasSchemaException(
                            $"The {kind} collection type '{TypeIdentity.GetCanonicalName(collectionType)}' threw from its wrapping constructor " +
                            $"while a save was being migrated: {e.InnerException.Message}", e.InnerException);
                    }
                }
            }

            throw new Ceras.Exceptions.CerasSchemaException(
                $"Ceras cannot construct the {kind} collection type '{TypeIdentity.GetCanonicalName(collectionType)}' while migrating a save: it has " +
                $"no public parameterless constructor and no constructor that accepts a {backingType?.Name ?? "matching built-in collection"}, " +
                $"and it exposes no usable '{clearMethodName}' to refill. Give the type a public parameterless constructor (or a wrapping " +
                "constructor Ceras can use), declare a custom formatter for it, or load the save with the build that wrote it.");
        }

        /// <summary>
        /// Finds the public single-argument constructor through which <paramref name="collectionType"/>
        /// can be built from the built-in collection <paramref name="backingType"/> (as
        /// <c>ReadOnlyCollection&lt;T&gt;</c> wraps an <c>IList&lt;T&gt;</c>, and
        /// <c>ReadOnlyDictionary&lt;K,V&gt;</c> an <c>IDictionary&lt;K,V&gt;</c>).
        /// </summary>
        static ConstructorInfo FindWrappingConstructor(Type collectionType, Type backingType)
        {
            var sources = new List<Type> { backingType };
            var args = backingType.GetGenericArguments();
            if (args.Length == 1)
            {
                sources.Add(typeof(IEnumerable<>).MakeGenericType(args));
                sources.Add(typeof(ICollection<>).MakeGenericType(args));
                sources.Add(typeof(IList<>).MakeGenericType(args));
                sources.Add(typeof(IReadOnlyList<>).MakeGenericType(args));
                sources.Add(typeof(IReadOnlyCollection<>).MakeGenericType(args));
            }
            else if (args.Length == 2)
            {
                sources.Add(typeof(IDictionary<,>).MakeGenericType(args));
                sources.Add(typeof(IReadOnlyDictionary<,>).MakeGenericType(args));
                sources.Add(typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(args)));
            }

            foreach (var ctor in collectionType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length != 1) continue;
                foreach (var source in sources)
                {
                    if (parameters[0].ParameterType.IsAssignableFrom(source))
                        return ctor;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves the member that adds one element to a collection shape: the non-generic
        /// <see cref="System.Collections.IList.Add(object)"/> when the fill target exposes it, otherwise
        /// the shape's own <c>Add(T)</c>/<c>Enqueue(T)</c>/<c>Push(T)</c> (which is also how
        /// <c>CollectionFormatter</c> writes the shape), otherwise <c>Add(K,V)</c>.
        /// </summary>
        static Action<object, object> ResolveElementAdder(object fillTarget, Type elementType)
        {
            if (elementType != null && fillTarget is System.Collections.IList list)
                return (target, value) => list.Add(value);

            foreach (var name in new[] { "Add", "Enqueue", "Push" })
            {
                var add = fillTarget.GetType().GetMethod(name, new[] { elementType });
                if (add != null)
                    return (target, value) => add.Invoke(target, new[] { value });
            }
            return null;
        }

        static Action<object, object, object> ResolveEntryAdder(object fillTarget, Type keyType, Type valueType)
        {
            if (keyType != null && valueType != null && fillTarget is System.Collections.IDictionary dictionary)
                return (target, key, value) => dictionary.Add(key, value);

            var add = fillTarget.GetType().GetMethod("Add", new[] { keyType, valueType });
            if (add != null)
                return (target, key, value) => add.Invoke(target, new[] { key, value });

            return null;
        }

        /// <summary>
        /// Reads one element of a collection payload, or consumes and discards it when the file did not
        /// record the element's type identity. A fixed-size element can still be skipped from its current
        /// CLR type; anything else fails loudly rather than desyncing the stream.
        /// </summary>
        object SkipAndReturnDefault(Type elementType, ref SequenceReader<byte> reader)
        {
            int fixedSize = elementType != null && WellKnownTypes.TryGetId(elementType, out int wellKnownId)
                ? WellKnownTypes.GetFixedSize(wellKnownId)
                : 0;
            if (fixedSize > 0)
            {
                reader.Advance(fixedSize);
                return elementType.IsValueType ? Activator.CreateInstance(elementType) : null;
            }

            throw new Ceras.Exceptions.CerasSchemaException(
                $"Cannot decode a collection of '{elementType?.FullName ?? "unknown element type"}': the save file did not record the element " +
                "type identity, and the current element type has no fixed size to skip. This save was written with a layout Ceras cannot migrate; " +
                "load it with the build that wrote it.");
        }


        /// <summary>
        /// Allocates a reference-typed instance the way the generated reader does when no constructor
        /// arguments are involved: uninitialized, then the parameterless constructor when the type has
        /// one, so field initializers run and the object is fully initialized before its members are
        /// assigned. A configured <see cref="TypeConstruction"/> (a factory or a pool) is deliberately not
        /// consulted here — routing reference types through it is TODO 0.8.11, and doing it now would
        /// change reuse/pooling semantics as a side effect of this contract.
        /// </summary>
        object CreateReference(Type type)
        {
            if (type == null) return null;

            var obj = RuntimeHelpers.GetUninitializedObject(type);
            var parameterless = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (parameterless != null)
            {
                try
                {
                    parameterless.Invoke(obj, null);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"The parameterless constructor of '{TypeIdentity.GetCanonicalName(type)}' threw while migrating a save: " +
                        $"{e.InnerException.GetType().Name}: {e.InnerException.Message}", e.InnerException);
                }
            }
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

            return RuntimeHelpers.GetUninitializedObject(type);
        }

        object ReadGenericInstantiation(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Array) return ReadArray(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.KeyValuePair) return ReadKeyValuePair(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Nullable) return ReadNullable(oldSchema, targetType, ref reader);
            if (WellKnownTypes.IsTupleDefinition(oldSchema.DefinitionTypeId)) return ReadTuple(oldSchema, targetType, ref reader, cacheIndex, existingObject);

            // Arbitrary generic class or struct
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type currentType = plan?.CurrentType ?? targetType;

            // A collection payload is written by the built-in collection formatter as an explicit count
            // plus its elements/entries, so every collection is read through the single collection
            // reader, never through the ordinary user-type path (which would consume a reference marker
            // that was never written). This check must come BEFORE any well-known definition dispatch: a
            // collection subclass and a shape with no public parameterless constructor
            // (ReadOnlyCollection<T>, ReadOnlyDictionary<K,V>) both bake the reserved definition id of the
            // built-in collection they are described as, so dispatching on that id first would pick a
            // reader for the wrong shape and then fail to construct the instance.
            if (IsCollectionShape(currentType))
                return ReadCollectionShape(currentType, targetType, oldSchema, ref reader, cacheIndex, existingObject);

            // The file describes a collection payload, but this build has no concrete collection type for
            // it (an unresolvable old name, or a polymorphic slot read as object). Consume the payload so
            // the stream stays aligned and report no value.
            if (IsCollectionPayload(oldSchema.DefinitionTypeId))
                return ReadUnknownCollectionPayload(oldSchema, ref reader);

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

            return ReadReferenceInstance(oldSchema, plan, currentType, ref reader, cacheIndex, existingObject);
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
        /// representation.
        /// <para>
        /// A <c>Tuple</c> is a class written through <c>ReferenceFormatter&lt;Tuple&lt;…&gt;&gt;</c>, so the
        /// writer registers it in the object cache and any later occurrence of the same instance is a
        /// back-reference to that slot — the slot reserved by <c>ReadPolymorphicValue</c> must be filled
        /// with the instance. A <c>ValueTuple</c> is a struct written with no reference marker and is never
        /// registered, so it gets no slot. For the class form the instance is created (uninitialized) and
        /// registered <em>before</em> the items are read, so a self-referential graph cannot recurse.
        /// </para>
        /// </summary>
        object ReadTuple(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            int count = oldSchema.ArgTypeIds.Length;
            var argTypes = targetType != null && targetType.IsGenericType ? targetType.GetGenericArguments() : null;

            if (targetType == null || argTypes == null)
            {
                // Nothing to reconstruct into: consume the items so the stream stays aligned.
                foreach (int oldItemId in oldSchema.ArgTypeIds) SkipValue(oldItemId, ref reader);
                return null;
            }

            if (!targetType.IsValueType)
            {
                object tuple = RuntimeHelpers.GetUninitializedObject(targetType);
                if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = tuple;

                var itemFields = GetTupleItemFields(targetType);
                for (int i = 0; i < count; i++)
                {
                    Type itemType = i < argTypes.Length ? argTypes[i] : null;
                    var value = ReadValue(oldSchema.ArgTypeIds[i], itemType, ref reader);
                    if (itemFields != null && i < itemFields.Length && itemFields[i] != null)
                        itemFields[i].SetValue(tuple, value);
                }
                return tuple;
            }

            var values = new object[count];
            for (int i = 0; i < count; i++)
            {
                int oldItemId = oldSchema.ArgTypeIds[i];
                Type itemType = i < argTypes.Length ? argTypes[i] : null;
                values[i] = ReadValue(oldItemId, itemType, ref reader);
            }

            if (values.Length != argTypes.Length)
            {
                // A tuple whose arity changed cannot be reconstructed; the items were still consumed, so
                // the stream stays aligned.
                return null;
            }

            try
            {
                return Activator.CreateInstance(targetType, values);
            }
            catch (MissingMethodException)
            {
                // Arity change (or an item type the current runtime does not have a ctor for): the items
                // were consumed, so the stream stays aligned and no value can be produced.
                return null;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt payload: constructing the value tuple '{TypeIdentity.GetCanonicalName(targetType)}' from the stored items failed: " +
                    $"{e.InnerException.Message}", e.InnerException);
            }
            catch (ArgumentException e)
            {
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt payload: the stored items do not form a valid '{TypeIdentity.GetCanonicalName(targetType)}': {e.Message}", e);
            }
        }

        /// <summary>
        /// The item fields of a tuple type in order (<c>Item1..Item7</c>, then <c>Rest</c> for the
        /// 8-argument form), or null when they cannot be identified. <c>ValueTuple</c> names them
        /// <c>Item1</c>…, while <c>Tuple</c> keeps them in <c>m_Item1</c>…, so both spellings are
        /// accepted; the item is then written straight into the field.
        /// </summary>
        static FieldInfo[] GetTupleItemFields(Type tupleType)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var byIndex = new FieldInfo[8]; // 0..6 = Item1..Item7, 7 = Rest

            foreach (var field in tupleType.GetFields(Flags))
            {
                string name = field.Name;
                if (name.StartsWith("m_", StringComparison.Ordinal)) name = name.Substring(2);
                else if (name.StartsWith("_", StringComparison.Ordinal)) name = name.Substring(1);

                int index;
                if (string.Equals(name, "Rest", StringComparison.Ordinal))
                {
                    index = 7;
                }
                else if (name.Length == 5 && name.StartsWith("Item", StringComparison.Ordinal)
                         && int.TryParse(name.Substring(4), out int itemNumber) && itemNumber >= 1 && itemNumber <= 7)
                {
                    index = itemNumber - 1;
                }
                else
                {
                    continue;
                }

                byIndex[index] ??= field;
            }

            int count = 0;
            while (count < 7 && byIndex[count] != null) count++;
            if (count == 7 && byIndex[7] != null) count = 8; // the 8-argument form keeps the tail in Rest
            if (count == 0) return null;

            var itemFields = new FieldInfo[count];
            Array.Copy(byIndex, itemFields, count);
            return itemFields;
        }

        object ReadArray(OldTypeSchema oldSchema, Type targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object existingObject = null)
        {
            // Multidimensional arrays are written by MultiDimensionalArrayFormatter (rank + per-dimension
            // lengths + flattened elements), which this structural reader does not decode. Reject them
            // loudly rather than misreading the payload as a single length.
            if (targetType != null && targetType.IsArray && targetType.GetArrayRank() != 1)
                throw SchemaRegistry.UnsupportedMultidimensionalArray(targetType, "the migration interpreter");

            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count < 0 || count > _maxCollectionSize)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt save: an array declares {count} elements, outside the configured maximum of {_maxCollectionSize}.");

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
        /// Rejects the saves written before the format learned to describe collection and tuple shapes
        /// structurally. Those files record such a value as an ordinary class schema with <b>no fields</b>
        /// (the shape's payload was never described), while the bytes are a count plus elements. Reading
        /// them as a field-less class consumes nothing, so the interpreter silently desyncs and every
        /// following field decodes as garbage. Failing loudly turns that silent corruption into an
        /// actionable error; such saves are not migratable (see Format/LAYOUT.md).
        /// </summary>
        static void RejectUndescribableShape(OldTypeSchema oldSchema, Type currentType)
        {
            if (oldSchema == null || currentType == null) return;
            if (oldSchema.Kind != SchemaTypeKind.Class && oldSchema.Kind != SchemaTypeKind.Struct) return;
            if (oldSchema.Fields == null || oldSchema.Fields.Length != 0) return;
            if (oldSchema.DefinitionTypeId != 0) return; // a definition id means it was described structurally

            bool describableNow = IsCollectionShape(currentType)
                || WellKnownTypes.IsWellKnownStructuralValueType(currentType);

            if (describableNow)
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot migrate the value of '{oldSchema.StableName}': the save file describes it as a class with no members, which is how " +
                    $"Ceras recorded a collection or tuple before the format could describe those shapes. Its payload is a count plus elements, " +
                    $"which that schema does not describe, so reading it would silently desynchronize the rest of the save. " +
                    $"'{TypeIdentity.GetCanonicalName(currentType)}' is described structurally now, so files written by this version onward " +
                    "migrate; load this one with the build that wrote it, or start the save again.");
        }

        /// <summary>
        /// True when a file's definition id describes a collection payload: an explicit count followed by
        /// the elements (or key/value entries) of one of the built-in collections.
        /// </summary>
        static bool IsCollectionPayload(int definitionTypeId)
            => definitionTypeId == WellKnownTypes.List
            || definitionTypeId == WellKnownTypes.Dictionary
            || definitionTypeId == WellKnownTypes.HashSet
            || definitionTypeId == WellKnownTypes.Queue
            || definitionTypeId == WellKnownTypes.Stack;

        /// <summary>
        /// The single collection reader. The file's definition id decides the <em>payload</em> (an explicit
        /// count plus elements/entries, exactly like <c>CollectionFormatter</c>/<c>QueueFormatter</c>/
        /// <c>StackFormatter</c> write), while the current type supplies the CLR element/key/value types
        /// and the instance to fill. Using the file's identity for the layout and the current type for the
        /// CLR side means a rename, an id reassignment or a changed shape still decodes by shape rather
        /// than by id.
        /// </summary>
        object ReadCollectionShape(Type currentType, Type targetType, OldTypeSchema oldSchema, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            if (!WellKnownTypes.TryResolveCollectionShape(currentType, out var shapeDef, out var shapeArgs))
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot decode collection type '{TypeIdentity.GetCanonicalName(currentType)}': Ceras could not infer its element/key/value type. " +
                    "Give the type a concrete ICollection<T>/IDictionary<K,V> implementation, or provide a custom formatter for it.");

            if (!IsCollectionPayload(oldSchema.DefinitionTypeId))
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot decode '{TypeIdentity.GetCanonicalName(currentType)}': the save file describes it as type id " +
                    $"{oldSchema.DefinitionTypeId}, which is not a collection payload. Ceras cannot tell how many bytes its " +
                    "elements occupy, so the rest of the save cannot be read safely.");

            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count < 0 || count > _maxCollectionSize)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt save: a collection declares {count} elements, outside the configured maximum of {_maxCollectionSize}.");

            int? oldElem0 = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            int? oldElem1 = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;
            Type elementType = shapeArgs.Length > 0 ? shapeArgs[0] : null;
            Type keyType = shapeArgs.Length > 0 ? shapeArgs[0] : null;
            Type valueType = shapeArgs.Length > 1 ? shapeArgs[1] : null;

            switch (oldSchema.DefinitionTypeId)
            {
                case WellKnownTypes.Dictionary:
                    return ReadDictionaryShape(currentType, keyType, valueType, oldElem0, oldElem1, count, ref reader, cacheIndex, existingObject);
                case WellKnownTypes.HashSet:
                    return ReadHashSetShape(currentType, elementType, oldElem0, count, ref reader, cacheIndex, existingObject);
                case WellKnownTypes.Queue:
                    return ReadQueueShape(currentType, elementType, oldElem0, count, ref reader, cacheIndex, existingObject);
                case WellKnownTypes.Stack:
                    return ReadStackShape(currentType, elementType, oldElem0, count, ref reader, cacheIndex, existingObject);
                default:
                    return ReadListShape(currentType, elementType, oldElem0, count, ref reader, cacheIndex, existingObject);
            }
        }

        /// <summary>
        /// The file describes a collection payload, but this build has no concrete collection type for it
        /// (the old name no longer resolves, or the slot is polymorphic and the runtime type is unknown
        /// here). Consume the payload so the stream stays aligned and report no value.
        /// </summary>
        object ReadUnknownCollectionPayload(OldTypeSchema oldSchema, ref SequenceReader<byte> reader)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return null;
            if (count < 0 || count > _maxCollectionSize)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt save: a collection declares {count} elements, outside the configured maximum of {_maxCollectionSize}.");

            int? oldKey = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            int? oldValue = oldSchema.ArgTypeIds.Length > 1 ? (int?)oldSchema.ArgTypeIds[1] : null;

            if (oldSchema.DefinitionTypeId == WellKnownTypes.Dictionary)
            {
                for (int i = 0; i < count; i++)
                {
                    SkipValue(oldKey ?? WellKnownTypes.Object, ref reader);
                    SkipValue(oldValue ?? WellKnownTypes.Object, ref reader);
                }
            }
            else
            {
                SkipElements((uint)count, oldKey, ref reader);
            }
            return null;
        }

        object ReadListShape(Type currentType, Type elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var fill = CreateCollection(currentType, typeof(List<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "list");
            CacheCollection(fill, cacheIndex);

            var add = ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the collection type '{TypeIdentity.GetCanonicalName(currentType)}' while migrating: it has no usable " +
                    $"'{elementType?.Name ?? "element"}' add method. Provide a custom formatter for it, or load the save with the build that wrote it.");

            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue
                    ? ReadValue(oldElemId.Value, elementType, ref reader)
                    : SkipAndReturnDefault(elementType, ref reader);
                add(fill.Target, Coerce(val, coerce, elementType));
            }
            return fill.Result;
        }

        object ReadDictionaryShape(Type currentType, Type keyType, Type valType, int? oldKeyId, int? oldValId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var fill = CreateCollection(currentType, typeof(Dictionary<,>), new[] { keyType, valType },
                existingObject, "Clear", "dictionary");
            CacheCollection(fill, cacheIndex);

            var add = ResolveEntryAdder(fill.Target, keyType, valType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the dictionary type '{TypeIdentity.GetCanonicalName(currentType)}' while migrating: it has no usable " +
                    $"'Add({keyType?.Name ?? "key"}, {valType?.Name ?? "value"})' method. Provide a custom formatter for it, or load the save " +
                    "with the build that wrote it.");

            var kCoerce = oldKeyId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldKeyId.Value, keyType) : CoercionKind.None;
            var vCoerce = oldValId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldValId.Value, valType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var k = oldKeyId.HasValue ? ReadValue(oldKeyId.Value, keyType, ref reader) : SkipAndReturnDefault(keyType, ref reader);
                var v = oldValId.HasValue ? ReadValue(oldValId.Value, valType, ref reader) : SkipAndReturnDefault(valType, ref reader);
                add(fill.Target, Coerce(k, kCoerce, keyType), Coerce(v, vCoerce, valType));
            }
            return fill.Result;
        }

        object ReadHashSetShape(Type currentType, Type elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var fill = CreateCollection(currentType, typeof(HashSet<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "set");
            CacheCollection(fill, cacheIndex);

            var add = ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the set type '{TypeIdentity.GetCanonicalName(currentType)}' while migrating: it has no usable " +
                    $"'Add({elementType?.Name ?? "element"})' method.");

            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                add(fill.Target, Coerce(val, coerce, elementType));
            }
            return fill.Result;
        }

        object ReadQueueShape(Type currentType, Type elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object existingObject)
        {
            var fill = CreateCollection(currentType, typeof(Queue<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "queue");
            CacheCollection(fill, cacheIndex);

            var enqueue = ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the queue type '{TypeIdentity.GetCanonicalName(currentType)}' while migrating: it has no usable " +
                    $"'Enqueue({elementType?.Name ?? "element"})' method.");

            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                enqueue(fill.Target, Coerce(val, coerce, elementType));
            }
            return fill.Result;
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

            var fill = CreateCollection(currentType, typeof(Stack<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "stack");
            CacheCollection(fill, cacheIndex);

            var push = ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the stack type '{TypeIdentity.GetCanonicalName(currentType)}' while migrating: it has no usable " +
                    $"'Push({elementType?.Name ?? "element"})' method.");

            for (int i = count - 1; i >= 0; i--) push(fill.Target, values[i]);
            return fill.Result;
        }

        /// <summary>
        /// Registers the collection in the writer's object cache. Only a slot the writer actually
        /// reserved may be filled: a monomorphic collection member is written with no reference marker
        /// and is never registered, so appending a slot here would shift every later back-reference onto
        /// the wrong object.
        /// </summary>
        void CacheCollection(CollectionFill fill, int? cacheIndex)
        {
            if (cacheIndex.HasValue) _objectCache[cacheIndex.Value] = fill.Result;
        }

        void SkipValue(int typeId, ref SequenceReader<byte> reader)
        {
            if (_oldById.TryGetValue(typeId, out var s) && s.FixedByteSize > 0) { reader.Advance(s.FixedByteSize); return; }
            int fixedSize = WellKnownTypes.GetFixedSize(typeId);
            if (fixedSize > 0) { reader.Advance(fixedSize); return; }

            switch (typeId)
            {
                case WellKnownTypes.Bool: reader.Advance(1); return;
                case WellKnownTypes.String: SerializerBinary.ReadUInt32Fixed(ref reader); return;
                case WellKnownTypes.Int32: SerializerBinary.ReadInt32Fixed(ref reader); return;
                case WellKnownTypes.UInt32: SerializerBinary.ReadUInt32Fixed(ref reader); return;
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
                // 0.8.5: the old skip branch "hoped it has no fields", which desynchronised the stream
                // for everything after an undescribed full-name reference. The read path now rejects the
                // same bytes, so the skip path must too: skipping a value it cannot account for would
                // silently produce a wrong result instead of an error.
                string typeName = ReadStringFromTable(ref reader);
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"The save references '{typeName}' by full name at a position that has to be skipped, but that type is not part " +
                    "of the current context's manifest, so its payload cannot be accounted for. Declare it in the context and rebuild.");
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
                // Compare and map by raw bits so a ulong value above long.MaxValue survives. A value that
                // cannot even be read as bits is corruption, not a decoder failure.
                ulong bits;
                try
                {
                    bits = EnumBits.ToRawBits(rawValue, plan.CurrentType);
                }
                catch (Exception e) when (e is OverflowException or InvalidCastException or FormatException)
                {
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt enum payload for '{TypeIdentity.GetCanonicalName(plan.CurrentType)}': the stored value '{rawValue ?? "null"}' " +
                        "is not representable as the enum's underlying type.", e);
                }

                if (plan.EnumValueMap.TryGetValue(bits, out ulong mapped))
                    return Enum.ToObject(plan.CurrentType, EnumBits.ToClrValue(mapped, plan.CurrentType));
            }

            if (targetType != null && targetType.IsEnum)
                return MakeEnum(targetType, rawValue);

            return rawValue;
        }

        static object MakeEnum(Type enumType, object rawValue)
        {
            try
            {
                return Enum.ToObject(enumType, rawValue);
            }
            catch (Exception e) when (e is ArgumentException or InvalidCastException or OverflowException)
            {
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt enum payload for '{TypeIdentity.GetCanonicalName(enumType)}': the stored value '{rawValue ?? "null"}' is not a " +
                    $"valid '{enumType.Name}' value.", e);
            }
        }

        void SkipEnumValue(OldTypeSchema oldSchema, ref SequenceReader<byte> reader)
        {
            int underlying = oldSchema.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            SkipValue(underlying, ref reader);
        }

        /// <summary>
        /// Reads the fields of <paramref name="oldSchema"/> in payload order and applies them to
        /// <paramref name="target"/>.
        /// <para>
        /// When a <paramref name="ctorArgs"/> plan is supplied, nothing is assigned to a target: the
        /// values are routed to the constructor's parameters and collected in
        /// <paramref name="assignments"/> instead, so the caller can construct the instance first and
        /// assign afterwards. That is the order the generated reader uses for a type whose constructor
        /// takes its values out of the payload. <paramref name="target"/> is then ignored.
        /// </para>
        /// </summary>
        void ReadAndApplyFields(OldTypeSchema oldSchema, TypeMigrationPlan plan, object target, ref SequenceReader<byte> reader,
            CtorArgumentPlan ctorArgs = null, object[] ctorValues = null, List<(FieldInfo Field, object Value)> assignments = null)
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

                    var coerced = Coerce(value, mapping.Coercion, mapping.NewField.FieldType);

                    if (assignments != null)
                    {
                        int slot = ctorArgs.SlotForField(mapping.NewField);
                        if (slot >= 0) ctorValues[slot] = coerced;
                        else assignments.Add((mapping.NewField, coerced));
                    }
                    else if (target != null)
                    {
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
