using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Ceras.GameplayLoop;

namespace Ceras.Benchmarks
{
    /// <summary>
    /// The BenchmarkDotNet half of the gameplay message loop.
    /// <para>
    /// <c>--network-loop</c> (see <see cref="Run"/>) is the primary measurement: a deterministic table
    /// with the transport-only baseline, the encode, the decode and the whole roundtrip in separate
    /// rows, which is what makes the numbers attributable. This class is the same loop under
    /// BenchmarkDotNet, for when a change needs a confidence interval rather than an order of
    /// magnitude:
    /// </para>
    /// <code>
    /// dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-bench --filter *NetworkLoop*
    /// </code>
    /// <para>
    /// The <c>Allocated</c> column here is a cross-check on the quick table's B/op column, from an
    /// independent counter implementation, and it is where the string rows are visibly not zero. The
    /// struct rows are expected to report <c>-</c>: a loop that allocates nothing allocates nothing in
    /// BenchmarkDotNet either, so a non-zero number on <see cref="FullExchange"/> means the loop has
    /// stopped being allocation-free and the quick table's zero is stale.
    /// </para>
    /// </summary>
    [MemoryDiagnoser]
    public class NetworkLoopBenchmarks
    {
        GameplayLoopHarness _loop;

        [GlobalSetup]
        public void Setup() => _loop = new GameplayLoopHarness();

        [GlobalCleanup]
        public void Cleanup() => _loop?.Dispose();

        /// <summary>The floor: framing and moving the bytes, with no Ceras in the path at all.</summary>
        [Benchmark(Baseline = true)]
        public void TransportOnly() => _loop.TransportOnly();

        /// <summary>One pooled write of one input. The convenience overload is not used: it allocates its result.</summary>
        [Benchmark]
        public void EncodeInput() => _loop.EncodeInputOnly();

        /// <summary>One read of one input out of already-encoded bytes.</summary>
        [Benchmark]
        public void DecodeInput() => _loop.DecodeInputOnly();

        /// <summary>Encode, frame, move and verify in the client-to-server direction.</summary>
        [Benchmark]
        public void OneWayExchange() => _loop.ExchangeInputOnly();

        /// <summary>The whole loop: input out, state back, both directions verified.</summary>
        [Benchmark]
        public void FullExchange() => _loop.Exchange();

        /// <summary>Encoding a reference-typed message from an instance the caller already holds.</summary>
        [Benchmark]
        public void ChatEncode_ReusedInstance() => _loop.EncodeChatOnly();

        /// <summary>Reading a reference-typed message into a target nobody held. Allocates; see the note.</summary>
        [Benchmark]
        public void ChatDecode_FreshTarget() => _loop.DecodeChatIntoFreshTarget();

        /// <summary>Reading it into a target the server already held. The object is reused, the string is not.</summary>
        [Benchmark]
        public void ChatDecode_ReusedTarget() => _loop.DecodeChatIntoReusedTarget();

        /// <summary>Both directions of the reference-typed message. Allocates; reported, not hidden.</summary>
        [Benchmark]
        public void FullExchange_ReusedInstances() => _loop.ExchangeChat();
    }

    /// <summary>
    /// The deterministic network-loop table, run by <c>--network-loop</c>.
    /// <para>
    /// It runs the same thirteen self-checks as every other mode first (so a broken build cannot be
    /// measured), then a scaled-up version of the verification the xUnit smoke test does - a real run
    /// of the loop with the pool's outstanding-rental count and the transport's frame and growth
    /// counters checked around it - and only then the table. The verification is what makes the table
    /// worth reading: a loop that silently stopped decoding would report the same nanoseconds.
    /// </para>
    /// </summary>
    public static class NetworkLoopTable
    {
        /// <summary>Exchanges per timed pass unless <c>--exchanges</c> says otherwise.</summary>
        public const int DefaultExchanges = 200_000;

        /// <summary>Untimed exchanges before the first timed pass unless <c>--warmup</c> says otherwise.</summary>
        public const int DefaultWarmup = 20_000;

        /// <summary>Timed passes per row unless <c>--passes</c> says otherwise.</summary>
        public const int DefaultPasses = 5;

        /// <summary>
        /// <c>dotnet run --project samples/Ceras.Benchmarks -c Release -- --network-loop</c>, with
        /// <c>--exchanges N</c>, <c>--warmup N</c> and <c>--passes N</c> accepted after the mode.
        /// </summary>
        /// <returns>0 when the self-checks passed and the loop verified, 1 otherwise.</returns>
        public static int Run(string[] options)
        {
            int exchanges = DefaultExchanges, warmup = DefaultWarmup, passes = DefaultPasses;
            switch (TryReadOptions(options, ref exchanges, ref warmup, ref passes))
            {
                case Options.Ok: break;
                case Options.Help: PrintOptions(); return 0;
                default:
                    Console.Error.WriteLine();
                    PrintOptions();
                    return 2;
            }

            Console.WriteLine("=== Ceras gameplay message loop (transient, in-memory duplex) ===");
            Console.WriteLine($".NET        : {Environment.Version}");
            Console.WriteLine($"server GC   : {System.Runtime.GCSettings.IsServerGC}");
            Console.WriteLine($"context     : {BenchContext.Name}");
            Console.WriteLine($"exchanges   : {exchanges:N0} per pass, {passes} passes (best of)");
            Console.WriteLine($"warm-up     : at least {warmup:N0} iterations and {LoopMeasurementRunner.DefaultWarmupMilliseconds} ms of wall clock per row");
            Console.WriteLine();

            if (!Verify(exchanges, out string report))
            {
                Console.WriteLine(report);
                Console.WriteLine();
                Console.WriteLine("The loop did not verify, so the table below would be meaningless. Aborting.");
                return 1;
            }
            Console.WriteLine(report);
            Console.WriteLine();

            var rows = new List<(string Name, string Note)>();
            var results = new List<LoopMeasurement>();

            using (var loop = new GameplayLoopHarness())
            {
                Console.WriteLine($"One input encodes to {loop.EncodedInputLength} B, one state to {loop.EncodedStateLength} B, " +
                                  $"one chat line to {loop.EncodedChatLength} B (framing adds {FakeDuplexTransport.LengthPrefixSize} B per frame).");
                Console.WriteLine($"Transport lanes: {loop.Transport.CapacityToServer} B per direction, " +
                                  $"ceiling {FakeDuplexTransport.DefaultMaximumPayloadBytes:N0} B payload.");
                Console.WriteLine();

                void Row(string name, string note, Action body)
                {
                    results.Add(LoopMeasurementRunner.Measure(body, warmup, exchanges, passes));
                    rows.Add((name, note));
                }

                // The floor. Everything above it is Ceras, so it is the row that turns "the exchange
                // costs 300 ns" into "the exchange costs 300 ns, of which 40 is the pipe".
                Row("Transport only (no Ceras)", "frame + copy, both directions", loop.TransportOnly);
                Row("Encode only (client)", "pooled write, no result array", loop.EncodeInputOnly);
                Row("Decode only (server)", "reader only, no encode", loop.DecodeInputOnly);
                Row("One way (encode+move+decode)", "client -> server, verified", loop.ExchangeInputOnly);
                Row("Full exchange (ping-pong)", "input out, state back, both verified", loop.Exchange);

                // The second case. Reported, not asserted to be zero: a string read out of bytes is a
                // new string, and the row that shows how much of the cost is the object rather than
                // the string is the one next to it.
                Row("Chat encode (reused instance)", "caller already held it", loop.EncodeChatOnly);
                Row("Chat decode (fresh target)", "object + string, expected > 0 B", loop.DecodeChatIntoFreshTarget);
                Row("Chat decode (reused target)", "object reused, string still new", loop.DecodeChatIntoReusedTarget);
                Row("Full chat exchange (reused)", "both directions, expected > 0 B", loop.ExchangeChat);

                if (loop.Failures != 0)
                {
                    Console.WriteLine($"FAIL: the loop reported {loop.Failures:N0} verification failures after the table; the rows above are wrong.");
                    return 1;
                }
            }

            Console.WriteLine($"{"workload",-34} {"ns/op",10} {"ops/s",16} {"B/op",10} {"total B",14}  note");
            Console.WriteLine(new string('-', 116));
            for (int i = 0; i < rows.Count; i++)
            {
                var (name, note) = rows[i];
                var r = results[i];
                Console.WriteLine($"{name,-34} {r.NanosPerOp,10:F1} {r.OpsPerSecond,16:N0} {r.BytesPerOp,10:F1} {r.TotalBytes,14:N0}  {note}");
            }

            Console.WriteLine();
            Console.WriteLine($"  The 'total B' column is GC.GetAllocatedBytesForCurrentThread's raw delta over all {passes} measured");
            Console.WriteLine($"  passes of {exchanges:N0} operations ({exchanges * passes:N0} operations in total), so B/op above is that");
            Console.WriteLine("  number divided and the division can be checked rather than taken on trust.");
            Console.WriteLine();
            PrintReading(results);
            PrintCaveats();
            return 0;
        }

        /// <summary>
        /// Runs the loop for real and checks the things a fast loop can silently get wrong: that both
        /// directions decoded what was sent, that the transport moved exactly the frames it should
        /// have, that no ring had to grow, and that the two pooled write buffers are still exactly two
        /// outstanding rentals when the run ends.
        /// </summary>
        static bool Verify(int exchanges, out string report)
        {
            var lines = new List<string>();
            int liveBefore = CerasBufferPool.LiveRentals;
            int rentedByLoop;
            bool ok = true;

            using (var loop = new GameplayLoopHarness())
            {
                // One exchange before the checks below, so the table is not the first thing that has
                // ever touched these formatters.
                loop.Exchange();
                rentedByLoop = CerasBufferPool.LiveRentals - liveBefore;
                int capacityBefore = loop.Transport.CapacityToServer;
                long checksumBefore = loop.Checksum;

                for (int i = 0; i < exchanges; i++) loop.Exchange();

                lines.Add($"=== loop verification ({exchanges:N0} exchanges) ===");
                lines.Add($"  exchanges run               : {loop.Sequence:N0}");
                lines.Add($"  verification failures       : {loop.Failures}");
                lines.Add($"  frames client -> server     : {loop.Transport.FramesToServer:N0} " +
                          $"({loop.Transport.PayloadBytesToServer:N0} payload B, largest frame {loop.Transport.LargestFrame} B)");
                lines.Add($"  frames server -> client     : {loop.Transport.FramesToClient:N0} " +
                          $"({loop.Transport.PayloadBytesToClient:N0} payload B)");
                lines.Add($"  transport buffer growths  : {loop.Transport.BufferGrowths}");
                lines.Add($"  transport refusals         : {loop.Transport.RefusedSends}");
                lines.Add($"  lane cursor resets         : {loop.Transport.CursorResets:N0} (compactions {loop.Transport.Compactions})");
                lines.Add($"  checksum                   : {loop.Checksum - checksumBefore} (non-zero, so the decoded values were read)");

                if (loop.Failures != 0) { ok = false; lines.Add("  -> a decoded value did not match the value that was sent."); }
                if (loop.Sequence != exchanges + 1) { ok = false; lines.Add($"  -> {loop.Sequence:N0} exchanges ran, {exchanges + 1:N0} were asked for."); }
                if (loop.Transport.FramesToServer != exchanges + 1) { ok = false; lines.Add("  -> the server received a different number of frames than the client sent."); }
                if (loop.Transport.FramesToClient != exchanges + 1) { ok = false; lines.Add("  -> the client received a different number of frames than the server sent."); }
                if (loop.Transport.BufferGrowths != 0) { ok = false; lines.Add("  -> a transport lane had to grow, so the B/op column is not a steady state."); }
                if (loop.Transport.RefusedSends != 0) { ok = false; lines.Add("  -> a frame was refused, so some exchange did not happen."); }
                if (loop.Transport.CapacityToServer != capacityBefore) { ok = false; lines.Add("  -> the client-to-server lane changed size."); }
                if (loop.Checksum == checksumBefore) { ok = false; lines.Add("  -> the checksum did not move, so nothing was read back."); }
                if (rentedByLoop != 2) { ok = false; lines.Add($"  -> the loop holds {rentedByLoop} pooled write buffers, expected 2 (one per endpoint)."); }

                // The pooled write buffers are still live rentals at this point, and the transport's
                // own rings are not: that separation is the property the pool guard exists to keep, and
                // a loop that blurred it would be writing into a buffer somebody else may be holding.
                if (!CerasBufferPool.IsRental(loop.ClientWriteBuffer)) { ok = false; lines.Add("  -> the client's write buffer is not a live rental."); }
                if (!CerasBufferPool.IsRental(loop.ServerWriteBuffer)) { ok = false; lines.Add("  -> the server's write buffer is not a live rental."); }
                if (CerasBufferPool.IsRental(loop.Transport.ToServerBuffer)) { ok = false; lines.Add("  -> the transport's buffer is being treated as a pooled rental."); }
            }

            int liveAfter = CerasBufferPool.LiveRentals;
            lines.Add($"  pooled rentals returned  : {rentedByLoop} (outstanding {liveBefore} -> {liveAfter})");
            if (liveAfter != liveBefore) { ok = false; lines.Add("  -> a pooled write buffer was not returned, or was returned twice."); }
            lines.Add(ok ? "  -> OK" : "  -> FAILED");

            report = string.Join(Environment.NewLine, lines);
            return ok;
        }

        /// <summary>
        /// Turns the table into differences, because the rows are not independent: a full exchange
        /// contains a one-way exchange, which contains an encode, a decode and the transport.
        /// </summary>
        static void PrintReading(List<LoopMeasurement> rows)
        {
            double transport = rows[0].NanosPerOp;
            double encode = rows[1].NanosPerOp;
            double decode = rows[2].NanosPerOp;
            double oneWay = rows[3].NanosPerOp;
            double full = rows[4].NanosPerOp;

            Console.WriteLine("=== how to read those numbers ===");
            Console.WriteLine($"  transport, both directions        : {transport,7:F1} ns");
            Console.WriteLine($"  one input encode                   : {encode,7:F1} ns");
            Console.WriteLine($"  one input decode                   : {decode,7:F1} ns");
            Console.WriteLine($"  encode + move + decode, one way    : {oneWay,7:F1} ns");
            Console.WriteLine($"  the full ping-pong                 : {full,7:F1} ns");
            Console.WriteLine();
            Console.WriteLine($"  one way - (encode + decode)        : {oneWay - (encode + decode),7:F1} ns  (its share of the transport)");
            Console.WriteLine($"  full - one way                     : {full - oneWay,7:F1} ns  (the server's reply half)");
            Console.WriteLine($"  transport share of the exchange    : {100.0 * transport / full:F1}%");
            Console.WriteLine($"  serializer share of the exchange   : {100.0 * (full - transport) / full:F1}%");
            Console.WriteLine();
            Console.WriteLine("  The differences attribute cost; they are not a budget that adds up. One that comes");
            Console.WriteLine("  out negative is expected and not hidden: each row is its own tight loop, so a");
            Console.WriteLine("  part measured alone pays a call and a working set it does not pay here.");
            Console.WriteLine();
            Console.WriteLine("  Every row above is a single thread driving both endpoints. A real game runs the");
            Console.WriteLine("  client and the server on two machines with a socket and a scheduler between them;");
            Console.WriteLine("  none of that is measured here, and nothing here should be read as a prediction");
            Console.WriteLine("  about a real connection's latency.");
            Console.WriteLine();
        }

        static void PrintCaveats()
        {
            Console.WriteLine("Caveats:");
            Console.WriteLine("  * B/op comes from GC.GetAllocatedBytesForCurrentThread, so it counts allocations on the");
            Console.WriteLine("    measuring thread only. Work done on another thread reports 0 and is not counted.");
            Console.WriteLine("  * The zero rows are zero for the fixed-size struct messages on this thread, in this");
            Console.WriteLine("    process, with the lanes sized as shown. They say nothing about a real socket, a");
            Console.WriteLine("    second thread, a second process, or a host whose GC is configured differently.");
            Console.WriteLine("  * The transport is an in-process byte lane. It is not a socket: no kernel, no syscall,");
            Console.WriteLine("    no packet, no partial read, no slow-peer backpressure, no Nagle, no encryption.");
            Console.WriteLine("  * Every row pays one delegate invocation per operation, so the fastest row is");
            Console.WriteLine("    over-reported by a nanosecond or two.");
            Console.WriteLine($"  * Each row is warmed for at least {LoopMeasurementRunner.DefaultWarmupMilliseconds} ms of wall clock, not");
            Console.WriteLine("    just a fixed iteration count. Tiered compilation suspends call counting for 100 ms");
            Console.WriteLine("    after a method is first called, so an iteration-count warm-up of a fast loop");
            Console.WriteLine("    finishes before promotion can start and times unoptimized tier-0 code - which");
            Console.WriteLine("    inflated these rows by up to 3x and, before it was fixed, made a full exchange");
            Console.WriteLine("    look faster than the one-way exchange it contains. See LoopMeasurementRunner.");
            Console.WriteLine("  * Timing is best-of-N passes on a machine that was not otherwise quiet. Treat a change");
            Console.WriteLine("    smaller than a few nanoseconds per operation as unmeasured.");
        }

        enum Options { Ok, Help, Bad }

        static Options TryReadOptions(string[] options, ref int exchanges, ref int warmup, ref int passes)
        {
            for (int i = 0; i < options.Length; i++)
            {
                switch (options[i])
                {
                    case "--exchanges": if (!TryReadCount(options, ref i, ref exchanges)) return Options.Bad; break;
                    case "--warmup": if (!TryReadCount(options, ref i, ref warmup)) return Options.Bad; break;
                    case "--passes": if (!TryReadCount(options, ref i, ref passes)) return Options.Bad; break;
                    case "--help":
                    case "-h":
                    case "-?":
                        return Options.Help;
                    default:
                        Console.Error.WriteLine($"Unknown option '{options[i]}'.");
                        return Options.Bad;
                }
            }

            if (exchanges < 1) { Console.Error.WriteLine("--exchanges must be at least 1."); return Options.Bad; }
            if (passes < 1) { Console.Error.WriteLine("--passes must be at least 1."); return Options.Bad; }
            if (warmup < 0) { Console.Error.WriteLine("--warmup cannot be negative."); return Options.Bad; }
            return Options.Ok;
        }

        static void PrintOptions()
        {
            Console.WriteLine("Options for --network-loop:");
            Console.WriteLine($"  --exchanges N   exchanges per timed pass (default {DefaultExchanges})");
            Console.WriteLine($"  --warmup N      minimum untimed iterations before the first pass (default {DefaultWarmup}).");
            Console.WriteLine($"                  The warm-up is also at least {LoopMeasurementRunner.DefaultWarmupMilliseconds} ms of wall clock per row, which is the");
            Console.WriteLine("                  part that matters: an iteration count alone can finish before");
            Console.WriteLine("                  tiered compilation promotes anything.");
            Console.WriteLine($"  --passes N      timed passes per row, best of (default {DefaultPasses})");
        }

        static bool TryReadCount(string[] options, ref int index, ref int value)
        {
            if (index + 1 >= options.Length || !int.TryParse(options[index + 1], out int parsed))
            {
                Console.Error.WriteLine($"{options[index]} needs a number.");
                return false;
            }
            value = parsed;
            index++;
            return true;
        }
    }
}
