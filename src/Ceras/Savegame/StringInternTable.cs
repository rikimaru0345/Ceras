using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Ceras.Savegame
{
    /// <summary>
    /// Write-side string intern table for the Ceras savegame format.
    ///
    /// All strings that appear in the data section are interned here during
    /// serialization. A 4-byte fixed-width LE index is written inline wherever
    /// a string value occurs; the full string table is flushed once at the end
    /// of the file (its offset is recorded in the file header).
    ///
    /// Index 0 is permanently reserved for <c>null</c> — it is never stored in
    /// the table and never passed to <see cref="Flush"/>. Actual string entries
    /// are 1-based.
    /// </summary>
    public sealed class StringInternTable
    {
        readonly Dictionary<string, int> _map     = new(StringComparer.Ordinal);
        readonly List<string>            _strings = new();

        /// <summary>Number of interned (non-null) strings.</summary>
        public int Count => _strings.Count;

        // ── Intern ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the 1-based index for <paramref name="s"/>, inserting it
        /// if not already present. Returns 0 for <c>null</c>.
        /// </summary>
        public int Intern(string s)
        {
            if (s is null)
                return 0;

            if (_map.TryGetValue(s, out int idx))
                return idx;

            idx = _strings.Count + 1; // 1-based
            _strings.Add(s);
            _map[s] = idx;
            return idx;
        }

        // ── Write helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Interns <paramref name="s"/> and writes a 4-byte LE index into
        /// <paramref name="buffer"/> at <paramref name="offset"/>.
        /// </summary>
        public void WriteIndex(ref byte[] buffer, ref int offset, string s)
        {
            int idx = Intern(s);
            EnsureCapacity(ref buffer, offset + 4);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), idx);
            offset += 4;
        }

        // ── Flush ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the string table block to <paramref name="buffer"/> at
        /// <paramref name="offset"/> and returns the byte offset at which the
        /// block starts (for storage in the file header).
        ///
        /// Format:
        /// <code>
        ///   string_count : LEB128
        ///   for each string:
        ///     byte_length : LEB128
        ///     utf8_bytes  : byte[byte_length]
        /// </code>
        /// </summary>
        public int Flush(ref byte[] buffer, ref int offset)
        {
            int tableStart = offset;

            SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)_strings.Count);

            foreach (var s in _strings)
            {
                int byteCount = Encoding.UTF8.GetByteCount(s);
                SchemaSection.WriteLeb128(ref buffer, ref offset, (uint)byteCount);
                EnsureCapacity(ref buffer, offset + byteCount);
                Encoding.UTF8.GetBytes(s, 0, s.Length, buffer, offset);
                offset += byteCount;
            }

            return tableStart;
        }

        // ── Buffer growth ─────────────────────────────────────────────────────

        static void EnsureCapacity(ref byte[] buffer, int required)
        {
            if (required <= buffer.Length)
                return;
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, required + 64));
        }
    }

    /// <summary>
    /// Read-side string table for the Ceras savegame format.
    ///
    /// Call <see cref="Load"/> once per save file before reading the data
    /// section. The returned array is 1-based (index 0 = <c>null</c>).
    /// Use <see cref="ReadString"/> to consume 4-byte inline indices from the
    /// data stream.
    /// </summary>
    public static class StringTable
    {
        // ── Load ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Deserializes the string table block from <paramref name="buffer"/>
        /// starting at <paramref name="tableOffset"/> (absolute byte position).
        ///
        /// Returns a 1-based <c>string[]</c> where element 0 is <c>null</c>
        /// and elements 1..N are the interned strings in intern order.
        /// </summary>
        public static string[] Load(byte[] buffer, int tableOffset)
        {
            int  offset = tableOffset;
            uint count  = SchemaSection.ReadLeb128(buffer, ref offset);

            var table = new string[count + 1]; // index 0 = null
            for (uint i = 0; i < count; i++)
            {
                uint byteLen = SchemaSection.ReadLeb128(buffer, ref offset);
                table[i + 1] = Encoding.UTF8.GetString(buffer, offset, (int)byteLen);
                offset += (int)byteLen;
            }

            return table;
        }

        // ── Read helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Reads a 4-byte LE string-table index from <paramref name="buffer"/>
        /// at <paramref name="offset"/> and returns the corresponding string.
        /// Advances <paramref name="offset"/> by 4.
        /// </summary>
        public static string ReadString(byte[] buffer, ref int offset, string[] table)
        {
            int idx = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            offset += 4;

            if ((uint)idx >= (uint)table.Length)
                throw new InvalidOperationException(
                    $"String table index {idx} is out of range " +
                    $"(table has {table.Length - 1} entries).");

            return table[idx]; // table[0] = null
        }
    }
}
