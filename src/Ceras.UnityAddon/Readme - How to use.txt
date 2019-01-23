
===
=== Instructions
===


1. Copy the .cs file into your Unity project.

Just copy the file, that's all.
Don't compile this project in VS or anywhere else, just let Unitys compiler handle it.



2. Call config.AddUnityFormatters()

If you don't pass a SerializerConfig when you instantiate a CerasSerializer, then you need to do that.
Just do `var config = new SerializerConfig();` and then `config.AddUnityFormatters();`.
That's it, all done.

Internally this will add a method to config.OnResolveFormatter.