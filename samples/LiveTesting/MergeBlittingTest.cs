using Ceras;
using Ceras.Formatters;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiveTesting
{
    class MergeBlittingTest
    {
        static byte[] _buffer = new byte[1000];
        static void DoTest(Vector3 value, IFormatter<Vector3> formatter)
        {
            var writer = new CerasSerializer.FixedArrayBufferWriter(_buffer, 0);
            formatter.Serialize(writer, value);

            var seq = new ReadOnlySequence<byte>(_buffer, 0, writer.Offset);
            var reader = new SequenceReader<byte>(seq);
            Vector3 clone = default;
            formatter.Deserialize(ref reader, ref clone);
        }

        internal static void Test()
        {
            var defaultF = new DefaultFormatter();
            var rawCall = new RawCallFormatter();
            var mergeBlit = new TestMergeBlitFormatter();
            var mergeBlitSafe = new TestMergeBlitSafeFormatter();
            var inlineFixed = new Inline_Fixed_Formatter();
            var inlineUnsafe = new Inline_Unsafe_Formatter();
            var reinterpret = new ReinterpretFormatter<Vector3>();

            var value = new Vector3(2325.123123f, -3524625.3424f, -0.2034324234234f);

            MicroBenchmark.Run(5, new BenchJob[]
            {
                ("Default", () => DoTest(value, defaultF)),
                ("RawCall", () => DoTest(value, rawCall)),
                ("MergeBlit", () => DoTest(value, mergeBlit)),
                ("MergeBlitSafe", () => DoTest(value, mergeBlitSafe)),
                ("Reinterpret", () => DoTest(value, reinterpret)),
                ("InlineFixed", () => DoTest(value, inlineFixed)),
                ("InlineUnsafe", () => DoTest(value, inlineUnsafe)),
            });

            // - reinterpret (whole vector3)

            Console.WriteLine("done");
            Console.ReadKey();
        }


    }

    struct Vector3
    {
        public float X, Y, Z;

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    class DefaultFormatter : FormatterBase<Vector3>
    {
        IFormatter<float> _floatFormatter = new FloatFormatter();

        public override void Serialize(IBufferWriter<byte> writer, Vector3 value)
        {
            _floatFormatter.Serialize(writer, value.X);
            _floatFormatter.Serialize(writer, value.Y);
            _floatFormatter.Serialize(writer, value.Z);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Vector3 value)
        {
            _floatFormatter.Deserialize(ref reader, ref value.X);
            _floatFormatter.Deserialize(ref reader, ref value.Y);
            _floatFormatter.Deserialize(ref reader, ref value.Z);
        }
    }

    class RawCallFormatter : FormatterBase<Vector3>
    {
        public override void Serialize(IBufferWriter<byte> writer, Vector3 value)
        {
            SerializerBinary.WriteFloat32Fixed(writer, value.X);
            SerializerBinary.WriteFloat32Fixed(writer, value.Y);
            SerializerBinary.WriteFloat32Fixed(writer, value.Z);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Vector3 value)
        {
            value.X = SerializerBinary.ReadFloat32Fixed(ref reader);
            value.Y = SerializerBinary.ReadFloat32Fixed(ref reader);
            value.Z = SerializerBinary.ReadFloat32Fixed(ref reader);
        }
    }


    unsafe class TestMergeBlitFormatter : FormatterBase<Vector3>
    {
        public override void Serialize(IBufferWriter<byte> writer, Vector3 value)
        {
            var span = writer.GetSpan(3 * 4);
            fixed (byte* ptr = span)
            {
                Unsafe.Write<float>(ptr, value.X);
                Unsafe.Write<float>(ptr + 4, value.Y);
                Unsafe.Write<float>(ptr + 8, value.Z);
            }
            writer.Advance(3 * 4);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Vector3 value)
        {
            var span = reader.UnreadSpan;
            if (span.Length < 3 * 4) throw new InvalidOperationException("Not enough bytes");
            fixed (byte* ptr = span)
            {
                value.X = Unsafe.Read<float>(ptr);
                value.Y = Unsafe.Read<float>(ptr + 4);
                value.Z = Unsafe.Read<float>(ptr + 8);
            }
            reader.Advance(3 * 4);
        }
    }


    class TestMergeBlitSafeFormatter : FormatterBase<Vector3>
    {
        public override void Serialize(IBufferWriter<byte> writer, Vector3 value)
        {
            var span = writer.GetSpan(3 * 4);
            ref float p = ref Unsafe.As<byte, float>(ref span[0]);
            p = value.X;

            p = ref Unsafe.Add(ref p, 1);
            p = value.Y;

            p = ref Unsafe.Add(ref p, 1);
            p = value.Z;

            writer.Advance(3 * 4);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Vector3 value)
        {
            if (reader.Remaining < 3 * 4) throw new InvalidOperationException("Not enough bytes");
            var span = reader.UnreadSpan;
            value.X = Unsafe.ReadUnaligned<float>(ref MemoryMarshal.GetReference(span));
            value.Y = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), 4));
            value.Z = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), 8));

            reader.Advance(3 * 4);
        }
    }


    unsafe class Inline_Fixed_Formatter : FormatterBase<Vector3>
    {
        public override void Serialize(IBufferWriter<byte> writer, Vector3 value)
        {
            var span = writer.GetSpan(3 * 4);
            fixed (byte* pBuffer = span)
            {
                var ptr = (float*)pBuffer;
                *ptr = value.X;

                ptr++;
                *ptr = value.Y;

                ptr++;
                *ptr = value.Z;
            }
            writer.Advance(3 * 4);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Vector3 value)
        {
            var span = reader.UnreadSpan;
            if (span.Length < 3 * 4) throw new InvalidOperationException("Not enough bytes");
            fixed (byte* pBuffer = span)
            {
                var ptr = (float*)pBuffer;
                value.X = *ptr;
                value.Y = *(ptr + 1);
                value.Z = *(ptr + 2);
            }
            reader.Advance(3 * 4);
        }
    }

    unsafe class Inline_Unsafe_Formatter : FormatterBase<Vector3>
    {
        public override void Serialize(IBufferWriter<byte> writer, Vector3 value)
        {
            var span = writer.GetSpan(3 * 4);
            fixed (byte* pBuffer = span)
            {
                var ptr = (byte*)pBuffer;

                Unsafe.Write<float>(ptr, value.X);
                Unsafe.Write<float>(ptr + 4, value.Y);
                Unsafe.Write<float>(ptr + 8, value.Z);
            }
            writer.Advance(3 * 4);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Vector3 value)
        {
            var span = reader.UnreadSpan;
            if (span.Length < 3 * 4) throw new InvalidOperationException("Not enough bytes");
            fixed (byte* pBuffer = span)
            {
                var ptr = (byte*)pBuffer;

                value.X = Unsafe.Read<float>(ptr);
                value.Y = Unsafe.Read<float>(ptr + 4);
                value.Z = Unsafe.Read<float>(ptr + 8);
            }
            reader.Advance(3 * 4);
        }
    }
}