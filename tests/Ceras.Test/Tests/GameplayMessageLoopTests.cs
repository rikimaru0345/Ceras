// A simple, realistic gameplay message loop, and the one property worth asserting about it: in steady
// state it allocates nothing.
//
// The shape is the one a game actually runs. A client samples input every tick and sends it; a server
// receives it, checks it, moves the player, and sends authoritative state back; the client receives
// that and applies it. Two messages, both directions, once per tick, forever.
//
// What makes this test worth having, and not a restatement of ReusedWriteWriterTests and
// PooledBufferOwnershipTests (which already show that a pooled write of a small struct allocates
// nothing), is everything *around* the serializer:
//
//   * both endpoints really deserialize, and really compare what arrived against what was sent, with a
//     value that changes every exchange - a loop that encoded a constant and discarded the decode would
//     report the same zero;
//   * the bytes are moved by a real transport with length framing rather than passed between two
//     locals, so the measurement includes moving them and the framing is on the wire rather than
//     inferred;
//   * the encode buffers are pooled rentals and the transport's buffers are not, which is the
//     separation the pool's ownership guard exists to keep and which a harness that blurred it would
//     turn into a silent use-after-return;
//   * the second message case is a reference type carrying a string, and its allocations are
//     *reported*, not asserted to be zero. A string read out of bytes is a new string; pretending
//     otherwise would make this test a liar.
//
// The numbers live in samples/Ceras.Benchmarks (`--network-loop`), over the same shared transport; see
// `GameLoop` below for what is and is not shared. This file is the fast gate, not the measurement:
// <=128 warm-up iterations, <=64 measured exchanges, no sleeps, and no assertion on elapsed time
// anywhere. A timing threshold would be the fragile part; the byte count is not.
using System;
using System.Buffers;
using Ceras;
using Ceras.GameplayLoop;
using Ceras.Generated;
using Xunit;

namespace Ceras.Test.NetLoop
{
    // The two message shapes a game exchanges every tick, in this project's own "Tests" context.
    // They are declared here rather than shared with samples/Ceras.Benchmarks because a Ceras
    // serialization context is a per-compilation artifact: two generator-enabled assemblies cannot both
    // emit Ceras.Generated.CerasConfigExtensions, so a message type compiled into both could only be
    // used by one of them. The transport and the measurement helper carry no context and *are* shared
    // (shared/GameplayLoop).

    /// <summary>
    /// A client's input sample for one tick. Fixed-size by construction: 24 bytes encoded, every member
    /// a fixed-width primitive, no reference members, and no null flag because a struct has no null to
    /// flag. Nothing on the wire has a length that has to be discovered, which is what makes this the
    /// case the zero-allocation claim is about.
    /// </summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public struct PlayerInput
    {
        public int Sequence;
        public int PlayerId;
        public int Buttons;
        public float PosX;
        public float PosY;
        public float PosZ;
    }

    /// <summary>
    /// The authoritative answer to one <see cref="PlayerInput"/>: which input the server applied, where
    /// it thinks the player is, and on which tick. Carries the client's sequence back, without which the
    /// loop could be checked for speed but not for correctness.
    /// </summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public struct ServerState
    {
        public int Sequence;
        public int LastAppliedInput;
        public int ServerTick;
        public float PosX;
        public float PosY;
        public float PosZ;
        public bool Accepted;
    }

    /// <summary>
    /// A reference-typed message with a string, measured honestly rather than optimistically: see
    /// <see cref="GameplayMessageLoopTests.TheStringCase_AllocatesAndIsReportedAsSuch"/>.
    /// </summary>
    [CerasContext("Tests")]
    [CerasSerializable]
    public class ChatLine
    {
        public int Sequence;
        public string Text;
    }

    public class GameplayMessageLoopTests
    {
        /// <summary>
        /// Warm-up iterations before the counter starts. Every generated formatter, per-operation cache
        /// and spare writer is built on the first few calls; 128 is far more than that and still under a
        /// millisecond.
        /// </summary>
        const int WarmupIterations = 128;

        /// <summary>
        /// Measured exchanges. Small on purpose - the claim is "zero bytes", which 64 exchanges can
        /// establish as well as a million, and a test that ran a million of them would be a test nobody
        /// runs before committing.
        /// </summary>
        const int MeasuredExchanges = 64;

        // ── the property under test ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The whole loop, in steady state, allocates nothing on the thread that runs it.
        /// <para>
        /// This is the roadmap's "sustained traffic and steady-state allocations" line in the only form
        /// that can be checked cheaply enough to live in the suite: after the first exchanges have built
        /// the formatters and the per-operation caches, a further 64 complete client/server exchanges -
        /// two encodes, two frames, two decodes and every comparison in between - move the allocation
        /// counter by zero bytes.
        /// </para>
        /// <para>
        /// Asserting zero is only meaningful because the warm-up is real and the loop is verified
        /// afterwards. If a decode silently stopped happening, or a comparison stopped being made, this
        /// test would still pass; <see cref="AnExchangeActuallyMovesAndChecksTheBytes"/> is what keeps the
        /// zero honest.
        /// </para>
        /// <para>
        /// 128 warm-up iterations is short enough that the loop is still running unoptimized tier-0
        /// code when the counter starts, and that is the safe direction: tier-0 code allocates at least
        /// as much as tier-1 code, so a loop that is allocation-free here is allocation-free once it is
        /// optimised. It is also why this test catches an injected allocation reliably. Injecting
        /// <c>var scratch = new byte[inputBytes];</c> into <see cref="GameLoop.Exchange"/> fails this test
        /// and <see cref="TheZeroIsAClaimAboutThisThread"/>; so does swapping the pooled write for the
        /// convenience <c>Serialize(obj)</c> overload, and so does replacing the transport's preallocated
        /// lane with a <c>MemoryStream</c> per frame. (An injected allocation that nothing observes - a dead
        /// local - is reported by this test but then removed by the optimiser at tier-1, which is a
        /// reminder that the interesting bug is the one the JIT cannot see.)
        /// </para>
        /// </summary>
        [Fact]
        public void ASteadyStateExchange_AllocatesNothing()
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            using (var loop = GameLoop.Create())
            {
                int before = loop.Sequence;
                long allocated = LoopMeasurementRunner.MeasureBytes(loop.Exchange, WarmupIterations, MeasuredExchanges);

                Assert.Equal(0, allocated);

                // The exchanges really ran, and the values really were read. Both are read here, so a
                // loop that had quietly stopped doing the work could not satisfy the assertion above.
                Assert.Equal(0, loop.Failures);
                Assert.Equal(WarmupIterations + MeasuredExchanges, loop.Sequence - before);
                Assert.NotEqual(0, loop.Checksum);
            }
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// The same property restated as what it is: a claim about one thread, in one process.
        /// <para>
        /// It is worth having a test whose name says so. A reader who sees "0 B/op" in the benchmark
        /// table and a passing test here should not have to guess whether it also holds across threads,
        /// across a socket, or across processes. It does not, and nothing here measures those: the
        /// counter is <c>GC.GetAllocatedBytesForCurrentThread</c>, a real socket copies through kernel
        /// buffers this harness does not have, and the other endpoint of a real connection is a different
        /// process with its own serializers.
        /// </para>
        /// </summary>
        [Fact]
        public void TheZeroIsAClaimAboutThisThread()
        {
            using (var loop = GameLoop.Create())
            {
                int before = loop.Sequence;
                long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < MeasuredExchanges; i++) loop.Exchange();
                long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

                Assert.Equal(0, bytesAfter - bytesBefore);
                Assert.Equal(MeasuredExchanges, loop.Sequence - before);
                Assert.Equal(0, loop.Failures);
            }
        }

        // ── the loop is real, not a fast no-op ──────────────────────────────────────────────────

        /// <summary>
        /// The bytes on the wire are framed, they change every exchange, and both ends read them back to
        /// the values that were sent.
        /// <para>
        /// Comparing the encoded bytes of two different exchanges is the strong form: if the encode were
        /// constant, the decode skipped, or the transport replayed a stale frame, two consecutive
        /// exchanges would produce identical bytes and this fails. The framing is pinned too - the frame
        /// size the transport wrote is exactly the payload plus its length prefix, and the loop fails
        /// itself if the decoder did not consume exactly the bytes the prefix declared.
        /// </para>
        /// </summary>
        [Fact]
        public void AnExchangeActuallyMovesAndChecksTheBytes()
        {
            using (var loop = GameLoop.Create())
            {
                loop.Exchange();
                // The snapshot is taken by the test, not by the loop: an allocation here would be
                // charged to the test, which is exactly where it belongs.
                byte[] first = loop.LastClientInputBytes();
                int firstFrame = loop.LastInputFrameBytes;
                int firstPayload = loop.LastInputPayloadBytes;

                loop.Exchange();
                byte[] second = loop.LastClientInputBytes();

                // Different sequence numbers, so different bytes: the loop is not encoding a constant.
                Assert.NotEqual(first, second);
                Assert.Equal(firstPayload, second.Length);

                // Framing: prefix plus payload, both directions, every time.
                Assert.Equal(firstPayload + FakeDuplexTransport.LengthPrefixSize, firstFrame);
                Assert.Equal(loop.EncodedInputLength, firstPayload);
                Assert.Equal(loop.EncodedInputLength, second.Length);

                // Both exchanges verified themselves: the loop compares every decoded field against
                // what it sent, so a decode that returned the wrong value cannot leave Failures at 0.
                Assert.Equal(0, loop.Failures);
                Assert.Equal(0, loop.UnconsumedBytes);
            }
        }

        /// <summary>
        /// The two ends are two serializer instances, and they are independently usable.
        /// <para>
        /// Not a formality. A configured <see cref="CerasSerializer"/> owns mutable per-operation state -
        /// its formatter table, object cache, type cache and spare writers - so one instance shared by
        /// two endpoints is two writers into the same state, and it is not what a real client or server
        /// does either. The second half is the part that earns the test: the same message encoded through
        /// each end's own encoder must produce identical bytes, which is only a meaningful statement if
        /// the two are separately configured for the same contract and neither has been used in a way
        /// that leaves state behind for the other.
        /// </para>
        /// </summary>
        [Fact]
        public void BothEndsUseTheirOwnSerializerInstance()
        {
            using (var loop = GameLoop.Create())
            {
                Assert.NotSame(loop.Client, loop.Server);
                Assert.NotSame(loop.ClientWriteBuffer, loop.ServerWriteBuffer);

                var input = new PlayerInput { Sequence = 11, PlayerId = GameLoop.PlayerId, Buttons = 3, PosX = 1, PosY = 2, PosZ = 3 };
                Assert.Equal(loop.EncodeOnClient(input), loop.EncodeOnServer(input));

                // The server decodes what the client encoded, and the client decodes what the server
                // encoded, with the loop checking both. If the two were aliased this is where it breaks.
                loop.Exchange();
                loop.Exchange();
                Assert.Equal(0, loop.Failures);
            }
        }

        // ── buffer ownership, growth, and the pool guard ─────────────────────────────────────────

        /// <summary>
        /// The loop's own buffers and the pool's rules: the two encode buffers are live rentals, the
        /// transport's buffers are not, and the transport's buffers are <em>refused</em> by the pooled
        /// write path.
        /// <para>
        /// That last part is the point of the section. A loop that handed a transport buffer to
        /// <c>Serialize(obj, ref buffer, ref offset)</c> would be declaring it a rental; if a message ever
        /// outgrew it, Ceras would return it to the pool and an unrelated tenant would be writing into a
        /// buffer this loop still believed it owned. The refusal is checked here, so the mistake cannot be
        /// made silently, and the checks after it show the refusal changed nothing: the lane is the same
        /// array and still not a rental.
        /// </para>
        /// </summary>
        [Fact]
        public void PooledWriteBuffersAndTransportBuffersStayDistinct()
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            using (var loop = GameLoop.Create())
            {
                // Two rentals, one per endpoint, and nothing else outstanding.
                Assert.Equal(liveBefore + 2, CerasBufferPool.LiveRentals);
                Assert.True(CerasBufferPool.IsRental(loop.ClientWriteBuffer));
                Assert.True(CerasBufferPool.IsRental(loop.ServerWriteBuffer));
                Assert.False(CerasBufferPool.IsRental(loop.Transport.ToServerBuffer));
                Assert.False(CerasBufferPool.IsRental(loop.Transport.ToClientBuffer));

                var lane = loop.Transport.ToServerBuffer;
                byte[] buffer = lane;
                int offset = 0;
                var ex = Assert.Throws<ArgumentException>(
                    () => loop.Client.Serialize(new PlayerInput(), ref buffer, ref offset));

                // Actionable: the refusal says where a buffer that *can* be returned comes from.
                Assert.Contains("CerasBufferPool.Rent", ex.Message, StringComparison.Ordinal);
                Assert.Same(lane, loop.Transport.ToServerBuffer);
                Assert.False(CerasBufferPool.IsRental(loop.Transport.ToServerBuffer));
                Assert.Equal(liveBefore + 2, CerasBufferPool.LiveRentals);
            }
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
        }

        /// <summary>
        /// A used buffer cannot be used after it has been returned. <see cref="GameLoop"/> hands each
        /// rental back exactly once; the pool refuses a second return of the same array, and refuses a
        /// write through an array that is no longer a rental, so a loop that returned early and kept
        /// encoding would fail here instead of writing into somebody else's memory.
        /// </summary>
        [Fact]
        public void ABufferIsNotUsableAfterItHasBeenReturned()
        {
            int liveBefore = CerasBufferPool.LiveRentals;
            byte[] buffer = CerasBufferPool.Rent(CerasSerializer.DefaultBufferSize);
            var client = new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests"));

            int offset = 0;
            client.Serialize(new PlayerInput { Sequence = 1 }, ref buffer, ref offset);
            Assert.True(CerasBufferPool.IsRental(buffer));

            CerasBufferPool.Return(buffer);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);

            // Returning it again is a different bug from never having rented it and gets a different
            // answer: it is already back in the shared pool, and may already be somebody else's.
            Assert.Throws<InvalidOperationException>(() => CerasBufferPool.Return(buffer));

            // And encoding through it is refused, which is the property that matters for a loop.
            byte[] reused = buffer;
            int afterReturn = 0;
            Assert.Throws<ArgumentException>(
                () => client.Serialize(new PlayerInput { Sequence = 2 }, ref reused, ref afterReturn));
        }

        /// <summary>
        /// The transport's lanes are bounded, and both ways of reaching the bound are exercised.
        /// <para>
        /// The first half is what the steady-state claim rests on: 64 exchanges of a 24 B message over a
        /// 256 B lane must leave the lane the same size, or the measured region would have included a
        /// lane growth and its zero would be about a different loop. The second half is about the
        /// harness: a frame too large for the lane grows it once, within the transport's own ceiling,
        /// and past that ceiling it is refused rather than grown - because a transport that can always
        /// grow is a transport whose allocation behaviour depends on message size, which is exactly what
        /// a steady-state B/op number is supposed to rule out.
        /// </para>
        /// </summary>
        [Fact]
        public void TheTransportGrowsWithinItsBoundAndIsRefusedPastIt()
        {
            using (var loop = GameLoop.Create())
            {
                int capacity = loop.Transport.CapacityToServer;
                byte[] before = loop.Transport.ToServerBuffer;

                for (int i = 0; i < MeasuredExchanges; i++) loop.Exchange();

                // Small messages in a large lane: no growth, same array, no refused frames, and the
                // lane really did go round its buffer rather than growing quietly to avoid it.
                Assert.Equal(capacity, loop.Transport.CapacityToServer);
                Assert.Same(before, loop.Transport.ToServerBuffer);
                Assert.Equal(0, loop.Transport.BufferGrowths);
                Assert.Equal(0, loop.Transport.RefusedSends);
                Assert.True(loop.Transport.CursorResets > 0,
                    "the lane never reused its tail, so the steady-state loop has not exercised the reclaim path");

                // A frame larger than the lane, on a drained lane: one bounded growth, and the bytes
                // still arrive intact.
                var big = new byte[capacity * 2];
                for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 7);

                int frame = loop.Transport.SendToServer(big);
                Assert.Equal(big.Length + FakeDuplexTransport.LengthPrefixSize, frame);
                Assert.Equal(1, loop.Transport.BufferGrowths);
                Assert.True(loop.Transport.CapacityToServer >= frame);
                Assert.Equal(big.Length, loop.Transport.PeekDeclaredLengthToServer());
                Assert.True(loop.Transport.TryReceiveOnServer(out var received));
                Assert.Equal(big, ToArray(received));

                // Past the ceiling it is refused, and the lane does not move.
                var strict = GameLoop.Create(transportMaximumPayloadBytes: 1024);
                try
                {
                    Assert.Throws<TransportFullException>(() => strict.Transport.SendToServer(new byte[2048]));
                    Assert.Equal(0, strict.Transport.BufferGrowths);
                    Assert.Equal(-1, strict.Transport.PeekDeclaredLengthToServer());
                }
                finally { strict.Dispose(); }
            }
        }

        /// <summary>
        /// A lane that still holds unread frames does not overwrite them and does not grow under them.
        /// A real pipe applies back pressure; one that overwrote would deliver a message nobody sent, and
        /// a loop that did not notice would still report zero allocations and a fine-looking checksum.
        /// </summary>
        [Fact]
        public void AFullLaneRefusesRatherThanOverwritingUnreadFrames()
        {
            // A 64 B lane and 24 B frames: two fit, a third does not while they are unread.
            var transport = new FakeDuplexTransport(capacityPerDirection: 64, maximumPayloadBytes: 4096);
            var small = new byte[20];
            small[0] = 1;

            transport.SendToServer(small);
            transport.SendToServer(small);
            int liveBefore = CerasBufferPool.LiveRentals;

            var ex = Assert.Throws<TransportFullException>(() => transport.SendToServer(small));
            Assert.Contains("unread", ex.Message, StringComparison.Ordinal);

            // Nothing was lost or overwritten, and nothing was allocated to refuse it.
            Assert.Equal(1, transport.RefusedSends);
            Assert.Equal(0, transport.BufferGrowths);
            Assert.Equal(liveBefore, CerasBufferPool.LiveRentals);
            Assert.True(transport.TryReceiveOnServer(out var first));
            Assert.True(transport.TryReceiveOnServer(out var second));
            Assert.Equal(1, first.First.Span[0]);
            Assert.Equal(1, second.First.Span[0]);

            // Drained, the same frame is accepted: the lane reclaimed its tail rather than growing.
            transport.SendToServer(small);
            Assert.True(transport.TryReceiveOnServer(out var third));
            Assert.Equal(1, third.First.Span[0]);
            Assert.Equal(0, transport.BufferGrowths);
        }

        /// <summary>
        /// The lane's own framing, read off the wire rather than inferred from a decoded value.
        /// <para>
        /// Three things, in increasing order of how much they matter. Peeking reports the length the
        /// prefix declares and does not consume the frame. A frame that cannot fit is refused rather
        /// than written over the bytes already on the wire. And a prefix that claims more than has
        /// actually arrived is not handed out as a short read - the honest answer is "not yet", because
        /// a payload of whatever happens to be in the buffer would be delivered as if it were data the
        /// sender had sent. The last one is produced by writing a prefix straight into the lane, which is
        /// the only way to make a well-behaved send's own length and the bytes present disagree.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFrameLengthIsOnTheWireAndIsNotTrustedBlindly()
        {
            var transport = new FakeDuplexTransport(capacityPerDirection: 64, maximumPayloadBytes: 4096);
            var payload = new byte[10] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            // Nothing to read yet, not even a prefix.
            Assert.Equal(-1, transport.PeekDeclaredLengthToServer());
            Assert.False(transport.TryReceiveOnServer(out _));

            transport.SendToServer(payload);
            Assert.Equal(payload.Length, transport.PeekDeclaredLengthToServer());
            // Peeking does not consume: the frame is still there afterwards.
            Assert.Equal(payload.Length, transport.PeekDeclaredLengthToServer());
            Assert.True(transport.TryReceiveOnServer(out var received));
            Assert.Equal(payload, ToArray(received));
            Assert.Equal(-1, transport.PeekDeclaredLengthToServer());

            // A frame the lane could never hold, offered while a frame is still unread. The lane is not
            // drained, so it may neither grow nor reclaim, and the frame already on the wire is left
            // exactly where it was rather than being overwritten by a refusal.
            transport.SendToServer(payload);
            int pending = transport.PendingToServer;
            Assert.False(transport.TrySendToServer(new byte[transport.CapacityToServer]));
            Assert.Equal(pending, transport.PendingToServer);
            Assert.True(transport.TryReceiveOnServer(out var untouched));
            Assert.Equal(payload, ToArray(untouched));

            // A length prefix that lies. A well-behaved send always writes the whole frame, so the only
            // way to make the declared length and the bytes present disagree is to corrupt the prefix
            // after the fact - which is exactly the case a defensive reader exists for, and the one a
            // truncated or spliced stream would produce.
            var damaged = new FakeDuplexTransport(capacityPerDirection: 64, maximumPayloadBytes: 4096);
            damaged.SendToServer(payload);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                damaged.ToServerBuffer.AsSpan(0, FakeDuplexTransport.LengthPrefixSize), 1000);

            Assert.Equal(1000, damaged.PeekDeclaredLengthToServer());
            Assert.False(damaged.TryReceiveOnServer(out _));
            // Refused, not consumed: the frame is still pending, so draining it and looking again
            // gives the same answer rather than a silently truncated read.
            Assert.Equal(payload.Length + FakeDuplexTransport.LengthPrefixSize, damaged.PendingToServer);
        }

        /// <summary>
        /// A ceiling above what the framing arithmetic can represent is refused, and the extremal
        /// <em>representable</em> ceiling behaves: <c>TrySend</c> and <c>Send</c> agree on both sides of
        /// it, and a refused frame leaves the lane untouched.
        /// <para>
        /// The defect this pins is arithmetic, not allocation. A frame is a 4-byte length prefix plus the
        /// payload, counted in the same <see cref="int"/> as the cursors, so a ceiling of
        /// <see cref="int.MaxValue"/> made <c>4 + ceiling</c> wrap negative and a payload near
        /// <see cref="int.MaxValue"/> wrap its own frame size negative - and a negative frame passes
        /// every "does it fit" test, so the send was reported as accepted and then failed part-way
        /// through, with its length prefix already written to the lane.
        /// </para>
        /// <para>
        /// Reproducing that needs a ~2 GB span, so this pins the two halves that are checkable cheaply:
        /// the configuration above the limit is refused outright, and at a small ceiling the accept/refuse
        /// boundary is exact and shared. The overflowing case itself is arithmetic on two ints, and the
        /// constructor bound is what removes it.
        /// </para>
        /// </summary>
        [Fact]
        public void ACeilingTheFramingCannotRepresent_IsRefusedRatherThanWrapped()
        {
            int limit = FakeDuplexTransport.MaximumPayloadBytesLimit;
            Assert.Equal(int.MaxValue - FakeDuplexTransport.LengthPrefixSize, limit);

            // One past the limit is the configuration that wraps. The largest representable one is fine,
            // and is accepted without allocating anything near it: the lane is 64 B.
            Assert.Throws<ArgumentOutOfRangeException>(() => new FakeDuplexTransport(64, limit + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FakeDuplexTransport(64, int.MaxValue));
            var roomy = new FakeDuplexTransport(64, limit);
            Assert.Equal(64, roomy.CapacityToServer);
            Assert.True(roomy.TrySendToServer(new byte[10]));

            // A 64 B lane with a 60 B ceiling: the extremal case in miniature, where the largest legal
            // payload frames to exactly the lane size.
            var transport = new FakeDuplexTransport(capacityPerDirection: 64, maximumPayloadBytes: 60);
            var exact = new byte[60];
            exact[0] = 7;
            Assert.True(transport.TrySendToServer(exact));
            Assert.Equal(64, transport.SendToServer(exact));
            Assert.True(transport.TryReceiveOnServer(out var received));
            Assert.Equal(exact, ToArray(received));

            // One byte over: both entry points refuse.
            var tooBig = new byte[61];
            Assert.False(transport.TrySendToServer(tooBig));
            Assert.Throws<TransportFullException>(() => transport.SendToServer(tooBig));

            // No half-written frame: a refused payload must not leave a length prefix behind, which is
            // exactly what the wrapped arithmetic did.
            Assert.Equal(0, transport.PendingToServer);
            Assert.Equal(-1, transport.PeekDeclaredLengthToServer());
            Assert.False(transport.TryReceiveOnServer(out _));
            Assert.Equal(2, transport.RefusedSends);

            // And the lane still works afterwards.
            Assert.True(transport.TrySendToServer(exact));
            Assert.Equal(64, transport.SendToServer(exact));
            Assert.True(transport.TryReceiveOnServer(out var again));
            Assert.Equal(exact, ToArray(again));
        }

        // ── the second case: a string, reported rather than asserted away ────────────────────────

        /// <summary>
        /// The reference-typed message is measured, and its allocations are reported rather than denied.
        /// <para>
        /// Encoding reuses the instance the caller already holds, so the write side has nothing to
        /// allocate and measures zero. Decoding cannot: the bytes name a string, so reading one
        /// produces a new string, and a target nobody held also has to be created. The two assertions
        /// are what make this a report rather than a shrug - the reused-encode case really does allocate
        /// nothing, and the decode case really does allocate something, with a floor rather than an
        /// exact figure, because the exact figure is a property of the runtime's string and object
        /// layout and would be a fragile thing to pin.
        /// </para>
        /// <para>
        /// The reused-target decode is measured too, because it is the row that shows what reuse is
        /// worth: the object is reused, the string is not, so it allocates strictly less than the
        /// fresh-target decode and is still not zero. A harness that asserted zero for all three would
        /// be asserting something false about the third.
        /// </para>
        /// </summary>
        [Fact]
        public void TheStringCase_AllocatesAndIsReportedAsSuch()
        {
            using (var loop = GameLoop.Create())
            {
                // Warmed by hand, once per body, so the first call's formatter construction is not in
                // any of the three numbers. (The chat payload itself is pre-encoded in the harness's
                // constructor, so nothing is built inside a measured region.)
                for (int i = 0; i < WarmupIterations; i++) loop.EncodeChatOnly();
                for (int i = 0; i < WarmupIterations; i++) loop.DecodeChatIntoReusedTarget();
                for (int i = 0; i < WarmupIterations; i++) loop.DecodeChatIntoFreshTarget();

                long encode = LoopMeasurementRunner.MeasureBytes(loop.EncodeChatOnly, 0, MeasuredExchanges);
                long reusedTarget = LoopMeasurementRunner.MeasureBytes(loop.DecodeChatIntoReusedTarget, 0, MeasuredExchanges);
                long freshTarget = LoopMeasurementRunner.MeasureBytes(loop.DecodeChatIntoFreshTarget, 0, MeasuredExchanges);

                // Reuse on the write side really is free: the caller already had the object and the
                // string, and neither is recreated in order to be written.
                Assert.Equal(0, encode);

                // The read side allocates, and the floor is the honest claim. A string is a heap
                // object; reading one out of bytes cannot avoid making it.
                Assert.True(freshTarget > 0, $"decoding into a fresh target allocated {freshTarget} B, expected some");
                Assert.True(reusedTarget > 0, $"decoding into a reused target allocated {reusedTarget} B, expected at least the string");
                Assert.True(reusedTarget < freshTarget,
                    $"reusing the target should save the object ({reusedTarget} B vs {freshTarget} B)");
            }
        }

        static byte[] ToArray(in ReadOnlySequence<byte> sequence)
        {
            var copy = new byte[(int)sequence.Length];
            sequence.CopyTo(copy);
            return copy;
        }
    }

    /// <summary>
    /// The loop under test: two transient serializers (one per endpoint), two pooled write buffers (one
    /// per endpoint), and the shared <see cref="FakeDuplexTransport"/> between them.
    /// <para>
    /// This is deliberately the same shape as the benchmark's <c>GameplayLoopHarness</c> rather than
    /// something smaller. The transport and the measurement helper are literally the same code
    /// (shared/GameplayLoop); the loop and the message types are declared twice because a Ceras
    /// serialization context is a per-compilation artifact and the two projects are two compilations.
    /// That is the one thing about them that has to be duplicated, and it is why the two copies are
    /// expected to stay in step.
    /// </para>
    /// <para>
    /// Nothing in <see cref="Exchange"/> allocates: the per-exchange snapshot of the encoded bytes that
    /// <c>AnExchangeActuallyMovesAndChecksTheBytes</c> compares is copied into a buffer allocated once
    /// here, because a <c>new byte[]</c> inside the loop would have been the one allocation the
    /// zero-allocation test was supposed to rule out.
    /// </para>
    /// </summary>
    sealed class GameLoop : IDisposable
    {
        /// <summary>Fixed for the whole run, so a wrong PlayerId shows up as a mismatch.</summary>
        public const int PlayerId = 7;

        /// <summary>Room for the byte snapshots <see cref="Exchange"/> keeps, and for a small growth.</summary>
        const int SnapshotCapacity = 256;

        readonly CerasFastSerializer _client;
        readonly CerasFastSerializer _server;
        // Not readonly: the pooled write path may hand back a larger array through the `ref`, and the
        // array to return is always the one the field now holds. See FakeDuplexTransport's note on
        // keeping this and the transport's own buffer as two separate objects.
        byte[] _clientWrite;
        byte[] _serverWrite;
        int _clientOffset;
        int _serverOffset;

        PlayerInput _serverInput;
        ServerState _clientState;
        ChatLine _clientChat = new ChatLine { Text = "spawn" };
        ChatLine _serverChat = new ChatLine { Text = "spawn" };

        readonly byte[] _encodedInput;
        readonly byte[] _encodedChat;
        readonly byte[] _lastClientInput = new byte[SnapshotCapacity];
        int _lastInputBytes;
        int _lastInputFrameBytes;
        long _unconsumedBytes;

        int _sequence;
        long _failures;
        long _checksum;

        public FakeDuplexTransport Transport { get; }
        public CerasFastSerializer Client => _client;
        public CerasFastSerializer Server => _server;
        public byte[] ClientWriteBuffer => _clientWrite;
        public byte[] ServerWriteBuffer => _serverWrite;
        public int Sequence => _sequence;
        public long Failures => _failures;
        public long Checksum => _checksum;

        /// <summary>Encoded size of one <see cref="PlayerInput"/>, prefix excluded.</summary>
        public int EncodedInputLength => _encodedInput.Length;

        /// <summary>Total framed size of the last input, prefix included.</summary>
        public int LastInputFrameBytes => _lastInputFrameBytes;

        /// <summary>Payload size of the last input, prefix excluded.</summary>
        public int LastInputPayloadBytes => _lastInputBytes;

        /// <summary>
        /// Bytes the last input's decoder left unread. Zero is the property: the frame's length prefix
        /// said how many bytes there were, and the decoder consumed exactly that many.
        /// </summary>
        public long UnconsumedBytes => _unconsumedBytes;

        public static GameLoop Create(int transportCapacityPerDirection = 256,
                                      int transportMaximumPayloadBytes = FakeDuplexTransport.DefaultMaximumPayloadBytes)
        {
            var loop = new GameLoop(transportCapacityPerDirection, transportMaximumPayloadBytes);
            try
            {
                // One exchange, so anything a test inspects afterwards is produced by the loop rather
                // than by this constructor.
                loop.Exchange();
                return loop;
            }
            catch
            {
                loop.Dispose();
                throw;
            }
        }

        GameLoop(int transportCapacityPerDirection, int transportMaximumPayloadBytes)
        {
            // Two instances, never one: a configured serializer owns mutable per-operation state, and a
            // client and a server are two processes in reality anyway.
            _client = new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests"));
            _server = new CerasFastSerializer(c => c.UseGeneratedFormatters("Tests"));
            if (ReferenceEquals(_client, _server))
                throw new InvalidOperationException("the client and the server must not share a serializer instance");

            Transport = new FakeDuplexTransport(transportCapacityPerDirection, transportMaximumPayloadBytes);

            _clientWrite = CerasBufferPool.Rent(CerasSerializer.DefaultBufferSize);
            _serverWrite = CerasBufferPool.Rent(CerasSerializer.DefaultBufferSize);

            var sample = new PlayerInput { Sequence = 1, PlayerId = PlayerId, Buttons = 1, PosX = 1, PosY = 2, PosZ = 3 };
            _encodedInput = EncodeOnce(_client, sample);
            _encodedChat = EncodeOnce(_client, new ChatLine { Sequence = 1, Text = _clientChat.Text });
        }

        static byte[] EncodeOnce<T>(CerasFastSerializer serializer, T value)
        {
            byte[] buffer = CerasBufferPool.Rent(CerasSerializer.DefaultBufferSize);
            try
            {
                int offset = 0;
                int written = serializer.Serialize(value, ref buffer, ref offset);
                var copy = new byte[written];
                Array.Copy(buffer, copy, written);
                return copy;
            }
            finally { CerasBufferPool.Return(buffer); }
        }

        /// <summary>One encoded input, copied out for a test that wants to look at the bytes.</summary>
        public byte[] LastClientInputBytes()
        {
            var copy = new byte[_lastInputBytes];
            Array.Copy(_lastClientInput, copy, _lastInputBytes);
            return copy;
        }

        /// <summary>One input encoded through the client's own serializer.</summary>
        public byte[] EncodeOnClient(PlayerInput value) => EncodeWith(_client, value, ref _clientWrite);

        /// <inheritdoc cref="EncodeOnClient"/>
        public byte[] EncodeOnServer(PlayerInput value) => EncodeWith(_server, value, ref _serverWrite);

        static byte[] EncodeWith(CerasFastSerializer serializer, PlayerInput value, ref byte[] buffer)
        {
            int offset = 0;
            int written = serializer.Serialize(value, ref buffer, ref offset);
            var copy = new byte[written];
            Array.Copy(buffer, copy, written);
            return copy;
        }

        /// <summary>
        /// One complete client/server roundtrip: encode an input, frame it, move it, decode and verify
        /// it, apply it, encode the resulting state, frame it, move it, decode and verify that.
        /// Allocates nothing.
        /// </summary>
        public void Exchange()
        {
            int sequence = ++_sequence;

            // ---- client -> server: the input sample --------------------------------------------
            var input = new PlayerInput
            {
                Sequence = sequence,
                PlayerId = PlayerId,
                Buttons = sequence & 0xF,
                PosX = sequence,
                PosY = sequence * 2f,
                PosZ = sequence * 3f,
            };

            _clientOffset = 0;
            int inputBytes = _client.Serialize(input, ref _clientWrite, ref _clientOffset);

            // The snapshot a test compares two exchanges with. A copy into a preallocated buffer, not a
            // new array: allocating here would be the very thing the zero-allocation test measures.
            if (inputBytes > _lastClientInput.Length)
                throw new InvalidOperationException($"the input grew to {inputBytes} B, past the {_lastClientInput.Length} B snapshot buffer");
            Array.Copy(_clientWrite, _lastClientInput, inputBytes);
            _lastInputBytes = inputBytes;

            int inputFrame = Transport.SendToServer(new ReadOnlySpan<byte>(_clientWrite, 0, inputBytes));
            _lastInputFrameBytes = inputFrame;

            if (!Transport.TryReceiveOnServer(out ReadOnlySequence<byte> inputWire))
            {
                _failures++;
                return;
            }

            var inputReader = new SequenceReader<byte>(inputWire);
            _server.Deserialize(ref inputReader, ref _serverInput);

            // The framing said how many bytes there were; the decoder must have eaten exactly that many.
            _unconsumedBytes = inputReader.Remaining;
            if (inputReader.Remaining != 0) _failures++;

            // The value changes every exchange and the decoder has to agree with it, so neither the
            // encode nor the decode can be optimized away.
            if (_serverInput.Sequence != sequence) _failures++;
            if (_serverInput.PlayerId != PlayerId) _failures++;
            if (_serverInput.Buttons != (sequence & 0xF)) _failures++;
            if (_serverInput.PosX != sequence) _failures++;
            if (_serverInput.PosY != sequence * 2f) _failures++;
            if (_serverInput.PosZ != sequence * 3f) _failures++;

            // ---- server -> client: the authoritative answer --------------------------------------
            // Authoritative in a way that depends on what arrived, so a decode that returned anything
            // other than the input could not produce a plausible state.
            var state = new ServerState
            {
                Sequence = _serverInput.Sequence,
                LastAppliedInput = _serverInput.Sequence,
                ServerTick = sequence,
                PosX = _serverInput.PosX * 0.5f,
                PosY = _serverInput.PosY * 0.5f,
                PosZ = _serverInput.PosZ * 0.5f,
                Accepted = (_serverInput.Buttons & 1) == 0,
            };

            _serverOffset = 0;
            int stateBytes = _server.Serialize(state, ref _serverWrite, ref _serverOffset);
            Transport.SendToClient(new ReadOnlySpan<byte>(_serverWrite, 0, stateBytes));

            if (!Transport.TryReceiveOnClient(out ReadOnlySequence<byte> stateWire))
            {
                _failures++;
                return;
            }

            var stateReader = new SequenceReader<byte>(stateWire);
            _client.Deserialize(ref stateReader, ref _clientState);
            if (stateReader.Remaining != 0) _failures++;

            if (_clientState.Sequence != sequence) _failures++;
            if (_clientState.LastAppliedInput != sequence) _failures++;
            if (_clientState.ServerTick != sequence) _failures++;
            if (_clientState.PosX != sequence * 0.5f) _failures++;
            if (_clientState.PosY != sequence * 1f) _failures++;
            if (_clientState.PosZ != sequence * 1.5f) _failures++;
            if (_clientState.Accepted != ((sequence & 1) == 0)) _failures++;

            _checksum += inputBytes + stateBytes + inputFrame
                         + _serverInput.Sequence + _clientState.LastAppliedInput;
        }

        /// <summary>Encoding a chat line from the instance the client already holds.</summary>
        public void EncodeChatOnly()
        {
            int sequence = ++_sequence;
            _clientChat.Sequence = sequence;

            _clientOffset = 0;
            int written = _client.Serialize(_clientChat, ref _clientWrite, ref _clientOffset);
            _checksum += written + _clientWrite[0];
        }

        /// <summary>
        /// Decoding a chat line into a target nobody held: a new object, and a new string, because the
        /// bytes name a string rather than pointing at one.
        /// </summary>
        public void DecodeChatIntoFreshTarget()
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(_encodedChat));
            ChatLine received = null;
            _server.Deserialize(ref reader, ref received);
            if (received == null) { _failures++; return; }
            _checksum += received.Sequence + received.Text.Length;
        }

        /// <summary>
        /// Decoding a chat line into a target the server already holds. The object is reused; the string
        /// is not, because the decoded value replaces the field.
        /// </summary>
        public void DecodeChatIntoReusedTarget()
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(_encodedChat));
            _serverChat.Sequence = 0;
            _server.Deserialize(ref reader, ref _serverChat);
            if (_serverChat.Text == null) { _failures++; return; }
            _checksum += _serverChat.Sequence + _serverChat.Text.Length;
        }

        public void Dispose()
        {
            // Exactly one return per rental. Returning the same array twice is refused by the pool
            // (and would be a real double return), and the arrays are read back so that a buffer the
            // write path replaced is the one that goes home.
            if (_clientWrite != null) CerasBufferPool.Return(_clientWrite);
            if (_serverWrite != null) CerasBufferPool.Return(_serverWrite);
        }
    }
}
