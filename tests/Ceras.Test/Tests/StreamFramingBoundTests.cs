// Bounded framing for the network example's stream extensions.
//
// `WriteToStream` / `ReadFromStream` are the only way `samples/NetworkExample` moves an object over a
// socket, and the receive side used to cast the length prefix straight to `int` and allocate an array
// of that size. Nothing stands between the peer and that allocation: the length arrives *first*, so a
// peer picks the size. Every test here is about where the refusal happens - before a payload byte is
// read and before the array exists - and about the framing bytes not having moved.
//
// Three properties, in the order the code applies them:
//
//   * the prefix is a varint of a *32-bit* length: at most five bytes, and the fifth contributes only
//     bits 28-31. `FF FF FF FF 1F` is therefore not a length of `uint.MaxValue` - which is what keeping
//     its low 32 bits produces - but a prefix that cannot exist at all;
//   * the decoded length is compared against the caller's cap *as a uint* and only cast to `int` once
//     it is known to fit. That is what makes `int.MaxValue` and `uint.MaxValue` refusals instead of
//     allocations, and it is the same order `SchemaSection.ReadInlineString` uses;
//   * a prefix or a payload that stops early is corruption, reported the way the rest of the library
//     reports corruption (`CerasDataCorruptionException` with a message naming what was wrong) rather
//     than as a bare `Exception` a caller has to string-match.
//
// Everything here is small. No test allocates the payload a hostile prefix asks for - that is the whole
// point - and none of them sends a frame anywhere near the default 1 MiB cap, so the suite stays cheap.
// The exact-cap boundary is proven at an explicit cap of a few dozen bytes instead, which is the same
// code path; the default differs only in the constant it passes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ceras;
using Ceras.Exceptions;
using Ceras.Helpers;
using Xunit;

namespace Ceras.Test.StreamFraming
{
    public class StreamFramingBoundTests
    {
        /// <summary>
        /// Bytes a refused read may allocate. A state machine, the message and the exception object all
        /// count, so the bound is generous - and still four orders of magnitude below the 2 GB an
        /// <see cref="int.MaxValue"/> prefix would have asked for, which is what makes it evidence that
        /// the declared length picked no allocation rather than a rounding difference.
        /// </summary>
        const int RefusalAllocationCeiling = 8 * 1024;

        static CerasFastSerializer New() => new CerasFastSerializer();

        // ── the default cap ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The documented default is 1 MiB, and the parameterless overload is the one that applies it.
        /// A peer that declares one byte more than that is refused by the cap - and refused after the
        /// prefix and nothing else, with no payload byte read and nothing like 1 MiB allocated.
        /// <para>
        /// The prefix is built by hand here rather than by a writer, because the case only exists if the
        /// declared length and the bytes that follow it disagree; a well-behaved send cannot produce it.
        /// </para>
        /// </summary>
        [Fact]
        public async Task AFrameOneByteOverTheDefaultCapIsRefusedBeforeAnyPayloadByteIsReadOrAllocated()
        {
            const int DefaultCap = 1 << 20;
            Assert.Equal(DefaultCap, CerasNetworkingExampleExtensions.DefaultMaxPayloadBytes);

            uint declared = (uint)DefaultCap + 1;
            var prefix = VarUInt(declared);
            var stream = new ScriptedStream(prefix, serveLimit: prefix.Length);
            var ceras = New();

            long before = GC.GetAllocatedBytesForCurrentThread();
            var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(stream));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // The message names both numbers, so a log line says which frame and which limit.
            Assert.Contains(declared.ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Contains($"limit of {DefaultCap} bytes", ex.Message, StringComparison.Ordinal);

            // Refused on the prefix alone: the length was all that was needed to refuse it, so the
            // payload must never have been asked for.
            Assert.Equal(prefix.Length, stream.BytesServed);
            Assert.True(allocated < RefusalAllocationCeiling,
                $"refusing the frame allocated {allocated} B; a refusal must not pick an allocation from the declared length");
        }

        // ── an explicit cap ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The same bound with a cap the caller chose, at the length where the old code stopped being a
        /// reader and became an allocation: <see cref="int.MaxValue"/> bytes.
        /// <para>
        /// 2 GB is not something a test may allocate in order to prove it was refused. It does not have
        /// to: the frame is refused on the prefix, so the proof is that the stream served the prefix and
        /// nothing else and that the thread allocated almost nothing. The old code cast first and asked
        /// for the array second, and this is the case that tells the two orders apart.
        /// </para>
        /// </summary>
        [Fact]
        public async Task AFrameOverAnExplicitCapIsRefusedBeforeAnyPayloadByteIsReadOrAllocated()
        {
            // int.MaxValue is a legal uint varint (FF FF FF FF 07), so this is not a malformed prefix: it
            // is a well-formed length that no cap this API can express should accept.
            var prefix = VarUInt(int.MaxValue);
            Assert.Equal(5, prefix.Length);
            var stream = new ScriptedStream(prefix, serveLimit: prefix.Length);
            var ceras = New();
            const int cap = 4096;

            long before = GC.GetAllocatedBytesForCurrentThread();
            var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(stream, cap));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Contains($"limit of {cap} bytes", ex.Message, StringComparison.Ordinal);
            Assert.Equal(prefix.Length, stream.BytesServed);
            Assert.True(allocated < RefusalAllocationCeiling,
                $"refusing the frame allocated {allocated} B; the declared length must not reach an allocation");
        }

        /// <summary>
        /// The cap is exact: a frame whose payload is exactly the cap is read, and the same frame one
        /// byte over is refused.
        /// <para>
        /// "At most <c>n</c> bytes" is only a contract if the boundary itself is checked. Both halves are
        /// the same bytes and the same reader, so a cap implemented as <c>&gt;</c> instead of
        /// <c>&gt;=</c> - the usual off-by-one - fails here rather than in a protocol.
        /// </para>
        /// </summary>
        [Fact]
        public async Task AnExplicitCapIsHonouredExactly_AtTheCapItReads_OneOverItRefuses()
        {
            const string Message = "a message whose length is whatever it turns out to be";
            var ceras = New();
            var frame = WriteFrame(ceras, Message);
            uint payloadLength = ReadVarUInt(frame, 0, out int prefixBytes);

            // A prefix of its own, so "exactly the payload" is not also "everything there is".
            Assert.True(payloadLength >= 2, $"the probe frame carries {payloadLength} payload bytes");

            var atCap = await ceras.ReadFromStream(new MemoryStream(frame), (int)payloadLength);
            Assert.Equal(Message, Assert.IsType<string>(atCap));

            // The payload is unreadable here: a reader that touched it would be reading bytes it had no
            // reason to want, since the prefix alone was enough to refuse the frame.
            var oneOver = new ScriptedStream(frame, serveLimit: prefixBytes);
            var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(
                () => ceras.ReadFromStream(oneOver, (int)payloadLength - 1));
            Assert.Contains($"limit of {(int)payloadLength - 1} bytes", ex.Message, StringComparison.Ordinal);
            Assert.Equal(prefixBytes, oneOver.BytesServed);
        }

        /// <summary>
        /// A cap that could never accept a frame is a configuration mistake, not corrupt data, so it is
        /// reported as one - and reported at the call, before the stream is touched at all.
        /// </summary>
        [Fact]
        public async Task TheCapIsValidatedBeforeAnythingIsRead()
        {
            var ceras = New();
            var frame = WriteFrame(ceras, "hello");
            var prefix = VarUInt(DeclaredLength(frame));

            foreach (int badCap in new[] { 0, -1, int.MinValue })
            {
                var stream = new ScriptedStream(frame, serveLimit: 0);
                var ex = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ceras.ReadFromStream(stream, badCap); });
                Assert.Equal("maxPayloadBytes", ex.ParamName);
                Assert.Equal(0, stream.BytesServed);
            }

            var nullStream = Assert.Throws<ArgumentNullException>(() => { _ = ceras.ReadFromStream(null!, 1024); });
            Assert.Equal("stream", nullStream.ParamName);

            // The default overload answers a null stream the same way, rather than letting the read fail
            // somewhere inside itself.
            var nullStream2 = Assert.Throws<ArgumentNullException>(() => { _ = ceras.ReadFromStream(null!); });
            Assert.Equal("stream", nullStream2.ParamName);

            // A cap that is merely small is legal, and says so by refusing the frame rather than the
            // configuration: one byte is a fine ceiling for a heartbeat and too small for this frame.
            var tiny = new ScriptedStream(frame, serveLimit: prefix.Length);
            var refused = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(tiny, 1));
            Assert.Contains("limit of 1 bytes", refused.Message, StringComparison.Ordinal);
            Assert.Equal(prefix.Length, tiny.BytesServed);
        }

        // ── the 32-bit prefix grammar ────────────────────────────────────────────────────────────

        /// <summary>
        /// The fifth byte of a 32-bit varint carries four bits, not seven, and the reader says so instead
        /// of keeping the low 32.
        /// <para>
        /// Truncating is the dangerous outcome rather than the missing one: `FF FF FF FF 1F` denotes
        /// 0x1FFFFFFFF, so keeping its low 32 bits answers `uint.MaxValue` - a number that looks like a
        /// plausible length. A caller with a large cap would then allocate 4 GB - or, cast to `int`, ask
        /// for a negative array and get an arithmetic exception from inside its own read. Refusing is the
        /// only answer that is honest about what the bytes say.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ALengthPrefixThatDoesNotFitA32BitLengthIsRefusedRatherThanTruncated()
        {
            var ceras = New();

            // Every fifth byte from 0x10 (bit 32, one past the top of a uint) upwards is the same defect.
            foreach (byte fifth in new byte[] { 0x10, 0x1F, 0x7F })
            {
                var prefix = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, fifth };
                var stream = new ScriptedStream(prefix, serveLimit: prefix.Length);

                var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(stream));
                Assert.Contains("32-bit", ex.Message, StringComparison.Ordinal);

                // Refused on the prefix: the value never became a length, let alone an array.
                Assert.Equal(prefix.Length, stream.BytesServed);
            }
        }

        /// <summary>
        /// The other side of the same rule: the largest length a uint can hold is a legal prefix, and it
        /// is refused by the cap rather than by the prefix grammar.
        /// <para>
        /// This is what keeps the previous test honest. A decoder that refused every five-byte prefix, or
        /// that kept its own private threshold below <see cref="uint.MaxValue"/>, would pass it - and
        /// would break a legitimate large frame from a peer that really meant that number. `FF FF FF FF
        /// 0F` is <see cref="uint.MaxValue"/>, it decodes exactly, and it is over the limit.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TheLargest32BitLengthIsRefusedByTheCapNotByThePrefixGrammar()
        {
            var prefix = VarUInt(uint.MaxValue);
            Assert.Equal(5, prefix.Length);
            Assert.Equal(0x0F, prefix[4]);

            var stream = new ScriptedStream(prefix, serveLimit: prefix.Length);
            var ceras = New();
            const int cap = 1 << 20;

            var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(stream, cap));
            Assert.Contains($"limit of {cap} bytes", ex.Message, StringComparison.Ordinal);

            // The distinction the previous test depends on: this is a limit, and says so. Neither
            // "does not fit a 32-bit length" nor "not a valid varint" may appear.
            Assert.DoesNotContain("32-bit", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("valid varint", ex.Message, StringComparison.Ordinal);
            Assert.Equal(prefix.Length, stream.BytesServed);
        }

        /// <summary>
        /// A prefix longer than five bytes is not a 32-bit length in any encoding, however the bits fall.
        /// It is refused at the byte that made it too long, without reading the sixth.
        /// </summary>
        [Fact]
        public async Task APrefixLongerThanFiveBytesIsRefusedAndNeverReachesThePayload()
        {
            var ceras = New();

            // A continuation on every byte, then a sixth byte that would have supplied the low bits.
            var prefix = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
            var stream = new ScriptedStream(prefix, serveLimit: prefix.Length);

            var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(stream));
            Assert.Contains("at most 5 bytes", ex.Message, StringComparison.Ordinal);

            Assert.Equal(5, stream.BytesServed);
        }

        // ── truncation ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A stream that ends in the middle of its own length prefix. That is a truncated frame, and it
        /// is reported as corruption rather than as a cap violation or a bare "stream terminated".
        /// <para>
        /// It cannot be reported as a limit refusal: there is no length to compare against the cap,
        /// which is precisely why the grammar has to be strict before the comparison. Nothing is
        /// allocated and no payload byte is requested.
        /// </para>
        /// </summary>
        [Fact]
        public async Task ATruncatedPrefixIsCorruptionAndNotALimitRefusal()
        {
            var ceras = New();

            foreach (var prefix in new[]
            {
                new byte[] { 0x80 },                   // a continuation bit, then the end of the stream
                new byte[] { 0x80, 0x80 },             // two continuations, then the end
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, // four of the five bytes of a legal prefix
            })
            {
                var stream = new ScriptedStream(prefix, serveLimit: prefix.Length);
                var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(stream));

                Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(prefix.Length, stream.BytesServed);
            }

            // An empty stream: not even the first prefix byte.
            var empty = new ScriptedStream(Array.Empty<byte>(), serveLimit: 0);
            var ex2 = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(empty));
            Assert.Contains("truncated", ex2.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, empty.BytesServed);
        }

        /// <summary>
        /// A well-formed prefix whose payload stops early. The length was legal and within the cap, so
        /// this is the one refusal that happens after the array exists - the frame was genuinely allowed
        /// to be this big, and the peer simply stopped sending.
        /// </summary>
        [Fact]
        public async Task ATruncatedPayloadIsCorruption()
        {
            var ceras = New();
            const int declared = 64;
            var prefix = VarUInt(declared);
            var frame = new byte[prefix.Length + 10];    // 10 of the 64 declared payload bytes arrived
            Array.Copy(prefix, frame, prefix.Length);

            var stream = new ScriptedStream(frame, serveLimit: frame.Length);
            var ex = await Assert.ThrowsAsync<CerasDataCorruptionException>(() => ceras.ReadFromStream(stream));

            Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(frame.Length, stream.BytesServed);    // it read everything that was there
        }

        // ── the happy path, and the framing bytes ──────────────────────────────────────────────

        /// <summary>
        /// A frame written by <see cref="CerasNetworkingExampleExtensions.WriteToStream"/> reads back
        /// through the default cap and through an explicit one, and a null round-trips as null rather
        /// than as a failure.
        /// <para>
        /// The null is a value on this wire, so a reader that treated it as an error would break a
        /// protocol that uses it - and one that substituted a default would break it silently instead.
        /// </para>
        /// </summary>
        [Fact]
        public async Task AValidFrameAndANullFrameBothRoundTrip()
        {
            var ceras = New();

            var value = new Dictionary<string, int> { { "answer", 42 } };
            var frame = WriteFrame(ceras, value);

            var back = await ceras.ReadFromStream(new MemoryStream(frame));
            Assert.Equal(42, Assert.IsType<Dictionary<string, int>>(back)["answer"]);

            // The same frame under a cap chosen by the caller, comfortably above it.
            var capped = await ceras.ReadFromStream(new MemoryStream(frame), 64 * 1024);
            Assert.Equal(42, Assert.IsType<Dictionary<string, int>>(capped)["answer"]);

            Assert.Null(await ceras.ReadFromStream(new MemoryStream(WriteFrame(ceras, null))));
        }

        /// <summary>
        /// The framing on the wire is unchanged: a canonical varint length followed by the payload, and a
        /// reader that stops exactly at the end of the frame, so the next frame starts where it should.
        /// <para>
        /// "Unchanged" is the part that matters for a protocol. A bound added on the read side must not
        /// move the bytes the write side produces, or the two ends of an existing connection stop
        /// understanding each other. The prefix is decoded here by a small independent reader and
        /// compared against the same value re-encoded longhand, so a writer that emitted a non-minimal
        /// varint - or a reader that consumed the wrong number of prefix bytes - fails here rather than
        /// passing as two sides agreeing on something new.
        /// </para>
        /// </summary>
        [Fact]
        public async Task TheFramingIsStillALengthPrefixTheWriterHasAlwaysWritten()
        {
            var ceras = New();

            // Long enough that the prefix needs more than one byte, so the multi-byte case is the one
            // under test rather than the trivial one.
            const string Message = "a frame long enough that its length prefix cannot fit in a single byte, " +
                                   "which is the only way a varint prefix is exercised above 127 bytes";
            var frame = WriteFrame(ceras, Message);
            uint declared = ReadVarUInt(frame, 0, out int prefixBytes);

            Assert.True(prefixBytes >= 2,
                $"the probe frame carries {declared} bytes, so its {prefixBytes}-byte prefix proves nothing; make the message longer");
            Assert.Equal(declared, (uint)(frame.Length - prefixBytes));
            Assert.Equal(VarUInt(declared), Slice(frame, 0, prefixBytes));   // canonical, minimal

            // Two frames in one stream: the first reader must stop at the declared length, not eat the
            // second frame's prefix as payload and not stop short of it either.
            var two = new MemoryStream();
            ceras.WriteToStream(two, Message);
            ceras.WriteToStream(two, 12345);
            two.Position = 0;    // a stream reads from where it is, and writing left it at the end

            Assert.Equal(Message, Assert.IsType<string>(await ceras.ReadFromStream(two)));
            Assert.Equal(12345, await ceras.ReadFromStream(two));
            Assert.Equal(two.Length, two.Position);
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A frame as <see cref="CerasNetworkingExampleExtensions.WriteToStream"/> writes it, returned
        /// as bytes so a test can measure, truncate or re-frame it.
        /// </summary>
        static byte[] WriteFrame(CerasFastSerializer ceras, object value)
        {
            var stream = new MemoryStream();
            ceras.WriteToStream(stream, value);
            return stream.ToArray();
        }

        static byte[] Slice(byte[] bytes, int offset, int count)
        {
            var copy = new byte[count];
            Array.Copy(bytes, offset, copy, 0, count);
            return copy;
        }

        /// <summary>
        /// Canonical varint bytes for a value, written out longhand so the tests do not agree with the
        /// implementation by construction. This mirrors the writer's encoding; the hostile prefixes in
        /// these tests are spelled out as literal bytes instead.
        /// </summary>
        static byte[] VarUInt(uint value)
        {
            var bytes = new List<byte>();
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                bytes.Add(b);
            } while (value != 0);
            return bytes.ToArray();
        }

        /// <summary>An independent varint reader, used to check what the writer put on the wire.</summary>
        static uint ReadVarUInt(byte[] bytes, int offset, out int consumed)
        {
            uint result = 0;
            int shift = 0;
            int i = offset;
            while (true)
            {
                Assert.True(i < bytes.Length, "the frame ended inside its own length prefix");
                byte b = bytes[i++];
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) { consumed = i - offset; return result; }
                shift += 7;
            }
        }

        static uint DeclaredLength(byte[] frame) => ReadVarUInt(frame, 0, out _);

        /// <summary>
        /// A stream over a fixed script, with a hard stop partway through it.
        /// <para>
        /// <paramref name="serveLimit"/> is the point of the class. A refusal is supposed to happen on
        /// the length prefix, before any payload byte is wanted, so the payload is made unreadable
        /// rather than merely uncounted: a reader that reached for one fails loudly here, instead of
        /// quietly satisfying an assertion about how many bytes it happened to take. <see cref="MemoryStream"/>
        /// cannot do that - its whole buffer is readable and it reports the end of the stream by
        /// returning 0 - and counting alone cannot tell "refused without reading" apart from "refused
        /// after reading".
        /// </para>
        /// </summary>
        sealed class ScriptedStream : Stream
        {
            readonly byte[] _bytes;
            readonly int _serveLimit;
            int _position;

            /// <summary>Bytes the reader actually took. Never more than <c>serveLimit</c>.</summary>
            public int BytesServed => _position;

            public ScriptedStream(byte[] bytes, int serveLimit)
            {
                _bytes = bytes;
                _serveLimit = serveLimit;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0) return 0;

                // A real stream returns what is there and only then reports the end, by returning 0. So
                // a read past the script is the end of the stream, not a violation.
                int available = Math.Min(count, _bytes.Length - _position);
                if (available <= 0) return 0;

                // Past the limit but with bytes on hand: the reader wanted something this test refused
                // to serve, which is the failure these tests are built to notice.
                if (_position + available > _serveLimit)
                    throw new InvalidOperationException(
                        $"the reader asked for payload bytes: {available} more at position {_position}, past the " +
                        $"{_serveLimit} B this test allows serving. A refusal must happen on the length prefix.");

                Array.Copy(_bytes, _position, buffer, offset, available);
                _position += available;
                return available;
            }

            // The extension reads through the byte[] overload, which is the one the streaming API was
            // written against; a cancellation token is deliberately not part of it.
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.FromResult(Read(buffer, offset, count));

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
