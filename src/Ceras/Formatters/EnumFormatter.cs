namespace Ceras.Resolvers
{
    using System;
    using System.Buffers;
    using Formatters;

    /// <summary>
    /// This formatter uses enum.ToString() and Enum.Parse(). It is not used by default, but you can activate through the type config if you want to.
    /// </summary>
    public sealed class EnumAsStringFormatter<T> : FormatterBase<T> where T : System.Enum
    {
        SizeLimits _sizeLimits = default!;

        public override void Serialize(IBufferWriter<byte> writer, T value)
        {
            var str = value.ToString();
            SerializerBinary.WriteString(writer, str);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            var maxStrLen = _sizeLimits.MaxStringLength;
            var str = SerializerBinary.ReadStringLimited(ref reader, maxStrLen);
            // Serialize always writes value.ToString(), which is never null for an enum, so a null marker
            // here means the payload does not describe an enum. Enum.Parse would report that as a plain
            // ArgumentNullException about "value", which says nothing about the file.
            if (str == null)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: an enum value of type '{typeof(T)}' was written with a null name.");

            value = (T)System.Enum.Parse(typeof(T), str);
        }
    }
}
