using System;
using System.Buffers;

namespace Ceras
{
    /// <summary>
    /// Fast, raw serialization for transient data (networking, internal state).
    /// Does not include any schema or versioning information in the payload.
    /// Wire format is not guaranteed to be stable across builds.
    /// </summary>
    public static class CerasRaw
    {
        public static byte[] Serialize<T>(T obj, CerasSerializer serializer)
        {
            return serializer.Serialize(obj);
        }

        public static void Serialize<T>(IBufferWriter<byte> writer, T obj, CerasSerializer serializer)
        {
            serializer.Serialize(writer, obj);
        }

        public static T Deserialize<T>(byte[] data, CerasSerializer serializer)
        {
            return serializer.Deserialize<T>(data);
        }

        public static void Deserialize<T>(ref T value, byte[] data, CerasSerializer serializer)
        {
            serializer.Deserialize(ref value, data);
        }

        public static void Deserialize<T>(ref SequenceReader<byte> reader, ref T value, CerasSerializer serializer)
        {
            serializer.Deserialize(ref reader, ref value);
        }
    }
}
