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
- **New: Specialized Robust format Format** with version-tolerance, string interning, and schema-based migration.
- ***[full feature list (and planned features)](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features)***

## Robust format Format (Version Tolerant)

Ceras now includes a specialized **robust format format** designed for persistent storage that must survive game updates. It adds a self-describing schema section to each file, allowing you to add, remove, or rename fields without breaking compatibility.

### Why use it?
- **Robust Versioning**: Safely add/remove fields or rename classes between game versions.
- **Fast Path**: Zero overhead when the schema matches (uses generated formatters directly).
- **AOT-Safe Migration**: The migration engine works on all platforms (including Unity IL2CPP) without `Expression.Compile`.
- **Space Efficient**: Includes built-in **string interning** (duplicate strings are stored only once).

### Quick start (3 steps)

**1. Annotate your save types with `VersionTolerant = true`:**

```csharp
[CerasSerializable(VersionTolerant = true)]
[PreviousTypeName("PlayerEntity")] // Handle class renames
public class Player
{
    [SerializedAs("Hp")]  // Stable wire name
    public float HitPoints;

    [PreviousName("Experience")] // Handle field renames
    public int Level;

    [Monomorphic] // Optimization: promise this field is always exactly 'object' (or whatever type is declared)
    public object Extra;
}
```

**2. Configure your serializer once at startup:**

```csharp
var config = new CerasRawConfig();
RobustFormatters.UseRobustFormatters(config);
CerasRobust.ConfigureRobust format(config);

var ceras = new CerasSerializer(config);
```

**3. Use the `CerasRobust` API:**

```csharp
uint currentGameVersion = 1;

// Save
byte[] data = CerasRobust.Save(myPlayer, currentGameVersion, ceras);

// Load (automatically migrates if the save was from an older version)
var player = CerasRobust.Load<Player>(data, currentGameVersion, ceras);
```

### Advanced Migration

- **Field Mapping**: Fields are matched by their `[SerializedAs]` name, then by `[PreviousName]` aliases, and finally by their C# name.
- **Type Mapping**: Classes and structs are matched by their `StableName` (from `[CerasSerializable]`), then by `[PreviousTypeName]` aliases.
- **Enum Migration**: Enums are automatically mapped by **name** rather than integer value. This allows you to reorder enum members or change their underlying values safely. If a value in the save file no longer exists in the current enum, it is reset to the default (0).
- **Numeric Coercion**: The engine automatically handles common numeric upgrades (e.g. `int` -> `long`, `float` -> `double`) during migration.
- **Monomorphic Optimization**: The source generator automatically detects sealed types and value types to skip unnecessary type-ID overhead. You can use `[Monomorphic]` or `[Polymorphic]` to override this behavior manually.

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
| Unity IL2CPP / iOS / AOT | ✅ works (reflection fallback) | ✅ recommended (faster) |
| NativeAOT (`dotnet publish`) | ✅ works (reflection fallback) | ✅ recommended (faster) |

### Quick setup (2 steps)

**1. Reference the source generator in your `.csproj`:**

```xml
<ItemGroup>
  <ProjectReference Include="..\Ceras.SourceGenerator\Ceras.SourceGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

**2. Annotate the types you want pre-generated formatters for:**

```csharp
using Ceras;

// Standard attribute on the type
[CerasSerializable]
public class Player
{
    public string Name;
    public int    Score;
}

// Or use the generic version anywhere (e.g. at the assembly level) 
// to generate formatters for external types you don't control:
[assembly: CerasSerializable<ExternalLibrary.UserType>]
```

> **Automatic Registration**: Ceras will automatically look for and register your generated formatters in its constructor. You no longer need to manually call `RobustFormatters.UseRobustFormatters(config)` (though you still can if you want to apply them to a specific config object manually).

The generated formatters produce **byte-identical output** to the runtime `ReflectionFallbackFormatter`, so serialized data is fully interchangeable.

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


