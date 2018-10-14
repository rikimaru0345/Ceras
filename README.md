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

-> Check out the [**Detailed Usage Guide**](https://github.com/rikimaru0345/Ceras/blob/master/LiveTesting/Tutorial.cs) for examples on all features.


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
  - Encodes type-information in just 1 byte in most cases.
   - In case you don't want to use `KnownTypes`, Ceras writes type-information only when needed and only once (gets reused)
  - No type lookups! (except for polymorphic types of course)
- Can serialize an object graph into multiple "fragments" and automatically reassemble everything at deserialization time.
- No allocations
  - Generates no "garbage" (garbage-collector pressure) by recycling objects.
  - Integrates with user provided object-pools through `ObjectFactory` and `DiscardObject` methods. Especially useful for use as a network protocol or in games.
  - Can also recycle serialization buffers.
- Advanced caching settings to remember objects and typing information over multiple serialization calls to save even more space when working with "unknown" types (types you didn't provide in `KnownTypes`).
- Can be used as an extremely efficient binary network protocol
- Can generate a checksum of all types, fields, attributes, ... which can be used to ensure binary compatability (very useful for networking where you want to check if the server/client are using the same protocol...)
- Very easy to add new "Formatters" (the things that the serializer uses to actually read/write an object)
- Various Attributes like `[Config]`, `[Ignore]`, `[Include]`


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

##### I'm forced to work with unknown types and can't provide any (at least some) types to `KnownTypes`, what are my options?
You can still get some massive space-savings by shortening the namespaces.
Using a custom TypeBinder that's very easy. For example in your long type name `MyVeryLongCompanyName.MyVeryLongProductName.SomeNamespace1.SomeType.MyActualNestedType`
you can easily replace the first 3 parts with a single symbol. For example when Ceras needs to write the type name, you could change the string that actually gets written to something like this: `~1.SomeType.MyActualNestedType`
And at deserialization time your type-binder knows that types that begin with `~1.` are just a shorthand for a longer type name.
   *(todo: write a tutorial step for this)*

##### Ceras doesn't support some type I need to serialize
Report it as an issue. If it's a common type I'll most likely add a dedicated built-in formatter for it.



# Planned features

### Next (very soon)
- Making Ceras available as a nuget package
- Better exceptions (actual exception types instead of the generic `Exception`)

### Backlog
- .NET standard build target
- Override formatter per-member (aka `[Formatter(...)]` attribute)
- Support for version tolerance is planned for one of the next versions and pretty high up on the priority list.
For now, Ceras is made without versioning support, but there are some easy work arounds.
For more details about this see the data-upgrade guide where this is explained in more detail.
- Performance comparisons beyond simple micro benchmarks
- Wider range of unit tests instead of manual test cases / debug asserts
- Use [FastExpressionCompiler](https://github.com/dadhi/FastExpressionCompiler) when all its bugs are fixed
- Support for more built-in types, including common Unity3D types.
- More DynamicSerializer variants to support extra use cases like immutable objects, readonly collections, generally being able to have serialization-constructors...

### Done
- **Ceras supports properties and fields now** ~~Ceras does not serialize Properties yet. Support for that is coming soon!~~ 




