Ceras
===
[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/rikimaru0345/Ceras?branch=master&svg=true)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts)  [![Test Results](https://img.shields.io/appveyor/tests/rikimaru0345/ceras.svg)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/tests) [![Discord](https://discordapp.com/api/guilds/367211057787305985/embed.png)](https://discord.gg/FGaCX4c) [![NuGet](https://img.shields.io/nuget/v/Ceras.svg?logo=nuget&logoColor=ddd)](https://www.nuget.org/packages/Ceras/)  [![Release](https://img.shields.io/badge/download-70kb%20%5Brelease.zip%5D-blue.svg?logo=appveyor )](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts) 

Ceras is a binary serializer. It converts an object graph into a `byte[]` and back.
It goes above and beyond in terms of features, speed, and comfort.
Supports reference loops and inheritance chains. It does **not** support every .NET type — see
[When should I not use this?](#when-should-i-not-use-this).

## Direction of the next implementation

Design targets, **not** a description of the current release. What is implemented today is claimed
elsewhere on this page; the open items live in `TODO.md`. In short: the robust format has a
self-describing layout, a generated fast path and a streaming migration interpreter, but the
old-data graph, compact-encoding overrides, a dedicated networking mode, Godot export testing and
NativeAOT support are all still to do.

## Quick start

```csharp
using Ceras;

class Person { public string Name; public int Age; }
var p = new Person { Name = "riki", Age = 5 };

// Fast / transient: networking, undo stacks. No schema header, wire format is not
// stable across builds, so do not persist it.
var fast = new CerasFastSerializer();
byte[] packet = fast.Serialize(p);
var copy = fast.Deserialize<Person>(packet);

// Robust / persistent: savegames and anything that must survive game updates.
// `UseGeneratedFormatters(context)` is emitted by the source generator for your assembly;
// each named context has its own finite manifest and layout fingerprint (see below).
var robust = new CerasRobustSerializer(config => config.UseGeneratedFormatters("SaveGame"));
byte[] data = robust.Save(p, robustVersion: 1);
var p2 = robust.Load<Person>(data, currentRobustVersion: 1);
```

1. [**>> Many more examples in the code tutorial**](https://github.com/rikimaru0345/Ceras/blob/master/src/Tutorial/Tutorial.cs)
2. [**>> Detailed guides for specific scenarios on my blog**](https://www.rikidev.com/)
3. [**>> Read 'Optimization & Usage Pitfalls'**](https://github.com/rikimaru0345/Ceras/wiki/Optimization-&-Pitfalls)
- [**>> Changelog v3.0 (rikidev.com/new-features-in-ceras-3-0-2)**](https://www.rikidev.com/new-features-in-ceras-3-0-2/)
- [**>> New features in v4.0 (https://github.com/rikimaru0345/Ceras/releases/tag/4.0)**](https://github.com/rikimaru0345/Ceras/releases/tag/4.0)


## Features

- Very fast, small binary output (see **[Performance](https://github.com/rikimaru0345/Ceras#performance-benchmarks)**)
- Supports a wide range of .NET types:
	- [Hand-written formatters for all common .NET types](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features#built-in-types)
	- Generates new formatters for user types at compile time (source generator)
	- [Very easy to extend and customize](https://www.rikidev.com/extending-ceras-with-a-custom-formatter/)
  - **Not** supported: delegates/`Action`/`Func`, reflection metadata (`MethodInfo`,
    `PropertyInfo`, ...), enumerators, `Assembly`, and engine-native objects such as Unity's
    `UnityEngine.Object` — see [When should I not use this?](#when-should-i-not-use-this).
- Reference persistence, including circular references. One documented limit: a cycle through
  `readonly` members that can only be built through constructor parameters is rejected with an
  actionable diagnostic unless an explicit construction strategy (`[CerasConstructor]` or a
  `TypeConstruction`) can build it.
- Polymorphism, inheritance and interfaces. In the robust format this is scoped to what exists at
  compile time: a context has a finite manifest, so every concrete subtype must be declared, and a
  type that only appears at runtime is an error rather than a silent fallback.
- **New: Specialized Robust format** with version-tolerance, string interning, and schema-based migration.
- ***[full feature list (and planned features)](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features)***

## Robust format (Version Tolerant)

`CerasRobustSerializer` writes a self-describing **schema header** into every payload. That lets a
future version of your game add or remove fields, and rename fields, classes or enum members, and
still load old data — but a rename has to be *recorded*, or it is not recognised:

- a renamed **member** needs `[CerasStableName]` on the current member (a stable wire name) and/or
  `[PreviousName]` carrying the old one. Without an alias the old field is treated as removed (its
  bytes are skipped, reported in `MigrationContext.SkippedFields`) and the new member keeps its
  declared default.
- a renamed **class** needs `[PreviousTypeName]` with the old `[CerasSerializable("…")]` name.
  Without it the type does not resolve and the load fails rather than matching by structure.
- a renamed **enum member** needs either `[CerasStableName]` (pin the wire name once and the C# member
  can be renamed freely) or `[PreviousName]` carrying the old spelling — see
  [Enum Migration](#advanced-migration). Both schema paths record the *wire* name, so the two sides
  agree. What no attribute can do is recover a spelling the current build cannot derive: a member that
  was renamed in C# **and** repinned to a new wire name is unreachable from a file written before the
  schema recorded wire names, so that file's name has to be declared as a `[PreviousName]` too. A
  *deleted* enum member needs nothing: the value stays the number it was, and a save that carries it
  loads as an unnamed value rather than silently defaulting.

What is *not* covered: reparenting a type under a different base class, inlining or removing a
class, and lossy field conversions. Those make the load fail with a `CerasSchemaException` naming the
field and both types, instead of being guessed at.

### Why use it?
- **Robust Versioning**: Add/remove fields, and rename fields, classes or enum members between
  game versions, as long as the renames carry the matching alias attribute (see above).
- **Fast Path**: When the schema matches, generated formatters read the data directly.
- **Slow Path / Migration**: On a schema mismatch, a schema-traversal engine migrates the data.
- **No runtime code generation**: formatters are ordinary C# emitted by a Roslyn source generator.
  Nothing calls `Expression.Compile` or emits IL at runtime, which is the prerequisite an AOT host
  needs — but see [Source Generator](#source-generator-no-runtime-codegen) for what is
  *not* verified.
- **Space Efficient**: Includes built-in **string interning** (duplicate strings are stored once).

### Quick start (3 steps)

**1. Annotate your save types (and declare a context):**

```csharp
[CerasContext("SaveGame")]        // declares the "SaveGame" context with Player as a root
[CerasSerializable]
[PreviousTypeName("PlayerEntity")] // handle class renames
public class Player
{
    [CerasStableName("Hp")] // stable wire name; the C# member can be renamed freely
    public float HitPoints;

    [PreviousName("Experience")] // handle field renames
    public int Level;
}
```

A type is part of a context when it is a `[CerasContext]` root of it or is reachable from one
(member types, generic arguments, polymorphic subtypes). Interfaces and abstract base types should
be annotated too, so the generator discovers their concrete subtypes (needed for polymorphic
fields). See [Polymorphic / runtime-only types](#polymorphic--runtime-only-types) and
[Serialization contexts](#serialization-contexts).

**2. Create the serializer and register the named context once:**

```csharp
using Ceras;
using Ceras.Generated;

var ceras = new CerasRobustSerializer(config => config.UseGeneratedFormatters("SaveGame"));
```

**3. Save and Load:**

```csharp
uint gameVersion = 1;   // a monotonic application version of your own

// Save
byte[] data = ceras.Save(myGame, gameVersion);

// Load (automatically migrates if the save came from an older schema)
var game = ceras.Load<Game>(data, gameVersion);
```

(This snippet compiles either way — but `Game` must be a `[CerasContext("SaveGame")]` root, or
reachable from one, or the first `Save`/`Load` throws a `CerasSchemaException` saying it is not
declared in the selected context. That is a runtime declaration error, not a compile error.)

The `gameVersion` is a monotonic application version. Loading data written by a *newer* version
throws; older data is migrated automatically.

Separately, a save produced by a *newer build* is rejected before the payload is decoded, even when
the layouts match. That check needs the owning build to declare its timestamp with
`[CerasBuildTimestamp]`; without it the timestamp is `0`/unknown and the check cannot run.

### Advanced Migration

- **Field Mapping**: An old field is matched against the current member's *stable wire name* — its
  `[CerasStableName]`, or the C# name when the attribute is absent — and then against its
  `[PreviousName]` aliases. There is no third fallback: an unmatched old field is reported in
  `MigrationContext.SkippedFields`.
- **Type Mapping**: Classes and structs are matched by their `StableName` (from `[CerasSerializable]`), then by `[PreviousTypeName]` aliases.
- **Enum Migration**: Enums are mapped by **name** rather than integer value, so members can be
  reordered or renumbered safely. The name is the member's *stable wire name* — its
  `[CerasStableName]`, or the C# name when the attribute is absent — and it is the name the schema
  records, so the two always agree. A member renamed in C# while keeping its `[CerasStableName]`
  therefore migrates on its own; one renamed *without* a pinned name needs `[PreviousName]`. A current
  C# member name is also accepted, because that is what the schema recorded before wire names were
  written, so a file from such a build still matches a member that kept its C# name. An old member
  whose name no longer matches at all **keeps its numeric value**, reinterpreted in the current enum —
  it does *not* reset to the default, so a removed (or repinned) member can arrive as an undefined
  enum value.
- **Numeric Coercion**: Exactly five widening conversions are applied automatically:
  `Int16 -> Int32`, `Int32 -> Int64`, `Int32 -> Single`, `Int32 -> Double`, `Single -> Double`.
  Any other retype (e.g. `double` -> `decimal`) makes the **load fail** with a
  `CerasSchemaException` naming the field and both types, rather than silently dropping the value.
  (Treating that as a removed field would leave the declared default and lose data quietly, so
  Ceras refuses instead.)

To react to a migration, add a method that takes a `MigrationContext`. It runs on **both** load
paths — the fast path passes a context whose `WasMigrated` is `false` — so guard on that flag:

```csharp
using Ceras.Format;   // MigrationContext lives here

[OnAfterDeserialize]
void OnMigrated(MigrationContext ctx)
{
    if (ctx.WasMigrated)
    {
        Console.WriteLine($"Loaded save from game version {ctx.OldGameVersion}");
        foreach (var field in ctx.AddedFields)   /* ... init defaults ... */ ;
        foreach (var field in ctx.SkippedFields) /* ... log warnings ... */ ;
    }
}
```

> Note: both `[OnBeforeDeserialize]` and `[OnAfterDeserialize]` are invoked by the generated
> fast-path formatter too, exactly once per loaded instance — but they are **not** symmetric:
>
> - `[OnBeforeDeserialize]` always runs inline, before the instance's own members are overwritten, so
>   it sees the **pre-load state**: constructor and field-initializer state on a freshly constructed
>   instance, or the values the instance already held when `Load(ref existing, …)` reuses it. It is
>   never deferred, for any type.
> - `[OnAfterDeserialize]` runs after the members are read. For **reference types** it is further
>   deferred until the whole root graph is populated, so it can inspect parent, sibling, shared and
>   cyclic references. For a **struct** it cannot be deferred and runs inline as soon as the value
>   is read.
>
> Taking a `CerasSerializer` parameter instead of a `MigrationContext` is supported, and gives you
> `UserContext`.
>
> The corresponding **serialize** callbacks (`[OnBeforeSerialize]`, `[OnAfterSerialize]`) are invoked by
> the generated formatters as well, once per written instance, bracketing that instance's member writes:
>
> - `[OnBeforeSerialize]` runs before the first member is read, so whatever it writes into the object is
>   what ends up in the stream.
> - `[OnAfterSerialize]` runs after the last member has been written, so it can finish the state the
>   writer just captured — but anything it changes now is *not* in the stream.
>
> They accept no parameter or a `CerasSerializer` (for `UserContext`). A `MigrationContext` parameter is
> rejected at compile time (`CERAS028`): it describes a migration, and there is none while saving. So is
> any other shape Ceras cannot invoke — a non-void return, a `static` or generic method, an `async void`
> method (it would return at its first `await` and could no longer be ordered around the member writes), or
> a `ref`/`out`/`in` parameter (a callback parameter is always passed by value). Every such signature is
> reported rather than silently never called.

## Performance benchmarks
Ceras generally ranks at the top end of the performance spectrum, together with NetSerializer and MessagePack-CSharp.
To get an idea of how Ceras performs here are the preliminary benchmark results.
The resulting binary size is about the same as MessagePack-CSharp.

![Single object performance benchmark](https://i.imgur.com/Q896UgV.png)

> **Historical and unverified.** This chart predates the current source generator, the robust format
> and the .NET 10 port, and it has not been regenerated — treat the numbers as indicative only, not
> as a measurement of the current release.

For the **current** code, the in-repository tool is
[`samples/Ceras.Benchmarks`](samples/Ceras.Benchmarks/README.md). It measures the current
robust save/load, isolated payload costs, small network messages, cold construction vs steady state,
the migration path, and the per-section costs (CRC, schema, string table, payload copy):

```sh
dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-quick   # table + self-checks
dotnet run --project samples/Ceras.Benchmarks -c Release -- --roadmap-bench   # BenchmarkDotNet
```

It runs the self-checks first and exits non-zero if any fails, so a number is never reported for a
build that does not actually round-trip.

I encourage you to not only try it yourself, but to also provide feedback about scenarios you had good and bad results with.

Don't forget to tune the settings in `CerasConfig` for your specific situation.
Using Ceras to read/write network packets might require different settings than, lets say, saving a settings-object to a file, or persisting items/spells/monsters in a game, or ... 

The project is still heavily work-in-progress, meaning that over time more optimizations will get implemented (your feedback is important here!).

## What can this be used for?

### Example usages
The goal is a general-purpose serializer usable in many situations.
Personally my primary intentions were easy object persistance and network communication.

Examples:
- **Settings:**
Saving objects to disk quickly without much trouble: settings, robust formats, whatever it  is.
The transient mode needs no configuration beyond the annotations; the robust mode does — it needs a
declared context (`[CerasContext]` plus `config.UseGeneratedFormatters("…")`).
See steps 1 and 2 in the [Usage Guide](https://github.com/rikimaru0345/Ceras/blob/master/src/Tutorial/Tutorial.cs#L22)

- **Splitting:**
So your `Person` has references to other `Person` objects, but each one should be serialized individually!?

> **Not supported today.** The `IExternalRootObject` API that used to do this was removed, and
> there is no replacement in the library. Serialize each object in its own call and reconnect the
> references in your own code, or put the whole graph in one payload and treat it as an opaque
> `byte[]` on the wire. A first-class answer is still on the roadmap.

- **Network:**
`CerasFastSerializer`'s simple API makes it easy to drop into a protocol, and I wrote
[a short guide](https://rikidev.com/networking-with-ceras-part-1/) showing a basic TCP shape.
It is *not* the dedicated network mode the roadmap describes: the fast serializer still shares the
reference-tracking machinery with the object-graph format, so it carries graph costs into every
packet, and it performs no compatibility negotiation. Treat the guide as a starting point, not a
battle-tested networking system.

- **More:**
The above are just examples. Some shapes are out of scope however they are used — see
[When should I not use this?](#when-should-i-not-use-this).

### When should I not use this?

- If you need human readable output for some reason. For example some file that you want to be able to edit in a text-editor. For those usages JSON or XML are likely better suited.

- If you must serialize a type Ceras rejects outright. Delegates, `Action`/`Func`, reflection
  metadata (`MethodInfo`, `PropertyInfo`, `Assembly`, ...), enumerators and engine-native objects
  (anything under a `UnityEngine.Object`-style base) are **banned**.

  > **Known bug, not intended behaviour.** Today a banned type is *silently excluded*: a member whose
  > type is banned is dropped from the payload and comes back `null`/default with no error, and a
  > banned runtime value in a reference slot is written as `null`. A custom `IFormatter<T>` does not
  > override the ban, and `[Include]` forces inclusion but then fails at load. This is scheduled to
  > become a hard error; do not rely on it, and assert on a round-trip if it matters.

- If your robust save must serialize types that only exist at runtime. A robust context has a
  **finite** manifest baked at the first `Save`/`Load`; a type that is not in it is an error, not a
  fallback. The transient mode has no such restriction.

- ~~You plan to use this on a platform that does not support code generation.~~ Ceras does not
  generate code at runtime — no `Expression.Compile`, no runtime IL — so the **source generator**
  gives an AOT host ordinary C# formatters. That is the prerequisite, not a guarantee of support:
  Unity/IL2CPP integration is deferred and unverified, and the reflection fallback needs reflected
  members preserved, which trimming can remove. See
  [Source Generator](#source-generator-no-runtime-codegen) for the honest table.

- If you need a stable *wire* format from the transient mode. As the quick start above notes,
  `CerasFastSerializer` writes no schema header and its wire format is not stable across builds, so
  it is not for persistence. Only `CerasRobustSerializer` has a compatibility promise, and that
  promise starts with the format documented in [Format/LAYOUT.md](src/Ceras/Format/LAYOUT.md) —
  saves written by earlier experimental builds are not guaranteed to load.


## Source Generator (no runtime codegen)

Ceras ships a **Roslyn C# source generator** (`Ceras.SourceGenerator`) that emits `IFormatter<T>` implementations at **compile time**.
This replaces the old external AotGenerator tool and works automatically — no separate build step required.

### When do you need it?

| Scenario | ReflectionFallbackFormatter (default) | Source Generator |
|---|---|---|
| Normal .NET desktop/server | ✅ works out of the box | optional (skips reflection when resolving formatters) |
| NativeAOT / trimmed publish | ⚠️ **untested**; needs the members it reflects on preserved | ⚠️ **untested** |

> **Neither AOT row is a tested claim.** The test suite runs on desktop .NET only, this repository
> has no NativeAOT smoke test, and the package declares neither `IsAotCompatible` nor trimming
> annotations (`[DynamicallyAccessedMembers]`) and ships no `link.xml`. Ceras still reflects at
> runtime for formatter injection, object construction and the migration interpreter, so the
> generated formatters remove one reflection cost, not all of them. Treat AOT/IL2CPP as a design
> goal, not a supported platform.
>
> **Unity/IL2CPP is deferred and unverified** (see TODO.md). `src/Ceras.UnityAddon` still exists in
> the solution but contains no formatter code, and the package is not tagged for Unity. Native Unity
> objects are on the banned-type list, so a member of that type is excluded from the payload rather
> than written — the silent-exclusion bug described below. The intended editor/export integration
> target is Godot; exercising `samples/GodotIntegration` in editor play, across assembly reload and
> in an export is still open work.

> **Breaking change (generator diagnostics):** shapes the generator cannot access — currently a
> member whose *type* generated code cannot name (e.g. a private nested type) — now report
> `CERAS018` as an **error** instead of emitting silently-broken output. A merely private member
> with a nameable type still works through `[UnsafeAccessor]`. Mark the member `[Exclude]`, make
> the type/member accessible, or provide a custom formatter to resolve it.

### Quick setup (2 steps)

**1. Reference the source generator in your `.csproj`:**

```xml
<ItemGroup>
  <ProjectReference Include="..\Ceras.SourceGenerator\Ceras.SourceGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

**2. Annotate the types you want pre-generated formatters for, and declare their context:**

```csharp
using Ceras;

// [CerasContext] makes Player a root of the "SaveGame" context; every type reachable from it
// (members, generic arguments, polymorphic subtypes) joins the same context.
[CerasContext("SaveGame")]
[CerasSerializable]
public class Player
{
    public string Name;
    public int    Score;
}

// Or use the generic version anywhere (e.g. at the assembly level) to generate formatters
// for external types you don't control. Assembly-level declarations join every declared
// context by default; [CerasKnownType] also accepts an explicit context name:
//   [assembly: CerasKnownType<List<Hidden>>("SaveGame")]
[assembly: CerasSerializable<ExternalLibrary.UserType>]
[assembly: CerasKnownType<List<Hidden>>]
```

> Assembly-level declarations with **no** context name join *every* declared context, so adding one can
> change more than one manifest (and more than one fingerprint) at once. Name the context explicitly
> when you only mean one of them.

> **Registration**: call the generated `RobustFormatters.UseRobustFormatters(config, "SaveGame")`
> (or `config.UseGeneratedFormatters("SaveGame")`) after constructing the config, passing the
> context name. There is no implicit default context. Ceras does not scan for generated formatters
> automatically — a serializer in a library assembly cannot see the formatters emitted for your
> assembly. `RobustFormatters.ContextNames` lists the contexts declared in the current compilation,
> and passing an unknown name throws an error with that list.
>
> Because there is no default context, a compilation that shows serialization intent (it annotates
> a type with `[CerasSerializable]`/`[CerasContext]`, or declares an assembly-level
> `[CerasSerializable<T>]`/`[CerasKnownType]`) but declares **no** `[CerasContext]` fails the build
> with `CERAS025`. A project that references the generator without any such declaration is left
> alone.

Generated formatters change *how* values are read and written. A custom formatter is the exception:
it owns its own bytes, which the schema does not describe (see
[Polymorphic / runtime-only types](#polymorphic--runtime-only-types)).

### Serialization contexts

A **context** is a named, finite set of serializable types that share one generated manifest, one
deterministic type-ID space, and one layout fingerprint. You select it by name when configuring a
serializer. The generator discovers each context independently from its own roots, so selecting a
context registers *only* that context's types; types belonging only to another context are not in
its manifest (and are rejected as undeclared roots).

Why more than one?
- A **persistent savegame** context must stay readable across releases, so you want a frozen type
  set and no accidental coupling to unrelated types. Adding a message type elsewhere must not change
  the save-game fingerprint and invalidate existing saves.
- A **transient network** context has no compatibility promise, may change every build, and should
  not drag the save-game schema (or its version gates) into every packet.

Splitting them gives each independent evolution, smaller manifests, and precise undeclared-type
errors. A type can belong to several contexts at once: name each context on `[CerasContext]`, or
make it reachable from roots of each. There is no context composition at runtime: one serializer
config uses exactly one context.

```csharp
[CerasContext("SaveGame")] public class Player { public string Name; }
[CerasContext("Network")]  public class ChatMessage { public string Text; }

var save = new CerasRobustSerializer(c => c.UseGeneratedFormatters("SaveGame"));
var net  = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Network"));
// save knows Player (and its closure) but not ChatMessage, and vice versa.
```

Type IDs are **context-local**, so the same CLR type does not necessarily get different numbers in
two contexts — the id spaces are assigned independently and can coincide. What identifies a context
is its **layout fingerprint**, which covers the whole id space, so a save written under one context
does not match the other and must be loaded under the context it was written with.

### Viewing and Debugging Generated Code

Source generators run during compilation, and their output isn't always visible in your file explorer. Here is how to see what Ceras is writing:

**1. Using your IDE**:
- **Visual Studio**: Expand your project -> Dependencies -> Analyzers -> Ceras.SourceGenerator -> RobustFormatters.g.cs.
- **Rider**: Expand your project -> Scratches and Consoles -> Extension Generated Files -> Ceras.SourceGenerator.

**2. Persisting files to disk**:
To see the files in your project folder, add these properties to your `.csproj`:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

This will save the generated C# code to `obj/Generated` (or your specified path), allowing you to set breakpoints, inspect the logic, or verify that all members are being handled correctly.

### Polymorphic / runtime-only types

The source generator only sees types that exist at compile time.
For polymorphic scenarios (serializing an interface or base-class field whose concrete type varies at runtime), annotate **all concrete derived types** with `[CerasSerializable]`:

```csharp
[CerasSerializable] public class Cat : Animal { ... }
[CerasSerializable] public class Dog : Animal { ... }
// Animal itself also needs one if it is ever serialized directly:
[CerasSerializable] public abstract class Animal { ... }
```

Subtypes are still discovered automatically when they are reachable from an annotated base, but that
propagation is implicit — the generator emits `CERAS016` (a warning) for a polymorphic member whose
base type is not itself annotated. Annotate the base to make the intent explicit.

If a concrete type is only known at runtime (e.g. loaded from a plugin assembly), it cannot be
source-generated. What you can do then depends on the mode:

- **Transient mode** (`CerasFastSerializer`): a generated formatter is not required. A type with no
  generated formatter falls back to `ReflectionFallbackFormatter` while
  `config.AllowReflectionFallback` is `true` (the default; it is marked `[Obsolete]` and is being
  phased out), or you can write a hand-crafted `IFormatter<T>` and register it via
  `config.ConfigType<T>().CustomFormatter = new MyFormatter();`. Either way the wire format is
  whatever those formatters write, with no schema describing it.
- **Robust mode** (`CerasRobustSerializer`): a runtime-only type is refused, with a different error
  depending on where it appears. As the **root** type it is a `CerasSchemaException` ("not declared
  in the selected serialization context"); as a **polymorphic value** inside a declared type it is a
  `CerasException` from the type formatter ("appears as a polymorphic value during serialization but
  is not registered in the schema registry"). A context's manifest is finite and baked at the first
  `Save`/`Load`, and a custom formatter overrides the bytes for a type *already* in the manifest
  rather than adding to it — so the type must be declared at compile time with
  `[CerasSerializable]`/`[CerasKnownType]` (which is why plugin types generally need a compile-time
  reference), or that subgraph has to travel as an opaque `byte[]` your own code owns. A custom
  formatter's bytes are never described by the schema, so a save containing one has no automatic
  upgrade guarantee.

# Support
- Open an issue
- Join my [Discord](https://discord.gg/FGaCX4c) (probably the best for direct one-on-one help)
- Make sure you've read [FAQ](https://github.com/rikimaru0345/Ceras/wiki/FAQ) and [Optimization & Pitfalls](https://github.com/rikimaru0345/Ceras/wiki/Optimization-&-Pitfalls)

## [**>> FAQ**](https://github.com/rikimaru0345/Ceras/wiki/FAQ)
## [**>> Development Blog**](https://rikidev.com/)
