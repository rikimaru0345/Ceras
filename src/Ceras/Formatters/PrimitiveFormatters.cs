using System;
using System.Buffers;
using System.Linq.Expressions;

namespace Ceras.Formatters
{
    sealed class ByteFormatter : IFormatter<byte>
    {
        public void Serialize(IBufferWriter<byte> writer, byte value)
        {
            SerializerBinary.WriteByte(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref byte value)
        {
            value = SerializerBinary.ReadByte(ref reader);
        }
    }

    sealed class SByteFormatter : IFormatter<sbyte>
    {
        public void Serialize(IBufferWriter<byte> writer, sbyte value)
        {
            SerializerBinary.WriteByte(writer, (byte)value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref sbyte value)
        {
            value = (sbyte)SerializerBinary.ReadByte(ref reader);
        }
    }

    sealed class BoolFormatter : IFormatter<bool>
    {
        public void Serialize(IBufferWriter<byte> writer, bool value)
        {
            SerializerBinary.WriteInt32(writer, value ? 1 : 0);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref bool value)
        {
            value = SerializerBinary.ReadInt32(ref reader) != 0;
        }
    }

    sealed class CharFormatter : IFormatter<char>
    {
        public void Serialize(IBufferWriter<byte> writer, char value)
        {
            SerializerBinary.WriteInt16Fixed(writer, (short)value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref char value)
        {
            value = (char)SerializerBinary.ReadInt16Fixed(ref reader);
        }
    }

    sealed class Int16Formatter : IFormatter<short>
    {
        public void Serialize(IBufferWriter<byte> writer, short value)
        {
            SerializerBinary.WriteInt16Fixed(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref short value)
        {
            value = SerializerBinary.ReadInt16Fixed(ref reader);
        }
    }

    sealed class UInt16Formatter : IFormatter<ushort>
    {
        public void Serialize(IBufferWriter<byte> writer, ushort value)
        {
            SerializerBinary.WriteInt16Fixed(writer, (short)value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ushort value)
        {
            value = (ushort)SerializerBinary.ReadInt16Fixed(ref reader);
        }
    }

    sealed class Int32Formatter : IFormatter<int>, IInlineEmitter
    {
        public void Serialize(IBufferWriter<byte> writer, int value)
        {
            SerializerBinary.WriteInt32(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref int value)
        {
            value = SerializerBinary.ReadInt32(ref reader);
        }


        public Expression EmitWrite(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int writtenSize)
        {
            writtenSize = 4;
            return null;
        }

        public Expression EmitRead(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int readSize)
        {
            readSize = 4;
            return null;
        }
    }

    sealed class UInt32Formatter : IFormatter<uint>
    {
        public void Serialize(IBufferWriter<byte> writer, uint value)
        {
            SerializerBinary.WriteUInt32(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref uint value)
        {
            value = SerializerBinary.ReadUInt32(ref reader);
        }
    }

    sealed class FloatFormatter : IFormatter<float>
    {
        public void Serialize(IBufferWriter<byte> writer, float value)
        {
            SerializerBinary.WriteFloat32Fixed(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref float value)
        {
            value = SerializerBinary.ReadFloat32Fixed(ref reader);
        }
    }

    sealed class DoubleFormatter : IFormatter<double>
    {
        public void Serialize(IBufferWriter<byte> writer, double value)
        {
            SerializerBinary.WriteDouble64Fixed(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref double value)
        {
            value = SerializerBinary.ReadDouble64Fixed(ref reader);
        }
    }

    sealed class Int64Formatter : IFormatter<long>
    {
        public void Serialize(IBufferWriter<byte> writer, long value)
        {
            SerializerBinary.WriteInt64Fixed(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref long value)
        {
            value = SerializerBinary.ReadInt64Fixed(ref reader);
        }
    }

    sealed class UInt64Formatter : IFormatter<ulong>
    {
        public void Serialize(IBufferWriter<byte> writer, ulong value)
        {
            SerializerBinary.WriteInt64Fixed(writer, (long)value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref ulong value)
        {
            value = (ulong)SerializerBinary.ReadInt64Fixed(ref reader);
        }
    }

    sealed class IntPtrFormatter : IFormatter<IntPtr>
    {
        public void Serialize(IBufferWriter<byte> writer, IntPtr IntPtr)
        {
            SerializerBinary.WriteInt64Fixed(writer, IntPtr.ToInt64());
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref IntPtr value)
        {
            value = new IntPtr(SerializerBinary.ReadInt64Fixed(ref reader));
        }
    }

    sealed class UIntPtrFormatter : IFormatter<UIntPtr>
    {
        public void Serialize(IBufferWriter<byte> writer, UIntPtr IntPtr)
        {
            SerializerBinary.WriteInt64Fixed(writer, (long)IntPtr.ToUInt64());
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref UIntPtr value)
        {
            value = new UIntPtr((ulong)SerializerBinary.ReadInt64Fixed(ref reader));
        }
    }

}
