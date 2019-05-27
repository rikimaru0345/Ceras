namespace Ceras
{
	using System;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;
	using System.Text;



	public static unsafe class SerializerBinary
	{
		#region Variable Length Encoding (Int32, UInt32, Int64, UInt64)

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt32(ref byte[] buffer, ref int offset, int value)
		{
			EnsureCapacity(ref buffer, offset, 4 + 1);

			var zigZag = EncodeZigZag32((long)value);
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



		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt64(ref byte[] buffer, ref int offset, long value)
		{
			EnsureCapacity(ref buffer, offset, 8 + 1);

			var zigZag = EncodeZigZag64((long)value);
			WriteVarInt(ref buffer, ref offset, (ulong)zigZag);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long ReadInt64(byte[] buffer, ref int offset)
		{
			var zigZag = ReadVarInt(buffer, ref offset, 64);
			return (int)DecodeZigZag(zigZag);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteUInt64(ref byte[] buffer, ref int offset, ulong value)
		{
			EnsureCapacity(ref buffer, offset, 8 + 1);

			var zigZag = EncodeZigZag64((long)value);
			WriteVarInt(ref buffer, ref offset, (ulong)zigZag);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ulong ReadUInt64(byte[] buffer, ref int offset)
		{
			var zigZag = ReadVarInt(buffer, ref offset, 64);
			return (ulong)DecodeZigZag(zigZag);
		}

		

		// public static long EncodeZigZag(long value, int bitLength) => (value << 1) ^ (value >> (bitLength - 1));

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


		
		// todo: 
		// 1. Test if unrolling (to max 9 bytes) is faster than our loop
		// 2. I've read about some optimization that could be applied to the protobuf varint writer, maybe the same can be done here?
		// 3. Would it make any sense to use unsafe code to address into the buffer directly (skipping bounds checks?)
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

		#endregion


		#region Specialized (Bias, NoCapacityCheck)

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
		public static void WriteUInt32BiasNoCheck(byte[] buffer, ref int offset, int value, int bias)
		{
			value += bias;
			WriteVarInt(ref buffer, ref offset, (ulong)value);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteUInt32NoCheck(byte[] buffer, ref int offset, uint value)
		{
			WriteVarInt(ref buffer, ref offset, (ulong)value);
		}

		#endregion

		
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
		public static void WriteInt16Fixed(ref byte[] buffer, ref int offset, short value)
		{
			EnsureCapacity(ref buffer, offset, 2);
			Unsafe.As<byte, short>(ref buffer[offset]) = value;
			offset += 2;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static short ReadInt16Fixed(byte[] buffer, ref int offset)
		{
			var value = Unsafe.As<byte, short>(ref buffer[offset]);
			offset += 2;
			return value;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt32Fixed(ref byte[] buffer, ref int offset, int value)
		{
			EnsureCapacity(ref buffer, offset, 4);
			Unsafe.As<byte, int>(ref buffer[offset]) = value;
			offset += 4;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int ReadInt32Fixed(byte[] buffer, ref int offset)
		{
			var value = Unsafe.As<byte, int>(ref buffer[offset]);
			offset += 4;
			return value;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteUInt32Fixed(ref byte[] buffer, ref int offset, uint value)
		{
			EnsureCapacity(ref buffer, offset, 4);
			Unsafe.As<byte, uint>(ref buffer[offset]) = value;
			offset += 4;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static uint ReadUInt32Fixed(byte[] buffer, ref int offset)
		{
			var value = Unsafe.As<byte, uint>(ref buffer[offset]);
			offset += 4;
			return value;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt64Fixed(ref byte[] buffer, ref int offset, long value)
		{
			EnsureCapacity(ref buffer, offset, 8);
			Unsafe.As<byte, long>(ref buffer[offset]) = value;
			offset += 8;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static long ReadInt64Fixed(byte[] buffer, ref int offset)
		{
			var value = Unsafe.As<byte, long>(ref buffer[offset]);
			offset += 8;
			return value;
		}



		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteFloat32Fixed(ref byte[] buffer, ref int offset, float value)
		{
			EnsureCapacity(ref buffer, offset, 4);
			Unsafe.As<byte, float>(ref buffer[offset]) = value;
			offset += 4;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float ReadFloat32Fixed(byte[] buffer, ref int offset)
		{
			var value = Unsafe.As<byte, float>(ref buffer[offset]);
			offset += 4;
			return value;
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteFloat32FixedNoCheck(byte[] buffer, ref int offset, float value)
		{
			Unsafe.As<byte, float>(ref buffer[offset]) = value;
			offset += 4;
		}



		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteDouble64Fixed(ref byte[] buffer, ref int offset, double value)
		{
			EnsureCapacity(ref buffer, offset, 8);
			Unsafe.As<byte, double>(ref buffer[offset]) = value;
			offset += 8;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static double ReadDouble64Fixed(byte[] buffer, ref int offset)
		{
			var value = Unsafe.As<byte, double>(ref buffer[offset]);
			offset += 8;
			return value;
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
			WriteUInt32BiasNoCheck(buffer, ref offset, valueBytesCount, 1);

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

			if ((uint)length > maxLength)
				throw new InvalidOperationException($"The current data contains a string of length '{length}', but the maximum allowed string length is '{maxLength}'");


			// Data
			var str = _utf8Encoding.GetString(buffer, offset, length);
			offset += length;

			return str;
		}



		/// <summary>
		/// Copy up to 512 bytes in a fast path, else fallback to Unsafe.CopyBlock
		/// <para>Important: Does not handle size==0 !</para>
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void FastCopy(byte[] sourceArray, uint sourceOffset, byte[] targetArray, uint targetOffset, uint size)
		{
			Debug.Assert(sourceArray != null, "sourceArray must not be null");
			Debug.Assert(targetArray != null, "targetArray must not be null");

			// Can't handle 0 because we use '&ar[0]' to bypass bounds check while pinning
			Debug.Assert(size != 0, "FastCopy must not be called with size=0");


#if DEBUG
			// MultidimensionalReinterpretArrayFormatter will unsafe-cast its array to byte[], so the comparison will actually fail.
			// We shouldn't compare (or even access) '.Length' of an multidimensional array.
			bool canUseLength = sourceArray.GetType() == typeof(byte[]) && sourceArray.GetType() == targetArray.GetType();

			if (canUseLength)
				// Detect overflow (reading or writing beyond the end)
				Debug.Assert(size <= (sourceArray.Length - sourceOffset) && size <= (targetArray.Length - targetOffset), $"FastCopy would overflow source or target! (sourceArray.Length={sourceArray.Length}, sourceOffset={sourceOffset}, targetArray.Length={targetArray.Length}, targetOffset={targetOffset}, size={size})");
#endif


			fixed (byte* srcPtr = &sourceArray[0])
			fixed (byte* destPtr = &targetArray[0])
			{
				byte* src = srcPtr + sourceOffset;
				byte* dst = destPtr + targetOffset;

				FastCopy(src, dst, size);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void FastCopy(byte* src, byte* dest, uint size)
		{
			// Check for the small case (<=16 bytes) first because that's where every nanosecond counts.
			SMALLTABLE:
			switch (size)
			{
			case 16:
				*(long*)dest = *(long*)src;
				*(long*)(dest + 8) = *(long*)(src + 8);
				return;
			case 15:
				*(short*)(dest + 12) = *(short*)(src + 12);
				*(dest + 14) = *(src + 14);
				goto case 12;
			case 14:
				*(short*)(dest + 12) = *(short*)(src + 12);
				goto case 12;
			case 13:
				*(dest + 12) = *(src + 12);
				goto case 12;
			case 12:
				*(long*)dest = *(long*)src;
				*(int*)(dest + 8) = *(int*)(src + 8);
				return;
			case 11:
				*(short*)(dest + 8) = *(short*)(src + 8);
				*(dest + 10) = *(src + 10);
				goto case 8;
			case 10:
				*(short*)(dest + 8) = *(short*)(src + 8);
				goto case 8;
			case 9:
				*(dest + 8) = *(src + 8);
				goto case 8;
			case 8:
				*(long*)dest = *(long*)src;
				return;
			case 7:
				*(short*)(dest + 4) = *(short*)(src + 4);
				*(dest + 6) = *(src + 6);
				goto case 4;
			case 6:
				*(short*)(dest + 4) = *(short*)(src + 4);
				goto case 4;
			case 5:
				*(dest + 4) = *(src + 4);
				goto case 4;
			case 4:
				*(int*)dest = *(int*)src;
				return;
			case 3:
				*(dest + 2) = *(src + 2);
				goto case 2;
			case 2:
				*(short*)dest = *(short*)src;
				return;
			case 1:
				*dest = *src;
				return;
			case 0:
				return;
			default:
				break;
			}

			// For anything above 512 bytes the framework has really optimized code (using SIMD and whatnot)
			if (size > 512)
			{
				Unsafe.CopyBlock((void*)dest, (void*)src, size);
				return;
			}

			// The 'medium' case is anything between 16 and 512 bytes
			// Copy in 32byte blocks...
			uint dwordCount = size / 32;
			size -= (size / 32) * 32;

			while (dwordCount > 0)
			{
				((long*)dest)[0] = ((long*)src)[0]; // 8
				((long*)dest)[1] = ((long*)src)[1]; // 16
				((long*)dest)[2] = ((long*)src)[2]; // 24
				((long*)dest)[3] = ((long*)src)[3]; // 32

				dest += 32;
				src += 32;
				dwordCount--;
			}

			// Single 16 byte block...
			if (size > 16)
			{
				((long*)dest)[0] = ((long*)src)[0];
				((long*)dest)[1] = ((long*)src)[1];

				src += 16;
				dest += 16;
				size -= 16;
			}

			// We'll have anything between 0 and 16 bytes left
			goto SMALLTABLE;
		}


		// Microsoft docs: "0x7fffffc7 for byte arrays and arrays of single-byte structures"
		const int MaximumArraySize = 0x7fffffc7;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void EnsureCapacity(ref byte[] buffer, int offset, int size)
		{
			// Fast path
			int newSize = offset + size;
			if (buffer.Length >= newSize)
				return;

			// Slow path
			// We now know that we'll end up having to do a resize.
			// Compared to the fast-path above, this will take a lot longer and is also very rare!
			// That means we won't have to spend much time optimizing any calculations that come next,
			// since any potential gains won't even be measurable because of the alloc/copy operation.
			ExpandBuffer(ref buffer, newSize);
		}

		internal static void ExpandBuffer(ref byte[] buffer, int newSize)
		{
			ThrowIfBufferTooLarge(newSize);

			if (newSize < 0x4000)
			{
				newSize = 0x4000;
			}
			else
			{
				// Increase base buffer size until we have enough space
				var size = 0x4000;
				while (size < newSize)
				{
					size = unchecked(size * 2);
					if (size < 0)
					{
						size = MaximumArraySize;
						break;
					}
				}

				newSize = size;
			}

			FastResize(ref buffer, newSize);
		}

		static void FastResize(ref byte[] buffer, int newSize)
		{
			var oldBuffer = buffer;

			#if DEBUG
			if (newSize <= 0)
				throw new ArgumentOutOfRangeException(nameof(newSize));
			if (newSize <= oldBuffer.Length)
				throw new ArgumentOutOfRangeException(nameof(newSize) + " cannot be smaller than (or equal to) the old size");
			#endif


			// Get a new buffer
			var pool = CerasBufferPool.Pool;
			byte[] newBuffer = pool.RentBuffer(newSize);

			// Copy what we've written so far into the new buffer
			fixed (byte* source = &oldBuffer[0])
			fixed (byte* target = &newBuffer[0])
				Unsafe.CopyBlock(target, source, (uint)oldBuffer.Length);

			// Return the old buffer
			pool.Return(buffer);

			// And replace the current buffer reference
			buffer = newBuffer;
		}

		static void ThrowIfBufferTooLarge(int newSize)
		{
			if (newSize > MaximumArraySize || newSize < 0)
				throw new InvalidOperationException($"Trying to expand a buffer to {newSize} bytes, which is greater than the maximum allowed size {MaximumArraySize}. This is a limitation of the runtime, but you can either use IExternalRootObject to split your object graph into parts (if there is no single element that is causing this), or write a custom formatter if you have a single huge element that is causing this. Checkout the GitHub page for more information.");
		}

	}
}