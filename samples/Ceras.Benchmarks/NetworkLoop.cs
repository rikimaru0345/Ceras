using System;
using System.Buffers;
using Ceras.GameplayLoop;

namespace Ceras.Benchmarks
{
    // ==================================================================================================
    // The gameplay message loop: a client and a server, two separate transient serializers, and a
    // bounded in-memory duplex transport between them.
    //
    // The messages are deliberately the two shapes a real game sends every frame, so the numbers mean
    // something:
    //
    //   * PlayerInput / ServerState are fixed-size structs - the input sample a client sends at its
    //     tick rate and the authoritative state a server answers with. Every member is a fixed-width
    //     primitive, so the encoded size is a compile-time constant and there is nothing on the wire
    //     whose length has to be discovered. This is the case that *can* be allocation-free, and it is
    //     the only case the zero-B/op row is about.
    //   * ChatLine is a reference type carrying a string, measured as a second, separate case. See
    //     the type's own note for why its allocations are reported rather than designed away.
    //
    // The models are declared here, in this project's own "CerasBenchmarks" context, rather than
    // shared with tests/Ceras.Test. A Ceras context is a per-compilation artifact: two
    // generator-enabled assemblies cannot both emit Ceras.Generated.CerasConfigExtensions (CS0121), so
    // a message type compiled into both could only be used by one of them. The transport and the
    // measurement helper *are* shared (shared/GameplayLoop) because they carry no context.
    // ==================================================================================================

    /// <summary>
    /// A client's input sample for one tick. Fixed-size: 24 bytes encoded, no reference members, no
    /// null flag (a struct has no null to flag), and nothing whose encoded length varies.
    /// </summary>
    [CerasContext(BenchContext.Name)]
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
    /// The authoritative answer to one <see cref="PlayerInput"/>: which input the server actually
    /// applied, where it thinks the player is, and the tick it applied it on. Fixed-size, 25 bytes
    /// encoded. Carries the client's sequence back so the client can match the answer to its request -
    /// without that echo this loop could not be checked for correctness, only for speed.
    /// </summary>
    [CerasContext(BenchContext.Name)]
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
    /// A reference-typed message with a string, measured honestly rather than optimistically. The
    /// client holds one instance and rewrites its <see cref="Sequence"/>, so the *encode* side has a
    /// reused object and a reused string; the *decode* side cannot have either, because the bytes name
    /// a string rather than pointing at one.
    /// </summary>
    [CerasContext(BenchContext.Name)]
    [CerasSerializable]
    public class ChatLine
    {
        public int Sequence;
        public string Text;
    }

    /// <summary>
    /// The loop itself: two transient serializers (one per endpoint, never shared), two pooled write
    /// buffers (one per endpoint, each rented once and returned once), and a bounded duplex transport
    /// that allocates nothing per frame.
    /// <para>
    /// Every stage is a separate public method so the benchmark can time encode, decode, transport and
    /// the full roundtrip independently, and so the transport-only row is genuinely transport-only: it
    /// moves the very bytes the real exchange moves, with no serialization in the path at all.
    /// </para>
    /// <para>
    /// Both directions really deserialize and really check what arrived, against a value that changes
    /// every exchange. A loop that encoded a constant and decoded into a discarded local would report
    /// the same nanoseconds and the same zero bytes while measuring nothing, so the decoded sequence
    /// is compared against the one that was sent, the decoded position against the one that was moved,
    /// and the running result of that comparison is left in <see cref="Failures"/> and
    /// <see cref="Checksum"/> for the caller to read. A mismatch is counted, never thrown: a throw
    /// inside a timed loop would end the measurement instead of reporting it.
    /// </para>
    /// <para>
    /// <b>Disposal is not optional.</b> The two write buffers are <see cref="CerasBufferPool"/>
    /// rentals, and the pool refuses a return it did not hand out and refuses a second return of the
    /// same array. <see cref="Dispose"/> returns each exactly once, and the loop never touches a
    /// buffer it has already handed back.
    /// </para>
    /// </summary>
    public sealed class GameplayLoopHarness : IDisposable
    {
        /// <summary>Fixed for the whole run, so a wrong PlayerId shows up as a mismatch.</summary>
        public const int PlayerId = 7;

        /// <summary>The client's serializer. Separate from <see cref="_server"/> on purpose.</summary>
        readonly CerasFastSerializer _client;

        /// <summary>The server's serializer. See <see cref="_client"/>.</summary>
        readonly CerasFastSerializer _server;

        /// <summary>The link between them. Two bounded byte lanes and nothing else.</summary>
        public FakeDuplexTransport Transport { get; }

        // One pooled write buffer per endpoint, rented in the constructor and returned in Dispose.
        // Two separate buffers because the two endpoints encode concurrently in a real process; a shared
        // one would be a data race that a single-threaded harness would simply not have. Neither is
        // readonly: the pooled write path hands a grown-out array back through the `ref`, and the array
        // to return is always the one the field now holds.
        byte[] _clientWrite;
        byte[] _serverWrite;
        int _clientOffset;
        int _serverOffset;

        // Reused decode targets, so "decode" is the cost of reading a message rather than the cost of
        // allocating one. The string case deliberately also reports the fresh-target number. The chat
        // instances are not readonly: reusing a decode target means handing the serializer a `ref` to
        // the very field it overwrites.
        PlayerInput _serverInput;
        ServerState _clientState;
        ChatLine _clientChat = new ChatLine { Text = "spawn" };
        ChatLine _serverChat = new ChatLine { Text = "spawn" };

        // Pre-encoded payloads, produced once, for the rows that measure one stage in isolation.
        byte[] _encodedInput;
        byte[] _encodedState;
        byte[] _encodedChat;

        int _sequence;
        long _failures;
        long _checksum;

        /// <summary>Frames exchanged so far, in one direction only.</summary>
        public int Sequence => _sequence;

        /// <summary>
        /// Checks that failed. Zero is the only acceptable value, and it is checked by the caller
        /// rather than asserted here, so the benchmark prints it and the test asserts on it.
        /// </summary>
        public long Failures => _failures;

        /// <summary>
        /// A running total over everything the loop decoded and verified. It exists so that a JIT
        /// cannot decide the decode results are unused and delete the work: it is read after the run,
        /// and its value depends on every exchange's contents.
        /// </summary>
        public long Checksum => _checksum;

        /// <summary>The client's encoder.</summary>
        public CerasFastSerializer Client => _client;

        /// <summary>The server's encoder. A different instance from <see cref="Client"/>.</summary>
        public CerasFastSerializer Server => _server;

        /// <summary>The client's pooled write buffer. A live rental for as long as the harness is.</summary>
        public byte[] ClientWriteBuffer => _clientWrite;

        /// <summary>The server's pooled write buffer. A separate rental from the client's.</summary>
        public byte[] ServerWriteBuffer => _serverWrite;

        public GameplayLoopHarness(int transportCapacityPerDirection = FakeDuplexTransport.DefaultCapacityPerDirection,
                                   int transportMaximumPayloadBytes = FakeDuplexTransport.DefaultMaximumPayloadBytes,
                                   string chatText = "spawn")
        {
            // Two instances, not one. A configured CerasSerializer owns mutable per-operation state
            // (formatter table, object cache, type cache, spare writers), so sharing one between two
            // endpoints would not be a "saves a little setup" optimisation - it would be two writers
            // into the same per-operation state. Two constructions is also what a real client and a
            // real server do.
            _client = BenchData.Fast();
            _server = BenchData.Fast();
            if (ReferenceEquals(_client, _server))
                throw new InvalidOperationException("the client and the server must not share a serializer instance");

            Transport = new FakeDuplexTransport(transportCapacityPerDirection, transportMaximumPayloadBytes);

            _clientWrite = CerasBufferPool.Rent(CerasSerializer.DefaultBufferSize);
            _serverWrite = CerasBufferPool.Rent(CerasSerializer.DefaultBufferSize);

            _clientChat.Text = chatText;
            _serverChat.Text = chatText;

            // The pre-encoded payloads are produced with a throwaway rental so the steady-state
            // buffers stay exactly as big as the loop needs them.
            _encodedInput = EncodeOnce(_client, new PlayerInput { Sequence = 1, PlayerId = PlayerId, Buttons = 1, PosX = 1, PosY = 2, PosZ = 3 });
            _encodedState = EncodeOnce(_server, new ServerState { Sequence = 1, LastAppliedInput = 1, ServerTick = 1, PosX = 0.5f, PosY = 1, PosZ = 1.5f, Accepted = true });
            _encodedChat = EncodeOnce(_client, new ChatLine { Sequence = 1, Text = chatText });
        }

        /// <summary>Encoded size of one <see cref="PlayerInput"/>, prefix excluded.</summary>
        public int EncodedInputLength => _encodedInput.Length;

        /// <summary>Encoded size of one <see cref="ServerState"/>, prefix excluded.</summary>
        public int EncodedStateLength => _encodedState.Length;

        /// <summary>Encoded size of one <see cref="ChatLine"/>, prefix excluded.</summary>
        public int EncodedChatLength => _encodedChat.Length;

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

        // ── the full exchange ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// One complete client/server roundtrip, the shape a game runs once per tick: encode an input,
        /// frame it, move it, decode and verify it, apply it, encode the resulting state, frame it,
        /// move it, decode and verify that. The whole method allocates nothing.
        /// </summary>
        public void Exchange()
        {
            int sequence = ++_sequence;

            // ---- client -> server: the input sample ------------------------------------------------
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
            int inputFrame = Transport.SendToServer(new ReadOnlySpan<byte>(_clientWrite, 0, inputBytes));

            if (!Transport.TryReceiveOnServer(out ReadOnlySequence<byte> inputWire))
            {
                _failures++;
                return;
            }

            var inputReader = new SequenceReader<byte>(inputWire);
            _server.Deserialize(ref inputReader, ref _serverInput);
            if (inputReader.Remaining != 0) _failures++;   // framing said one length; the decoder ate another

            // The value changes every exchange and the decoder has to agree with it, so neither the
            // encode nor the decode can be optimized away.
            if (_serverInput.Sequence != sequence) _failures++;
            if (_serverInput.PlayerId != PlayerId) _failures++;
            if (_serverInput.Buttons != (sequence & 0xF)) _failures++;
            if (_serverInput.PosX != sequence) _failures++;
            if (_serverInput.PosY != sequence * 2f) _failures++;
            if (_serverInput.PosZ != sequence * 3f) _failures++;

            // ---- server -> client: the authoritative answer ----------------------------------------
            // "Authoritative" in a way that depends on what arrived, so a decode that returned
            // anything other than the input cannot produce a plausible state.
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
            int stateFrame = Transport.SendToClient(new ReadOnlySpan<byte>(_serverWrite, 0, stateBytes));

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

            _checksum += inputBytes + stateBytes + inputFrame + stateFrame
                         + _serverInput.Sequence + _clientState.LastAppliedInput;
        }

        // ── single-stage rows, so the table can attribute the cost ───────────────────────────────

        /// <summary>
        /// Transport only: frame and move the same payload sizes the real exchange moves, in both
        /// directions, with no serialization anywhere in the path. This is the floor the roundtrip is
        /// read against - everything above it is Ceras.
        /// </summary>
        public void TransportOnly()
        {
            int sequence = ++_sequence;

            Transport.SendToServer(_encodedInput);
            if (!Transport.TryReceiveOnServer(out ReadOnlySequence<byte> toServer)) { _failures++; return; }
            var serverReader = new SequenceReader<byte>(toServer);
            if (!serverReader.TryRead(out byte firstToServer)) _failures++;

            Transport.SendToClient(_encodedState);
            if (!Transport.TryReceiveOnClient(out ReadOnlySequence<byte> toClient)) { _failures++; return; }
            var clientReader = new SequenceReader<byte>(toClient);
            if (!clientReader.TryRead(out byte firstToClient)) _failures++;

            // The sequence advances even though nothing was serialized, so the two rows cover the
            // same number of iterations and stay comparable.
            _checksum += sequence + firstToServer + firstToClient;
        }

        /// <summary>Encode only: the client's pooled write of one input. No framing, no receive.</summary>
        public void EncodeInputOnly()
        {
            int sequence = ++_sequence;
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
            int written = _client.Serialize(input, ref _clientWrite, ref _clientOffset);

            // The bytes just written are read, so the encode cannot be skipped and a write that
            // produced nothing would be visible rather than free.
            _checksum += written + _clientWrite[0];
        }

        /// <summary>
        /// Decode only: the server reads one input out of the pre-encoded bytes. No encode, no
        /// transport - so whatever this costs is the reader and nothing else.
        /// </summary>
        public void DecodeInputOnly()
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(_encodedInput));
            _server.Deserialize(ref reader, ref _serverInput);
            if (reader.Remaining != 0) _failures++;

            _checksum += _serverInput.Sequence + _serverInput.PlayerId + (long)_serverInput.PosZ;
        }

        /// <summary>
        /// One direction of the real exchange: encode an input, frame it, move it, decode and verify
        /// it. The full <see cref="Exchange"/> is this plus the reply, so the difference between the
        /// two rows is the server's own half.
        /// </summary>
        public void ExchangeInputOnly()
        {
            int sequence = ++_sequence;
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
            int written = _client.Serialize(input, ref _clientWrite, ref _clientOffset);
            Transport.SendToServer(new ReadOnlySpan<byte>(_clientWrite, 0, written));

            if (!Transport.TryReceiveOnServer(out ReadOnlySequence<byte> wire)) { _failures++; return; }
            var reader = new SequenceReader<byte>(wire);
            _server.Deserialize(ref reader, ref _serverInput);
            if (reader.Remaining != 0) _failures++;

            if (_serverInput.Sequence != sequence) _failures++;
            if (_serverInput.Buttons != (sequence & 0xF)) _failures++;

            _checksum += written + _serverInput.Sequence;
        }

        // ── the string case, reported rather than optimised ─────────────────────────────────────

        /// <summary>
        /// Encode one chat line from an instance the client already holds. The object and its string
        /// are reused, so whatever this allocates is the encoder's, not the caller's.
        /// </summary>
        public void EncodeChatOnly()
        {
            int sequence = ++_sequence;
            _clientChat.Sequence = sequence;

            _clientOffset = 0;
            int written = _client.Serialize(_clientChat, ref _clientWrite, ref _clientOffset);
            _checksum += written + _clientWrite[0];
        }

        /// <summary>
        /// Decode one chat line into a fresh target - what a caller that does not hold a target pays.
        /// This allocates: the bytes name a string, so reading one produces a new string, and a fresh
        /// target is a fresh object. The number is the point of the row.
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
        /// Decode one chat line into a target the server already holds. Reusing the object is
        /// supported and does remove the object allocation; it cannot remove the string, because the
        /// decoded value replaces the field. This row exists so the difference between it and
        /// <see cref="DecodeChatIntoFreshTarget"/> is a measured one rather than an assumed one.
        /// </summary>
        public void DecodeChatIntoReusedTarget()
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(_encodedChat));
            _serverChat.Sequence = 0;
            _server.Deserialize(ref reader, ref _serverChat);
            if (_serverChat.Text == null) { _failures++; return; }

            _checksum += _serverChat.Sequence + _serverChat.Text.Length;
        }

        /// <summary>
        /// A full exchange of the reference-typed message: both directions encode from a reused
        /// instance and decode into a reused instance. Unlike <see cref="Exchange"/> this is expected to
        /// allocate, and the B/op column reports what it actually costs.
        /// </summary>
        public void ExchangeChat()
        {
            int sequence = ++_sequence;

            _clientChat.Sequence = sequence;
            _clientOffset = 0;
            int clientBytes = _client.Serialize(_clientChat, ref _clientWrite, ref _clientOffset);
            Transport.SendToServer(new ReadOnlySpan<byte>(_clientWrite, 0, clientBytes));

            if (!Transport.TryReceiveOnServer(out ReadOnlySequence<byte> toServer)) { _failures++; return; }
            var serverReader = new SequenceReader<byte>(toServer);
            _serverChat.Sequence = 0;
            _server.Deserialize(ref serverReader, ref _serverChat);
            if (_serverChat.Sequence != sequence) _failures++;

            _serverChat.Sequence = sequence;
            _serverOffset = 0;
            int serverBytes = _server.Serialize(_serverChat, ref _serverWrite, ref _serverOffset);
            Transport.SendToClient(new ReadOnlySpan<byte>(_serverWrite, 0, serverBytes));

            if (!Transport.TryReceiveOnClient(out ReadOnlySequence<byte> toClient)) { _failures++; return; }
            var clientReader = new SequenceReader<byte>(toClient);
            _clientChat.Sequence = 0;
            _client.Deserialize(ref clientReader, ref _clientChat);
            if (_clientChat.Sequence != sequence) _failures++;

            _checksum += clientBytes + serverBytes + _serverChat.Text.Length + _clientChat.Text.Length;
        }

        public void Dispose()
        {
            if (_clientWrite != null) { CerasBufferPool.Return(_clientWrite); _clientWrite = null; }
            if (_serverWrite != null) { CerasBufferPool.Return(_serverWrite); _serverWrite = null; }
        }
    }
}
