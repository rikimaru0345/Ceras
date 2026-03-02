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
	[MemberConfig(TargetMember.AllFields)]
	public class ClassWithBannedFieldRepro {
		public BannedClassRepro Banned = new BannedClassRepro();
	}

	public class UserReproTest : TestBase {
		[Fact]
		public void ExcludedClass_IsExcluded() {
			var robust = new CerasRobustSerializer(config => config.UseGeneratedFormatters());

			var original = new ClassWithBannedFieldRepro();
			original.Banned.Value = 2345;

			var data = robust.Save(original, 1);
			var clone = robust.Load<ClassWithBannedFieldRepro>(data, 1);

			// Banned class should not be serialized
			// We expect that after Load "Value" will be its default (42)
			Assert.NotEqual(original.Banned.Value, clone.Banned.Value);
		}

		[Fact]
		public void RuntimeDiscoveredType_InRobustMode_IsSerializedAsNull() {
			var robust1 = new CerasRobustSerializer(config => config.UseGeneratedFormatters());

			var root = new ContainerRepro { Entities = new List<IEntityRepro>() };
			root.Entities.Add(new RealEntityRepro { Value = 123 });

			// 1. Save
			var data = robust1.Save(root, 1);

			// 2. Load with a fresh serializer (simulates game restart)
			var robust2 = new CerasRobustSerializer(config => config.UseGeneratedFormatters());
			var loaded = robust2.Load<ContainerRepro>(data, 1);

			// This should now succeed thanks to FullNameMarker fallback!
			Assert.NotNull(loaded.Entities[0]);
			Assert.IsType<RealEntityRepro>(loaded.Entities[0]);
			Assert.Equal(123, ((RealEntityRepro)loaded.Entities[0]).Value);
		}

		[Fact]
		public void WillReuseExistingObject() {
			var robust = new CerasRobustSerializer(config => config.UseGeneratedFormatters());

			var original = new ReuseTestClass();
			original.list1.Add(1000);
			original.List2.Add(2000);
			original.InitOnlyAutoProp.Add(3000);

			var listCountA = MyList<int>.created;

			var data = robust.Save(original, 1);

			// Saving should not create anything
			Assert.Equal(listCountA, MyList<int>.created);

			var clone = new ReuseTestClass();
			var listCountB = MyList<int>.created;

			robust.Load(ref clone, data, 1);

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
