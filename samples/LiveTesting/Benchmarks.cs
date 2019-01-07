using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;
using static Ceras.SerializerBinary;

namespace LiveTesting
{
	[ClrJob]
	[RankColumn]
	public class SerializerBinaryBenchmarks
	{
		int[] numbers;
		byte[] data;

		int offset;

		const int oneByteVarInt_LowerLimit = -63;
		const int oneByteVarInt_UpperLimit = 64;
		static byte[] varIntLookupTable;

		/*
        [Params(1000, 10000)]
        public int N;
		*/
		[GlobalSetup]
		public void Setup()
		{
			numbers = new int[1000];
			var rng = new Random(12346);

			for (int i = 0; i < numbers.Length; i++)
			{
				var mode = rng.Next(4);
				switch (mode)
				{
					case 0:
					numbers[i] = rng.Next(-127, 127);
					break;

					case 1:
					numbers[i] = rng.Next(byte.MaxValue, short.MaxValue);
					break;

					case 2:
					case 3:
					numbers[i] = rng.Next();
					break;
				}
			}

			byte[] buffer = new byte[5];
			varIntLookupTable = new byte[127];
			for (int i = oneByteVarInt_LowerLimit; i < oneByteVarInt_UpperLimit; i++)
			{
				int lookupBufferOffset = 0;

				var n = i; // -63 .. 64

				var biasedNumber = n - oneByteVarInt_LowerLimit; // 0 .. 127

				WriteInt32(ref buffer, ref lookupBufferOffset, n);

				if(lookupBufferOffset != 1)
					throw new Exception("error");

				varIntLookupTable[biasedNumber] = buffer[0];
			}

		}

		[Benchmark]
		public void Benchmark_NormalAlgorithmicEncoding()
		{
			offset = 0;
			for (int i = 0; i < numbers.Length; i++)
			{
				var n = numbers[i];
				WriteInt32_Normal(ref data, ref offset, n);
			}
		}

		[Benchmark]
		public void Benchmark_FastPathEncoding()
		{
			offset = 0;
			for (int i = 0; i < numbers.Length; i++)
			{
				var n = numbers[i];

				if (n >= oneByteVarInt_LowerLimit && n < oneByteVarInt_UpperLimit)
				{
					WriteInt32_OneByteFastPath(ref data, ref offset, n);
				}
				else
				{
					WriteInt32_Normal(ref data, ref offset, numbers[i]);
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt32_OneByteFastPath(ref byte[] buffer, ref int offset, int value)
		{
			EnsureCapacity(ref buffer, offset, 1);

			var lookupIndex = value - oneByteVarInt_LowerLimit;
			var encodedByte = varIntLookupTable[lookupIndex];

			buffer[offset] = encodedByte;
			offset++;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void WriteInt32_Normal(ref byte[] buffer, ref int offset, int value)
		{
			EnsureCapacity(ref buffer, offset, 4 + 1);

			var zigZag = EncodeZigZag((long)value, 32);
			WriteVarInt(ref buffer, ref offset, (ulong)zigZag);
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
		static long EncodeZigZag(long value, int bitLength)
		{
			return (value << 1) ^ (value >> (bitLength - 1));
		}

	}

}
