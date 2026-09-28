# Ceras roadmap benchmarks

A standalone, runnable measurement tool for the roadmap performance baselines
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

### Save: reused graph vs fresh graph

Two different measurements, named as such in both modes:

| Row | Mode | What it includes |
|---|---|---|
| `Robust Save (reused graph)` | quick | `Save` of the graph built in setup |
| `Robust_Save` | BDN | `Save` of the graph built in `[GlobalSetup]` - the same thing, so the two tables are comparable |
| `Robust Save (fresh graph)` | quick | `Save` **plus** a full `BenchData.MakeWorld()` |
| `Robust_Save_FreshGraph` | BDN | the same, and named for it |

`Robust_Save` used to call `BenchData.MakeWorld()` inside the benchmark body, so it reported graph
construction under the serializer's name. That construction is not cheap: 64 `BenchNode` objects, each
with its own `List<int>`, plus `All`, `Tags`, a `BenchPrivateGeneric` and the two arrays - 66
collections. Measured as the difference between the two BDN rows, it adds roughly 5 us and 13 KB to a
save of roughly 29 us and 25 KB, i.e. about a fifth of the time and half the allocation. Anything that
rebuilds the graph per iteration now lives in the row whose name says so, and `Robust_Save` is directly
comparable to the quick table's reused-graph row.

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
- At these sizes a single timed pass does not reach steady-state JIT, and `--roadmap-quick` has 10 ns
  of resolution — a change worth measuring is routinely smaller than that. `FixedCostProbe`
  (`CERAS_FIXED=1`) takes the minimum of several long runs for that reason, and the `--roadmap-quick`
  rows have been seen to report a real 55% change as 12%. Prefer it for deltas; treat the table for
  shape.
- A microbenchmark that calls a method through an interface it only ever sees one implementation of
  will report that the interface call is cheaper than it is, and that sealing the type helps. Both
  were measured here and neither survived contact with the real encode: the microbenchmark's call
  site is specialised by guarded devirtualization because it sees one type, while the real call site
  inside `SerializerBinary` is shared across every formatter and sees several. Sealing
  `FixedArrayBufferWriter` moved the microbenchmark by 10% and the real encode by nothing
  (2.056 → 2.048 ns/field). `DevirtSpike` (`CERAS_SPIKE=1`) is the experiment; a number from a
  benchmark of that shape is a ceiling, not a prediction.
