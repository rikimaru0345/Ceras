
==================
== Instructions ==
==================


==
== 1. Copy files
==

You need:
- The Ceras library (.dll, .xml, ...)
- The Extension.cs file
- The link.xml file

You can either download the compiled assemblies from the CI system: https://ci.appveyor.com/project/rikimaru0345/ceras/build/artifacts
or you can compile Ceras yourself (just clone the git repo)

The extension.cs and link.xml files are right next to this readme file.

Create a new folder named "Ceras" in your "Assets" folder and put all the files mentioned above in it.
The link.xml file is only needed if you're using IL2CPP.


==
== 2. Activate the formatters
==

In your code, just do the following:

  var config = new SerializerConfig();
  CerasUnityFormatters.ApplyToConfig(config);
  var ceras = new CerasSerializer(config);

If you are targetting some normal platform, you're done here.



==
== 3. AoT Configuration
==

In AoT scenarios Ceras can not generate any dynamic code.
To disable all (most?) dynamic code generation and replace it with other alternatives,
you need to set the the following setting in the config

  config.Advanced.AotMode = AotMode.Enabled;

You also need the link.xml file because Ceras finds and uses a ton of its own functions indirectly (through reflection).

Support for IL2CPP is work in progress, so if you're sending network packets (structs), you'll be mostly fine.
If you are serializing any reference types though, you'll probably have to write custom formatters for them.
There are two alternatives in the works for that though: A reflection-based dynamic formatter (which is slow),
or pre-generating all used formatters using an external tool (currently work in progress).

If you're experienced with IL2CPP and its nuances, send me a message! :)