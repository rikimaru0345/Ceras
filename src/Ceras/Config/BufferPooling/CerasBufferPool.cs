using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ceras
{
    /// <summary>
    /// Provides a pool of buffers for Ceras to use, and is the one place that knows which arrays are
    /// its own.
    /// <para>
    /// That knowledge cannot come from <see cref="ArrayPool{T}"/>. Its <c>Return</c> <em>adopts</em>
    /// any array whose length happens to be one of its bucket sizes, so "a byte[] of a plausible
    /// length" is not a test of ownership and cannot be turned into one: a caller's own 4096-byte
    /// buffer would be accepted, filed away, and handed to an unrelated renter who would then be
    /// writing over a buffer the caller still believes it owns. A length that is not a bucket size
    /// surfaces the same mistake as an <see cref="ArgumentException"/> from inside an otherwise
    /// successful write, which is a worse version of the same defect. This pool therefore keeps its
    /// own registry of the arrays it has handed out and refuses everything else, at every path.
    /// </para>
    /// </summary>
    public static class CerasBufferPool
    {
        /// <summary>
        /// Rentals handed out and not yet returned. A diagnostic, and the thing that makes a
        /// balanced pool checkable: a caller that rents one buffer and returns it leaves this
        /// unchanged, and a path that grows a buffer without returning the replaced one leaves it
        /// permanently higher.
        /// <para>
        /// It counts rentals, not arrays, and a refused <see cref="Return"/> does not touch it: the
        /// count is decremented only after the array has been claimed as this pool's, so a refused
        /// return cannot quietly balance a lease that is still outstanding.
        /// </para>
        /// </summary>
        internal static int LiveRentals => Volatile.Read(ref _liveRentals);
        static int _liveRentals;

        /// <summary>
        /// The ownership registry: the arrays this pool has handed out, keyed weakly by the array
        /// itself.
        /// <para>
        /// Weak keys are the point, not an optimisation. A strong key would be simpler and would
        /// quietly make this table a second, immortal copy of the pool: one 1 GB rental, returned
        /// and then dropped by the pool, would stay reachable here for the life of the process. An
        /// entry therefore lives exactly as long as its array, which is bounded by what the pool
        /// itself is holding.
        /// </para>
        /// <para>
        /// Keyed by reference, which is what ownership is: two distinct 4096-byte arrays are two
        /// distinct keys, and a caller who <c>new</c>s a byte[4096] cannot collide with a rental.
        /// </para>
        /// </summary>
        static readonly ConditionalWeakTable<byte[], Rental> _rentals = new ConditionalWeakTable<byte[], Rental>();

        /// <summary>
        /// Creates the entry for an array that is a live rental from the instant it exists, rather
        /// than from after a second store. <c>GetValue</c> publishes the value it returns, so there
        /// is no window in which the table says it knows the array and the entry says it is not a
        /// rental - which <see cref="Return"/> would read as "never rented" and report against a
        /// perfectly good buffer.
        /// </summary>
        static readonly ConditionalWeakTable<byte[], Rental>.CreateValueCallback _liveRental = _ => new Rental();

        /// <summary>
        /// What the registry records about one array. A class rather than a bare int so that the
        /// transition out of "live" can be a compare-and-swap on a field every holder of the entry
        /// shares, which is what makes a double return impossible even when two threads race.
        /// </summary>
        sealed class Rental
        {
            /// <summary>An outstanding rental. The only state a caller may act on.</summary>
            public const int Live = 1;

            /// <summary>Back in the shared pool, and possibly already somebody else's.</summary>
            public const int Returned = 2;

            /// <summary>
            /// <see cref="Live"/> while the array is an outstanding rental, and one past
            /// <see cref="Returned"/> for each time it has been given back. The count is what lets a
            /// double return be told apart from a caller's own array: both are absent from the pool,
            /// but they are different bugs and send the reader to different places.
            /// </summary>
            public int State = Live;
        }

        /// <summary>
        /// Rent a buffer from the shared pool.
        /// <para>
        /// Every rental is recorded, which is what lets <see cref="Return"/> and the growth path
        /// tell this pool's arrays from everyone else's. A zero-length request is answered from the
        /// smallest real bucket rather than passed through: the shared pool answers
        /// <c>Rent(0)</c> with the process-wide <c>Array.Empty</c> singleton, which is not an array
        /// this pool could own - every caller would then share one buffer, and a single
        /// <c>Array.Empty</c> reference returned by any of them would be accepted as a rental.
        /// </para>
        /// </summary>
        /// <param name="minimumSize">Smallest usable length. Zero is served from the smallest
        /// bucket; a negative size is refused, as the shared pool refuses it.</param>
        public static byte[] Rent(int minimumSize)
        {
            if (minimumSize < 0)
                throw new ArgumentOutOfRangeException(nameof(minimumSize), minimumSize,
                    "A buffer size cannot be negative. Rent a buffer of the size the message needs, or CerasSerializer.RentWriteBuffer for the pooled write path.");

            byte[] buffer = ArrayPool<byte>.Shared.Rent(minimumSize == 0 ? 1 : minimumSize);

            // Recorded before the counter moves, so the count never names a rental the registry
            // would refuse to return. A rental that is already live is a re-rent of an array this
            // pool handed out before and took back; the new lease supersedes the old one, and the
            // previous lease's already-reported return is not a return of this one.
            var rental = _rentals.GetValue(buffer, _liveRental);
            if (rental.State != Rental.Live) Volatile.Write(ref rental.State, Rental.Live);

            Interlocked.Increment(ref _liveRentals);
            return buffer;
        }

        /// <summary>
        /// Return a buffer to the shared pool.
        /// <para>
        /// Only an array this pool is currently renting may be returned, and each rental exactly
        /// once. Anything else is refused by name - an array this pool never handed out, or one that
        /// has already been returned - because the shared pool cannot refuse it: it adopts any array
        /// whose length is one of its bucket sizes, and hands that array to the next renter of that
        /// size. A caller's own 4096-byte buffer given to it here would stop being the caller's, and
        /// the pool does not deduplicate, so a double return puts the same array in the pool twice
        /// and two unrelated renters can be given it at once. Refusing is the only thing that keeps
        /// either mistake from being silent.
        /// </para>
        /// <para>
        /// A refused return changes nothing: <see cref="LiveRentals"/> is only decremented once the
        /// array has been claimed, so a refusal cannot balance a lease that is still outstanding.
        /// </para>
        /// </summary>
        public static void Return(byte[] buffer)
        {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer),
                    "A pooled buffer is never null. If a call did not need one, do not pass one; if it grew, read the buffer back and return the one you hold now.");

            if (!_rentals.TryGetValue(buffer, out Rental? rental))
                throw new ArgumentException(NotARental(buffer), nameof(buffer));

            // Claim first, and with one compare-and-swap rather than a read followed by a write: two
            // threads returning the same array must produce exactly one pool return between them, and
            // a check-then-act pair would let both of them see "live" and both proceed.
            int previous = Interlocked.CompareExchange(ref rental.State, Rental.Returned, Rental.Live);
            if (previous != Rental.Live)
                throw new InvalidOperationException(AlreadyReturned(buffer));

            Interlocked.Decrement(ref _liveRentals);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        /// <summary>
        /// Whether <paramref name="buffer"/> is an array this pool is currently renting. False for a
        /// caller's own array, for one that has already been returned, and for the shared
        /// <c>Array.Empty</c> singleton.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsRental(byte[]? buffer)
            => buffer != null && _rentals.TryGetValue(buffer, out Rental? rental) && rental.State == Rental.Live;

        /// <summary>
        /// Refuses anything that is not an outstanding rental, before the caller acts on the
        /// assumption that it is one. Null is not a failure: the pooled write path treats a null
        /// buffer as "rent me one", which adopts nothing of the caller's.
        /// </summary>
        /// <param name="buffer">The buffer the caller declared to be a rental, or null.</param>
        /// <param name="paramName">The name to report, so the refusal points at the argument the
        /// caller actually passed.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void RequireRental(byte[]? buffer, string paramName)
        {
            if (buffer is null || IsRental(buffer)) return;
            throw new ArgumentException(NotARental(buffer), paramName);
        }

        static string NotARental(byte[] buffer) =>
            $"This array (byte[{buffer.Length}]) is not a buffer CerasBufferPool.Rent handed out, so it cannot be used where a pooled buffer is required. " +
            "ArrayPool<byte>.Shared.Return adopts any array whose length happens to be one of its bucket sizes, which is why an array of a plausible size cannot be told from a rental - and a caller's own array returned to the pool would be handed to an unrelated renter, who would then write over a buffer the caller still owns. " +
            "Get the buffer from CerasBufferPool.Rent (or CerasSerializer.RentWriteBuffer for the write path) and return it to CerasBufferPool.Return when finished. " +
            "A caller-owned buffer is a supported input, but only where the API says so: FixedArrayBufferWriter and SerializerBinary.EnsureCapacity both take a 'pooled' flag that must be false for one.";

        static string AlreadyReturned(byte[] buffer) =>
            $"This array (byte[{buffer.Length}]) has already been returned to CerasBufferPool; it is back in the shared pool and may already have been handed to somebody else. " +
            "ArrayPool does not deduplicate, so returning it again would put the same array in the pool twice and two unrelated renters could be given it at once. " +
            "Return each rental exactly once. A pooled buffer is replaced when it grows, and Ceras returns the arrays it grows out of itself, so the array to return is always the one the call read back into its buffer variable.";
    }
}
