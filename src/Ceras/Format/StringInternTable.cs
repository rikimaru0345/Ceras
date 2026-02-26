using System;
using System.Buffers;
using System.Collections.Generic;
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

        public int Flush(ref byte[] buffer, ref int offset)
        {
            var writer = new ArrayBufferWriter<byte>();
            int count = _indexToString.Count - 1;
            SchemaSection.WriteLeb128(writer, (uint)count);

            string last = "";
            for (int i = 1; i <= count; i++)
            {
                string current = _indexToString[i] ?? "";
                
                // 1. Find common prefix length
                int commonLength = 0;
                int minLen = Math.Min(last.Length, current.Length);
                while (commonLength < minLen && last[commonLength] == current[commonLength])
                    commonLength++;

                // 2. Write (PrefixLength, Suffix)
                SchemaSection.WriteLeb128(writer, (uint)commonLength);
                string suffix = current.Substring(commonLength);
                SchemaSection.WriteInlineString(writer, suffix);

                last = current;
            }

            var data = writer.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            int start = offset;
            offset += data.Length;
            return start;
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

        public static string[] Load(byte[] data, int offset)
        {
            var sequence = new ReadOnlySequence<byte>(data, offset, data.Length - offset);
            var reader = new SequenceReader<byte>(sequence);
            uint count = SchemaSection.ReadLeb128(ref reader);
            
            var table = new string[count + 1];
            table[0] = null;
            
            string last = "";
            for (uint i = 1; i <= count; i++)
            {
                uint commonLength = SchemaSection.ReadLeb128(ref reader);
                string suffix = SchemaSection.ReadInlineString(ref reader);
                
                if (commonLength > last.Length)
                    throw new InvalidOperationException("Invalid Front Compression data: commonLength > last.Length");
                
                string current = last.Substring(0, (int)commonLength) + suffix;
                table[i] = current;
                last = current;
            }
            
            return table;
        }
    }
}
