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
            // A null string is a legal payload value: WriteString encodes it as a null marker, so the
            // value read back here really can be null and has to be handed to the caller's ref parameter
            // as-is. The null-forgiving operator documents that without claiming it cannot happen.
            value = SerializerBinary.ReadString(ref reader)!;
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
            // Same null-marker round trip as StringFormatter: the length limit only bounds the string,
            // it does not turn the null marker into a value.
            value = SerializerBinary.ReadStringLimited(ref reader, _maxLength)!;
        }
    }
}
