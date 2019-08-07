using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Test
{
	using Xunit;

	public class Examples : TestBase
	{
		[Fact]
		void ExcludeByType()
		{
			// Let's say we want to make sure that some type (in this example 'SecretData')
			// will never be serialized. There are various ways to do this.

			SerializerConfig config = new SerializerConfig();
			config.OnConfigNewType = (Ceras.TypeConfig t) =>
			{
				// 'OnConfigNewType' will be called once for every type Ceras encounters.
				// The 'TypeConfig' we get as a parameter is a powerful tool to customize how something should be serialized.

				// 1. Members
				// We check every member (field or property) for its type, and if it
				// is 'SecretData' we let Ceras skip over it (ignoring it).
				foreach (var m in t.Members)
				{
					// If the field-Type or property-Type is 'SecretData' we'll exclude it
					if (m.MemberType == typeof(SecretData))
					{
						m.SerializationOverride = SerializationOverride.ForceSkip;
					}
				}

				// 2. Other locations
				if (t.Type == typeof(SecretData))
				{
					// It is also possible for the type to be referenced in an indirect way
					// - 'SecretData[] array = ...' 
					// - 'List<SecretData> list = ...'
					// Or a sneaky 'SecretData' instance might even try to hide inside an 'object'-type variable!
					// - 'object anything = secretData;'

					// Fortunately 'OnConfigNewType' will still be called in all of those cases.
					// When that happens we have two options:

					// a) Skip all members of 'SecretData' itself
					foreach (var m in t.Members)
						m.SerializationOverride = SerializationOverride.ForceSkip;

					// b) Throw an exception
					// In case Ceras finds a type we never want it to see, aborting the
					// serialization and drawing the programmers attention is probably 
					// the safest bet in any case.
					throw new Exception("Ceras has encountered the 'SecretData' type somewhere");
				}
			};

			var ceras = new CerasSerializer(config);

			var x = new NormalClass();
			x.Objects.Add(new AnotherClass
			{
				Number = Environment.TickCount,
				Name = "abc",
				Factor = 2.345,
				SecretData = new SecretData
				{
					MySecretData = "secret! should never end up getting serialized"
				}
			});

			var data = ceras.Serialize(x);
			var clone = ceras.Deserialize<NormalClass>(data);

			Assert.True(x.Objects[0].SecretData != null);
			Assert.True(clone.Objects[0].SecretData == null);
			Assert.True(clone.Objects[0].Name == "abc");

		}

		[Fact]
		void CallMethodAfterDeserialize()
		{
			List<MyMethodCallTest> list = new List<MyMethodCallTest>();
			list.Add(new MyMethodCallTest());
			list.Add(new MyMethodCallTest());
			list.Add(new MyMethodCallTest());

			foreach (var obj in list)
				obj.ComputeZ();

			var clone = Clone(list);

			for (var i = 0; i < list.Count; i++)
			{
				var obj1 = list[i];
				var obj2 = clone[i];

				Assert.True(obj1.x == obj2.x);
				Assert.True(obj1.y == obj2.y);
				Assert.True(obj1.z == obj2.z);
			}
		}

		[Fact]
		public void BeforeAndAfterSerializeCalls()
		{
			SimpleListTest test = new SimpleListTest();
			test.SomeNumber = 123;
			test.SomeText = "asdasd";

			// Deserialize stuff should only be called on the new object (obviously)
			// And Serialize methods only on the old object
			{
				var clone = Clone(test);

				Assert.True(test.Actions.SequenceEqual(new int[] { 0, 1, 2 }));
				Assert.True(clone.Actions.SequenceEqual(new int[] { 0, 3, 4 }));
			}


			// But if we overwrite the object instead, we expect all methods to appear in the list
			var c = new CerasSerializer();

			var data = c.Serialize(test); // we serialize AGAIN (in addition to the clone above), so we expect 0,1,2,1,2
			Assert.True(test.Actions.SequenceEqual(new int[] { 0, 1, 2, 1, 2 }));

			c.Deserialize(ref test, data); // overwrite data into existing object
			Assert.True(test.Actions.SequenceEqual(new int[] { 0, 1, 2, 1, 2, 3, 4 }));
		}

		[Fact]
		public void CallbackWithContext()
		{
			var ceras = new CerasSerializer();

			var obj = new CallbackTestWithContext();
			obj.Text = "abc";

			// Set context, then clone
			var context = new ContextObject();
			ceras.UserContext = context;

			var clone = ceras.Advanced.Clone(obj);

			// Source object should have gotten calls to OnBeforeSerialize and OnAfterSerialize, 
			// each method incrementing the counter by one.
			// After that the Deserialize call should have added two more.

			Assert.Equal(4, context.Counter);
		}

	}

	class NormalClass
	{
		public List<AnotherClass> Objects = new List<AnotherClass>();
	}

	class AnotherClass
	{
		public int Number;
		public string Name;
		public double Factor;
		public SecretData SecretData; // We'll exclude this without knowing about 'AnotherClass
	}

	class SecretData
	{
		string _secretData;
		public string MySecretData
		{
			set => _secretData = value;
			get => throw new InvalidOperationException("this whole type should never be serialized!");
		}
	}

	class MyMethodCallTest
	{
		public int x;
		public int y;

		[Exclude]
		public int z;

		public MyMethodCallTest()
		{
			var rng = new Random();
			x = rng.Next(1, 100);
			y = rng.Next(1, 100);
		}

		public void ComputeZ()
		{
			z = x + y;
		}

		[OnAfterDeserialize]
		void OnAfterDeserialize() // method doesn't have to be named like that, it can have any name, the attribute is enough.
		{
			Assert.True(x != 0);
			Assert.True(y != 0);

			Assert.True(z == 0);
			ComputeZ();
			Assert.True(z != 0);
		}
	}

	class SimpleListTest
	{
		[NonSerialized]
		public List<int> Actions = new List<int>();

		public int SomeNumber;
		public int AnotherNumber;
		public string SomeText;
		public string AnotherText;


		public SimpleListTest()
		{
			Actions.Add(0);
		}

		[OnBeforeSerialize]
		void BeforeSerialize()
		{
			Actions.Add(1);
		}

		[OnAfterSerialize]
		void AfterSerialize()
		{
			Actions.Add(2);
		}

		[OnBeforeDeserialize]
		void BeforeDeserialize()
		{
			Actions.Add(3);
		}

		[OnAfterDeserialize]
		void AfterDeserialize()
		{
			Actions.Add(4);
		}
	}

	class ContextObject
	{
		public int Counter = 0;
	}

	class CallbackTestWithContext
	{
		public string Text;

		[OnBeforeSerialize]
		void BeforeSerialize(CerasSerializer ceras)
		{
			var ctx = ceras.UserContext as ContextObject;
			ctx.Counter++;
		}

		[OnAfterSerialize]
		void AfterSerialize(CerasSerializer ceras)
		{
			var ctx = ceras.UserContext as ContextObject;
			ctx.Counter++;
		}

		[OnBeforeDeserialize]
		void BeforeDeserialize(CerasSerializer ceras)
		{
			var ctx = ceras.UserContext as ContextObject;
			ctx.Counter++;
		}

		[OnAfterDeserialize]
		void AfterDeserialize(CerasSerializer ceras)
		{
			var ctx = ceras.UserContext as ContextObject;
			ctx.Counter++;
		}
	}
}
