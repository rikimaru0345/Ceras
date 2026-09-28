using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Ceras.Format
{
    public sealed class StringInternTable
    {
        readonly Dictionary<string, int> _stringToIndex = new(StringComparer.Ordinal);
        // Index 0 is the null sentinel: GetIndex(null) hands it back, nothing is ever written for it,
        // and a decoded table carries the same sentinel at [0] (StringTable.Load below).
        readonly List<string> _indexToString = new() { null! };

        public int Count => _indexToString.Count - 1;

        /// <summary>
        /// Empties the table so the instance can encode another operation's strings.
        /// <para>
        /// <see cref="Dictionary{TKey, TValue}.Clear"/> and <see cref="List{T}.Clear"/> both keep
        /// the arrays they have already grown, so a reused table stops re-walking the growth ladder
        /// (3, 7, 17, 37, 71, ... entries, each one a fresh pair of arrays plus a rehash) on every
        /// save. A table that is thrown away per operation pays that ladder every time, which for a
        /// graph with a lot of distinct strings is a large part of what writing it costs.
        /// </para>
        /// <para>
        /// The null sentinel at index 0 is restored, so <see cref="GetIndex"/> keeps answering 0 for
        /// null and the next interned string gets 1 - the same numbering a fresh table starts from,
        /// which is what <see cref="Flush"/> writes and what the reader expects.
        /// </para>
        /// </summary>
        public void Clear()
        {
            _stringToIndex.Clear();
            _indexToString.Clear();
            _indexToString.Add(null!);
        }

        public int Intern(string s) => GetIndex(s);

        public int GetIndex(string s)
        {
            if (s == null) return 0;
            if (_stringToIndex.TryGetValue(s, out int index)) return index;
            index = _indexToString.Count;
            _stringToIndex.Add(s, index);
            _indexToString.Add(s);
            return index;
        }

        /// <summary>
        /// Writes the interned strings (and, compressed, the count that precedes them) into
        /// <paramref name="buffer"/>, and returns the offset the section started at.
        /// </summary>
        /// <param name="buffer">The destination. Replaced through this parameter if it has to grow.</param>
        /// <param name="offset">Where the section starts; advanced past what was written.</param>
        /// <param name="compression">Deflate the section, or write it as-is.</param>
        /// <param name="bufferIsPooled">
        /// True only if <paramref name="buffer"/> came from <see cref="CerasBufferPool.Rent"/>, so that
        /// growing it may return the replaced array. False for a caller's own array - the schema bake
        /// flushes into a plain <c>new byte[256]</c>, for instance. See
        /// <see cref="SerializerBinary.EnsureCapacity"/> for why that has to be stated.
        /// </param>
        public int Flush(ref byte[] buffer, ref int offset, StringTableCompression compression = StringTableCompression.Disabled, bool bufferIsPooled = false)
        {
            var writer = new ArrayBufferWriter<byte>();
            int count = _indexToString.Count - 1;
            SchemaSection.WriteLeb128(writer, (uint)count);

            for (int i = 1; i <= count; i++)
            {
                string current = _indexToString[i] ?? "";
                SchemaSection.WriteInlineString(writer, current);
            }

            var data = writer.WrittenSpan;

            if (compression == StringTableCompression.Deflate)
            {
                using var ms = new MemoryStream();
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
                {
                    deflate.Write(data);
                }
                var compressed = ms.ToArray();
                SerializerBinary.EnsureCapacity(ref buffer, offset, compressed.Length, bufferIsPooled);
                compressed.CopyTo(buffer.AsSpan(offset));
                int start = offset;
                offset += compressed.Length;
                return start;
            }
            else
            {
                SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length, bufferIsPooled);
                data.CopyTo(buffer.AsSpan(offset));
                int start = offset;
                offset += data.Length;
                return start;
            }
        }
    }

    public static class StringTable
    {
        public static string ReadString(ref SequenceReader<byte> reader, string[] table)
        {
            // A string-table index is a fixed 4-byte field in both regions (payload: written by
            // RobustStringFormatter; schema: written by SchemaSection.WriteInternedString).
            uint index = SerializerBinary.ReadUInt32Fixed(ref reader);
            if (index >= (uint)table.Length)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: string-table index {index} is outside the table ({table.Length} entries).");
            return table[index];
        }

        /// <summary>
        /// Smallest scratch block the bounded Deflate expansion reads through. Not a cap: a larger
        /// table is read over as many iterations as it needs. This is only the size of the first
        /// request, and it is pooled, so a save whose string table is a few hundred bytes no longer
        /// allocates (and discards) 64 KB to read them.
        /// </summary>
        const int DeflateReadBlockFloor = 512;

        /// <summary>
        /// Decodes a string-table region. <paramref name="maxStringLength"/> bounds each entry and
        /// <paramref name="maxDecompressedSize"/> bounds the Deflate output, so neither an entry length
        /// nor a small compressed region can pick an unbounded allocation. Both are checked before the
        /// allocation they bound.
        /// </summary>
        public static string[] Load(byte[] data, int offset, int length,
            StringTableCompression compression = StringTableCompression.Disabled,
            uint maxStringLength = uint.MaxValue,
            long maxDecompressedSize = long.MaxValue)
        {
            if (offset < 0 || length < 0 || (long)offset + length > data.Length)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt string table: region [{offset}, {offset + length}) is outside the data ({data.Length} bytes).");

            ReadOnlySequence<byte> sequence;

            if (compression == StringTableCompression.Deflate)
            {
                // Bounded expansion: DeflateStream.CopyTo would take an unbounded output buffer, so a
                // small compressed region could expand until the process died (a zip bomb). The
                // expansion is therefore read in bounded blocks and stopped as soon as the cap is
                // passed.
                //
                // The block is pooled and its size is a floor on the *first* read rather than a
                // fixed 64 KB: a 64 KB scratch buffer allocated per load was two thirds of everything
                // loading a save allocated, thrown away immediately, for a string table that is
                // typically a few hundred bytes. ReadByte reads one byte at a time and its own
                // buffer is only the slack the stream returns per call, so a small block costs
                // nothing and a large table simply takes more iterations.
                var block = CerasBufferPool.Rent(DeflateReadBlockFloor);
                try
                {
                    using var ms = new MemoryStream(data, offset, length);
                    using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                    using var decompressedMs = new MemoryStream();
                    long total = 0;
                    int read;
                    while ((read = deflate.Read(block, 0, block.Length)) > 0)
                    {
                        total += read;
                        if (total > maxDecompressedSize)
                            throw new Ceras.Exceptions.CerasDataCorruptionException(
                                $"Corrupt string table: the compressed region at [{offset}, {offset + length}) expands past the " +
                                $"configured limit of {maxDecompressedSize} bytes.");
                        decompressedMs.Write(block, 0, read);
                    }
                    sequence = new ReadOnlySequence<byte>(decompressedMs.ToArray());
                }
                catch (Ceras.Exceptions.CerasDataCorruptionException) { throw; }
                catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
                {
                    // The region does not hold a decompressible stream — a corrupt or mis-offset table.
                    // That is corruption, not a decoder bug, and it must not escape as a raw BCL exception.
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt string table: the region at [{offset}, {offset + length}) could not be decompressed " +
                        $"({e.GetType().Name}: {e.Message}).", e);
                }
                finally
                {
                    CerasBufferPool.Return(block);
                }
            }
            else
            {
                sequence = new ReadOnlySequence<byte>(data, offset, length);
            }

            var reader = new SequenceReader<byte>(sequence);
            uint count = SchemaSection.ReadLeb128(ref reader);

            // Every entry is at least one leb128 length byte, so a count larger than the remaining bytes
            // is corrupt. Reject before allocating (see ProtocolValidationTests).
            if (count > (uint)sequence.Length)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt string table: declared {count} entries but only {sequence.Length} bytes remain.");

            var table = new string[count + 1];
            table[0] = null!;

            // Bounded per entry, so one declared length cannot pick the allocation.
            int maxEntryBytes = maxStringLength > int.MaxValue ? int.MaxValue : (int)maxStringLength;
            for (uint i = 1; i <= count; i++)
            {
                string current = SchemaSection.ReadInlineString(ref reader, maxEntryBytes);
                table[i] = current;
            }

            return table;
        }
    }
}
