namespace Ceras.Resolvers
{
    using Formatters;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A really really boring resolver that produces formatters for all the "primitives" like bool, int, float, double, ... 
    /// </summary>
    public sealed class PrimitiveResolver : IFormatterResolver
    {
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

        public IFormatter GetFormatter(Type type)
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