using System;

namespace LiveTesting
{
	using BenchmarkDotNet.Running;
	using Ceras;
	using Ceras.Formatters;
	using Ceras.Resolvers;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using Tutorial;
	using Xunit;
	using Encoding = System.Text.Encoding;

	class Program
	{
		static Guid staticGuid = Guid.Parse("39b29409-880f-42a4-a4ae-2752d97886fa");

		static void Main(string[] args)
		{
			// Benchmarks();


			CustomComparerFormatter();

			ExpressionTreesTest();

			TuplesTest();

			EnsureSealedTypesThrowsException();

			InjectSpecificFormatterTest();

			VersionToleranceTest();

			ReadonlyTest();

			MemberInfoAndTypeInfoTest();

			SimpleDictionaryTest();

			MaintainTypeTest();

			InterfaceFormatterTest();

			InheritTest();

			StructTest();

			WrongRefTypeTest();

			PerfTest();

			TupleTest();

			NullableTest();

			ErrorOnDirectEnumerable();

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
			// tutorial.Step6_NetworkExample();
			tutorial.Step7_GameDatabase();
			// tutorial.Step8_DataUpgrade_OLD();
			// tutorial.Step9_VersionTolerance();
			tutorial.Step10_ReadonlyHandling();

			Console.WriteLine("All tests completed.");
			Console.ReadKey();
		}


		static void Benchmarks()
		{
			var config = new CerasGlobalBenchmarkConfig();

			var b = new ConstantsInGenericContainerBenchmarks();
			b.Setup();

			for (int i = 0; i < 500; i++)
			{
				b.Method1();
				b.Method2();
			}

			//BenchmarkRunner.Run<MergeBlittingBenchmarks>(config);
			//BenchmarkRunner.Run<Feature_MreRefs_Benchmarks>(config);
			//BenchmarkRunner.Run<SerializerComparisonBenchmarks>(config);
			BenchmarkRunner.Run<ConstantsInGenericContainerBenchmarks>(config);



			Environment.Exit(0);
		}


		static void CustomComparerFormatter()
		{
			// Our HashSet<byte> is losing its Comparer
			// We use a custom formatter to fix it

			SerializerConfig config = new SerializerConfig();

			config.OnResolveFormatter.Add((c, t) =>
			{
				if (t == typeof(HashSet<byte[]>))
					return new HashSetFormatterThatKeepsItsComparer();
				return null; // continue searching
			});

			config.ConfigType<HashSet<byte[]>>()
				  .SetFormatter(new HashSetFormatterThatKeepsItsComparer());

			var ceras = new CerasSerializer(config);

			var set = new HashSet<byte[]>(new CustomComparer());
			set.Add(new byte[] { 1, 2, 3 });
			set.Add(new byte[] { 4, 5, 6 });

			var clone = ceras.Deserialize<HashSet<byte[]>>(ceras.Serialize(set));

			Debug.Assert(clone.Comparer.GetType() == typeof(CustomComparer));

		}


		class HashSetFormatterThatKeepsItsComparer : IFormatter<HashSet<byte[]>>
		{
			// Sub-formatters are automatically set by Ceras' dependency injection
			IFormatter<byte[]> _byteArrayFormatter;
			IFormatter<IEqualityComparer<byte[]>> _comparerFormatter; // auto-implemented by Ceras using DynamicFormatter

			public void Serialize(ref byte[] buffer, ref int offset, HashSet<byte[]> set)
			{
				// What do we need?
				// - The comparer
				// - Number of entries
				// - Actual content

				// Comparer
				_comparerFormatter.Serialize(ref buffer, ref offset, set.Comparer);

				// Count
				// We could use a 'IFormatter<int>' field, but Ceras will resolve it to this method anyway...
				SerializerBinary.WriteInt32(ref buffer, ref offset, set.Count);

				// Actual content
				foreach (var array in set)
					_byteArrayFormatter.Serialize(ref buffer, ref offset, array);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref HashSet<byte[]> set)
			{
				IEqualityComparer<byte[]> equalityComparer = null;
				_comparerFormatter.Deserialize(buffer, ref offset, ref equalityComparer);

				// We can already create the hashset
				set = new HashSet<byte[]>(equalityComparer);

				// Read content...
				int count = SerializerBinary.ReadInt32(buffer, ref offset);
				for (int i = 0; i < count; i++)
				{
					byte[] ar = null;
					_byteArrayFormatter.Deserialize(buffer, ref offset, ref ar);

					set.Add(ar);
				}
			}
		}

		class CustomComparer : IEqualityComparer<byte[]>
		{
			public bool Equals(byte[] x, byte[] y)
			{
				if (x == null && y != null)
					return false;
				if (x != null && y == null)
					return false;
				if (x == null && y == null)
					return true;

				return x.SequenceEqual(y);
			}

			public int GetHashCode(byte[] data)
			{
				unchecked
				{
					const int p = 0x1000193;
					int hash = (int)0x811C9DC5;

					for (int i = 0; i < data.Length; i++)
						hash = (hash ^ data[i]) * p;

					hash += hash << 13;
					hash ^= hash >> 7;
					hash += hash << 3;
					hash ^= hash >> 17;
					hash += hash << 5;
					return hash;
				}
			}
		}



		class ReadonlyTestBaseClass
		{
			readonly string _baseName = "base default";

			public ReadonlyTestBaseClass()
			{
				_baseName = "base ctor";
			}

			protected string ProtectedGetBaseName()
			{
				return _baseName;
			}
		}

		class ReadonlyTestClass : ReadonlyTestBaseClass
		{
			readonly string _name = "default";

			public ReadonlyTestClass(string name)
			{
				_name = name;
			}

			public string GetName()
			{
				return _name;
			}

			public string GetBaseName()
			{
				return base.ProtectedGetBaseName();
			}
		}


		static void ExpressionTreesTest()
		{
			// Primitive test (private readonly in a base type)
			{
				SerializerConfig config = new SerializerConfig();
				config.ConfigType<ReadonlyTestClass>()
					  .ConstructByUninitialized()
					  .SetReadonlyHandling(ReadonlyFieldHandling.ForcedOverwrite)
					  .SetTargetMembers(TargetMember.PrivateFields);

				var ceras = new CerasSerializer(config);

				var obj = new ReadonlyTestClass("a");
				var data = ceras.Serialize(obj);

				var clone = ceras.Deserialize<ReadonlyTestClass>(data);

				Debug.Assert(obj.GetName() == clone.GetName());
				Debug.Assert(obj.GetBaseName() == clone.GetBaseName());

				Console.WriteLine();
			}

			// Small test 1
			{
				Expression<Func<string, int, char>> getCharAtIndex = (text, index) => text.ElementAt(index);
				MethodCallExpression body = (MethodCallExpression)getCharAtIndex.Body;

				// Serialize and deserialize delegate
				SerializerConfig config = new SerializerConfig();
				var ceras = new CerasSerializer(config);

				var data = ceras.Serialize<object>(body);
				var dataAsStr = Encoding.ASCII.GetString(data).Replace('\0', ' ');

				var clonedExp = (MethodCallExpression)ceras.Deserialize<object>(data);

				Debug.Assert(clonedExp.Method == body.Method);
				Debug.Assert(clonedExp.Arguments.Count == body.Arguments.Count);
			}

			// Small test 2
			{
				// Test data
				string inputString = "abcdefgh";


				Expression<Func<string, int, char>> getCharAtIndex = (text, index) => (text.ElementAt(index).ToString() + text[index])[0];
				var del1 = getCharAtIndex.Compile();
				char c1 = del1(inputString, 2);


				// Serialize and deserialize expression
				SerializerConfig config = new SerializerConfig();
				var ceras = new CerasSerializer(config);

				var data = ceras.Serialize(getCharAtIndex);
				var dataAsStr = Encoding.ASCII.GetString(data).Replace('\0', ' ');

				var clonedExp = ceras.Deserialize<Expression<Func<string, int, char>>>(data);


				// Compile the restored expression, check if it works and returns the same result
				var del2 = clonedExp.Compile();

				// Check single case
				var c2 = del2(inputString, 2);
				Debug.Assert(c1 == c2);

				// Check all cases
				for (int i = 0; i < inputString.Length; i++)
					Debug.Assert(del1(inputString, i) == del2(inputString, i));
			}
		}


		static void TuplesTest()
		{
			var ceras = new CerasSerializer();

			var obj1 = Tuple.Create(5, "a", DateTime.Now, 3.141);

			var data = ceras.Serialize<object>(obj1);
			var clone = ceras.Deserialize<object>(data);

			Debug.Assert(obj1.Equals(clone));



			var obj2 = (234, "bsdasdasdf", DateTime.Now, 34.23424);

			data = ceras.Serialize<object>(obj2);
			clone = ceras.Deserialize<object>(data);

			Debug.Assert(obj2.Equals(clone));
		}

		static void EnsureSealedTypesThrowsException()
		{
			//
			// 1. Check while serializing
			//
			var obj = new List<object>();
			obj.Add(5);
			obj.Add(DateTime.Now);
			obj.Add("asdasdas");
			obj.Add(new Person() { Name = "abc" });

			var config = new SerializerConfig();
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(int));
			// Some types not added on purpose

			// Should be true by default!
			Debug.Assert(config.Advanced.SealTypesWhenUsingKnownTypes);

			var ceras = new CerasSerializer(config);

			try
			{
				ceras.Serialize(obj);

				Debug.Assert(false, "this line should not be reached, we want an exception here!");
			}
			catch (Exception e)
			{
				// all good
			}

			//
			// 2. Check while deserializing
			//
			config = new SerializerConfig();
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(int));
			config.Advanced.SealTypesWhenUsingKnownTypes = false;
			ceras = new CerasSerializer(config);

			var data = ceras.Serialize(obj);

			config = new SerializerConfig();
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(int));
			config.Advanced.SealTypesWhenUsingKnownTypes = true;
			ceras = new CerasSerializer(config);

			try
			{
				ceras.Deserialize<List<object>>(data);

				Debug.Assert(false, "this line should not be reached, we want an exception here!");
			}
			catch (Exception e)
			{
				// all good
			}

		}

		static void InjectSpecificFormatterTest()
		{
			var config = new SerializerConfig();
			config.OnResolveFormatter.Add((c, t) =>
			{
				if (t == typeof(Person))
					return new DependencyInjectionTestFormatter();
				return null;
			});

			var ceras = new CerasSerializer(config);

			var f = ceras.GetSpecificFormatter(typeof(Person));

			DependencyInjectionTestFormatter exampleFormatter = (DependencyInjectionTestFormatter)f;

			Debug.Assert(exampleFormatter.Ceras == ceras);
			Debug.Assert(exampleFormatter.EnumFormatter != null);
			Debug.Assert(exampleFormatter == exampleFormatter.Self);

		}

		class DependencyInjectionTestFormatter : IFormatter<Person>
		{
			public CerasSerializer Ceras;
			public EnumFormatter<ByteEnum> EnumFormatter;
			public DependencyInjectionTestFormatter Self;

			public void Serialize(ref byte[] buffer, ref int offset, Person value) => throw new NotImplementedException();
			public void Deserialize(byte[] buffer, ref int offset, ref Person value) => throw new NotImplementedException();
		}

		static void ReadonlyTest()
		{
			// Test #1:
			// By default the setting is off. Fields are ignored.
			{
				SerializerConfig config = new SerializerConfig();
				CerasSerializer ceras = new CerasSerializer(config);

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "xyz", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 10, Setting2 = "asdasdas" });

				var data = ceras.Serialize(obj);

				var cloneNew = ceras.Deserialize<ReadonlyFieldsTest>(data);

				Debug.Assert(cloneNew.Int == 1);
				Debug.Assert(cloneNew.String == "a");
				Debug.Assert(cloneNew.Container == null);
			}

			// Test #2A:
			// In the 'Members' mode we expect an exception for readonly value-typed fields.
			{
				SerializerConfig config = new SerializerConfig();
				config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.Members;

				config.ConfigType<ReadonlyFieldsTest>()
					  .ConfigMember(f => f.Int).Include()
					  .ConfigMember(f => f.String).Include();


				CerasSerializer ceras = new CerasSerializer(config);

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "55555", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 555555, Setting2 = "555555555" });

				var data = ceras.Serialize(obj);

				ReadonlyFieldsTest existingTarget = new ReadonlyFieldsTest(6, "66666", null);

				bool gotException = false;
				try
				{
					var cloneNew = ceras.Deserialize<ReadonlyFieldsTest>(data);
				}
				catch (Exception ex)
				{
					gotException = true;
				}

				Debug.Assert(gotException); // We want an exception
			}

			// Test #2B:
			// In the 'Members' mode (when not dealing with value-types)
			// we want Ceras to re-use the already existing object
			{
				SerializerConfig config = new SerializerConfig();
				config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.Members;

				config.ConfigType<ReadonlyFieldsTest>()
					  .ConfigMember(f => f.Int).Exclude()
					  .ConfigMember(f => f.String).Exclude()
					  .ConfigMember(f => f.Container).Include(ReadonlyFieldHandling.Members);

				CerasSerializer ceras = new CerasSerializer(config);

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "55555", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 555555, Setting2 = "555555555" });

				var data = ceras.Serialize(obj);

				var newContainer = new ReadonlyFieldsTest.ContainerThingA { Setting1 = -1, Setting2 = "this should get overwritten" };
				ReadonlyFieldsTest existingTarget = new ReadonlyFieldsTest(6, "66666", newContainer);

				// populate existing data
				ceras.Deserialize<ReadonlyFieldsTest>(ref existingTarget, data);

				// The simple fields should have been ignored
				Debug.Assert(existingTarget.Int == 6);
				Debug.Assert(existingTarget.String == "66666");

				// The reference itself should not have changed
				Debug.Assert(existingTarget.Container == newContainer);

				// The content of the container should be changed now
				Debug.Assert(newContainer.Setting1 == 555555);
				Debug.Assert(newContainer.Setting2 == "555555555");

			}


			// Test #3
			// In 'ForcedOverwrite' mode Ceras should fix all possible mismatches by force (reflection),
			// which means that it should work exactly like as if the field were not readonly.
			{
				SerializerConfig config = new SerializerConfig();
				config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
				CerasSerializer ceras = new CerasSerializer(config);

				// This time we want Ceras to fix everything, reference mismatches and value mismatches alike.

				ReadonlyFieldsTest obj = new ReadonlyFieldsTest(5, "55555", new ReadonlyFieldsTest.ContainerThingA { Setting1 = 324, Setting2 = "1134" });

				var data = ceras.Serialize(obj);

				ReadonlyFieldsTest existingTarget = new ReadonlyFieldsTest(123, null, new ReadonlyFieldsTest.ContainerThingB());

				// populate existing object
				ceras.Deserialize<ReadonlyFieldsTest>(ref existingTarget, data);


				// Now we really check for everything...

				// Sanity check, no way this could happen, but lets make sure.
				Debug.Assert(ReferenceEquals(obj, existingTarget) == false);

				// Fields should be like in the original
				Debug.Assert(existingTarget.Int == 5);
				Debug.Assert(existingTarget.String == "55555");

				// The container type was wrong, Ceras should have fixed that by instantiating a different object 
				// and writing that into the readonly field.
				var container = existingTarget.Container as ReadonlyFieldsTest.ContainerThingA;
				Debug.Assert(container != null);

				// Contents of the container should be correct as well
				Debug.Assert(container.Setting1 == 324);
				Debug.Assert(container.Setting2 == "1134");
			}

			// Test #4:
			// Everything should work fine when using the MemberConfig attribute as well
			{
				var ceras = new CerasSerializer();

				var obj = new ReadonlyFieldsTest2();
				obj.Numbers.Clear();
				obj.Numbers.Add(234);

				var data = ceras.Serialize(obj);

				var clone = new ReadonlyFieldsTest2();
				var originalList = clone.Numbers;
				ceras.Deserialize(ref clone, data);

				Debug.Assert(originalList == clone.Numbers); // actual reference should not have changed
				Debug.Assert(clone.Numbers.Count == 1); // amount of entries should have changed
				Debug.Assert(clone.Numbers[0] == 234); // entry itself should be right
			}

			// todo: also test the case where the existing object does not match the expected type
		}


		class ReadonlyFieldsTest
		{
			public readonly int Int = 1;
			public readonly string String = "a";
			public readonly ContainerBase Container = null;

			public ReadonlyFieldsTest()
			{
			}

			public ReadonlyFieldsTest(int i, string s, ContainerBase c)
			{
				Int = i;
				String = s;
				Container = c;
			}

			public abstract class ContainerBase
			{
			}

			public class ContainerThingA : ContainerBase
			{
				public int Setting1 = 2;
				public string Setting2 = "b";
			}

			public class ContainerThingB : ContainerBase
			{
				public float Float = 1;
				public byte Byte = 1;
				public string String = "c";
			}
		}

		[MemberConfig(ReadonlyFieldHandling = ReadonlyFieldHandling.Members)]
		class ReadonlyFieldsTest2
		{
			public readonly List<int> Numbers = new List<int> { -1, -1, -1, -1 };
		}


		static void MemberInfoAndTypeInfoTest()
		{
			var ceras = new CerasSerializer();

			var multipleTypesHolder = new TypeTestClass();
			multipleTypesHolder.Type1 = typeof(Person);
			multipleTypesHolder.Type2 = typeof(Person);
			multipleTypesHolder.Type3 = typeof(Person);

			multipleTypesHolder.Object1 = new Person();
			multipleTypesHolder.Object2 = new Person();
			multipleTypesHolder.Object3 = multipleTypesHolder.Object1;

			multipleTypesHolder.Member = typeof(TypeTestClass).GetMembers().First();
			multipleTypesHolder.Method = typeof(TypeTestClass).GetMethods().First();


			var data = ceras.Serialize(multipleTypesHolder);
			data.VisualizePrint("member info");
			var multipleTypesHolderClone = ceras.Deserialize<TypeTestClass>(data);

			// todo: check object1 .. 3 as well.

			Debug.Assert(multipleTypesHolder.Member.MetadataToken == multipleTypesHolderClone.Member.MetadataToken);
			Debug.Assert(multipleTypesHolder.Method.MetadataToken == multipleTypesHolderClone.Method.MetadataToken);

			Debug.Assert(multipleTypesHolder.Type1 == multipleTypesHolderClone.Type1);
			Debug.Assert(multipleTypesHolder.Type2 == multipleTypesHolderClone.Type2);
			Debug.Assert(multipleTypesHolder.Type3 == multipleTypesHolderClone.Type3);

		}




		class TypeTestClass
		{
			public Type Type1;
			public Type Type2;
			public Type Type3;
			public object Object1;
			public object Object2;
			public object Object3;

			public MemberInfo Member;
			public MethodInfo Method;
		}


		static void SimpleDictionaryTest()
		{
			var dict = new Dictionary<string, object>
			{
				["name"] = "Test",
			};
			var s = new CerasSerializer();

			var data = s.Serialize(dict);
			var clone = s.Deserialize<Dictionary<string, object>>(data);

			Debug.Assert(dict != clone);

			string n1 = dict["name"] as string;
			string n2 = clone["name"] as string;
			Debug.Assert(n1 == n2);
		}

		static void MaintainTypeTest()
		{
			CerasSerializer ceras = new CerasSerializer();

			var dict = new Dictionary<string, object>
			{
				["int"] = 5,
				["byte"] = (byte)12,
				["float"] = 3.141f,
				["ushort"] = (ushort)345,
				["sbyte"] = (sbyte)91,
			};

			var data1 = ceras.Serialize(dict);
			var clone = ceras.Deserialize<Dictionary<string, object>>(data1);

			foreach (var kvp in dict)
			{
				var cloneValue = clone[kvp.Key];

				Debug.Assert(kvp.Value.Equals(cloneValue));

				if (kvp.Value.GetType() != cloneValue.GetType())
					Debug.Assert(false, $"Type does not match: A={kvp.Value.GetType()} B={cloneValue.GetType()}");
				else
					Console.WriteLine($"Success! Type matching: {kvp.Value.GetType()}");
			}

			var data2 = new Dictionary<string, object>();
			data2["test"] = 5;

			var s = new CerasSerializer();
			var clonedDict = s.Deserialize<Dictionary<string, object>>(s.Serialize(data2));

			var originalType = data2["test"].GetType();
			var clonedType = clonedDict["test"].GetType();

			if (originalType != clonedType)
			{
				Debug.Assert(false, $"Types don't match anymore!! {originalType} {clonedType}");
			}
			else
			{
				Console.WriteLine("Success! Type match: " + originalType);
			}

		}

		static void InterfaceFormatterTest()
		{
			CerasSerializer ceras = new CerasSerializer();

			var intListFormatter = ceras.GetFormatter<IList<int>>();

			List<int> list = new List<int> { 1, 2, 3, 4 };


			byte[] buffer = new byte[200];
			int offset = 0;
			intListFormatter.Serialize(ref buffer, ref offset, list);


			// Deserializing into a IList variable should be no problem!

			offset = 0;
			IList<int> clonedList = null;
			intListFormatter.Deserialize(buffer, ref offset, ref clonedList);

			Debug.Assert(clonedList != null);
			Debug.Assert(clonedList.SequenceEqual(list));
		}

		public abstract class NetObjectMessage
		{
			public uint NetId;
		}
		public class SyncUnitHealth : NetObjectMessage
		{
			public System.Int32 Health;
		}

		static void InheritTest()
		{
			var config = new SerializerConfig();
			config.KnownTypes.Add(typeof(SyncUnitHealth));
			var ceras = new CerasSerializer(config);

			// This should be no problem:
			// - including inherited fields
			// - registering as derived (when derived is used), but still including inherited fields
			// There's literally no reason why this shouldn't work (except for some major bug ofc)

			var obj = new SyncUnitHealth { NetId = 1235, Health = 600 };
			var bytes = ceras.Serialize<object>(obj);

			var clone = ceras.Deserialize<object>(bytes) as SyncUnitHealth;

			Debug.Assert(obj != clone);
			Debug.Assert(obj.NetId == clone.NetId);
			Debug.Assert(obj.Health == clone.Health);

			// we're using KnownTypes, so we expect the message to be really short
			Debug.Assert(bytes.Length == 6);
		}

		class StructTestClass
		{
			public TestStruct TestStruct;
		}

		public struct TestStruct
		{
			[Ceras.Include]
			uint _value;

			public static implicit operator uint(TestStruct id)
			{
				return id._value;
			}
			public static implicit operator TestStruct(uint id)
			{
				return new TestStruct { _value = id };
			}

			public override string ToString()
			{
				return _value.ToString("X");
			}
		}

		static void StructTest()
		{
			var c = new StructTestClass();
			c.TestStruct = 5;

			var ceras = new CerasSerializer();
			var data = ceras.Serialize<object>(c);
			var clone = ceras.Deserialize<object>(data);

			data.VisualizePrint("Struct Test");

			var cloneContainer = clone as StructTestClass;

			Debug.Assert(c.TestStruct == cloneContainer.TestStruct);
		}

		static void VersionToleranceTest()
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;
			config.VersionTolerance.VerifySizes = true;

			config.Advanced.TypeBinder = new DebugVersionTypeBinder();

			// We are using a new ceras instance every time.
			// We want to make sure that no caching is going on.
			// todo: we have to run the same tests with only one instance to test the opposite, which is that cached stuff won't get in the way!

			var v1 = new VersionTest1 { A = 33, B = 34, C = 36 };
			var v2 = new VersionTest2 { A = -3, C2 = -6, D = -7 };

			var v1Data = (new CerasSerializer(config)).Serialize(v1);
			v1Data.VisualizePrint("data with version tolerance");
			(new CerasSerializer(config)).Deserialize<VersionTest2>(ref v2, v1Data);

			Debug.Assert(v1.A == v2.A, "normal prop did not persist");
			Debug.Assert(v1.C == v2.C2, "expected prop 'C2' to be populated by prop previously named 'C'");


			// Everything should work the same way when forcing serialization to <object>
			var v1DataAsObj = (new CerasSerializer(config)).Serialize<object>(v1);
			v1DataAsObj.VisualizePrint("data with version tolerance (as object)");
			var v1Clone = (new CerasSerializer(config)).Deserialize<object>(v1DataAsObj);

			var v1CloneCasted = v1Clone as VersionTest2;
			Debug.Assert(v1CloneCasted != null, "expected deserialized object to have changed to the newer type");
			Debug.Assert(v1CloneCasted.A == v1.A, "expected A to stay the same");
			Debug.Assert(v1CloneCasted.C2 == v1.C, "expected C to be transferred to C2");
			Debug.Assert(v1CloneCasted.D == new VersionTest2().D, "expected D to have the default value");


			// todo: we have to add a test for the case when we read some old data, and the root object has not changed (so it's still the same as always), but a child object has changed
			// todo: test the case where a user-value-type is a field in some root object, and while reading the schema changes (because are reading old data), an exception is expected/wanted
			// todo: test reading multiple different old serializations in random order; each one encoding a different version of the object; 
		}

		static void WrongRefTypeTest()
		{
			var ceras = new CerasSerializer();

			var container = new WrongRefTypeTestClass();

			LinkedList<int> list = new LinkedList<int>();
			list.AddLast(6);
			list.AddLast(2);
			list.AddLast(7);
			container.Collection = list;

			var data = ceras.Serialize(container);
			var linkedListClone = ceras.Deserialize<WrongRefTypeTestClass>(data);
			var listClone = linkedListClone.Collection as LinkedList<int>;

			Debug.Assert(listClone != null);
			Debug.Assert(listClone.Count == 3);
			Debug.Assert(listClone.First.Value == 6);

			// Now the actual test:
			// We change the type that is actually inside
			// And next ask to deserialize into the changed instance!
			// What we expect to happen is that ceras sees that the type is wrong and creates a new object
			container.Collection = new List<int>();

			ceras.Deserialize(ref container, data);

			Debug.Assert(container.Collection is LinkedList<int>);
		}

		class WrongRefTypeTestClass
		{
			public ICollection<int> Collection;
		}

		static void PerfTest()
		{
			// todo: compare against msgpack

			// 1.) Primitives
			// Compare encoding of a mix of small and large numbers to test var-int encoding speed
			var rng = new Random();

			List<int> numbers = new List<int>();
			for (int i = 0; i < 200; i++)
				numbers.Add(i);
			for (int i = 1000; i < 1200; i++)
				numbers.Add(i);
			for (int i = short.MaxValue + 1000; i < short.MaxValue + 1200; i++)
				numbers.Add(i);
			numbers = numbers.OrderBy(n => rng.Next(1000)).ToList();

			var ceras = new CerasSerializer();

			var cerasData = ceras.Serialize(numbers);



			// 2.) Object Data
			// Many fields/properties, some nesting



			/*
			 * todo
			 *
			 * - prewarm proxy pool; prewarm 
			 *
			 * - would ThreadsafeTypeKeyHashTable actually help for the cases where we need to type switch?
			 *
			 * - reference lookups take some time; we could disable them by default and instead let the user manually enable reference serialization per type
			 *      config.EnableReference(typeof(MyObj));
			 *
			 * - directly inline all primitive reader/writer functions. Instead of creating an Int32Formatter the dynamic formatter directly calls the matching method
			 *
			 * - potentially improve number encoding speed (varint encoding is naturally not super fast, maybe we can apply some tricks...)
			 *
			 * - have DynamicFormatter generate its expressions, but inline the result directly to the reference formatter
			 *
			 * - reference proxies: use array instead of a list, don't return references to a pool, just reset them!
			 *
			 * - when we're later dealing with version tolerance, we write all the the type definitions first, and have a skip offset in front of each object
			 *
			 * - avoid overhead of "Formatter" classes for all primitives and directly use them, they can also be accessed through a static generic
			 *
			 * - would a specialized formatter for List<> help? maybe, we'd avoid interfaces vtable calls
			 *
			 * - use static generic caching where possible (rarely the case since ceras can be instantiated multiple times with different settings)
			 *
			 * - primitive arrays can be cast and blitted directly
			 *
			 * - optimize simple properties: serializing the backing field directly, don't call Get/Set (add a setting so it can be deactivated)
			*/
		}

		static void TupleTest()
		{
			// todo:
			//
			// - ValueTuple: can already be serialized as is! We just need to somehow enforce serialization of public fields
			//	 maybe a predefined list of fixed overrides? An additional step directly after ShouldSerializeMember?
			//
			// - Tuple: does not work and (for now) can't be fixed. 
			//   we'll need support for a different kind of ReferenceSerializer (one that does not create an instance)
			//   and a different DynamicSerializer (one that collects the values into local variables, then instantiates the object)
			//

			SerializerConfig config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			var ceras = new CerasSerializer(config);

			var vt = ValueTuple.Create(5, "b", DateTime.Now);

			var data = ceras.Serialize(vt);
			var vtClone = ceras.Deserialize<ValueTuple<int, string, DateTime>>(data);

			Debug.Assert(vt.Item1 == vtClone.Item1);
			Debug.Assert(vt.Item2 == vtClone.Item2);
			Debug.Assert(vt.Item3 == vtClone.Item3);

			//var t = Tuple.Create(5, "b", DateTime.Now);
			//data = ceras.Serialize(vt);
			//var tClone = ceras.Deserialize<Tuple<int, string, DateTime>>(data);
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


			var container = new GenericTest<IEnumerable<int>> { Value = enumerable };
			try
			{
				var ceras = new CerasSerializer();
				var data = ceras.Serialize(container);

				Debug.Assert(false, "Serialization of IEnumerator is supposed to fail, but it did not!");
			}
			catch (Exception e)
			{
				// All good, we WANT an exception
			}
		}



		static void PropertyTest()
		{
			var p = new PropertyClass()
			{
				Name = "qweqrwetwr",
				Num = 348765213,
				Other = new OtherPropertyClass()
			};
			p.MutateProperties();
			p.Other.Other = p;
			p.Other.PropertyClasses.Add(p);
			p.Other.PropertyClasses.Add(p);

			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.All;

			var ceras = new CerasSerializer(config);
			var data = ceras.Serialize(p);
			data.VisualizePrint("Property Test");
			var clone = ceras.Deserialize<PropertyClass>(data);

			Debug.Assert(p.Name == clone.Name);
			Debug.Assert(p.Num == clone.Num);
			Debug.Assert(p.Other.PropertyClasses.Count == 2);
			Debug.Assert(p.Other.PropertyClasses[0] == p.Other.PropertyClasses[1]);

			Debug.Assert(p.VerifyAllPropsAreChanged());

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
			var config = new SerializerConfig();
			config.Advanced.PersistTypeCache = true;
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


		static MethodInfo GetMethod(Expression<Action> e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		static MethodInfo GetMethod<T>(Expression<Func<T>> e)
		{
			var b = e.Body;

			if (b is MethodCallExpression m)
				return m.Method;

			throw new ArgumentException();
		}

		static ConstructorInfo GetCtor<T>(Expression<Func<T>> e)
		{
			var b = e.Body;

			if (b is NewExpression n)
				return n.Constructor;

			throw new ArgumentException();
		}
	}

	class DebugVersionTypeBinder : ITypeBinder
	{
		Dictionary<Type, string> _commonNames = new Dictionary<Type, string>
		{
				{ typeof(VersionTest1), "*" },
				{ typeof(VersionTest2), "*" }
		};

		SimpleTypeBinder _simpleTypeBinder = new SimpleTypeBinder();

		public string GetBaseName(Type type)
		{
			if (_commonNames.TryGetValue(type, out string v))
				return v;

			return _simpleTypeBinder.GetBaseName(type);
		}

		public Type GetTypeFromBase(string baseTypeName)
		{
			// While reading, we want to resolve to 'VersionTest2'
			// So we can simulate that the type changed.
			if (_commonNames.ContainsValue(baseTypeName))
				return typeof(VersionTest2);

			return _simpleTypeBinder.GetTypeFromBase(baseTypeName);
		}

		public Type GetTypeFromBaseAndArguments(string baseTypeName, params Type[] genericTypeArguments)
		{
			throw new NotSupportedException("this binder is only for debugging");
			// return SimpleTypeBinderHelper.GetTypeFromBaseAndAgruments(baseTypeName, genericTypeArguments);
		}
	}

	class VersionTest1
	{
		public int A = -11;
		public int B = -12;
		public int C = -13;
	}
	class VersionTest2
	{
		// A stays as it is
		public int A = 50;

		// B got removed
		// --

		[PreviousName("C", "C2")]
		public int C2 = 52;

		// D is new
		public int D = 53;
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

	class PropertyClass
	{
		public string Name { get; set; } = "abcdef";
		public int Num { get; set; } = 6235;
		public OtherPropertyClass Other { get; set; }

		public string PublicProp { get; set; } = "Public Prop (default value)";
		internal string InternalProp { get; set; } = "Internal Prop (default value)";
		string PrivateProp { get; set; } = "Private Prop (default value)";
		protected string ProtectedProp { get; set; } = "Protected Prop (default value)";
		public string ReadonlyProp1 { get; private set; } = "ReadOnly Prop (default value)";

		public PropertyClass()
		{
		}

		internal void MutateProperties()
		{
			PublicProp = "changed";
			InternalProp = "changed";
			PrivateProp = "changed";
			ProtectedProp = "changed";
			ReadonlyProp1 = "changed";
		}

		internal bool VerifyAllPropsAreChanged()
		{
			return PublicProp == "changed"
				&& InternalProp == "changed"
				&& PrivateProp == "changed"
				&& ProtectedProp == "changed"
				&& ReadonlyProp1 == "changed";
		}
	}

	[MemberConfig(TargetMembers = TargetMember.All)]
	class OtherPropertyClass
	{
		public PropertyClass Other { get; set; }
		public List<PropertyClass> PropertyClasses { get; set; } = new List<PropertyClass>();
	}

	class GenericTest<T>
	{
		public T Value;
	}
}
