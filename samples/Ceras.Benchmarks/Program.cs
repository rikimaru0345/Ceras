using System;
using System.Collections.Generic;
using System.Linq;

namespace Ceras.Benchmarks
{
    /// <summary>
    /// Standalone roadmap measurement tool for Ceras.
    /// <para>
    /// Modes, all run from the repository root:
    /// </para>
    /// <code>
    /// dotnet run --project samples/Ceras.Benchmarks -c Release -- --self-check     # correctness only
    /// dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-quick  # quick numbers + self-checks
    /// dotnet run --project samples/Ceras.Benchmarks -c Release -- --network-loop   # gameplay message loop
    /// dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-bench  # BenchmarkDotNet harness
    /// </code>
    /// <para>
    /// <c>--roadmap-quick</c>, <c>--network-loop</c> and <c>--self-check</c> exit non-zero when a
    /// self-check fails, so they can be used as a smoke test. Always build <c>-c Release</c>: a Debug
    /// build's unoptimized timings are not comparable with anything.
    /// </para>
    /// <para>
    /// <c>--network-loop</c> is a mode of its own rather than more rows in <c>--roadmap-quick</c>
    /// because it answers a different question - what a client/server exchange costs end to end, on
    /// one thread, with no I/O - and because its warm-up and pass count are large enough that folding
    /// it into the default mode would change how long the default mode takes.
    /// </para>
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "--roadmap-quick";

            switch (mode)
            {
                case "--self-check":
                    return RunSelfChecks() ? 0 : 1;

                case "--roadmap-quick":
                    if (!RunSelfChecks()) return 1;
                    return RoadmapBenchmarks.RunQuick();

                case "--network-loop":
                    // The same thirteen self-checks run first, so a broken build cannot be measured
                    // here either; then the loop verifies itself, and only then is the table printed.
                    if (!RunSelfChecks()) return 1;
                    return NetworkLoopTable.Run(args.Skip(1).ToArray());

                case "--roadmap-bench":
                    // Everything after the mode is BenchmarkDotNet's own command line.
                    RoadmapBenchmarks.RunBenchmarkDotNet(args.Skip(1).ToArray());
                    return 0;

                case "--help":
                case "-h":
                case "-?":
                    PrintUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown argument '{mode}'.");
                    Console.Error.WriteLine();
                    PrintUsage();
                    return 2;
            }
        }

        static bool RunSelfChecks()
        {
            Console.WriteLine("=== Ceras benchmark self-checks ===");
            Console.WriteLine($"context     : {BenchContext.Name}");
            Console.WriteLine();

            List<SelfChecks.Result> results = SelfChecks.RunAll();

            foreach (var result in results)
            {
                string status = result.Passed ? "PASS" : "FAIL";
                string detail = string.IsNullOrEmpty(result.Detail) ? "" : $"  ({result.Detail})";
                Console.WriteLine($"  {status}  {result.Name}{detail}");
            }
            int failed = results.Count(r => !r.Passed);
            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? $"All {results.Count} self-checks passed."
                : $"{failed} of {results.Count} self-checks FAILED - the numbers below would be meaningless.");
            Console.WriteLine();
            return failed == 0;
        }

        static void PrintUsage()
        {
            Console.WriteLine("Ceras roadmap benchmarks (samples/Ceras.Benchmarks)");
            Console.WriteLine();
            Console.WriteLine("  --self-check     Verify the behaviours the measurements assume; print a pass/fail list.");
            Console.WriteLine("  --roadmap-quick  Self-checks, then a deterministic ms/op and B/op table (no BDN). Default.");
            Console.WriteLine("  --network-loop   Self-checks, then a client/server gameplay exchange measured over an");
            Console.WriteLine("                  in-memory duplex: transport, encode, decode, roundtrip and the");
            Console.WriteLine("                  string case, with a B/op column per row.");
            Console.WriteLine("                  Options: --exchanges N (default 200000), --warmup N (20000), --passes N (5).");
            Console.WriteLine("  --roadmap-bench  The BenchmarkDotNet harness (RobustSaveLoad/NetworkTransient/ColdInit/");
            Console.WriteLine("                  NetworkLoop). Further arguments go to BenchmarkDotNet, e.g.");
            Console.WriteLine("                  --filter *Load* --job short, or --filter *NetworkLoop*.");
            Console.WriteLine("  --help           This text.");
            Console.WriteLine();
            Console.WriteLine("All modes: dotnet run --project samples/Ceras.Benchmarks -c Release -- <mode>");
            Console.WriteLine();
            Console.WriteLine("Caveats: results are per-machine, absolute timings are not comparable across");
            Console.WriteLine("machines or runtimes, and B/op counts allocations on the measured thread only");
            Console.WriteLine("(so a workload that allocates on another thread reports 0).");
        }
    }
}
