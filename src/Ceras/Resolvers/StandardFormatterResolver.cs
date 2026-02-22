namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;
    using System;
    using System.Buffers;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Numerics;

    public sealed class StandardFormatterResolver : IFormatterResolver
    {
        static readonly Type _iTupleInterface = typeof(Tuple<>).GetInterfaces().First(t => t.Name == "ITuple");

        static readonly Type[] _tupleFormatterTypes = new Type[] { null, typeof(TupleFormatter<>), typeof(TupleFormatter<,>), typeof(TupleFormatter<,,>), typeof(TupleFormatter<,,,>), typeof(TupleFormatter<,,,,>), typeof(TupleFormatter<,,,,,>), typeof(TupleFormatter<,,,,,,>) };
        static readonly Type[] _valueTupleFormatterTypes = new Type[] { null, typeof(ValueTupleFormatter<>), typeof(ValueTupleFormatter<,>), typeof(ValueTupleFormatter<,,>), typeof(ValueTupleFormatter<,,,>), typeof(ValueTupleFormatter<,,,,>), typeof(ValueTupleFormatter<,,,,,>), typeof(ValueTupleFormatter<,,,,,,>) };

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
        }

        public IFormatter GetFormatter(Type type)
        {
            if (_primitiveFormatters.TryGetValue(type, out var f)) return f;
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(Nullable<>))
                {
                    var closedType = ReflectionHelper.FindClosedType(type, typeof(Nullable<>));
                    return (IFormatter)Activator.CreateInstance(typeof(NullableFormatter<>).MakeGenericType(closedType.GetGenericArguments()), _ceras);
                }
                if (genericDef == typeof(KeyValuePair<,>))
                {
                    var closedType = ReflectionHelper.FindClosedType(type, typeof(KeyValuePair<,>));
                    return (IFormatter)Activator.CreateInstance(typeof(KeyValuePairFormatter<,>).MakeGenericType(closedType.GetGenericArguments()), _ceras);
                }
                if (_iTupleInterface.IsAssignableFrom(type))
                {
                    var nArgs = type.GenericTypeArguments.Length;
                    var formatterType = (type.IsValueType ? _valueTupleFormatterTypes : _tupleFormatterTypes)[nArgs].MakeGenericType(type.GenericTypeArguments);
                    var formatter = (IFormatter)Activator.CreateInstance(formatterType);
                    CerasSerializer.AddFormatterConstructedType(type);
                    return formatter;
                }
            }
            return null;
        }

        class NullableFormatter<T> : FormatterBase<T?> where T : struct
        {
            IFormatter<T> _specificFormatter;
            public NullableFormatter(CerasSerializer serializer) { _specificFormatter = serializer.GetSpecificFormatter<T>(); }
            public override void Serialize(IBufferWriter<byte> writer, T? value)
            {
                if (value.HasValue) { SerializerBinary.WriteByte(writer, 1); _specificFormatter.Serialize(writer, value.Value); }
                else SerializerBinary.WriteByte(writer, 0);
            }
            public override void Deserialize(ref SequenceReader<byte> reader, ref T? value)
            {
                if (SerializerBinary.ReadByte(ref reader) != 0) { T inner = default; _specificFormatter.Deserialize(ref reader, ref inner); value = inner; }
                else value = null;
            }
        }

        class KeyValuePairFormatter<TKey, TValue> : FormatterBase<KeyValuePair<TKey, TValue>>
        {
            IFormatter<TKey> _keyFormatter; IFormatter<TValue> _valueFormatter;
            public KeyValuePairFormatter(CerasSerializer ceras) { _keyFormatter = ceras.GetReferenceFormatter<TKey>(); _valueFormatter = ceras.GetReferenceFormatter<TValue>(); }
            public override void Serialize(IBufferWriter<byte> writer, KeyValuePair<TKey, TValue> value) { _keyFormatter.Serialize(writer, value.Key); _valueFormatter.Serialize(writer, value.Value); }
            public override void Deserialize(ref SequenceReader<byte> reader, ref KeyValuePair<TKey, TValue> kvp)
            {
                TKey k = default; _keyFormatter.Deserialize(ref reader, ref k);
                TValue v = default; _valueFormatter.Deserialize(ref reader, ref v);
                kvp = new KeyValuePair<TKey, TValue>(k, v);
            }
        }

        class DateTimeFormatter : FormatterBase<DateTime>
        {
            public override void Serialize(IBufferWriter<byte> writer, DateTime value) => SerializerBinary.WriteInt64Fixed(writer, value.ToBinary());
            public override void Deserialize(ref SequenceReader<byte> reader, ref DateTime value) => value = DateTime.FromBinary(SerializerBinary.ReadInt64Fixed(ref reader));
        }

        class DateTimeOffsetFormatter : FormatterBase<DateTimeOffset>
        {
            public override void Serialize(IBufferWriter<byte> writer, DateTimeOffset value) { SerializerBinary.WriteInt64Fixed(writer, value.Ticks); SerializerBinary.WriteInt16Fixed(writer, (short)value.Offset.TotalMinutes); }
            public override void Deserialize(ref SequenceReader<byte> reader, ref DateTimeOffset value) => value = new DateTimeOffset(SerializerBinary.ReadInt64Fixed(ref reader), TimeSpan.FromMinutes(SerializerBinary.ReadInt16Fixed(ref reader)));
        }

        class TimeSpanFormatter : FormatterBase<TimeSpan>
        {
            public override void Serialize(IBufferWriter<byte> writer, TimeSpan value) => SerializerBinary.WriteInt64Fixed(writer, value.Ticks);
            public override void Deserialize(ref SequenceReader<byte> reader, ref TimeSpan value) => value = new TimeSpan(SerializerBinary.ReadInt64Fixed(ref reader));
        }

        class BitVector32Formatter : FormatterBase<BitVector32>
        {
            public override void Serialize(IBufferWriter<byte> writer, BitVector32 value) => SerializerBinary.WriteInt32Fixed(writer, value.Data);
            public override void Deserialize(ref SequenceReader<byte> reader, ref BitVector32 value) => value = new BitVector32(SerializerBinary.ReadInt32Fixed(ref reader));
        }

        class BigIntegerFormatter : FormatterBase<BigInteger>
        {
            public override void Serialize(IBufferWriter<byte> writer, BigInteger value) { var data = value.ToByteArray(); SerializerBinary.WriteUInt32(writer, (uint)data.Length); writer.Write(data); }
            public override void Deserialize(ref SequenceReader<byte> reader, ref BigInteger value) { int length = (int)SerializerBinary.ReadUInt32(ref reader); byte[] bytes = new byte[length]; reader.TryCopyTo(bytes); reader.Advance(length); value = new BigInteger(bytes); }
        }
    }
}
