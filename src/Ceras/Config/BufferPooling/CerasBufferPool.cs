using System;

namespace Ceras
{
	/// <summary>
	/// A pool that Ceras will to rent buffers from (and return it to), assign it to <see cref="CerasBufferPool.Pool"/>.
	/// 
	/// <para>If you are looking to get some more performance easily, then take a look at <see cref="CerasDefaultBufferPool"/> that Ceras comes with (only on platforms *newer* than NET45) for a very good default implementation!</para>
	/// 
	/// <para>Also take a look at the documentation on <see cref="CerasBufferPool"/> </para>
	///
	/// <para>Since <see cref="CerasBufferPool.Pool"/> is static, it is strongly advised to make the implementation thread-safe in some way.</para>
	/// </summary>
	public interface ICerasBufferPool
	{
		byte[] RentBuffer(int minimumSize);
		void Return(byte[] buffer);
	}

	/// <summary>
	/// By default, Ceras does not use a buffer pool (using an instance of 'NullPool' which just ) because the user might not be an experienced programmer and can't be expected to know that buffers from a pool must not be kept/stored.
	/// <para>Most of the time simply having a 'byte[] buffer' field that you pass by ref </para>
	/// <para>Check out <see cref="CerasDefaultBufferPool"/> for a really good default implementation</para>
	/// </summary>
	public static class CerasBufferPool
	{
		public static ICerasBufferPool Pool = NullPool.Instance;

		internal static byte[] Rent(int minimumSize) => Pool.RentBuffer(minimumSize);
		internal static void Return(byte[] buffer) => Pool.Return(buffer);
	}

	// Default / Fallback pool that only allocates and lets the GC clean-up (assuming the user just throws the buffers they receive away)
	sealed class NullPool : ICerasBufferPool
	{
		internal static readonly NullPool Instance = new NullPool();

		NullPool() { }

		public byte[] RentBuffer(int minimumSize)
		{
			return new byte[minimumSize];
		}

		public void Return(byte[] buffer)
		{
		}
	}

#if !NET45
	/// <summary>
	/// A simple implementation for <see cref="ICerasBufferPool"/> that you can use. This is a wrapper around <see cref="System.Buffers.ArrayPool{T}"/> which is a great implementation.
	/// <para>Ceras comes with this pre-made pool because <see cref="System.Buffers.ArrayPool{T}"/> is very good pool to use that leaves pretty much nothing to be desired. Consider using this instead of going through the effort of creating your own <see cref="ICerasBufferPool"/> implementation.</para>
	/// <para>Ceras does *not* use any sort of pool by default (<see cref="CerasBufferPool"/>.Pool is null be default!) since we can't assume that the user will definitely not re-use any buffers returned from Ceras' <c>Serialize</c> methods.</para>
	/// </summary>
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
#endif

}
