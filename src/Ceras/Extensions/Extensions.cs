using System;
using System.IO;
using System.Buffers;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
    public static class CerasNetworkingExampleExtensions
    {
        [ThreadStatic] static byte[] _streamBuffer;

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

        public static async Task<object> ReadFromStream(this CerasFastSerializer ceras, Stream stream)
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

        public static void WriteToStream(this CerasSerializer ceras, Stream stream, object obj)
        {
            // Serialize the object
            int size = ceras.Serialize<object>(obj, ref _streamBuffer);

            // Write size prefix
            byte[] sizePrefix = new byte[5];
            var writer = new ArrayBufferWriter<byte>(5);
            SerializerBinary.WriteUInt32(writer, (uint)size);
            stream.Write(writer.WrittenSpan);

            // Write payload
            stream.Write(_streamBuffer, 0, size);
        }

        public static async Task<object> ReadFromStream(this CerasSerializer ceras, Stream stream)
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
