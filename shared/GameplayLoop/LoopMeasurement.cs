using System;
using System.Diagnostics;

namespace Ceras.GameplayLoop
{
    /// <summary>
    /// One measurement of one body: how long it took, how many operations that was, and how many
    /// bytes it allocated on the thread that ran it.
    /// <para>
    /// Named in operations rather than exchanges because the same helper times rows whose "operation"
    /// is a whole ping-pong and rows whose operation is a single encode; what one operation is, is
    /// said by the row's name and note, not by this type.
    /// </para>
    /// </summary>
    public readonly struct LoopMeasurement
    {
        /// <summary>Operations in the pass the reported timing came from.</summary>
        public int Iterations { get; }

        /// <summary>Passes actually timed. See <see cref="LoopMeasurementRunner.Measure"/>.</summary>
        public int Passes { get; }

        /// <summary>Best (shortest) pass, in nanoseconds per operation.</summary>
        public double NanosPerOp { get; }

        /// <summary>
        /// Operations per second implied by <see cref="NanosPerOp"/>. Derived rather than measured
        /// separately, so the two columns can never disagree.
        /// </summary>
        public double OpsPerSecond => NanosPerOp <= 0 ? 0 : 1e9 / NanosPerOp;

        /// <summary>Bytes allocated across every timed pass, on the measuring thread.</summary>
        public long TotalBytes { get; }

        /// <summary>
        /// Bytes per operation, averaged over every timed pass. This is the number a "steady state"
        /// claim is made about, and the only one that can legitimately be zero.
        /// </summary>
        public double BytesPerOp => (long)Iterations * Passes <= 0 ? 0 : (double)TotalBytes / (Iterations * Passes);

        public LoopMeasurement(int iterations, int passes, double nanosPerOp, long totalBytes)
        {
            Iterations = iterations;
            Passes = passes;
            NanosPerOp = nanosPerOp;
            TotalBytes = totalBytes;
        }

        public override string ToString() =>
            $"{NanosPerOp:F1} ns/op ({OpsPerSecond:N0}/s), {BytesPerOp:F1} B/op";
    }

    /// <summary>
    /// The one way this repository times a loop, shared by the xUnit smoke test and the roadmap
    /// benchmark so the two cannot drift into measuring different things.
    /// <para>
    /// The order is the whole point: warm the body first (so formatters, JIT tiers and the first
    /// Gen0 growth are already paid for), then collect, then take the allocation counter, then time,
    /// then take it again. Measuring before the warm-up is how a "0 B/op" claim turns into a "40 B/op"
    /// claim, and timing before the collect is how a table gets a Gen0 collection attributed to
    /// whichever row happened to be running.
    /// </para>
    /// <para>
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts allocations on the measuring thread
    /// only. A workload that allocates somewhere else - another thread, a finalizer, a timer -
    /// reports zero, and nothing here can see it. Every number produced by this type is a claim about
    /// one thread in one process, not about a program.
    /// </para>
    /// </summary>
    public static class LoopMeasurementRunner
    {
        /// <summary>Stopwatch ticks per nanosecond, as a double.</summary>
        public static double NanosPerTick => 1e9 / Stopwatch.Frequency;

        /// <summary>
        /// Default time-based warm-up for <see cref="Measure"/>, in milliseconds.
        /// <para>
        /// This exists because an iteration count is the wrong unit for warming up. Tiered compilation
        /// leaves a freshly-called method at unoptimized tier-0 and then <em>suspends call counting for
        /// 100 ms</em> (<c>TC_CallCountingDelayMs</c>); a 20 000-iteration warm-up of a 100 ns loop
        /// takes about 2 ms, so it finishes before promotion can even start and the row gets timed at
        /// tier-0. That is not a small error. Measured on this repository's gameplay loop, it put a
        /// two-frame transport at 95.7 ns/op when the same code at tier-1 is 32.4 ns/op - and, worse
        /// than the size of the error, it inverted the table: a full client/server exchange came out
        /// <em>faster</em> (102.5 ns) than the one-way exchange it contains (146.8 ns), because the
        /// earlier rows had warmed the methods the later rows call. 500 ms clears the delay with room to
        /// spare.
        /// </para>
        /// <para>
        /// The iteration floor it is combined with still matters, in the other direction: a body that is
        /// genuinely slow reaches steady state in few iterations, and the floor is what says "this
        /// method has been called enough times to be promoted" for a body slower than the delay.
        /// </para>
        /// </summary>
        public const int DefaultWarmupMilliseconds = 500;

        /// <summary>
        /// Hard cap on the warm-up, so a pathological body cannot make the tool look hung. Reaching it
        /// means the row is reported anyway, at whatever tier it reached.
        /// </summary>
        public const int MaximumWarmupMilliseconds = 10_000;

        /// <summary>
        /// Allocates nothing per iteration, and reports only the byte count. This is what a test
        /// asserts on: no elapsed time, so the assertion cannot be made fragile by a loaded machine.
        /// </summary>
        /// <param name="body">The loop body. The delegate itself is created by the caller, outside
        /// the measured region, so it never shows up in the count.</param>
        /// <param name="warmupIterations">Iterations run before the counter starts. Enough to build the
        /// formatters; small on purpose, because a test's job is to fail when the steady state stops
        /// being allocation-free, not to benchmark. A count is safe here where it is not safe for
        /// timing: tier-0 code allocates at least as much as tier-1 code, so a loop that is
        /// allocation-free this early is allocation-free once it is optimised too.</param>
        /// <param name="iterations">Measured iterations.</param>
        public static long MeasureBytes(Action body, int warmupIterations, int iterations)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            for (int i = 0; i < warmupIterations; i++) body();

            Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++) body();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// Times <paramref name="body"/> and counts what it allocated, taking the best of
        /// <paramref name="passes"/> passes of <paramref name="iterations"/> operations each.
        /// <para>
        /// The warm-up runs at least <paramref name="warmupIterations"/> operations and then keeps going
        /// until <paramref name="warmupMilliseconds"/> have elapsed. Both halves are load-bearing; see
        /// <see cref="DefaultWarmupMilliseconds"/> for why the time half cannot be dropped.
        /// <para>
        /// Best-of rather than mean, for the same reason <c>FixedCostProbe</c> in
        /// <c>samples/Ceras.Benchmarks</c> exists: at tens of nanoseconds per operation a single pass
        /// is inside the range where an interrupt or a Gen0 collection moves the answer by more than
        /// the effect being measured. Allocation is summed over <em>all</em> passes rather than taken
        /// from the fastest one, because the fastest pass is not a pass where less happened - it is
        /// the pass that got lucky - and a B/op figure should describe every pass, not the luckiest.
        /// </para>
        /// </summary>
        public static LoopMeasurement Measure(Action body, int warmupIterations, int iterations, int passes = 3,
                                             int warmupMilliseconds = DefaultWarmupMilliseconds)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (passes < 1) throw new ArgumentOutOfRangeException(nameof(passes), passes, "At least one pass is needed.");

            WarmUp(body, warmupIterations, warmupMilliseconds);

            double bestTicksPerOp = double.MaxValue;
            long totalBytes = 0;

            for (int pass = 0; pass < passes; pass++)
            {
                Collect();
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                long beforeTicks = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) body();
                long ticks = Stopwatch.GetTimestamp() - beforeTicks;
                totalBytes += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

                double ticksPerOp = (double)ticks / iterations;
                if (ticksPerOp < bestTicksPerOp) bestTicksPerOp = ticksPerOp;
            }

            return new LoopMeasurement(iterations, passes, bestTicksPerOp * NanosPerTick, totalBytes);
        }

        /// <summary>
        /// Runs <paramref name="body"/> until it has been called at least
        /// <paramref name="warmupIterations"/> times <em>and</em> for at least
        /// <paramref name="warmupMilliseconds"/>, whichever takes longer, subject to
        /// <see cref="MaximumWarmupMilliseconds"/>.
        /// </summary>
        public static void WarmUp(Action body, int warmupIterations, int warmupMilliseconds)
        {
            long budgetTicks = (long)(warmupMilliseconds / 1000.0 * Stopwatch.Frequency);
            long capTicks = (long)(MaximumWarmupMilliseconds / 1000.0 * Stopwatch.Frequency);

            int calls = 0;
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                // Checked between batches rather than inside one: the body is the thing being warmed,
                // and putting a clock read or a counter in its path would be measuring something else.
                for (int i = 0; i < 64; i++) body();
                calls += 64;
                long elapsed = Stopwatch.GetTimestamp() - start;
                if (calls >= warmupIterations && elapsed >= budgetTicks) return;
                if (elapsed >= capTicks) return;
            }
        }

        /// <summary>
        /// Two full blocking collections with a finalizer barrier between them. One is not enough: a
        /// finalizer promoted by the first run can resurrect objects into the second, and the counter
        /// would then include an allocation that had already been accounted for.
        /// </summary>
        public static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
