using System;
using System.Buffers;
using Ceras.Formatters;
using Ceras.Helpers;

namespace Ceras.Format
{
    /// <summary>
    /// In robust format format, we don't write CLR type names for polymorphic fields.
    /// We write schema type-IDs (LEB128).
    /// </summary>
    sealed class RobustTypeFormatter : FormatterBase<Type>
    {
        readonly SchemaRegistry _registry;

        // The context's Config.TypeBinder, which every other Type-value door consults too
        // (SchemaTraversalEngine.ResolveFullName, CerasRobustSerializer.BuildIdToType); read only for a
        // FullNameMarker type value, a read-only path no Ceras writer emits (Format/LAYOUT.md).
        ITypeBinder _typeBinder;

        const int FullNameMarker = 127; // Special marker for unregistered types

        public RobustTypeFormatter(SchemaRegistry registry, ITypeBinder? typeBinder = null)
        {
            _registry = registry;
            _typeBinder = typeBinder ?? new SimpleTypeBinder();
        }

        /// <summary>
        /// Points this formatter at <paramref name="typeBinder"/>. The context's formatters are installed
        /// <em>before</em> the configuration callback runs, so a binder that callback assigns is not known
        /// at construction time; this is the second half of the same fix, and
        /// <see cref="CerasRobustSerializer"/> calls it once the configuration has settled.
        /// <para>
        /// The coalesce matches <c>SchemaTraversalEngine</c>: an unset <c>CerasConfig.TypeBinder</c> is a
        /// <c>SimpleTypeBinder</c> and never null, so it only covers a caller that pushed null through
        /// the non-nullable property — and it keeps this field non-null, which the read needs.
        /// </para>
        /// </summary>
        internal void UseTypeBinder(ITypeBinder? typeBinder) => _typeBinder = typeBinder ?? new SimpleTypeBinder();

        public override void Serialize(IBufferWriter<byte> writer, Type value)
        {
            if (value == null)
            {
                SchemaSection.WriteLeb128(writer, (uint)WellKnownTypes.Null);
                return;
            }

            // Same policy as the transient TypeFormatter: a reference to a type Ceras cannot
            // serialize at all (a delegate, an enumerator, reflection metadata, ...) is rejected by
            // name. Without this the value would fall through to the registry lookup below and be
            // reported as an undeclared type, which names a fix ("add [CerasSerializable] to it")
            // that cannot work for a banned shape.
            if (BannedTypes.TryGetBanned(value, out var banReason))
            {
                throw new BannedTypeException(
                    $"A type reference describes the banned type '{value.FriendlyName(true)}', which Ceras cannot serialize, " +
                    $"and it will not be written as null: a null here silently changes what the file means. Remove the reference, " +
                    $"or point it at a type Ceras can serialize. Reason this type is banned: \"{banReason}\".");
            }

            if (WellKnownTypes.TryGetId(value, out int wkId))
            {
                SchemaSection.WriteLeb128(writer, (uint)wkId);
                return;
            }

            // Arrays
            if (value.IsArray)
            {
                // A rank > 1 array is refused, here and in the reference wrapper that wrote this tag
                // (both refuse the same value; an array is never a banned shape, so the two checks do not
                // overlap). The array form of a type value is (Array marker, element type) and carries
                // nothing else — no rank, no dimensions — and Deserialize below rebuilds it with
                // MakeArrayType(), which is always rank 1. Writing one here is therefore not merely
                // described imprecisely: it is read back as a one-dimensional array over the same
                // flattened elements, and the save itself succeeds. Nothing downstream can tell that
                // apart from the array the user meant, so the shape is refused before the marker or any
                // byte of the value behind it is written.
                if (MultidimensionalArrayGuard.IsMultidimensional(value))
                    throw MultidimensionalArrayGuard.Reject(
                        value, "a polymorphic slot or a System.Type value", MultidimensionalArrayGuard.TypeValueReason);

                SchemaSection.WriteLeb128(writer, (uint)WellKnownTypes.Array);
                // An array's element type is never null: GetElementType() answers null exactly for the
                // types that have no element type, and this branch is only reached for an array.
                Serialize(writer, value.GetElementType()!);
                return;
            }

            // For closed generics that are based on well-known types (like List<int>),
            // we write the well-known ID of the base type, and then the arguments.
            if (value.IsGenericType && !value.IsGenericTypeDefinition)
            {
                var def = value.GetGenericTypeDefinition();
                if (WellKnownTypes.TryGetId(def, out wkId))
                {
                    SchemaSection.WriteLeb128(writer, (uint)wkId);
                    var args = value.GetGenericArguments();
                    SchemaSection.WriteLeb128(writer, (uint)args.Length);
                    foreach (var arg in args)
                        Serialize(writer, arg);
                    return;
                }
            }

            var typeToId = RobustContext.TypeToId;
            if (typeToId != null && typeToId.TryGetValue(value, out int id))
            {
                SchemaSection.WriteLeb128(writer, (uint)id);
                return;
            }

            // If we're here, the type was not found in the pre-computed schema section.
            // This happens if a polymorphic field contains a type that wasn't discovered
            // during the initial schema traversal (Phase 1).
            if (RobustContext.InternTable != null)
            {
                // We are inside a Robust write context but the type is not registered.
                throw new Ceras.Exceptions.CerasException(
                    $"Type '{value.FullName}' appears as a polymorphic value during serialization but " +
                    $"is not registered in the schema registry. Add [CerasSerializable] to it and call " +
                    $"UseFormatters(config) before creating the serializer.");
            }

            // Fallback to registry (only if not in a robust context, e.g. normal serialization using this formatter)
            if (_registry != null && _registry.TryGetId(value, out id))
            {
                SchemaSection.WriteLeb128(writer, (uint)id);
                return;
            }

            // A type that is unknown to the schema registry was encountered during serialization.
            // This usually means a concrete subtype of a polymorphic field lacks [CerasSerializable],
            // so no schema entry or formatter was generated for it.
            throw new Ceras.Exceptions.CerasException(
                $"Type '{value.FullName}' is used as a polymorphic value but is not registered in the " +
                $"Ceras schema registry. Add [CerasSerializable] to the interface or abstract base type " +
                $"of the field that holds this value, or manually register a formatter for '{value.Name}'.");
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Type value)
            => value = DeserializeAndCheck(ref reader, ref value);

        /// <summary>
        /// Rebuilds the type a payload names, and refuses one that
        /// <see cref="UnsupportedCollectionPolicy"/> does not support.
        /// <para>
        /// <c>Serialize</c> refuses the same values by name (through <see cref="BannedTypes"/>), so this is
        /// the mirror rather than a new rule: a save must not be the one way a collection shape the writer
        /// would refuse can still be produced. A file whose layout fingerprint matches comes from a build
        /// that passed the same check, so in practice this fires only for a hand-built payload or a schema
        /// a caller forged — which is exactly where a silent hand-back would be least welcome. Every path
        /// has consumed its bytes by the time the check runs, so the stream stays aligned.
        /// </para>
        /// </summary>
        Type DeserializeAndCheck(ref SequenceReader<byte> reader, ref Type value)
        {
            value = DeserializeCore(ref reader, out value);
            RejectRefused(value);
            return value;
        }

        /// <summary>
        /// The grammar itself. <paramref name="value"/> is the recursion's out parameter (a ref struct
        /// cannot be a <c>ref</c> out argument here, and the nested calls need somewhere to put their
        /// result), so every return path assigns it before returning.
        /// <para>
        /// The leading tag is read as a <b>type id</b>, and the whole of this method is written in that
        /// type: a LEB128 field is a <c>uint</c> and five bytes of it are legal, so a tag above
        /// <see cref="int.MaxValue"/> narrows to a <em>negative</em> id, which matches no well-known
        /// type and no manifest entry — and then fell out of the bottom of the method as
        /// <c>null</c>. A null is a value the user can legitimately have here, so nothing downstream
        /// could tell that apart from a file naming a type that does not exist: a crafted tag decoded
        /// silently into "no type" instead of being refused. <c>SchemaTraversalEngine</c> reads the same
        /// field the same way, in both the positions a type value is read in; only the wording around it
        /// differs (it names the field after the position it stands in), so the two refuse the same bytes
        /// and each quotes the value it read.
        /// </para>
        /// </summary>
        Type DeserializeCore(ref SequenceReader<byte> reader, out Type value)
        {
            // The position-free name, because this one decoder serves both positions a `Type` value is
            // read in — a `Type` member, and the type tag behind a polymorphic value's reference marker —
            // and the interpreter, which reads the tag at each of those two sites separately, names the
            // field after the position it is standing in. Same field, same words either way here.
            int id = SchemaSection.ReadTypeIdLeb128(ref reader, "a type value's tag");
            if (id == WellKnownTypes.Null)
            {
                value = null!;
                return value;
            }

            if (id == FullNameMarker)
            {
                var table = RobustContext.StringTable;
                if (table == null) throw new InvalidOperationException("RobustContext not active");
                string typeName = StringTable.ReadString(ref reader, table);
                value = _typeBinder.GetTypeFromBase(typeName);
                return value;
            }

            if (id == WellKnownTypes.Array)
            {
                // The recursion target. IFormatter<T>.Deserialize declares its ref parameter
                // non-nullable, so the local starts as the not-yet-read default; the null the
                // recursion can write into it is what the check below rejects.
                Type elementType = null!;
                Deserialize(ref reader, ref elementType);
                if (elementType == null) throw new Ceras.Exceptions.CerasException("Failed to deserialize element type for array");
                value = elementType.MakeArrayType();
                return value;
            }

            if (WellKnownTypes.TryGetType(id, out var wellKnown))
            {
                value = wellKnown;
                if (wellKnown.IsGenericTypeDefinition)
                {
                    int argCount = ReadBoundedArgCount(ref reader, id, wellKnown);
                    Type[] args = new Type[argCount];
                    for (int i = 0; i < argCount; i++)
                    {
                        // Same recursion target as the array branch above, and the same null check.
                        Type arg = null!;
                        Deserialize(ref reader, ref arg);
                        if (arg == null) throw new Ceras.Exceptions.CerasException($"Failed to deserialize generic argument {i} for type '{wellKnown.Name}'");
                        args[i] = arg;
                    }
                    value = wellKnown.MakeGenericType(args);
                }
                return value;
            }

            var idToType = RobustContext.IdToType;
            if (idToType != null && idToType.TryGetValue(id, out var resolved))
            {
                value = resolved;
                return value;
            }

            // A tag in neither table: not a well-known type and not an id this build's manifest
            // declares. A type the application has removed is *not* this — a manifest id the build
            // cannot resolve is a real answer elsewhere (BuildIdToType) — so there is nothing to hand
            // back, and `null` is a value the user can legitimately have here, which is exactly why it
            // must not be what a field nothing describes decodes to. The only writer that emits an id
            // like this is a hand-built or damaged file, so it is refused rather than guessed at.
            throw new Ceras.Exceptions.CerasDataCorruptionException(
                $"Corrupt System.Type payload: a type value names type id {id}, which is neither one of the format's well-known " +
                $"types (0-{WellKnownTypes.DateTimeOffset}) nor an id the save's own schema section describes (manifest ids start at " +
                $"{WellKnownTypes.UserTypeBase}). No Ceras writer emits a type value with such a tag, so the file is damaged or was not " +
                "assembled by a Ceras Save; it is refused here rather than handed back as Type's null, which would be indistinguishable " +
                "from a value that really is null.");
        }

        /// <summary>
        /// The argument count of a closed built-in generic, refused unless it is the count that
        /// definition takes. This is the same question, asked in the same order and for the same reasons,
        /// as the interpreter's <c>TypeValueShapes.ReadBoundedArgCount</c>: those two are the only
        /// decoders of this grammar, and a count one of them refuses the other must refuse too - or a file
        /// is corrupt on one reader and readable on the other, decided by nothing but whether its header
        /// fingerprint happened to match. Every malformed-collection test in the suite builds a file with
        /// fingerprint 0 and therefore only ever reached the interpreter, which is how a decoder that
        /// sized an array straight from a doctored field survived review.
        /// <para>
        /// Both guards run before the array this count sizes exists, and each answers "is this field sane
        /// at all" before the semantic question below is even asked. The field is a LEB128, so it can be
        /// any <c>uint</c> - <c>ReadLeb128</c> accepts five bytes - and it is read as a type id, so a value
        /// above <see cref="int.MaxValue"/> is refused here rather than becoming a negative length (which
        /// was an <see cref="OverflowException"/> out of the array-length conversion). And the definition
        /// fixes the count, so a larger one is refused before it sizes anything: <c>new Type[count]</c> is
        /// eight bytes per declared argument, so 100 million of them is 800 MB of allocation from a
        /// single byte in a file.
        /// </para>
        /// </summary>
        static int ReadBoundedArgCount(ref SequenceReader<byte> reader, int definitionId, Type definition)
        {
            int argCount = SchemaSection.ReadTypeIdLeb128(ref reader, $"the argument count of '{definition.Name}'");

            // The arity itself is asked of the one place that holds it - a table derived from
            // WellKnownTypes, so it cannot drift from the id space the format reserves - rather than
            // restated here. A definition reserved there and absent from it is not a thing that can
            // happen: every well-known generic definition is a structural definition, which is also why
            // this branch cannot be reached with a count of 0 standing for "not this question".
            int required = StructuralShape.RequiredArity(definitionId);

            // The bound is asked first because it is a resource guard: this count sizes an array, and the
            // definition's own arity bounds it as tightly as anything can. What is left once it passes is
            // a count that is within the bound and still wrong, which is the rule rather than the
            // resource - and the two are separate refusals because they are separate faults: one says
            // this field cannot be believed, the other says this value names no type.
            if (argCount > required)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt System.Type payload: '{definition.Name}' declares {argCount} generic argument" +
                    $"{(argCount == 1 ? "" : "s")}, but the definition takes exactly {required}. This count sizes the argument " +
                    "array, so it is refused before anything is allocated for it: a Type value names a concrete runtime type, " +
                    "and a wrong argument count does not name one at all.");

            // A wrong count that is within the bound is still a wrong count. Left to MakeGenericType it
            // was an ArgumentException out of reflection, naming neither the definition nor either count.
            if (argCount != required)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt System.Type payload: '{definition.Name}' declares {argCount} generic argument" +
                    $"{(argCount == 1 ? "" : "s")}, but the definition takes exactly {required}. A Type value names a concrete " +
                    "runtime type, so a wrong argument count does not name a type at all, and the payload that follows it " +
                    "cannot be counted.");

            return argCount;
        }

        /// <summary>
        /// Refuses a type value the policy refuses, by the same wording <c>Serialize</c> uses. A null
        /// reference is a value the user has, not a refusal, so it passes.
        /// </summary>
        void RejectRefused(Type? value)
        {
            if (!UnsupportedCollectionPolicy.IsUnsupported(value)) return;

            throw new Formatters.BannedTypeException(
                $"A type value in the save names the collection type '{TypeIdentity.GetCanonicalName(value!)}', which Ceras cannot " +
                $"serialize, and it will not be handed back as {nameof(Type)}'s null: a null here silently changes what the value means. " +
                UnsupportedCollectionPolicy.BanReason);
        }
    }
}
