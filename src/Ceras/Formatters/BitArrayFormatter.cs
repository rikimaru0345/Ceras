namespace Ceras.Formatters
{
    using System;
    using System.Buffers;
    using System.Collections;

    public sealed class BitArrayFormatter : FormatterBase<BitArray>
    {
        public BitArrayFormatter()
        {
            CerasSerializer.AddFormatterConstructedType(typeof(BitArray));
        }

        public override void Serialize(IBufferWriter<byte> writer, BitArray value)
        {
            SerializerBinary.WriteInt32(writer, value.Length);
            byte[] bytes = new byte[(value.Length + 7) / 8];
            value.CopyTo(bytes, 0);
            writer.Write(bytes);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref BitArray value)
        {
            int length = SerializerBinary.ReadInt32(ref reader);
            int byteCount = (length + 7) / 8;
            byte[] bytes = new byte[byteCount];
            if (!reader.TryCopyTo(bytes)) throw new Ceras.Exceptions.CerasDataCorruptionException("Corrupt data: the save ended before a value could be read (truncated payload).");
            reader.Advance(byteCount);
            value = new BitArray(bytes);
            value.Length = length;
        }
    }
}
