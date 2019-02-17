using System;
using System.Collections.Generic;

namespace Ceras.Test
{
	using Formatters;
	using System.Runtime.InteropServices;
	using Xunit;

	public class StructReinterpret : TestBase
	{
		static Type[] _blittableTypes =
		{
			typeof(int),
			typeof(bool),
			typeof(byte),
			typeof(sbyte),
			typeof(ulong),

			typeof(float),
			typeof(double),
			typeof(decimal),

			typeof(int[]),
			typeof(bool[]),
			typeof(float[]),
			typeof(double[]),
			typeof(decimal[]),

			typeof(Vector3),
			typeof(Half2),
			typeof(BigStruct),

			typeof(BigStruct[]),
			typeof(Vector3[]),


		};

		static Type[] _nonBlittableTypes =
		{
			typeof(IntPtr),
			typeof(UIntPtr),

			typeof(IntPtr[]),
			typeof(UIntPtr[]),

			typeof(TestBase),
			typeof(List<int>),
			typeof(List<object>),
			typeof(object),
			typeof(Type),
			typeof(Type).GetType(),
			typeof(string),
			
			// generics can never be blitted, they are ".IsAutoLayout" by definition
			typeof(ValueTuple<byte, uint>),
			typeof(ValueTuple<int, float, double, decimal>),
			typeof(ValueTuple<BigStruct, Vector3, byte>),

			typeof(ValueTuple<byte, uint>[]),
			typeof(ValueTuple<int, float, double, decimal>[]),
			typeof(ValueTuple<BigStruct, Vector3, byte>[]),

			typeof(ValueTuple<byte, string>),
			typeof(ValueTuple<byte, Type>),

			typeof(ValueTuple<object>),
			typeof(ValueTuple<string>[]),

			typeof(ValueTuple<int[]>),
			typeof(ValueTuple<byte[], uint>),
			typeof(ValueTuple<byte, uint>[]),
			typeof(ValueTuple<BigStruct, Vector3[], byte>),
		};

		static Type[] _blitFormattersOpenTypes =
		{
			typeof(ReinterpretArrayFormatter<>),
			typeof(ReinterpretFormatter<>),

			typeof(BoolFormatter),

			typeof(ByteFormatter),
			typeof(SByteFormatter),

			typeof(CharFormatter),

			typeof(Int16Formatter),
			typeof(UInt16Formatter),
			typeof(Int32Formatter),
			typeof(UInt32Formatter),
			typeof(Int64Formatter),
			typeof(UInt64Formatter),

			typeof(FloatFormatter),
			typeof(DoubleFormatter),
		};


		[Fact]
		public void Formatters()
		{
			var config = new SerializerConfig();
			config.Advanced.UseReinterpretFormatter = true;
			var ceras = new CerasSerializer(config);

			foreach (var t in _blittableTypes)
			{
				var formatter = ceras.GetSpecificFormatter(t);

				var ft = formatter.GetType();
				if (ft.IsGenericType)
					ft = ft.GetGenericTypeDefinition();

				Assert.Contains(ft, _blitFormattersOpenTypes);
			}

			foreach (var t in _nonBlittableTypes)
			{
				var formatter = ceras.GetSpecificFormatter(t);

				var ft = formatter.GetType();

				if (ft.IsGenericType)
					if (ft.GetGenericTypeDefinition() == typeof(ReinterpretFormatter<>) ||
						ft.GetGenericTypeDefinition() == typeof(ReinterpretArrayFormatter<>))
						throw new Exception("Computed formatter for type " + t.Name + " is ReinterpretFormatter, that's wrong!");
			}
		}

		[Fact]
		public unsafe void BigStruct()
		{
			BigStruct bigStruct = new BigStruct();
			var bigStructSize = Marshal.SizeOf(typeof(BigStruct));

			bigStruct.First = 0xff;
			bigStruct.Second = 0.123456789;

			bigStruct.Third[0] = 0x5E;
			bigStruct.Third[1] = 0xE5;
			bigStruct.Third[2] = 0x99;

			bigStruct.Fourth = new Half4(rngShort, rngShort, rngShort, rngShort);
			bigStruct.Fifth = '@';

			var v3Size = Marshal.SizeOf(typeof(Vector3));
			*((Vector3*)(bigStruct.Sixth + v3Size * 0)) = new Vector3(rngFloat, rngFloat, rngFloat);
			*((Vector3*)(bigStruct.Sixth + v3Size * 1)) = new Vector3(rngFloat, rngFloat, rngFloat);

			bigStruct.Eighth = 0x11_22_33_44___55_66_77_88;



			// Clone
			var ceras = new CerasSerializer();
			var serializedData = ceras.Serialize(bigStruct);
			var clone = ceras.Deserialize<BigStruct>(serializedData);


			// Direct equality
			Assert.Equal(bigStruct, clone);
			Assert.True(EqualityComparer<BigStruct>.Default.Equals(bigStruct, clone));


			// Manual equality
			Assert.Equal(bigStruct.First, clone.First);
			Assert.Equal(bigStruct.Second, clone.Second);

			Assert.Equal(bigStruct.Third[0], clone.Third[0]);
			Assert.Equal(bigStruct.Third[1], clone.Third[1]);
			Assert.Equal(bigStruct.Third[2], clone.Third[2]);

			Assert.Equal(bigStruct.Fourth, clone.Fourth);
			Assert.Equal(bigStruct.Fourth.Left, clone.Fourth.Left);
			Assert.Equal(bigStruct.Fourth.Right, clone.Fourth.Right);

			Assert.Equal(bigStruct.Fifth, clone.Fifth);
			Assert.Equal(*((Vector3*)(bigStruct.Sixth + v3Size * 0)), *((Vector3*)(clone.Sixth + v3Size * 0)));
			Assert.Equal(*((Vector3*)(bigStruct.Sixth + v3Size * 1)), *((Vector3*)(clone.Sixth + v3Size * 1)));


			// Binary equality
			var oriStructAddr = &bigStruct;
			var cloneStructAddr = &clone;

			Assert.Equal(oriStructAddr[0], cloneStructAddr[0]);

			// Byte equality
			{
				byte* oriBytes = (byte*)oriStructAddr;
				byte* cloneBytes = (byte*)cloneStructAddr;

				for (int i = 0; i < bigStructSize; i++)
				{
					Assert.Equal(oriBytes[i], cloneBytes[i]);
					Assert.Equal(*(oriBytes + i), *(cloneBytes + i));
				}
			}


			// Ensure the layout of the serialized data was not
			// changed somehow and that no extra bytes were added
			{
				Assert.True(serializedData.Length == bigStructSize);
				
				byte* oriBytes = (byte*)oriStructAddr;
				byte* cloneBytes = (byte*)cloneStructAddr;

				for (int i = 0; i < bigStructSize; i++)
				{
					Assert.True(serializedData[i] == *(oriBytes + i));
					Assert.True(serializedData[i] == *(cloneBytes + i));
				}
			}
			
		}
	}
}
