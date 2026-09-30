using System;
using System.IO;
using System.Buffers;
using System.Threading.Tasks;
using Ceras.Exceptions;

namespace Ceras.Helpers
{
    public static class CerasNetworkingExampleExtensions
    {
        /// <summary>
        /// Ceiling <see cref="ReadFromStream(CerasFastSerializer, Stream)"/> applies when the caller does
        /// not choose one: 1 MiB of payload per frame.
        /// <para>
        /// It bounds one thing: how large a receive buffer a peer can make this process allocate. The
        /// length prefix arrives before the payload, so with no ceiling the shortest possible frame - five
        /// bytes on the wire - is enough to ask for two gigabytes.
        /// </para>
        /// <para>
        /// It is <b>not</b> a claim that an untrusted payload is safe to deserialize. A frame inside the
        /// ceiling is still handed to the deserializer, and the value it describes can be as deep, as
        /// wide or as self-referential as the sender likes: nothing here bounds graph depth, collection
        /// size or total work, on this transient path or any other. Bound the frame; what is inside it is
        /// the separate question TODO.md's Milestone 4 leaves open.
        /// </para>
        /// </summary>
        public const int DefaultMaxPayloadBytes = 1 << 20;

        // Thread-static and null until this thread's first Serialize fills it (the buffer is handed
        // back by ref so it is reused); the annotation is the one RobustContext._current uses.
        [ThreadStatic] static byte[] _streamBuffer = null!;

        /// <summary>
        /// Writes <paramref name="obj"/> as a varint length prefix followed by its bytes - the framing
        /// <see cref="ReadFromStream(CerasFastSerializer, Stream)"/> reads.
        /// <para>
        /// Note the asymmetry a receiver imposes on this: that reader refuses a frame above
        /// <see cref="DefaultMaxPayloadBytes"/> (1 MiB) unless it was given a bigger ceiling. Writing
        /// something larger than that is therefore a frame no default reader will accept - which is the
        /// intended answer rather than a truncation, but it means the object and the ceiling have to be
        /// chosen together on both ends.
        /// </para>
        /// </summary>
        public static void WriteToStream(this CerasFastSerializer ceras, Stream stream, object obj)
        {
            // Serialize the object
            int size = ceras.Serialize<object>(obj, ref _streamBuffer);

            // Write size prefix
            var writer = new ArrayBufferWriter<byte>(5);
            SerializerBinary.WriteUInt32(writer, (uint)size);
            stream.Write(writer.WrittenSpan);

            // Write payload
            stream.Write(_streamBuffer, 0, size);
        }

        /// <summary>
        /// Reads a value written by <see cref="WriteToStream"/>, refusing any frame larger than
        /// <see cref="DefaultMaxPayloadBytes"/>, or null when the bytes hold the null marker.
        /// <para>
        /// This is the entry point for a caller who has no protocol opinion; it is the same read as
        /// <see cref="ReadFromStream(CerasFastSerializer, Stream, int)"/> with
        /// <see cref="DefaultMaxPayloadBytes"/> as the ceiling. Use the three-argument overload to say
        /// what your protocol actually allows.
        /// </para>
        /// <para>
        /// The null is a value, not a failure. The wire format encodes it, so a payload written from
        /// a null object reads back as null and the round trip preserves that rather than
        /// substituting a default - which is the same contract
        /// <see cref="CerasFastSerializer.Deserialize{T}(byte[])"/> itself has, and why its result
        /// is <c>MaybeNull</c>. A caller that needs an object checks for null (or passes a
        /// <c>byte[]</c> it knows is non-null by construction).
        /// </para>
        /// </summary>
        public static Task<object?> ReadFromStream(this CerasFastSerializer ceras, Stream stream)
        {
            // The null check the explicit-cap overload makes, minus the ceiling check - a constant cannot
            // fail it - so both entry points answer a null stream the same way.
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            return ceras.ReadFramedAsync(stream, DefaultMaxPayloadBytes);
        }

        /// <summary>
        /// Reads a value written by <see cref="WriteToStream"/>, refusing any frame whose declared
        /// payload exceeds <paramref name="maxPayloadBytes"/>.
        /// <para>
        /// The declared length is checked against the ceiling <em>before</em> anything is allocated, and
        /// it is compared as a <see cref="uint"/> - the type the prefix decodes to - so a peer cannot
        /// reach an allocation, a negative array length, or an arithmetic exception by sending a length
        /// near <see cref="int.MaxValue"/>. An over-ceiling frame is refused as
        /// <see cref="CerasDataCorruptionException"/>, the same shape the library reports every other
        /// "these bytes do not describe what they claim" refusal in, with the declared length and the
        /// ceiling in the message.
        /// </para>
        /// <para>
        /// A prefix or a payload that stops early is corruption for the same reason. The stream simply
        /// ended: there is no frame to hand back, and the frame that did start is not a value.
        /// </para>
        /// <para>
        /// The ceiling is validated here, at the call, rather than inside the asynchronous body: a cap
        /// that could never accept a frame is a mistake in the calling code, and should not arrive as a
        /// faulted task that a caller who forgot to <c>await</c> never sees.
        /// </para>
        /// </summary>
        public static Task<object?> ReadFromStream(this CerasFastSerializer ceras, Stream stream, int maxPayloadBytes)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (maxPayloadBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes), maxPayloadBytes,
                    "A ceiling that admits no payload at all is not a framing limit.");

            return ceras.ReadFramedAsync(stream, maxPayloadBytes);
        }

        /// <summary>
        /// The read both overloads share: one bounded frame off <paramref name="stream"/>, decoded.
        /// <para>
        /// It is an extension method so each public overload can name its own receiver - the two entry
        /// points then differ only in the ceiling they pass - and this stays the one place that knows
        /// what a frame is.
        /// </para>
        /// </summary>
        static async Task<object?> ReadFramedAsync(this CerasFastSerializer ceras, Stream stream, int maxPayloadBytes)
        {
            uint declared = await ReadVarIntFromStream(stream);

            // Compared before the cast, and as a uint, because the value is peer-supplied: int.MaxValue
            // and uint.MaxValue both arrive here looking like ordinary lengths. Casting first is what
            // turns this bound into an allocation - or, for a length above 2 GiB, into a negative one.
            if (declared > (uint)maxPayloadBytes)
                throw new CerasDataCorruptionException(
                    $"Corrupt data: a frame declares {declared} payload bytes, which exceeds the configured limit of {maxPayloadBytes} bytes.");

            // Safe now, and only now: the cast cannot wrap, because the value has already been shown to
            // fit an int.
            int length = (int)declared;
            var recvBuffer = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = await stream.ReadAsync(recvBuffer, totalRead, length - totalRead);
                if (read <= 0)
                    throw new CerasDataCorruptionException(
                        $"Corrupt data: a frame declares {length} payload bytes but the stream ended after {totalRead} (truncated payload).");
                totalRead += read;
            }
            return ceras.Deserialize<object>(recvBuffer);
        }

        /// <summary>
        /// The frame's length prefix: a varint of a 32-bit length, exactly as
        /// <see cref="SerializerBinary.WriteUInt32(IBufferWriter{byte}, uint)"/> writes it, read one
        /// byte at a time because the prefix has no known length up front.
        /// <para>
        /// Two rules, and both are about what the bytes may claim rather than about taste. A 32-bit
        /// value needs at most five bytes, so a sixth continuation is not a number this prefix can
        /// describe; and the fifth byte carries bits 28-31 only, so its high three bits must be clear.
        /// <c>FF FF FF FF 1F</c> denotes 0x1FFFFFFFF, which is not a length at all - and keeping the
        /// low 32 bits of it, which is exactly what the <c>(uint)</c> cast below does, answers
        /// <c>uint.MaxValue</c>: a four-gigabyte frame the sender never asked for, and a negative array
        /// length one cast later. Anything the prefix does not legitimately describe is corruption and
        /// is refused as such.
        /// </para>
        /// <para>
        /// Everything a legitimate uint encoding can say is still accepted, including the non-minimal
        /// ways of spelling a small value in five bytes: the cap below is what decides whether a frame
        /// is acceptable, and this only decides whether the length could exist.
        /// </para>
        /// </summary>
        static async Task<uint> ReadVarIntFromStream(Stream stream)
        {
            var buf = new byte[1];
            int shift = 0;
            ulong result = 0;
            while (true)
            {
                if (await stream.ReadAsync(buf, 0, 1) <= 0)
                    throw new CerasDataCorruptionException(
                        "Corrupt data: a frame's length prefix is truncated (the stream ended after " +
                        (shift == 0 ? "no bytes" : $"{shift / 7} bytes") + " of the prefix).");

                ulong b = buf[0];
                ulong bits = b & 0x7f;

                if (shift == 28)
                {
                    // The fifth and last byte of a 32-bit varint: four bits, 28 through 31.
                    if (bits > 0x0f)
                        throw new CerasDataCorruptionException(
                            $"Corrupt data: a frame's length prefix does not fit a 32-bit length (fifth byte 0x{buf[0]:X2} sets bits at or above 32).");
                    if ((b & 0x80) != 0)
                        throw new CerasDataCorruptionException(
                            "Corrupt data: a frame's length prefix is not a valid varint (at most 5 bytes for a 32-bit length).");
                }

                result |= bits << shift;
                if ((b & 0x80) == 0) return (uint)result;

                shift += 7;
            }
        }
    }
}
