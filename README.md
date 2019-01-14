# Ceras

[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/rikimaru0345/Ceras?branch=master&svg=true)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts)  [![Test Results](https://img.shields.io/appveyor/tests/rikimaru0345/ceras.svg)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/tests) [![LICENSE](https://img.shields.io/github/license/rikimaru0345/Ceras.svg)](https://github.com/rikimaru0345/Ceras/blob/master/LICENSE) [![Discord](https://discordapp.com/api/guilds/367211057787305985/embed.png)](https://discord.gg/FGaCX4c)

[![NuGet](https://img.shields.io/nuget/v/Ceras.svg?logo=nuget&logoColor=ddd)](https://www.nuget.org/packages/Ceras/)  [![Release](https://img.shields.io/badge/download-70kb%20%5Brelease.zip%5D-blue.svg?logo=appveyor )](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts) 



###### Universal binary serializer for a wide variety of scenarios, lots of features, and tuned for performance 
Ceras is a binary serializer. It converts whatever object you give it into a `byte[]` and back.
It's not just a replacement for BinaryFormatter or MessagePack, it also adds tons of features on top. 

Supports reference loops, large/complicated inheritance chains, splitting objects into parts, ...

# Quick start

```csharp
using Ceras;

class ExamplePerson { public string Name; public int Number; }
var p = new ExamplePerson { Name = "test", Number = 5 };

var s = new CerasSerializer();

var bytes = s.Serialize(p);
```

## [**>> You can find many more examples in the tutorial**](https://github.com/rikimaru0345/Ceras/blob/master/LiveTesting/Tutorial.cs)

### [**>> Optimization & Usage Pitfalls**](https://github.com/rikimaru0345/Ceras/wiki/Optimization-&-Pitfalls)


# Features

### Major Features
- Very fast, very small binary output
- **Full** support for circular references (including object caching)
- **Full** support for polymorphism / inheritance / interfaces
- Can serialize objects into parts as "ExtenalObjects" (useful in many many scenarios)
- Automatic version-tolerance, no need to assign any attributes to your classes!

## [**>> Full feature list (and planned features)**](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features)



# What can this be used for?

### Example usages
The primary goal is to make an universal serializer that can be used in every situation.
Personally my primary intentions were easy object persistance and network communication.
I've added many features over time and whenever someone can think of a good scenario that should be supported as well I'll make it happen. 

Examples:
- **Settings:**
Saving objects to disk quickly without much trouble: settings, savegames, whatever it  is. With pretty much zero config.
See steps 1 and 2 in the [Usage Guide](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L21)

- **Splitting:**
So your `Person` has reference to other `Person` objects, but each one should be serialized individually?
No problem, use `IExternalRootObject`. It's super easy. (see [External Objects Guide (Game DB example))](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L300)).

- **Network:** 
In the past people used to manually write network messages into some network stream or packet because serialization was slow.
Receiving objects from the network also allocated a lot of 'garbage objects' because there was no easy way to recycle packets.
Ceras can fix all this, with some simple setup you can implement a very efficient protocol (see [Network Example Guide](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L278)).
If you want to, you can even let Ceras 'learn' the used types over a network-session so types will be automatically encoded to short IDs (or use `config.KnownTypes` to pre-establish types).

- **More:**
The above are just examples, Ceras is made so it can be used in pretty much every situation...

### When should I not use this?

- If you need human readable output for some reason. For example some file that you want to be able to edit in a text-editor. For those usages JSON or XML are likely better suited.

- You plan to use this on a platform that does not support code generation. Serializers for user-types are created at runtime through code-generation. And if that isn't allowed (for example on iOS) Ceras won't be able to generate arbitrary object-formatters. Built-in types will still work though. There are ways to fix this though... (pre-generating the formatters)

## [**>> FAQ**](https://github.com/rikimaru0345/Ceras/wiki/FAQ)
## [**>> Usage guides on my blog**](https://rikidev.com/networking-with-ceras-part-1/)



