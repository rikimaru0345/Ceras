using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Ceras.Exceptions;
using Ceras.Formatters;

namespace Ceras.Format
{
    /// <summary>
    /// The wire grammar of a user custom formatter's opaque payload, and the wording of the refusals a
    /// framed payload can raise.
    /// <para>
    /// The grammar is one line — <c>int32 length (little-endian, four fixed bytes) | length bytes of the
    /// formatter's own output</c> — and it lives here rather than inside the formatter that writes it
    /// because three unrelated places have to agree on it byte for byte: the writer that emits the
    /// block, the reader that consumes it, and the migration interpreter, which can only <em>skip</em> a
    /// block it cannot read. Three copies of a framing rule is three chances to desynchronize a save.
    /// </para>
    /// <para>
    /// <b>What the declared length is, and what it is not.</b> It accounts for bytes and nothing else.
    /// It cannot say what they mean, so it cannot make one formatter's payload readable by another —
    /// that is what the formatter's name in the schema entry and in the layout fingerprint is for — and
    /// it does not bound a payload either: a declared length larger than what is left in the file is
    /// corruption, which is the same answer a length larger than the buffer gives, so the frame needs
    /// no size limit of its own.
    /// </para>
    /// <para>
    /// See <c>Format/LAYOUT.md</c>, "A user custom formatter's payload is an opaque block", for the
    /// grammar in context and for what a refused custom payload looks like to a caller.
    /// </para>
    /// </summary>
    internal static class OpaquePayload
    {
        /// <summary>
        /// The width of the declared length: a fixed four-byte little-endian <c>int32</c>. Fixed rather
        /// than variable-width so that a skip is one read and one advance, on any path, including a
        /// reader that is looking at nothing but a length.
        /// </summary>
        public const int LengthPrefixSize = sizeof(int);

        /// <summary>
        /// The name a file records for a custom formatter: the canonical name of the formatter's own CLR
        /// type, by the same rule every other name in the schema section follows
        /// (<see cref="TypeIdentity.GetCanonicalName"/>) — namespace and name, with no assembly version,
        /// culture or public key token, so a file written by one build and read by another can agree on
        /// it.
        /// <para>
        /// The formatter's <em>type</em>, not the type it formats. The formatted type is already a fact
        /// of the file (it is the entry the manifest holds for that type), and recording it twice would
        /// say nothing a reader did not know; the formatter is the half the file cannot derive.
        /// </para>
        /// </summary>
        public static string FormatterToken(IFormatter formatter)
        {
            if (formatter == null) throw new ArgumentNullException(nameof(formatter));
            return TypeIdentity.GetCanonicalName(formatter.GetType());
        }

        /// <summary>
        /// Writes the declared length in front of a payload the caller has already produced. The four
        /// fixed bytes are <c>SerializerBinary</c>'s, like every other fixed-width field in the format.
        /// </summary>
        public static void WriteLength(IBufferWriter<byte> writer, int length)
            => SerializerBinary.WriteInt32Fixed(writer, length);

        /// <summary>
        /// Accounts for one framed block without interpreting it: read the declared length, advance
        /// exactly that far, and refuse a length the rest of the file cannot satisfy.
        /// <para>
        /// This is the whole reason the frame exists. A skipped value belongs to a formatter, so nothing
        /// here can make sense of it — but the file's own length says precisely how many bytes to
        /// advance, which is all a skip needs. A member the application has since removed, or a value
        /// of a type this build no longer has, therefore still leaves the stream aligned for everything
        /// after it. Whether the load may <em>proceed</em> is a separate question, asked by
        /// <c>SchemaTraversalEngine.CustomPayloadVerdict</c> before this is called: the bytes can be
        /// accounted for, and the value is still not being delivered to anything.
        /// </para>
        /// </summary>
        public static void SkipBlock(ref SequenceReader<byte> reader, string position)
        {
            int declared = ReadDeclaredLength(ref reader, position);
            RequireRemaining(ref reader, position, declared);
            reader.Advance(declared);
        }

        /// <summary>
        /// Reads the declared length and slices exactly that many bytes off the reader, returning a reader
        /// over the block alone. The caller's reader is left positioned at the first byte after the
        /// block whatever the caller does with the result, so a formatter that reads less than the block
        /// declares still cannot desynchronize the rest of the file: the excess is a diagnosed
        /// disagreement, not a hole in the stream.
        /// </summary>
        public static SequenceReader<byte> ReadBlock(ref SequenceReader<byte> reader, string position, out int declaredLength)
        {
            declaredLength = ReadDeclaredLength(ref reader, position);
            RequireRemaining(ref reader, position, declaredLength);

            var block = reader.Sequence.Slice(reader.Position, declaredLength);
            reader.Advance(declaredLength);
            return new SequenceReader<byte>(block);
        }

        /// <summary>
        /// The bound a declared length is checked against: the rest of the save. A block longer than that
        /// is corruption, which is the same answer a block longer than the buffer gives — so the frame
        /// needs no size limit of its own, and cannot be talked into allocating one by a hostile length.
        /// </summary>
        static void RequireRemaining(ref SequenceReader<byte> reader, string position, int declared)
        {
            if (reader.Remaining >= declared) return;
            throw Truncated(position, declared, reader.Remaining);
        }

        /// <summary>
        /// The other half of the frame's contract: a formatter that read fewer bytes than its block
        /// declares has disagreed with the length the writer wrote, which is corruption of the one thing
        /// the frame promises. It is checked rather than assumed, because the consequence of not
        /// checking is silent: the value comes back as whatever the formatter managed to build, and
        /// nothing in the file says so.
        /// </summary>
        public static void RequireBlockConsumed(ref SequenceReader<byte> blockReader, string position, int declaredLength)
        {
            if (blockReader.Remaining == 0) return;

            throw new CerasDataCorruptionException(
                $"Corrupt data: the custom formatter for {position} read {blockReader.Consumed} of the {declaredLength} bytes its block " +
                $"declares, and {blockReader.Remaining} bytes of the block are unaccounted for. The length in front of a custom payload is " +
                "written by the same build that wrote the payload, so the two cannot disagree unless the file is damaged. " +
                "Loading it further would hand back a value the formatter did not finish reading.");
        }

        /// <summary>
        /// The shared wording for "this file records a custom payload and this build cannot read it".
        /// <para>
        /// The message names the position, the formatter the file recorded and what this build has
        /// instead, because those three are what a caller can act on: a wrong formatter name is a
        /// renamed class, a different formatter is a changed encoding, and a missing type is a removed
        /// one. It then says what authorizing the loss does, so the way out is in the refusal rather
        /// than in the documentation.
        /// </para>
        /// <para>
        /// <paramref name="mayDrop"/> is asked rather than assumed so the two wordings cannot drift
        /// apart. Every refusal Ceras raises today arrives with it <c>false</c>, because being
        /// authorized to drop a custom payload is exactly what turns the refusal into a skip; it is a
        /// parameter so that a caller which reaches here having authorized the loss is described for
        /// what it is doing, instead of being handed the wording for a decision it did not make.
        /// </para>
        /// </summary>
        public static Exception DescribeUnreadable(string position, string recordedFormatter, string currentFormatter, bool mayDrop)
        {
            var sb = new StringBuilder();
            sb.Append("Cannot read ").Append(position)
              .Append(": the save records it as an opaque block written by the custom formatter '").Append(recordedFormatter)
              .Append("', and this build has ").Append(currentFormatter).AppendLine(" for that type.");
            sb.AppendLine();
            sb.AppendLine(
                "A custom formatter's payload is a declared length followed by the formatter's own bytes, so Ceras can account for those bytes");
            sb.AppendLine(
                "exactly and can skip them, but it cannot read them with a different formatter: the length says how many there are, never what");
            sb.AppendLine(
                "they mean. Nothing has been written and nothing has been skipped, so the load has not started — this is raised before the value");
            sb.AppendLine("is interpreted.");
            sb.AppendLine();
            if (mayDrop)
            {
                sb.AppendLine(
                    "This build authorized dropping custom payloads it cannot read, so the block is skipped by its declared length, the load");
                sb.AppendLine("continues, and the value is lost. A reference slot keeps the instance the reader allocated for it, so its members");
                sb.AppendLine("keep their defaults — the same thing a member added since the save gets.");
            }
            else
            {
                sb.AppendLine(
                    "To load this file anyway, set CerasRobustConfig.AllowDroppingCustomPayloads when you build the serializer (in its 'configure'");
                sb.AppendLine(
                    "callback, or on the config object before you hand it to one - the config is sealed as the serializer is constructed, so it cannot be changed");
                sb.AppendLine(
                    "afterwards): the block is then skipped by its declared length, the load continues, and the value is lost. A reference slot keeps the instance the");
                sb.AppendLine("reader allocated for it, so its members keep their defaults, and a member the file records is reported in MigrationContext.SkippedFields");
                sb.AppendLine("when the model no longer has it.");
            }
            sb.AppendLine();
            sb.AppendLine(
                "The other half is the formatter itself: a file records the formatter's full name, so renaming or moving that class changes what");
            sb.AppendLine(
                "the file's bytes are, and the load is refused rather than guessed at. Keep the class name and namespace stable across builds.");
            return new CerasSchemaException(sb.ToString());
        }

        /// <summary>
        /// What this build has for the type the file recorded an opaque block for, as one clause for
        /// <see cref="DescribeUnreadable"/>. Three answers, because "not readable" has three reasons and
        /// only the first two are the same problem: a different formatter, no formatter at all (the type
        /// is described by this build's manifest as usual), and no type (the file names one this build
        /// does not have, which is a removed type rather than a changed formatter).
        /// </summary>
        public static string DescribeCurrentFormatter(Type? currentType, string? formatterName)
        {
            if (formatterName != null)
                return $"the custom formatter '{formatterName}'";

            if (currentType == null)
                return "no such type at all — the type the file records is not in this build's manifest, so this build has nothing that could read those bytes";

            return $"no custom formatter for '{TypeIdentity.GetCanonicalName(currentType)}' (this build describes that type's members instead)";
        }

        static int ReadDeclaredLength(ref SequenceReader<byte> reader, string position)
        {
            Span<byte> prefix = stackalloc byte[LengthPrefixSize];
            if (!reader.TryCopyTo(prefix))
                throw new CerasDataCorruptionException(
                    $"Corrupt data: the save ended before the declared length of {position} could be read (truncated payload).");

            int declared = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            reader.Advance(LengthPrefixSize);

            if (declared < 0)
                throw new CerasDataCorruptionException(
                    $"Corrupt data: {position} declares a length of {declared} bytes. A declared length is written from the number of bytes the " +
                    "formatter produced, so it cannot be negative; the file is damaged.");

            return declared;
        }

        static CerasDataCorruptionException Truncated(string position, int declared, long remaining)
            => new CerasDataCorruptionException(
                $"Corrupt data: {position} declares a length of {declared} bytes but only {remaining} bytes are left in the save. The declared " +
                "length is the only thing that accounts for a custom payload, so a block the file cannot deliver cannot be skipped either, and " +
                "there is no way to find where the next value starts.");
    }
}
