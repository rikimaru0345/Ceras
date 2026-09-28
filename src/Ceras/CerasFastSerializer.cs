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
        /// <summary>
        /// Writes <paramref name="obj"/> at offset 0 of a pooled <paramref name="buffer"/> and
        /// returns the number of bytes written. The allocation-free path for a message written every
        /// frame: rent once, write many, return once.
        /// <para>
        /// The buffer must come from <see cref="CerasBufferPool.Rent"/> (or
        /// <see cref="CerasSerializer.RentWriteBuffer"/>) and must be returned to
        /// <see cref="CerasBufferPool.Return"/> when finished. If the object does not fit, Ceras
        /// returns the replaced array itself and hands back a larger one, so read
        /// <paramref name="buffer"/> back after every call. There is never a moment where you hold a
        /// replaced array, so returning the one you hold can never be a double return.
        /// </para>
        /// </summary>
        public new int Serialize<T>(T obj, ref byte[] buffer) => base.Serialize(obj, ref buffer);

        /// <inheritdoc cref="CerasSerializer.Serialize{T}(T, ref byte[], ref int)"/>
        public new int Serialize<T>(T obj, ref byte[] buffer, ref int offset) => base.Serialize(obj, ref buffer, ref offset);

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

        /// <summary>
        /// What this context is, for a connection-time handshake or a diagnostic: its context name,
        /// whether blittable structs use the reinterpret formatter, and the Ceras version.
        /// <para>
        /// It carries no layout fingerprint, and no member that could be mistaken for one: the transient
        /// layout is not hashed and is not stable across compiles of your types, so the only thing that
        /// can honestly be reported is that both ends must be the same build.
        /// </para>
        /// </summary>
        public Ceras.Format.TransientCompatibilityDescriptor TransientCompatibility
            => Ceras.Format.CompatibilityDescriptors.For(Config);
    }
}
