
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
			// 4.) 
			// Deciding what gets serialized
			// There are multiple ways to configure what members to serialize
			// Ceras determines member inclusion in this order:
			//
			//  - a. Using the result of "ShouldSerializeMember".
			//       This method can always override everything else.
			//       If it returns "NoOverride" or the method is not set
			//       the search for a decision continues.
			//   
			//  - b. [Ignore] and [Include] attributes on individual members
			//     
			//  - c. [MemberConfig] attribute	  
			//
			//  - d. "DefaultTargets" setting in the SerializerConfig
			//       which defaults to 'TargetMember.PublicFields'
			//

			SerializerConfig config = new SerializerConfig();

			config.DefaultTargets = TargetMember.PublicProperties | TargetMember.PrivateFields;

			config.ShouldSerializeMember = m => SerializationOverride.NoOverride;




			//
			// 5.) Circular references
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

			/*
			 * Note:
			 *
			 * 1.)
			 * Keep in mind that we can not share a deserialization buffer!
			 * That means overwriting the buffer you passed to Deserialize while the deserialization is still in progress will cause problems.
			 * "But why, when would I even attempt that??"
			 * -> If you remember Step1 there's a part about re-using buffers. Well, in some cases you might be tempted to share a deserialization buffer as well.
			 *    For example you might think "if I use File.ReadAllBytes() for every object, that'd be wasteful, better use one big buffer and populate it from the file!"
			 *    The idea is nice and would work to avoid creating a large buffer each time you want to read an object; but when combining it with this IExternalObject idea,
			 *    things begin to break down because:
			 *
			 *    Lets say you have a Monster1.bin file, and load it into the shared buffer. Now while deserializing Ceras realizes that the monster also has a reference to Spell3.bin.
			 *    It will send a request to your OnExternalObject function, asking for Type=Spell ID=3.
			 *    That's when you'd load the Spell3.bin data into the shared buffer, OVERWRITING THE DATA of the monster that is still being deserialized.
			 *
			 * In other words: Just make sure to not overwrite a buffer before the library is done with it (which should be common sense for any programmer tbh :P)
			 *
			 * 2.)
			 * Consider a situation where we have 2 Person objects, both refering to each other (like the BestFriend example in Step1)
			 * And now we'd like to load one person again.
			 * Obviously Ceras has to also load the second person, so it will request it from you
			 * Of course you again load the file (this time the requested person2.bin) and deserialize it.
			 * Now! While deserializing person2 Ceras sees that it needs Person1!
			 * And it calls your OnExternalObject again...
			 *
			 * > "Oh no, its an infinite loop, how to deal with this?"
			 *
			 * No problem. What you do is:
			 * At the very start before deserializing, you first create an empty object:
			 *    var p = new Person();
			 * and then you add it to a dictionary!
			 *    myDictionary.Add(id, p);
			 *
			 * And then you call Ceras in "populate" mode, passing the object you created.
			 *    ceras.Deserialize(ref p, data);
			 *
			 * And you do it that way evertime something gets deserialized.
			 * Now the problem is solved: While deserializing Person2 ceras calls your load function, and this time you already have an object!
			 * Yes, it is not yet fully populated, but that doesn't matter at all. What matters is that the reference matches.
			 *
			 *
			 * If this was confusing to you wait until I wrote another, even more detailed guide or something (or just open an issue on github!)
			 *  
			 *
			 * (todo: write better guide; maybe even write some kind of "helper" class that deals with all of this maybe?)
			 */

			// Load the data again:
			var loadedMonster = serializer.Deserialize<MyMonster>(MyGameDatabase.Monsters[1]);

			var ability1 = serializer.Deserialize<MyAbility>(MyGameDatabase.Abilities[1]);
			var ability2 = serializer.Deserialize<MyAbility>(MyGameDatabase.Abilities[2]);

		}

		public void Step8_DataUpgrade_OLD()
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
	
		public void Step9_VersionTolerance()
		{
			/*
			 * This is like V2 of the 'DataUpgrade' section.
			 * Since then Ceras got a VersionTolerance feature which can be enabled in the config.
			 * 
			 * 
			 */

			// todo 1: show how to use it

			// todo 2: mention that any type-changes of existing fields are not supported (int X; becoming a float X; or so)

			// todo 3: show how to deal with changing types (like in #8, just keep the old object around) 

			// todo 4: In the future: Embed type-data and maybe a version number, so Ceras can also deal with changing types!
			//         Advantage: Now we have perfect version tolerance, no need for any workarounds!
			//		   Disadvantage: Uses more additional space, we can't magically save more data (the member types) without using more space.
			//		   Problems: If we are asked to load an older type, and we see that a type has changed, we need the user to provide some sort of type-converter thingy.
			//				     but we can probably do automatic conversion for all the simple stuff (int->float, ...), but maybe that's too dangerous.
			//				     What if someone has a locale set that uses '.' and we use ',' and then "1.45" becomes 145 as int (which is ofc wrong)
		}

		public void Step10_ReadonlyHandling()
		{
			/*
			 * Situation:
			 * 
			 * You have an object, which has a 'readonly Settings CurrentSettings;'.
			 * Of course that can't (normally) be serialized or deserialized.
			 * But Ceras can still deal with it.
			 * 
			 * Default: readonly fields are completely ignored
			 * 
			 * Members: save/restore the content of the variable itself
			 * 
			 * Forced: also fix if there's a mismatch
			 * 
			 * 
			 * 
			 */


			// todo: example

			// todo: explain that its only for readonly fields. 
			// Readonly props can add a {private set;}, 
			// and if for whatever reason simply adding a private set is not possible and you only have a {get;}=...; then things get extremely complicated 
			// with SkipCompilerGeneratedFields and all the tons of problems that comes with.

			// todo: explain in detail what a mismatch is (null -> not null,  not null -> null, polymorphic type mismatch)
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
		// Public IFormatter<T> fields are auto-injected by Ceras's dependency injection system
		public IFormatter<Person> PersonFormatter;


		public void Serialize(ref byte[] buffer, ref int offset, Person value)
		{
			SerializerBinary.WriteString(ref buffer, ref offset, value.Name);
			SerializerBinary.WriteInt32(ref buffer, ref offset, value.Health);

			// !! Important - Read below!
			PersonFormatter.Serialize(ref buffer, ref offset, value.BestFriend);

			// You might be tempted to just recursively call your own Serialize method (this method) again for 'BestFriend', but that won't work!
			// That won't work because Ceras does many things behind the scenes to make reference-loops work.
			// 
			// Think about it like this:
			// When we want to serialize '.BestFriend' and someone is their own best friend (silly, i know :P) then we'd want the serialized data to
			// say "this object was already written, look it up here..."
			// Otherwise we'd get into an infinite loop, which is exactly what is happening if we'd just write "this.Serialize(ref buffer, ref offset, value.BestFriend);" here.
			//
			// Now, as for the 'PersonFormatter' field in this class.
			// Ceras can inject fields of type 'IFormatter' and 'CerasSerializer' into all its formatters. 
			// So, even though it may look like that the object injected into "PersonFormatter" is just 'MyCustomPersonFormatter' itself again, that's not the case!
			//
			// In case you are interested in what's going on behind the scenes:
			// Ceras actually injects a 'ReferenceFormatter<Person>' into our 'PersonFormatter' field, which deals with reference loops.
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Person value)
		{
			// Nothing interesting here, all the important stuff is explained in 'Serialize()'
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

	[MemberConfig(TargetMember.All)]
	class SomeAttributeExample
	{
		int _privateNumber = 5;

		public int PublicNumber = 7;

		[Ignore]
		string _privateString = "this will not get serialized";

		[Ignore]
		public string PublicString = "and neither will this...";
	}


	[MemberConfig(TargetMember.None)]
	class SomeAttributeExample2
	{
		[Include]
		int _private1 = 5;

		string _privateString = "this will not get serialized";

		public int Public1 = 7;

		[Include]
		public string Public2 = "this will be serialized";
	}


}
