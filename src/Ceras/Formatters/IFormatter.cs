using Ceras.Helpers;
using System;

namespace Ceras.Formatters
{

    public interface IFormatter { }

    public interface IFormatter<T> : IFormatter
    {
        void Serialize(ref byte[] buffer, ref int offset, T value);
        void Deserialize(byte[] buffer, ref int offset, ref T value);
    }

    public delegate void SerializeDelegate<T>(ref byte[] buffer, ref int offset, T value);
    public delegate void DeserializeDelegate<T>(byte[] buffer, ref int offset, ref T value);

    public delegate void StaticSerializeDelegate(ref byte[] buffer, ref int offset);
    public delegate void StaticDeserializeDelegate(byte[] buffer, ref int offset);


    // A formatter that is relying on the Schema of a Type in some way (either directly: SchemaDynamicFormatter, or indirectly ReferenceFormatter)
    // Those formatters need to be notified when the CurrentSchema of a Type changes (because of reading an older schema, or because we're resetting to the primary schema for writing) 
    interface ISchemaTaintedFormatter
    {
        void OnSchemaChanged(TypeMetaData meta);
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