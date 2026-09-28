namespace Ceras.Formatters
{
    using System;
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Writes a run of fixed-width primitive elements with one <see cref="IBufferWriter{T}"/>
    /// <c>GetSpan</c>/<c>Advance</c> pair per block instead of one per element.
    /// <para>
    /// Writing an array or list element by element costs an <c>IFormatter&lt;T&gt;</c> interface call, a
    /// <c>SerializerBinary</c> call, and two more interface calls for the span and the advance -
    /// about 2.4 ns per <see cref="int"/> measured. The bytes are the same little-endian fields
    /// either way, because these are exactly the ones the per-element formatters already write
    /// through <c>BinaryPrimitives.Write*LittleEndian</c>; the only difference is how many times the
    /// buffer is asked for room.
    /// </para>
    /// <para>
    /// Endianness is handled by construction rather than by a raw copy: every element goes through
    /// the same <c>Write*LittleEndian</c> call the single-value formatters use, so this is correct on
    /// a big-endian host and needs no platform check. <see cref="bool"/> is written as 0 or 1
    /// explicitly rather than copied, because nothing guarantees how a CLR represents a managed
    /// <c>bool</c> in memory.
    /// </para>
    /// <para>
    /// Only the types the wire format stores as a fixed-width field are handled. Everything else -
    /// including <c>decimal</c>, which is variable-width, and any struct - returns false so the
    /// caller falls back to the element-at-a-time path. <typeparamref name="T"/> is unconstrained
    /// because that is how the formatters declare their element type, so the reinterpretation goes
    /// through <see cref="Unsafe.As{TFrom, TTo}(ref TFrom)"/>; it is only reached for a primitive
    /// <see cref="IsFixedWidth{T}"/> has already accepted, where the widths match by construction.
    /// </para>
    /// </summary>
    static class FixedWidthBulkWriter
    {
        /// <summary>
        /// Ceiling on a single span request, so a huge array asks for room in bounded blocks instead
        /// of one span the size of the whole payload.
        /// </summary>
        const int MaxBlockBytes = 64 * 1024;

        /// <summary>
        /// Writes every element of <paramref name="values"/>. Returns false without writing anything
        /// when <typeparamref name="T"/> is not a fixed-width wire primitive, so the caller can fall
        /// back.
        /// </summary>
        public static bool TryWrite<T>(IBufferWriter<byte> writer, ReadOnlySpan<T> values)
        {
            if (typeof(T) == typeof(byte)) { WriteBlock<T, ByteWide>(writer, values, 1); return true; }
            if (typeof(T) == typeof(sbyte)) { WriteBlock<T, SByteWide>(writer, values, 1); return true; }
            if (typeof(T) == typeof(bool)) { WriteBlock<T, BoolWide>(writer, values, 1); return true; }

            if (typeof(T) == typeof(short)) { WriteBlock<T, Int16Wide>(writer, values, 2); return true; }
            if (typeof(T) == typeof(ushort)) { WriteBlock<T, UInt16Wide>(writer, values, 2); return true; }
            if (typeof(T) == typeof(char)) { WriteBlock<T, CharWide>(writer, values, 2); return true; }

            if (typeof(T) == typeof(int)) { WriteBlock<T, Int32Wide>(writer, values, 4); return true; }
            if (typeof(T) == typeof(uint)) { WriteBlock<T, UInt32Wide>(writer, values, 4); return true; }
            if (typeof(T) == typeof(float)) { WriteBlock<T, SingleWide>(writer, values, 4); return true; }

            if (typeof(T) == typeof(long)) { WriteBlock<T, Int64Wide>(writer, values, 8); return true; }
            if (typeof(T) == typeof(ulong)) { WriteBlock<T, UInt64Wide>(writer, values, 8); return true; }
            if (typeof(T) == typeof(double)) { WriteBlock<T, DoubleWide>(writer, values, 8); return true; }

            return false;
        }

        /// <summary>True when <see cref="TryWrite{T}"/> would handle <typeparamref name="T"/>.</summary>
        public static bool IsFixedWidth<T>()
        {
            var t = typeof(T);
            return t == typeof(byte) || t == typeof(sbyte) || t == typeof(bool)
                || t == typeof(short) || t == typeof(ushort) || t == typeof(char)
                || t == typeof(int) || t == typeof(uint) || t == typeof(float)
                || t == typeof(long) || t == typeof(ulong) || t == typeof(double);
        }

        static void WriteBlock<T, TWide>(IBufferWriter<byte> writer, ReadOnlySpan<T> values, int width)
            where TWide : struct, IWide
        {
            // Reinterpreted, not converted: T and TWide are the same width because the caller paired
            // them above and IsFixedWidth has already rejected everything else.
            var wide = MemoryMarshal.Cast<byte, TWide>(AsBytes(values, width));

            int perBlock = Math.Max(1, MaxBlockBytes / width);
            for (int start = 0; start < wide.Length; start += perBlock)
            {
                int count = Math.Min(perBlock, wide.Length - start);
                var destination = writer.GetSpan(count * width);
                for (int i = 0; i < count; i++)
                    wide[start + i].WriteTo(destination.Slice(i * width));
                writer.Advance(count * width);
            }
        }

        static ReadOnlySpan<byte> AsBytes<T>(ReadOnlySpan<T> values, int width)
        {
            if (values.Length == 0) return default;
            return MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)),
                values.Length * width);
        }

        /// <summary>
        /// Writes one element. A constrained call on a struct type parameter is devirtualized and
        /// inlined by the JIT, so this costs the same as a direct static call - which is why the
        /// element types are structs behind an interface rather than a delegate, whose indirect call
        /// would put back much of what the bulk path exists to save.
        /// </summary>
        interface IWide
        {
            void WriteTo(Span<byte> destination);
        }

        readonly struct ByteWide : IWide
        {
            readonly byte _value;
            public ByteWide(byte value) => _value = value;
            public void WriteTo(Span<byte> destination) => destination[0] = _value;
        }

        readonly struct SByteWide : IWide
        {
            readonly sbyte _value;
            public SByteWide(sbyte value) => _value = value;
            public void WriteTo(Span<byte> destination) => destination[0] = (byte)_value;
        }

        // 0/1 rather than a copy of the in-memory representation, which the format may not assume.
        readonly struct BoolWide : IWide
        {
            readonly bool _value;
            public BoolWide(bool value) => _value = value;
            public void WriteTo(Span<byte> destination) => destination[0] = _value ? (byte)1 : (byte)0;
        }

        readonly struct Int16Wide : IWide
        {
            readonly short _value;
            public Int16Wide(short value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteInt16LittleEndian(destination, _value);
        }

        readonly struct UInt16Wide : IWide
        {
            readonly ushort _value;
            public UInt16Wide(ushort value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteUInt16LittleEndian(destination, _value);
        }

        readonly struct CharWide : IWide
        {
            readonly char _value;
            public CharWide(char value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteInt16LittleEndian(destination, (short)_value);
        }

        readonly struct Int32Wide : IWide
        {
            readonly int _value;
            public Int32Wide(int value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteInt32LittleEndian(destination, _value);
        }

        readonly struct UInt32Wide : IWide
        {
            readonly uint _value;
            public UInt32Wide(uint value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteUInt32LittleEndian(destination, _value);
        }

        readonly struct SingleWide : IWide
        {
            readonly float _value;
            public SingleWide(float value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteSingleLittleEndian(destination, _value);
        }

        readonly struct Int64Wide : IWide
        {
            readonly long _value;
            public Int64Wide(long value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteInt64LittleEndian(destination, _value);
        }

        readonly struct UInt64Wide : IWide
        {
            readonly ulong _value;
            public UInt64Wide(ulong value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteUInt64LittleEndian(destination, _value);
        }

        readonly struct DoubleWide : IWide
        {
            readonly double _value;
            public DoubleWide(double value) => _value = value;
            public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteDoubleLittleEndian(destination, _value);
        }
    }
}
