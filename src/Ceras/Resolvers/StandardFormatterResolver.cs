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

        static readonly Type[] _tupleFormatterTypes = new Type[] { null!, typeof(TupleFormatter<>), typeof(TupleFormatter<,>), typeof(TupleFormatter<,,>), typeof(TupleFormatter<,,,>), typeof(TupleFormatter<,,,,>), typeof(TupleFormatter<,,,,,>), typeof(TupleFormatter<,,,,,,>) };
        static readonly Type[] _valueTupleFormatterTypes = new Type[] { null!, typeof(ValueTupleFormatter<>), typeof(ValueTupleFormatter<,>), typeof(ValueTupleFormatter<,,>), typeof(ValueTupleFormatter<,,,>), typeof(ValueTupleFormatter<,,,,>), typeof(ValueTupleFormatter<,,,,,>), typeof(ValueTupleFormatter<,,,,,,>) };

        readonly TypeDictionary<IFormatter> _primitiveFormatters = new TypeDictionary<IFormatter>();
        readonly CerasSerializer _ceras;

        internal StandardFormatterResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
            _primitiveFormatters.GetOrAddValueRef(typeof(DateTime)) = new DateTimeFormatter();
            _primitiveFormatters.GetOrAddValueRef(typeof(DateTimeOffset)) = new DateTimeOffsetFormatter();
            _primitiveFormatters.GetOrAddValueRef(typeof(TimeSpan)) = new TimeSpanFormatter();
            _primitiveFormatters.GetOrAddValueRef(typeof(Guid)) = new GuidFormatter();
            _primitiveFormatters.GetOrAddValueRef(typeof(BitVector32)) = new BitVector32Formatter();
            _primitiveFormatters.GetOrAddValueRef(typeof(BigInteger)) = new BigIntegerFormatter();
            _primitiveFormatters.GetOrAddValueRef(typeof(Uri)) = new UriFormatter();
            _primitiveFormatters.GetOrAddValueRef(typeof(BitArray)) = new BitArrayFormatter();
        }

        public IFormatter? GetFormatter(Type type)
        {
            if (_primitiveFormatters.TryGetValue(type, out var f)) return f;
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(Nullable<>))
                {
                    var closedType = ReflectionHelper.FindClosedType(type, typeof(Nullable<>));
                    return (IFormatter)Ceras.Helpers.Instantiator.Create(typeof(NullableFormatter<>).MakeGenericType(closedType!.GetGenericArguments()), _ceras);
                }
                if (genericDef == typeof(KeyValuePair<,>))
                {
                    var closedType = ReflectionHelper.FindClosedType(type, typeof(KeyValuePair<,>));
                    return (IFormatter)Ceras.Helpers.Instantiator.Create(typeof(KeyValuePairFormatter<,>).MakeGenericType(closedType!.GetGenericArguments()), _ceras);
                }
                if (_iTupleInterface.IsAssignableFrom(type))
                {
                    var nArgs = type.GenericTypeArguments.Length;
                    var formatterType = (type.IsValueType ? _valueTupleFormatterTypes : _tupleFormatterTypes)[nArgs].MakeGenericType(type.GenericTypeArguments);
                    var formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType);
                    CerasSerializer.AddFormatterConstructedType(type);
                    return formatter;
                }
            }
            return null;
        }

        class NullableFormatter<T> : FormatterBase<T?> where T : struct
        {
            IFormatter<T> _specificFormatter;
            internal NullableFormatter(CerasSerializer serializer) { _specificFormatter = serializer.GetSpecificFormatter<T>(); }
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
            internal KeyValuePairFormatter(CerasSerializer ceras) { _keyFormatter = ceras.GetReferenceFormatter<TKey>(); _valueFormatter = ceras.GetReferenceFormatter<TValue>(); }
            public override void Serialize(IBufferWriter<byte> writer, KeyValuePair<TKey, TValue> value) { _keyFormatter.Serialize(writer, value.Key); _valueFormatter.Serialize(writer, value.Value); }
            public override void Deserialize(ref SequenceReader<byte> reader, ref KeyValuePair<TKey, TValue> kvp)
            {
                // A reference-typed key or value can come back null from the payload, but the formatter
                // interface declares its ref parameter non-nullable, so both locals start as "not read".
                TKey k = default!; _keyFormatter.Deserialize(ref reader, ref k);
                TValue v = default!; _valueFormatter.Deserialize(ref reader, ref v);
                kvp = new KeyValuePair<TKey, TValue>(k, v);
            }
        }
    }
}
