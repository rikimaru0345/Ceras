using System;

namespace Ceras.Formatters
{
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;
	using Helpers;

	/// <summary>
	/// Extremely fast formatter that can be used with all unmanaged types. For example DateTime, int, Vector3, Point, ...
	/// <para>This formatter always uses native endianness!</para>
	/// </summary>
	public sealed unsafe class ReinterpretFormatter<T> : IFormatter<T>, IInlineEmitter where T : unmanaged
	{
		delegate void ReadWriteRawDelegate(byte[] buffer, int offset, ref T value);

		internal static readonly MethodInfo _writeMethod = new ReadWriteRawDelegate(Write_Raw).Method;
		internal static readonly MethodInfo _readMethod = new ReadWriteRawDelegate(Read_Raw).Method;
		

		static ReinterpretFormatter()
		{
			/*
			var type = typeof(T);

			if (type.IsEnum)
				type = type.GetEnumUnderlyingType();

			_itemSize = ReflectionHelper.GetSize(type);
			if (_itemSize < 0)
				throw new InvalidOperationException("Type is not blittable");
			*/
		}

		public ReinterpretFormatter()
		{
			ThrowIfNotSupported();
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value)
		{
			SerializerBinary.EnsureCapacity(ref buffer, offset, Unsafe.SizeOf<T>());

			Write_Raw(buffer, offset, ref value);

			offset += Unsafe.SizeOf<T>();
		}

		public void Deserialize(byte[] buffer, ref int offset, ref T value)
		{
			Read_Raw(buffer, offset, ref value);

			offset += Unsafe.SizeOf<T>();
		}


		// Write value type, don't check if it fits, don't modify offset
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void Write_Raw(byte[] buffer, int offset, ref T value)
		{
			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (T*)(pBuffer + offset);
				*ptr = value;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void Write_Raw(byte[] buffer, int offset, T value)
		{
			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (T*)(pBuffer + offset);
				*ptr = value;
			}
		}

		// Read value type, don't modify offset
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void Read_Raw(byte[] buffer, int offset, ref T value)
		{
			fixed (byte* pBuffer = &buffer[0])
			{
				var ptr = (T*)(pBuffer + offset);
				value = *ptr;
			}
		}


		Expression IInlineEmitter.EmitWrite(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int writtenSize)
		{
			var call = Expression.Call(method: _writeMethod,
									   arg0: bufferExp,
									   arg1: offsetExp,
									   arg2: valueExp);

			writtenSize = Unsafe.SizeOf<T>();
			return call;
		}

		Expression IInlineEmitter.EmitRead(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int readSize)
		{
			var call = Expression.Call(method: _readMethod,
									   arg0: bufferExp,
									   arg1: offsetExp,
									   arg2: valueExp);

			readSize = Unsafe.SizeOf<T>();
			return call;
		}


		internal static void ThrowIfNotSupported()
		{
			// todo: check if T is layout sequential
			// todo: T must not have any holes, must not have any fixed buffers,
			// todo: must have the same memory layout as in the meta-data (same offsets using OffsetOf())
			// todo: must have the same size as we've expected

			if (!BitConverter.IsLittleEndian)
				throw new Exception("ReinterpretFormatter requires little endian environment. Please turn off " + nameof(SerializerConfig.Advanced.UseReinterpretFormatter));
		}

	}

	/// <summary>
	/// Extremely fast formatter that can be used with all blitable types. For example DateTime, int, Vector3, Point, ...
	/// <para>This formatter always uses native endianness!</para>
	/// </summary>
	public sealed class ReinterpretArrayFormatter<T> : IFormatter<T[]> where T : unmanaged
	{
		static readonly int _itemSize;
		readonly uint _maxCount;

		static ReinterpretArrayFormatter()
		{
			var type = typeof(T);

			if (type.IsEnum)
				type = type.GetEnumUnderlyingType();

			_itemSize = ReflectionHelper.GetSize(type);
			if (_itemSize < 0)
				throw new InvalidOperationException("Type is not blittable");
		}

		public ReinterpretArrayFormatter(uint maxCount)
		{
			ReinterpretFormatter<T>.ThrowIfNotSupported();
			_maxCount = maxCount;
		}

		public unsafe void Serialize(ref byte[] buffer, ref int offset, T[] value)
		{
			int count = value.Length;

			// Ensure capacity
			int size = _itemSize;
			int neededSize = (count * size) + 5;
			SerializerBinary.EnsureCapacity(ref buffer, offset, neededSize);

			// Count
			SerializerBinary.WriteUInt32NoCheck(buffer, ref offset, (uint)count);

			int bytes = count * size;
			if (bytes == 0)
				return;

			// Write
			fixed (T* srcAr = &value[0])
			fixed (byte* destAr = &buffer[0])
			{
				byte* src = (byte*)srcAr;
				byte* dest = destAr + offset;
				SerializerBinary.FastCopy(src, dest, (uint)bytes);
			}


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


			int bytes = count * _itemSize;
			if (bytes == 0)
				return;

			int remainingBytes = buffer.Length - offset;
			if(bytes > remainingBytes)
				throw new IndexOutOfRangeException($"Trying to read an array of '{typeof(T).FriendlyName()}' ({count} elements, {bytes} bytes) but only {remainingBytes} bytes are left in the buffer (buffer length: {buffer.Length}, offset: {offset}).");

			// Read
			fixed (byte* srcAr = &buffer[0])
			fixed (T* destAr = &value[0])
			{
				byte* src = srcAr + offset;
				byte* dest = (byte*)destAr;
				SerializerBinary.FastCopy(src, dest, (uint)bytes);
			}

			offset += bytes;
		}
	}


	/*
	 * I tried for days to get this working for all cases.
	 * But it's too brittle.
	 * Something like this will break it:
	 * 
	 * KeyValuePair<Vector3, bool>[,] ar6 = new[,]
	 * {
	 *   { new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), },
	 *	 { new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), },
	 * };
	 * 
	 * 
	/// <summary>
	/// Like <see cref="ReinterpretArrayFormatter{T}"/> but for multidimensional arrays.
	/// Since the target type is 'Array' it can't be used directly (because Serialize/Deserialize must use the specific type)
	/// </summary>
	public class MultiDimensionalReinterpretArrayFormatter<TItem> :
		IFormatter<Array>,
		IFormatter<TItem[,]>, // 2D
		IFormatter<TItem[,,]>, // 3D
		IFormatter<TItem[,,,]>, // 4D
		IFormatter<TItem[,,,,]>, // 5D
		IFormatter<TItem[,,,,,]> // 6D
	{
		readonly Type _itemType;
		readonly int _itemSize;
		readonly uint _maxCount;

		public MultiDimensionalReinterpretArrayFormatter(uint maxCount)
		{
			_itemType = typeof(TItem);
			_itemSize = ReflectionHelper.GetSize(typeof(TItem));
			_maxCount = maxCount;
		}


		public unsafe void Serialize(ref byte[] buffer, ref int offset, Array baseAr)
		{
			int count = baseAr.Length;

			// Dimensions
			int dimensions = baseAr.Rank;
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)dimensions);

			// Dimension sizes
			for (int d = 0; d < dimensions; d++)
			{
				var size = baseAr.GetLength(d);
				SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)size);
			}

			// Bytes
			int bytes = count * _itemSize;
			if (bytes == 0)
				return;

			// Ensure capacity for elements
			SerializerBinary.EnsureCapacity(ref buffer, offset, bytes);

			// Write

			// BlockCopy will not work when the element type is a struct
			// Buffer.BlockCopy(baseAr, 0, buffer, offset, bytes);

			GCHandle arrayHandle = default;
			GCHandle bufferHandle = default;
			try
			{
				arrayHandle = GCHandle.Alloc(baseAr, GCHandleType.Pinned);
				byte* arBytes = (byte*)arrayHandle.AddrOfPinnedObject();

				bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
				byte* bufferBytes = (byte*)bufferHandle.AddrOfPinnedObject();
				bufferBytes += offset;

				SerializerBinary.FastCopy(arBytes, bufferBytes, bytes);
			}
			finally
			{
				arrayHandle.Free();
				bufferHandle.Free();
			}
			
			offset += bytes;

		}

		public unsafe void Deserialize(byte[] buffer, ref int offset, ref Array baseAr)
		{
			// Dimensions
			int dimensions = (int)SerializerBinary.ReadUInt32(buffer, ref offset);

			// Dimension sizes
			var dimensionSizes = new int[dimensions];
			for (int d = 0; d < dimensions; d++)
			{
				var size = (int)SerializerBinary.ReadUInt32(buffer, ref offset);
				dimensionSizes[d] = size;
			}

			// Count
			int count = dimensionSizes[0];
			for (int d = 1; d < dimensions; d++)
				count *= dimensionSizes[d];

			if (count > _maxCount)
				throw new InvalidOperationException($"The data describes an array with '{count}' elements, which exceeds the allowed limit of '{_maxCount}'");


			// Create array
			baseAr = Array.CreateInstance(_itemType, dimensionSizes);

			// Read
			int bytes = count * _itemSize;
			if (bytes == 0)
				return;
			
			GCHandle bufferHandle = default;
			GCHandle arrayHandle = default;
			try
			{
				bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
				byte* bufferBytes = (byte*)bufferHandle.AddrOfPinnedObject();
				bufferBytes += offset;

				arrayHandle = GCHandle.Alloc(baseAr, GCHandleType.Pinned);
				byte* arBytes = (byte*)arrayHandle.AddrOfPinnedObject();

				SerializerBinary.FastCopy(bufferBytes, arBytes, bytes);
			}
			finally
			{
				bufferHandle.Free();
				arrayHandle.Free();
			}

			offset += bytes;
		}
	
				
	
		// 2D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		// 2D
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,])array;
		}


		// 3D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		// 3D
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,])array;
		}


		// 4D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		// 4D
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,,])array;
		}


		// 5D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		// 5D
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,,,])array;
		}


		// 6D
		public void Serialize(ref byte[] buffer, ref int offset, TItem[,,,,,] ar)
		{
			Array array = ar;
			Serialize(ref buffer, ref offset, array);
		}
		// 6D
		public void Deserialize(byte[] buffer, ref int offset, ref TItem[,,,,,] ar)
		{
			Array array = ar;
			Deserialize(buffer, ref offset, ref array);
			ar = (TItem[,,,,,])array;
		}
	}

	*/

	// Our member sorting puts blitable types together; so we can merge repeated "EnsureCapacity" calls into one big call!
	interface IInlineEmitter
	{
		Expression EmitWrite(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int writtenSize);
		Expression EmitRead(ParameterExpression bufferExp, ParameterExpression offsetExp, ParameterExpression valueExp, out int readSize);
	}
}
