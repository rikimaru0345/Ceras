# Ceras
###### Universal binary serializer for a wide variety of scenarios, lots of features, and tuned for performance 

## What is this?
Ceras is a binary serializer, inspired by [MsgPack](https://github.com/neuecc/MessagePack-CSharp) and intended to not only fix the pain-points I've experienced using it, but also add a lot of extra features.
Ceras implements its own format and is not at all compatible with the "MsgPack" format.

Support for reference loops, large/complicated inheritnace chains, "external" objects, ...

## Getting started (Quick start)

```csharp
using Ceras;

class ExamplePerson { public string Name; public int Number; }
var p = new ExamplePerson { Name = "test", Number = 5 };

var s = new CerasSerializer();

var bytes = s.Serialize(p);
```

-> Check out the [**Detailed Usage Guide**](https://github.com/rikimaru0345/Ceras/blob/master/LiveTesting/Tutorial.cs) for examples on all features.


## Features (Overview)

### Major Features
- Very fast, very small binary output
- *Full* support for circular references (including object caching)
- *Full* support for polymorphism / inheritance / interfaces
- Can serialize objects into parts as "ExtenalObjects" (very useful for usage with databases)

#### Other Features
- Can serialize Fields and Properties (Check out the [**tutorial**](https://github.com/rikimaru0345/Ceras/blob/master/LiveTesting/Tutorial.cs) to see all the different configuration options)
- VarInt & Zig-Zag encoding for integers (example: values up to 128 only take 1 byte instead of 4...)
- Efficient serialization for the `Type`-type, information is only written once and re-used whenever possible!
- Embeds type information only when needed! (When using the `<object>` overload, or for abstract types)
- No allocations; can be used in a mode that generates next to no "garbage" (garbage-collector pressure) by recycling objects. Especially useful for use as a network protocol or in games. Can also recycle output buffers for serialization.
- Can remember objects, including strings, and typing information over multiple serialization calls to save even more space
- Can be used as an extremely efficient binary network protocol
- You can add your types to a `KnownTypes` collection in order to further reduce the size of the binary output
- Generates a checksum of types, fields, attributes, ... which can be used to ensure binary compatability
- Very easy to add new "Formatters" (the things that the serializer uses to actually read/write an object)
- Various Attributes like `[Config]`, `[Ignore]`, `[Include]`
- Easy control over what gets serialized: ShouldSerialize callback > Member Attribute > Class Attrib > Global Default


#### Built-in types
Built-in support for many commonly used .NET types: Primitives(`int`, `string`, ...), `Enum`, `DateTime`, `Guid`, `Array[]`, `KeyValuePair<,>`, everything that implements `ICollection<>` so `List<>`, `Dictionary<,>`, ... 

Automatically generates optimized formatters for your types! No attributes or anything needed, everything fully automatic.

- Planned: Will include an (optional!) set of formatters for Unity3D objects in the next version.

## Features (Details)

Ceras will never write a long type name like `MyApplicatoin.MyNamespace.MyClass.MyNestedClass. ...` multiple times (unless you specifically want that), and will reuse previously written type-names.
The same mechanism that enables this also enables serialization of object-references; and can even be used to "cache" whole objects across serialization calls! If, for example, you'd have a chat-application, Ceras could automatically cache the user-names for you, so they only get sent once over the network, and future messages will just contain a lookup-index instead of the full user-name.

(todo: expand this section)



## When should I use this? (example usages)

The two primary intentions for creating this serializer were easy object persistance and network communication.
Thus these scenarios are where Ceras really shines.

Example Scenarios:
- Saving objects to disk quickly without much trouble: settings, savegames (pretty much zero config) (see steps 1 and 2 in the [Usage Guide](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L21) )
- As object DB by using `IExternalRootObject` (see [External Objects Guide (Game DB example))](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L300))
- Network communication, with Ceras doing the majority of the work to implement a very efficient protocol (see [Network Example Guide](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L278))

## When should I not use this?

1) If you need human readable output for some reason. For example some file that you want to be able to edit in a text-editor. For those usages JSON or XML are likely better suited.

2) You plan to use this on a platform that does not support code generation. Serializers for custom types obviously have to be created at runtime through code-generation, so Ceras won't be able to generate arbitrary object-formatters on platforms that do not support this (for example iOS). Built-in types will still work though. Maybe I'll fix this in the future (no concrete plans there yet though!)


## Planned features

- ~~Ceras does not serialize Properties yet. Support for that is coming soon!~~ **Ceras supports properties and fields now**

- Making Ceras available as a nuget package

- Support for version tolerance is planned for one of the next versions and pretty high up on the priority list.
For now, Ceras is made without versioning support, but there are some easy work arounds.
For more details about this see the data-upgrade guide where this is explained in more detail.

- Support for more built-in types, including common Unity3D types.

- Support for readonly collections

