
using Ceras;

namespace Tutorial
{
	using Ceras.Formatters;
	using Ceras.Resolvers;
	using Newtonsoft.Json.Linq;
	using System;
	using System.Collections.Generic;

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
			// aka. "I'm here for the cool features! I want to optimize for max-performance later"
			var person = new Person { Name = "riki", Health = 100 };

			var serializer = new CerasSerializer();

			var data = serializer.Serialize(person);
			data.VisualizePrint("Simple Person");

			var clone1 = serializer.Deserialize<Person>(data);
			Console.WriteLine($"Clone: Name={clone1.Name}, Health={clone1.Health}");



			// 2.) Types
			// You can also serialize as <object>.
			// In that case the type information will be included. 
			// If a type is written it will only be written ONCE, so a List<Person> will not suddenly waste a
			// ton of space by continously writing the type-names
			var objectData = serializer.Serialize<object>(person);
			objectData.VisualizePrint("Person as <object>");
			var objectClone = serializer.Deserialize<object>(objectData);




			//
			// 3.) Improvement:
			// Recycle the serialization buffer by keeping the reference to it around.
			// Optionally we can even let Ceras create (or resize) the buffer for us.
			byte[] buffer = null;
			int writtenBytes = serializer.Serialize(person, ref buffer);

			// Now we could send this over the network, for example:
			//   socket.Send(buffer, writtenBytes, SocketFlags.None);
			var clone2 = serializer.Deserialize<Person>(buffer);



			//
			// 3.) Circular references
			// Serializers commonly have trouble serializing circular references.
			// Ceras supports every possible object-graph, and there's literally
			// nothing to do or configure, it just works out of the box.
			// Lets make an example anyway...

			var personA = new Person { Name = "alice" };
			var personB = new Person { Name = "bob" };

			personA.BestFriend = personB;
			personB.BestFriend = personA;

			var dataWithCircularReferences = serializer.Serialize(personA);
			dataWithCircularReferences.VisualizePrint("Circular references data");

			var cloneA = serializer.Deserialize<Person>(dataWithCircularReferences);

			if (cloneA.BestFriend.BestFriend.BestFriend.BestFriend.BestFriend.BestFriend == cloneA)
				Console.WriteLine("Circular reference serialization working as intended!");
			else
				throw new Exception("There was some problem!");


		}

		public void Step2_Attributes()
		{
			/*
			 * Attributes are completely optional.
			 *
			 * Ceras has Attributes to include or skip fields. (todo: and later to set optional names/keys...)
			 *
			 */
			CerasSerializer ceras = new CerasSerializer();

			var obj = new SomeAttributeExample();
			var data = ceras.Serialize(obj);

			data.VisualizePrint("Attribute Example");


			data = ceras.Serialize(new SomeAttributeExample2());
			data.VisualizePrint("Attribute Example 2");
		}

		public void Step3_Recycling()
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

			Person recycledPerson = new Person { Health = 35, Name = "test" };
			byte[] buffer = serializer.Serialize(recycledPerson); // Lets assume we already got a network buffer and now we just have to read it.

			for (int i = 0; i < 100; i++)
			{
				// No person object will be allocated, the fields
				// of 'recycledPerson' will just be overwritten
				serializer.Deserialize<Person>(ref recycledPerson, buffer);
			}



			//
			// Now we'll use some extremely simple object-pooling solution to reduce GC pressure.
			// The 'MyVerySimplePool' is obviously just for illustration, in something like Unity3D you would
			// of course make something much more elaborate...
			//
			// If the data in the buffer tells us "it's a 'null' object" then Ceras will of course set 'recycledPerson' to null.
			// But now you might wonder what happens to the instance that was previously inside 'recycledPerson'.
			// Normally the answer would be that the object would be simply lost (and eventually its space would be reclaimed by the .NET "garbage-collector").
			//
			// In some scenarios (games) we don't want this because garbage-collections often cause stutters.
			// A common solution to this is object-pooling.
			// Ceras supports that by allowing you to "catch" unused objects, so you can return them to your object-pool.

			MyVerySimplePool<Person> pool = new MyVerySimplePool<Person>();

			SerializerConfig config = new SerializerConfig();
			config.ObjectFactoryMethod = type =>
			{
				if (type != typeof(Person))
					return null;

				return pool.GetFromPool();
			};
			config.DiscardObjectMethod = obj =>
			{
				pool.ReturnToPool((Person)obj);
			};


			serializer = new CerasSerializer(config);

			// todo: the example is not fully done yet

			/* 
			var personA = new Person { Name = "a", Health = 1 };
			var personB = new Person { Name = "b", Health = 2 };
			var personC = new Person { Name = "c", Health = 3 };

			personA.BestFriend = personB;
			personB.BestFriend = personC;
			personC.BestFriend = personA;

			serializer.Serialize();
			*/


		}

		public void Step4_KnownTypes()
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

			data.VisualizePrint("Data serialized using KnownTypes");

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

		public void Step5_CustomFormatters()
		{
			/*
			 * Scenario:
			 * - An object is somehow special an needs to be serialized in a very special way
			 * - Simply writing out the fields is not enough, maybe some meta-data is also needed or whatever...
			 * - Ceras' dynamic serializer (code generator) is not enough to deal with the object
			 *
			 * Solution:
			 * - Provide a specialized formatter
			 *
			 * I hear you: "what the hell is a formatter??"
			 * It's actually just a simple class that inherits from IFormatter<YourTypeHere>
			 * and it simply takes over the serialization completely.
			 */

			/*
			 * "OK where do I start?"
			 * 1.) Just inherit from IFormatter<YourTypeHere>
			 * 2.) Give an instance of your formatter to Ceras when it asks for one through the callback in config
			 *
			 */

			SerializerConfig config = new SerializerConfig();
			config.OnResolveFormatter = (s, t) =>
			{
				if (t == typeof(Person))
					return new MyCustomPersonFormatter();
				return null;
			};

			var serializer = new CerasSerializer(config);

			var p = new Person { Name = "riki", Health = 100 };
			p.BestFriend = p;

			var customSerializedData = serializer.Serialize(p);

			var clone = serializer.Deserialize<Person>(customSerializedData);
		}

		public void Step6_NetworkExample()
		{
			// todo: ...

			/*
			 * If you cannot wait for the guide, then take a look at those properties
			 * and read the XML documentation for them (hover over their names or press F12 on them) 
			 */
			 
			/*
			SerializerConfig config = new SerializerConfig();

			config.GenerateChecksum = true;
			config.KnownTypes.Add();
			config.PersistTypeCache = true;

			config.ObjectFactoryMethod = ...;
			config.DiscardObjectMethod = ...;

			*/
		}

		public void Step7_GameDatabase()
		{
			/*
			 * Scenario:
			 * We have "MyMonster" and "MyAbility" for a game.
			 * We want to be able to easily serialize the whole graph, but we also
			 * want MyMonster and MyAbility instances to be saved in their own files!
			 *
			 * Lets first take a look at the classes we're working with:
			 */

			MyMonster monster = new MyMonster();
			monster.Name = "Skeleton Mage";
			monster.Health = 250;
			monster.Mana = 100;

			monster.Abilities.Add(new MyAbility
			{
				Name = "Fireball",
				ManaCost = 12,
				Cooldown = 0.5f,
			});

			monster.Abilities.Add(new MyAbility
			{
				Name = "Ice Lance",
				ManaCost = 14,
				Cooldown = 6,
			});

			// We want to save monsters and abilities in their their own files.
			// Using other serializers this would be a terribly time-consuming task.
			// We would have to add attributes or maybe even write custom serializers so the "root objects"
			// can be when they are referenced in another object..
			// Then we'd need a separate field maybe where we'd save a list of IDs or something....
			// And then at load(deserialization)-time we would have to manually load that list, and resolve the
			// objects they stand for...
			//
			// And all that for literally every "foreign key" (as it is called in database terms). :puke: !
			//
			//
			// Ceras offers a much better approach.
			// You can implement IExternalRootObject, telling Ceras the "Id" of your object.
			// You can generate that Id however you want, most people would proably opt to use some kind of auto-increment counter
			// from their SQLite/SQL/MongoDB/LiteDB/...
			//
			// At load time Ceras will ask you to load the object again given its Id.
			//

			SerializerConfig config = new SerializerConfig();
			var myGameObjectsResolver = new MyGameObjectsResolver();
			config.ExternalObjectResolver = myGameObjectsResolver;
			config.KnownTypes.Add(typeof(MyAbility));
			config.KnownTypes.Add(typeof(MyMonster));
			config.KnownTypes.Add(typeof(List<>));


			// Ceras will call "OnExternalObject" (if you provide a function).
			// It can be used to find all the IExternalRootObject's that Ceras encounters while
			// serializing your object.
			// 
			// In this example we just collect them in a list and then serialize them as well
			List<IExternalRootObject> externalObjects = new List<IExternalRootObject>();

			config.OnExternalObject = obj => { externalObjects.Add(obj); };

			var serializer = new CerasSerializer(config);
			myGameObjectsResolver.Serializer = serializer;

			var monsterData = serializer.Serialize(monster);
			// we can write this monster to the "monsters" sql-table now
			monsterData.VisualizePrint("Monster data");
			MyGameDatabase.Monsters[monster.Id] = monsterData;

			// While serializing the monster we found some other external objects as well (the abilities)
			// Since we have collected them into a list we can serialize them as well.
			// Note: while in this example the abilities themselves don't reference any other external objects,
			// it is quite common in a real-world scenario that every object has tons of references, so keep in mind that 
			// the following serializations would keep adding objects to our 'externalObjects' list.
			for (var i = 0; i < externalObjects.Count; i++)
			{
				var obj = externalObjects[i];

				var abilityData = serializer.Serialize(obj);

				var id = obj.GetReferenceId();
				MyGameDatabase.Abilities[id] = abilityData;

				abilityData.VisualizePrint($"Ability {id} data:");
			}

			// Problems:
			/*
			 * 1.) 
			 * Cannot deserialize recursively
			 * we'd overwrite our object cache, Ids would go out of order, ...
			 * Example: A nested object tells us "yea, this is object ID 5 again", while 5 is already some other object (because its the wrong context!)
			 *
			 * -> Need to make it so the serializer has Stack<>s of object- and type-caches.
			 * 
			 *
			 * 2.)
			 * Keep in mind that we can NOT share a deserialization buffer!!
			 * If we load from Monster1.bin, and then require Spell5.bin, that'd overwrite our shared buffer,
			 * and then when the spell is done and we want to continue with the monster, the data will have changed!
			 *
			 * -> debug helper: "The data has changed while deserializing, this must be a bug on your end!"
			 *
			 * 3.)
			 * while deserializing objects, we need to create them, add to cache, then populate.
			 * otherwise we might get into a situation where we want to load an ability that points to a monster (the one we're already loading)
			 * and then we end up with two monsters (and if they code continues to run, infinite, and we get a stackoverflow)
			 * In other words: Objects that are still being deserialized, need to already be in the cache, so they can be used by other stuff!
			 *
			 * -> create helper class that deals with deserializing object graphs?
			 *
			 */

			// Load the data again:
			var loadedMonster = serializer.Deserialize<MyMonster>(MyGameDatabase.Monsters[1]);

			var ability1 = serializer.Deserialize<MyAbility>(MyGameDatabase.Abilities[1]);
			var ability2 = serializer.Deserialize<MyAbility>(MyGameDatabase.Abilities[2]);

		}

		public void Step8_DataUpgrade()
		{
			/*
			 * So you have a settings class or something, and now you have done 3 types of changes:
			 * - removed a field
			 * - added a new field
			 * - renamed a field
			 *
			 * For now Ceras trades off versioning support for speed and binary-size, so this is not directly supported.
			 * You can still load the old data using the old class (maybe move it into a separate namepace, or an extra .dll)
			 *
 			 *
			 * Note:
			 * - In a future version I'll most likely add an option to serialize
			 *   by name / integer-keys and support for automatic conversion from older formats.
			 *
			 *
			 */



			// In this example I'm just using JObject (from Newtonsoft.Json) for easy editting and transfer.
			// However you can also do it fully manually, but personally I find working with JObject pretty easy.
			var settings = new SettingsOld { Bool1 = true, Int1 = 5, String1 = "test" };

			var serializer = new CerasSerializer();
			var oldData = serializer.Serialize(settings);

			// Now we have some data in the old format
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

	static class MyGameDatabase
	{
		public static Dictionary<int, byte[]> Monsters = new Dictionary<int, byte[]>();
		public static Dictionary<int, byte[]> Abilities = new Dictionary<int, byte[]>();
	}

	class MyGameObjectsResolver : IExternalObjectResolver
	{
		// todo: in the future ExternalObject resolvers will also support dependency-injection, so you won't have to set this yourself...
		public CerasSerializer Serializer;

		public void Resolve<T>(int id, out T value)
		{
			byte[] requestedData;

			if (typeof(T) == typeof(MyMonster))
				requestedData = MyGameDatabase.Monsters[id];
			else if (typeof(T) == typeof(MyAbility))
				requestedData = MyGameDatabase.Abilities[id];
			else
				throw new Exception("cannot resolve external object type: " + typeof(T).FullName);

			value = Serializer.Deserialize<T>(requestedData);
		}
	}

	class MyCustomPersonFormatter : IFormatter<Person>
	{
		// Fields are auto-injected by ceras
		public CerasSerializer Serializer;
		public IFormatter<Person> PersonFormatter;


		public void Serialize(ref byte[] buffer, ref int offset, Person value)
		{
			SerializerBinary.WriteString(ref buffer, ref offset, value.Name);
			SerializerBinary.WriteInt32(ref buffer, ref offset, value.Health);
			PersonFormatter.Serialize(ref buffer, ref offset, value.BestFriend);

			// Important: 
			// You might be tempted to just recursively call Serialize again for 'BestFriend', but that won't work!
			// Do not think you can manually serialize other instances.
			// It may look like that the object injected into "PersonFormatter" is just 'MyCustomPersonFormatter' itself again,
			// but that's not the case!
			// In fact, what you get is a lot of magic behind the scenes that deals with a ton of edge cases (object references, and reference loop handling, and more)
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Person value)
		{
			// Just for illustration purposes we'll do exactly the same thing that Ceras would
			// normally generate for us automatically, but instead we're doing it manually here.

			value.Name = SerializerBinary.ReadString(buffer, ref offset);
			value.Health = SerializerBinary.ReadInt32(buffer, ref offset);
			PersonFormatter.Deserialize(buffer, ref offset, ref value.BestFriend);
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
		// Removed:
		// public bool Bool1;

		// Renamed: from Int1
		public int Int2;

		// This one stays as it is
		public string String1;

		// Newly added:
		public string String2;
	}

	class MyVerySimplePool<T> where T : new()
	{
		Stack<T> _stack = new Stack<T>();

		public int Count => _stack.Count;

		public T GetFromPool()
		{
			if (_stack.Count == 0)
				return new T();

			return _stack.Pop();
		}

		public void ReturnToPool(T obj)
		{
			_stack.Push(obj);
		}
	}

	class MyAbility : IExternalRootObject
	{
		static int _autoIncrementId;
		public int Id = ++_autoIncrementId;
		int IExternalRootObject.GetReferenceId() => Id;

		public string Name;
		public float Cooldown;
		public int ManaCost;
	}

	class MyMonster : IExternalRootObject
	{
		static int _autoIncrementId;
		public int Id = ++_autoIncrementId;
		int IExternalRootObject.GetReferenceId() => Id;

		public string Name;
		public int Health;
		public int Mana;
		public List<MyAbility> Abilities = new List<MyAbility>();

	}

	[CerasConfig(IncludePrivate = true, MemberSerialization = MemberSerialization.OptOut)]
	class SomeAttributeExample
	{
		int _privateNumber = 5;

		public int PublicNumber = 7;

		[Ignore]
		string _privateString = "this will not get serialized";

		[Ignore]
		public string PublicString = "and neither will this...";
	}

	
	[CerasConfig(MemberSerialization = MemberSerialization.OptIn)]
	class SomeAttributeExample2
	{
		[Include]
		int _private1 = 5;
		
		string _privateString = "this will not get serialized";
		
		public int Public1 = 7;

		[Include]
		public string Public2 = "this will be serialized";
	}

	/*
	 *
	 * Features
	 * - forigen keys and object loading!! usage as a database
	 *
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
