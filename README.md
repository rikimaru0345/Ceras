Ceras
===
[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/rikimaru0345/Ceras?branch=master&svg=true)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts)  [![Test Results](https://img.shields.io/appveyor/tests/rikimaru0345/ceras.svg)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/tests) [![Discord](https://discordapp.com/api/guilds/367211057787305985/embed.png)](https://discord.gg/FGaCX4c) [![NuGet](https://img.shields.io/nuget/v/Ceras.svg?logo=nuget&logoColor=ddd)](https://www.nuget.org/packages/Ceras/)  [![Release](https://img.shields.io/badge/download-70kb%20%5Brelease.zip%5D-blue.svg?logo=appveyor )](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts) 

Ceras is a binary serializer. It converts *any* object into a `byte[]` and back.
It goes above and beyond in terms of features, speed, and comfort.
Supports reference loops, large/complicated inheritance chains, splitting objects into parts, ...

## Direction of the next implementation

The following are design targets; the implementation and older examples below are still
being brought into alignment.

- Generate inspectable ordinary C# from annotated roots and their recursively discovered
  types. Explicit known-type declarations cover otherwise hidden types; undeclared runtime
  types, including closed generic combinations, are errors. No runtime IL generation.
- Target .NET 10 / C# 14, with Godot editor and exported-game integration tested.
  Unity-specific support is deferred. Source generation is motivated by debugging, maintenance, and optimization;
  NativeAOT compatibility is desirable.
- Support little-endian systems only; big-endian support and byte-order conversion are
  out of scope. Default to fixed-width primitives, with explicit compact-encoding overrides.
- Robust saves carry a self-describing layout. Matching layouts use generated readers
  without schema interpretation; upgrades can use an identity-preserving old-data graph
  without retaining historical game classes. Upgrade time and memory are secondary.
- Keep layout fingerprints, format versions, and producer-build ordering separate.
  Reject saves from newer builds. Semantic game versions and post-load fixes belong to
  application code. The next documented robust format begins the compatibility promise;
  experimental saves are excluded. Saves containing arbitrary custom payloads have no
  automatic upgrade guarantee; opaque framing is deferred.
- Networking negotiates compatibility at connection setup, with no per-message schema or
  protocol hashes. Optimize for graphs without cycles or reference tracking, raw strings,
  caller-owned buffers, and reusable/value-type messages. Context-specific custom formatters
  can transmit shared game-data references as IDs resolved to existing local objects.
  The network context performs no cycle detection; cyclic input is misuse and may stack
  overflow. Ordinary structs/reusable objects are supported; ref structs are not required.
- Robust supports mutable cycles; unsupported constructor-dependent immutable cycles get
  a diagnostic unless an explicit construction strategy is supplied.

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
byte[] data = robust.Save(p, gameVersion: 1);
var p2 = robust.Load<Person>(data, currentGameVersion: 1);
```

1. [**>> Many more examples in the code tutorial**](https://github.com/rikimaru0345/Ceras/blob/master/src/Tutorial/Tutorial.cs)
2. [**>> Detailed guides for specific scenarios on my blog**](https://www.rikidev.com/)
3. [**>> Read 'Optimization & Usage Pitfalls'**](https://github.com/rikimaru0345/Ceras/wiki/Optimization-&-Pitfalls)
- [**>> Changelog v3.0 (rikidev.com/new-features-in-ceras-3-0-2)**](https://www.rikidev.com/new-features-in-ceras-3-0-2/)
- [**>> New features in v4.0 (https://github.com/rikimaru0345/Ceras/releases/tag/4.0)**](https://github.com/rikimaru0345/Ceras/releases/tag/4.0)


## Features

- Very fast, small binary output (see **[Performance](https://github.com/rikimaru0345/Ceras#performance-benchmarks)**)
- Supports pretty much any type:
	- [Hand-written formatters for all common .NET types](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features#built-in-types)
	- Generates new formatters for user types at compile time (source generator)
	- [Very easy to extend and customize](https://www.rikidev.com/extending-ceras-with-a-custom-formatter/)
- **Full** support for reference persistence (including circular references!)
- **Full** support for polymorphism / inheritance / interfaces
- Can serialize objects into parts as "ExtenalObjects" (useful in many many scenarios)
- **New: Specialized Robust format Format** with version-tolerance, string interning, and schema-based migration.
- ***[full feature list (and planned features)](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features)***

## Robust format (Version Tolerant)

`CerasRobustSerializer` writes a self-describing **schema header** into every payload. That lets a
future version of your game add, remove, or rename fields (and even classes or enum members) and
still load old data.

### Why use it?
- **Robust Versioning**: Safely add/remove fields or rename classes between game versions.
- **Fast Path**: When the schema matches, generated formatters read the data directly.
- **Slow Path / Migration**: On a schema mismatch, a schema-traversal engine migrates the data.
- **AOT-Safe**: Both paths work on AOT platforms (no `Expression.Compile`).
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
uint gameVersion = 1;

// Save
byte[] data = ceras.Save(game, gameVersion);

// Load (automatically migrates if the save came from an older schema)
var game = ceras.Load<Game>(data, gameVersion);
```

The `gameVersion` is a monotonic application version. Loading data written by a *newer* version
throws; older data is migrated automatically.

### Advanced Migration

- **Field Mapping**: Fields are matched by their stable name (`[CerasStableName]`), then by `[PreviousName]` aliases, and finally by their C# name.
- **Type Mapping**: Classes and structs are matched by their `StableName` (from `[CerasSerializable]`), then by `[PreviousTypeName]` aliases.
- **Enum Migration**: Enums are mapped by **name** rather than integer value, so members can be reordered or renumbered safely. Unknown values reset to the default (0).
- **Numeric Coercion**: Common numeric upgrades (e.g. `int` -> `long`, `float` -> `double`) are handled automatically.

To react to a migration, add a method that takes a `MigrationContext`. It is called on the slow
path (only when the schema actually changed):

```csharp
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

> Note: a parameterless `[OnAfterDeserialize]` method is **not** invoked by the generated (fast-path)
> formatter. Only the `MigrationContext` overload is recognized, and only by the migration engine.
> Use a lifecycle hook (e.g. your own post-load pass) to repair non-serialized state on every load.

## Performance benchmarks
Ceras generally ranks at the top end of the performance spectrum, together with NetSerializer and MessagePack-CSharp.
To get an idea of how Ceras performs here are the preliminary benchmark results.
The resulting binary size is about the same as MessagePack-CSharp.

![Single object performance benchmark](https://i.imgur.com/Q896UgV.png)

The shown results are obtained from **[this code](https://github.com/rikimaru0345/Ceras/blob/master/samples/LiveTesting/Benchmarks.cs)** and I encourage you to not only try it yourself, but to also provide feedback about scenarios you had good and bad results with.

Don't forget to tune the settings in `CerasConfig` for your specific situation.
Using Ceras to read/write network packets might require different settings than, lets say, saving a settings-object to a file, or persisting items/spells/monsters in a game, or ... 

The project is still heavily work-in-progress, meaning that over time more optimizations will get implemented (your feedback is important here!).

## What can this be used for?

### Example usages
The primary goal is to make an universal serializer that can be used in every situation.
Personally my primary intentions were easy object persistance and network communication.
I've added many features over time and whenever someone can think of a good scenario that should be supported as well I'll make it happen. 

Examples:
- **Settings:**
Saving objects to disk quickly without much trouble: settings, robust formats, whatever it  is. With pretty much zero config.
See steps 1 and 2 in the [Usage Guide](https://github.com/rikimaru0345/Ceras/blob/master/src/Tutorial/Tutorial.cs#L22)

- **Splitting:**
So your `Person` has references to other `Person` objects, but each one should be serialized individually!? (without the references quickly dragging in essentially your whole program).
Maybe you want to be able to put each `Person` into its own file, or send them over the network one-by-one as needed?
**No problem!** Using `IExternalRootObject` it's not an issue! See [External Objects Guide (Game DB example))](https://github.com/rikimaru0345/Ceras/blob/master/src/Tutorial/Tutorial.cs#L350).

- **Network:** 
Because of its simple API and vast set of features Ceras is uniquely suited to implement a full 'network-protocol' for you.
I wrote [a short guide](https://rikidev.com/networking-with-ceras-part-1/) that shows off how a basic  TCP implementation could look like:
Just `Send(myObject);` it, then `var obj = await Receive();` on the other side, that's it! It literally can't get any easier than that.
At the moment the guide only has 2 parts, but when I have some (and if there are requests for it) I'd like to continue the series, eventually building that sample into a full-fledged, robust, and battle-tested networking system.

- **More:**
The above are just examples, Ceras is made so it can be used in pretty much every situation...

### When should I not use this?

- If you need human readable output for some reason. For example some file that you want to be able to edit in a text-editor. For those usages JSON or XML are likely better suited.

- ~~You plan to use this on a platform that does not support code generation.~~ Ceras supports IL2CPP / AOT platforms via its **compile-time source generator** — see [Source Generator (AOT / IL2CPP)](#source-generator-aot--il2cpp) below.


## Source Generator (AOT / IL2CPP)

Ceras ships a **Roslyn C# source generator** (`Ceras.SourceGenerator`) that emits `IFormatter<T>` implementations at **compile time**.
This replaces the old external AotGenerator tool and works automatically — no separate build step required.

### When do you need it?

| Scenario | ReflectionFallbackFormatter (default) | Source Generator |
|---|---|---|
| Normal .NET desktop/server | ✅ works out of the box | optional (slightly faster startup) |
| NativeAOT (`dotnet publish`) | ✅ works (reflection fallback) | ✅ recommended (faster) |

> Unity-specific integration is currently **deferred** (see TODO.md); the historical
> `Ceras.UnityAddon` project is not maintained or verified.

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

Generated formatters only change *how* values are read and written; the wire format is unchanged.

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
make it reachable from roots of each. Each context still gets its own manifest entry, type ID, and
fingerprint for that type. There is no context composition at runtime: one serializer config uses
exactly one context.

```csharp
[CerasContext("SaveGame")] public class Player { public string Name; }
[CerasContext("Network")]  public class ChatMessage { public string Text; }

var save = new CerasRobustSerializer(c => c.UseGeneratedFormatters("SaveGame"));
var net  = new CerasRobustSerializer(c => c.UseGeneratedFormatters("Network"));
// save knows Player (and its closure) but not ChatMessage, and vice versa.
```

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

If a concrete type is only known at runtime (e.g. loaded from a plugin assembly), it cannot be source-generated. In that case you have two options:
- Let `ReflectionFallbackFormatter` handle those types as a fallback (uses reflection).
- Write a hand-crafted `IFormatter<T>` and register it via `config.ConfigType<T>().CustomFormatter = new MyFormatter();`.

# Support
- Open an issue
- Join my [Discord](https://discord.gg/FGaCX4c) (probably the best for direct one-on-one help)
- Make sure you've read [FAQ](https://github.com/rikimaru0345/Ceras/wiki/FAQ) and [Optimization & Pitfalls](https://github.com/rikimaru0345/Ceras/wiki/Optimization-&-Pitfalls)

## [**>> FAQ**](https://github.com/rikimaru0345/Ceras/wiki/FAQ)
## [**>> Development Blog**](https://rikidev.com/)
