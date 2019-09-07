using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Test
{
	using Xunit;

	public class ConstructionAndPooling : TestBase
	{
		static int Add1(int x) => x + 1;
		static int Add2(int x) => x + 2;


		[Fact]
		public void TestDirectPoolingMethods()
		{
			var pool = new InstancePoolTest();

			// Test: Ctor with argument
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Select ctor, not delegate
					  .ConstructBy(() => new Person("name"));

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
				Assert.True(clone.Name.StartsWith("riki"));
				Assert.True(clone.Name.EndsWith(Person.CtorSuffix));
			}

			// Test: Manual config
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  .ConstructBy(TypeConstruction.ByStaticMethod(() => StaticPoolTest.CreatePerson()));

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
			}

			// Test: Normal ctor, but explicitly
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Select ctor, not delegate
					  .ConstructBy(() => new Person());

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
			}

			// Test: Construct from instance-pool
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Instance + method select
					  .ConstructBy(pool, () => pool.CreatePerson());

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
				Assert.True(pool.IsFromPool(clone));
			}

			// Test: Construct from static-pool
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // method select
					  .ConstructBy(() => StaticPoolTest.CreatePerson());

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
				Assert.True(StaticPoolTest.IsFromPool(clone));
			}

			// Test: Construct from any delegate (in this example: a lambda expression)
			{
				SerializerConfig config = new SerializerConfig();

				Person referenceCapturedByLambda = null;

				config.ConfigType<Person>()
					  // Use delegate
					  .ConstructByDelegate(() =>
					  {
						  var obj = new Person();
						  referenceCapturedByLambda = obj;
						  return obj;
					  });

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
				Assert.True(ReferenceEquals(clone, referenceCapturedByLambda));
			}

			// Test: Construct from instance-pool, with parameter
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Use instance + method selection
					  .ConstructBy(pool, () => pool.CreatePersonWithName("abc"));

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
				Assert.True(clone.Name.StartsWith("riki"));
				Assert.True(pool.IsFromPool(clone));
			}

			// Test: Construct from static-pool, with parameter
			{
				SerializerConfig config = new SerializerConfig();

				config.ConfigType<Person>()
					  // Use instance + method selection
					  .ConstructBy(() => StaticPoolTest.CreatePersonWithName("abc"));

				var clone = DoRoundTripTest(config);
				Assert.True(clone != null);
				Assert.True(clone.Name.StartsWith("riki"));
				Assert.True(StaticPoolTest.IsFromPool(clone));
			}
		}
		
		[Fact]
		public void DelegatesTest()
		{
			var config = new SerializerConfig();
			config.Advanced.DelegateSerialization = DelegateSerializationFlags.AllowStatic;
			var ceras = new CerasSerializer(config);

			// 1. Simple test: can ceras persist a static-delegate
			{
				Func<int, int> staticFunc = Add1;

				var data = ceras.Serialize(staticFunc);

				var staticFuncClone = ceras.Deserialize<Func<int, int>>(data);

				Assert.True(staticFuncClone != null);
				Assert.True(object.Equals(staticFunc, staticFuncClone) == true); // must be considered the same
				Assert.True(object.ReferenceEquals(staticFunc, staticFuncClone) == false); // must be a new instance


				Assert.True(staticFuncClone(5) == staticFunc(5));
			}

			// 2. What about a collection of them
			{
				var rng = new Random();
				List<Func<int, int>> funcs = new List<Func<int, int>>();

				for (int i = 0; i < rng.Next(15, 20); i++)
				{
					Func<int, int> f;

					if (rng.Next(100) < 50)
						f = Add1;
					else
						f = Add2;

					funcs.Add(f);
				}

				var data = ceras.Serialize(funcs);
				var cloneList = ceras.Deserialize<List<Func<int, int>>>(data);

				// Check by checking if the result is the same
				Assert.True(funcs.Count == cloneList.Count);
				for (int i = 0; i < funcs.Count; i++)
				{
					var n = rng.Next();
					Assert.True(funcs[i](n) == cloneList[i](n));
				}
			}

			// 3. If we switch to "allow instance", it should persist instance-delegates, but no lambdas
			{
				config = new SerializerConfig();
				config.Advanced.DelegateSerialization = DelegateSerializationFlags.AllowInstance;
				ceras = new CerasSerializer(config);


				//
				// A) Direct Instance
				//
				var method = GetMethod(() => new Person().GetHealth());
				var p = new Person("direct instance") { Health = 3456 };
				var del = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), p, method);

				// Does our delegate even work?
				var testResult = del();
				Assert.True(testResult == p.Health);

				// Can we serialize the normal instance delegate?
				var data = ceras.Serialize(del);
				var clone = ceras.Deserialize<Func<int>>(data);

				// Does it still work?
				Assert.True(testResult == clone());


				

			}
		}




		static Person DoRoundTripTest(SerializerConfig config, string name = "riki")
		{
			var ceras = new CerasSerializer(config);

			var p = new Person();
			p.Name = name;

			var data = ceras.Serialize(p);

			var clone = ceras.Deserialize<Person>(data);
			return clone;
		}


		class StaticPoolTest
		{
			static HashSet<Person> _objectsCreatedByPool = new HashSet<Person>();

			public static Person CreatePerson()
			{
				var p = new Person();
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public static Person CreatePersonWithName(string name)
			{
				var p = new Person();
				p.Name = name;
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public static bool IsFromPool(Person p) => _objectsCreatedByPool.Contains(p);

			public static void DiscardPooledObjectTest(Person p)
			{
			}
		}

		class InstancePoolTest
		{
			HashSet<Person> _objectsCreatedByPool = new HashSet<Person>();

			public Person CreatePerson()
			{
				var p = new Person();
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public Person CreatePersonWithName(string name)
			{
				var p = new Person();
				p.Name = name;
				_objectsCreatedByPool.Add(p);
				return p;
			}

			public bool IsFromPool(Person p) => _objectsCreatedByPool.Contains(p);

			public void DiscardPooledObjectTest(Person p)
			{
			}
		}
		
		class Person
		{
			public const string CtorSuffix = " (modified by constructor)";

			public string Name;
			public int Health;
			public Person BestFriend;

			public Person()
			{
			}

			public Person(string name)
			{
				Name = name + CtorSuffix;
			}

			public int GetHealth() => Health;

			public string SayHello() => $"Hello I'm {Name}";
		}

	}
}
