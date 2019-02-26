using System;

namespace Ceras.Formatters
{
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;

	/// <summary>
	/// Extremely fast formatter that can be used with all unmanaged types. For example DateTime, int, Vector3, Point, ...
	/// <para>This formatter always uses native endianness!</para>
	/// </summary>
	public sealed unsafe class ReinterpretFormatter<T> : IFormatter<T>, IInlineEmitter where T : unmanaged
	{
		delegate void ReadWriteRawDelegate(byte[] buffer, int offset, ref T value);

		internal static readonly MethodInfo _writeMethod = new ReadWriteRawDelegate(Write_Raw).Method;
		internal static readonly MethodInfo _readMethod = new ReadWriteRawDelegate(Read_Raw).Method;

		internal static readonly int _size;


		static ReinterpretFormatter()
		{
			var type = typeof(T);
			
			if (type.IsEnum)
				type = type.GetEnumUnderlyingType();

			_size = Marshal.SizeOf(type);
		}

		public ReinterpretFormatter()
		{
			ThrowIfNotSupported();
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, _size);

			Write_Raw(buffer, offset, ref value);

			offset += _size;
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			Read_Raw(buffer, offset, ref value);

			offset += _size;
		}


		Expression IInlineEmitter.EmitWrite(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int writtenSize)
		{
			var call = Expression.Call(method: _writeMethod,
									   arg0: bufferExp,
									   arg1: offsetExp,
									   arg2: valueExp);
			writtenSize = _size;

			return call;
		}

		Expression IInlineEmitter.EmitRead(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int readSize)
		{
			var call = Expression.Call(method: _readMethod,
									   arg0: bufferExp,
									   arg1: offsetExp,
									   arg2: valueExp);
			readSize = _size;

			return call;
		}


		// Write value type, don't check if it fits, don't modify offset
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void Write_Raw(byte[] buffer, int offset, ref T value)
		{
			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (T*)(pBuffer + offset);
				*ptr = value;
			}
		}

		// Read value type, don't modify offset
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void Read_Raw(byte[] buffer, int offset, ref T value)
		{
			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (T*)(pBuffer + offset);
				value = *ptr;
			}
		}


		internal static void ThrowIfNotSupported()
		{
			// todo: check if T is layout sequential
			// todo: T must not have any holes, must not have any fixed buffers,
			// todo: must have the same memory layout as in the meta-data (same offsets using OffsetOf())
			// todo: must have the same size as we've expected

			if (!BitConverter.IsLittleEndian)
				throw new Exception("The reinterpret formatters require a little endian environment (CPU/OS). Please turn off " + nameof(SerializerConfig.Advanced.UseReinterpretFormatter));
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
			ReinterpretFormatter<T>.ThrowIfNotSupported();

			_maxCount = maxCount;
			_size = Marshal.SizeOf(default(T));
		}

		public unsafe void Serialize(ref byte[] buffer, ref int offset, T[] value)
		{
			// Ensure capacity
			int size = _size;
			int neededSize = size + 5;
			SerializerBinary.EnsureCapacity(ref buffer, offset, neededSize);

			// Count
			int count = value.Length;
			SerializerBinary.WriteUInt32NoCheck(buffer, ref offset, (uint)count);

			int bytes = count * size;
			if (bytes == 0)
				return;

			// Write
#if NETSTANDARD2_0 || NET47
			fixed (T* srcAr = &value[0])
			fixed (byte* dest = &buffer[offset])
			{
				byte* srcByteAr = (byte*) srcAr;
				Buffer.MemoryCopy(srcByteAr, dest, bytes, bytes);
			}
#else
			fixed (T* p = &value[0])
				Marshal.Copy(new IntPtr(p), buffer, offset, count);
#endif

			offset += bytes;
		}

		public unsafe void Deserialize(byte[] buffer, ref int offset, ref T[] value)
		{
			// Count
			int count = (int)SerializerBinary.ReadUInt32(buffer, ref offset);

			if (count > _maxCount)
				throw new InvalidOperationException($"The data describes an array with '{count}' elements, which exceeds the allowed limit of '{_maxCount}'");

			// Create target array
			if (value == null || value.Length != count)
				value = new T[count];

			int bytes = count * _size;

			if (bytes == 0)
				return;

			// Read
			fixed (T* ar = &value[0])
			{
				byte* byteAr = (byte*)ar;
				Marshal.Copy(buffer, offset, new IntPtr(byteAr), bytes);
			}

			offset += bytes;
		}
	}

	// We can often write multiple unmanaged things in one batch
	interface IInlineEmitter
	{
		// Implement a call 
		Expression EmitWrite(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int writtenSize);
		Expression EmitRead(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int readSize);
	}
}
