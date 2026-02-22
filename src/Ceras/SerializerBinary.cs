namespace Ceras
{
    using System;
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Ceras.Helpers;

    public static unsafe class SerializerBinary
    {
        #region Variable Length Encoding (Int32, UInt32, Int64, UInt64)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32(IBufferWriter<byte> writer, int value)
        {
            var zigZag = EncodeZigZag32((long)value);
            WriteVarInt(writer, (ulong)zigZag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32(ref SequenceReader<byte> reader)
        {
            var zigZag = ReadVarInt(ref reader, 32);
            return (int)DecodeZigZag(zigZag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32(IBufferWriter<byte> writer, uint value)
        {
            WriteVarInt(writer, (ulong)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32(ref SequenceReader<byte> reader)
        {
            return (uint)ReadVarInt(ref reader, 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64(IBufferWriter<byte> writer, long value)
        {
            var zigZag = EncodeZigZag64((long)value);
            WriteVarInt(writer, (ulong)zigZag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadInt64(ref SequenceReader<byte> reader)
        {
            var zigZag = ReadVarInt(ref reader, 64);
            return (long)DecodeZigZag(zigZag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64(IBufferWriter<byte> writer, ulong value)
        {
            WriteVarInt(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadUInt64(ref SequenceReader<byte> reader)
        {
            return (ulong)ReadVarInt(ref reader, 64);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long EncodeZigZag32(long value) => (value << 1) ^ (value >> (32 - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long EncodeZigZag64(long value) => (value << 1) ^ (value >> (64 - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long DecodeZigZag(ulong value)
        {
            if ((value & 0x1) != 0)
            {
                return (-1 * ((long)(value >> 1) + 1));
            }

            return (long)(value >> 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void WriteVarInt(IBufferWriter<byte> writer, ulong value)
        {
            var span = writer.GetSpan(10); 
            int written = 0;
            while (value >= 0x80)
            {
                span[written++] = (byte)((value & 0x7F) | 0x80);
                value >>= 7;
            }
            span[written++] = (byte)value;
            writer.Advance(written);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ReadVarInt(ref SequenceReader<byte> reader, int bits)
        {
            int shift = 0;
            ulong result = 0;

            while (true)
            {
                if (!reader.TryRead(out byte byteValue))
                    throw new InvalidOperationException("Unexpected end of sequence while reading VarInt");

                ulong tmp = (ulong)(byteValue & 0x7f);
                result |= tmp << shift;

                if (shift > bits)
                    throw new ArgumentOutOfRangeException(nameof(reader), "Malformed VarInt");

                if ((byteValue & 0x80) != 0x80)
                    return result;

                shift += 7;
            }
        }

        #endregion

        #region Specialized (Bias)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32Bias(IBufferWriter<byte> writer, int value, int bias)
        {
            WriteVarInt(writer, (ulong)((long)value + bias));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadUInt32Bias(ref SequenceReader<byte> reader, int bias)
        {
            var value = (int)ReadVarInt(ref reader, 32);
            return value - bias;
        }

        #endregion

        #region Fixed Length Primitives

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteByte(IBufferWriter<byte> writer, byte value)
        {
            var span = writer.GetSpan(1);
            span[0] = value;
            writer.Advance(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReadByte(ref SequenceReader<byte> reader)
        {
            if (!reader.TryRead(out byte b))
                throw new InvalidOperationException("End of sequence");
            return b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt16Fixed(IBufferWriter<byte> writer, short value)
        {
            var span = writer.GetSpan(2);
            BinaryPrimitives.WriteInt16LittleEndian(span, value);
            writer.Advance(2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadInt16Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 2)
            {
                short val = BinaryPrimitives.ReadInt16LittleEndian(reader.UnreadSpan);
                reader.Advance(2);
                return val;
            }
            Span<byte> temp = stackalloc byte[2];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(2);
            return BinaryPrimitives.ReadInt16LittleEndian(temp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32Fixed(IBufferWriter<byte> writer, int value)
        {
            var span = writer.GetSpan(4);
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            writer.Advance(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 4)
            {
                int val = BinaryPrimitives.ReadInt32LittleEndian(reader.UnreadSpan);
                reader.Advance(4);
                return val;
            }
            Span<byte> temp = stackalloc byte[4];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(4);
            return BinaryPrimitives.ReadInt32LittleEndian(temp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32Fixed(IBufferWriter<byte> writer, uint value)
        {
            var span = writer.GetSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            writer.Advance(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 4)
            {
                uint val = BinaryPrimitives.ReadUInt32LittleEndian(reader.UnreadSpan);
                reader.Advance(4);
                return val;
            }
            Span<byte> temp = stackalloc byte[4];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(4);
            return BinaryPrimitives.ReadUInt32LittleEndian(temp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64Fixed(IBufferWriter<byte> writer, long value)
        {
            var span = writer.GetSpan(8);
            BinaryPrimitives.WriteInt64LittleEndian(span, value);
            writer.Advance(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadInt64Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 8)
            {
                long val = BinaryPrimitives.ReadInt64LittleEndian(reader.UnreadSpan);
                reader.Advance(8);
                return val;
            }
            Span<byte> temp = stackalloc byte[8];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(8);
            return BinaryPrimitives.ReadInt64LittleEndian(temp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFloat32Fixed(IBufferWriter<byte> writer, float value)
        {
            var span = writer.GetSpan(4);
            BinaryPrimitives.WriteSingleLittleEndian(span, value);
            writer.Advance(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadFloat32Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 4)
            {
                float val = BinaryPrimitives.ReadSingleLittleEndian(reader.UnreadSpan);
                reader.Advance(4);
                return val;
            }
            Span<byte> temp = stackalloc byte[4];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(4);
            return BinaryPrimitives.ReadSingleLittleEndian(temp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDouble64Fixed(IBufferWriter<byte> writer, double value)
        {
            var span = writer.GetSpan(8);
            BinaryPrimitives.WriteDoubleLittleEndian(span, value);
            writer.Advance(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadDouble64Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 8)
            {
                double val = BinaryPrimitives.ReadDoubleLittleEndian(reader.UnreadSpan);
                reader.Advance(8);
                return val;
            }
            Span<byte> temp = stackalloc byte[8];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(8);
            return BinaryPrimitives.ReadDoubleLittleEndian(temp);
        }

        #endregion

        #region Strings

        static readonly UTF8Encoding _utf8Encoding = new UTF8Encoding(false, true);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteString(IBufferWriter<byte> writer, string value)
        {
            if (value == null)
            {
                WriteUInt32Bias(writer, -1, 1);
                return;
            }

            int byteCount = _utf8Encoding.GetByteCount(value);
            WriteUInt32Bias(writer, byteCount, 1);
            var span = writer.GetSpan(byteCount);
            _utf8Encoding.GetBytes(value, span);
            writer.Advance(byteCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadString(ref SequenceReader<byte> reader)
        {
            int length = ReadUInt32Bias(ref reader, 1);
            if (length == -1) return null;

            if (reader.UnreadSpan.Length >= length)
            {
                var str = _utf8Encoding.GetString(reader.UnreadSpan.Slice(0, length));
                reader.Advance(length);
                return str;
            }
            else
            {
                byte[] temp = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (!reader.TryCopyTo(temp.AsSpan(0, length)))
                        throw new InvalidOperationException("Failed to read string: not enough data");
                    
                    var str = _utf8Encoding.GetString(temp, 0, length);
                    reader.Advance(length);
                    return str;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadStringLimited(ref SequenceReader<byte> reader, uint maxLength)
        {
            int length = ReadUInt32Bias(ref reader, 1);
            if (length == -1) return null;

            if ((uint)length > maxLength)
                throw new InvalidOperationException($"String length {length} exceeds max {maxLength}");

            if (reader.UnreadSpan.Length >= length)
            {
                var str = _utf8Encoding.GetString(reader.UnreadSpan.Slice(0, length));
                reader.Advance(length);
                return str;
            }
            else
            {
                byte[] temp = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (!reader.TryCopyTo(temp.AsSpan(0, length)))
                        throw new InvalidOperationException("Failed to read string: not enough data");
                    
                    var str = _utf8Encoding.GetString(temp, 0, length);
                    reader.Advance(length);
                    return str;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
        }

        #endregion

        #region Buffer Management

        const int MaximumArraySize = 0x7fffffc7;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureCapacity(ref byte[] buffer, int offset, int size)
        {
            int newSize = offset + size;
            if (buffer.Length >= newSize) return;
            ExpandBuffer(ref buffer, newSize);
        }

        internal static void ExpandBuffer(ref byte[] buffer, int newSize)
        {
            if (newSize > MaximumArraySize || newSize < 0) throw new InvalidOperationException("Buffer too large");
            int currentSize = buffer.Length;
            int targetSize = Math.Max(0x4000, currentSize * 2);
            while (targetSize < newSize) targetSize *= 2;
            targetSize = Math.Min(targetSize, MaximumArraySize);

            byte[] newBuffer = CerasBufferPool.Rent(targetSize);
            buffer.AsSpan(0, currentSize).CopyTo(newBuffer);
            CerasBufferPool.Return(buffer);
            buffer = newBuffer;
        }

        #endregion
    }
}
