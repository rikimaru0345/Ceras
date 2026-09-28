using System.Diagnostics.CodeAnalysis;
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

        // The file's own schema, indexed by name and by the file's type id (FileSchemaView), and the
        // old-to-current conversion plans built from it (MigrationPlanBuilder). Everything below reads
        // the payload through those two, so neither the indexing nor the planning is duplicated here.
        readonly FileSchemaView                        _file;
        readonly Dictionary<string, TypeMigrationPlan> _plans;
        readonly string[]                              _stringTable;
        readonly uint                                  _robustVersion;
        readonly CerasSerializer                       _serializer;
        readonly SchemaRegistry                        _registry;
        readonly ITypeBinder                           _typeBinder;
        readonly IdentityCache                         _identity = new();
        readonly CurrentObjectFactory                  _factory;
        readonly TypeValueShapes                       _typeValues;
        readonly uint                                  _maxCollectionSize;

        const int FullNameMarker = 127;

        public SchemaTraversalEngine(
            Dictionary<string, OldTypeSchema> oldSchemas,
            string[]                          stringTable,
            uint                              robustVersion,
            CerasSerializer                   serializer)
        {
            _file            = new FileSchemaView(oldSchemas);
            _factory         = new CurrentObjectFactory(serializer);
            _stringTable     = stringTable;
            _robustVersion   = robustVersion;

            // 0.8.6: this used to be a fresh SimpleTypeBinder while CerasRobustSerializer resolves the same
            // file's type ids with Config.TypeBinder, so a configured binder was honoured by one half of the
            // interpreter and ignored by the other - the same name could resolve to two different types in
            // one Load. Take the configured one.
            _typeBinder = serializer?.Config?.TypeBinder ?? new SimpleTypeBinder();
            _serializer      = serializer;
            // The robust serializer is the only caller and always passes one with a robust config.
        _registry        = ((CerasRobustConfig)serializer!.Config).SchemaRegistry;
            _maxCollectionSize = serializer.Config.SizeLimits.MaxCollectionSize;

            // The decoder for the Type *value* a polymorphic value carries. It resolves a manifest id
            // through the same map every other id in this engine resolves through, so a renamed type used
            // as a nested argument reaches its current type the way every other migration does.
            _typeValues = new TypeValueShapes(_file, ResolveManifestTypeId);

            // The plans below map the file's names onto the current model, which needs the complete name
            // space: a shape only the bake registers answers to its [PreviousTypeName] alias once it is
            // registered. CerasRobustSerializer builds the file's id map first, and that call fails
            // unless the manifest is baked - so the ordering is enforced before this ctor runs.
            _plans = MigrationPlanBuilder.BuildAll(_file.ByName, _registry);

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

        static Action<CerasSerializer, object>? MakeInvoker(MethodInfo method, CallbackArgumentKind kind, MigrationContext context)
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

        [return: MaybeNull]
        public T ReadRoot<T>(ref SequenceReader<byte> reader, T rootObject = default)
        {
            // Roots are declared types, so the stored root id and this CLR type were already
            // validated to match. Dispatch on the encoding shape rather than assuming every root
            // is a user schema: well-known scalars/string/object/Type are never present in the
            // file's schema section, so looking them up by stable name would silently return
            // default (e.g. 0 for a saved int).
            if (WellKnownTypes.TryGetId(typeof(T), out int wellKnownId))
            {
                object? wellKnown = ReadValue(wellKnownId, typeof(T), ref reader, rootObject);
                return wellKnown == null ? default : (T)wellKnown;
            }

            string stableName = ResolveOldStableName(typeof(T));
            object? obj = ReadUserTypeByStableName(stableName, typeof(T), ref reader, rootObject);
            return obj == null ? default : (T)obj;
        }

        string ResolveOldStableName(Type t) => _file.ResolveStableName(t, _registry);

        // ── Core dispatch ─────────────────────────────────────────────────────

        /// <summary>Whether a value is being decoded or only accounted for.</summary>
        enum WalkMode { Read, Skip }

        /// <summary>
        /// The single entry point for a payload value, in both directions. <para>
        /// "What does this type id mean?" used to be answered twice — once in <c>ReadValue</c> and once in
        /// <c>SkipValue</c> — and the two switches had to be kept in step by hand, so a change to one could
        /// silently desynchronise the other. Now the type-id dispatch, the fixed-size short-circuit and the
        /// scalar widths exist once; only the leaf differs between decoding a value and advancing past it.
        /// </para>
        /// <para>
        /// A skipped value still validates: a back-reference past the identity cache and a full-name
        /// reference the manifest cannot describe are both refused, because a skip that quietly accepted
        /// them would return a plausible answer for a stream it had lost track of.
        /// </para>
        /// </summary>
        object? WalkValue(int typeId, Type? targetType, ref SequenceReader<byte> reader,
            object? existingObject, WalkMode mode)
        {
            // A fixed-size value is a fixed-size value in both directions: when skipping, that is the whole
            // of it, and it is why a described struct's payload never has to be walked field by field.
            if (mode == WalkMode.Skip)
            {
                if (_file.TryGetById(typeId, out var described) && described.FixedByteSize > 0)
                {
                    reader.Advance(described.FixedByteSize);
                    return null;
                }

                int fixedSize = WellKnownTypes.GetFixedSize(typeId);
                if (fixedSize > 0)
                {
                    reader.Advance(fixedSize);
                    return null;
                }
            }

            switch (typeId)
            {
                case WellKnownTypes.Null:    return null;
                case WellKnownTypes.Bool:    return mode == WalkMode.Read ? SerializerBinary.ReadByte(ref reader) != 0 : Advance(ref reader, 1);
                case WellKnownTypes.Byte:    return mode == WalkMode.Read ? SerializerBinary.ReadByte(ref reader) : Advance(ref reader, 1);
                case WellKnownTypes.SByte:   return mode == WalkMode.Read ? (object)(sbyte)SerializerBinary.ReadByte(ref reader) : Advance(ref reader, 1);
                case WellKnownTypes.Char:    return mode == WalkMode.Read ? (char)SerializerBinary.ReadInt16Fixed(ref reader) : Advance(ref reader, 2);
                case WellKnownTypes.Int16:   return mode == WalkMode.Read ? SerializerBinary.ReadInt16Fixed(ref reader) : Advance(ref reader, 2);
                case WellKnownTypes.UInt16:  return mode == WalkMode.Read ? (ushort)SerializerBinary.ReadInt16Fixed(ref reader) : Advance(ref reader, 2);
                case WellKnownTypes.Int32:   return mode == WalkMode.Read ? SerializerBinary.ReadInt32Fixed(ref reader) : Advance(ref reader, 4);
                case WellKnownTypes.UInt32:  return mode == WalkMode.Read ? SerializerBinary.ReadUInt32Fixed(ref reader) : Advance(ref reader, 4);
                case WellKnownTypes.Single:  return mode == WalkMode.Read ? SerializerBinary.ReadFloat32Fixed(ref reader) : Advance(ref reader, 4);
                case WellKnownTypes.Int64:   return mode == WalkMode.Read ? SerializerBinary.ReadInt64Fixed(ref reader) : Advance(ref reader, 8);
                case WellKnownTypes.UInt64:  return mode == WalkMode.Read ? (object)(ulong)SerializerBinary.ReadInt64Fixed(ref reader) : Advance(ref reader, 8);
                case WellKnownTypes.Double:  return mode == WalkMode.Read ? SerializerBinary.ReadDouble64Fixed(ref reader) : Advance(ref reader, 8);
                case WellKnownTypes.Decimal:
                {
                    // 16 bytes, validated by SerializerBinary so an impossible scale/sign/reserved-bit word is
                    // reported as data corruption rather than escaping as ArgumentException.
                    if (mode == WalkMode.Skip) return Advance(ref reader, 16);
                    return SerializerBinary.ReadDecimal(ref reader);
                }
                case WellKnownTypes.String:
                {
                    if (mode == WalkMode.Skip) { SerializerBinary.ReadUInt32Fixed(ref reader); return null; }
                    return ReadStringFromTable(ref reader);
                }
                case WellKnownTypes.DateTime:
                    // Matches SerializerBinary.WriteDateTime / DateTimeFormatter: the writer persists
                    // DateTime.ToBinary() (ticks plus kind bits), so the interpreter must use FromBinary.
                    // Treating the bits as raw ticks throws for Utc/Local values and loses the kind.
                    if (mode == WalkMode.Skip) return Advance(ref reader, 8);
                    return DateTime.FromBinary(SerializerBinary.ReadInt64Fixed(ref reader));
                case WellKnownTypes.TimeSpan:
                    if (mode == WalkMode.Skip) return Advance(ref reader, 8);
                    return new TimeSpan(SerializerBinary.ReadInt64Fixed(ref reader));
                case WellKnownTypes.DateTimeOffset:
                    // Matches DateTimeOffsetFormatter / SerializerBinary: ticks then offset minutes, and
                    // validates them (an out-of-range value is corruption, not a decoder failure).
                    if (mode == WalkMode.Skip) return Advance(ref reader, 10);
                    return SerializerBinary.ReadDateTimeOffset(ref reader);
                case WellKnownTypes.Guid:
                {
                    if (mode == WalkMode.Skip) return Advance(ref reader, 16);
                    byte[] b = new byte[16];
                    if (!reader.TryCopyTo(b))
                        throw new Ceras.Exceptions.CerasDataCorruptionException(
                            "Corrupt data: the save ended before a 16-byte Guid could be read (truncated payload).");
                    reader.Advance(16);
                    return new Guid(b);
                }
                case WellKnownTypes.Type:
                    if (mode == WalkMode.Read) return ReadTypeValue(ref reader);
                    SkipTypeValue(ref reader);
                    return null;
                case WellKnownTypes.Object:
                    if (mode == WalkMode.Read)
                        return ReadPolymorphicValue(WellKnownTypes.Object, targetType, ref reader, existingObject);

                    SkipPolymorphicValue(WellKnownTypes.Object, ref reader);
                    return null;
                case WellKnownTypes.Array:
                    // The *declared* type is System.Array itself, and that declaration is a polymorphic
                    // slot exactly like System.Object above: it names no element type, so the writer
                    // reaches it through ReferenceFormatter<Array> and writes a reference marker, the
                    // concrete runtime type as a Type value, and then that type's own payload. (A
                    // *concrete* array is a different identity entirely — a manifest entry of kind
                    // GenericInstantiation with definition Array — and it is written by
                    // ArrayFormatter with no marker in front of it; see ReadGenericInstantiation.)
                    //
                    // This case used to fall through to the default branch, which treats an id below
                    // the manifest base it does not recognize as "nothing describable here" and
                    // returns without consuming a byte. So the slot came back null with its whole
                    // payload still in the stream, and every member after it decoded from the wrong
                    // offset — which the exact-consumption check then reports as a layout
                    // disagreement. The two readers have to agree on the *bytes*, and these are the
                    // bytes; the rank>1 refusal stays where it is, on the write side, because the
                    // rank is not in the type value either.
                    if (mode == WalkMode.Read)
                        return ReadPolymorphicValue(WellKnownTypes.Array, targetType, ref reader, existingObject);

                    SkipPolymorphicValue(WellKnownTypes.Array, ref reader);
                    return null;
                default:
                {
                    if (typeId < WellKnownTypes.UserTypeBase)
                    {
                        // Not a type this reader knows: it cannot be decoded, and it has no describable
                        // payload either, so there is nothing to account for.
                        return null;
                    }

                    if (mode == WalkMode.Read)
                        return ReadUserType(typeId, targetType, ref reader, existingObject);

                    SkipUserType(typeId, ref reader);
                    return null;
                }
            }
        }

        /// <summary>Consumes <paramref name="count"/> bytes. The result is always null: a skip produces no value.</summary>
        static object? Advance(ref SequenceReader<byte> reader, int count)
        {
            reader.Advance(count);
            return null;
        }

        object? ReadValue(int typeId, Type? targetType, ref SequenceReader<byte> reader, object? existingObject = null)
            => WalkValue(typeId, targetType, ref reader, existingObject, WalkMode.Read);

        void SkipValue(int typeId, ref SequenceReader<byte> reader)
            => WalkValue(typeId, null, ref reader, null, WalkMode.Skip);

        /// <summary>
        /// Decodes a <see cref="Type"/> value: the writer's recursive grammar, decoded by
        /// <see cref="TypeValueShapes"/> — the same decoder a polymorphic value's type tag uses, so the two
        /// can never describe the bytes differently. Reconstructing the closed type is required both to
        /// return the right value and to consume the argument bytes so the stream stays aligned.
        /// </summary>
        object? ReadTypeValue(ref SequenceReader<byte> reader)
        {
            int tag = SchemaSection.ReadTypeIdLeb128(ref reader, "a System.Type value's tag");
            if (tag == FullNameMarker)
                return _typeBinder.GetTypeFromBase(ReadStringFromTable(ref reader));

            return _typeValues.Materialize(_typeValues.Decode(tag, ref reader));
        }

        /// <summary>
        /// The current CLR type a manifest id from <b>this save's</b> id space names, resolved through the
        /// map the loader built from the file's schema section. That map already carries rename aliases, so
        /// an id the file used for a type this build calls something else answers with the current type —
        /// which is what a renamed type used as a nested generic argument needs.
        /// </summary>
        Type? ResolveManifestTypeId(int id)
        {
            var idToType = RobustContext.IdToType;
            return idToType != null && idToType.TryGetValue(id, out var resolved) ? resolved : null;
        }

        /// <summary>
        /// The current type a value of <paramref name="old"/>'s identity decodes into: the migration plan
        /// the file's own name resolves to, else the type a Type-value entry named, else the caller's
        /// declared type. A Type-value entry has no stable name in the file, so it has no plan; the type it
        /// names is what decides whether its payload sits behind a reference marker (a collection, an array
        /// or a class does; a <c>Nullable</c>, a tuple, a <c>KeyValuePair</c> and a struct do not).
        /// </summary>
        Type? CurrentTypeOf(OldTypeSchema old, Type? targetType)
        {
            if (_plans.TryGetValue(old.StableName, out var plan) && plan?.CurrentType != null)
                return plan.CurrentType;

            return _file.TypeValueCurrentType(old.TypeId) ?? targetType;
        }

        object? ReadUserType(int typeId, Type? targetType, ref SequenceReader<byte> reader, object? existingObject = null)
        {
            if (!_file.TryGetById(typeId, out var oldSchema)) {
                return null;
            }

            _plans.TryGetValue(oldSchema.StableName, out var plan);
            var currentType = CurrentTypeOf(oldSchema, targetType);

            if (oldSchema.Kind == SchemaTypeKind.Enum)
                return ReadEnumValue(oldSchema, targetType, plan, ref reader);

            // One decision, shared with the skipper: a struct, an enum and a value-typed closed generic are
            // written inline; everything else is behind a reference marker.
            if (!CarriesReferenceMarker(oldSchema, currentType))
                return ReadUserTypeDirect(typeId, targetType, ref reader, null, existingObject);

            return ReadPolymorphicValue(typeId, targetType, ref reader, existingObject);
        }

        /// <summary>
        /// Whether a value of this type carries a reference marker. <para>
        /// This is the decision the reader and the skipper must agree on first, because getting it wrong
        /// shifts the whole rest of the save: a marker read where a count was written (or the reverse)
        /// turns every following field into garbage. It is answered from the file's schema kind, refined
        /// by the <em>current</em> CLR type, because the marker is the writer's reference wrapper's doing —
        /// a <c>List&lt;T&gt;</c> member is written through that wrapper (marker, then count, then elements)
        /// while the same type as a built-in collection payload is not. Answering it from the file's
        /// definition id alone gets that backwards, which is what this predicate exists to prevent.
        /// </para>
        /// </summary>
        bool CarriesReferenceMarker(OldTypeSchema old, Type? currentType)
        {
            switch (old.Kind)
            {
                // A struct and an enum are written inline: no marker, and a struct may not even be nullable.
                case SchemaTypeKind.Struct:
                case SchemaTypeKind.Enum:
                    return false;

                case SchemaTypeKind.GenericInstantiation:
                    // A closed generic maps to a value type (a KeyValuePair, a tuple, a Nullable): written
                    // inline. A collection, an array or anything mapped to a reference type goes through the
                    // reference wrapper.
                    return currentType == null || !currentType.IsValueType;

                default:
                    // A class or interface is always behind a reference.
                    return true;
            }
        }

        object? ReadUserTypeDirect(int typeId, Type? targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object? existingObject = null)
        {
            if (!_file.TryGetById(typeId, out var oldSchema)) return null;

            if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation)
                return ReadGenericInstantiation(oldSchema, targetType, ref reader, cacheIndex, existingObject);

            return ReadUserTypeInstance(oldSchema, targetType, ref reader, cacheIndex, existingObject);
        }

        object? ReadPolymorphicValue(int expectedTypeId, Type? targetType, ref SequenceReader<byte> reader, object? existingObject = null)
        {
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, RefBias);
            if (marker == RefNull) return null;
            if (marker >= 0)
            {
                // 0.8.5: an index past the end of the identity cache is a corrupt/oversized marker, not a null.
                return _identity.Resolve(marker);
            }

            if (marker == RefInlineType)
            {
                return ReadValue(WellKnownTypes.Type, typeof(Type), ref reader);
            }

            int typeId;
            if (marker == RefNewValue)
            {
                typeId = SchemaSection.ReadTypeIdLeb128(ref reader, "a polymorphic value's type tag");

                // ── A tag below the manifest base is a *Type value*, not a schema type id. ──────────────
                // RobustTypeFormatter records the concrete runtime type of a polymorphic value as a Type
                // value: a well-known id followed, for a definition, by its argument type values. Manifest
                // ids start at UserTypeBase and the file's schema section never holds a well-known id, so
                // the tag alone decides which of the three it is. Reading a well-known tag as a schema id is
                // what made a closed built-in generic, an array, a Nullable, a KeyValuePair or a tuple in a
                // polymorphic slot come back null with its payload left in the stream, desynchronising every
                // field after it.
                if (typeId < WellKnownTypes.UserTypeBase && typeId != FullNameMarker)
                {
                    int typeValueSlot = _identity.Reserve(existingObject);
                    return ReadTypeValuePayload(typeId, ref reader, typeValueSlot, existingObject);
                }
            }
            else if (marker == RefNewValueSameType)
            {
                // "Same type" means the writer found the declared type exact, so it wrote no tag — and a
                // type's payload is whatever *its own* formatter writes. The one well-known identity that
                // reaches this path is System.Object: every other reference-typed declared member has a
                // manifest id, and a declared scalar, string, array or collection is walked by its own id
                // instead of through a marker. `object` has no serializable members at all, so the writer
                // wrote the marker and *nothing else* — `new object()` in an `object` slot is one byte, and
                // an `object` root is two. Read that here instead of refusing it: the empty payload is the
                // whole grammar for this identity, and a fresh instance is what the writer's object cache
                // holds for it.
                if (expectedTypeId == WellKnownTypes.Object) return ReadEmptyObject(existingObject);

                if (expectedTypeId < WellKnownTypes.UserTypeBase)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt save: a value behind a same-type marker declares the well-known type id {expectedTypeId}, " +
                        "whose payload this engine does not read through a reference marker. A declared scalar, string, array " +
                        "or collection is written by its own id, so the writer never puts one here.");

                typeId = expectedTypeId;
            }
            else
            {
                throw new Ceras.Exceptions.CerasException($"Invalid reference marker {marker}");
            }

            // The slot is reserved before the payload is read, so a cycle back into this object resolves.
            int cacheIndex = _identity.Reserve(existingObject);

            if (typeId == FullNameMarker)
            {
                // 0.8.5: this used to allocate an uninitialized instance, run the parameterless ctor and
                // return it *without consuming a single payload byte* - silent data loss for the object
                // plus a stream desync for everything after it. A name that resolves to a type the
                // current manifest describes is still readable (it is a real, described type); anything
                // else is rejected, because this engine cannot know how the bytes that follow are laid
                // out and guessing would corrupt the rest of the graph.
                string typeName = ReadStringFromTable(ref reader);
                Type? type = ResolveFullName(typeName);
                if (type == null)
                    throw new Ceras.Exceptions.CerasSchemaException(
                        $"The save names the type '{typeName}' for a full-name reference, but it does not resolve in this " +
                        "application. A reference to an undescribed type cannot be read: the bytes that follow are laid out by " +
                        "that type's own formatter, and this engine has no schema for it.");

                if (_registry.TryGetId(type, out int actualId) && _file.TryGetById(actualId, out var registeredSchema))
                {
                    var obj = ReadUserTypeInstance(registeredSchema, targetType, ref reader, cacheIndex, existingObject);
                    _identity.Publish(cacheIndex, obj);
                    return obj;
                }

                throw new Ceras.Exceptions.CerasSchemaException(
                    $"The save references '{TypeIdentity.GetCanonicalNameOrUnknown(type)}' by full name, but that type is not part of the " +
                    "current context's manifest, so its payload cannot be decoded. Declare it in the context " +
                    "([CerasContext]/[CerasKnownType]) and rebuild; loading the save as a type that is merely structurally similar " +
                    "would desynchronise the rest of the graph.");
            }

            // A tag at or above the manifest base is a schema type id, and the file's own schema section is
            // the only thing that says how such a value's payload is written. A real writer's id always
            // comes from its own manifest, so one the section does not describe has no describable payload:
            // refused, rather than read as a value of no bytes.
            if (!_file.ContainsById(typeId))
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Corrupt save: a polymorphic value names type id {typeId}, which the save's own schema section does not " +
                    "describe, so nothing says how its payload is written and the rest of the save could not be read safely.");

            return ReadUserTypeDirect(typeId, targetType, ref reader, cacheIndex, existingObject);
        }

        /// <summary>
        /// Reads the payload of a concrete type the file named with a <c>Type</c> value behind a reference
        /// marker. The tag has already been consumed.
        /// <para>
        /// A closed built-in generic, an array, a <c>Nullable</c>, a <c>KeyValuePair</c> and a tuple all
        /// reduce to a <c>GenericInstantiation</c> entry with a definition id and ordered argument ids —
        /// exactly the shape <see cref="ClassifyPayload"/> and <see cref="ReadGenericInstantiation"/> consume,
        /// and exactly the shape the file's own schema section writes for a collection member, so all of
        /// them (including every nesting level of them) take the same path a declared member takes, with the
        /// same reference markers, element-count encodings and size limits.
        /// </para>
        /// <para>
        /// A terminal tag — a well-known scalar, or a manifest id for a user type — keeps the ordinary
        /// per-id walk, which is already exactly right for it: a user type's payload is its schema's, and a
        /// scalar's is its fixed layout.
        /// </para>
        /// </summary>
        object? ReadTypeValuePayload(int tag, ref SequenceReader<byte> reader, int cacheIndex, object? existingObject)
        {
            var shape = _typeValues.Decode(tag, ref reader);

            if (!shape.DescribesPayload)
            {
                // A tag of System.Object would mean the writer decided the declared type was *not* object
                // while the runtime type was, and nothing derives from object except object itself — so the
                // writer cannot emit it (a declared `object` holding `new object()` takes the same-type
                // marker and no tag at all). Refused as corruption, with that reason, rather than read
                // through the object reader, which would look for a marker that was never written.
                if (shape.DefinitionId == WellKnownTypes.Object)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        "Corrupt save: a polymorphic value carries System.Object as a type tag. The writer emits a tag only " +
                        "when the declared and runtime types differ, and nothing derives from System.Object except itself.");

                var scalar = ReadValue(shape.DefinitionId, shape.ClrType, ref reader, existingObject);
                _identity.Publish(cacheIndex, scalar);
                return scalar;
            }

            return ReadGenericInstantiation(_typeValues.InternRoot(shape), shape.ClrType, ref reader, cacheIndex, existingObject);
        }

        /// <summary>
        /// Reads a value whose declared type is <see cref="object"/>: the writer's own formatter for
        /// <c>object</c> has no members, so its payload is empty, and the object-cache slot it reserved is
        /// the identity the writer recorded. A supplied instance is reused in place, exactly as the
        /// generated reference reader reuses a non-null one.
        /// </summary>
        object ReadEmptyObject(object? existingObject)
        {
            int slot = _identity.Reserve(existingObject);
            var value = existingObject ?? new object();
            _identity.Publish(slot, value);
            return value;
        }

        /// <summary>
        /// Resolves a type named by a full-name reference, through the configured binder. A binder reports
        /// "no such type" either by returning null or by throwing (SimpleTypeBinder throws), so both are
        /// normalized to null and the caller decides what an unresolvable name means.
        /// </summary>
        Type? ResolveFullName(string typeName)
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

        object? ReadUserTypeByStableName(string stableName, Type targetType, ref SequenceReader<byte> reader, object existingObject = null!)
        {
            if (stableName != null && _file.TryGetByName(stableName, out var oldSchema))
            {
                return ReadUserType(oldSchema.TypeId, targetType, ref reader, existingObject);
            }
            return null;
        }

        object? ReadUserTypeInstance(OldTypeSchema oldSchema, Type? targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object? existingObject = null)
        {
            _plans.TryGetValue(oldSchema.StableName, out var plan);
            Type? currentType = plan?.CurrentType ?? targetType;

            RejectUndescribableShape(oldSchema, currentType);

            // Reuse is only valid for an exact runtime-type match; a subtype/supertype mismatch must
            // allocate a new instance (matching the generated reference wrapper).
            if (existingObject != null && !CurrentObjectFactory.IsExactType(existingObject, currentType))
                existingObject = null;

            if (oldSchema.Kind == SchemaTypeKind.Struct || (currentType != null && currentType.IsValueType))
            {
                object? structObj = existingObject ?? _factory.CreateInstance(currentType);
                _identity.Publish(cacheIndex, structObj);

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

            return ReadReferenceInstance(oldSchema, plan, targetType, currentType, ref reader, cacheIndex, existingObject);
        }


        /// <summary>
        /// Reads a reference-typed value, matching the generated reader's construction contract:
        /// <list type="number">
        /// <item>allocate — through the configured construction when one applies (see
        /// <see cref="CurrentObjectFactory"/>), otherwise uninitialized when a parameterized
        /// <c>[CerasConstructor]</c> is about to supply the values, and otherwise the allocation the type's
        /// own formatter performs. In the constructor-argument case the instance is registered in the object
        /// cache <em>before</em> the constructor runs, so a cycle through a constructor parameter
        /// resolves;</item>
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
        object? ReadReferenceInstance(OldTypeSchema oldSchema, TypeMigrationPlan? plan, Type? declaredType,
            Type? currentType, ref SequenceReader<byte> reader, int? cacheIndex, object? existingObject)
        {
            // A reused instance is never re-constructed: no constructor, no field initializers, no pool.
            var ctorArgs = existingObject == null
                ? CtorArgumentPlanBuilder.Resolve(currentType, plan)
                : null;

            object? obj = ctorArgs != null
                ? _factory.CreateWithConstructorArguments(declaredType, currentType)
                : existingObject ?? _factory.CreateReference(declaredType, currentType);

            _identity.Publish(cacheIndex, obj);

            FireBeforeCallback(obj, plan);

            if (TryReadCustomFormatter(currentType, ref obj, ref reader))
            {
                EnqueueAfterCallback(obj, plan);
                return obj;
            }

            if (ctorArgs != null)
            {
                var values = new object?[ctorArgs.Parameters.Length];
                var assignments = new List<(FieldInfo Field, object? Value)>();
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
                        $"The [CerasConstructor] of '{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}' threw while migrating a save " +
                        $"(payload fields: {CtorArgumentPlanBuilder.DescribeOldFields(plan)}): {e.InnerException.GetType().Name}: {e.InnerException.Message}",
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

        /// <summary>
        /// Reads one element of a collection payload, or consumes and discards it when the file did not
        /// record the element's type identity. A fixed-size element can still be skipped from its current
        /// CLR type; anything else fails loudly rather than desyncing the stream.
        /// </summary>
        object? SkipAndReturnDefault(Type? elementType, ref SequenceReader<byte> reader)
        {
            int fixedSize = elementType != null && WellKnownTypes.TryGetId(elementType, out int wellKnownId)
                ? WellKnownTypes.GetFixedSize(wellKnownId)
                : 0;
            if (fixedSize > 0)
            {
                // elementType is non-null here: a fixed size is only known for a registered type.
                reader.Advance(fixedSize);
                return elementType!.IsValueType ? Activator.CreateInstance(elementType) : null;
            }

            throw new Ceras.Exceptions.CerasSchemaException(
                $"Cannot decode a collection of '{elementType?.FullName ?? "unknown element type"}': the save file did not record the element " +
                "type identity, and the current element type has no fixed size to skip. This save was written with a layout Ceras cannot migrate; " +
                "load it with the build that wrote it.");
        }


        object? ReadGenericInstantiation(OldTypeSchema oldSchema, Type? targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object? existingObject = null)
        {
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Array) return ReadArray(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.KeyValuePair) return ReadKeyValuePair(oldSchema, targetType, ref reader, cacheIndex, existingObject);
            if (oldSchema.DefinitionTypeId == WellKnownTypes.Nullable) return ReadNullable(oldSchema, targetType, ref reader);
            if (WellKnownTypes.IsTupleDefinition(oldSchema.DefinitionTypeId)) return ReadTuple(oldSchema, targetType, ref reader, cacheIndex, existingObject);

            // Arbitrary generic class or struct
            var currentType = CurrentTypeOf(oldSchema, targetType);
            _plans.TryGetValue(oldSchema.StableName, out var plan);

            // A collection payload is written by the built-in collection formatter as an explicit count
            // plus its elements/entries, so every collection is read through the single collection
            // reader, never through the ordinary user-type path (which would consume a reference marker
            // that was never written). This check must come BEFORE any well-known definition dispatch: a
            // collection subclass and a shape with no public parameterless constructor
            // (ReadOnlyCollection<T>, ReadOnlyDictionary<K,V>) both bake the reserved definition id of the
            // built-in collection they are described as, so dispatching on that id first would pick a
            // reader for the wrong shape and then fail to construct the instance.
            if (CurrentObjectFactory.IsCollectionShape(currentType))
                return ReadCollectionShape(currentType, targetType, oldSchema, ref reader, cacheIndex, existingObject);

            // The file describes a collection payload, but this build has no concrete collection type for
            // it (an unresolvable old name, or a polymorphic slot read as object). Consume the payload so
            // the stream stays aligned and report no value.
            if (IsCollectionPayload(oldSchema.DefinitionTypeId))
                return ReadUnknownCollectionPayload(oldSchema, ref reader);

            if (existingObject != null && !CurrentObjectFactory.IsExactType(existingObject, currentType))
                existingObject = null;

            if (currentType != null && currentType.IsValueType)
            {
                object? structObj = existingObject ?? _factory.CreateInstance(currentType);
                _identity.Publish(cacheIndex, structObj);

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

            return ReadReferenceInstance(oldSchema, plan, targetType, currentType, ref reader, cacheIndex, existingObject);
        }

        /// <summary>
        /// <see cref="Nullable{T}"/> layout: one presence byte, then the element value when present
        /// (matching <c>NullableFormatter&lt;T&gt;</c>). Returns the boxed element value, or null.
        /// </summary>
        object? ReadNullable(OldTypeSchema oldSchema, Type? targetType, ref SequenceReader<byte> reader)
        {
            int hasValue = SerializerBinary.ReadByte(ref reader);
            int? innerId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;

            if (hasValue == 0)
                return null;

            Type? innerType = targetType == null ? null : Nullable.GetUnderlyingType(targetType);
            if (innerType == null || innerId == null)
            {
                // No current element representation: still consume the element to keep alignment.
                if (innerId.HasValue) SkipValue(innerId.Value, ref reader);
                return null;
            }

            return ReadValue(innerId.Value, innerType, ref reader);
        }

        object? ReadKeyValuePair(OldTypeSchema oldSchema, Type? targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object? existingObject = null)
        {
            Type? keyType = targetType != null && targetType.IsGenericType ? targetType.GetGenericArguments()[0] : null;
            Type? valType = targetType != null && targetType.IsGenericType ? targetType.GetGenericArguments()[1] : null;
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
            object? kvp = Activator.CreateInstance(targetType, key, val);
            // KeyValuePair is a value type written with no reference marker and is never registered by
            // the writer, so only fill a slot the writer actually created.
            _identity.Publish(cacheIndex, kvp);
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
        object? ReadTuple(OldTypeSchema oldSchema, Type? targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object? existingObject = null)
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
                _identity.Publish(cacheIndex, tuple);

                var itemFields = GetTupleItemFields(targetType);
                for (int i = 0; i < count; i++)
                {
                    Type? itemType = i < argTypes.Length ? argTypes[i] : null;
                    var value = ReadValue(oldSchema.ArgTypeIds[i], itemType, ref reader);
                    if (itemFields != null && i < itemFields.Length && itemFields[i] != null)
                        itemFields[i].SetValue(tuple, value);
                }
                return tuple;
            }

            var values = new object?[count];
            for (int i = 0; i < count; i++)
            {
                int oldItemId = oldSchema.ArgTypeIds[i];
                Type? itemType = i < argTypes.Length ? argTypes[i] : null;
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
                    $"Corrupt payload: constructing the value tuple '{TypeIdentity.GetCanonicalNameOrUnknown(targetType)}' from the stored items failed: " +
                    $"{e.InnerException.Message}", e.InnerException);
            }
            catch (ArgumentException e)
            {
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt payload: the stored items do not form a valid '{TypeIdentity.GetCanonicalNameOrUnknown(targetType)}': {e.Message}", e);
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

        object? ReadArray(OldTypeSchema oldSchema, Type? targetType, ref SequenceReader<byte> reader, int? cacheIndex = null, object? existingObject = null)
        {
            // Multidimensional arrays are written by MultiDimensionalArrayFormatter (rank + per-dimension
            // lengths + flattened elements), which this structural reader does not decode. Reject them
            // loudly rather than misreading the payload as a single length.
            if (targetType != null && targetType.IsArray && targetType.GetArrayRank() != 1)
                throw SchemaRegistry.UnsupportedMultidimensionalArray(targetType, "the migration interpreter");

            int count = ReadCollectionCount(WellKnownTypes.Array, ref reader);
            if (count == -1) return null;

            Type? elementType = targetType?.IsArray == true ? targetType.GetElementType() : null;
            int? oldElemTypeId = oldSchema.ArgTypeIds.Length > 0 ? (int?)oldSchema.ArgTypeIds[0] : null;
            if (elementType == null || oldElemTypeId == null) { SkipElements((uint)count, oldElemTypeId, ref reader); return null; }

            var arr = existingObject as Array;
            if (arr == null || arr.Length != count || (targetType != null && arr.GetType() != targetType))
                arr = Array.CreateInstance(elementType, (int)count);

            // Only fill an object-cache slot that the writer actually created. Monomorphic array
            // members are written by ArrayFormatter with no reference marker and are never
            // registered by the writer, so adding a slot here would shift every later back-reference.
            _identity.Publish(cacheIndex, arr);

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
        /// Rejects the saves written before the format learned to describe collection and tuple shapes
        /// structurally. Those files record such a value as an ordinary class schema with <b>no fields</b>
        /// (the shape's payload was never described), while the bytes are a count plus elements. Reading
        /// them as a field-less class consumes nothing, so the interpreter silently desyncs and every
        /// following field decodes as garbage. Failing loudly turns that silent corruption into an
        /// actionable error; such saves are not migratable (see Format/LAYOUT.md).
        /// </summary>
        static void RejectUndescribableShape(OldTypeSchema oldSchema, Type? currentType)
        {
            if (oldSchema == null || currentType == null) return;
            if (oldSchema.Kind != SchemaTypeKind.Class && oldSchema.Kind != SchemaTypeKind.Struct) return;
            if (oldSchema.Fields == null || oldSchema.Fields.Length != 0) return;
            if (oldSchema.DefinitionTypeId != 0) return; // a definition id means it was described structurally

            bool describableNow = CurrentObjectFactory.IsCollectionShape(currentType)
                || WellKnownTypes.IsWellKnownStructuralValueType(currentType);

            if (describableNow)
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot migrate the value of '{oldSchema.StableName}': the save file describes it as a class with no members, which is how " +
                    $"Ceras recorded a collection or tuple before the format could describe those shapes. Its payload is a count plus elements, " +
                    $"which that schema does not describe, so reading it would silently desynchronize the rest of the save. " +
                    $"'{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}' is described structurally now, so files written by this version onward " +
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
        /// What a file's schema entry says the payload of a generic instantiation is, once it is known that
        /// there is no reference marker in front of it. The file's definition id decides this, never the
        /// current CLR type: a rename, a re-assigned id or a changed shape must still decode by the layout
        /// the writer used, and the current type only supplies the CLR types to decode into.
        /// <para>
        /// Written once for both directions. The skipper used to re-derive the same grammar from a different
        /// question — "is the current type a value type?", then "is the definition id a dictionary?" — so a
        /// shape could be read one way and skipped another.
        /// </para>
        /// </summary>
        enum PayloadShape
        {
            /// <summary>Not one of the described structural shapes.</summary>
            Unknown,

            Array,
            KeyValuePair,
            Nullable,
            Tuple,

            /// <summary>An explicit count followed by that many elements.</summary>
            Elements,

            /// <summary>An explicit count followed by that many key/value entries.</summary>
            Entries,
        }

        /// <summary>Classifies a file's generic instantiation. Reads no payload.</summary>
        static PayloadShape ClassifyPayload(OldTypeSchema old)
        {
            int definition = old.DefinitionTypeId;

            if (definition == WellKnownTypes.Array) return PayloadShape.Array;
            if (definition == WellKnownTypes.KeyValuePair) return PayloadShape.KeyValuePair;
            if (definition == WellKnownTypes.Nullable) return PayloadShape.Nullable;
            if (WellKnownTypes.IsTupleDefinition(definition)) return PayloadShape.Tuple;
            if (definition == WellKnownTypes.Dictionary) return PayloadShape.Entries;
            if (definition == WellKnownTypes.List || definition == WellKnownTypes.HashSet
                || definition == WellKnownTypes.Queue || definition == WellKnownTypes.Stack) return PayloadShape.Elements;

            return PayloadShape.Unknown;
        }

        /// <summary>True when the file describes this id as an array instantiation.</summary>
        bool IsArrayPayload(int typeId)
            => _file.TryGetById(typeId, out var schema)
            && schema.Kind == SchemaTypeKind.GenericInstantiation
            && schema.DefinitionTypeId == WellKnownTypes.Array;

        /// <summary>The file's schema for an id, or an empty placeholder when the file did not describe it.</summary>
        OldTypeSchema SkipSchemaOf(int typeId)
            => _file.TryGetById(typeId, out var schema) ? schema : EmptySchema;

        static readonly OldTypeSchema EmptySchema = new OldTypeSchema(
            0, "", Array.Empty<string>(), SchemaTypeKind.Class, Array.Empty<OldFieldSchema>(), 0, 0, null!, null!);

        /// <summary>The old type's argument id at <paramref name="index"/>, or null when it did not record one.</summary>
        static int? ArgId(OldTypeSchema old, int index)
            => old.ArgTypeIds != null && old.ArgTypeIds.Length > index ? (int?)old.ArgTypeIds[index] : null;

        /// <summary>
        /// Whether a payload written for <paramref name="definitionTypeId"/> starts with a fixed, <b>unbiased</b>
        /// 4-byte little-endian element count instead of the shared bias-<c>+1</c> varint.
        /// <para>
        /// <c>QueueFormatter</c> and <c>StackFormatter</c> write their count through the injected
        /// <c>IFormatter&lt;int&gt;</c>, which is the fixed 4-byte little-endian <c>int</c> encoding
        /// (<c>Int32Formatter</c>), and they have no null marker. Every other collection —
        /// <c>ArrayFormatter</c>, <c>CollectionFormatter</c> for <c>List</c>/<c>HashSet</c>/<c>Dictionary</c>
        /// and every collection subclass — writes <c>WriteUInt32Bias(count, 1)</c>, where <c>-1</c> is a null
        /// collection.
        /// </para>
        /// <para>
        /// The two encodings are indistinguishable for the small counts a test save usually holds, which is
        /// exactly why this matters: reading a queue or a stack with the shared biased read does not fail, it
        /// returns a plausible count, consumes the wrong number of bytes and desynchronises everything after it.
        /// </para>
        /// </summary>
        static bool UsesFixedElementCount(int definitionTypeId)
            => definitionTypeId == WellKnownTypes.Queue || definitionTypeId == WellKnownTypes.Stack;

        /// <summary>
        /// The element count that introduces a described collection payload, plus the bound check. One reader
        /// for every shape and both directions, so the count's encoding and its limit cannot drift between them.
        /// <para>
        /// The encoding is chosen by the <em>file's</em> definition id, never by the current CLR type, for the
        /// same reason the rest of the payload grammar is: a rename, a re-assigned id or a changed shape must
        /// still decode by the layout the writer used.
        /// </para>
        /// <para>
        /// Returns <c>-1</c> for an encoded null collection, which only the biased encodings can express — a
        /// queue or a stack cannot be written as null at all, so a negative count there is corruption rather
        /// than a null.
        /// </para>
        /// </summary>
        int ReadCollectionCount(int definitionTypeId, ref SequenceReader<byte> reader)
        {
            if (UsesFixedElementCount(definitionTypeId))
            {
                int fixedCount = SerializerBinary.ReadInt32Fixed(ref reader);
                if (fixedCount < 0 || fixedCount > _maxCollectionSize)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt save: a {(definitionTypeId == WellKnownTypes.Queue ? "queue" : "stack")} declares {fixedCount} elements, " +
                        $"outside the configured maximum of {_maxCollectionSize}. Its count is a plain 4-byte element count, " +
                        "so a negative one is a corrupt payload, not a null collection.");
                return fixedCount;
            }

            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return -1;                      // a null collection
            if (count < 0 || count > _maxCollectionSize)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt save: a collection declares {count} elements, outside the configured maximum of {_maxCollectionSize}.");
            return count;
        }

        /// <summary>
        /// Accounts for a collection payload: an explicit count followed by that many elements or key/value
        /// entries, each count read by <see cref="ReadCollectionCount"/> so the queue/stack encoding is honoured
        /// here too.
        /// </summary>
        void SkipCollectionPayload(PayloadShape shape, OldTypeSchema old, ref SequenceReader<byte> reader)
        {
            int count = ReadCollectionCount(old.DefinitionTypeId, ref reader);
            if (count == -1) return;                          // a null collection

            if (shape == PayloadShape.Entries)
            {
                int? keyId = ArgId(old, 0);
                int? valueId = ArgId(old, 1);
                for (int i = 0; i < count; i++)
                {
                    if (keyId.HasValue) SkipValue(keyId.Value, ref reader);
                    if (valueId.HasValue) SkipValue(valueId.Value, ref reader);
                }
            }
            else
            {
                SkipElements((uint)count, ArgId(old, 0), ref reader);
            }
        }

        /// <summary>
        /// The single collection reader. The file's definition id decides the <em>payload</em> (an explicit
        /// count plus elements/entries, exactly like <c>CollectionFormatter</c>/<c>QueueFormatter</c>/
        /// <c>StackFormatter</c> write), while the current type supplies the CLR element/key/value types
        /// and the instance to fill. Using the file's identity for the layout and the current type for the
        /// CLR side means a rename, an id reassignment or a changed shape still decodes by shape rather
        /// than by id.
        /// <para>
        /// A collection shape the current build cannot describe is refused here, before a single payload
        /// byte is consumed: the file's count says how many bytes follow, and reading on with no way to
        /// put the elements anywhere would desynchronize the rest of the save. It is a
        /// <see cref="Ceras.Exceptions.CerasSchemaException"/> and not a corruption error because the bytes
        /// are irrelevant — it is the *current* type (this build's model) that has no describable shape,
        /// so reporting the user's data as corrupt would send them looking in the wrong place.
        /// </para>
        /// </summary>
        object? ReadCollectionShape(Type? currentType, Type? targetType, OldTypeSchema oldSchema, ref SequenceReader<byte> reader, int? cacheIndex, object? existingObject)
        {
            if (!WellKnownTypes.TryResolveCollectionShape(currentType, out var shapeDef, out var shapeArgs, out var shapeRejection))
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot decode the collection the save file records as '{oldSchema.StableName}': its current type is one Ceras " +
                    $"cannot describe, so the element count that follows cannot be read into anything and the rest of the save could not " +
                    $"be read safely. {WellKnownTypes.DescribeCollectionShapeRejection(currentType, shapeRejection)}");

            if (!IsCollectionPayload(oldSchema.DefinitionTypeId))
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot decode '{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}': the save file describes it as type id " +
                    $"{oldSchema.DefinitionTypeId}, which is not a collection payload. Ceras cannot tell how many bytes its " +
                    "elements occupy, so the rest of the save cannot be read safely.");

            int count = ReadCollectionCount(oldSchema.DefinitionTypeId, ref reader);
            if (count == -1) return null;

            int? oldElem0 = ArgId(oldSchema, 0);
            int? oldElem1 = ArgId(oldSchema, 1);

            // The resolve above succeeded, so the shape's arguments are there; ArgId-style guards keep the
            // arity honest if a file ever records fewer.
            var args = shapeArgs!;
            Type? elementType = args.Length > 0 ? args[0] : null;
            Type? keyType = args.Length > 0 ? args[0] : null;
            Type? valueType = args.Length > 1 ? args[1] : null;

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
        object? ReadUnknownCollectionPayload(OldTypeSchema oldSchema, ref SequenceReader<byte> reader)
        {
            int count = ReadCollectionCount(oldSchema.DefinitionTypeId, ref reader);
            if (count == -1) return null;

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

        object? ReadListShape(Type? currentType, Type? elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object? existingObject)
        {
            var fill = _factory.CreateCollection(currentType, typeof(List<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "list");
            CacheCollection(fill, cacheIndex);

            var add = CurrentObjectFactory.ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the collection type '{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}' while migrating: it has no usable " +
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

        object? ReadDictionaryShape(Type? currentType, Type? keyType, Type? valType, int? oldKeyId, int? oldValId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object? existingObject)
        {
            var fill = _factory.CreateCollection(currentType, typeof(Dictionary<,>), new[] { keyType, valType },
                existingObject, "Clear", "dictionary");
            CacheCollection(fill, cacheIndex);

            var add = CurrentObjectFactory.ResolveEntryAdder(fill.Target, keyType, valType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the dictionary type '{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}' while migrating: it has no usable " +
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

        object? ReadHashSetShape(Type? currentType, Type? elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object? existingObject)
        {
            var fill = _factory.CreateCollection(currentType, typeof(HashSet<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "set");
            CacheCollection(fill, cacheIndex);

            var add = CurrentObjectFactory.ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the set type '{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}' while migrating: it has no usable " +
                    $"'Add({elementType?.Name ?? "element"})' method.");

            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                add(fill.Target, Coerce(val, coerce, elementType));
            }
            return fill.Result;
        }

        object? ReadQueueShape(Type? currentType, Type? elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object? existingObject)
        {
            var fill = _factory.CreateCollection(currentType, typeof(Queue<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "queue");
            CacheCollection(fill, cacheIndex);

            var enqueue = CurrentObjectFactory.ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the queue type '{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}' while migrating: it has no usable " +
                    $"'Enqueue({elementType?.Name ?? "element"})' method.");

            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                enqueue(fill.Target, Coerce(val, coerce, elementType));
            }
            return fill.Result;
        }

        object? ReadStackShape(Type? currentType, Type? elementType, int? oldElemId, int count, ref SequenceReader<byte> reader, int? cacheIndex, object? existingObject)
        {
            var values = new object?[count];
            var coerce = oldElemId.HasValue ? MigrationPlanBuilder.DetectCoercion(oldElemId.Value, elementType) : CoercionKind.None;
            for (int i = 0; i < count; i++)
            {
                var val = oldElemId.HasValue ? ReadValue(oldElemId.Value, elementType, ref reader) : SkipAndReturnDefault(elementType, ref reader);
                values[i] = Coerce(val, coerce, elementType);
            }

            var fill = _factory.CreateCollection(currentType, typeof(Stack<>), elementType == null ? null : new[] { elementType },
                existingObject, "Clear", "stack");
            CacheCollection(fill, cacheIndex);

            var push = CurrentObjectFactory.ResolveElementAdder(fill.Target, elementType)
                ?? throw new Ceras.Exceptions.CerasSchemaException(
                    $"Cannot refill the stack type '{TypeIdentity.GetCanonicalNameOrUnknown(currentType)}' while migrating: it has no usable " +
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
        void CacheCollection(CollectionFill fill, int? cacheIndex) => _identity.Publish(cacheIndex, fill.Result);

        /// <summary>
        /// Skips a <see cref="Type"/> value written by <c>RobustTypeFormatter</c>, through the same decoder
        /// the reader uses: a type value is pure structure, so consuming it is decoding it and discarding
        /// the result, and sharing the decoder is what keeps the two from ever disagreeing about how many
        /// bytes one occupies.
        /// </summary>
        void SkipTypeValue(ref SequenceReader<byte> reader)
        {
            int tag = SchemaSection.ReadTypeIdLeb128(ref reader, "a System.Type value's tag");
            if (tag == FullNameMarker)
            {
                ReadStringFromTable(ref reader);
                return;
            }

            _typeValues.Decode(tag, ref reader);
        }

        /// <summary>
        /// Accounts for a described user type, one kind at a time. The fixed-size short-circuit is
        /// <em>not</em> repeated here: <see cref="WalkValue"/> is this method's only caller and has already
        /// applied it, which is the point of having one entry point.
        /// </summary>
        void SkipUserType(int typeId, ref SequenceReader<byte> reader)
        {
            if (!_file.TryGetById(typeId, out var oldSchema)) return;

            switch (oldSchema.Kind)
            {
                case SchemaTypeKind.Struct: foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, ref reader); break;
                case SchemaTypeKind.Class: SkipPolymorphicValue(typeId, ref reader); break;
                case SchemaTypeKind.Enum: SkipEnumValue(oldSchema, ref reader); break;
                case SchemaTypeKind.GenericInstantiation:
                {
                    // The same two shared decisions the reader makes, in the same order: is there a marker,
                    // and if not, what is the payload?
                    if (CarriesReferenceMarker(oldSchema, CurrentTypeOf(oldSchema, null)))
                    {
                        SkipPolymorphicValue(typeId, ref reader);
                        break;
                    }

                    SkipUnmarkedPayload(oldSchema, ref reader);
                    break;
                }
            }
        }

        /// <summary>
        /// Accounts for a described value that carries no reference marker, dispatching on the same
        /// <see cref="ClassifyPayload"/> answer <see cref="ReadGenericInstantiation"/> dispatches on, so a
        /// value can never be read one way and skipped the other. The two callers are a described member
        /// with no marker in front of it, and a polymorphic value whose <c>Type</c> value named the type.
        /// </summary>
        void SkipUnmarkedPayload(OldTypeSchema oldSchema, ref SequenceReader<byte> reader)
        {
            switch (ClassifyPayload(oldSchema))
            {
                case PayloadShape.Nullable:
                    if (SerializerBinary.ReadByte(ref reader) != 0)
                    {
                        int? innerId = ArgId(oldSchema, 0);
                        if (innerId.HasValue) SkipValue(innerId.Value, ref reader);
                    }
                    return;

                case PayloadShape.Tuple:
                    foreach (var argId in oldSchema.ArgTypeIds) SkipValue(argId, ref reader);
                    return;

                case PayloadShape.Array:
                case PayloadShape.Elements:
                case PayloadShape.Entries:
                    SkipCollectionPayload(ClassifyPayload(oldSchema), oldSchema, ref reader);
                    return;

                default:
                    // A generic instantiation whose *definition* the well-known set does not name is a user
                    // generic class or struct, whose payload is its members - not a count followed by
                    // elements, which is what reading a member count here used to do. This is also the exact
                    // shape a schema the file really baked takes, so read and skip agree on both.
                    foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, ref reader);
                    return;
            }
        }

        void SkipPolymorphicValue(int expectedTypeId, ref SequenceReader<byte> reader)
        {
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, RefBias);
            if (marker == RefNull || marker >= 0) return;
            if (marker == RefInlineType) { SkipValue(WellKnownTypes.Type, ref reader); return; }

            int typeId;
            if (marker == RefNewValue)
            {
                typeId = SchemaSection.ReadTypeIdLeb128(ref reader, "a polymorphic value's type tag");

                // A well-known tag is a *Type value* (see ReadPolymorphicValue), so it is decoded as one
                // and the payload it names is accounted for by the representation that definition names,
                // through the very same ClassifyPayload answer the reader and SkipUserType give. A tag and
                // a schema entry of the same shape are therefore skipped the same way, nesting included.
                if (typeId < WellKnownTypes.UserTypeBase && typeId != FullNameMarker)
                {
                    _identity.Reserve(null);
                    SkipTypeValuePayload(typeId, ref reader);
                    return;
                }
            }
            else
            {
                // The mirror of the reader: a declared System.Object has an empty payload, so the marker
                // plus the object-cache slot it reserved is the whole of it.
                if (expectedTypeId == WellKnownTypes.Object)
                {
                    _identity.Reserve(null);
                    return;
                }

                if (expectedTypeId < WellKnownTypes.UserTypeBase)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt save: a value behind a same-type marker declares the well-known type id {expectedTypeId}, " +
                        "whose payload this engine does not account for through a reference marker. A declared scalar, string, " +
                        "array or collection is written by its own id, so the writer never puts one here.");

                typeId = expectedTypeId;
            }

            _identity.Reserve(null); // keep the index space in sync with the writer's, without building anything

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

            if (_file.TryGetById(typeId, out var oldSchema))
            {
                if (oldSchema.Kind == SchemaTypeKind.Enum) SkipEnumValue(oldSchema, ref reader);
                else if (oldSchema.Kind == SchemaTypeKind.GenericInstantiation) SkipUnmarkedPayload(oldSchema, ref reader);
                else foreach (var f in oldSchema.Fields) SkipValue(f.TypeId, ref reader);
            }
            else
            {
                // The mirror of the reader's guard: a value whose payload nothing describes cannot be
                // accounted for, so it is refused rather than skipped as if it were empty.
                throw new Ceras.Exceptions.CerasSchemaException(
                    $"Corrupt save: a polymorphic value at a position that has to be skipped names type id {typeId}, which the " +
                    "save's own schema section does not describe, so its payload cannot be accounted for.");
            }
        }

        /// <summary>
        /// Accounts for the payload of a type the file named with a <c>Type</c> value behind a reference
        /// marker. The tag has already been read; the rest of the type value is decoded first, because the
        /// definition it names is what says how the payload is written.
        /// </summary>
        void SkipTypeValuePayload(int tag, ref SequenceReader<byte> reader)
        {
            var shape = _typeValues.Decode(tag, ref reader);

            if (!shape.DescribesPayload)
            {
                // The reader's reason, in the reader's words: a System.Object tag is unreachable from a
                // writer, so there is no payload to account for and nothing to guess.
                if (shape.DefinitionId == WellKnownTypes.Object)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        "Corrupt save: a polymorphic value carries System.Object as a type tag. The writer emits a tag only " +
                        "when the declared and runtime types differ, and nothing derives from System.Object except itself.");

                SkipValue(shape.DefinitionId, ref reader);
                return;
            }

            SkipUnmarkedPayload(_typeValues.InternRoot(shape), ref reader);
        }

		Type GetCurrentType(string stableName) { _registry.TryResolveType(stableName, out var t); return t; }

        object? ReadEnumValue(OldTypeSchema oldSchema, Type? targetType, TypeMigrationPlan? plan, ref SequenceReader<byte> reader)
        {
            int underlyingId = oldSchema.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : oldSchema.DefinitionTypeId;
            object? rawValue = ReadValue(underlyingId, null, ref reader);

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

        static object? MakeEnum(Type enumType, object? rawValue)
        {
            try
            {
                return Enum.ToObject(enumType, rawValue);
            }
            catch (Exception e) when (e is ArgumentException or InvalidCastException or OverflowException)
            {
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt enum payload for '{TypeIdentity.GetCanonicalNameOrUnknown(enumType)}': the stored value '{rawValue ?? "null"}' is not a " +
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
        void ReadAndApplyFields(OldTypeSchema oldSchema, TypeMigrationPlan? plan, object? target, ref SequenceReader<byte> reader,
            CtorArgumentPlan ctorArgs = null!, object[] ctorValues = null!, List<(FieldInfo Field, object? Value)>? assignments = null)
        {
            var mappings = plan?.Mappings ?? BuildSkipMappings(oldSchema);
            foreach (var mapping in mappings)
            {
                if (mapping.NewField == null) {
                    // The mirror of the array exception below: an array-typed member is written by
                    // ArrayFormatter with no reference marker, so a *removed* one has to be skipped the same
                    // way. Without this the skip reads a marker where the writer wrote a count.
                    //
                    // A member declared as System.Array is deliberately NOT that case, and the id keeps the
                    // two apart on its own: a declared concrete array is a manifest entry the file
                    // describes, so IsArrayPayload finds it, while the well-known id of the declared type
                    // System.Array is never a schema entry. It falls to SkipValue, which reads it as the
                    // polymorphic slot it is — marker, type value, count, elements.
                    if (mapping.NewField == null && IsArrayPayload(mapping.OldField.TypeId))
                        SkipCollectionPayload(PayloadShape.Array, SkipSchemaOf(mapping.OldField.TypeId), ref reader);
                    else
                        SkipValue(mapping.OldField.TypeId, ref reader);
                }
                else
                {
                    // Seed a reference-typed member from its current value so Load(ref existing) reuses
                    // it in place, exactly like the generated reader does. Value types always overwrite.
                    object? existing = null;
                    if (target != null && !mapping.NewField.FieldType.IsValueType)
                        existing = mapping.NewField.GetValue(target);

                    // An array-typed member is written by ArrayFormatter with no reference marker (the
                    // declared type is exactly an array), so it must be read through the structural
                    // array reader instead of the polymorphic path.
                    //
                    // A member declared as System.Array is the other case and falls through to ReadValue
                    // below, which reads the well-known declared-type id as a polymorphic slot. The two
                    // conditions cannot be satisfied at once, because a declared System.Array is not an
                    // array type in reflection (`typeof(Array).IsArray` is false) and a declared `T[]` is a
                    // manifest entry rather than that id.
                    object value;
                    if (mapping.NewField.FieldType.IsArray &&
                        _file.TryGetById(mapping.OldField.TypeId, out var fieldSchema) &&
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

        static object? Coerce(object? value, CoercionKind kind, Type? targetType)
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

        static HashSet<string> CollectSkippedFields(TypeMigrationPlan? plan)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (plan?.Mappings == null) return set;
            foreach (var m in plan!.Mappings) if (m.NewField == null && m.OldField != null) set.Add(m.OldField.StableName);
            return set;
        }

        void FireBeforeCallback(object? obj, TypeMigrationPlan? plan)
        {
            if (obj == null || plan?.BeforeInvoker == null) return;
            plan.BeforeInvoker(_serializer, obj);
        }

        /// <summary>
        /// Defers the post-deserialization callback until the whole root graph is populated. Used for
        /// reference types; value types invoke it inline because a boxed struct copy cannot be deferred.
        /// </summary>
        void EnqueueAfterCallback(object? obj, TypeMigrationPlan? plan)
        {
            if (obj == null || plan?.AfterInvoker == null) return;
            _serializer.EnqueueDeserializeCallback(obj, plan.AfterInvoker);
        }

        void FireAfterCallbackInline(object? obj, TypeMigrationPlan? plan)
        {
            if (obj == null || plan?.AfterInvoker == null) return;
            plan.AfterInvoker(_serializer, obj);
        }

        bool TryReadCustomFormatter(Type? type, ref object? obj, ref SequenceReader<byte> reader)
        {
            if (type == null) return false;
            var config = _serializer.Config.GetTypeConfigForClosedType(type, false);
            if (config == null) return false;

            var formatter = config.CustomFormatter;
            if (formatter == null) return false;

            // Only a formatter that writes opaque bytes — one the context's schema does not describe —
            // may be driven from here. The robust internal formatters are already handled by ReadValue,
            // and a source-generated formatter relies on the normal object cache and format
            // expectations that differ from the robust schema traversal stream structure. That
            // distinction is by identity, not by the formatter's namespace: a user class declared in
            // Ceras.Generated would otherwise be mistaken for generated and read as structural fields it
            // never wrote. (A robust context has already refused every formatter that is not the
            // manifest's own, so in practice this returns false for all of them; it is kept as this
            // boundary's own statement of which formatters may be driven from here.)
            if (RobustCustomFormatterPolicy.DescribesSchemaBytes(formatter, config, _registry)) return false;

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
