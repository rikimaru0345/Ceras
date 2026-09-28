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

        public int Flush(ref byte[] buffer, ref int offset, StringTableCompression compression = StringTableCompression.Disabled)
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
                SerializerBinary.EnsureCapacity(ref buffer, offset, compressed.Length);
                compressed.CopyTo(buffer.AsSpan(offset));
                int start = offset;
                offset += compressed.Length;
                return start;
            }
            else
            {
                SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
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
                // small compressed region could expand until the process died (a zip bomb). Copy in
                // bounded blocks and stop as soon as the cap is passed.
                const int blockSize = 64 * 1024;
                try
                {
                    using var ms = new MemoryStream(data, offset, length);
                    using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                    using var decompressedMs = new MemoryStream();
                    var block = new byte[blockSize];
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
            }
            else
            {
                sequence = new ReadOnlySequence<byte>(data, offset, length);
            }

            var reader = new SequenceReader<byte>(sequence);
            uint count = SchemaSection.ReadLeb128(ref reader);

            // Every entry is at least one leb128 length byte, so a count larger than the remaining bytes
            // is corrupt. Reject before allocating (TODO 0.6 item 8).
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
