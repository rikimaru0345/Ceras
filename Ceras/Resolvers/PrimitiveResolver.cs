namespace Ceras.Resolvers
{
	using Formatters;
	using System;
	using System.Linq.Expressions;

	class PrimitiveResolver : IFormatterResolver
	{
		readonly CerasSerializer _serializer;

		IFormatter _byteFormatter = new ByteFormatter();
		IFormatter _int16Formatter = new Int16Formatter();
		IFormatter _boolFormatter = new BoolFormatter();
		IFormatter _intFormatter = new IntFormatter();
		IFormatter _uintFormatter = new UIntFormatter();
		IFormatter _floatFormatter = new FloatFormatter();
		IFormatter _doubleFormatter = new DoubleFormatter();
		IFormatter _int64Formatter = new Int64Formatter();
		IFormatter _stringFormatter = new StringFormatter();

		public PrimitiveResolver(CerasSerializer serializer)
		{
			_serializer = serializer;
		}

		public IFormatter GetFormatter(Type type)
		{
			if (type == typeof(int))
				return _intFormatter;
			if (type == typeof(uint))
				return _uintFormatter;
			if (type == typeof(bool))
				return _boolFormatter;
			if (type == typeof(byte))
				return _byteFormatter;
			if (type == typeof(short))
				return _int16Formatter;
			if (type == typeof(float))
				return _floatFormatter;
			if (type == typeof(double))
				return _doubleFormatter;
			if (type == typeof(long))
				return _int64Formatter;

			if (type == typeof(string))
				return _stringFormatter;

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
		class IntFormatter : IFormatter<int>
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
		class UIntFormatter : IFormatter<uint>
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

				// Cast to int, write as int32
				// Todo: get the right formatter from the serializer: for byte, short, int, ...
				var enumBaseType = typeof(T).GetEnumUnderlyingType();
				var formatter = serializer.GetFormatter(enumBaseType, false, true, "Enum formatter for baseType");

				var write = formatter.GetType().GetMethod("Serialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
				
				var converted = Expression.Convert(value, enumBaseType);
				var writeCall = Expression.Call(instance: Expression.Constant(formatter), method: write, arg0: refBuffer, arg1: refOffset, arg2: converted);

				_enumWriter = Expression.Lambda<WriteEnum>(writeCall, refBuffer, refOffset, value).Compile();
				
				var buffer = Expression.Parameter(typeof(byte[]), "buffer");
				var refValue = Expression.Parameter(typeof(T).MakeByRefType(), "value");

				var readInt32 = typeof(SerializerBinary).GetMethod("ReadInt32");

				var readCall = Expression.Call(readInt32, buffer, refOffset);
				var convertBack = Expression.Convert(readCall, typeof(T));
				var assignment = Expression.Assign(refValue, convertBack);

				_enumReader = Expression.Lambda<ReadEnum>(assignment, buffer, refOffset, refValue).Compile();
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