using System;
using System.IO;
using System.Buffers;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
    public static class CerasNetworkingExampleExtensions
    {
        // Thread-static and null until this thread's first Serialize fills it (the buffer is handed
        // back by ref so it is reused); the annotation is the one RobustContext._current uses.
        [ThreadStatic] static byte[] _streamBuffer = null!;

        public static void WriteToStream(this CerasFastSerializer ceras, Stream stream, object obj)
        {
            // Serialize the object
            int size = ceras.Serialize<object>(obj, ref _streamBuffer);

            // Write size prefix
            var writer = new ArrayBufferWriter<byte>(5);
            SerializerBinary.WriteUInt32(writer, (uint)size);
            stream.Write(writer.WrittenSpan);

            // Write payload
            stream.Write(_streamBuffer, 0, size);
        }

        /// <summary>
        /// Reads a value written by <see cref="WriteToStream"/>, or null when the bytes hold the
        /// null marker.
        /// <para>
        /// The null is a value, not a failure. The wire format encodes it, so a payload written from
        /// a null object reads back as null and the round trip preserves that rather than
        /// substituting a default - which is the same contract
        /// <see cref="CerasFastSerializer.Deserialize{T}(byte[])"/> itself has, and why its result
        /// is <c>MaybeNull</c>. A caller that needs an object checks for null (or passes a
        /// <c>byte[]</c> it knows is non-null by construction).
        /// </para>
        /// </summary>
        public static async Task<object?> ReadFromStream(this CerasFastSerializer ceras, Stream stream)
        {
            var length = (int)await ReadVarIntFromStream(stream);
            var recvBuffer = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = await stream.ReadAsync(recvBuffer, totalRead, length - totalRead);
                if (read <= 0) throw new Exception("Stream closed");
                totalRead += read;
            }
            return ceras.Deserialize<object>(recvBuffer);
        }


        static async Task<uint> ReadVarIntFromStream(Stream stream)
        {
            var buf = new byte[1];
            int shift = 0;
            ulong result = 0;
            while (true)
            {
                if (await stream.ReadAsync(buf, 0, 1) <= 0) throw new Exception("Stream terminated");
                ulong b = buf[0];
                result |= (b & 0x7f) << shift;
                if ((b & 0x80) == 0) return (uint)result;
                shift += 7;
                if (shift > 32) throw new Exception("Malformed VarInt");
            }
        }
    }
}
