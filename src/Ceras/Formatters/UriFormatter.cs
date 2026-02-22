namespace Ceras.Formatters
{
    using System;
    using System.Buffers;

    sealed class UriFormatter : IFormatter<Uri>
    {
        public void Serialize(IBufferWriter<byte> writer, Uri value)
        {
            SerializerBinary.WriteString(writer, value?.OriginalString);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref Uri value)
        {
            var str = SerializerBinary.ReadString(ref reader);
            if (str == null)
            {
                value = null;
                return;
            }

            value = new Uri(str);
        }
    }
}
