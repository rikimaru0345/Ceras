using System;

namespace LiveTesting
{
	using Ceras;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using Tutorial;
	using Xunit;

	class Program
	{
		static Guid staticGuid = Guid.Parse("39b29409-880f-42a4-a4ae-2752d97886fa");

		static void Main(string[] args)
		{
			NullableTest();

			ErrorOnDirectEnumerable();

			CtorTest();

			PropertyTest();

			NetworkTest();

			GuidTest();

			EnumTest();

			ComplexTest();

			ListTest();



			var tutorial = new Tutorial();

			tutorial.Step1_SimpleUsage();
			tutorial.Step2_Attributes();
			tutorial.Step3_Recycling();
			tutorial.Step4_KnownTypes();
			tutorial.Step5_CustomFormatters();

			tutorial.Step7_GameDatabase();

		}

		static void NullableTest()
		{
			var ceras = new CerasSerializer();

			var obj = new NullableTestClass
			{
				A = 12.00000476M,
				B = 13.000001326M,
				C = 14,
				D = 15
			};
			
			var data = ceras.Serialize(obj);
			var clone = ceras.Deserialize<NullableTestClass>(data);
			
			Debug.Assert(obj.A == clone.A);
			Debug.Assert(obj.B == clone.B);
			Debug.Assert(obj.C == clone.C);
			Debug.Assert(obj.D == clone.D);
		}

		class NullableTestClass
		{
			public decimal A;
			public decimal? B;
			public byte C;
			public byte? D;
		}

		static void ErrorOnDirectEnumerable()
		{
			// Enumerables obviously cannot be serialized
			// Would we resolve it into a list? Or serialize the "description" / linq-projection it represents??
			// What if its a network-stream? Its just not feasible.

			var ar = new[] { 1, 2, 3, 4 };
			IEnumerable<int> enumerable = ar.Select(x => x + 1);


			try
			{
				var ceras = new CerasSerializer();
				var data = ceras.Serialize(enumerable);

				Debug.Assert(false, "Serialization of IEnumerator is supposed to fail, but it did not!");
			}
			catch (Exception e)
			{
				// All good, we WANT an exception
			}
		}

		static void CtorTest()
		{
			var obj = new ConstructorTest(5);
			var ceras = new CerasSerializer();
			var data = ceras.Serialize(obj);

			// This is expected to throw an exception
			try
			{
				var clone = ceras.Deserialize<ConstructorTest>(data);

				Debug.Assert(false, "deserialization was supposed to fail, but it didn't!");
			}
			catch (Exception e)
			{
				// This is ok and expected!
				// The object does not have a parameterless constructor on purpose.

				// Support for that is already on the todo list.
			}
		}

		static void PropertyTest()
		{
			var p = new PropertyClass
			{
				Name = "qweqrwetwr",
				Num = 348765213,
				Other = new OtherPropertyClass()
			};
			p.Other.Other = p;
			p.Other.PropertyClasses.Add(p);
			p.Other.PropertyClasses.Add(p);


			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.All;

			var ceras = new CerasSerializer();
			var data = ceras.Serialize(p);
			data.VisualizePrint("Property Test");
			var clone = ceras.Deserialize<PropertyClass>(data);

			Debug.Assert(p.Name == clone.Name);
			Debug.Assert(p.Num == clone.Num);
			Debug.Assert(p.Other.PropertyClasses.Count == 2);
			Debug.Assert(p.Other.PropertyClasses[0] == p.Other.PropertyClasses[1]);

		}

		static void ListTest()
		{
			var data = new List<int> { 6, 32, 573, 246, 24, 2, 9 };

			var s = new CerasSerializer();

			var p = new Person() { Name = "abc", Health = 30 };
			var pData = s.Serialize<object>(p);
			pData.VisualizePrint("person data");
			var pClone = (Person)s.Deserialize<object>(pData);
			Assert.Equal(p.Health, pClone.Health);
			Assert.Equal(p.Name, pClone.Name);


			var serialized = s.Serialize(data);
			var clone = s.Deserialize<List<int>>(serialized);
			Assert.Equal(data.Count, clone.Count);
			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], clone[i]);


			var serializedAsObject = s.Serialize<object>(data);
			var cloneObject = s.Deserialize<object>(serializedAsObject);

			Assert.Equal(data.Count, ((List<int>)cloneObject).Count);

			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], ((List<int>)cloneObject)[i]);
		}

		static void ComplexTest()
		{
			var s = new CerasSerializer();

			var c = new ComplexClass();
			var complexClassData = s.Serialize(c);
			complexClassData.VisualizePrint("Complex Data");

			var clone = s.Deserialize<ComplexClass>(complexClassData);

			Debug.Assert(!ReferenceEquals(clone, c));
			Debug.Assert(c.Num == clone.Num);
			Debug.Assert(c.SetName.Name == clone.SetName.Name);
			Debug.Assert(c.SetName.Type == clone.SetName.Type);
		}

		static void EnumTest()
		{
			var s = new CerasSerializer();

			var longEnum = LongEnum.b;

			var longEnumData = s.Serialize(longEnum);
			var cloneLong = s.Deserialize<LongEnum>(longEnumData);
			Debug.Assert(cloneLong == longEnum);


			var byteEnum = ByteEnum.b;
			var cloneByte = s.Deserialize<ByteEnum>(s.Serialize(byteEnum));
			Debug.Assert(byteEnum == cloneByte);
		}

		static void GuidTest()
		{
			var s = new CerasSerializer();

			var g = staticGuid;

			// As real type (generic call)
			var guidData = s.Serialize(g);
			Debug.Assert(guidData.Length == 16);

			var guidClone = s.Deserialize<Guid>(guidData);
			Debug.Assert(g == guidClone);

			// As Object
			var guidAsObjData = s.Serialize<object>(g);
			Debug.Assert(guidAsObjData.Length > 16); // now includes type-data, so it has to be larger
			var objClone = s.Deserialize<object>(guidAsObjData);
			var objCloneCasted = (Guid)objClone;

			Debug.Assert(objCloneCasted == g);

		}

		static void NetworkTest()
		{
			var config = new SerializerConfig
			{
				PersistTypeCache = true,
			};
			config.KnownTypes.Add(typeof(SetName));
			config.KnownTypes.Add(typeof(NewPlayer));
			config.KnownTypes.Add(typeof(LongEnum));
			config.KnownTypes.Add(typeof(ByteEnum));
			config.KnownTypes.Add(typeof(ComplexClass));
			config.KnownTypes.Add(typeof(Complex2));

			var msg = new SetName
			{
				Name = "abc",
				Type = SetName.SetNameType.Join
			};

			CerasSerializer sender = new CerasSerializer(config);
			CerasSerializer receiver = new CerasSerializer(config);

			Console.WriteLine("Hash: " + sender.ProtocolChecksum.Checksum);

			var data = sender.Serialize<object>(msg);
			PrintData(data);
			data = sender.Serialize<object>(msg);
			PrintData(data);

			var obj = receiver.Deserialize<object>(data);
			var clone = (SetName)obj;

			Debug.Assert(clone.Name == msg.Name);
			Debug.Assert(clone.Type == msg.Type);
		}

		static void PrintData(byte[] data)
		{
			var text = BitConverter.ToString(data);
			Console.WriteLine(data.Length + " bytes: " + text);
		}
	}

	class ConstructorTest
	{
		public int x;

		public ConstructorTest(int x)
		{
			this.x = x;
		}
	}

	public enum LongEnum : long
	{
		a = 1,
		b = long.MaxValue - 500
	}

	public enum ByteEnum : byte
	{
		a = 1,
		b = 200,
	}

	class SetName
	{
		public SetNameType Type;
		public string Name;

		public enum SetNameType
		{
			Initial, Change, Join
		}

		public SetName()
		{

		}
	}

	class NewPlayer
	{
		public string Guid;
	}

	interface IComplexInterface { }
	interface IComplexA : IComplexInterface { }
	interface IComplexB : IComplexInterface { }
	interface IComplexX : IComplexA, IComplexB { }

	class Complex2 : IComplexX
	{
		public IComplexB Self;
		public ComplexClass Parent;
	}

	class ComplexClass : IComplexA
	{
		static Random rng = new Random(9);

		public int Num;
		public IComplexA RefA;
		public IComplexB RefB;
		public SetName SetName;

		public ComplexClass()
		{
			Num = rng.Next(0, 10);
			if (Num < 8)
			{
				RefA = new ComplexClass();

				var c2 = new Complex2 { Parent = this };
				c2.Self = c2;
				RefB = c2;

				SetName = new SetName { Type = SetName.SetNameType.Change, Name = "asd" };
			}
		}
	}

	[MemberConfig(TargetMembers = TargetMember.All)]
	class PropertyClass
	{
		public string Name { get; set; } = "abcdef";
		public int Num { get; set; } = 6235;
		public OtherPropertyClass Other { get; set; }
	}

	[MemberConfig(TargetMembers = TargetMember.All)]
	class OtherPropertyClass
	{
		public PropertyClass Other { get; set; }
		public List<PropertyClass> PropertyClasses { get; set; } = new List<PropertyClass>();
	}

}
