namespace Ceras.Formatters
{
    using System;
    using System.Buffers;
    using Ceras.Helpers;

    sealed class SchemaDynamicFormatter<T> : IFormatter<T>
    {
        readonly CerasSerializer _ceras;
        readonly Schema _schema;
        readonly IFormatter<T> _internalFormatter;

        public SchemaDynamicFormatter(CerasSerializer ceras, Schema schema)
        {
            _ceras = ceras;
            _schema = schema;
            var formatterType = typeof(DynamicFormatter<>).MakeGenericType(typeof(T));
            _internalFormatter = (IFormatter<T>)Activator.CreateInstance(formatterType, new object[] { ceras, false });
        }

        public void Serialize(IBufferWriter<byte> writer, T value)
        {
            _internalFormatter.Serialize(writer, value);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            _internalFormatter.Deserialize(ref reader, ref value);
        }
    }
}
