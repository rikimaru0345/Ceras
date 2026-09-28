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
        readonly ITypeBinder _typeBinder = new SimpleTypeBinder(); // Fallback binder

        const int FullNameMarker = 127; // Special marker for unregistered types

        public RobustTypeFormatter(SchemaRegistry registry)
        {
            _registry = registry;
        }

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
                Serialize(writer, value.GetElementType());
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
        /// </summary>
        Type DeserializeCore(ref SequenceReader<byte> reader, out Type value)
        {
            uint id = SchemaSection.ReadLeb128(ref reader);
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

            if (id == (uint)WellKnownTypes.Array)
            {
                Type? elementType = null;
                Deserialize(ref reader, ref elementType);
                if (elementType == null) throw new Ceras.Exceptions.CerasException("Failed to deserialize element type for array");
                value = elementType.MakeArrayType();
                return value;
            }

            if (WellKnownTypes.TryGetType((int)id, out value))
            {
                if (value.IsGenericTypeDefinition)
                {
                    uint argCount = SchemaSection.ReadLeb128(ref reader);
                    Type[] args = new Type[argCount];
                    for (int i = 0; i < argCount; i++)
                    {
                        Type? arg = null;
                        Deserialize(ref reader, ref arg);
                        if (arg == null) throw new Ceras.Exceptions.CerasException($"Failed to deserialize generic argument {i} for type '{value.Name}'");
                        args[i] = arg;
                    }
                    value = value.MakeGenericType(args);
                }
                return value;
            }

            var idToType = RobustContext.IdToType;
            if (idToType != null && idToType.TryGetValue((int)id, out value))
                return value;

            value = null!;
            return value;
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
