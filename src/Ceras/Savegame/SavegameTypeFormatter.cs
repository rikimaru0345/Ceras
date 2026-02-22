using System;
using System.Buffers;
using Ceras.Formatters;

namespace Ceras.Savegame
{
    /// <summary>
    /// In savegame format, we don't write CLR type names for polymorphic fields.
    /// We write schema type-IDs (LEB128).
    /// </summary>
    sealed class SavegameTypeFormatter : IFormatter<Type>
    {
        SchemaRegistry _registry;

        public void Serialize(IBufferWriter<byte> writer, Type value)
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

            var typeToId = SavegameContext.TypeToId;
            if (typeToId != null && typeToId.TryGetValue(value, out int id))
            {
                SchemaSection.WriteLeb128(writer, (uint)id);
                return;
            }

            // Fallback
            if (_registry != null && _registry.TryGetId(value, out id))
            {
                SchemaSection.WriteLeb128(writer, (uint)id);
                return;
            }

            SchemaSection.WriteLeb128(writer, (uint)WellKnownTypes.Null);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Type value)
        {
            uint id = SchemaSection.ReadLeb128(ref reader);
            if (id == WellKnownTypes.Null)
            {
                value = null;
                return;
            }

            var idToType = SavegameContext.IdToType;
            if (idToType != null)
            {
                if (idToType.TryGetValue((int)id, out value))
                    return;
            }

            value = null;
        }
    }
}
