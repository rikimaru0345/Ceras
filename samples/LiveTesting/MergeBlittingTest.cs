using Ceras;
using Ceras.Formatters;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace LiveTesting
{
	class MergeBlittingTest
	{
		static byte[] _buffer = new byte[1000];
		static void DoTest(Vector3 value, IFormatter<Vector3> formatter)
		{
			int offset = 0;
			formatter.Serialize(ref _buffer, ref offset, value);

			offset = 0;
			Vector3 clone = default;
			formatter.Deserialize(_buffer, ref offset, ref clone);
		}

		internal static void Test()
		{
			var defaultF = new DefaultFormatter();
			var rawCall = new RawCallFormatter();
			var mergeBlit = new TestMergeBlitFormatter();
			var mergeBlitSafe = new TestMergeBlitSafeFormatter();
			var inlineFixed = new Inline_Fixed_Formatter();
			var inlineUnsafe = new Inline_Unsafe_Formatter();
			var reinterpret = new ReinterpretFormatter<Vector3>();

			var value = new Vector3(2325.123123f, -3524625.3424f, -0.2034324234234f);

			MicroBenchmark.Run(5, new BenchJob[]
			{
				("Default", () => DoTest(value, defaultF)),
				("RawCall", () => DoTest(value, rawCall)),
				("MergeBlit", () => DoTest(value, mergeBlit)),
				("MergeBlitSafe", () => DoTest(value, mergeBlitSafe)),
				("Reinterpret", () => DoTest(value, reinterpret)),
				("InlineFixed", () => DoTest(value, inlineFixed)),
				("InlineUnsafe", () => DoTest(value, inlineUnsafe)),
			});

			// - reinterpret (whole vector3)

			Console.WriteLine("done");
			Console.ReadKey();
		}


	}

	struct Vector3
	{
		public float X, Y, Z;

		public Vector3(float x, float y, float z)
		{
			X = x;
			Y = y;
			Z = z;
		}
	}

	class DefaultFormatter : IFormatter<Vector3>
	{
		IFormatter<float> _floatFormatter = new FloatFormatter();

		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			_floatFormatter.Serialize(ref buffer, ref offset, value.X);
			_floatFormatter.Serialize(ref buffer, ref offset, value.Y);
			_floatFormatter.Serialize(ref buffer, ref offset, value.Z);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			_floatFormatter.Deserialize(buffer, ref offset, ref value.X);
			_floatFormatter.Deserialize(buffer, ref offset, ref value.Y);
			_floatFormatter.Deserialize(buffer, ref offset, ref value.Z);
		}
	}

	class RawCallFormatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 3 * 4);

			ReinterpretFormatter<float>.Write_Raw(buffer, offset, value.X);
			ReinterpretFormatter<float>.Write_Raw(buffer, offset + 4, value.Y);
			ReinterpretFormatter<float>.Write_Raw(buffer, offset + 8, value.Z);

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			ReinterpretFormatter<float>.Read_Raw(buffer, offset, ref value.X);
			ReinterpretFormatter<float>.Read_Raw(buffer, offset + 4, ref value.Y);
			ReinterpretFormatter<float>.Read_Raw(buffer, offset + 8, ref value.Z);

			offset += 3 * 4;
		}
	}


	unsafe class TestMergeBlitFormatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 3 * 4);

			byte* basePtr = (byte*)Unsafe.AsPointer(ref buffer[0]);
			var ptr = basePtr + offset;

			Unsafe.Write<float>(ptr, value.X);
			Unsafe.Write<float>(ptr + 4, value.X);
			Unsafe.Write<float>(ptr + 8, value.X);

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			byte* basePtr = (byte*)Unsafe.AsPointer(ref buffer[0]);
			var ptr = basePtr + offset;

			value.X = Unsafe.Read<float>(ptr);
			value.Y = Unsafe.Read<float>(ptr + 4);
			value.Z = Unsafe.Read<float>(ptr + 8);

			offset += 3 * 4;
		}
	}


	class TestMergeBlitSafeFormatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 3 * 4);

			ref float p = ref Unsafe.As<byte, float>(ref buffer[offset]);
			p = value.X;

			p = ref Unsafe.Add(ref p, 1);
			p = value.Y;

			p = ref Unsafe.Add(ref p, 1);
			p = value.Z;

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			ref float p = ref Unsafe.As<byte, float>(ref buffer[offset]);
			value.X = p;

			p = ref Unsafe.Add(ref p, 1);
			value.Y = p;

			p = ref Unsafe.Add(ref p, 1);
			value.Z = p;


			offset += 3 * 4;
		}
	}


	unsafe class Inline_Fixed_Formatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 3 * 4);

			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (float*)(pBuffer + offset);
				*ptr = value.X;

				ptr++;
				*ptr = value.Y;

				ptr++;
				*ptr = value.Z;
			}

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (float*)(pBuffer + offset);
				value.X = *ptr;

				ptr++;
				value.Y = *ptr;

				ptr++;
				value.Z = *ptr;
			}

			offset += 3 * 4;
		}
	}

	unsafe class Inline_Unsafe_Formatter : IFormatter<Vector3>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, 3 * 4);

			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (byte*)(pBuffer + offset);

				Unsafe.Write<float>(ptr, value.X);
				Unsafe.Write<float>(ptr + 4, value.Y);
				Unsafe.Write<float>(ptr + 8, value.Z);
			}

			offset += 3 * 4;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value)
		{
			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (byte*)(pBuffer + offset);

				value.X = Unsafe.Read<float>(ptr);
				value.Y = Unsafe.Read<float>(ptr);
				value.Z = Unsafe.Read<float>(ptr);
			}

			offset += 3 * 4;
		}
	}
}