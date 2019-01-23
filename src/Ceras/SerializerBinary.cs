namespace Ceras
{
	using System;
	using System.Runtime.CompilerServices;
	using System.Text;

	/*
	 * We've benchmarked all sorts of ways to write stuff:
	 * - for each byte: buffer[offset + 0] = unchecked((byte)(value >> (7 * 8)));
	 * - fixed(...) *((long*)(pBuffer + offset)) = value;
	 * - ...
	 *
	 * The "Unsafe"/Pointer way was always the fastest in 32bit as well as 64bit.
	 * Ranging from 5x faster to "only" 1.8x
	*/
	public static unsafe class SerializerBinary
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt16Fixed(ref byte[] buffer, ref int offset, short value)
		{
			EnsureCapacity(ref buffer, offset, 2);

			fixed (byte* pBuffer = buffer)
			{
				*((short*)(pBuffer + offset)) = value;
			}

			offset += 2;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static short ReadInt16Fixed(byte[] buffer, ref int offset)
		{
			short value;

			fixed (byte* pBuffer = buffer)
			{
				value = *((short*)(pBuffer + offset));
			}

			offset += 2;
			return value;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt32(ref byte[] buffer, ref int offset, int value)
		{
			EnsureCapacity(ref buffer, offset, 4 + 1);

			var zigZag = EncodeZigZag((long)value, 32);
			WriteVarInt(ref buffer, ref offset, (ulong)zigZag);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int ReadInt32(byte[] buffer, ref int offset)
		{
			var zigZag = ReadVarInt(buffer, ref offset, 32);
			return (int)DecodeZigZag(zigZag);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteUInt32(ref byte[] buffer, ref int offset, uint value)
		{
			EnsureCapacity(ref buffer, offset, 4 + 1);

			WriteVarInt(ref buffer, ref offset, (ulong)value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static uint ReadUInt32(byte[] buffer, ref int offset)
		{
			return (uint)ReadVarInt(buffer, ref offset, 32);
		}


		#region Specialized

		//
		// In our formatters we're often using 0-n for someID or count, and a limited set of negative numbers (often only -2 and -1) to signify some special cases, like "null" or something like that.
		// The following method pair optimizes this case a lot.
		// We can avoid useless ZigZag encoding because we know the values will never be negative.
		// And to ensure that a bias is given to the method to move the number into the right range.
		// This saves both time (no zigzag) and memory (twice the amount of numbers available)
		//
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteUInt32Bias(ref byte[] buffer, ref int offset, int value, int bias)
		{
			value += bias;

			EnsureCapacity(ref buffer, offset, 4 + 1);

			WriteVarInt(ref buffer, ref offset, (ulong)value);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int ReadUInt32Bias(byte[] buffer, ref int offset, int bias)
		{
			var value = (int)ReadVarInt(buffer, ref offset, 32);
			value -= bias;
			return value;
		}


		// Same as WriteUInt32Bias, but without the capacity size check (make sure you reserve 5 bytes for this method)
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteUInt32BiasNoCheck(ref byte[] buffer, ref int offset, int value, int bias)
		{
			value += bias;
			WriteVarInt(ref buffer, ref offset, (ulong)value);
		}


		#endregion


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt64(ref byte[] buffer, ref int offset, long value)
		{
			EnsureCapacity(ref buffer, offset, 8 + 1);

			var zigZag = EncodeZigZag((long)value, 64);
			WriteVarInt(ref buffer, ref offset, (ulong)zigZag);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long ReadInt64(byte[] buffer, ref int offset)
		{
			var zigZag = ReadVarInt(buffer, ref offset, 64);
			return (int)DecodeZigZag(zigZag);
		}



		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt64Fixed(ref byte[] buffer, ref int offset, long value)
		{
			EnsureCapacity(ref buffer, offset, 8);

			fixed (byte* pBuffer = buffer)
			{
				*((long*)(pBuffer + offset)) = value;
			}

			offset += 8;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long ReadInt64Fixed(byte[] buffer, ref int offset)
		{
			long value;

			fixed (byte* pBuffer = buffer)
			{
				value = *((long*)(pBuffer + offset));
			}

			offset += 8;
			return value;
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt32Fixed(ref byte[] buffer, ref int offset, int value)
		{
			EnsureCapacity(ref buffer, offset, 4);

			fixed (byte* pBuffer = buffer)
			{
				*((int*)(pBuffer + offset)) = value;
			}

			offset += 4;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int ReadInt32Fixed(byte[] buffer, ref int offset)
		{
			int value;

			fixed (byte* pBuffer = buffer)
			{
				value = *((int*)(pBuffer + offset));
			}

			offset += 4;
			return value;
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteUInt32Fixed(ref byte[] buffer, ref int offset, uint value)
		{
			EnsureCapacity(ref buffer, offset, 4);

			fixed (byte* pBuffer = buffer)
			{
				*((uint*)(pBuffer + offset)) = value;
			}

			offset += 4;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static uint ReadUInt32Fixed(byte[] buffer, ref int offset)
		{
			uint value;

			fixed (byte* pBuffer = buffer)
			{
				value = *((uint*)(pBuffer + offset));
			}

			offset += 4;
			return value;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteByte(ref byte[] buffer, ref int offset, byte value)
		{
			EnsureCapacity(ref buffer, offset, 1);

			buffer[offset] = value;
			offset += 1;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static byte ReadByte(byte[] buffer, ref int offset)
		{
			var b = buffer[offset];
			offset += 1;
			return b;
		}



		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void WriteVarInt(ref byte[] buffer, ref int offset, ulong value)
		{
			do
			{
				var byteVal = value & 0x7f;
				value >>= 7;

				if (value != 0)
					byteVal |= 0x80;

				buffer[offset++] = (byte)byteVal;

			} while (value != 0);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static ulong ReadVarInt(byte[] bytes, ref int offset, int bits)
		{
			int shift = 0;
			ulong result = 0;

			while (true)
			{
				ulong byteValue = bytes[offset++];
				ulong tmp = byteValue & 0x7f;
				result |= tmp << shift;

				if (shift > bits)
					throw new ArgumentOutOfRangeException(nameof(bytes), "Malformed VarInt");

				if ((byteValue & 0x80) != 0x80)
					return result;

				shift += 7;
			}
		}
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteFloat32FixedNoCheck(ref byte[] buffer, ref int offset, float value)
		{
			fixed (byte* pBuffer = buffer)
			{
				*((float*)(pBuffer + offset)) = value;
			}

			offset += 4;
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteFloat32Fixed(ref byte[] buffer, ref int offset, float value)
		{
			EnsureCapacity(ref buffer, offset, 4);

			fixed (byte* pBuffer = buffer)
			{
				*((float*)(pBuffer + offset)) = value;
			}

			offset += 4;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float ReadFloat32Fixed(byte[] buffer, ref int offset)
		{
			float d;
			fixed (byte* pBuffer = buffer)
			{
				d = *((float*)(pBuffer + offset));
			}

			offset += 4;

			return d;
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteDouble64Fixed(ref byte[] buffer, ref int offset, double value)
		{
			EnsureCapacity(ref buffer, offset, 8);

			fixed (byte* pBuffer = buffer)
			{
				*((double*)(pBuffer + offset)) = value;
			}

			offset += 8;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double ReadDouble64Fixed(byte[] buffer, ref int offset)
		{
			double d;
			fixed (byte* pBuffer = buffer)
			{
				d = *((double*)(pBuffer + offset));
			}

			offset += 8;

			return d;
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteGuid(ref byte[] buffer, ref int offset, Guid value)
		{
			EnsureCapacity(ref buffer, offset, 16);

			fixed (byte* dst = &buffer[offset])
			{
				var src = &value;
				*(Guid*)dst = *src;
			}

			offset += 16;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Guid ReadGuid(byte[] buffer, ref int offset)
		{
			Guid guid;

			fixed (byte* src = &buffer[offset])
			{
				guid = *(Guid*)src;
			}

			offset += 16;

			return guid;
		}


		static readonly UTF8Encoding _utf8Encoding = new UTF8Encoding(false, true);


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteString(ref byte[] buffer, ref int offset, string value)
		{
			if (value == null)
			{
				WriteUInt32Bias(ref buffer, ref offset, -1, 1);
				return;
			}

			// todo: maybe we can replace Encoding.UTF8.GetByteCount with reasonable estimation?
			// default implementation Encoding.GetByteCount still allocates a new array
			// but Encoding.UTF8 overrides it with more efficient implementation.
			// If Encoding.UTF8 will be replaced in future - original implementation
			// might be even faster.

			var encoding = _utf8Encoding;
			
			var valueBytesCount = encoding.GetByteCount(value);
			EnsureCapacity(ref buffer, offset, valueBytesCount + 5); // 5 bytes space for the VarInt

			// Length
			WriteUInt32BiasNoCheck(ref buffer, ref offset, valueBytesCount, 1);

			var realBytesCount = encoding.GetBytes(value, 0, value.Length, buffer, offset);
			offset += realBytesCount;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static string ReadString(byte[] buffer, ref int offset)
		{
			// Length
			int length = ReadUInt32Bias(buffer, ref offset, 1);

			if (length == -1)
				return null;

			// Data
			var str = _utf8Encoding.GetString(buffer, offset, length);
			offset += length;
			
			return str;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static string ReadStringLimited(byte[] buffer, ref int offset, uint maxLength)
		{
			// Length
			int length = ReadUInt32Bias(buffer, ref offset, 1);

			if (length == -1)
				return null;

			if ((uint) length > maxLength)
				throw new InvalidOperationException($"The current data contains a string of length '{length}', but the maximum allowed string length is '{maxLength}'");


			// Data
			var str = _utf8Encoding.GetString(buffer, offset, length);
			offset += length;
			
			return str;
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long EncodeZigZag(long value, int bitLength)
		{
			return (value << 1) ^ (value >> (bitLength - 1));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long DecodeZigZag(ulong value)
		{
			if ((value & 0x1) == 0x1)
			{
				return (-1 * ((long)(value >> 1) + 1));
			}

			return (long)(value >> 1);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void EnsureCapacity(ref byte[] buffer, int offset, int size)
		{
			int newSize = offset + size;

			if (buffer.Length >= newSize)
				return;

			if (newSize < 0x4000)
				newSize = 0x4000;
			else
				newSize *= 2;

			FastResize(ref buffer, newSize);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void FastResize(ref byte[] array, int newSize)
		{
			if (newSize < 0)
				throw new ArgumentOutOfRangeException(nameof(newSize));

			byte[] array2 = array;
			if (array2 == null)
			{
				array = new byte[newSize];
				return;
			}

			if (array2.Length != newSize)
			{
				byte[] array3 = new byte[newSize];
				Buffer.BlockCopy(array2, 0, array3, 0, (array2.Length > newSize) ? newSize : array2.Length);
				array = array3;
			}
		}
	}
}