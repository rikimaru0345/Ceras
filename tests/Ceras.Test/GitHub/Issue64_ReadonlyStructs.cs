using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Ceras.Test
{
	public class Issue64_ReadonlyStructs
	{

		public class Wrapper
		{
			public readonly TestDec? NullableStruct;

			public Wrapper(TestDec? nullableStruct)
			{
				NullableStruct = nullableStruct;
			}
		}

		public struct TestDec
		{
			public decimal Value;
		}


		[Fact]
		public void Repro64_v1_NullableStruct()
		{
			var config = new SerializerConfig { DefaultTargets = TargetMember.AllFields, PreserveReferences = false };
			config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
			config.Advanced.SkipCompilerGeneratedFields = false;
			config.OnConfigNewType = tc => tc.TypeConstruction = TypeConstruction.ByUninitialized();

			var ceras = new CerasSerializer(config);

			var wrapper = new Wrapper(new TestDec { Value = 5 });
			var clone = ceras.Advanced.Clone(wrapper);

			Assert.Equal(5M, clone.NullableStruct.Value.Value);

			wrapper = new Wrapper(null);
			clone = ceras.Advanced.Clone(wrapper);

			Assert.Null(clone.NullableStruct);
		}



		public class NullableWrapper
		{
			public Test? TestStruct;

			public NullableWrapper(Test? testStruct)
			{
				TestStruct = testStruct;
			}
		}

		public struct Test : IEquatable<Test>
		{
			public decimal Value;
			public SubStruct SubStruct;

			public override bool Equals(object obj)
			{
				return obj is Test test && Equals(test);
			}

			public bool Equals(Test other)
			{
				return Value == other.Value &&
					   SubStruct.Equals(other.SubStruct);
			}
		}

		public readonly struct SubStruct : IEquatable<SubStruct>
		{
			public readonly NameAge NameAge;
			public readonly int? Count;
			public readonly decimal? Num;

			public SubStruct(NameAge nameAge, int? count, decimal? num)
			{
				NameAge = nameAge;
				Count = count;
				Num = num;
			}

			public override bool Equals(object obj)
			{
				return obj is SubStruct @struct && Equals(@struct);
			}

			public bool Equals(SubStruct other)
			{
				return NameAge.Equals(other.NameAge) &&
					   EqualityComparer<int?>.Default.Equals(Count, other.Count) &&
					   EqualityComparer<decimal?>.Default.Equals(Num, other.Num);
			}
		}

		public readonly struct NameAge
		{
			public readonly string Name;
			public readonly int? Age;

			public NameAge(string name, int? age)
			{
				Name = name;
				Age = age;
			}
		}



		[Fact]
		public void Repro64_v2_ReadonlyStructs_Direct()
		{
			var config = new SerializerConfig { DefaultTargets = TargetMember.AllFields, PreserveReferences = false };
			config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
			config.Advanced.SkipCompilerGeneratedFields = false;
			config.OnConfigNewType = tc =>
			{
				tc.TypeConstruction = TypeConstruction.ByUninitialized();
			};


			var ceras = new CerasSerializer(config);

			var obj = new NullableWrapper(new Test { Value = 2.34M, SubStruct = new SubStruct(new NameAge("riki", 5), 6, 7) });


			var objClone = ceras.Advanced.Clone(obj);
			Assert.Equal(obj.TestStruct, objClone.TestStruct);

			var subStruct = new SubStruct(new NameAge("riki", 5), 6, 7);
			var ssClone = ceras.Advanced.Clone(subStruct);
			Assert.True(DeepComparer.Instance.CheckEquality(subStruct, ssClone));
		}

		[Fact]
		public void Repro64_v2_ReadonlyStructs_Ctor()
		{
			var config = new SerializerConfig { DefaultTargets = TargetMember.AllFields, PreserveReferences = false };
			config.Advanced.ReadonlyFieldHandling = ReadonlyFieldHandling.ForcedOverwrite;
			config.Advanced.SkipCompilerGeneratedFields = false;
			int nCtor=0;
			int nUninit=0;
			config.OnConfigNewType = tc =>
			{
				var ctor = tc.Type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).FirstOrDefault();
				if (ctor != null)
				{
					tc.TypeConstruction = TypeConstruction.ByConstructor(ctor);
					nCtor++;
				}
				else
				{
					tc.TypeConstruction = TypeConstruction.ByUninitialized();
					nUninit++;
				}
			};
			

			var ceras = new CerasSerializer(config);

			var obj = new NullableWrapper(new Test { Value = 2.34M, SubStruct = new SubStruct(new NameAge("riki", 5), 6, 7) });
			var clone = ceras.Advanced.Clone(obj);

			Assert.Equal(7, nCtor);
			Assert.Equal(1, nUninit);

			Assert.Equal(obj.TestStruct.Value.Value, clone.TestStruct.Value.Value);
			Assert.Equal(obj.TestStruct.Value.SubStruct.Num, clone.TestStruct.Value.SubStruct.Num);
			Assert.Equal(obj.TestStruct.Value.SubStruct.NameAge.Age, clone.TestStruct.Value.SubStruct.NameAge.Age);
			Assert.Equal(obj.TestStruct.Value.SubStruct.NameAge.Name, clone.TestStruct.Value.SubStruct.NameAge.Name);
		}

	}
}
