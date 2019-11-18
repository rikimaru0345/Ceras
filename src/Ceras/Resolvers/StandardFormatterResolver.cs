namespace Ceras.Resolvers
{
	using Formatters;
	using Helpers;
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;
	using System.Numerics;
	using static SerializerBinary;

	/// <summary>
	/// Another boring resolver that handles "common" types like <see cref="DateTime"/>, <see cref="Guid"/>, <see cref="Tuple"/>, and many more...
	/// </summary>
	public sealed class StandardFormatterResolver : IFormatterResolver
	{
		// implemented by both tuple and value tuple
		static readonly Type _iTupleInterface = typeof(Tuple<>).GetInterfaces().First(t => t.Name == "ITuple");

		static readonly Type[] _tupleFormatterTypes = new Type[]
		{
				null, // [0] doesn't exist
				typeof(TupleFormatter<>), // 1
				typeof(TupleFormatter<,>), // 2
				typeof(TupleFormatter<,,>), // 3
				typeof(TupleFormatter<,,,>), // 4
				typeof(TupleFormatter<,,,,>), // 5
				typeof(TupleFormatter<,,,,,>), // 6
				typeof(TupleFormatter<,,,,,,>), // 7
		};

		static readonly Type[] _valueTupleFormatterTypes = new Type[]
		{
				null, // [0] doesn't exist
				typeof(ValueTupleFormatter<>), // 1
				typeof(ValueTupleFormatter<,>), // 2
				typeof(ValueTupleFormatter<,,>), // 3
				typeof(ValueTupleFormatter<,,,>), // 4
				typeof(ValueTupleFormatter<,,,,>), // 5
				typeof(ValueTupleFormatter<,,,,,>), // 6
				typeof(ValueTupleFormatter<,,,,,,>), // 7
		};

		
		readonly TypeDictionary<IFormatter> _primitiveFormatters = new TypeDictionary<IFormatter>();
		readonly CerasSerializer _ceras;
		

		public StandardFormatterResolver(CerasSerializer ceras)
		{
			_ceras = ceras;

			_primitiveFormatters.GetOrAddValueRef(typeof(DateTime)) = new DateTimeFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(DateTimeOffset)) = new DateTimeOffsetFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(TimeSpan)) = new TimeSpanFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(BitVector32)) = new BitVector32Formatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(BigInteger)) = new BigIntegerFormatter();

			_primitiveFormatters.GetOrAddValueRef(typeof(Uri)) = new UriFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(BitArray)) = new BitArrayFormatter();


			#if NETFRAMEWORK
			_primitiveFormatters.GetOrAddValueRef(typeof(System.Drawing.Color)) = new ColorFormatter();
			_primitiveFormatters.GetOrAddValueRef(typeof(System.Drawing.Bitmap)) = new BitmapFormatter();
			#endif
		}

		public IFormatter GetFormatter(Type type)
		{
			if (_primitiveFormatters.TryGetValue(type, out var f))
				return f;

			if (type.IsGenericType)
			{
				var genericDef = type.GetGenericTypeDefinition();

				if (genericDef == typeof(Nullable<>))
				{
					// Find the closed type, it's possible the current type is not generic itself, but derives from a closed generic
					var closedType = ReflectionHelper.FindClosedType(type, typeof(Nullable<>));
					var formatterType = typeof(NullableFormatter<>).MakeGenericType(closedType.GetGenericArguments());
					return (IFormatter)Activator.CreateInstance(formatterType, _ceras);
				}

				if (genericDef == typeof(KeyValuePair<,>))
				{
					// Find the closed type, it's possible the current type is not generic itself, but derives from a closed generic
					var closedType = ReflectionHelper.FindClosedType(type, typeof(KeyValuePair<,>));
					var formatterType = typeof(KeyValuePairFormatter<,>).MakeGenericType(closedType.GetGenericArguments());
					return (IFormatter)Activator.CreateInstance(formatterType);
				}

				if (_iTupleInterface.IsAssignableFrom(type))
				{
					if (type.IsValueType) // ValueTuple
					{
						var nArgs = type.GenericTypeArguments.Length;
						var formatterType = _valueTupleFormatterTypes[nArgs];
						formatterType = formatterType.MakeGenericType(type.GenericTypeArguments);

						var formatter = (IFormatter)Activator.CreateInstance(formatterType);
					
						CerasSerializer.AddFormatterConstructedType(type);

						return formatter;
					}

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
					throw new InvalidOperationException($"Trying to create a 'NullableFormatter<>' for reference type '{typeof(T).FullName}'!");

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


		class KeyValuePairFormatter<TKey, TValue> : IFormatter<KeyValuePair<TKey, TValue>>
		{
			IFormatter<TKey> _keyFormatter;
			IFormatter<TValue> _valueFormatter;
			
			public void Serialize(ref byte[] buffer, ref int offset, KeyValuePair<TKey, TValue> value)
			{
				_keyFormatter.Serialize(ref buffer, ref offset, value.Key);
				_valueFormatter.Serialize(ref buffer, ref offset, value.Value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref KeyValuePair<TKey, TValue> kvp)
			{
				TKey key = default;
				_keyFormatter.Deserialize(buffer, ref offset, ref key);

				TValue value = default;
				_valueFormatter.Deserialize(buffer, ref offset, ref value);

				kvp = new KeyValuePair<TKey, TValue>(key, value);
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
	}
}
