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
			// Let's say we have a huge graph of objects and we want to make sure that some specific type will never be serialized
			SerializerConfig config = new SerializerConfig();
			config.OnConfigNewType = t =>
			{
				// We have got a 'TypeConfig', we could check 't.Type' to see what kind of "container" it is.
				// In this example 't.Type' would be 'AnotherClass', but we don't check for that,
				// we just want to remove 'SecretData' from every type everywhere.

				foreach(var m in t.Members)
				{
					// If the field-Type or property-Type is 'SecretData' we'll exclude it
					if (m.MemberType == typeof(SecretData))
					{
						m.SerializationOverride = SerializationOverride.ForceSkip;
					}
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
}
