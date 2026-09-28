using System;
using System.Diagnostics;

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
        }

        static void Min(string name, int n, Action body)
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
        }
    }
}
