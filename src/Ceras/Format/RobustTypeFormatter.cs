using System;
using System.Buffers;
using Ceras.Formatters;

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

            if (WellKnownTypes.TryGetId(value, out int wkId))
            {
                SchemaSection.WriteLeb128(writer, (uint)wkId);
                return;
            }

            // Arrays
            if (value.IsArray)
            {
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

            // Fallback to registry
            if (_registry != null && _registry.TryGetId(value, out id))
            {
                SchemaSection.WriteLeb128(writer, (uint)id);
                return;
            }

            // A type that is unknown to the schema registry was encountered during serialization.
            // This usually means a concrete subtype of a polymorphic field lacks [CerasSerializable],
            // so no schema entry or formatter was generated for it.
            // Failing here (at save time) gives a clear, actionable error instead of a cryptic crash
            // later during loading (especially when the SchemaTraversalEngine is active after a schema change).
            //
            // Fix options:
            //   (1) Add [CerasSerializable] to the interface/abstract base type of the field so that
            //       all subtypes are auto-discovered and registered by the source generator.
            //   (2) Manually register the type's formatter in UseFormatters / BuildSerializerConfig.
            throw new Ceras.Exceptions.CerasException(
                $"Type '{value.FullName}' is used as a polymorphic value but is not registered in the " +
                $"Ceras schema registry. Add [CerasSerializable] to the interface or abstract base type " +
                $"of the field that holds this value, or manually register a formatter for '{value.Name}'.");
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Type value)
        {
            uint id = SchemaSection.ReadLeb128(ref reader);
            if (id == WellKnownTypes.Null)
            {
                value = null;
                return;
            }

            if (id == FullNameMarker)
            {
                var table = RobustContext.StringTable;
                if (table == null) throw new InvalidOperationException("RobustContext not active");
                string typeName = StringTable.ReadString(ref reader, table);
                value = _typeBinder.GetTypeFromBase(typeName);
                return;
            }

            if (id == (uint)WellKnownTypes.Array)
            {
                Type elementType = null;
                Deserialize(ref reader, ref elementType);
                if (elementType == null) throw new Ceras.Exceptions.CerasException("Failed to deserialize element type for array");
                value = elementType.MakeArrayType();
                return;
            }

            if (WellKnownTypes.TryGetType((int)id, out value))
            {
                if (value.IsGenericTypeDefinition)
                {
                    uint argCount = SchemaSection.ReadLeb128(ref reader);
                    Type[] args = new Type[argCount];
                    for (int i = 0; i < argCount; i++)
                    {
                        Type arg = null;
                        Deserialize(ref reader, ref arg);
                        if (arg == null) throw new Ceras.Exceptions.CerasException($"Failed to deserialize generic argument {i} for type '{value.Name}'");
                        args[i] = arg;
                    }
                    value = value.MakeGenericType(args);
                }
                return;
            }

            var idToType = RobustContext.IdToType;
            if (idToType != null)
            {
                if (idToType.TryGetValue((int)id, out value))
                    return;
            }

            value = null;
        }
    }
}
