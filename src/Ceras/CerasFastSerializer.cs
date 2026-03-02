using System;
using System.Buffers;

namespace Ceras
{
    /// <summary>
    /// Fast, transient serialization suitable for networking and undo stacks.
    /// Does not support version tolerance or schema migration.
    /// </summary>
    public class CerasFastSerializer
    {
        readonly CerasSerializer _serializer;
        public CerasRawConfig Config { get; }

        public CerasFastSerializer(Action<CerasRawConfig> configure = null)
        {
            Config = new CerasRawConfig();
            configure?.Invoke(Config);
            _serializer = new CerasSerializer(Config);
        }

        public byte[] Serialize<T>(T obj) => _serializer.Serialize(obj);
        public void Serialize<T>(IBufferWriter<byte> writer, T obj) => _serializer.Serialize(writer, obj);
        public int Serialize<T>(T obj, ref byte[] buffer, int offset = 0) => _serializer.Serialize(obj, ref buffer, offset);

        public T Deserialize<T>(byte[] buffer) => _serializer.Deserialize<T>(buffer);
        public void Deserialize<T>(ref T obj, byte[] buffer) => _serializer.Deserialize(ref obj, buffer);
        public void Deserialize<T>(ref T obj, byte[] buffer, ref int offset) => _serializer.Deserialize(ref obj, buffer, ref offset);
        public void Deserialize<T>(ref SequenceReader<byte> reader, ref T obj) => _serializer.Deserialize(ref reader, ref obj);
    }
}
