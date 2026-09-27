namespace Ceras.Formatters
{
    using System;
    using System.Buffers;

    public sealed class UriFormatter : FormatterBase<Uri>
    {
        public UriFormatter()
        {
            CerasSerializer.AddFormatterConstructedType(typeof(Uri));
        }

        public override void Serialize(IBufferWriter<byte> writer, Uri value)
        {
            // A null Uri is a legal payload value: WriteString encodes it as a null string marker, and
            // Deserialize reads that marker back as a null Uri (see below). So the writer is handed a
            // null on purpose, which the null-forgiving operator makes explicit.
            SerializerBinary.WriteString(writer, (value?.OriginalString)!);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Uri value)
        {
            var str = SerializerBinary.ReadString(ref reader);
            if (str == null)
            {
                value = null!;
                return;
            }

            value = new Uri(str);
        }
    }
}
