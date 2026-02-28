using System;
using System.Collections.Generic;
using Xunit;
using Ceras;
using Ceras.Formatters;
using Ceras.Helpers;
using Ceras.Format;
using Ceras.Generated;

namespace Ceras.Test {
	[Ceras.Exclude]
	public class BannedClassRepro {
		public int Value = 42;
	}

	[CerasSerializable]
	[MemberConfig(TargetMember.All)]
	public class ClassWithBannedFieldRepro {
		public BannedClassRepro Banned = new BannedClassRepro();
	}

	public class UserReproTest : TestBase {
		[Fact]
		public void ExcludedClass_IsExcluded() {
			var config = new SerializerConfig();
			Ceras.Generated.RobustFormatters.UseRobustFormatters(config);
			CerasRobust.Configure(config);
			var ceras = new CerasSerializer(config);

			var original = new ClassWithBannedFieldRepro();
			original.Banned.Value = 2345;

			var data = CerasRobust.Save(original, 1, ceras);
			var clone = CerasRobust.Load<ClassWithBannedFieldRepro>(data, 1, ceras);

			// Banned class should not be serialized
			// We expect that after Load "Value" will be its default (42)
			Assert.NotEqual(original.Banned.Value, clone.Banned.Value);
		}

		[Fact]
		public void RuntimeDiscoveredType_InRobustMode_IsSerializedAsNull() {
			var config1 = new SerializerConfig();
			Ceras.Generated.RobustFormatters.UseRobustFormatters(config1);
			CerasRobust.Configure(config1);
			var ceras1 = new CerasSerializer(config1);

			var root = new ContainerRepro { Entities = new List<IEntityRepro>() };
			root.Entities.Add(new RealEntityRepro { Value = 123 });

			// 1. Save
			var data = CerasRobust.Save(root, 1, ceras1);

			// 2. Load
			var config2 = new SerializerConfig();
			CerasRobust.Configure(config2);
			Ceras.Generated.RobustFormatters.UseRobustFormatters(config2);
			var ceras2 = new CerasSerializer(config2);
			var loaded = CerasRobust.Load<ContainerRepro>(data, 1, ceras2);

			// This should now succeed thanks to FullNameMarker fallback!
			Assert.NotNull(loaded.Entities[0]);
			Assert.IsType<RealEntityRepro>(loaded.Entities[0]);
			Assert.Equal(123, ((RealEntityRepro)loaded.Entities[0]).Value);
		}

		[Fact]
		public void WillReuseExistingObject() {
			var config = new SerializerConfig();
			Ceras.Generated.RobustFormatters.UseRobustFormatters(config);
			CerasRobust.Configure(config);
			var ceras = new CerasSerializer(config);

			var original = new ReuseTestClass();
			original.list1.Add(1000);
			original.List2.Add(2000);
			original.InitOnlyAutoProp.Add(3000);

			var listCountA = MyList<int>.created;

			var data = CerasRobust.Save(original, 1, ceras);

			// Saving should not create anything
			Assert.Equal(listCountA, MyList<int>.created);

			var clone = new ReuseTestClass();
			var listCountB = MyList<int>.created;

			CerasRobust.Load(ref clone, data, 1, ceras);

			var listCountC = MyList<int>.created;

			// everything should be the same, ceras should have no problem with any of this, and should not have created more lists
			// it should reuse the lists that are already there
			Assert.Equal(listCountB, listCountC);
			Assert.Equal(listCountC, MyList<int>.created);
		}
	}

	[CerasSerializable]
	public class ContainerRepro {
		public List<IEntityRepro> Entities = new List<IEntityRepro>();
	}

	[CerasSerializable]
	public interface IEntityRepro { }

	public class RealEntityRepro : IEntityRepro {
		public int Value;
	}

	[CerasSerializable]
	public class ReuseTestClass {
		// field
		public MyList<int> list1 = [1, 2, 3];
		// readonly backing field
		private readonly MyList<int> list2 = [7, 5, 1];
		public MyList<int> List2 => list2;
		// autoprop
		public MyList<int> InitOnlyAutoProp { get; } = [4, 5, 6];
	}

	public class MyList<T> : List<T> {
		public static int created = 0;
		public MyList() {
			created++;
		}
	}

}
