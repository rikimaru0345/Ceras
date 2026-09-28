using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Ceras
{
    /// <summary>
    /// Fast, transient serialization suitable for networking and undo stacks.
    /// Does not support version tolerance or schema migration.
    /// </summary>
    public sealed class CerasFastSerializer : CerasSerializer
    {
        public new CerasRawConfig Config => (CerasRawConfig)base.Config;

        public CerasFastSerializer(Action<CerasRawConfig> configure = null!) : base(MakeConfig(configure)) { }
        internal CerasFastSerializer(CerasRawConfig config) : base(config) { }

        static CerasRawConfig MakeConfig(Action<CerasRawConfig> configure)
        {
            var config = new CerasRawConfig();
            configure?.Invoke(config);
            return config;
        }

        /// <summary>
        /// The serialized bytes. Never null: an object that writes nothing produces an empty array.
        /// </summary>
        public new byte[] Serialize<T>(T obj) => base.Serialize(obj);
        public new void Serialize<T>(IBufferWriter<byte> writer, T obj) => base.Serialize(writer, obj);
        /// <inheritdoc cref="CerasSerializer.Serialize{T}(T, ref byte[], int)"/>
        /// <remarks>
        /// The buffer can be replaced by a larger one - read it back after the call. If it was, the
        /// array you were handed is the shared pool's rather than yours, so only that one may be
        /// passed to <see cref="CerasBufferPool.Return(byte[])"/>.
        /// </remarks>
        public new int Serialize<T>(T obj, ref byte[] buffer, int offset = 0) => base.Serialize(obj, ref buffer, offset);

        /// <summary>
        /// Reads a value. Returns null (or <c>default</c>) when the bytes describe a null value.
        /// </summary>
        [return: MaybeNull]
        public new T Deserialize<T>(byte[] buffer) => base.Deserialize<T>(buffer);
        public new void Deserialize<T>(ref T obj, byte[] buffer) => base.Deserialize(ref obj, buffer);
        public void Deserialize<T>(ref T obj, byte[] buffer, ref int offset) => base.Deserialize(ref obj, buffer, ref offset);
        public new void Deserialize<T>(ref SequenceReader<byte> reader, ref T obj) => base.Deserialize(ref reader, ref obj);

        /// <summary>
        /// Round-trips a value through the serializer. Returns null (or <c>default</c>) when the
        /// object was null, so a clone of a null is a null rather than a fresh instance.
        /// </summary>
        [return: MaybeNull]
        public new T Clone<T>(T obj) => base.Clone(obj);
        public new object UserContext { get => base.UserContext; set => base.UserContext = value; }
    }
}
