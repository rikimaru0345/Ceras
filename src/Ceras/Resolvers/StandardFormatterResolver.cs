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
        /// <summary>
        /// What <c>Instantiator.Create</c> needs from the two formatters this resolver hands
        /// arguments to: <c>Activator</c> is given <c>BindingFlags.Public | BindingFlags.NonPublic</c>
        /// and the argument count of the closed instantiation is not something a dependency can pin
        /// down, so the whole constructor set is what has to stay.
        /// </summary>
        const DynamicallyAccessedMemberTypes Constructors =
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors;

        /// <summary>
        /// The narrower requirement for the fourteen tuple formatters: they are constructed with no
        /// arguments at all, so a parameterless constructor is the only one <c>Activator</c> can bind.
        /// </summary>
        const DynamicallyAccessedMemberTypes ParameterlessConstructors =
            DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.NonPublicConstructors;

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

        /// <summary>
        /// The sixteen <see cref="DynamicDependencyAttribute"/>s state the members of the sixteen
        /// formatter definitions this method closes over a runtime <see cref="Type"/>, which is the
        /// only site any of them is ever closed. Nothing in a compilation names them: a generated
        /// formatter refers to a formatter only as the type of an injected field, which roots the
        /// instantiation and nothing inside it, and the two static tables above name the open
        /// definitions, not the closed types. So each closed type exists only as a
        /// <c>MakeGenericType</c> result - which is also why the requirement cannot be stated on
        /// <paramref name="type"/>: <see cref="DynamicallyAccessedMembersAttribute"/> describes the
        /// members of a type the trimmer can see, while a dependency on the open definition is the
        /// one form that covers every closed construction, including the ones no compilation has
        /// heard of (the element type of a tuple in a payload).
        /// <para>
        /// <see cref="NullableFormatter{T}"/> and <see cref="KeyValuePairFormatter{TKey, TValue}"/>
        /// are handed the serializer, so they ask for the whole constructor set; the fourteen tuple
        /// formatters are constructed with no arguments, so a parameterless constructor is all
        /// <c>Activator</c> can bind and all that has to be kept. All sixteen are ordinary
        /// <c>FormatterBase&lt;T&gt;</c> subclasses reached through the non-generic
        /// <see cref="IFormatter"/>, whose <c>SerializeUntyped</c>/<c>DeserializeUntyped</c> root the
        /// <c>Serialize</c>/<c>Deserialize</c> overrides - unlike the array interfaces of
        /// <c>MultiDimensionalArrayFormatter&lt;&gt;</c>, which nothing names.
        /// </para>
        /// </summary>
        [DynamicDependency(Constructors, typeof(NullableFormatter<>))]
        [DynamicDependency(Constructors, typeof(KeyValuePairFormatter<,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(TupleFormatter<>))]
        [DynamicDependency(ParameterlessConstructors, typeof(TupleFormatter<,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(TupleFormatter<,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(TupleFormatter<,,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(TupleFormatter<,,,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(TupleFormatter<,,,,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(TupleFormatter<,,,,,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ValueTupleFormatter<>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ValueTupleFormatter<,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ValueTupleFormatter<,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ValueTupleFormatter<,,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ValueTupleFormatter<,,,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ValueTupleFormatter<,,,,,>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ValueTupleFormatter<,,,,,,>))]
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
