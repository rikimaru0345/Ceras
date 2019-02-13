using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	using System.Runtime.InteropServices;

	/// <summary>
	/// Extremely fast formatter that can be used with all unmanaged types. For example DateTime, int, Vector3, Point, ...
	/// <para>This formatter always uses native endianness!</para>
	/// </summary>
	public sealed unsafe class ReinterpretFormatter<T> : IFormatter<T> where T : unmanaged
	{
		readonly int _size;

		public ReinterpretFormatter()
		{
			_size = Marshal.SizeOf(default(T));
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, _size);

			fixed (byte* pBuffer = buffer)
			{
				var ptr = (T*)(pBuffer + offset);
				*ptr = value;
			}

			offset += _size;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			fixed (byte* pBuffer = buffer)
			{
				var ptr = (T*)(pBuffer + offset);
				value = *ptr;
			}

			offset += _size;
		}
	}

	/// <summary>
	/// Extremely fast formatter that can be used with all unmanaged types. For example DateTime, int, Vector3, Point, ...
	/// <para>This formatter always uses native endianness!</para>
	/// </summary>
	public sealed class ReinterpretArrayFormatter<T> : IFormatter<T[]> where T : unmanaged
	{
		readonly uint _maxCount;
		readonly int _size;

		public ReinterpretArrayFormatter() : this(uint.MaxValue)
		{
		}

		public ReinterpretArrayFormatter(uint maxCount)
		{
			_maxCount = maxCount;
			_size = Marshal.SizeOf(default(T));
		}

		public void Serialize(ref byte[] buffer, ref int offset, T[] value)
		{
			// Ensure capacity
			int size = _size;
			int neededSize = size + 5;
			SerializerBinary.EnsureCapacity(ref buffer, offset, neededSize);

			// Count
			int count = value.Length;
			SerializerBinary.WriteUInt32NoCheck(buffer, ref offset, (uint)count);

			// Write
			var bytes = count * size;
			Buffer.BlockCopy(value, 0, buffer, offset, bytes);

			offset += _size;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T[] value)
		{
			// Count
			int count = (int)SerializerBinary.ReadUInt32(buffer, ref offset);

			if(count > _maxCount)
				throw new InvalidOperationException($"The data contains a byte-array of size '{count}', which exceeds the allowed limit of '{_maxCount}'");

			// Create target array
			if(value == null || value.Length != count)
				value = new T[count];

			// Read
			int bytes = count * _size;
			Buffer.BlockCopy(buffer, offset, value, 0, bytes);
			
			offset += _size;
		}
	}

}
