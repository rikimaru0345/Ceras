namespace Ceras.Formatters
{
    using System;
    using System.Buffers;

    public sealed class StringFormatter : FormatterBase<string>
    {
        public override void Serialize(IBufferWriter<byte> writer, string value)
        {
            SerializerBinary.WriteString(writer, value);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref string value)
        {
            value = SerializerBinary.ReadString(ref reader);
        }
    }

    public sealed class MaxSizeStringFormatter : FormatterBase<string>
    {
        readonly uint _maxLength;

        public MaxSizeStringFormatter(uint maxLength)
        {
            _maxLength = maxLength;
        }

        public override void Serialize(IBufferWriter<byte> writer, string value)
        {
            SerializerBinary.WriteString(writer, value);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref string value)
        {
            value = SerializerBinary.ReadStringLimited(ref reader, _maxLength);
        }
    }
}
