# Ceras roadmap benchmarks

A standalone, runnable measurement tool for the P0 0.7 roadmap performance baselines
(`TODO.md` → "Benchmark sustained traffic and steady-state allocations…", and Milestone 5's
"retain only genuinely useful exploratory tools/benchmarks").

It is a tool, not a test and not a sample: it references only `src/Ceras` plus the source
generator. It therefore has no test-assembly and no sandbox dependency, so changing or deleting
the test suite (or a sample) cannot move its numbers — but it does build and measure `src/Ceras`,
so a library change is expected to. Treat a number as describing the library as it currently is,
not a pinned historical baseline, and pin the library state when you compare two runs. Its models
belong to their own serialization context, `"CerasBenchmarks"` (not `Ceras.Test`'s `"Tests"`).

## Running it

Always `-c Release`; a Debug build's numbers are not comparable with anything.

```sh
# Correctness only: verifies the behaviour the measurements assume, exits non-zero on failure.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --self-check

# Self-checks, then a deterministic ms/op and B/op table (no BenchmarkDotNet). The default mode.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-quick

# The BenchmarkDotNet harness: RobustSaveLoadBenchmarks, NetworkTransientBenchmarks, ColdInitBenchmarks.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-bench
```

`--roadmap-bench` forwards any further arguments to BenchmarkDotNet, e.g.
`--roadmap-bench --filter *Load* --job short`.

`--self-check` and `--roadmap-quick` print a pass/fail line per check and exit non-zero if any
fails, so they double as a smoke test: a measurement of a broken build is worse than none.

## What is measured

`--roadmap-quick` prints a header/section breakdown of a real save, then a table of:

| Group | Rows |
|---|---|
| Robust graph | Save (fresh/reused graph), Load (fresh/reused target), Load via the migration interpreter |
| Isolated payloads | `int[256]`, `List<int>(256)`, `List<string>(32, 8 unique)`, a private generic member |
| Network | Encode/decode of a small struct and of a small string message (`CerasFastSerializer`) |
| Cold vs steady state | Serializer construction, first Save (seal/bake), and the game's construct-per-operation pattern |
| Compression | The same save/load with `StringTableCompression.Deflate` vs `Disabled` |
| Per-section costs | Loader CRC32, payload copy, schema copy, payload string-table decode, schema-section parse |
| Concurrency | 8 threads × 2000 loads, one serializer per thread |

The per-section rows call the library's own code on a real save (`RobustEnvelope.Checksum`,
`StringTable.Load`, `SchemaSection.Read`), so they attribute cost instead of approximating it. The
benchmark is a friend assembly of Ceras, which is how it reads the robust header offsets and the
loader's CRC32 without restating them.

## Interpreting the numbers

- Absolute values are specific to the machine, runtime and GC mode that produced them. They are not
  comparable across machines, and the tool prints the runtime and whether server GC is on.
- `--roadmap-quick` is not a statistical benchmark. Iteration counts are small and chosen to keep
  the whole table in seconds; treat a row as an order of magnitude and use `--roadmap-bench` for
  confidence intervals and allocation columns.
- `B/op` comes from `GC.GetAllocatedBytesForCurrentThread`, so it counts only allocations on the
  measured thread. A row that allocates on another thread reports 0.
- A global warm-up runs before the first row, so the early rows are not paying tiered-JIT
  promotion or initial Gen0 growth. The run still ends with a reminder of these caveats.
- The `--roadmap-bench` mode writes a `BenchmarkDotNet.Artifacts` directory (git-ignored).
