Ceras
===
[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/rikimaru0345/Ceras?branch=master&svg=true)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts)  [![Test Results](https://img.shields.io/appveyor/tests/rikimaru0345/ceras.svg)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/tests) [![Discord](https://discordapp.com/api/guilds/367211057787305985/embed.png)](https://discord.gg/FGaCX4c) [![NuGet](https://img.shields.io/nuget/v/Ceras.svg?logo=nuget&logoColor=ddd)](https://www.nuget.org/packages/Ceras/)  [![Release](https://img.shields.io/badge/download-70kb%20%5Brelease.zip%5D-blue.svg?logo=appveyor )](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts) 

Ceras is a binary serializer. It converts *any* object into a `byte[]` and back.
It goes above and beyond in terms of features, speed, and comfort.
Supports reference loops, large/complicated inheritance chains, splitting objects into parts, ...

## Quick start

```csharp
class Person { public string Name; public int Age; }
var p = new Person { Name = "riki", Age = 5 };

var ceras = new CerasSerializer();

var bytes = ceras.Serialize(p);
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
	- [Comes with formatters for all common Unity types](https://github.com/rikimaru0345/Ceras/tree/master/src/Ceras.UnityAddon)
	- Generates new formatters at runtime for any new/user type
	- [Very easy to extend and customize](https://www.rikidev.com/extending-ceras-with-a-custom-formatter/)
- **Full** support for reference persistence (including circular references!)
- **Full** support for polymorphism / inheritance / interfaces
- Can serialize objects into parts as "ExtenalObjects" (useful in many many scenarios)
- **New: Specialized Savegame Format** with version-tolerance, string interning, and schema-based migration.
- Automatic version-tolerance, no need to place any attributes on your classes!
- ***[full feature list (and planned features)](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features)***

## Savegame Format (Version Tolerant)

Ceras now includes a specialized **savegame format** designed for persistent storage that must survive game updates. It adds a self-describing schema section to each file, allowing you to add, remove, or rename fields without breaking compatibility.

### Why use it?
- **Robust Versioning**: Safely add/remove fields or rename classes between game versions.
- **Fast Path**: Zero overhead when the schema matches (uses generated formatters directly).
- **AOT-Safe Migration**: The migration engine works on all platforms (including Unity IL2CPP) without `Expression.Compile`.
- **Space Efficient**: Includes built-in **string interning** (duplicate strings are stored only once).

### Quick start (3 steps)

**1. Annotate your save types with `VersionTolerant = true`:**

```csharp
[GenerateFormatter(VersionTolerant = true)]
public class Player
{
    [SerializedAs("Hp")]  // stable wire name
    public float HitPoints;

    [PreviousName("Experience")]
    public int Level;
}
```

**2. Configure your serializer once at startup:**

```csharp
var config = new SerializerConfig();
GeneratedFormatters.UseFormatters(config);
CerasSave.ConfigureSavegame(config);

var ceras = new CerasSerializer(config);
```

**3. Use the `CerasSave` API:**

```csharp
uint currentGameVersion = 1;

// Save
byte[] data = CerasSave.Save(myPlayer, currentGameVersion, ceras);

// Load (automatically migrates if the save was from an older version)
var player = CerasSave.Load<Player>(data, currentGameVersion, ceras);
```

### Advanced Migration

You can receive details about the migration by adding a method with `[OnAfterDeserialize]`:

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

## Performance benchmarks
Ceras generally ranks at the top end of the performance spectrum, together with NetSerializer and MessagePack-CSharp.
To get an idea of how Ceras performs here are the preliminary benchmark results.
The resulting binary size is about the same as MessagePack-CSharp.

![Single object performance benchmark](https://i.imgur.com/Q896UgV.png)

The shown results are obtained from **[this code](https://github.com/rikimaru0345/Ceras/blob/master/samples/LiveTesting/Benchmarks.cs)** and I encourage you to not only try it yourself, but to also provide feedback about scenarios you had good and bad results with.

Don't forget to tune the settings in `SerializerConfig` for your specific situation.
Using Ceras to read/write network packets might require different settings than, lets say, saving a settings-object to a file, or persisting items/spells/monsters in a game, or ... 

The project is still heavily work-in-progress, meaning that over time more optimizations will get implemented (your feedback is important here!).

## What can this be used for?

### Example usages
The primary goal is to make an universal serializer that can be used in every situation.
Personally my primary intentions were easy object persistance and network communication.
I've added many features over time and whenever someone can think of a good scenario that should be supported as well I'll make it happen. 

Examples:
- **Settings:**
Saving objects to disk quickly without much trouble: settings, savegames, whatever it  is. With pretty much zero config.
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

| Scenario | DynamicFormatter (default) | Source Generator |
|---|---|---|
| Normal .NET desktop/server | ✅ works out of the box | optional (slightly faster startup) |
| Unity IL2CPP / iOS / AOT | ❌ crashes at runtime | ✅ required |
| NativeAOT (`dotnet publish`) | ❌ crashes at runtime | ✅ required |

### Quick setup (3 steps)

**1. Reference the source generator in your `.csproj`:**

```xml
<ItemGroup>
  <ProjectReference Include="..\Ceras.SourceGenerator\Ceras.SourceGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

> When Ceras is published as a NuGet package, the generator will be included automatically — no manual project reference needed.

**2. Annotate the types you want pre-generated formatters for:**

```csharp
using Ceras;

[GenerateFormatter]
public class Player
{
    public string Name;
    public int    Score;
}

// Use [MemberConfig] to control which members are included
[GenerateFormatter]
[MemberConfig(TargetMember.PublicProperties)]
public class Vec2
{
    public float X { get; set; }
    public float Y { get; set; }
}
```

The generator respects `[Include]`, `[Exclude]`, and `[MemberConfig]` just like the runtime serializer.

**3. Register the generated formatters before creating your serializer:**

```csharp
using Ceras.Generated;

var config = new SerializerConfig();
GeneratedFormatters.UseFormatters(config);   // registers all [GenerateFormatter] types

var ceras = new CerasSerializer(config);
```

The generated formatters produce **byte-identical output** to the runtime `DynamicFormatter`, so serialized data is fully interchangeable.

### Enforcing AOT-only mode (no dynamic fallback)

To guarantee that Ceras never falls back to the runtime `DynamicFormatter` — which will crash on AOT platforms — enable `AotMode`:

```csharp
var config = new SerializerConfig();
GeneratedFormatters.UseFormatters(config);
config.Advanced.AotMode = AotMode.Enabled;  // throws if any type lacks a pre-generated formatter

var ceras = new CerasSerializer(config);
```

With `AotMode.Enabled`, any type that slips through without a `[GenerateFormatter]` annotation will throw an `InvalidOperationException` immediately (at serializer construction time, not silently at runtime on device).

### Polymorphic / runtime-only types

The source generator only sees types that exist at compile time.
For polymorphic scenarios (serializing an interface or base-class field whose concrete type varies at runtime), annotate **all concrete derived types** with `[GenerateFormatter]`:

```csharp
[GenerateFormatter] public class Cat : Animal { ... }
[GenerateFormatter] public class Dog : Animal { ... }
// Animal itself also needs one if it is ever serialized directly:
[GenerateFormatter] public abstract class Animal { ... }
```

If a concrete type is only known at runtime (e.g. loaded from a plugin assembly), it cannot be source-generated. In that case you have two options:
- Keep `AotMode = AotMode.None` (the default) so `DynamicFormatter` handles those types as a fallback — only viable on JIT platforms.
- Write a hand-crafted `IFormatter<T>` and register it via `config.ConfigType<T>().CustomFormatter = new MyFormatter();`.

# Support
- Open an issue
- Join my [Discord](https://discord.gg/FGaCX4c) (probably the best for direct one-on-one help)
- Make sure you've read [FAQ](https://github.com/rikimaru0345/Ceras/wiki/FAQ) and [Optimization & Pitfalls](https://github.com/rikimaru0345/Ceras/wiki/Optimization-&-Pitfalls)

## [**>> FAQ**](https://github.com/rikimaru0345/Ceras/wiki/FAQ)
## [**>> Development Blog**](https://rikidev.com/)


