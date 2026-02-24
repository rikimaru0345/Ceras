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

            // Extreme fallback for unregistered types (mostly for tests or transition)
            SchemaSection.WriteLeb128(writer, (uint)FullNameMarker);
            SerializerBinary.WriteString(writer, _typeBinder.GetBaseName(value));
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
                string typeName = SerializerBinary.ReadString(ref reader);
                value = _typeBinder.GetTypeFromBase(typeName);
                return;
            }

            if (WellKnownTypes.TryGetType((int)id, out value))
                return;

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
