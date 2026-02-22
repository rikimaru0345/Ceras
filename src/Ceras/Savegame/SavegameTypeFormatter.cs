using System;
using Ceras.Formatters;

namespace Ceras.Savegame
{
    /// <summary>
    /// In savegame format, we don't write CLR type names for polymorphic fields.
    /// We write schema type-IDs (LEB128).
    /// </summary>
    sealed class SavegameTypeFormatter : IFormatter<Type>
    {
        public void Serialize(ref byte[] buffer, ref int offset, Type value)
        {
            if (value == null)
            {
                SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)WellKnownTypes.Null);
                return;
            }

            if (WellKnownTypes.TryGetId(value, out int wkId))
            {
                SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)wkId);
                return;
            }

            var typeToId = SavegameContext.TypeToId;
            if (typeToId != null && typeToId.TryGetValue(value, out int id))
            {
                SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)id);
                return;
            }

            // Fallback: search in registry directly (might be slow but correct)
            if (SavegameSchemaRegistry.TryGetId(value, out id))
            {
                SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)id);
                return;
            }

            // Unknown type - write Null so the reader doesn't crash.
            SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)WellKnownTypes.Null);
        }

        public void Deserialize(byte[] buffer, ref int offset, ref Type value)
        {
            uint id = SchemaSection.ReadLeb128(buffer, ref offset);
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
