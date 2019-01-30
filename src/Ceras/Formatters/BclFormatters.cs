namespace Ceras.Formatters
{
	using Helpers;
	using System;
	using System.Collections.Specialized;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;
	using System.Numerics;
	using static SerializerBinary;

	class BclFormatterResolver : Resolvers.IFormatterResolver
	{
		static readonly TypeDictionary<IFormatter> _primitiveFormatters = new TypeDictionary<IFormatter>();

		static readonly Type[] _tupleFormatterTypes = new Type[]
		{
				null, // [0] doesn't exist
				typeof(TupleFormatter<>), // 1
				typeof(TupleFormatter<,>), // 2
				typeof(TupleFormatter<,,>), // 3
				typeof(TupleFormatter<,,,>), // 4
				typeof(TupleFormatter<,,,,,>), // 5
				typeof(TupleFormatter<,,,,,,>), // 6
				typeof(TupleFormatter<,,,,,,,>), // 7
		};

#if NETSTANDARD
		static readonly Type[] _valueTupleFormatterTypes = new Type[]
		{
				null, // [0] doesn't exist
				typeof(ValueTupleFormatter<>), // 1
				typeof(ValueTupleFormatter<,>), // 2
				typeof(ValueTupleFormatter<,,>), // 3
				typeof(ValueTupleFormatter<,,,>), // 4
				typeof(ValueTupleFormatter<,,,,,>), // 5
				typeof(ValueTupleFormatter<,,,,,,>), // 6
				typeof(ValueTupleFormatter<,,,,,,,>), // 7
		};
#endif
		
		// implemented by both tuple and value tuple
		static readonly Type _iTupleInterface = typeof(Tuple<>).GetInterfaces().First(t => t.Name == "ITuple");


		CerasSerializer _serializer;

		public BclFormatterResolver(CerasSerializer serializer)
		{
			_serializer = serializer;

			_primitiveFormatters.GetOrAddValueRef(typeof(DateTime)) = new DateTimeFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(DateTimeOffset)) = new DateTimeOffsetFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(TimeSpan)) = new TimeSpanFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(Guid)) = new GuidFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(decimal)) = new DecimalFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(BitVector32)) = new BitVector32Formatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(BigInteger)) = new BigIntegerFormatter();

		}

		public IFormatter GetFormatter(Type type)
		{
			if (_primitiveFormatters.TryGetValue(type, out var f))
				return f;

			if (type.IsGenericType)
			{
				if (type.GetGenericTypeDefinition() == typeof(Nullable<>))
				{
					var innerType = type.GetGenericArguments()[0];
					var formatterType = typeof(NullableFormatter<>).MakeGenericType(innerType);
					return (IFormatter)Activator.CreateInstance(formatterType, _serializer);
				}

				if (_iTupleInterface.IsAssignableFrom(type))
				{
#if NETSTANDARD
					if (type.IsValueType) // ValueTuple
					{
						var nArgs = type.GenericTypeArguments.Length;
						var formatterType = _valueTupleFormatterTypes[nArgs];
						formatterType = formatterType.MakeGenericType(type.GenericTypeArguments);

						var formatter = (IFormatter)Activator.CreateInstance(formatterType);
					
						CerasSerializer.AddFormatterConstructedType(type);

						return formatter;
					}
#endif
					if (type.IsClass) // Tuple
					{
						var nArgs = type.GenericTypeArguments.Length;
						var formatterType = _tupleFormatterTypes[nArgs];
						formatterType = formatterType.MakeGenericType(type.GenericTypeArguments);

						var formatter = (IFormatter)Activator.CreateInstance(formatterType);
					
						CerasSerializer.AddFormatterConstructedType(type);

						return formatter;
					}
				}
			}

			return null;
		}



		[SuppressMessage("ReSharper", "ConvertNullableToShortForm")]
		[SuppressMessage("ReSharper", "RedundantExplicitNullableCreation")]
		[SuppressMessage("General", "IDE0001")]
		class NullableFormatter<T> : IFormatter<Nullable<T>> where T : struct
		{
			IFormatter<T> _specificFormatter;

			public NullableFormatter(CerasSerializer serializer)
			{
				if (!typeof(T).IsValueType)
					throw new InvalidOperationException($"Trying to create a 'NullableFormatter<>' for generic T = '{typeof(T).FullName}', which is not a value-type!");

				_specificFormatter = (IFormatter<T>)serializer.GetSpecificFormatter(typeof(T));
			}

			public void Serialize(ref byte[] buffer, ref int offset, Nullable<T> value)
			{
				if (value.HasValue)
				{
					WriteByte(ref buffer, ref offset, 1);
					_specificFormatter.Serialize(ref buffer, ref offset, value.Value);
				}
				else
				{
					WriteByte(ref buffer, ref offset, 0);
				}
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Nullable<T> value)
			{
				bool hasValue = ReadByte(buffer, ref offset) != 0;
				if (hasValue)
				{
					T innerValue = default;
					_specificFormatter.Deserialize(buffer, ref offset, ref innerValue);
					value = new Nullable<T>(innerValue);
				}
				else
				{
					value = new Nullable<T>();
				}
			}
		}


		class DateTimeFormatter : IFormatter<DateTime>
		{
			public void Serialize(ref byte[] buffer, ref int offset, DateTime value)
			{
				var v = value.ToBinary();
				WriteInt64Fixed(ref buffer, ref offset, v);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref DateTime value)
			{
				var v = ReadInt64Fixed(buffer, ref offset);
				value = DateTime.FromBinary(v);
			}
		}

		class DateTimeOffsetFormatter : IFormatter<DateTimeOffset>
		{
			public void Serialize(ref byte[] buffer, ref int offset, DateTimeOffset value)
			{
				WriteInt64Fixed(ref buffer, ref offset, value.Ticks);
				WriteInt16Fixed(ref buffer, ref offset, (short)value.Offset.TotalMinutes);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref DateTimeOffset value)
			{
				var dtTicks = ReadInt64Fixed(buffer, ref offset);
				var timeOffset = ReadInt16Fixed(buffer, ref offset);

				value = new DateTimeOffset(dtTicks, TimeSpan.FromMinutes(timeOffset));
			}
		}

		class TimeSpanFormatter : IFormatter<TimeSpan>
		{
			public void Serialize(ref byte[] buffer, ref int offset, TimeSpan value)
			{
				WriteInt64Fixed(ref buffer, ref offset, value.Ticks);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref TimeSpan value)
			{
				value = new TimeSpan(ReadInt64Fixed(buffer, ref offset));
			}
		}

		class GuidFormatter : IFormatter<Guid>
		{
			public void Serialize(ref byte[] buffer, ref int offset, Guid value)
			{
				WriteGuid(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Guid value)
			{
				value = ReadGuid(buffer, ref offset);
			}
		}

		class DecimalFormatter : IFormatter<decimal>
		{
			public unsafe void Serialize(ref byte[] buffer, ref int offset, decimal value)
			{
				EnsureCapacity(ref buffer, offset, 16);
				fixed (byte* dst = &buffer[offset])
				{
					var src = &value;

					*(decimal*)(dst) = *src;

					offset += 16;
				}
			}

			public unsafe void Deserialize(byte[] buffer, ref int offset, ref decimal value)
			{
				fixed (byte* src = &buffer[offset])
				{
					value = *(decimal*)(src);
					offset += 16;
				}
			}
		}

		class BitVector32Formatter : IFormatter<BitVector32>
		{
			public void Serialize(ref byte[] buffer, ref int offset, BitVector32 value)
			{
				WriteInt32Fixed(ref buffer, ref offset, value.Data);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref BitVector32 value)
			{
				var data = ReadInt32Fixed(buffer, ref offset);
				value = new BitVector32(data);
			}
		}

		class BigIntegerFormatter : IFormatter<BigInteger>
		{
			public void Serialize(ref byte[] buffer, ref int offset, BigInteger value)
			{
				var data = value.ToByteArray();

				// Length
				WriteUInt32(ref buffer, ref offset, (uint)data.Length);

				// Bytes
				EnsureCapacity(ref buffer, offset, data.Length);
				Buffer.BlockCopy(data, 0, buffer, offset, data.Length);

				offset += data.Length;
			}

			public void Deserialize(byte[] buffer, ref int offset, ref BigInteger value)
			{
				var length = (int)ReadUInt32(buffer, ref offset);

				var bytes = new byte[length];

				Buffer.BlockCopy(buffer, offset, bytes, 0, length);
				offset += length;

				value = new BigInteger(bytes);
			}
		}


		/*
		class TupleFormatter<T1, T2> : IFormatter<Tuple<T1, T2>>
		{
			IFormatter<T1> _f1;
			IFormatter<T2> _f2;

			public TupleFormatter(CerasSerializer serializer)
			{
				_f1 = (IFormatter<T1>)serializer.GetSpecificFormatter(typeof(T1));
				_f2 = (IFormatter<T2>)serializer.GetSpecificFormatter(typeof(T2));
			}


			public void Serialize(ref byte[] buffer, ref int offset, Tuple<T1, T2> value)
			{

			}

			public void Deserialize(byte[] buffer, ref int offset, ref Tuple<T1, T2> value)
			{
			}
		}
		*/

	}
}
