using System;
using System.Buffers;
using Ceras.Formatters;

namespace Ceras.Format
{
    sealed class RobustStringFormatter : FormatterBase<string>
    {
        public override void Serialize(IBufferWriter<byte> writer, string value)
        {
            if (value == null)
            {
                SerializerBinary.WriteUInt32Fixed(writer, 0);
                return;
            }

            var table = RobustContext.InternTable;
            if (table == null) throw new InvalidOperationException("RobustContext not active");

            int index = table.GetIndex(value);
            SerializerBinary.WriteUInt32Fixed(writer, (uint)index);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref string value)
        {
            uint index = SerializerBinary.ReadUInt32Fixed(ref reader);
            if (index == 0)
            {
                value = null;
                return;
            }

            var table = RobustContext.StringTable;
            if (table == null) throw new InvalidOperationException("RobustContext not active");

            if (index >= (uint)table.Length) throw new InvalidOperationException("Invalid string table index");
            value = table[(int)index];
        }
    }
}
