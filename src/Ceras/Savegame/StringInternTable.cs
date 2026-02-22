using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace Ceras.Savegame
{
    sealed class StringInternTable
    {
        readonly Dictionary<string, int> _stringToIndex = new(StringComparer.Ordinal);
        readonly List<string> _indexToString = new() { null };

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
