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

    // A formatter that is relying on the Schema of a Type in some way (either directly: SchemaDynamicFormatter, or indirectly ReferenceFormatter)
    // Those formatters need to be notified when the CurrentSchema of a Type changes (because of reading an older schema, or because we're resetting to the primary schema for writing) 
    interface ISchemaTaintedFormatter
    {
        void OnSchemaChanged(TypeMetaData meta);
    }

    interface IInlineEmitter
    {
        System.Linq.Expressions.Expression EmitWrite(System.Linq.Expressions.ParameterExpression bufferExp, System.Linq.Expressions.ParameterExpression offsetExp, System.Linq.Expressions.ParameterExpression valueExp, out int writtenSize);
        System.Linq.Expressions.Expression EmitRead(System.Linq.Expressions.ParameterExpression bufferExp, System.Linq.Expressions.ParameterExpression offsetExp, System.Linq.Expressions.ParameterExpression valueExp, out int readSize);
    }

    static class FormatterHelper
    {
        public static bool IsFormatterMatch(IFormatter formatter, Type type)
        {
            var closedFormatter = ReflectionHelper.FindClosedType(formatter.GetType(), typeof(IFormatter<>));
            var formattedType = closedFormatter.GetGenericArguments()[0];
            return type == formattedType;
        }

        public static void ThrowOnMismatch(IFormatter formatter, Type typeToFormat)
        {
            if (!IsFormatterMatch(formatter, typeToFormat))
                throw new InvalidOperationException($"The given formatter '{formatter.GetType().FullName}' is not an exact match for the formatted type '{typeToFormat.FullName}'");
        }
    }
}
