# `samples/TrimAotSmoke` — trimming / NativeAOT smoke test

A throwaway host that exercises the parts of Ceras a trimmer or the NativeAOT
compiler can break, and reports whether they still work. It is the executable
form of TODO.md Milestone 5's *"smoke-test realistic trimming/NativeAOT"*.

## Why it is a separate Exe

`PublishTrimmed` and `PublishAot` are meaningless on `src/Ceras/Ceras.csproj`:
a library has no entry point, so the trimmer refuses it outright —

```
ILLink : error IL1034: Root assembly 'Ceras, Version=4.1.7.0, ...' does not have entry point.
error NETSDK1144: Optimizing assemblies for size failed.
```

(`-p:PublishAot=true` on the library *does* exit 0 and writes a `Ceras.so`, but
that artifact is a shared object with no managed entry point, so it is not a
result — do not read it as one.) A real consumer Exe is the only shape in which
the trimmer's warnings and the AOT compiler's codegen actually mean something.

## Running it

```sh
./samples/TrimAotSmoke/run-smoke.sh              # all four configurations
./samples/TrimAotSmoke/run-smoke.sh trim aot     # only the named ones
```

| mode   | publish flags                              | isolates                        |
|--------|--------------------------------------------|---------------------------------|
| `jit`  | framework-dependent, no trimmer            | the control                     |
| `sc`   | self-contained, no trimmer                 | self-contained packaging        |
| `trim` | self-contained + `PublishTrimmed`          | **trimming**                    |
| `aot`  | `PublishAot` (implies trimming)            | **NativeAOT**                   |

Exit code is 0 only if every selected mode passed. Logs land in
`samples/TrimAotSmoke/artifacts/` (already covered by the root `.gitignore`).

Publishing `aot` takes minutes; the other three take seconds. Requirements are a
C++ toolchain for the AOT link step (plain `gcc`/`ld`/`objcopy` are enough —
**clang is not needed**) plus the ILCompiler/ILLink packages, which restore on
first run.

`PublishAot` / `PublishTrimmed` default to `true` **in the csproj**, deliberately
and not on the command line: a global `-p:PublishAot=true` is inherited by
`src/Ceras.SourceGenerator` (netstandard2.0, no entry point) and fails the whole
publish with `error NETSDK1207`. Override from the command line if you need to.

## What it checks

1. `CerasFastSerializer` raw round trip (generated formatters).
2. `CerasRobustSerializer` Save/Load round trip (schema header, interning,
   polymorphic slot).
3. Robust **schema migration** — a payload written at `robustVersion: 1` and read
   at `currentRobustVersion: 7`, which forces the reflection-heavy
   `SchemaTraversalEngine` instead of the generated fast path.
4. Reachability probes for the reflection entry points the trim analyzer flags in
   Ceras: `Type.GetType(string)`, `Assembly.GetType(string)`, non-public
   field/method probing, and — importantly — whether the constructors of the
   closed generic instantiations Ceras builds *only* through
   `Activator.CreateInstance(typeof(X<>).MakeGenericType(...))` survived
   trimming. These are `Report`ed, not asserted, so they inform the log without
   by themselves changing the exit code.

It is **not** in `Ceras.sln` and **not** in `dotnet test`, so the ordinary
build and test run are unaffected.

## Result on this checkout

`jit` and `sc` pass. `trim` and `aot` fail, for reasons that are in
`src/Ceras` and are listed in the report that accompanied this sample; the
interesting ones are

* `PublishTrimmed` → `MissingMethodException` from
  `Ceras.Helpers.Instantiator.Create` (`src/Ceras/Helpers/Instantiator.cs:15`),
  reached from `CerasSerializer.GetReferenceFormatter`
  (`src/Ceras/CerasSerializer.cs:682`). The site ILLink flags as **IL2067**.
* `PublishAot` → `PlatformNotSupportedException` in the `CerasRobustSerializer`
  constructor, which calls `Assembly.GetCallingAssembly()`
  (`src/Ceras/CerasRobustSerializer.cs:185` and `:200`).
* `PublishAot` → `NotSupportedException` on
  `Ceras.TypeConfig<Faction>..ctor`, from
  `CerasConfig.GetTypeConfigForConfiguration` (`src/Ceras/Config/CerasConfig.cs:101`).
  **This one is masked by the current probe set**: section 5 mentions
  `typeof(Ceras.TypeConfig<Faction>)`, and a single static mention of a closed
  generic is enough for ILC to emit native code for it. It was observed with
  that probe absent. Re-check it by deleting the `ReportCtor("TypeConfig<Faction>..ctor", …)`
  line — which is exactly the fix, applied on the consumer side, that a library
  change would have to make on its own.
