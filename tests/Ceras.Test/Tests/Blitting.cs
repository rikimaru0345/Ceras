using System;
using System.Collections.Generic;

namespace Ceras.Test
{
	using System.Linq;
	using Formatters;
	using Resolvers;
	using System.Runtime.InteropServices;
	using Xunit;
	using Ceras.Helpers;
	using System.Runtime.CompilerServices;

	public class Blitting : TestBase
	{
		// Types that the reinterpret formatter should be able to handle
		// Arrays are handled by "ReinterpretArrayFormatter" of course
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

			typeof(DayOfWeek), // The base type "Enum" can't be blitted, but an actual enum can!
		};

		// Types where neither ReinterpretFormatter nor ReinterpretArrayFormatter
		// should be used!
		static Type[] _nonBlittableTypes =
		{
			// Pointers and arrays
			typeof(IntPtr),
			typeof(UIntPtr),
			typeof(IntPtr[]),
			typeof(UIntPtr[]),
			
			// Reference types
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

			
			// Multi dimensional arrays can not be blitted because it would make the code extremely brittle (offset of the first element depends on the array-rank)
			typeof(string[]),
			typeof(string[,]),
			typeof(string[,,]),
			typeof(string[,,,]),
			typeof(int[,]),
			typeof(int[,,]),
			typeof(Vector3[,,,,]),
			typeof(byte[,,]),
		};

		// Formatters Ceras is allowed to use to handle "blittable types"
		static Type[] _blitFormattersOpenTypes =
		{
			typeof(ReinterpretFormatter<>),
			typeof(ReinterpretArrayFormatter<>),

			typeof(BoolFormatter),
			typeof(ByteFormatter),
			typeof(SByteFormatter),

			typeof(CharFormatter),
			typeof(Int16FixedFormatter),
			typeof(UInt16FixedFormatter),

			typeof(Int32FixedFormatter),
			typeof(UInt32FixedFormatter),
			typeof(Int64FixedFormatter),
			typeof(UInt64FixedFormatter),

			typeof(FloatFormatter),
			typeof(DoubleFormatter),
		};


		[Fact]
		public void BlittableTypesUseCorrectFormatter()
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
		public unsafe void BigStructBlittedCorrectly()
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

		[Fact]
		public void BlittingEnums()
		{
			// Usually we don't use the reinterpret-formatter for enum members so we can minimize the size (varint encoding)

			var stressTestValues = new[]
			{
				long.MinValue, long.MaxValue, -1, 0, 1, 5, 1000, 255, 256,
				rngByte, rngByte, rngByte,
				rngLong, rngLong, rngLong, rngLong, rngLong, rngLong,
			};


			var config = new SerializerConfig();
			config.OnConfigNewType = t => t.CustomResolver = (c, t2) => c.Advanced.GetFormatterResolver<ReinterpretFormatterResolver>().GetFormatter(t2);
			//config.OnConfigNewType = t => t.CustomFormatter = (IFormatter)Activator.CreateInstance(typeof(EnumFormatterUnsafe<>).MakeGenericType(t.Type));
			var ceras = new CerasSerializer(config);

			var typesToTest = new[]
			{
				typeof(TestEnumInt8),
				typeof(TestEnumUInt8),
				typeof(TestEnumInt16),
				typeof(TestEnumUInt16),
				typeof(TestEnumInt64),
				typeof(TestEnumUInt64),
			};

			var serializeMethod = typeof(CerasSerializer).GetMethods().First(m => m.Name == nameof(CerasSerializer.Serialize) && m.GetParameters().Length == 1);
			var deserializeMethod = typeof(CerasSerializer).GetMethods().First(m => m.Name == nameof(CerasSerializer.Deserialize) && m.GetParameters().Length == 1);


			foreach (var t in typesToTest)
			{
				Type baseType = t.GetEnumUnderlyingType();
				int expectedSize = Marshal.SizeOf(baseType);

				var values = Enum.GetValues(t).Cast<object>().Concat(stressTestValues.Cast<object>());

				foreach (var v in values)
				{
					var obj = Enum.ToObject(t, v);

					// We must call Serialize<T>, and we can't use <object> because that would embed the type information
					var data = (byte[])serializeMethod.MakeGenericMethod(t).Invoke(ceras, new object[] { obj });

					Assert.True(data.Length == expectedSize);

					var cloneObj = deserializeMethod.MakeGenericMethod(t).Invoke(ceras, new object[] { data });

					Assert.True(obj.Equals(cloneObj));
				}
			}


			Assert.True(ceras.Serialize(TestEnumInt8.a).Length == 1);
			Assert.True(ceras.Serialize(TestEnumUInt8.a).Length == 1);

			Assert.True(ceras.Serialize(TestEnumInt16.a).Length == 2);
			Assert.True(ceras.Serialize(TestEnumUInt16.a).Length == 2);

			Assert.True(ceras.Serialize(TestEnumInt64.a).Length == 8);
			Assert.True(ceras.Serialize(TestEnumUInt64.a).Length == 8);
		}

		enum TestEnumInt8 : sbyte { a = 123, b, c }
		enum TestEnumUInt8 : byte { a = 123, b, c }

		enum TestEnumInt16 : short { a = 123, b, c }
		enum TestEnumUInt16 : ushort { a = 123, b, c }

		enum TestEnumInt64 : long { a = 123, b, c }
		enum TestEnumUInt64 : ulong { a = 123, b, c }


		[Fact]
		public unsafe void SizeOfBoolIs1()
		{
			var single = ReflectionHelper.GetSize(typeof(bool));
			Assert.True(single == 1);
		}

		[Fact]
		public unsafe void SizeOfCharIs2()
		{
			var single = ReflectionHelper.GetSize(typeof(char));
			Assert.True(single == 2);
		}


		[Fact]
		public unsafe void BlittableSize()
		{
			var typeToExpectedSize = new (Type type, int singleSize)[]
			{
				( typeof(BlittableStruct_PackDefault), 8),
				( typeof(BlittableStruct_Pack0), 8),
				( typeof(BlittableStruct_Pack1), 7),

				( typeof(BlittableStruct_Size1), 1 ),
				( typeof(BlittableStruct_Size2), 2 ),
				( typeof(BlittableStruct_Size3), 3 ),
				( typeof(BlittableStruct_Size4), 4 ),
				( typeof(BlittableStruct_Size5), 5 ),
				
				( typeof(TestEnumInt8), 1 ),
				( typeof(TestEnumInt16), 2 ),
				( typeof(TestEnumInt64), 8 ),
			};

			foreach (var entry in typeToExpectedSize)
			{
				var actualSingleSize = ReflectionHelper.GetSize(entry.type);

				Assert.True(actualSingleSize == entry.singleSize);
			}
		}

		[StructLayout(LayoutKind.Sequential, Size = 1)]
		struct BlittableStruct_Size1 { public byte Byte; }
		[StructLayout(LayoutKind.Sequential, Size = 2)]
		struct BlittableStruct_Size2 { public byte Byte; }
		[StructLayout(LayoutKind.Sequential, Size = 3)]
		struct BlittableStruct_Size3 { public byte Byte; }
		[StructLayout(LayoutKind.Sequential, Size = 4)]
		struct BlittableStruct_Size4 { public byte Byte; }
		[StructLayout(LayoutKind.Sequential, Size = 5)]
		struct BlittableStruct_Size5 { public byte Byte; }


		[StructLayout(LayoutKind.Sequential)]
		struct BlittableStruct_PackDefault
		{
			public Int16 Int16;     // +2 = 2
			public byte Byte;       // +1 = 3
			public Int32 Int32;     // +4 = 7
		}

		[StructLayout(LayoutKind.Sequential, Pack = 0)]
		struct BlittableStruct_Pack0
		{
			public Int16 Int16;     // +2 = 2
			public byte Byte;       // +1 = 3
			public Int32 Int32;     // +4 = 7
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		struct BlittableStruct_Pack1
		{
			public Int16 Int16;     // +2 = 2
			public byte Byte;       // +1 = 3
			public Int32 Int32;     // +4 = 7
		}


		/*
		[Fact]
		public unsafe void CouldCopyValueTupleDirectly()
		{
			(int, decimal, Vector3, bool, long) t = ValueTuple.Create(5, 2.441M, new Vector3(1, 2, 3), false, (long)69419572);
			var s1 = ReflectionHelper.GetSize(t.GetType());
			var s2 = ReflectionHelper.UnsafeGetSize(t.GetType());

			var target = new byte[s2];
			Unsafe.Copy(Unsafe.AsPointer(ref target[0]), ref t);

			(int, decimal, Vector3, bool, long) clone = default;
			Unsafe.Copy(ref clone, Unsafe.AsPointer(ref target[0]));

			Assert.True(clone.Item3.Y == 2);
			Assert.Equal(t, clone);
		}
		*/


	}
}
