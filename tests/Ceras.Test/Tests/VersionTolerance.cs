using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Ceras.Test
{
	class VersionTest
	{
		public string Name;
		public int Number;
		public VersionTest Next;
	}

	public class VersionTolerance : TestBase
	{
		[Fact]
		public void EmulatorCanReadSchemaData()
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;
			CerasSerializer ceras = new CerasSerializer(config);

			var obj = new VersionTest();
			obj.Name = "abc";
			obj.Number = 123;
			obj.Next = null;

			byte[] buffer = new byte[100];
			ceras.Serialize(obj, ref buffer);

			ReadData(buffer);
		}

		void ReadData(byte[] buffer)
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;

			config.ConfigType<VersionTest>().CustomResolver = (c, t)
				=> new Ceras.Versioning.DynamicEmulator<VersionTest>(c, c.GetTypeMetaData(t).PrimarySchema);

			CerasSerializer ceras = new CerasSerializer(config);

			var clone = ceras.Deserialize<VersionTest>(buffer);

		}

		[Fact]
		public void DerivedProperties()
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;
			CerasSerializer ceras = new CerasSerializer(config);

			var obj = new DerivedClass();
			obj.Name = "derived!";

			var data = ceras.Serialize(obj);
			var clone = ceras.Deserialize<DerivedClass>(data);

			Assert.True(clone.Name == obj.Name);

			Assert.True(config.ConfigType<DerivedClass>().Members.Count(m => m.Member is PropertyInfo) == 1);
		}

	}

	class DisplayAttribute : Attribute
	{
		public string Name;
	}

	public class BaseClass
	{
		[Display(Name = "Bla bla bla")]
		public virtual string Name { get; set; }
	}

	public class DerivedClass : BaseClass
	{
		[Display(Name = "Cla Cla Cla")]
		public override string Name { get; set; }
	}
}
