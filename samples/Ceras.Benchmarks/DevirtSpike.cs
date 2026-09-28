/// <summary>
/// How much devirtualizing the write path is actually worth, measured by doing it in miniature.
/// <para>
/// The same eighty-one writes go down two paths that differ in exactly one respect: an
/// <see cref="IBufferWriter{T}"/> reference, which is what the formatters use today, and a struct
/// constrained to that interface, which is what a struct writer would use. The writes, the buffer
/// and the bounds check are the same, so the difference is the whole prize.
/// </para>
/// <para>
/// One result is worth knowing: sealing <c>FixedArrayBufferWriter</c> moves this number by about a
/// tenth, and moves the real encode by nothing at all. The microbenchmark's interface call site sees
/// exactly one writer type, so guarded devirtualization specializes it; the real one is shared across
/// every formatter and sees several, so it does not. A number this shape produces is a ceiling, not
/// a prediction - which is why the end-to-end rows next to it are the ones to decide on.
/// </para>
/// Run with <c>CERAS_SPIKE=1</c>.
/// </summary>
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ceras;

namespace Ceras.Benchmarks
{
    public static class DevirtSpike
    {
        // A writer the JIT can see through: a struct, sealed by construction, constrained generic.
        struct StructWriter : IBufferWriter<byte>
        {
            public byte[] Buffer;
            public int Offset;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Advance(int count) => Offset += count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Span<byte> GetSpan(int sizeHint = 0)
            {
                // The buffer is sized up front, so this is the same bounds check the real one does,
                // and no growth path is being measured in place of a write.
                if (Buffer.Length - Offset < 4) Grow();
                return Buffer.AsSpan(Offset);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Memory<byte> GetMemory(int sizeHint = 0) => GetSpan(sizeHint).ToArray();

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Grow() => throw new InvalidOperationException("spike: buffer too small");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void WriteFixed<TWriter>(ref TWriter writer, int value) where TWriter : IBufferWriter<byte>
        {
            var span = writer.GetSpan(4);
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            writer.Advance(4);
        }

        public static void Run()
        {
            const int Fields = 81;
            const int Iterations = 400000;

            var values = new int[Fields];
            for (int i = 0; i < Fields; i++) values[i] = i * 7;

            var buffer = CerasBufferPool.Rent(64 * 1024);

            // Current shape: a writer object, reached through the interface.
            var iface = new Ceras.CerasSerializer.FixedArrayBufferWriter(buffer, 0, true);
            double tInterface = Min("81 writes via IBufferWriter (today)", Iterations, () =>
            {
                iface.Reset(buffer, 0, true);
                for (int i = 0; i < Fields; i++)
                    SerializerBinary.WriteInt32Fixed(iface, values[i]);
            });

            // Proposed shape: a struct writer, reached through a constrained generic.
            double tStruct = Min("81 writes via struct + generic", Iterations, () =>
            {
                var w = new StructWriter { Buffer = buffer };
                for (int i = 0; i < Fields; i++)
                    WriteFixed(ref w, values[i]);
            });

            CerasBufferPool.Return(buffer);

            Console.WriteLine();
            Console.WriteLine($"  per field, interface : {tInterface / Fields,7:F3} ns");
            Console.WriteLine($"  per field, struct    : {tStruct / Fields,7:F3} ns");
            Console.WriteLine($"  saving per field     : {(tInterface - tStruct) / Fields,7:F3} ns  ({(1 - tStruct / tInterface) * 100,5:F1}%)");
        }

        static double Min(string name, int n, Action body)
        {
            for (int i = 0; i < 50000; i++) body();
            double best = double.MaxValue;
            for (int r = 0; r < 7; r++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) body();
                sw.Stop();
                double ns = sw.Elapsed.TotalMilliseconds * 1e6 / n;
                if (ns < best) best = ns;
            }
            Console.WriteLine($"  {name,-36} {best,8:F2} ns");
            return best;
        }
    }
}
