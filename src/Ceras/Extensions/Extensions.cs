using System;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
	using System.IO;

	public static class CerasNetworkingExampleExtensions
	{
		[ThreadStatic] static byte[] _lengthPrefixBuffer;
		[ThreadStatic] static byte[] _streamBuffer;

		
		/// <summary>
		/// Writes an object into a stream. This method prefixes the data with the actual size (in VarInt encoding).
		/// <para>This method(-pair) is intended to be an easy to understand example for networking scenarios.</para>
		/// <para>The implementation is reasonably efficient, but of course you can do a lot better with a solution specifically tailored to your scenario...</para>
		/// </summary>
		public static void WriteToStream(this CerasSerializer ceras, Stream stream, object obj)
		{
			if (_lengthPrefixBuffer == null)
				_lengthPrefixBuffer = new byte[5];


			// Serialize the object
			int size = ceras.Serialize<object>(obj, ref _streamBuffer);

			// Determine size prefix of the packet
			int sizeBytesLength = 0;
			SerializerBinary.WriteUInt32(ref _lengthPrefixBuffer, ref sizeBytesLength, (uint)size);

			// Write size
			stream.Write(_lengthPrefixBuffer, 0, sizeBytesLength);

			// Write payload
			stream.Write(_streamBuffer, 0, size);
		}

		/// <summary>
		/// Reads an object that was written using <see cref="WriteToStream(CerasSerializer, object, Stream)"/> by reading the size-prefix and then deserializing the data.
		/// <para>This method(-pair) is intended to be an easy to understand example for networking scenarios.</para>
		/// </summary>
		public static async Task<object> ReadFromStream(this CerasSerializer ceras, Stream stream)
		{
			// Read length bytes
			var length = (int)await ReadVarIntFromStream(stream);

			var recvBuffer = new byte[length];

			// Keep reading until we have the full packet
			int totalRead = 0;

			while (totalRead < length)
			{
				int leftToRead = length - totalRead;

				int read = await stream.ReadAsync(recvBuffer, totalRead, leftToRead);

				if (read <= 0)
					throw new Exception("Stream closed");

				totalRead += read;
			}

			// We have the full packet; now deserialize it
			var obj = ceras.Deserialize<object>(recvBuffer);

			return obj;
		}


		static async Task<uint> ReadVarIntFromStream(Stream stream)
		{
			var recvPrefixBuffer = new byte[1];
			
			int shift = 0;
			ulong result = 0;
			const int bits = 32;

			while (true)
			{
				int n = await stream.ReadAsync(recvPrefixBuffer, 0, 1);
				if (n <= 0)
					throw new Exception("Stream terminated");

				ulong byteValue = recvPrefixBuffer[0];

				ulong tmp = byteValue & 0x7f;
				result |= tmp << shift;

				if (shift > bits)
					throw new Exception("Malformed VarInt");

				if ((byteValue & 0x80) != 0x80)
					return (uint)result;

				shift += 7;
			}
		}
	}
}
