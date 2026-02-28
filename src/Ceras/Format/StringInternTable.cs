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
        readonly List<string> _indexToString = new() { null };

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
            uint index = SerializerBinary.ReadUInt32(ref reader);
            if (index >= (uint)table.Length) throw new InvalidOperationException("String index out of range");
            return table[index];
        }

        public static string[] Load(byte[] data, int offset, int length, StringTableCompression compression = StringTableCompression.Disabled)
        {
            ReadOnlySequence<byte> sequence;

            if (compression == StringTableCompression.Deflate)
            {
                using var ms = new MemoryStream(data, offset, length);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var decompressedMs = new MemoryStream();
                deflate.CopyTo(decompressedMs);
                var decompressed = decompressedMs.ToArray();
                sequence = new ReadOnlySequence<byte>(decompressed);
            }
            else
            {
                sequence = new ReadOnlySequence<byte>(data, offset, length);
            }

            var reader = new SequenceReader<byte>(sequence);
            uint count = SchemaSection.ReadLeb128(ref reader);
            
            var table = new string[count + 1];
            table[0] = null;
            
            for (uint i = 1; i <= count; i++)
            {
                string current = SchemaSection.ReadInlineString(ref reader);
                table[i] = current;
            }
            
            return table;
        }
    }
}
