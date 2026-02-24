using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace Ceras.Format
{
    sealed class StringInternTable
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

        public void WriteIndex(ref byte[] buffer, ref int offset, string s)
        {
            int index = GetIndex(s);
            SerializerBinary.EnsureCapacity(ref buffer, offset, 4);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), index);
            offset += 4;
        }

        public int Flush(ref byte[] buffer, ref int offset)
        {
            var writer = new ArrayBufferWriter<byte>();
            SchemaSection.WriteLeb128(writer, (uint)_indexToString.Count - 1);
            for (int i = 1; i < _indexToString.Count; i++)
                SchemaSection.WriteInlineString(writer, _indexToString[i]);

            var data = writer.WrittenSpan;
            SerializerBinary.EnsureCapacity(ref buffer, offset, data.Length);
            data.CopyTo(buffer.AsSpan(offset));
            int start = offset;
            offset += data.Length;
            return start;
        }
    }

    static class StringTable
    {
        public static string ReadString(byte[] buffer, ref int offset, string[] table)
        {
            int index = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            offset += 4;
            if (index < 0 || index >= table.Length) throw new InvalidOperationException("String index out of range");
            return table[index];
        }

        public static string[] Load(byte[] data, int offset)
        {
            var sequence = new ReadOnlySequence<byte>(data, offset, data.Length - offset);
            var reader = new SequenceReader<byte>(sequence);
            uint count = SchemaSection.ReadLeb128(ref reader);
            var table = new string[count + 1];
            for (uint i = 1; i <= count; i++)
                table[i] = SchemaSection.ReadInlineString(ref reader);
            return table;
        }
    }
}
