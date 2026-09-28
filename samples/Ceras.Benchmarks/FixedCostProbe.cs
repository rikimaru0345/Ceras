using System;
using System.Diagnostics;
using Ceras;

namespace Ceras.Benchmarks
{
    /// <summary>
    /// A precise version of the small-message encode rows in the roadmap table.
    /// <para>
    /// Those rows are the ones worth reading for shape, not for small deltas: they time one pass of
    /// a warmed loop, which at these sizes is dominated by whether the loop has reached steady-state
    /// JIT, and they print five decimals of a millisecond - 10 ns of resolution. A change worth
    /// measuring here is routinely smaller than both. Taking the minimum of several long runs
    /// instead puts the measurement in steady state and throws away the machine's bad moments, which
    /// is what makes a few-nanosecond difference visible at all.
    /// <para>
    /// Run with <c>CERAS_FIXED=1</c>. The absolute numbers include renting and returning a buffer,
    /// which is what a caller who does not hold one long-term pays; the comparison between runs is
    /// what matters, not the values.
    /// </para>
    /// </summary>
    public static class FixedCostProbe
    {
        public static void Run()
        {
            var s = BenchData.Fast();

            // Two shapes, because they are dominated by different things: the first is nearly all fixed
            // cost, the second adds a little field work on top.
            Min("encode  1 field  (all fixed)", 2000000, () =>
            {
                var b = CerasBufferPool.Rent(4096);
                int o = 0;
                s.Serialize(new Wide1 { F0 = 1 }, ref b, ref o);
                CerasBufferPool.Return(b);
            });

            Min("encode  3 fields (BenchMove)", 2000000, () =>
            {
                var b = CerasBufferPool.Rent(4096);
                int o = 0;
                s.Serialize(new BenchMove { Id = 7, X = 1.5f, Y = -2.5f }, ref b, ref o);
                CerasBufferPool.Return(b);
            });

            // The formatter lookup on its own, which is the memo's whole subject.
            Min("GetFormatter<Wide1>()", 2000000, () => { var x = s.GetFormatter<Wide1>(); });

            // The per-field slope, measured the only way it can be measured: as the difference
            // between two field counts, so the fixed cost cancels. The devirtualization question is
            // entirely about this number, and it is small enough that reading it off a single row -
            // or off a single run - is hopeless.
            var one = new Wide1 { F0 = 1 };
            var nine = new Wide9 { F0 = 1, F8 = 1 };
            var eightyone = BenchData.MakeWide81();

            double t1 = Min("encode  1 field", 2000000, () => Encode(s, one));
            double t9 = Min("encode  9 fields", 2000000, () => Encode(s, nine));
            double t81 = Min("encode 81 fields", 400000, () => Encode(s, eightyone));

            Console.WriteLine();
            Console.WriteLine($"  per-field cost, 1 -> 9 fields : {(t9 - t1) / 8,7:F3} ns/field");
            Console.WriteLine($"  per-field cost, 1 -> 81 fields: {(t81 - t1) / 80,7:F3} ns/field");
            Console.WriteLine($"  fixed cost of an operation   : {t1 - (t81 - t1) / 80,7:F2} ns");

            // The floor. Eighty-one varint writes with no formatter, no interface and no dispatch
            // around them, which is what devirtualizing the writer could at best get the encode down
            // to. Comparing this against the 81-field row above is the whole size of the prize; if the
            // two are close there is nothing there, and no amount of refactoring will show it.
            double floor81 = Min("81 varint writes, no formatter", 400000, () =>
            {
                var b = CerasBufferPool.Rent(64 * 1024);
                int o = 0;
                for (int i = 0; i < 81; i++) SerializerBinary.WriteInt32(ref b, ref o, WideValues[i]);
                CerasBufferPool.Return(b);
            });
            Console.WriteLine();
            Console.WriteLine($"  floor for 81 fields          : {floor81,7:F2} ns  ({(floor81 - t1) / 80,6:F3} ns/field)");
            Console.WriteLine($"  encode 81 fields             : {t81,7:F2} ns  ({(t81 - t1) / 80,6:F3} ns/field)");
            Console.WriteLine($"  -> the most devirtualizing the writer could return: {t81 - floor81,6:F2} ns of {t81:F0}");
        }

        static readonly int[] WideValues =
        {
            1, 300, 70000, 5, 12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0,
        };

        static void Encode<T>(CerasFastSerializer s, T value)
        {
            var b = CerasBufferPool.Rent(64 * 1024);
            int o = 0;
            s.Serialize(value, ref b, ref o);
            CerasBufferPool.Return(b);
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
            Console.WriteLine($"  {name,-30} {best,8:F2} ns   (min of 7 x {n / 1000}k)");
            return best;
        }
    }
}
