namespace Ceras.Resolvers
{
	using Formatters;
	using System;
	using System.Collections.Generic;
	using System.Linq.Expressions;

	class PrimitiveResolver : IFormatterResolver
	{
		readonly CerasSerializer _serializer;

		static Dictionary<Type, IFormatter> _primitiveFormatters = new Dictionary<Type, IFormatter>
		{
			[typeof(bool)] = new BoolFormatter(),

			[typeof(byte)] = new ByteFormatter(),
			[typeof(sbyte)] = new SByteFormatter(),

			[typeof(char)] = new CharFormatter(),

			[typeof(Int16)] = new Int16Formatter(),
			[typeof(UInt16)] = new UInt16Formatter(),

			[typeof(Int32)] = new Int32Formatter(),
			[typeof(UInt32)] = new UInt32Formatter(),

			[typeof(Int64)] = new Int64Formatter(),
			[typeof(UInt64)] = new UInt64Formatter(),

			[typeof(float)] = new FloatFormatter(),
			[typeof(double)] = new DoubleFormatter(),

			[typeof(string)] = new StringFormatter(),
		};


		public PrimitiveResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (_primitiveFormatters.TryGetValue(type, out var f))
				return f;

			if (type.IsEnum)
				return (IFormatter)Activator.CreateInstance(typeof(EnumFormatter<>).MakeGenericType(type), _serializer);

			return null;
		}

		class ByteFormatter : IFormatter<byte>
		{
			public void Serialize(ref byte[] buffer, ref int offset, byte value)
			{
				SerializerBinary.WriteByte(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref byte value)
			{
				value = SerializerBinary.ReadByte(buffer, ref offset);
			}
		}
		class SByteFormatter : IFormatter<sbyte>
		{
			public void Serialize(ref byte[] buffer, ref int offset, sbyte value)
			{
				SerializerBinary.WriteByte(ref buffer, ref offset, (byte)value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref sbyte value)
			{
				value = (sbyte)SerializerBinary.ReadByte(buffer, ref offset);
			}
		}
		class BoolFormatter : IFormatter<bool>
		{
			public void Serialize(ref byte[] buffer, ref int offset, bool value)
			{
				SerializerBinary.WriteInt32(ref buffer, ref offset, value ? 1 : 0);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref bool value)
			{
				value = SerializerBinary.ReadInt32(buffer, ref offset) != 0;
			}
		}
		class CharFormatter : IFormatter<char>
		{
			public void Serialize(ref byte[] buffer, ref int offset, char value)
			{
				SerializerBinary.WriteInt16Fixed(ref buffer, ref offset, (short)value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref char value)
			{
				value = (char)SerializerBinary.ReadInt16Fixed(buffer, ref offset);
			}
		}
		class Int16Formatter : IFormatter<short>
		{
			public void Serialize(ref byte[] buffer, ref int offset, short value)
			{
				SerializerBinary.WriteInt16Fixed(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref short value)
			{
				value = SerializerBinary.ReadInt16Fixed(buffer, ref offset);
			}
		}
		class UInt16Formatter : IFormatter<ushort>
		{
			public void Serialize(ref byte[] buffer, ref int offset, ushort value)
			{
				SerializerBinary.WriteInt16Fixed(ref buffer, ref offset, (short)value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref ushort value)
			{
				value = (ushort)SerializerBinary.ReadInt16Fixed(buffer, ref offset);
			}
		}
		class Int32Formatter : IFormatter<int>
		{
			public void Serialize(ref byte[] buffer, ref int offset, int value)
			{
				SerializerBinary.WriteInt32(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref int value)
			{
				value = SerializerBinary.ReadInt32(buffer, ref offset);
			}
		}
		class UInt32Formatter : IFormatter<uint>
		{
			public void Serialize(ref byte[] buffer, ref int offset, uint value)
			{
				SerializerBinary.WriteUInt32(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref uint value)
			{
				value = SerializerBinary.ReadUInt32(buffer, ref offset);
			}
		}
		class FloatFormatter : IFormatter<float>
		{
			public void Serialize(ref byte[] buffer, ref int offset, float value)
			{
				SerializerBinary.WriteFloat32Fixed(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref float value)
			{
				value = SerializerBinary.ReadFloat32Fixed(buffer, ref offset);
			}
		}
		class DoubleFormatter : IFormatter<double>
		{
			public void Serialize(ref byte[] buffer, ref int offset, double value)
			{
				SerializerBinary.WriteDouble64Fixed(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref double value)
			{
				value = SerializerBinary.ReadDouble64Fixed(buffer, ref offset);
			}
		}
		class Int64Formatter : IFormatter<long>
		{
			public void Serialize(ref byte[] buffer, ref int offset, long value)
			{
				SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref long value)
			{
				value = SerializerBinary.ReadInt64Fixed(buffer, ref offset);
			}
		}
		class UInt64Formatter : IFormatter<ulong>
		{
			public void Serialize(ref byte[] buffer, ref int offset, ulong value)
			{
				SerializerBinary.WriteInt64Fixed(ref buffer, ref offset, (long)value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref ulong value)
			{
				value = (ulong)SerializerBinary.ReadInt64Fixed(buffer, ref offset);
			}
		}


		class EnumFormatter<T> : IFormatter<T>
		{
			delegate void WriteEnum(ref byte[] buffer, ref int offset, T enumVal);
			delegate void ReadEnum(byte[] buffer, ref int offset, out T enumVal);

			WriteEnum _enumWriter;
			ReadEnum _enumReader;

			public EnumFormatter(CerasSerializer serializer)
			{
				var refBuffer = Expression.Parameter(typeof(byte[]).MakeByRefType(), "buffer");
				var refOffset = Expression.Parameter(typeof(int).MakeByRefType(), "offset");
				var value = Expression.Parameter(typeof(T), "value");


				//
				// Generate writer, previously we wrote as int32, but now we use the real underyling type
				var enumBaseType = typeof(T).GetEnumUnderlyingType();
				var formatter = serializer.GetSpecificFormatter(enumBaseType);

				var writeMethod = formatter.GetType().GetMethod("Serialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

				var converted = Expression.Convert(value, enumBaseType);
				var writeCall = Expression.Call(instance: Expression.Constant(formatter), method: writeMethod, arg0: refBuffer, arg1: refOffset, arg2: converted);

				_enumWriter = Expression.Lambda<WriteEnum>(writeCall, refBuffer, refOffset, value).Compile();


				//
				// Generate reader
				// First figure out what kind of reader we need and then use it (just the exact inverse of writing)

				var buffer = Expression.Parameter(typeof(byte[]), "buffer");
				var refValue = Expression.Parameter(typeof(T).MakeByRefType(), "value");

				// Deserialize(byte[] buffer, ref int offset, ref T value)
				var readMethod = formatter.GetType().GetMethod("Deserialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

				// We write/read different types. The 'T' we're serializing is (for example) "MyCoolEnum", but we actually write the base type, which is (in this example) "System.Int32"
				// That's why we need a local variable, into which we deserialize, and then we convert and write-back the deserialized value to the refValue parameter
				var tempLocal = Expression.Variable(enumBaseType, "temp");

				var readCall = Expression.Call(instance: Expression.Constant(formatter), method: readMethod, arg0: buffer, arg1: refOffset, arg2: tempLocal);
				var convertBack = Expression.Convert(tempLocal, typeof(T));
				var backAssignment = Expression.Assign(refValue, convertBack);

				BlockExpression deserializationBlock = Expression.Block(
																		variables: new ParameterExpression[] { tempLocal },
																		expressions: new Expression[] { readCall, backAssignment }
																		);

				_enumReader = Expression.Lambda<ReadEnum>(deserializationBlock, buffer, refOffset, refValue).Compile();
			}

			public void Serialize(ref byte[] buffer, ref int offset, T value)
			{
				_enumWriter(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref T value)
			{
				_enumReader(buffer, ref offset, out value);
			}
		}
	}
}