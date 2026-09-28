namespace Ceras.Resolvers
{
    using Formatters;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// A really really boring resolver that produces formatters for all the "primitives" like bool, int, float, double, ... 
    /// </summary>
    public sealed class PrimitiveResolver : IFormatterResolver
    {
        /// <summary>
        /// Both enum formatters below are constructed with no arguments, so the parameterless
        /// constructor is the whole set <c>Instantiator.Create</c> can bind. They are otherwise
        /// ordinary <c>FormatterBase&lt;T&gt;</c> subclasses reached through the non-generic
        /// <c>IFormatter</c>, so their own <c>Serialize</c>/<c>Deserialize</c> need no statement here.
        /// </summary>
        const DynamicallyAccessedMemberTypes ParameterlessConstructors =
            DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.NonPublicConstructors;

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
            [typeof(decimal)] = new DecimalFormatter(),

            [typeof(IntPtr)] = new IntPtrFormatter(),
            [typeof(UIntPtr)] = new UIntPtrFormatter(),
        };

        readonly CerasSerializer _ceras;



        internal PrimitiveResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
        }

        /// <summary>
        /// The two enum formatters are the only types this resolver closes over a runtime
        /// <see cref="Type"/>, and nothing in a compilation names them - an enum member reaches
        /// Ceras as a closed <c>EnumFormatter&lt;TEnum&gt;</c> that nothing statically mentions, so the
        /// closed type exists only as a <c>MakeGenericType</c> result. That is also why the
        /// requirement cannot be written on <paramref name="type"/>: <see
        /// cref="DynamicallyAccessedMembersAttribute"/> describes the members of a type the trimmer
        /// can see, and a dependency on the open definition is the one form that covers every closed
        /// construction, including an enum type that only appears in a payload.
        /// <para>
        /// <see cref="ReinterpretFormatter{T}"/> is the second of the two, and it is stated here as
        /// well as nowhere else: <c>Resolvers/ReinterpretFormatterResolver.cs</c> closes the same
        /// definition, and one dependency on the definition already covers every closed construction
        /// of it, so the two sites cannot disagree about what has to survive.
        /// </para>
        /// </summary>
        [DynamicDependency(ParameterlessConstructors, typeof(EnumFormatter<>))]
        [DynamicDependency(ParameterlessConstructors, typeof(ReinterpretFormatter<>))]
        public IFormatter? GetFormatter(Type type)
        {
            if (_primitiveFormatters.TryGetValue(type, out var f))
                return f;

            if (type.IsEnum)
            {
                if (_ceras.Config is CerasRobustConfig)
                    return (IFormatter)Ceras.Helpers.Instantiator.Create(typeof(EnumFormatter<>).MakeGenericType(type));

                return (IFormatter)Ceras.Helpers.Instantiator.Create(typeof(ReinterpretFormatter<>).MakeGenericType(type));
            }

            return null;
        }

        class EnumFormatter<T> : FormatterBase<T> where T : struct
        {
            public override void Serialize(global::System.Buffers.IBufferWriter<byte> writer, T value)
            {
                var underlying = Enum.GetUnderlyingType(typeof(T));
                if (underlying == typeof(int)) SerializerBinary.WriteInt32Fixed(writer, (int)(object)value);
                else if (underlying == typeof(uint)) SerializerBinary.WriteUInt32Fixed(writer, (uint)(object)value);
                else if (underlying == typeof(long)) SerializerBinary.WriteInt64Fixed(writer, (long)(object)value);
                else if (underlying == typeof(ulong)) SerializerBinary.WriteInt64Fixed(writer, (long)(ulong)(object)value);
                else if (underlying == typeof(short)) SerializerBinary.WriteInt16Fixed(writer, (short)(object)value);
                else if (underlying == typeof(ushort)) SerializerBinary.WriteInt16Fixed(writer, (short)(ushort)(object)value);
                else if (underlying == typeof(byte)) SerializerBinary.WriteByte(writer, (byte)(object)value);
                else if (underlying == typeof(sbyte)) SerializerBinary.WriteByte(writer, (byte)(sbyte)(object)value);
            }

            public override void Deserialize(ref global::System.Buffers.SequenceReader<byte> reader, ref T value)
            {
                var underlying = Enum.GetUnderlyingType(typeof(T));
                if (underlying == typeof(int)) value = (T)(object)SerializerBinary.ReadInt32Fixed(ref reader);
                else if (underlying == typeof(uint)) value = (T)(object)SerializerBinary.ReadUInt32Fixed(ref reader);
                else if (underlying == typeof(long)) value = (T)(object)SerializerBinary.ReadInt64Fixed(ref reader);
                else if (underlying == typeof(ulong)) value = (T)(object)(ulong)SerializerBinary.ReadInt64Fixed(ref reader);
                else if (underlying == typeof(short)) value = (T)(object)SerializerBinary.ReadInt16Fixed(ref reader);
                else if (underlying == typeof(ushort)) value = (T)(object)(ushort)SerializerBinary.ReadInt16Fixed(ref reader);
                else if (underlying == typeof(byte)) value = (T)(object)SerializerBinary.ReadByte(ref reader);
                else if (underlying == typeof(sbyte)) value = (T)(object)(sbyte)SerializerBinary.ReadByte(ref reader);
            }
        }

    }
}