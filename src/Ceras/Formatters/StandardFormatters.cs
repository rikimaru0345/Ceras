using System;
using System.Buffers;
using System.Collections.Specialized;
using System.Numerics;

namespace Ceras.Formatters
{
    public sealed class DateTimeFormatter : FormatterBase<DateTime>
    {
        public override void Serialize(IBufferWriter<byte> writer, DateTime value) => SerializerBinary.WriteInt64Fixed(writer, value.ToBinary());
        public override void Deserialize(ref SequenceReader<byte> reader, ref DateTime value) => value = DateTime.FromBinary(SerializerBinary.ReadInt64Fixed(ref reader));
    }

    public sealed class DateTimeOffsetFormatter : FormatterBase<DateTimeOffset>
    {
        public override void Serialize(IBufferWriter<byte> writer, DateTimeOffset value)
        {
            SerializerBinary.WriteInt64Fixed(writer, value.Ticks);
            SerializerBinary.WriteInt16Fixed(writer, (short)value.Offset.TotalMinutes);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref DateTimeOffset value)
        {
            // Same decoder as the robust path, so an impossible ticks/offset combination is reported as
            // data corruption here too instead of escaping as ArgumentOutOfRangeException.
            value = SerializerBinary.ReadDateTimeOffset(ref reader);
        }
    }

    public sealed class TimeSpanFormatter : FormatterBase<TimeSpan>
    {
        public override void Serialize(IBufferWriter<byte> writer, TimeSpan value) => SerializerBinary.WriteInt64Fixed(writer, value.Ticks);
        public override void Deserialize(ref SequenceReader<byte> reader, ref TimeSpan value) => value = new TimeSpan(SerializerBinary.ReadInt64Fixed(ref reader));
    }

    public sealed class GuidFormatter : FormatterBase<Guid>
    {
        public override void Serialize(IBufferWriter<byte> writer, Guid value) => SerializerBinary.WriteGuid(writer, value);
        public override void Deserialize(ref SequenceReader<byte> reader, ref Guid value) => value = SerializerBinary.ReadGuid(ref reader);
    }

    public sealed class BitVector32Formatter : FormatterBase<BitVector32>
    {
        public override void Serialize(IBufferWriter<byte> writer, BitVector32 value) => SerializerBinary.WriteInt32Fixed(writer, value.Data);
        public override void Deserialize(ref SequenceReader<byte> reader, ref BitVector32 value) => value = new BitVector32(SerializerBinary.ReadInt32Fixed(ref reader));
    }

    public sealed class BigIntegerFormatter : FormatterBase<BigInteger>
    {
        public override void Serialize(IBufferWriter<byte> writer, BigInteger value)
        {
            var data = value.ToByteArray();
            SerializerBinary.WriteUInt32(writer, (uint)data.Length);
            SerializerBinary.Write(writer, data);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref BigInteger value)
        {
            int length = (int)SerializerBinary.ReadUInt32(ref reader);
            byte[] bytes = new byte[length];
            reader.TryCopyTo(bytes);
            reader.Advance(length);
            value = new BigInteger(bytes);
        }
    }
}
