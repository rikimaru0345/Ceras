using Ceras.Helpers;
using System;
using System.Buffers;

namespace Ceras.Formatters
{
    public interface IFormatter 
    {
        void SerializeUntyped(IBufferWriter<byte> writer, object value);
        void DeserializeUntyped(ref SequenceReader<byte> reader, object container, System.Reflection.MemberInfo member);
    }

    public interface IFormatter<T> : IFormatter
    {
        void Serialize(IBufferWriter<byte> writer, T value);
        void Deserialize(ref SequenceReader<byte> reader, ref T value);
    }

    public delegate void SerializeDelegate<T>(IBufferWriter<byte> writer, T value);
    public delegate void DeserializeDelegate<T>(ref SequenceReader<byte> reader, ref T value);

    public delegate void StaticSerializeDelegate(IBufferWriter<byte> writer);
    public delegate void StaticDeserializeDelegate(ref SequenceReader<byte> reader);

    static class FormatterHelper
    {
        public static bool IsFormatterMatch(IFormatter formatter, Type type)
        {
            var closedFormatter = ReflectionHelper.FindClosedType(formatter.GetType(), typeof(IFormatter<>));
            // A closed formatter over T has exactly one argument, and it is T.
            var formattedType = closedFormatter!.GetGenericArguments()[0]!;
            return type == formattedType;
        }

        public static void ThrowOnMismatch(IFormatter formatter, Type typeToFormat)
        {
            if (!IsFormatterMatch(formatter, typeToFormat))
                throw new InvalidOperationException($"The given formatter '{formatter.GetType().FullName}' is not an exact match for the formatted type '{typeToFormat.FullName}'");
        }
    }
}
