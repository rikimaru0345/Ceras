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

The gameplay message loop adds exactly one thing that is not `src/Ceras`: two source files
compiled into this project *and* into `tests/Ceras.Test` from `shared/GameplayLoop` — the fake
duplex transport and the loop measurement helper. They are a source link, not a project
reference, so the dependency runs in neither direction; see `shared/GameplayLoop/README.md` for
why. Nothing else outside `src/Ceras` is referenced.

## Running it

Always `-c Release`; a Debug build's numbers are not comparable with anything.

```sh
# Correctness only: verifies the behaviour the measurements assume, exits non-zero on failure.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --self-check

# Self-checks, then a deterministic ms/op and B/op table (no BenchmarkDotNet). The default mode.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-quick

# The gameplay message loop: self-checks, then a verified client/server exchange table.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --network-loop

# The BenchmarkDotNet harness: RobustSaveLoadBenchmarks, NetworkTransientBenchmarks,
# ColdInitBenchmarks, NetworkLoopBenchmarks.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-bench
```

`--roadmap-bench` forwards any further arguments to BenchmarkDotNet, e.g.
`--roadmap-bench --filter *Load* --job short`, or `--roadmap-bench --filter *NetworkLoop*`.

`--self-check`, `--roadmap-quick` and `--network-loop` print a pass/fail line per check and exit
non-zero if any fails, so they double as a smoke test: a measurement of a broken build is worse than
none. `--network-loop` also runs the loop for real and refuses to print its table at all if the
exchange did not verify.

## The gameplay message loop (`--network-loop`)

```sh
# 200 000 exchanges per pass, 5 passes, ~6 s.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --network-loop

# A million exchanges per pass, or any other size.
dotnet run --project samples/Ceras.Benchmarks -c Release -- --network-loop --exchanges 1000000
dotnet run --project samples/Ceras.Benchmarks -c Release -- --network-loop --exchanges 50000 --passes 9
```

What it measures is a client and a server, in one process, on one thread, talking over an in-memory
duplex transport: the client encodes an input sample, frames it, the server decodes it, checks it,
moves the player and encodes authoritative state, and the client decodes that and checks it. Two
transient serializers, one per endpoint, never shared — a configured `CerasSerializer` owns mutable
per-operation state, so one instance for both ends would be two writers into the same state.

| Row | What it is |
|---|---|
| `Transport only (no Ceras)` | framing and moving the same bytes, both directions, with no serialization in the path — the floor the rest is read against |
| `Encode only (client)` | one pooled write of one input; the convenience `Serialize(obj)` overload is not used, because it allocates its result |
| `Decode only (server)` | one read of one input out of already-encoded bytes |
| `One way (encode+move+decode)` | the client-to-server half, verified |
| `Full exchange (ping-pong)` | the whole loop: input out, state back, both directions verified |
| `Chat encode/decode` rows | the same four questions for a reference-typed message carrying a string |

The two messages are chosen to be the two shapes a game actually sends. `PlayerInput` and
`ServerState` are fixed-size structs — 24 and 25 bytes encoded, every member a fixed-width
primitive, no reference members, and no null flag because a struct has no null to flag. Nothing on
the wire has a length that has to be discovered, and that is what makes them the case a
zero-allocation claim is about. `ChatLine` is a class with a `string`, and it is measured and
**reported** rather than asserted to be free: encoding reuses the instance the caller already holds
and costs nothing, while decoding a string out of bytes always makes a new one, and a target nobody
held also has to be created. The two decode rows differ by exactly what reuse of the target is
worth, which is a measured difference rather than an assumed one.

The transport is `shared/GameplayLoop/FakeDuplexTransport.cs`, compiled into this project *and* into
`tests/Ceras.Test`, so the smoke test and this table are about the same bytes moving the same way. It
is two bounded byte lanes with a little-endian `uint` length prefix per frame, allocated once. There
is no `MemoryStream` and no per-message array anywhere in the path — both of which allocate, and
either of which would have been charged to the serializer's row. Its buffers are the transport's own
and are not pooled rentals, and the loop keeps them and its two pooled write buffers as four separate
objects; `PooledBufferOwnershipTests` and
`GameplayMessageLoopTests.PooledWriteBuffersAndTransportBuffersStayDistinct` pin that separation.

`--network-loop` runs 200 000 exchanges per pass by default and takes a few seconds. The gate for
the property itself is in the test suite instead:
`tests/Ceras.Test/Tests/GameplayMessageLoopTests.cs`, which does the same loop with 128 warm-up
iterations and 64 measured exchanges and asserts zero bytes, so it costs a fifth of a second and runs
before every commit.

### An example run

.NET 10.0.11, workstation GC, x86-64, best of 5 passes of 200 000 exchanges. **These are one machine's
numbers on one day**, quoted so the shape of the table is visible; they are not a baseline, and a
change to `src/Ceras` is expected to move them.

```
workload                            ns/op       ops/s     B/op    total B
------------------------------------------------------------------------------
Transport only (no Ceras)            21.4  46,652,018     0.0          0
Encode only (client)                 18.8  53,169,939     0.0          0
Decode only (server)                 24.4  40,960,977     0.0          0
One way (encode+move+decode)         50.4  19,832,298     0.0          0
Full exchange (ping-pong)           103.3   9,677,893     0.0          0
Chat encode (reused instance)        61.2  16,341,092     0.0          0
Chat decode (fresh target)           82.0  12,198,875    64.0  64,000,000
Chat decode (reused target)          82.2  12,160,735    32.0  32,000,000
Full chat exchange (reused)         302.9   3,301,780    64.0  64,000,000
```

The two struct rows are the point: **a complete client/server exchange — two encodes, two frames, two
decodes and every comparison in between — costs about 103 ns and allocates nothing**, on this thread,
in this process. The chat rows are the honesty check: reuse on the write side is free, and reading a
string out of bytes is not. BenchmarkDotNet's independent `Allocated` column reports the same
0 / 64 B / 32 B for those three rows, from a different counter implementation
(`--roadmap-bench --filter *NetworkLoop*Chat* --job short`).

`total B` is the raw `GC.GetAllocatedBytesForCurrentThread` delta over all five passes of 200 000
operations, so `B/op` above is that number divided and the division can be checked. The zeros are
literally `0` — not a small figure that rounds.

### What the numbers do and do not say

- **B/op is measured, not assumed.** Every row prints the raw counter next to the divided figure, so
  the division can be checked. Zero really is zero: the counter's delta over a whole pass is `0`, not
  a small number that rounds.
- **The zero is about one thread.** `GC.GetAllocatedBytesForCurrentThread` counts the measuring
  thread's allocations only. Work done on another thread — or by a socket's kernel buffers, or by the
  other endpoint of a real connection, which is another process with its own serializers — is not
  counted and would not show up here.
- **The transport is not a socket.** No kernel, no syscall, no packet, no partial read, no slow-peer
  backpressure, no Nagle, no encryption, no scheduler. A real link's cost is not this cost, and a
  real roundtrip is two machines.
- **Each row is warmed by time, not by iteration count.** Tiered compilation leaves a newly-called
  method at unoptimized tier-0 and then suspends call counting for 100 ms
  (`TC_CallCountingDelayMs`), so an iteration-count warm-up of a fast loop finishes before promotion
  can start. That is not a rounding error: measured here, a 20 000-iteration warm-up put the
  two-frame transport row at 95.7 ns/op instead of 32.4 ns/op, and — worse than the size of the
  error — made a full exchange come out *faster* than the one-way exchange it contains, because the
  earlier rows had warmed the methods the later rows call. `LoopMeasurementRunner.WarmUp` therefore
  warms for at least 500 ms of wall clock as well as a minimum iteration count.
  `--roadmap-quick` is not changed by this and keeps its short warm-up, so **its per-operation
  timings are upper bounds for the fast rows**; its B/op column is unaffected, because a count does
  not depend on how fast the code is.
- **Every row pays one delegate invocation**, so the fastest row is over-reported by a nanosecond or
  two.
- **A negative difference between rows is normal.** Each row is its own tight loop, so a part
  measured alone pays a call and a cold-ish working set it does not pay while running alongside the
  rest of the exchange. The differences attribute cost; they are not a budget that adds up.

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
- `--roadmap-quick` also warms up by iteration count rather than by time, so its **per-operation
  timings for the fastest rows are upper bounds**: tiered compilation leaves a newly-called method at
  unoptimized tier-0 and suspends call counting for 100 ms, which a few thousand iterations of a
  sub-microsecond row finish before promotion can start. Its `B/op` column is unaffected — a byte count
  does not depend on how fast the code is. `--network-loop` warms up by time for the reason given
  above, and BenchmarkDotNet does its own.
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
