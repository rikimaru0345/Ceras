namespace Ceras.Versioning
{
    using System;
    using System.Buffers;
    using Ceras.Formatters;
    using Ceras.Helpers;

    sealed class DynamicEmulator<T> : FormatterBase<T>
    {
        readonly CerasSerializer _ceras;
        readonly Schema _schema;

        public DynamicEmulator(CerasSerializer ceras, Schema schema)
        {
            _ceras = ceras;
            _schema = schema;
        }

        public override void Serialize(IBufferWriter<byte> writer, T value)
        {
            throw new NotSupportedException("Emulation only supports deserialization.");
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            throw new NotSupportedException("Emulation not updated to Span-first yet.");
        }
    }
}
