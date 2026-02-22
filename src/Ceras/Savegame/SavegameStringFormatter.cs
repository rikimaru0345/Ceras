using System;
using System.Buffers;
using Ceras.Formatters;

namespace Ceras.Savegame
{
    sealed class SavegameStringFormatter : IFormatter<string>
    {
        public void Serialize(IBufferWriter<byte> writer, string value)
        {
            if (value == null)
            {
                SerializerBinary.WriteInt32Fixed(writer, 0);
                return;
            }

            var table = SavegameContext.InternTable;
            if (table == null) throw new InvalidOperationException("SavegameContext not active");

            int index = table.GetIndex(value);
            SerializerBinary.WriteInt32Fixed(writer, index);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref string value)
        {
            int index = SerializerBinary.ReadInt32Fixed(ref reader);
            if (index == 0)
            {
                value = null;
                return;
            }

            var table = SavegameContext.StringTable;
            if (table == null) throw new InvalidOperationException("SavegameContext not active");

            if (index < 0 || index >= table.Length) throw new InvalidOperationException("Invalid string table index");
            value = table[index];
        }
    }
}
