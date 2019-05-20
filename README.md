Ceras
===
[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/rikimaru0345/Ceras?branch=master&svg=true)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts)  [![Test Results](https://img.shields.io/appveyor/tests/rikimaru0345/ceras.svg)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/tests) [![LICENSE](https://img.shields.io/github/license/rikimaru0345/Ceras.svg)](https://github.com/rikimaru0345/Ceras/blob/master/LICENSE) [![Discord](https://discordapp.com/api/guilds/367211057787305985/embed.png)](https://discord.gg/FGaCX4c) [![NuGet](https://img.shields.io/nuget/v/Ceras.svg?logo=nuget&logoColor=ddd)](https://www.nuget.org/packages/Ceras/)  [![Release](https://img.shields.io/badge/download-70kb%20%5Brelease.zip%5D-blue.svg?logo=appveyor )](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts) 

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

1. [**>> Many more examples in the code tutorial**](https://github.com/rikimaru0345/Ceras/blob/master/samples/LiveTesting/Tutorial.cs)
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
- Automatic version-tolerance, no need to place any attributes on your classes!
- ***[full feature list (and planned features)](https://github.com/rikimaru0345/Ceras/wiki/Full-feature-list-&-planned-features)***

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
See steps 1 and 2 in the [Usage Guide](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L21)

- **Splitting:**
So your `Person` has references to other `Person` objects, but each one should be serialized individually!? (without the references quickly dragging in essentially your whole program).
Maybe you want to be able to put each `Person` into its own file, or send them over the network one-by-one as needed?
**No problem!** Using `IExternalRootObject` it's not an issue! See [External Objects Guide (Game DB example))](https://github.com/rikimaru0345/Ceras/blob/6a435a6c21c31cc9548dcc40b2d2c1d1d35d9000/samples/LiveTesting/Tutorial.cs#L327).

- **Network:** 
Because of its simple API and vast set of features Ceras is uniquely suited to implement a full 'network-protocol' for you.
I wrote [a short guide](https://rikidev.com/networking-with-ceras-part-1/) that shows off how a basic  TCP implementation could look like:
Just `Send(myObject);` it, then `var obj = await Receive();` on the other side, that's it! It literally can't get any easier than that.
At the moment the guide only has 2 parts, but when I have some (and if there are requests for it) I'd like to continue the series, eventually building that sample into a full-fledged, robust, and battle-tested networking system.

- **More:**
The above are just examples, Ceras is made so it can be used in pretty much every situation...

### When should I not use this?

- If you need human readable output for some reason. For example some file that you want to be able to edit in a text-editor. For those usages JSON or XML are likely better suited.

- ~~You plan to use this on a platform that does not support code generation. Serializers for user-types are created at runtime through code-generation. And if that isn't allowed (for example on iOS) Ceras won't be able to generate arbitrary object-formatters. Built-in types will still work though. There are ways to fix this though... (pre-generating the formatters)~~
  Ceras now has a dedicated `AotMode` in the config and a [code-generator](https://github.com/rikimaru0345/Ceras/tree/master/src/Ceras.AotGenerator) ([quick guide for it here](https://github.com/rikimaru0345/Ceras/wiki/Unity-IL2CPP-(iOS-and-AOT))) for IL2CPP/Unity/AoT.


# Support
- Open an issue
- Join my [Discord](https://discord.gg/FGaCX4c) (probably the best for direct one-on-one help)
- Make sure you've read [FAQ](https://github.com/rikimaru0345/Ceras/wiki/FAQ) and [Optimization & Pitfalls](https://github.com/rikimaru0345/Ceras/wiki/Optimization-&-Pitfalls)

## [**>> FAQ**](https://github.com/rikimaru0345/Ceras/wiki/FAQ)
## [**>> Development Blog**](https://rikidev.com/)


