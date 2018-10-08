
using Ceras;

namespace Tutorial
{
	using Ceras.Formatters;
	using System;
	using Newtonsoft.Json.Linq;

	class Person
	{
		public string Name;
		public int Health;
		public Person BestFriend;
	}

	class Tutorial
	{
		public void Step1_SimpleUsage()
		{
			//
			// 1.) Simple usage
			//
			var person = new Person { Name = "riki", Health = 100 };

			var serializer = new CerasSerializer();

			var data = serializer.Serialize(person);
			data.VisualizePrint();

			var clone1 = serializer.Deserialize<Person>(data);
			Console.WriteLine($"Clone: Name={clone1.Name}, Health={clone1.Health}");


			//
			// 2.) Improvement:
			// Recycle the serialization buffer by keeping the reference to it around.
			// Optionally we can even let Ceras create (or resize) the buffer for us.

			byte[] buffer = null;
			int writtenBytes = serializer.Serialize(person, ref buffer);

			// Now we could send this over the network, for example:
			//   socket.Send(buffer, writtenBytes, SocketFlags.None);
			var clone2 = serializer.Deserialize<Person>(buffer);



			//
			// 3.) Circular references
			// Circular references are a problem for many serializers, not so for Ceras.
			// There's literally nothing to do or configure, it just works out of the box.
			// Lets make an example anyway...

			var personA = new Person { Name = "a" };
			var personB = new Person { Name = "b" };

			personA.BestFriend = personB;
			personB.BestFriend = personA;

			var dataWithCircularReferences = serializer.Serialize(personA);
			Console.WriteLine("Circular references data: ");
			dataWithCircularReferences.VisualizePrint();

			var cloneA = serializer.Deserialize<Person>(dataWithCircularReferences);

			if (cloneA.BestFriend.BestFriend.BestFriend.BestFriend.BestFriend.BestFriend == cloneA)
				Console.WriteLine("Circular reference serialization working as intended!");
			else
				Console.WriteLine("There was some problem!");


		}

		public void Step2_Recycling()
		{
			/*
			 * Scenario:
			 *		- You might be developing a game or other timing-sensitive application
			 *		  where performance is extremely important and where small stutters by the GC have to be avoided
			 *		- You have your own object-pool and want Ceras to obtain instances from it and return them to it.
			 *
			 * What you want:
			 *		- You want more performance
			 *		- You want to decrease GC pauses and GC pressure
			 *
			 * What can we do:
			 *		- Recycle objects, reducing GC pressure
			 *		- Take objects from a pool instead of doing "new MyThing()" to improve cache-coherence. 
			 */



			// Assuming we're receiving "network packets" over the internet, we'd instantiate a new object every time we call Deserialize
			// That is pretty wasteful. We receive one object, deserialize it, use it, then discard it, ...
			// 
			// What we can do is have one instance that we can just overwrite all the time!
			//
			// Ceras will use the provided object and overwrite the fields; instead of creating a new object instance.
			// If no instance is provided, Ceras will just instantiate one.
			//
			// Hint: This can also be used to quickly set or reset an object to some predefined values in other scenarios.

			var serializer = new CerasSerializer();

			byte[] buffer = null; // Lets assume we already got a network buffer and now we just have to read it.
			Person recycledPerson = new Person();
			serializer.Deserialize<Person>(ref recycledPerson, buffer);




			//
			// Now, assuming we even got our own object-pooling solution going,
			// lets tell Ceras to use that.

			SerializerConfig config = new SerializerConfig();
			config.ObjectFactoryMethod = type =>
			{
				// In this example we have no pool, so create directly
				var obj = Activator.CreateInstance(type);

				// Instead we could have done:
				//    var obj = myPool.RentObject(type);

				return obj;
			};

			// !! TODO !!
			// The next part is not complete yet
			//
			// If the data in the buffer tells us "it's a 'null' object" then Ceras will of course set 'recycledPerson' to null.
			// But now you might wonder what happens to the instance that was previously inside 'recycledPerson'.
			// The answer is obviously that it would be lost, in other words it becomes "garbage" and will eventually be collected by the .NET GC.
			// In some scenarios (games) we don't want this, since garbage-collections can cause stutters.
			// A common solution to this is object-pooling, Ceras supports that by allowing you to "catch" unused objects, so you can return them to your object-pool.
			// Like this:

			/* todo: this will work after object recycling gets implemented fully
			var config = new Ceras.SerializerConfig();
			config.DiscardObjectMethod = obj =>
			{
				Console.WriteLine($"Object '{obj.ToString()}' is no longer needed, we can return it to our pool.");
			};

			serializer = new CerasSerializer(config);
			*/

		}

		public void Step3_KnownTypes()
		{
			/*
			 * Assuming we want to reduce the used space to an absolute minimum, we can tell Ceras what types will be present throughout a serialization.
			 * Ceras will (while creating the serializer) assign unique IDs to each type and use that instead of writing the long type names.
			 */
			var person = new Person { Name = "riki", Health = 100 };

			SerializerConfig config = new SerializerConfig();
			config.KnownTypes.Add(typeof(Person));

			var serializer = new CerasSerializer(config);

			var data = serializer.Serialize(person);

			Console.WriteLine("Data serialized using KnownTypes:");
			data.VisualizePrint();

			var clone1 = serializer.Deserialize<Person>(data);
			Console.WriteLine($"Clone (using KnownTypes): Name={clone1.Name}, Health={clone1.Health}");


			/*
			 * This is only really useful for small packages, for example when sending data over the net.
			 * 
			 * If you serialize, lets say, a list of 20 persons,
			 * then Ceras will only write the type-information once anyway.
			 * So there's not much savings to be had relative to the size of the data.
			 *
			 * Howver, using KnownTypes will still save you some space (and thus also serialization time)
			 * so using it is never a bad idea!
			 *
			 * For the network-scenario you can even use a 3rd option where Ceras still only writes type-data once,
			 * and the receiving side will remember it... Giving you the best of both approaches!
			 * More on that in the network example where all sorts of space-saving mechanisms (including KnownTypes) will be explored!
			 */
		}

		public void Step4_CustomFormatters()
		{
			/*
			 * Scenario:
			 * - An object is somehow special an needs to be serialized ina very special way
			 * - Simply writing out the fields is not enough, maybe some meta-data is also needed or whatever...
			 *
			 * Solution:
			 * Write your own formatter!
			 *
			 * I hear you: "what the hell is a formatter??"
			 * It's actually just a simple class that inherits from IFormatter<YourTypeHere>
			 * and it simply takes over the serialization completely.
			 */

			// todo: provide OnStart callbacks, or let the serializer instantiate the formatter-type
			//		 ceras solves this for its built-in serializers by having "Resolvers" (classes that given a type return a formatter for that type)
			//		 it would be a good idea to let users provide their own full resolvers, instead of just the formatters

			SerializerConfig config = new SerializerConfig();
			config.UserFormatters.AddFormatter(new MyCustomPersonFormatter());

			var serializer = new CerasSerializer(config);


			var p = new Person { Name = "riki", Health = 100 };
			// todo: if p.BestFriend = p; it will crash for now, but only because we can't obtain a the internal formatter for Person from the serializer.
			// the intended (future) solution is simple: let MyCustomPersonFormatter have a reference to the serializer, so it can call GetFormatter<Person>,
			// which will return a CacheFormatter, that enables reference-loops!
			// right now this is not possible, but will be done soon!
 
			var customSerializedData = serializer.Serialize(p);

			var clone = serializer.Deserialize<Person>(customSerializedData);

		}
		
		public void Step5_NetworkExample()
		{
			// todo: ...
		}
		
		public void Step6_GameDatabase()
		{
			// todo: ...
		}

		public void Step7_DataUpgrade()
		{
			/*
			 * So you have a settings class or something,
			 * and now you have done 3 types of changes:
			 * - removed a field
			 * - added a new field
			 * - renamed a field
			 *
			 * Since Ceras trades off versioning support for speed and binary-size, this is not directly supported.
			 * You can still load the old data using the old class (maybe move it into a separate namepace, or an extra .dll)
			 *
			 * In this example I'm just using JObject (from Newtonsoft.Json) for easy editting and transfer.
			 * However you can also do it fully manually, but personally I find working with JObject compfortable.
			 *
			 */

			var settings = new SettingsOld { Bool1 = true, Int1 = 5, String1 = "test" };

			var serializer = new CerasSerializer();
			var oldData = serializer.Serialize(settings);

			// Now we have some data in the old format,
			var oldLoaded = serializer.Deserialize<SettingsOld>(oldData);
			JObject jObj = JObject.FromObject(oldLoaded);

			// Remove Bool1
			jObj.Remove("Bool1");

			// Rename Int1 -> Int2
			var int1 = jObj["Int1"];
			jObj.Remove("Int1");
			jObj["Int2"] = int1;

			// Add String2
			jObj["String2"] = "string added during data-upgrade";

			var newSettings = jObj.ToObject<SettingsNew>();

			var newData = serializer.Serialize(newSettings);
		}
	}


	class MyCustomPersonFormatter : IFormatter<Person>
	{
		public void Serialize(ref byte[] buffer, ref int offset, Person value)
		{
			// Just for illustration purposes we'll do exactly the same thing that Ceras would
			// normally generate for us automatically, but instead we're doing it manually here.


			// Write a string: this writes a prefix for the length, and then the UTF8 bytes.
			SerializerBinary.WriteString(ref buffer, ref offset, value.Name);

			// Write the health: WriteInt32 will automatically ZigZag-encode the number
			// That means small numbers only take up one byte, instead of 4, only when more bytes are needed, are they actually used!
			SerializerBinary.WriteInt32(ref buffer, ref offset, value.Health);


			if (value.BestFriend == null)
			{
				SerializerBinary.WriteByte(ref buffer, ref offset, 0);
			}
			else
			{
				SerializerBinary.WriteByte(ref buffer, ref offset, 1);
				Serialize(ref buffer, ref offset, value.BestFriend);
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Person value)
		{
			// Just for illustration purposes we'll do exactly the same thing that Ceras would
			// normally generate for us automatically, but instead we're doing it manually here.
			value = new Person(); // todo: usually not needed, this is just a workaround until users can provide their own FormatterResolvers

			value.Name = SerializerBinary.ReadString(buffer, ref offset);
			value.Health = SerializerBinary.ReadInt32(buffer, ref offset);

			var hasBestFriend = SerializerBinary.ReadByte(buffer, ref offset);

			if (hasBestFriend == 0)
				return;

			Deserialize(buffer, ref offset, ref value.BestFriend);
		}
	}

	class SettingsOld
	{
		public bool Bool1;
		public int Int1;
		public string String1;
	}

	class SettingsNew
	{
		// Removed
		// public bool Bool1;

		// Renamed from Int1
		public int Int2;

		public string String1;

		// Newly added
		public string String2;
	}

	/*
	 * Features
	 * - forigen keys and object loading!! usage as a database
	 */


	/*
	 
	 * - NetworkProtocol:
	 *		- known types
	 *      - protocol checksum
	 *		- recycling objects
	 *
	 * - GameDB: IExternalRootObject
	 *		- usage as a game database for objects
	 *		- saving and loading stuff
	 *
	 * - DataUpgrade guide
	 *      - just load old data using old classes
	 *        serialize to Json, adjust stuff, serialize to new object format
	 *		  serialize using ceras again.
	 */

}
