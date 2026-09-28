using System.Buffers;
using System.Threading;

namespace Ceras
{
    /// <summary>
    /// Provides a pool of buffers for Ceras to use.
    /// </summary>
    public static class CerasBufferPool
    {
        /// <summary>
        /// Rentals handed out and not yet returned. A diagnostic, and the thing that makes a
        /// balanced pool checkable: a caller that rents one buffer and returns it leaves this
        /// unchanged, and a path that grows a buffer without returning the replaced one leaves it
        /// permanently higher.
        /// </summary>
        internal static int LiveRentals => Volatile.Read(ref _liveRentals);
        static int _liveRentals;

        /// <summary>
        /// Rent a buffer from the shared pool.
        /// </summary>
        public static byte[] Rent(int minimumSize)
        {
            Interlocked.Increment(ref _liveRentals);
            return ArrayPool<byte>.Shared.Rent(minimumSize);
        }

        /// <summary>
        /// Return a buffer to the shared pool.
        /// <para>
        /// Only an array this pool rented may be returned; the shared pool rejects anything else
        /// rather than quietly taking it, which is what keeps a caller's own array from being
        /// adopted by - and then handed to someone else from - the pool.
        /// </para>
        /// </summary>
        public static void Return(byte[] buffer)
        {
            Interlocked.Decrement(ref _liveRentals);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
