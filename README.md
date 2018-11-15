[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/rikimaru0345/Ceras?branch=master&svg=true)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts)  [![Test Results](https://img.shields.io/appveyor/tests/rikimaru0345/ceras.svg)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/tests)  [![NuGet](https://img.shields.io/nuget/v/Ceras.svg?logo=nuget&logoColor=ddd)](https://www.nuget.org/packages/Ceras/)  [![Release](https://img.shields.io/badge/ZipRelease-1.2.0-blue.svg?logo=appveyor)](https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts)  [![LICENSE](https://img.shields.io/github/license/rikimaru0345/Ceras.svg)](https://github.com/rikimaru0345/Ceras/blob/master/LICENSE)




# Ceras
###### Universal binary serializer for a wide variety of scenarios, lots of features, and tuned for performance 
Ceras is a binary serializer, inspired by [MsgPack](https://github.com/neuecc/MessagePack-CSharp) and intended to not only fix the pain-points I've experienced using it, but also add a lot of extra features.
Ceras implements its own format and is not at all compatible with the "MsgPack" format.

Support for reference loops, large/complicated inheritnace chains, "external" objects, ...

# Quick start

```csharp
using Ceras;

class ExamplePerson { public string Name; public int Number; }
var p = new ExamplePerson { Name = "test", Number = 5 };

var s = new CerasSerializer();

var bytes = s.Serialize(p);
```

-> Check out the [**Tutorial**](https://github.com/rikimaru0345/Ceras/blob/master/LiveTesting/Tutorial.cs)
-> It has many different examples for all sorts of use-cases and scenarios!


# Features

### Major Features
- Very fast, very small binary output
- *Full* support for circular references (including object caching)
- *Full* support for polymorphism / inheritance / interfaces
- Can serialize objects into parts as "ExtenalObjects" (very useful for usage with databases)

#### Other Features
- Can serialize Fields and Properties (Check out the [**Tutorial**](https://github.com/rikimaru0345/Ceras/blob/master/LiveTesting/Tutorial.cs) to see all the different configuration options)
  - `ShouldSerialize` Callback > Member-Attribute > Class-Attribute > Global Default
- No need to place attributes on members
  - Serialization is still completely "stable", since members are sorted by MemberTypeName+MemberName
- Efficient:
  - By default no versioning-, type- or other meta-data is written, only what is strictly needed.
  - Utilizes both VarInt & Zig-Zag encoding (example: values up to 128 only take 1 byte instead of 4...)
  - Encodes type-information in 0 or 1 byte in most cases
   - If the type already matches no types are written at all (vast majority of cases)
   - KnownTypes are encoded as 1 byte
   - Ceras dynamically learns new/unknown types while serializing. New types are written once in compressed form, thus automatically becoming a known type.
   - No type lookups! (except for polymorphic types of course)
- Automatic splitting and reassembling. You want to save your `Monster`, `Spell`, and `Player` objects each into their own file? No problem! Ceras can automatically split and reassemble object graphs for you. (See `IExternalRootObject`)
- No allocations
  - Generates no "garbage" (garbage-collector pressure) by recycling all internal objects.
  - Integrates with user object-pools as well with `ObjectFactory` and `DiscardObject` methods. Especially useful for use as a network protocol or in games so Ceras will not allocate new user-objects and instead get them from your pools.
  - Recycles serialization buffers (just pass in the buffer by ref)
- Can overwrite objects you already have, works even when an object already contains references to wrong types, Ceras will use your  `DiscardObject`-Method then. (use as an alternative way to eliminate allocations/GC pressure)
- Advanced caching settings to remember objects and typing information over multiple serialization calls to save even more space
- Can be used as an extremely efficient binary network protocol
- Can generate a checksum of all types, fields, attributes, ... which can be used to ensure binary compatability (very useful for networking where you want to check if the server/client are using the same protocol...)
- Very easy to add new "Formatters" (the things that the serializer uses to actually read/write an object)
- Various Attributes like `[Config]`, `[Ignore]`, `[Include]`
- Version tolerance. Supports all changes: adding new / renaming / reordering / deleting members.

#### Built-in types
Built-in support for many commonly used .NET types: Primitives(`int`, `string`, ...), `Enum`, `decimal`, `DateTime`, `TimeSpan`, `DateTimeOffset`, `Guid`, `Array[]`, `KeyValuePair<,>`, `Nullable<>`, everything that implements `ICollection<>` so `List<>`, `Dictionary<,>`, ... 

Automatically generates optimized formatters for your types! No attributes or anything needed, everything fully automatic.

- Some common type missing? Report it and I'll most likely add a formatter for it
- Planned: Will include an (optional!) set of formatters for Unity3D objects


# When should I use this?

### Example usages
The two primary intentions for creating this serializer were easy object persistance and network communication.
Thus these scenarios are where Ceras really shines.

Example Scenarios:
- Saving objects to disk quickly without much trouble: settings, savegames (pretty much zero config) (see steps 1 and 2 in the [Usage Guide](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L21) )
- As object DB by using `IExternalRootObject` (see [External Objects Guide (Game DB example))](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L300))
- Network communication, with Ceras doing the majority of the work to implement a very efficient protocol (see [Network Example Guide](https://github.com/rikimaru0345/Ceras/blob/5593ed603630275906dec831eef19564d0a5d94c/LiveTesting/Tutorial.cs#L278))

### When should I not use this?

1) If you need human readable output for some reason. For example some file that you want to be able to edit in a text-editor. For those usages JSON or XML are likely better suited.

2) You plan to use this on a platform that does not support code generation. Serializers for custom types obviously have to be created at runtime through code-generation, so Ceras won't be able to generate arbitrary object-formatters on platforms that do not support this (for example iOS). Built-in types will still work though. Maybe I'll fix this in the future (no concrete plans there yet though!)

# FAQ

##### How do I explicitly decide what fields or properties get serialized for types I didn't write?
Use `ShouldSerializeMember` in the config (SerializerConfig)

##### Does it work with Streams?
Yes, in the most efficient way.
Instead of having Ceras do thousands of "Stream.Write" calls, it first serializes everything to a buffer (that can also be re-used), and then the whole buffer can be written in one go. 
##### ...but what if my objects / object-graphs are too big? Or what if I want to serialize smaller parts for lower latency?
Also not a problem, check out `IExternalRootObject` in the tutorial.

##### What are the differences to MessagePack? Why should I use Ceras over MessagePack?
While MessagePack-CSharp is an excellent library, it is still bound to the 'MsgPack' format, and thus its inefficiencies.
Complex type hierarchies and references are a big problem. While the MessagePack-CSharp library managed to allivate some problems with the msgpack-standard, there are still many problems for real-world scenarios.
One of which is the need to annotate every object and field with an ID-key, having to manually setup attributes in your hierarchy everywhere, making sure nothing collides... it quickly gets out of hand.
Ceras fixes all those things (and more) completely. The downside is that objects serialized with Ceras are not at all compatible with MessagePack.


##### I'm forced to work with unknown types and can't provide any (at least some) types to `KnownTypes`, what are my options?
You can still get some massive space-savings by shortening the namespaces.
Using a custom TypeBinder that's very easy. For example your TypeBinder could just replace the first 3 parts in  `MyVeryLongCompanyName.MyVeryLongProductName.SomeNamespace1.SomeType.MyActualNestedType`
 with a single symbol. When Ceras needs to write the type name your binder would make it so only `~1.SomeType.MyActualNestedType` gets written. And at deserialization time your TypeBinder knows that types that begin with `~1.` are just a shorthand for a longer type name and reverses the change again.
 
Just implement `ITypeBinder` and set it in the `SerializerConfig`
 
   *(todo: write a tutorial step for this)*


##### Ceras doesn't support some type I need to serialize
Report it as an issue. If it's a common type I'll most likely add a dedicated built-in formatter for it.



# Planned features
- .NET standard build target
- "Serialization Constructors" for immutable collections (also supporting private static methods for construction)
- Performance comparisons beyond simple micro benchmarks
- Better exceptions (actual exception types instead of the generic `Exception`)
- Wider range of unit tests instead of manual test cases / debug asserts
- Use [FastExpressionCompiler](https://github.com/dadhi/FastExpressionCompiler) when all its bugs are fixed
- More built-in formatters, including common Unity3D types.
- Override formatter per-member (aka `[Formatter(...)]` attribute)
- Built-in LZ4 and GZip(Zlib) support, including support for Sync-Flush (especially useful for networking scenarios)

### Done
- Making Ceras available as a nuget package
- Automatic release builds
- Automatic version tolerance can now be enabled through config. `config.VersionTolerance = VersionTolerance.AutomaticEmbedded;`. More options will follow in the future including manual version tolerance if you want to go the extra mile to optimize your code. 
- Ceras supports fields **and properties** now, checkout the `tutorial.cs` file to see all the ways to configure what gets serialized



