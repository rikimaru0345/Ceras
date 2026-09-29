using System;
using System.Buffers;
using System.Buffers.Binary;
using Ceras.Exceptions;
using Ceras.Helpers;

namespace Ceras.Format
{
    /// <summary>
    /// Reads a whole robust save into an <see cref="OldDataGraph"/> with no current model in scope: the
    /// file's own header, its own schema section, its own two string tables and its own bytes, and
    /// nothing else. It is the file-only counterpart of <see cref="CerasRobustSerializer"/>'s
    /// <c>Load</c>, which maps the same section onto <em>this build's</em> model and therefore cannot read
    /// a save whose types, members or shapes this build no longer has.
    /// <para>
    /// The order is the envelope order the format specifies and the one <c>Load</c> follows: magic and
    /// version, then the section offsets, then the CRC32 over the whole block <em>before</em> any field it
    /// protects is interpreted, then the two string regions and the schema section, and only then the data
    /// section. A corrupted build-timestamp byte is therefore reported as corruption rather than as
    /// "produced by a newer build", exactly as on the load path.
    /// </para>
    /// <para>
    /// <b>What it deliberately does not do.</b> It does not refuse a save produced by a newer build, and it
    /// does not compare the stored layout fingerprint with anything: both are questions about <em>this</em>
    /// build — its producer timestamp and its declared layout — and a decoder that holds no model has no
    /// opinion about either. The fingerprint is read and not used, and that is a decision rather than an
    /// omission. What it does insist on is the file's own integrity: the CRC32, the version, the section
    /// offsets, the structural invariant for every described entry, and the data section being consumed
    /// exactly.
    /// </para>
    /// </summary>
    static class OldFileGraph
    {
        /// <summary>Reads a save with the library's own unbounded limits.</summary>
        public static OldDataGraph Read(byte[] data) => Read(data, OldFileGraphLimits.Default);

        /// <summary>
        /// Reads a save into a graph, or refuses it.
        /// </summary>
        /// <exception cref="CerasVersionMismatchException">The file is not a version this build reads.</exception>
        /// <exception cref="CerasDataCorruptionException">
        /// The header does not verify, the section contradicts itself, or the payload does not end where
        /// the header says it does.
        /// </exception>
        /// <exception cref="CerasSchemaException">
        /// The file records a value this graph has no node kind for yet.
        /// </exception>
        public static OldDataGraph Read(byte[] data, OldFileGraphLimits limits)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            RobustHeader.ValidateMagicAndVersion(data, CerasRobustSerializer.FormatVersion);
            RobustHeader.Validate(data);

            // Integrity before interpretation, for the reason the load path gives: the checksum covers
            // the whole block, so a field it protects cannot be trusted — and cannot be reported about —
            // until it matches.
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(RobustHeader.ChecksumOffset));
            uint computed = HashHelpers.CalculateCrc32WithZeroedRange(data, RobustHeader.ChecksumOffset, RobustHeader.ChecksumSize);
            if (stored != computed)
                throw new CerasDataCorruptionException(
                    $"CRC mismatch: the saved data is corrupted or truncated (stored 0x{stored:X8}, computed 0x{computed:X8}). The header " +
                    "fields cannot be trusted until the checksum matches.");

            RobustHeader.Read(data, out _, out byte flags, out long schemaStringOffsetL, out long dataSectionOffset, out long stringTableOffset, out _);

            if (!RobustHeader.IsValidCompressionFlag(flags))
                throw new CerasDataCorruptionException($"Corrupt robust header: unknown string-table compression flag {flags}.");

            int schemaStringOffset = (int)schemaStringOffsetL;

            // Two regions, two tables, and the two are independent: the schema strings are never
            // compressed whatever the header's flag says, and the payload strings are compressed exactly
            // as the flag says. Both are bounded before they are decoded.
            var schemaStrings = StringTable.Load(data, schemaStringOffset, (int)dataSectionOffset - schemaStringOffset,
                StringTableCompression.Disabled, limits.MaxStringLength, limits.MaxStringTableSize);

            var payloadStrings = StringTable.Load(data, (int)stringTableOffset, data.Length - (int)stringTableOffset,
                (StringTableCompression)flags, limits.MaxStringLength, limits.MaxStringTableSize);

            int schemaOffset = RobustHeader.Size;
            var fileSchemas = SchemaSection.Read(data, ref schemaOffset, schemaStrings);

            // A view of its own per build: the Type-value grammar interns the entries it synthesizes into
            // the view it is handed, and a second builder over the same one would be refused as colliding
            // with its own ids. The caller's dictionary is not touched by that interning, so nothing else
            // is copied.
            var builder = new OldDataGraphBuilder(new FileSchemaView(fileSchemas), payloadStrings, limits);

            long declaredLength = stringTableOffset - dataSectionOffset;
            var sequence = new ReadOnlySequence<byte>(data, (int)dataSectionOffset, (int)declaredLength);
            var reader = new SequenceReader<byte>(sequence);

            return builder.Build(ref reader, declaredLength);
        }
    }
}
