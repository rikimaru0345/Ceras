namespace Ceras.Formatters
{
    using System;
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public sealed class ReinterpretFormatter<T> : FormatterBase<T>
        where T : struct
    {
        readonly int _size;

        public ReinterpretFormatter()
        {
            _size = Unsafe.SizeOf<T>();
        }

        public override void Serialize(IBufferWriter<byte> writer, T value)
        {
            var span = writer.GetSpan(_size);
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value);
            writer.Advance(_size);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T value)
        {
            if (reader.UnreadSpan.Length >= _size)
            {
                value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(reader.UnreadSpan));
                reader.Advance(_size);
            }
            else
            {
                Span<byte> temp = stackalloc byte[_size];
                if (!reader.TryCopyTo(temp)) throw new Ceras.Exceptions.CerasDataCorruptionException("Corrupt data: the save ended before a value could be read (truncated payload).");
                value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(temp));
                reader.Advance(_size);
            }
        }
    }

    public sealed class ReinterpretArrayFormatter<T> : FormatterBase<T[]>
        where T : struct
    {
        readonly int _elementSize;
        readonly uint _maxCount;

        public ReinterpretArrayFormatter(uint maxCount)
        {
            _elementSize = Unsafe.SizeOf<T>();
            _maxCount = maxCount;
        }

        public override void Serialize(IBufferWriter<byte> writer, T[] value)
        {
            if (value == null)
            {
                SerializerBinary.WriteUInt32Bias(writer, -1, 1);
                return;
            }

            SerializerBinary.WriteUInt32Bias(writer, value.Length, 1);
            int totalSize = value.Length * _elementSize;
            var span = writer.GetSpan(totalSize);
            SerializerBinary.FastCopy<T>(value.AsSpan(), span);
            writer.Advance(totalSize);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref T[] value)
        {
            int length = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (length == -1)
            {
                value = null;
                return;
            }

            if (length > _maxCount) throw new InvalidOperationException("Max count exceeded");

            if (value == null || value.Length != length)
                value = new T[length];

            int totalSize = length * _elementSize;
            var destSpan = MemoryMarshal.AsBytes(value.AsSpan());
            if (reader.UnreadSpan.Length >= totalSize)
            {
                reader.UnreadSpan.Slice(0, totalSize).CopyTo(destSpan);
                reader.Advance(totalSize);
            }
            else
            {
                if (!reader.TryCopyTo(destSpan)) throw new Ceras.Exceptions.CerasDataCorruptionException("Corrupt data: the save ended before a value could be read (truncated payload).");
                reader.Advance(totalSize);
            }
        }
    }
}
