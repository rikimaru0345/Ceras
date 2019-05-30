using System;

namespace Ceras
{
	/// <summary>
	/// A pool that Ceras will to rent buffers from (and return it to), assign it to <see cref="CerasBufferPool.Pool"/>.
	/// 
	/// <para>Also take a look at the documentation on <see cref="CerasBufferPool"/> </para>
	///
	/// <para>Since <see cref="CerasBufferPool.Pool"/> is static, it is strongly advised to make the implementation thread-safe in some way.</para>
	/// </summary>
	/// 
	public interface ICerasBufferPool
	{
		byte[] RentBuffer(int minimumSize);
		void Return(byte[] buffer);
	}

	/// <summary>
	/// Whenever you call <c>Serialize</c> and the given buffer is 'null' or runs out of space during the serialization,
	/// Ceras will use the implementation in <see cref="CerasBufferPool.Pool"/> to request a new one and return the old one.
	/// 
	/// <para>
	/// By default Ceras does not use any pooling mechanism because incorrect usage will cause bugs and crashes.
	/// That's why Ceras uses its so called "NullPool" by default, which just calls <c>new byte[]</c> for <see cref="ICerasBufferPool.RentBuffer(int)"/>.
	/// If you want to use buffer pooling then pay close attention to the following points:
	/// </para>
	/// 
	/// <para>
	/// In many scenarios you don't actually need a pool if you simply use <see cref="CerasSerializer.Serialize{T}(T, ref byte[], int)"/> correctly.
	/// Just have a field like 'byte[] buffer;' somewhere and pass it by ref.
	/// If its too small (or even null) then Ceras will expand the buffer.
	/// Just recycle the buffer for subsequent calls and use the return value of 'Serialize' to know how many bytes were written.
	/// </para>
	/// 
	/// <para>
	/// Most pool implementations can not handle getting a buffer returned to them that was not rented from them in the first place.
	/// So don't create a buffer using 'new byte[]' when using a pool and instead rent one from it!
	/// </para>
	/// 
	/// For a good example implementation of a pool take a look at the comments in source code of this file.
	/// </summary>
	public static class CerasBufferPool
	{
		public static ICerasBufferPool Pool = NullPool.Instance;

		internal static byte[] Rent(int minimumSize) => Pool.RentBuffer(minimumSize);
		internal static void Return(byte[] buffer) => Pool.Return(buffer);
	}

	sealed class NullPool : ICerasBufferPool
	{
		internal static readonly NullPool Instance = new NullPool();

		NullPool() { }

		public byte[] RentBuffer(int minimumSize) => new byte[minimumSize];

		public void Return(byte[] buffer) { }
	}


	/*
	 * A simple implementation for <see cref="ICerasBufferPool"/> that you can use.
	 * ArrayPool is a great implementation, but it is only available on netstandard2.0 (or if you install the System.Buffers package from NuGet).
	 */

	/*
	public sealed class CerasDefaultBufferPool : ICerasBufferPool
	{
		public byte[] RentBuffer(int minimumSize)
		{
			return System.Buffers.ArrayPool<byte>.Shared.Rent(minimumSize);
		}

		public void Return(byte[] buffer)
		{
			System.Buffers.ArrayPool<byte>.Shared.Return(buffer, false);
		}
	}
	*/
}
