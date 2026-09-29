using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Ceras.GameplayLoop
{
    /// <summary>
    /// A duplex in-memory message transport that a client/server loop can be measured on without the
    /// transport's own allocations landing in the serializer's B/op column.
    /// <para>
    /// Two byte lanes, one per direction, each framing a payload as a little-endian <see cref="uint"/>
    /// length followed by that many payload bytes. Both are allocated once in the constructor and the
    /// only way a frame moves is a copy into storage that already exists: no
    /// <see cref="System.IO.MemoryStream"/>, no <c>ArrayBufferWriter</c>, no per-message array, no
    /// per-message result.
    /// </para>
    /// <para>
    /// A lane is a <em>linear</em> buffer with two cursors rather than a ring, so a received frame is
    /// always one contiguous run and can be handed to a reader as a single-segment
    /// <see cref="ReadOnlySequence{T}"/> with no copy - the public ways to build a two-segment sequence
    /// over a <c>byte[]</c> either need a heap-allocated <see cref="ReadOnlySequenceSegment{T}"/> or do
    /// not exist. It is also how a real byte pipe manages a buffer: a tail too small is reclaimed by
    /// restarting a drained lane or compacting pending bytes, and only when neither suffices does the
    /// pipe apply back pressure, which is what <see cref="SendToServer"/> does.
    /// </para>
    /// <para>
    /// <b>The lanes are the transport's own arrays and are not pooled rentals.</b> They must never be
    /// handed to <c>CerasSerializer.Serialize(obj, ref buffer, ref offset)</c>, which declares its buffer
    /// pooled and may therefore return a grown-out array to <see cref="Ceras.CerasBufferPool.Return"/> -
    /// and <c>ArrayPool&lt;byte&gt;.Shared.Return</c> adopts any array whose length is one of its bucket
    /// sizes, so an unrelated tenant would then be writing into a buffer the loop still owns. The pooled
    /// write path refuses such an array outright (see <c>PooledBufferOwnershipTests</c>), which is the
    /// point: a loop keeps its encode buffers and the transport's lanes as separate objects.
    /// </para>
    /// </summary>
    public sealed class FakeDuplexTransport
    {
        /// <summary>Bytes of framing in front of every payload: a little-endian <c>uint</c> length.</summary>
        public const int LengthPrefixSize = sizeof(uint);

        /// <summary>
        /// Lane size per direction unless the caller says otherwise. Comfortably larger than a gameplay
        /// message, so a steady-state loop never grows and its measured region stays free of the array a
        /// growth would allocate.
        /// </summary>
        public const int DefaultCapacityPerDirection = 4096;

        /// <summary>Largest payload either direction will ever accept, growth included.</summary>
        public const int DefaultMaximumPayloadBytes = 1024 * 1024;

        /// <summary>
        /// Largest <c>maximumPayloadBytes</c> that can be represented. A frame is a
        /// <see cref="LengthPrefixSize"/>-byte prefix plus the payload, counted in the same
        /// <see cref="int"/> as the cursors, so one byte more would wrap <c>LengthPrefixSize +
        /// maximumPayloadBytes</c> negative. A configuration above this is refused rather than accepted
        /// and silently miscomputed.
        /// </summary>
        public const int MaximumPayloadBytesLimit = int.MaxValue - LengthPrefixSize;

        byte[] _clientToServer;
        byte[] _serverToClient;
        int _c2sRead, _c2sWrite, _s2cRead, _s2cWrite;

        readonly int _maximumPayloadBytes;

        /// <summary>
        /// <c>LengthPrefixSize + _maximumPayloadBytes</c>, computed once. Cached rather than recomputed
        /// so that <see cref="CanAccept"/> and <see cref="WriteFrame"/> cannot each form their own idea
        /// of the ceiling, and so the invariant that it does not overflow is stated in exactly one place.
        /// </summary>
        readonly int _framingCeiling;

        int _bufferGrowths;
        int _refusedSends;
        int _cursorResets;
        int _compactions;
        int _largestFrame;
        long _framesToServer, _framesToClient;
        long _payloadBytesToServer, _payloadBytesToClient;

        /// <param name="capacityPerDirection">Lane size per direction. Must leave room for a length
        /// prefix and at least one payload byte. A size this large is a real allocation; growth is
        /// bounded by <paramref name="maximumPayloadBytes"/>, not by memory, so a ceiling near
        /// <see cref="MaximumPayloadBytesLimit"/> permits a lane of ~2 GB.</param>
        /// <param name="maximumPayloadBytes">Hard ceiling on a single payload, in either direction,
        /// growth included, and at most <see cref="MaximumPayloadBytesLimit"/>. This is the bound that
        /// makes the lanes' growth behaviour checkable rather than open-ended.</param>
        public FakeDuplexTransport(int capacityPerDirection = DefaultCapacityPerDirection,
                                   int maximumPayloadBytes = DefaultMaximumPayloadBytes)
        {
            if (capacityPerDirection < LengthPrefixSize + 1)
                throw new ArgumentOutOfRangeException(nameof(capacityPerDirection), capacityPerDirection,
                    $"A lane needs room for a {LengthPrefixSize}-byte length prefix and at least one payload byte.");
            if (maximumPayloadBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes), maximumPayloadBytes,
                    "A transport that accepts no payload at all is not a transport.");
            if (maximumPayloadBytes > MaximumPayloadBytesLimit)
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes), maximumPayloadBytes,
                    $"A frame is a {LengthPrefixSize}-byte length prefix plus the payload in a single int, so the largest configurable payload is {MaximumPayloadBytesLimit} B. " +
                    "A larger ceiling would make the framing arithmetic wrap rather than refuse.");

            _clientToServer = new byte[capacityPerDirection];
            _serverToClient = new byte[capacityPerDirection];
            _maximumPayloadBytes = maximumPayloadBytes;
            _framingCeiling = LengthPrefixSize + maximumPayloadBytes;   // cannot overflow: bounded above
        }

        /// <summary>Current lane size, client to server. Constant unless a frame forced a growth.</summary>
        public int CapacityToServer => _clientToServer.Length;

        /// <summary>Current lane size, server to client. Constant unless a frame forced a growth.</summary>
        public int CapacityToClient => _serverToClient.Length;

        /// <summary>Framed bytes written to the server and not yet received back, prefix included.</summary>
        public int PendingToServer => _c2sWrite - _c2sRead;

        /// <summary>Framed bytes written to the client and not yet received back, prefix included.</summary>
        public int PendingToClient => _s2cWrite - _s2cRead;

        /// <summary>How many times either lane was replaced by a bigger one. Zero in a steady-state loop.</summary>
        public int BufferGrowths => _bufferGrowths;

        /// <summary>Frames rejected because they did not fit. Non-zero means the caller is not draining.</summary>
        public int RefusedSends => _refusedSends;

        /// <summary>
        /// How many times a drained lane's cursors went back to the front rather than compacting
        /// pending bytes. Counted so a test can show the loop really does go round its buffer and still
        /// read back correctly.
        /// </summary>
        public int CursorResets => _cursorResets;

        /// <summary>How many times pending bytes were moved down to the front of a lane to make room.</summary>
        public int Compactions => _compactions;

        /// <summary>Framed size of the largest frame seen, prefix included. The loop's high-water mark.</summary>
        public int LargestFrame => _largestFrame;

        /// <summary>Payload bytes handed to the server lane, prefix excluded.</summary>
        public long PayloadBytesToServer => _payloadBytesToServer;

        /// <summary>Payload bytes handed to the client lane, prefix excluded.</summary>
        public long PayloadBytesToClient => _payloadBytesToClient;

        /// <summary>How many frames the client has written.</summary>
        public long FramesToServer => _framesToServer;

        /// <summary>How many frames the server has written.</summary>
        public long FramesToClient => _framesToClient;

        /// <summary>
        /// The client-to-server lane, for a caller in this assembly that needs to reason about who owns
        /// the array - a test asserting the lanes are not pooled rentals, or writing a deliberately
        /// corrupt prefix to check the reader refuses it. <c>internal</c> because it is mutable and the
        /// only supported way to move bytes is <see cref="SendToServer"/>, which frames them.
        /// </summary>
        internal byte[] ToServerBuffer => _clientToServer;

        /// <inheritdoc cref="ToServerBuffer"/>
        internal byte[] ToClientBuffer => _serverToClient;

        /// <summary>
        /// Frames <paramref name="payload"/> into the client-to-server lane and returns the total framed
        /// size written. A refused frame leaves the lane exactly as it was.
        /// </summary>
        /// <exception cref="TransportFullException">The frame does not fit, even after the lane has been
        /// made as empty as it can be.</exception>
        public int SendToServer(ReadOnlySpan<byte> payload)
        {
            int frame = WriteFrame(ref _clientToServer, ref _c2sWrite, ref _c2sRead, payload);
            _framesToServer++;
            _payloadBytesToServer += payload.Length;
            return frame;
        }

        /// <inheritdoc cref="SendToServer"/>
        public int SendToClient(ReadOnlySpan<byte> payload)
        {
            int frame = WriteFrame(ref _serverToClient, ref _s2cWrite, ref _s2cRead, payload);
            _framesToClient++;
            _payloadBytesToClient += payload.Length;
            return frame;
        }

        /// <summary>
        /// <see cref="SendToServer"/> without the exception: true exactly when that call would have
        /// succeeded, including the case where the frame exceeds the lane as it stands but the lane is
        /// drained and may therefore grow. A prediction about the same policy, not a second one.
        /// </summary>
        public bool TrySendToServer(ReadOnlySpan<byte> payload)
        {
            if (CanAccept(_clientToServer.Length, _c2sWrite, _c2sRead, payload.Length)) return true;
            _refusedSends++;
            return false;
        }

        /// <inheritdoc cref="TrySendToServer"/>
        public bool TrySendToClient(ReadOnlySpan<byte> payload)
        {
            if (CanAccept(_serverToClient.Length, _s2cWrite, _s2cRead, payload.Length)) return true;
            _refusedSends++;
            return false;
        }

        /// <summary>
        /// Hands back the next client-to-server frame's payload, aliasing the lane, and advances the read
        /// cursor. No copy and no allocation. The memory is only valid until the next send on this lane -
        /// a real socket has the same property, and it is why a caller deserializes from the sequence
        /// rather than keeping the bytes.
        /// </summary>
        public bool TryReceiveOnServer(out ReadOnlySequence<byte> payload)
            => TryReadFrame(_clientToServer, _c2sRead, _c2sWrite, ref _c2sRead, out payload);

        /// <inheritdoc cref="TryReceiveOnServer"/>
        public bool TryReceiveOnClient(out ReadOnlySequence<byte> payload)
            => TryReadFrame(_serverToClient, _s2cRead, _s2cWrite, ref _s2cRead, out payload);

        /// <summary>
        /// The length the next client-to-server frame declares, without consuming it, or -1 when no
        /// complete prefix has arrived. Lets a caller assert the framing is on the wire rather than
        /// inferring it from a decoded value.
        /// </summary>
        public int PeekDeclaredLengthToServer()
            => PeekDeclaredLength(_clientToServer, _c2sRead, _c2sWrite);

        /// <inheritdoc cref="PeekDeclaredLengthToServer"/>
        public int PeekDeclaredLengthToClient()
            => PeekDeclaredLength(_serverToClient, _s2cRead, _s2cWrite);

        // ── internals ─────────────────────────────────────────────────────────────────────────

        static int PeekDeclaredLength(byte[] buffer, int read, int write)
            => write - read < LengthPrefixSize
                ? -1
                : unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                    new ReadOnlySpan<byte>(buffer, read, LengthPrefixSize)));

        /// <summary>
        /// Whether a <paramref name="payloadLength"/>-byte payload would be accepted now, by the same
        /// rules in the same order as <see cref="WriteFrame"/>: the ceiling first, then whether the lane
        /// is big enough, then whether a drained lane could grow, and only then the tail.
        /// <para>
        /// The ceiling test is first precisely so that <c>frame</c> below is only ever formed from a
        /// payload the constructor already guaranteed is representable. With the ceiling checked last, or
        /// absent, <c>LengthPrefixSize + payloadLength</c> wraps negative for a payload near
        /// <see cref="int.MaxValue"/> - a negative frame passes every "does it fit" test, so an impossible
        /// send is reported as accepted and then fails part-way through, after the prefix is already on
        /// the wire.
        /// </para>
        /// </summary>
        bool CanAccept(int bufferLength, int write, int read, int payloadLength)
        {
            if (payloadLength > _maximumPayloadBytes) return false;
            int frame = LengthPrefixSize + payloadLength;          // safe: payloadLength <= _framingCeiling - 4
            if (frame <= bufferLength) return CanMakeRoom(bufferLength, write, read, frame);
            // Too big for this lane: a drained one may grow, one that still holds bytes may not.
            return write == read && frame <= _framingCeiling;
        }

        /// <summary>
        /// Whether a <paramref name="frame"/>-byte frame fits in a lane's tail, or would after the lane
        /// reclaims what it can. The tail's free space is <c>Length - write</c>, not
        /// <c>Length - pending</c>: a lane that checks the second happily writes past the end of its own
        /// array.
        /// </summary>
        static bool CanMakeRoom(int bufferLength, int write, int read, int frame)
        {
            if (frame <= bufferLength - write) return true;      // the tail is big enough as it stands
            int pending = write - read;
            if (pending == 0) return frame <= bufferLength;      // an empty lane simply starts over
            return pending + frame <= bufferLength;              // otherwise only compaction can help
        }

        int WriteFrame(ref byte[] buffer, ref int write, ref int read, ReadOnlySpan<byte> payload)
        {
            int payloadLength = payload.Length;

            // Before anything is written, and for the same reason as in CanAccept: past this point
            // `frame` is formed from a representable length and every "does it fit" test below means it.
            if (payloadLength > _maximumPayloadBytes)
            {
                _refusedSends++;
                throw new TransportFullException(
                    $"A {payloadLength} B payload is above this transport's {_maximumPayloadBytes} B ceiling, and a lane is not allowed to grow past it.");
            }

            int frame = LengthPrefixSize + payloadLength;

            if (frame > buffer.Length)
            {
                // Growing a lane that still holds unread frames would mean copying them, which is not
                // something a byte pipe does: it applies back pressure instead.
                if (write != read)
                {
                    _refusedSends++;
                    throw new TransportFullException(
                        $"A {payloadLength} B payload needs a {frame} B frame, which is larger than the {buffer.Length} B lane, and the lane still holds {write - read} unread bytes. " +
                        "A real pipe would block here; drain the lane before sending a frame this large.");
                }

                int grown = RoundUpToPowerOfTwo(frame);
                if (grown > _framingCeiling) grown = _framingCeiling;
                if (grown < frame)
                {
                    _refusedSends++;
                    throw new TransportFullException(
                        $"A {payloadLength} B payload cannot be framed within the {_framingCeiling} B ceiling this transport was given.");
                }

                buffer = new byte[grown];
                // The lane was empty, so rebasing both cursors to zero loses nothing.
                write = 0;
                read = 0;
                _bufferGrowths++;
            }

            if (!CanMakeRoom(buffer.Length, write, read, frame))
            {
                int pending = write - read;
                _refusedSends++;
                throw new TransportFullException(
                    $"A {frame} B frame does not fit in the {buffer.Length - pending} free bytes left in a {buffer.Length} B lane that still holds {pending} unread bytes. " +
                    "A real pipe would block here; drain the lane before sending.");
            }

            if (frame > buffer.Length - write)
            {
                // The tail is too small and CanMakeRoom has established that reclaiming it is enough.
                int pending = write - read;
                if (pending == 0)
                {
                    read = 0;
                    write = 0;
                    _cursorResets++;
                }
                else
                {
                    Buffer.BlockCopy(buffer, read, buffer, 0, pending);
                    read = 0;
                    write = pending;
                    _compactions++;
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(buffer, write, LengthPrefixSize), (uint)payloadLength);
            payload.CopyTo(new Span<byte>(buffer, write + LengthPrefixSize, payloadLength));

            write += frame;
            if (frame > _largestFrame) _largestFrame = frame;
            return frame;
        }

        static bool TryReadFrame(byte[] buffer, int read, int write, ref int newRead, out ReadOnlySequence<byte> payload)
        {
            payload = default;
            int pending = write - read;
            if (pending < LengthPrefixSize) return false;

            int declared = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                new ReadOnlySpan<byte>(buffer, read, LengthPrefixSize)));
            if (declared < 0 || declared > pending - LengthPrefixSize) return false;

            // Contiguous by construction: a frame is never split across the end of a lane, so this is
            // always a single segment and never a copy.
            int start = read + LengthPrefixSize;
            payload = new ReadOnlySequence<byte>(buffer, start, declared);
            newRead = start + declared;
            return true;
        }

        /// <summary>
        /// Next power of two at or above <paramref name="value"/>, without the overflow the BCL helper
        /// throws on near-<c>int.MaxValue</c>. A lane that big is not a thing, and a transport that
        /// cannot size its own lane should report that rather than an
        /// <see cref="ArgumentOutOfRangeException"/> from inside a send.
        /// </summary>
        static int RoundUpToPowerOfTwo(int value)
        {
            if (value <= 1) return 1;
            if (value > (1 << 30)) return value;
            return (int)BitOperations.RoundUpToPowerOf2((uint)value);
        }
    }

    /// <summary>
    /// A frame that does not fit. Its own type so a caller draining under back pressure can catch it
    /// without catching every <see cref="InvalidOperationException"/> in the loop, and so a mis-sized
    /// frame is never silently dropped.
    /// </summary>
    public sealed class TransportFullException : InvalidOperationException
    {
        public TransportFullException(string message) : base(message) { }
    }
}
