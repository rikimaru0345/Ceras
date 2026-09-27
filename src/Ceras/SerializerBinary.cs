namespace Ceras
{
    using System;
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Ceras.Helpers;
    using System.Numerics;

    using System.Runtime.InteropServices;
    using System.Runtime.Intrinsics;
    using System.Runtime.Intrinsics.X86;

    public static unsafe class SerializerBinary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FastCopy(byte[] source, int sourceOffset, byte[] destination, int destinationOffset, long count)
        {
            source.AsSpan(sourceOffset, (int)count).CopyTo(destination.AsSpan(destinationOffset, (int)count));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FastCopy<T>(ReadOnlySpan<T> source, Span<byte> destination) where T : struct
        {
            var sourceBytes = MemoryMarshal.AsBytes(source);
            if (Vector256.IsHardwareAccelerated && sourceBytes.Length >= Vector256<byte>.Count)
            {
                int i = 0;
                int count = sourceBytes.Length;
                int vectorSize = Vector256<byte>.Count;
                fixed (byte* pSrc = sourceBytes, pDest = destination)
                {
                    for (; i <= count - vectorSize; i += vectorSize)
                    {
                        var v = Vector256.Load(pSrc + i);
                        v.Store(pDest + i);
                    }
                }
                if (i < count) sourceBytes.Slice(i).CopyTo(destination.Slice(i));
            }
            else if (Vector128.IsHardwareAccelerated && sourceBytes.Length >= Vector128<byte>.Count)
            {
                int i = 0;
                int count = sourceBytes.Length;
                int vectorSize = Vector128<byte>.Count;
                fixed (byte* pSrc = sourceBytes, pDest = destination)
                {
                    for (; i <= count - vectorSize; i += vectorSize)
                    {
                        var v = Vector128.Load(pSrc + i);
                        v.Store(pDest + i);
                    }
                }
                if (i < count) sourceBytes.Slice(i).CopyTo(destination.Slice(i));
            }
            else
            {
                sourceBytes.CopyTo(destination);
            }
        }

        #region Variable Length Encoding (Int32, UInt32, Int64, UInt64)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32(ref byte[] buffer, ref int offset, int value)
        {
            var writer = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            WriteInt32(writer, value);
            buffer = writer.Buffer;
            offset = writer.Offset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32<TWriter>(TWriter writer, int value) where TWriter : IBufferWriter<byte>
        {
            var zigZag = EncodeZigZag32((long)value);
            WriteVarInt(writer, (ulong)zigZag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32(IBufferWriter<byte> writer, int value)
        {
            WriteInt32<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32(byte[] buffer, ref int offset)
        {
            var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset);
            var reader = new SequenceReader<byte>(seq);
            var result = ReadInt32(ref reader);
            offset += (int)reader.Consumed;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32(ref SequenceReader<byte> reader)
        {
            var zigZag = ReadVarInt(ref reader, 32);
            return (int)DecodeZigZag(zigZag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32(ref byte[] buffer, ref int offset, uint value)
        {
            var writer = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            WriteUInt32(writer, value);
            buffer = writer.Buffer;
            offset = writer.Offset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32<TWriter>(TWriter writer, uint value) where TWriter : IBufferWriter<byte>
        {
            WriteVarInt(writer, (ulong)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32(IBufferWriter<byte> writer, uint value)
        {
            WriteUInt32<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32(ref SequenceReader<byte> reader)
        {
            return (uint)ReadVarInt(ref reader, 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64(IBufferWriter<byte> writer, long value)
        {
            WriteInt64<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64<TWriter>(TWriter writer, long value) where TWriter : IBufferWriter<byte>
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
            WriteUInt64<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64<TWriter>(TWriter writer, ulong value) where TWriter : IBufferWriter<byte>
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
        public static void Write<TWriter>(TWriter writer, ReadOnlySpan<byte> data) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(data.Length);
            data.CopyTo(span);
            writer.Advance(data.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void WriteVarInt<TWriter>(TWriter writer, ulong value) where TWriter : IBufferWriter<byte>
        {
            // Determine how many bytes we need
            // each byte stores 7 bits.
            // 0..127 -> 1 byte (lzcnt 57..64)
            // 128..16383 -> 2 bytes (lzcnt 50..56)
            // ...
            
            int lzcnt = BitOperations.LeadingZeroCount(value | 1); // | 1 to handle 0
            int bits = 64 - lzcnt;
            int bytesNeeded = (bits + 6) / 7;
            if (bytesNeeded == 0) bytesNeeded = 1;

            var span = writer.GetSpan(bytesNeeded);
            
            // Unrolled loop for common cases
            if (bytesNeeded == 1)
            {
                span[0] = (byte)value;
            }
            else if (bytesNeeded == 2)
            {
                span[0] = (byte)((value & 0x7F) | 0x80);
                span[1] = (byte)(value >> 7);
            }
            else if (bytesNeeded == 3)
            {
                span[0] = (byte)((value & 0x7F) | 0x80);
                span[1] = (byte)((value >> 7 & 0x7F) | 0x80);
                span[2] = (byte)(value >> 14);
            }
            else
            {
                // Fallback for large values
                int written = 0;
                while (value >= 0x80)
                {
                    span[written++] = (byte)((value & 0x7F) | 0x80);
                    value >>= 7;
                }
                span[written++] = (byte)value;
            }
            
            writer.Advance(bytesNeeded);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ReadVarInt(ref SequenceReader<byte> reader, int bits)
        {
            // Fast path for 1-byte values
            if (reader.TryPeek(out byte b) && b < 0x80)
            {
                reader.Advance(1);
                return b;
            }

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
            WriteUInt32Bias<IBufferWriter<byte>>(writer, value, bias);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32Bias<TWriter>(TWriter writer, int value, int bias) where TWriter : IBufferWriter<byte>
        {
            WriteVarInt(writer, (ulong)((long)value + bias));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadUInt32Bias(byte[] buffer, ref int offset, int bias)
        {
            var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset);
            var reader = new SequenceReader<byte>(seq);
            var result = ReadUInt32Bias(ref reader, bias);
            offset += (int)reader.Consumed;
            return result;
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
        public static void WriteGuid<TWriter>(TWriter writer, Guid value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(16);
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value);
            writer.Advance(16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteGuid(IBufferWriter<byte> writer, Guid value) => WriteGuid<IBufferWriter<byte>>(writer, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid ReadGuid(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 16)
            {
                Guid val = Unsafe.ReadUnaligned<Guid>(ref MemoryMarshal.GetReference(reader.UnreadSpan));
                reader.Advance(16);
                return val;
            }
            Span<byte> temp = stackalloc byte[16];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(16);
            return Unsafe.ReadUnaligned<Guid>(ref MemoryMarshal.GetReference(temp));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDateTime<TWriter>(TWriter writer, DateTime value) where TWriter : IBufferWriter<byte>
        {
            WriteInt64Fixed(writer, value.ToBinary());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDateTime(IBufferWriter<byte> writer, DateTime value) => WriteDateTime<IBufferWriter<byte>>(writer, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DateTime ReadDateTime(ref SequenceReader<byte> reader)
        {
            return DateTime.FromBinary(ReadInt64Fixed(ref reader));
        }

        /// <summary>
        /// Fixed 10-byte <see cref="DateTimeOffset"/> encoding: 8-byte ticks (<c>value.Ticks</c>, the
        /// local wall-clock ticks), then 2-byte offset in minutes. Matches <c>DateTimeOffsetFormatter</c>
        /// and the migration interpreter. The offset is limited to ±13:59 (a valid
        /// <see cref="TimeSpan"/> offset), so it fits in the signed 16-bit range.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDateTimeOffset<TWriter>(TWriter writer, DateTimeOffset value) where TWriter : IBufferWriter<byte>
        {
            WriteInt64Fixed(writer, value.Ticks);
            WriteInt16Fixed(writer, (short)value.Offset.TotalMinutes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDateTimeOffset(IBufferWriter<byte> writer, DateTimeOffset value) => WriteDateTimeOffset<IBufferWriter<byte>>(writer, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DateTimeOffset ReadDateTimeOffset(ref SequenceReader<byte> reader)
        {
            long ticks = ReadInt64Fixed(ref reader);
            short minutes = ReadInt16Fixed(ref reader);
            try
            {
                return new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes));
            }
            catch (ArgumentException e)
            {
                // The bytes decoded fine but describe a value no CLR instance can hold (ticks past
                // DateTime.MaxValue, or an offset outside the ±14h the BCL allows). That is corruption,
                // not a decoder bug, and must be reported like every other invalid payload.
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt DateTimeOffset payload: ticks {ticks} with offset {minutes} minutes cannot be represented " +
                    $"(a DateTimeOffset needs ticks within [0, {DateTime.MaxValue.Ticks}] and an offset within ±14 hours).", e);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteTimeSpan<TWriter>(TWriter writer, TimeSpan value) where TWriter : IBufferWriter<byte>
        {
            WriteInt64Fixed(writer, value.Ticks);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteTimeSpan(IBufferWriter<byte> writer, TimeSpan value) => WriteTimeSpan<IBufferWriter<byte>>(writer, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TimeSpan ReadTimeSpan(ref SequenceReader<byte> reader)
        {
            return new TimeSpan(ReadInt64Fixed(ref reader));
        }

        /// <summary>
        /// Fixed 16-byte decimal encoding: the four <see cref="decimal.GetBits(decimal)"/> words in
        /// order (lo, mid, hi, flags), each little-endian. This is the layout described by
        /// <see cref="Ceras.Format.WellKnownTypes.Decimal"/> and read by the migration interpreter, so
        /// the writer, the generated reader and the interpreter all agree.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDecimal<TWriter>(TWriter writer, decimal value) where TWriter : IBufferWriter<byte>
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            var span = writer.GetSpan(16);
            BinaryPrimitives.WriteInt32LittleEndian(span, bits[0]);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), bits[1]);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), bits[2]);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), bits[3]);
            writer.Advance(16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDecimal(IBufferWriter<byte> writer, decimal value) => WriteDecimal<IBufferWriter<byte>>(writer, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static decimal ReadDecimal(ref SequenceReader<byte> reader)
        {
            Span<int> bits = stackalloc int[4];
            Span<byte> temp = stackalloc byte[16];
            if (reader.UnreadSpan.Length >= 16)
            {
                bits[0] = BinaryPrimitives.ReadInt32LittleEndian(reader.UnreadSpan);
                bits[1] = BinaryPrimitives.ReadInt32LittleEndian(reader.UnreadSpan.Slice(4));
                bits[2] = BinaryPrimitives.ReadInt32LittleEndian(reader.UnreadSpan.Slice(8));
                bits[3] = BinaryPrimitives.ReadInt32LittleEndian(reader.UnreadSpan.Slice(12));
                reader.Advance(16);
            }
            else
            {
                if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
                reader.Advance(16);
                bits[0] = BinaryPrimitives.ReadInt32LittleEndian(temp);
                bits[1] = BinaryPrimitives.ReadInt32LittleEndian(temp.Slice(4));
                bits[2] = BinaryPrimitives.ReadInt32LittleEndian(temp.Slice(8));
                bits[3] = BinaryPrimitives.ReadInt32LittleEndian(temp.Slice(12));
            }
            return ToDecimal(bits);
        }

        /// <summary>
        /// Builds a CLR <see cref="decimal"/> from four decoded words, reporting an impossible
        /// scale/sign/reserved-bit combination as data corruption instead of letting the BCL
        /// constructor's <see cref="ArgumentException"/> escape the decoder.
        /// </summary>
        static decimal ToDecimal(ReadOnlySpan<int> bits)
        {
            try
            {
                return new decimal(bits);
            }
            catch (ArgumentException e)
            {
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt decimal payload: the four words [{bits[0]}, {bits[1]}, {bits[2]}, {bits[3]}] " +
                    "(lo, mid, hi, flags) describe a value no decimal can hold (the scale in flags bits 16-23 must be " +
                    "0-28, and bits 6-30 are reserved and must be zero).", e);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteByte(IBufferWriter<byte> writer, byte value)
        {
            WriteByte<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteByte<TWriter>(TWriter writer, byte value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(1);
            span[0] = value;
            writer.Advance(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReadByte(byte[] buffer, ref int offset)
        {
            var val = buffer[offset];
            offset += 1;
            return val;
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
            WriteInt16Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt16Fixed<TWriter>(TWriter writer, short value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(2);
            BinaryPrimitives.WriteInt16LittleEndian(span, value);
            writer.Advance(2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt16Fixed(IBufferWriter<byte> writer, ushort value)
        {
            WriteUInt16Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt16Fixed<TWriter>(TWriter writer, ushort value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(2);
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
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
        public static ushort ReadUInt16Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 2)
            {
                ushort val = BinaryPrimitives.ReadUInt16LittleEndian(reader.UnreadSpan);
                reader.Advance(2);
                return val;
            }
            Span<byte> temp = stackalloc byte[2];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(2);
            return BinaryPrimitives.ReadUInt16LittleEndian(temp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32Fixed(IBufferWriter<byte> writer, int value)
        {
            WriteInt32Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32Fixed<TWriter>(TWriter writer, int value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(4);
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            writer.Advance(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32Fixed(byte[] buffer, ref int offset)
        {
            var val = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            offset += 4;
            return val;
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
            WriteUInt32Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32Fixed<TWriter>(TWriter writer, uint value) where TWriter : IBufferWriter<byte>
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
            WriteInt64Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64Fixed<TWriter>(TWriter writer, long value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(8);
            BinaryPrimitives.WriteInt64LittleEndian(span, value);
            writer.Advance(8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64Fixed(IBufferWriter<byte> writer, ulong value)
        {
            WriteUInt64Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64Fixed<TWriter>(TWriter writer, ulong value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(8);
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
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
        public static ulong ReadUInt64Fixed(ref SequenceReader<byte> reader)
        {
            if (reader.UnreadSpan.Length >= 8)
            {
                ulong val = BinaryPrimitives.ReadUInt64LittleEndian(reader.UnreadSpan);
                reader.Advance(8);
                return val;
            }
            Span<byte> temp = stackalloc byte[8];
            if (!reader.TryCopyTo(temp)) throw new InvalidOperationException("End of sequence");
            reader.Advance(8);
            return BinaryPrimitives.ReadUInt64LittleEndian(temp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFloat32Fixed(ref byte[] buffer, ref int offset, float value)
        {
            var writer = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            WriteFloat32Fixed(writer, value);
            buffer = writer.Buffer;
            offset = writer.Offset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFloat32Fixed(IBufferWriter<byte> writer, float value)
        {
            WriteFloat32Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFloat32Fixed<TWriter>(TWriter writer, float value) where TWriter : IBufferWriter<byte>
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
            WriteDouble64Fixed<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDouble64Fixed<TWriter>(TWriter writer, double value) where TWriter : IBufferWriter<byte>
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
        public static void WriteString(ref byte[] buffer, ref int offset, string value)
        {
            var writer = new CerasSerializer.FixedArrayBufferWriter(buffer, offset);
            WriteString(writer, value);
            buffer = writer.Buffer;
            offset = writer.Offset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteString(IBufferWriter<byte> writer, string value)
        {
            WriteString<IBufferWriter<byte>>(writer, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteString<TWriter>(TWriter writer, string value) where TWriter : IBufferWriter<byte>
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
        public static string ReadString(byte[] buffer, ref int offset)
        {
            var seq = new ReadOnlySequence<byte>(buffer, offset, buffer.Length - offset);
            var reader = new SequenceReader<byte>(seq);
            var result = ReadString(ref reader);
            offset += (int)reader.Consumed;
            return result;
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
            if (buffer.Length >= 0x4000)
                CerasBufferPool.Return(buffer);
            buffer = newBuffer;
        }

        #endregion
    }
}
