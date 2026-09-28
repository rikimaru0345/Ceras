// A robust `Save` that fails part-way through the payload must leave the buffer pool exactly as it found
// it — one array rented, one array returned, and every array it grew through on the way back in.
//
// `Save` writes its payload straight into the rented file buffer, through a `FixedArrayBufferWriter`, and
// growth is Ceras's own: `SerializerBinary.ExpandBuffer` rents a bigger array, copies, and returns the
// replaced one to the pool, because it was told (per growth point) that the buffer is a rental. That
// makes the invariant "the caller's local names the array the save currently owns" load-bearing, and the
// raw pooled API states it in so many words: `CerasSerializer.Serialize(obj, ref buffer, ref offset)`
// reads `buffer = writer.Buffer` inside its `finally` for exactly this reason. The robust path read it
// back *after* the try instead, so a graph that both outgrew the buffer and then failed — the shape of
// every refused save, since the refusal comes from a value part-way through the graph — skipped the
// read-back. `buffer` then still named the pre-growth array, which ExpandBuffer had already returned, so
// the outer `finally` returned the same array a second time. `ArrayPool` does not deduplicate, so from
// then on two unrelated renters could be handed it and write over each other's output; and the array the
// writer had grown to was leaked, because the released writer was the only reference to it.
//
// Note what the rental *count* does and does not show. `CerasBufferPool.LiveRentals` was unchanged
// either way — one rental for the grown array, two returns of the original — so a balanced count is
// exactly what a double return looks like, and cannot be the property under test. The property is what
// the pool *contains*, and these tests look at that directly.
using System;
using System.Buffers;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Formatters;
using Ceras.Generated;

namespace Ceras.Test.FailedSavePooling
{
    /// <summary>
    /// A blob big enough to outgrow the save's rented buffer, followed by a member whose *value* Ceras
    /// refuses. Members are written in the schema's order, which is the member type's name and then the
    /// member's — so <c>System.Byte[]</c> is written before <c>System.Int32</c> before
    /// <c>System.Type</c>, and the buffer has therefore already grown by the time the last member is
    /// refused. <c>TheBlobIsWrittenBeforeTheValueThatThrows</c> pins that premise, so this cannot
    /// quietly stop exercising the growth path if the order ever changes.
    /// </summary>
    [CerasContext("FailedSavePooling")]
    [CerasSerializable]
    public class FspHolder
    {
        public byte[] Blob;
        public int After;
        public Type Refused;      // a banned type: refused by name, mid-graph
    }

    public class FailedSaveBufferPoolTests : TestBase
    {
        const string Ctx = "FailedSavePooling";

        /// <summary>Big enough that the save of it takes the pool's 512 KB bucket rather than the default
        /// 4 KB one. That matters twice over: the failing save that follows starts from a buffer of
        /// exactly that bucket (a save remembers its own length, and the pool rounds it up), and a bucket
        /// nothing else in the suite rents keeps the probe below from meeting somebody else's arrays.</summary>
        const int ControlBlobLength = 300_000;

        /// <summary>Larger than the array the failing save starts from, so the payload cannot fit and the
        /// write has to grow before it can reach the member that throws.</summary>
        const int OversizedBlobLength = 1 << 20;

        static CerasRobustSerializer New() => new CerasRobustSerializer(c => c.UseGeneratedFormatters(Ctx));

        // ── The defect ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The whole finding: a save that grew its buffer and then failed left one array in the pool
        /// twice, so the next two rentals of that size could be handed the same array — two unrelated
        /// saves writing into one buffer, with no error anywhere.
        /// </summary>
        [Fact]
        public void AFailedSaveThatOutgrewItsRentedBufferLeavesEachPooledArrayInThePoolExactlyOnce()
        {
            var s = New();

            // A successful save first, for two reasons: it seals the manifest (so the model below is the
            // one actually written), and it leaves `_lastSaveLength` at this save's length, so the save
            // that follows starts from a buffer sized for it and has to grow to hold the bigger graph.
            var control = s.Save(new FspHolder { Blob = new byte[ControlBlobLength], After = 7, Refused = typeof(int) }, 1);
            int startLength = PoolBucketLength(control.Length);
            Assert.True(OversizedBlobLength > startLength,
                $"the blob ({OversizedBlobLength} bytes) has to be larger than the array the save starts from ({startLength}), " +
                "or this test would not be exercising the growth it is about");

            int liveBefore = CerasBufferPool.LiveRentals;
            Assert.False(PoolHandsOutTheSameArrayTwice(startLength), "the pool already held an array twice before this test ran");

            var thrown = Record.Exception(() => s.Save(GrowingAndFailing(), 1));

            // The refusal is the point of the graph, not an accident of it: a banned type value, refused
            // by name while the blob has already been written.
            var banned = Assert.IsType<BannedTypeException>(Innermost(thrown));
            Assert.Contains("Action", banned.Message);

            // The rental count is unchanged — one array rented per array returned — but that is also what
            // a double return looks like, which is exactly why the pool's contents are checked next.
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);

            // The property itself: every array this save touched is in the pool at most once. (With the
            // read-back left after the try, this saw the pre-growth array twice and failed here.)
            Assert.False(PoolHandsOutTheSameArrayTwice(startLength),
                "the save handed the pool an array it already had, so the pool can now hand the same buffer to two renters at once");
        }

        // ── The controls ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The same graph, saving successfully: growth and a pool return, and every array still in the
        /// pool exactly once. Without this the refusal above could be satisfied by a probe that always
        /// answers "no duplicate" — which is the failure mode a pool assertion really has.
        /// </summary>
        [Fact]
        public void ASuccessfulSaveThatOutgrewItsRentedBufferAlsoLeavesEachPooledArrayExactlyOnce()
        {
            var s = New();
            var control = s.Save(new FspHolder { Blob = new byte[ControlBlobLength], After = 7, Refused = typeof(int) }, 1);
            int startLength = PoolBucketLength(control.Length);

            var bytes = s.Save(new FspHolder { Blob = new byte[OversizedBlobLength], After = 7, Refused = typeof(int) }, 1);

            Assert.True(bytes.Length > startLength);   // it really did outgrow the array it started from
            Assert.False(PoolHandsOutTheSameArrayTwice(startLength));
        }

        /// <summary>
        /// That the probe can see the thing it is looking for. The double return is done by hand here —
        /// the pool does not deduplicate — so this pins the detector itself rather than trusting it: if
        /// this stops passing, the refusal above has stopped proving anything.
        /// </summary>
        [Fact]
        public void ThePoolProbeSeesAnArrayThePoolWasHandedTwice()
        {
            var a = ArrayPool<byte>.Shared.Rent(4096);
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(a);          // the defect, in isolation

            // No teardown: the probe takes both copies back out and puts one of them (one distinct array
            // per rental) so the pool is left without a duplicate, which is the whole point of the
            // assertion below having been worth making.
            Assert.True(PoolHandsOutTheSameArrayTwice(4096), "the probe cannot see a double return, so it cannot see one being fixed");
        }

        /// <summary>
        /// A refused save leaves the serializer usable — the next save of the same graph is a normal save
        /// and loads back — and the refusal is not what this pins. It is here so the growth above cannot be
        /// confused with a serializer that gave up on the second save.
        /// </summary>
        [Fact]
        public void ARefusedSaveLeavesTheSerializerAbleToSaveTheSameGraph()
        {
            var s = New();
            s.Save(new FspHolder { Blob = new byte[ControlBlobLength], After = 7, Refused = typeof(int) }, 1);

            Assert.NotNull(Record.Exception(() => s.Save(GrowingAndFailing(), 1)));

            var good = new FspHolder { Blob = new byte[OversizedBlobLength], After = 7, Refused = typeof(int) };
            var loaded = s.Load<FspHolder>(s.Save(good, 1), 1);
            Assert.Equal(good.Blob.Length, loaded.Blob.Length);
            Assert.Equal(typeof(int), loaded.Refused);
        }

        /// <summary>
        /// That the blob really is written before the member that throws. The order is the schema's
        /// (member type name, then member name — see the generator), not the declaration's, so it is
        /// asserted rather than assumed: were it ever the other way round, the test above would stop
        /// exercising a grown buffer and would still pass.
        /// </summary>
        [Fact]
        public void TheBlobIsWrittenBeforeTheValueThatThrows()
        {
            var s = New();
            s.Save(new FspHolder { Blob = new byte[16], After = 7, Refused = typeof(int) }, 1);

            var dump = s.DumpSchema();
            int blob = dump.IndexOf(nameof(FspHolder.Blob), StringComparison.Ordinal);
            int refused = dump.IndexOf(nameof(FspHolder.Refused), StringComparison.Ordinal);

            Assert.True(blob >= 0 && refused > blob, $"expected {nameof(FspHolder.Blob)} to be written before {nameof(FspHolder.Refused)} in:\n{dump}");
        }

        // ── The graph ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Larger than the array the save starts from (so the write grows) and carrying a value Ceras
        /// refuses (so the save fails after the growth). A delegate is banned by shape, and a
        /// <see cref="Type"/> slot is refused by <c>RobustTypeFormatter</c> by name.
        /// </summary>
        static FspHolder GrowingAndFailing() => new FspHolder
        {
            Blob = new byte[OversizedBlobLength],
            After = 7,
            Refused = typeof(Action),
        };

        // ── Looking at the pool ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The length of the array the pool hands out for a minimum size, without keeping it. The pool
        /// returns whole buckets, so this is the size the save under test starts from — read rather than
        /// assumed, so the assertion that the blob outgrows it stays true if the schema ever changes
        /// size.
        /// </summary>
        static int PoolBucketLength(int minimumSize)
        {
            var probe = ArrayPool<byte>.Shared.Rent(minimumSize);
            try { return probe.Length; }
            finally { ArrayPool<byte>.Shared.Return(probe); }
        }

        /// <summary>
        /// Whether the pool will hand the same array out twice. The shared pool is LIFO per thread, so a
        /// double return is at the top of what it will hand out next; a batch wide enough to reach it is
        /// therefore enough to see one, and an array the pool holds once can only come out once.
        /// <para>
        /// Each <em>distinct</em> array is returned exactly once at the end, so the probe never leaves a
        /// duplicate behind: a batch that happens to contain the same array twice still puts one copy
        /// back. That matters because a duplicate found here has just been taken out of the pool along
        /// with its twin, so a test that fails on one is not poisoning the next one.
        /// <para>
        /// Nothing else runs while this does: the test assembly disables xUnit parallelization
        /// (<c>TestConfig.cs</c>), so a duplicate found here is this test's, not a concurrent tenant's.
        /// </para>
        /// </summary>
        static bool PoolHandsOutTheSameArrayTwice(int minimumSize, int probes = 8)
        {
            var rented = new byte[probes][];
            for (int i = 0; i < probes; i++) rented[i] = ArrayPool<byte>.Shared.Rent(minimumSize);

            try
            {
                for (int i = 0; i < probes; i++)
                    for (int j = i + 1; j < probes; j++)
                        if (ReferenceEquals(rented[i], rented[j])) return true;
                return false;
            }
            finally
            {
                var returned = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
                foreach (var array in rented)
                    if (array != null && returned.Add(array))
                        ArrayPool<byte>.Shared.Return(array);
            }
        }

        /// <summary>The failure a caller has to act on: a refusal raised while writing a member of the
        /// root object is wrapped by the enclosing object's error context.</summary>
        static Exception Innermost(Exception e)
        {
            while (e?.InnerException != null) e = e.InnerException;
            return e!;
        }
    }
}
