# shared/GameplayLoop

Source files here are **compiled into** two projects, and into nothing else:

| File | Compiled into |
|---|---|
| `FakeDuplexTransport.cs` | `tests/Ceras.Test` and `samples/Ceras.Benchmarks` |
| `LoopMeasurement.cs` | `tests/Ceras.Test` and `samples/Ceras.Benchmarks` |

Both `.csproj` files pull them in with an explicit `<Compile Include>`, so neither project copies the
file and neither references the other.

## Why this file is here and not in one of them

The xUnit smoke test and the roadmap benchmark both have to move bytes between a client and a
server, and they are only worth anything together if they move the bytes the *same* way. A
transport that quietly reallocated per message, forgot its length prefix, or handed out a span into
a buffer it was about to overwrite would show up as a suspiciously good number in whichever project
owned it, and the other project would keep reporting a healthy one. One definition, two consumers,
is what stops that: the test then pins the exact code the benchmark times, and a change to the
transport has to pass both.

The obvious alternative - putting it in `tests/Ceras.Test` and referencing that project - is the one
this deliberately does not do. `samples/Ceras.Benchmarks` references only `src/Ceras` and the source
generator so that changing or deleting the test suite cannot move its numbers (see that project's
README); a compile-time link from the test tree would give the benchmark a dependency on the very
thing it is meant to be independent of, without making the dependency visible in the project graph.

## What is deliberately *not* here

The message types and the ping-pong loop itself. A Ceras serialization context is a per-compilation
artifact: `[CerasContext("…")]` names a manifest that the source generator emits into whichever
assembly the type is compiled into, and two generator-enabled assemblies cannot both emit
`Ceras.Generated.CerasConfigExtensions` (CS0121). A message type shared by both projects would have to
be declared with one context name, and only one of the two projects could use it. So each project
declares its own messages in its own context - `CerasBenchmarks` for the tool, `Tests` for the
suite - and each gets its own concrete, non-abstract loop. The loops are deliberately *not* shared
behind a base class or an interface either: the measured path should be the direct call, because this
repository has already been bitten once by a microbenchmark whose interface call site only ever sees
one implementation (see `samples/Ceras.Benchmarks/README.md`, "Interpreting the numbers").
