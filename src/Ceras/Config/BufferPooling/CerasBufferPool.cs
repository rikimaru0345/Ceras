using System;

namespace Ceras
{

	/// <summary>
	/// By default Ceras doesn't do any buffer pooling for you, because you need to be aware of some things (like not creating your own buffers, not keeping buffer references around after serialization, ...).
	/// Ceras does however come with a default implementation that you can use (if you want to). To do so you can either just assign a new instance of 'CerasDefaultBufferPool' to the 'Pool' field, or create your own implementation.
	/// </summary>
	public static class CerasBufferPool
	{
		public static ICerasBufferPool Pool = null; // No pooling by default
	}

	/// <summary>
	/// An object that Ceras uses to rent buffers from (and return it to).
	/// Assign it to <see cref="CerasBufferPool.Pool"/>.
	/// For performance reasons, there is always only one implementation for all Ceras instances; using some sort of thread-local storage inside your implementation is advised.
	/// </summary>
	public interface ICerasBufferPool
	{
		byte[] RentBuffer(int minimumSize);
		void Return(byte[] buffer);
	}

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
	/// Uses <see cref="System.Buffers.ArrayPool{T}"/>
	/// </summary>
	class CerasDefaultBufferPool : ICerasBufferPool
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
