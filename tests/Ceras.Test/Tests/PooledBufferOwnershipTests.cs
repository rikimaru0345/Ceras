// Pooled buffer ownership: who may hand a buffer to the pool, and what happens when someone tries.
//
// The write path hands a buffer back to the pool on growth, and the pool's own `Return` cannot
// refuse: `ArrayPool<byte>.Shared.Return` adopts *any* array whose length is one of its bucket
// sizes, so a caller's own byte[4096] is accepted silently and then handed to an unrelated renter,
// who writes over a buffer the caller still believes it owns. A byte[2000] is rejected instead -
// as an ArgumentException from deep inside an otherwise-successful write. Neither is a contract, so
// the contract has to live in CerasBufferPool, which now tracks the arrays it has actually handed
// out (weakly, by the array itself) and refuses everything else at every public path.
//
// These tests pin that refusal, and just as importantly pin that it is not over-eager: a valid
// rental must still survive repeated writes and growth, a caller-owned fixed buffer must still be
// a supported input on the paths that say so, and a refusal must leave the pool exactly as it found
// it (LiveRentals unchanged, no half-rented array, nothing leaked).
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Ceras;

namespace Ceras.Test.PoolOwnership
{
    public class PooledBufferOwnershipTests
    {
        /// <summary>
        /// Somewhere for the refusals to land. A discarded out-parameter, because the calls that
        /// throw never write one - declared once rather than as a local at each refusal site, where
        /// it would be reported as assigned-but-unused.
        /// </summary>
        static int unusedOffset;

        [CerasSerializable]
        public struct Small
        {
            public int A;
            public int B;
        }

        [CerasSerializable]
        public class Blob
        {
            public byte[] Data = new byte[64];
        }

        static CerasFastSerializer Fast() => new CerasFastSerializer(c => c.AllowReflectionFallback = true);

        // ── Return refuses what it did not hand out ────────────────────────────────────────────

        /// <summary>
        /// The two lengths that are the whole reason the guard exists, and they fail differently
        /// without it. 4096 is a bucket size, so the shared pool accepts the array and adopts it -
        /// the silent failure, where the caller's buffer is then handed to a stranger. 2000 is not,
        /// so the same mistake surfaces as an ArgumentException from inside a successful write.
        /// </summary>
        [Theory]
        [InlineData(4096)]
        [InlineData(2000)]
        public void Return_RefusesAnArrayThisPoolNeverHandedOut(int length)
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            var foreign = new byte[length];

            var ex = Assert.Throws<ArgumentException>(() => CerasBufferPool.Return(foreign));

            // Actionable: it says which method produces a buffer that *can* be returned, rather
            // than leaving the reader to work out that this pool is not ArrayPool.
            Assert.Contains("CerasBufferPool.Rent", ex.Message, StringComparison.Ordinal);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// The shared pool hands <c>Array.Empty</c> itself out for a zero-length request, so it is an
        /// array this pool can be holding and an array a caller can have. It must be refused like
        /// any other foreign array: accepting it would mean two different callers' "rentals" were
        /// the same array, and the first one to return it would be returning the process-wide empty
        /// singleton.
        /// </summary>
        [Fact]
        public void Return_RefusesTheSharedEmptyArray()
        {
            int liveBefore = CerasBufferPool.LiveRentals;

            var ex = Assert.Throws<ArgumentException>(() => CerasBufferPool.Return(Array.Empty<byte>()));

            Assert.Contains("CerasBufferPool.Rent", ex.Message, StringComparison.Ordinal);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        [Fact]
        public void Return_RefusesNull()
        {
            int liveBefore = CerasBufferPool.LiveRentals;

            Assert.Throws<ArgumentNullException>(() => CerasBufferPool.Return(null!));

            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// A rental handed out by the *shared* pool, which is a real pool array and still not a
        /// rental of <em>this</em> pool. It is the case that cannot be told from a rental by
        /// looking at it, and the case a user reaches for after reading about <c>ArrayPool</c>
        /// instead of <c>CerasBufferPool</c>.
        /// <para>
        /// The refusal's type is not pinned, and deliberately so. This pool rents from
        /// <c>ArrayPool.Shared</c> and gives arrays back to it, so a raw rental here is quite
        /// possibly an array this pool handed out and returned earlier - in which case "already
        /// returned" is the exact and more useful answer, not a wrong one. What holds either way is
        /// the property that matters: it is refused, and refusing it changes no counts. (The
        /// never-rented and double-return cases are pinned separately, on arrays whose history is
        /// not in question.)
        /// </para>
        /// </summary>
        [Fact]
        public void Return_RefusesARawArrayPoolRental()
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            var raw = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                var thrown = Record.Exception(() => CerasBufferPool.Return(raw));

                Assert.NotNull(thrown);
                Assert.True(thrown is ArgumentException || thrown is InvalidOperationException,
                    $"expected a refusal, got {thrown.GetType().Name}: {thrown.Message}");
                Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
            }
            finally { ArrayPool<byte>.Shared.Return(raw); }
        }

        /// <summary>
        /// Returning twice is a different bug from never having rented it - a call that grew its
        /// buffer and then returned the array it started with - and it is the one that puts the
        /// same array in the pool twice, so two unrelated renters can be handed it at once. It gets
        /// its own exception type for that reason, and the message says so.
        /// </summary>
        [Fact]
        public void ReturningTheSameRentalTwice_IsRefused()
        {
            var buffer = CerasBufferPool.Rent(4096);
            CerasBufferPool.Return(buffer);

            int liveBefore = CerasBufferPool.LiveRentals;
            var ex = Assert.Throws<InvalidOperationException>(() => CerasBufferPool.Return(buffer));

            Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
            // A refusal must not decrement the count: the first return already balanced this lease.
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// Which pool the array came from is a runtime fact, not a compile-time one, so the two
        /// refusals have to be distinguishable at runtime - and they are, by type. Both arrays here
        /// are <c>new</c>, so neither has any history that could blur the answer.
        /// </summary>
        [Fact]
        public void ANeverRentedArrayAndADoubleReturn_AreDistinguishable()
        {
            var never = new byte[4096];
            var doubled = CerasBufferPool.Rent(4096);
            CerasBufferPool.Return(doubled);

            Assert.Throws<ArgumentException>(() => CerasBufferPool.Return(never));
            Assert.Throws<InvalidOperationException>(() => CerasBufferPool.Return(doubled));
        }

        // ── The pooled Serialize entry refuses a foreign buffer, even when it fits ─────────────

        /// <summary>
        /// The defect the entry check closes, and the reason it is at the entry rather than in the
        /// growth path. A caller-owned array big enough for the message is accepted today, the
        /// write succeeds, and the mistake is not visible - until the message that happens to
        /// outgrow it, if one ever does. Checking at the door names the caller at the call they
        /// got wrong.
        /// </summary>
        [Theory]
        [InlineData(4096)]
        [InlineData(2000)]
        [InlineData(0)]
        public void ThePooledSerializeEntry_RefusesAnArrayThisPoolNeverHandedOut(int length)
        {
            var ceras = Fast();
            byte[] foreign = length == 0 ? Array.Empty<byte>() : new byte[length];
            byte[] buffer = foreign;
            int liveBefore = CerasBufferPool.LiveRentals;

            var ex = Assert.Throws<ArgumentException>(
                () => ceras.Serialize(new Small { A = 1, B = 2 }, ref buffer, ref unusedOffset));

            Assert.Contains("CerasBufferPool.Rent", ex.Message, StringComparison.Ordinal);
            // The two-argument overload is the same entry, and must refuse just the same.
            byte[] buffer2 = foreign;
            Assert.Throws<ArgumentException>(() => ceras.Serialize(new Small { A = 1, B = 2 }, ref buffer2));
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// A null buffer is not a foreign buffer. The pooled path treats it as "rent me one", which
        /// adopts nothing of the caller's, so the check must not refuse it - and the array the
        /// caller is handed back has to be a real rental it can return.
        /// </summary>
        [Fact]
        public void ThePooledSerializeEntry_RentsForANullBuffer()
        {
            var ceras = Fast();
            byte[] buffer = null!;
            int offset = 0;

            int written = ceras.Serialize(new Small { A = 7, B = 9 }, ref buffer, ref offset);

            Assert.Equal(8, written);
            Assert.NotNull(buffer);
            // It is a rental of this pool, so it is returnable - and returning it must balance.
            int liveBefore = CerasBufferPool.LiveRentals;
            CerasBufferPool.Return(buffer);
            Assert.Equal(liveBefore - 1, CerasBufferPool.LiveRentals);
        }

        // ── The public growth paths refuse a mislabelled buffer, and leave the caller's alone ──

        /// <summary>
        /// The two public entry points that can be told to treat a buffer as a rental. A caller who
        /// says "this is pooled" about their own array is asking for the pool to adopt it, and the
        /// answer is no - with the buffer untouched and nothing rented, because the check runs
        /// before the larger array is taken and before anything is copied.
        /// </summary>
        [Fact]
        public void TheDirectGrowthPaths_RefuseToAdoptACallerOwnedArray()
        {
            int liveBefore = CerasBufferPool.LiveRentals;

            var foreign = new byte[4096];
            foreign[0] = 0xAB;                       // a sentinel, to prove the array is untouched
            var writer = new CerasSerializer.FixedArrayBufferWriter(foreign, 0, bufferIsPooled: true);
            var ex = Assert.Throws<ArgumentException>(() => writer.GetSpan(8192));

            Assert.Contains("CerasBufferPool.Rent", ex.Message, StringComparison.Ordinal);

            // EnsureCapacity is the same path without the writer around it.
            byte[] other = new byte[4096];
            Assert.Throws<ArgumentException>(
                () => SerializerBinary.EnsureCapacity(ref other, 0, 8192, replacedBufferIsPooled: true));

            Assert.Equal(0xAB, foreign[0]);
            Assert.Equal(0, CerasBufferPool.LiveRentals - liveBefore);
        }

        /// <summary>
        /// The flip side, and the reason the flag exists at all: the very same call with the flag
        /// left at its default is the supported way to write into a fixed caller-owned array, and it
        /// must keep working. Growth rents from the pool, copies, and leaves the caller's array
        /// alone - no adoption, no double return.
        /// </summary>
        [Fact]
        public void ACallerOwnedFixedBuffer_StillGrowsThroughTheDefaultFlag()
        {
            int liveBefore = CerasBufferPool.LiveRentals;

            var foreign = new byte[8];
            foreign[0] = 0xAB;
            byte[] buffer = foreign;

            // Default replacedBufferIsPooled: false - the caller-owned case. Capacity only, so no
            // offset to account for.
            SerializerBinary.EnsureCapacity(ref buffer, 16, 8);
            Assert.True(buffer.Length >= 24);

            // The caller's array was copied from, not adopted.
            Assert.Equal(0xAB, buffer[0]);
            Assert.Equal(0xAB, foreign[0]);

            // ...and returning it is refused, which is the observable proof it was never adopted.
            Assert.Throws<ArgumentException>(() => CerasBufferPool.Return(foreign));

            // The rented replacement is a genuine rental; one Rent, one Return, balanced.
            CerasBufferPool.Return(buffer);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// The same thing one layer up, through the public writer: a caller-owned buffer with the
        /// default (non-pooled) flag is a supported input, and a payload too big for it grows in
        /// place. This is the path the migration interpreter and the schema baker use, so if the
        /// guard were over-eager it would show up here first.
        /// </summary>
        [Fact]
        public void ACallerOwnedBufferThroughThePublicWriter_GrowsWithoutBeingAdopted()
        {
            int liveBefore = CerasBufferPool.LiveRentals;

            var foreign = new byte[16];
            byte[] buffer = foreign;

            var writer = new CerasSerializer.FixedArrayBufferWriter(buffer, 0, bufferIsPooled: false);
            for (int i = 0; i < 64; i++)
            {
                var span = writer.GetSpan(64);
                span.Clear();
                writer.Advance(64);
            }

            Assert.True(writer.Buffer.Length > foreign.Length);
            // The caller's array is not the writer's, and is not a rental.
            Assert.NotSame(foreign, writer.Buffer);
            Assert.Throws<ArgumentException>(() => CerasBufferPool.Return(foreign));

            CerasBufferPool.Return(writer.Buffer);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        // ── A valid rental keeps working ───────────────────────────────────────────────────────

        /// <summary>
        /// The counter-case for every refusal above: the documented loop still works, allocates
        /// nothing per message, and ends balanced. If the guard cost any of that it would be the
        /// wrong trade.
        /// </summary>
        [Fact]
        public void AValidRental_StillSupportsAnAllocationFreeMessageLoop()
        {
            var ceras = Fast();
            byte[] buffer = CerasBufferPool.Rent(4096);
            try
            {
                int warm = 0;
                ceras.Serialize(new Small { A = 1, B = 2 }, ref buffer, ref warm);
                warm = 0;

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 64; i++)
                {
                    warm = 0;
                    ceras.Serialize(new Small { A = i, B = i * 2 }, ref buffer, ref warm);
                }
                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            finally { CerasBufferPool.Return(buffer); }
        }

        /// <summary>
        /// Reuse across growth: each replacement is returned, so a long run of increasingly large
        /// messages leaves the outstanding count where it started rather than climbing by one per
        /// growth.
        /// </summary>
        [Fact]
        public void RepeatedGrowth_OnOneValidRental_StaysBalanced()
        {
            var ceras = Fast();
            byte[] buffer = CerasBufferPool.Rent(64);
            int liveBefore = CerasBufferPool.LiveRentals;
            try
            {
                for (int size = 64; size <= 64 * 1024; size *= 2)
                {
                    int offset = 0;
                    ceras.Serialize(new Blob { Data = new byte[size] }, ref buffer, ref offset);
                    Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
                }
                CerasBufferPool.Return(buffer);
                Assert.Equal(liveBefore - 1, CerasBufferPool.LiveRentals);
            }
            finally { if (CerasBufferPool.IsRental(buffer)) CerasBufferPool.Return(buffer); }
        }

        /// <summary>
        /// A rental is still a rental after it has been written to, and re-renting an array that
        /// came back from the pool is a fresh lease rather than a continuation of the old one -
        /// otherwise the second Return would be refused as a double return of the first.
        /// </summary>
        [Fact]
        public void AnArrayRentedTwiceInSequence_IsTwoSeparateLeases()
        {
            int liveBefore = CerasBufferPool.LiveRentals;

            var first = CerasBufferPool.Rent(4096);
            CerasBufferPool.Return(first);

            // The pool very likely hands the same instance straight back, which is the interesting
            // case: the registry has to treat the re-rent as a new lease over the same array.
            var second = CerasBufferPool.Rent(4096);
            CerasBufferPool.Return(second);

            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// A zero-length request is answered from the smallest real bucket rather than passed
        /// through to the shared pool, which would return the process-wide <c>Array.Empty</c>
        /// singleton. Every caller would then hold the same array, and the first one to return it
        /// would be handing the process's empty array to the pool.
        /// </summary>
        [Fact]
        public void RentingZeroBytes_GivesAnArrayThatCanActuallyBeReturned()
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            var buffer = CerasBufferPool.Rent(0);

            Assert.NotSame(Array.Empty<byte>(), buffer);
            Assert.True(buffer.Length > 0);

            CerasBufferPool.Return(buffer);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        [Fact]
        public void RentingANegativeSize_IsRefused()
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            Assert.Throws<ArgumentOutOfRangeException>(() => CerasBufferPool.Rent(-1));
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        // ── Concurrency ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The claim has to be atomic. Two threads returning the same rental at the same time must
        /// produce exactly one pool return between them: a check-then-act pair would let both see
        /// "still live" and both hand the same array in, which is the failure the whole registry
        /// exists to make impossible.
        /// </summary>
        [Fact]
        public async Task TwoThreadsReturningTheSameRental_ProduceExactlyOnePoolReturn()
        {
            const int Attempts = 200;
            for (int i = 0; i < Attempts; i++)
            {
                int liveBefore = CerasBufferPool.LiveRentals;
                var buffer = CerasBufferPool.Rent(4096);

                int accepted = 0, refused = 0;
                using var gate = new ManualResetEventSlim(false);

                Task a = Task.Run(() => { gate.Wait(); if (TryReturn(buffer, ref accepted, ref refused)) { } });
                Task b = Task.Run(() => { gate.Wait(); if (TryReturn(buffer, ref accepted, ref refused)) { } });

                gate.Set();
                await Task.WhenAll(a, b);

                Assert.Equal(1, accepted);
                Assert.Equal(1, refused);
                Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
            }

            static bool TryReturn(byte[] buffer, ref int accepted, ref int refused)
            {
                try { CerasBufferPool.Return(buffer); Interlocked.Increment(ref accepted); return true; }
                catch (InvalidOperationException) { Interlocked.Increment(ref refused); return false; }
            }
        }

        /// <summary>
        /// Rent and return are independent per array, so concurrent tenants each end balanced even
        /// while every one of their arrays passes through the registry.
        /// </summary>
        [Fact]
        public async Task ConcurrentRentAndReturn_LeavesTheCountBalanced()
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            const int Threads = 8, PerThread = 500;

            var tasks = new Task[Threads];
            for (int t = 0; t < Threads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (int i = 0; i < PerThread; i++)
                    {
                        var buffer = CerasBufferPool.Rent(4096);
                        Assert.True(CerasBufferPool.IsRental(buffer));
                        CerasBufferPool.Return(buffer);
                    }
                });
            }
            await Task.WhenAll(tasks);

            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }
    }
}
