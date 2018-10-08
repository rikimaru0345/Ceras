# Ceras
###### Universal binary serializer for a wide variety of scenarios

## What is this?
Ceras is binary serializer, vaguely inspired by [MessagePack](https://github.com/neuecc/MessagePack-CSharp),
but designed from the ground up with specific needs in mind that MessagePack-CSharp (or rather the message-pack format itself) can simply not provide.

For example dealing with circular object-references, large/complicated inheritnace chains and interfaces, ...

## Getting started (Quick start)

```csharp
using Ceras;

class ExamplePerson { public string Name; public int Number; }
var p = new ExamplePerson { Name = "test", Number = 5 };

var s = new CerasSerializer();

var bytes = s.Serialize(p);
```

This kind of usage is already pretty efficient, but if you really need performance there are a few easy things you can change to get the most out of Ceras. See here: [Detailed Usage Guide (TODO)]()
In short until the guide is done:
- Use the `ref byte[] ` overload so Ceras does not have to allocate the byte-array for you
- Be extremely careful about what generic type you call Serialize and Deserialize with. They **must** be the same on both ends. When in doubt, you can use `Serialize<object>` and `Deserialize<object>` on both ends. (but you should use the concrete type whenever possible!)
- Make use of the config and all its settings (a parameter in the constructor of CerasSerializer)


## Features (Overview)

### Major Features
- Very fast serialization, very small binary output
- *Full* support for circular references (including object caching)
- *Full* support for polymorphism / inheritance / interfaces
- Can serialize objects into parts as "ExtenalObjects" (very useful for usage as a micro database)

#### Other Features
- Serializes public fields, optionally also private ones (you can even filter fields on a case-by-case basis)
- Efficient serialization for the `Type`-type
- Can embed type information to serialize `object`-fields correctly
- No allocations; can be used in a mode that generates next to no "garbage" (garbage-collector pressure) by recycling objects. Especially useful for use as a network protocol or in games. Can also recycle output buffers for serialization.
- Can remember objects (including strings) over multiple serialization calls to save space
- Can be used as an extremely efficient binary network protocol
- Can be used with `KnownTypes` to further reduce the size of the binary output
- Generates a checksum of types, fields, attributes, ... which can be used to ensure bianry compatability
- Type-information is only serialized once
- Very easy to add new "Formatters" (the things that the serializer uses to actually read/write an object)

#### Built-in types
Built-in support for many commonly used .NET types: Primitives(`int`, `string`, ...), `Enum`, `DateTime`, `Guid`, `Array[]`, `KeyValuePair<,>`, everything that implements `ICollection<>` so `List<>`, `Dictionary<,>`, ... 


## Features (Details)

Ceras will never write a long type name like `MyApplicatoin.MyNamespace.MyClass.MyNestedClass. ...` multiple times (unless you specifically want that), and will reuse previously written type-names.
The same mechanism that enables this also enables serialization of object-references; and can even be used to "cache" whole objects across serialization calls! If, for example, you'd have a chat-application, Ceras could automatically cache the user-names for you, so they only get sent once over the network, and future messages will just contain a lookup-index instead of the full user-name.


(todo: expand this section)



## When should I use this? (example usages)

The two primary intentions for creating this serializer were easy object persistance and network communication.
Thus these scenarios are where Ceras really shines.

- Saving objects to disk quickly without much trouble
  - settings, savegames (pretty much zero config)
  - as object DB; using `IExternalRootObject` (see [External Objects Guide]())
- Network communication
  For example for a game, when you absolutely need both fast serialization/deserialization, as well as small message/packet sizes.
  see [Network Example Guide]()

## When should I not use this?

1) If you need human readable output for some reason. For example a settings file that you want to be able to edit in an editor-application) then JSON or XML are likely better suited

2) You plan to use this on a platform that does not support code generation. Serializers for custom types obviously have to be created at runtime through code-generation, so Ceras won't be able to generate arbitrary object-formatters on platforms that do not support this (for example iOS). Built-in types will still work though. Maybe I'll fix this in the future (no concrete plans there yet though!)

3) Ceras was made without "version tolerance" in mind, but there are some easy work arounds. If however you need "version tolerant" binaryata **and** you cannot afford a so called "data upgrade" for some reason, then Ceras is not for you.

For the majority of use-cases this should not be an issue since you can just performa a so called "data-upgrade".
For more details about this see the data-upgrade guide where this potential issue is also explained in more detail.

Ceras does not have any functionality to deal with "missing" fields or data. For example if you use Ceras to serialize application settings and some day you decide to remove or add a field, then binaries saved (serialized) using the old classes can't be desrialized anymore; To deal with this you can simply do a "data-upgrade" (converting the old data to the new format so it can be loaded again). For an example of this see the [data-upgrade guide (TODO)]().   

There are plans to address this in an automatic way in the future.

4) Ceras does not serialize Properties yet. Support for that is already planned though.


## Planned features

- serialization of properties
- serializing readonly collections


