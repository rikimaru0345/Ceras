using System.Buffers;

namespace Ceras
{
    /// <summary>
    /// Provides a pool of buffers for Ceras to use.
    /// </summary>
    public static class CerasBufferPool
    {
        /// <summary>
        /// Rent a buffer from the shared pool.
        /// </summary>
        public static byte[] Rent(int minimumSize) => ArrayPool<byte>.Shared.Rent(minimumSize);

        /// <summary>
        /// Return a buffer to the shared pool.
        /// </summary>
        public static void Return(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
    }
}
