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
}
