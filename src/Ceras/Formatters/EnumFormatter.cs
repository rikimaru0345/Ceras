namespace Ceras.Resolvers
{
    using System;
    using System.Buffers;
    using Formatters;

    /// <summary>
    /// This formatter uses enum.ToString() and Enum.Parse(). It is not used by default, but you can activate through the type config if you want to.
    /// </summary>
    public sealed class EnumAsStringFormatter<T> : IFormatter<T> where T : System.Enum
    {
        ISizeLimitsConfig _sizeLimits;

        public void Serialize(IBufferWriter<byte> writer, T value)
        {
            var str = value.ToString();
            SerializerBinary.WriteString(writer, str);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            var maxStrLen = _sizeLimits.MaxStringLength;
            var str = SerializerBinary.ReadStringLimited(ref reader, maxStrLen);
            value = (T)System.Enum.Parse(typeof(T), str);
        }
    }
}
