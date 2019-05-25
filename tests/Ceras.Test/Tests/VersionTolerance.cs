using System;
using System.Collections.Generic;
using System.Linq;
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

		private void ReadData(byte[] buffer)
		{
			var config = new SerializerConfig();
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;

			config.ConfigType<VersionTest>().CustomResolver = (c, t)
				=> new Ceras.Versioning.DynamicEmulator<VersionTest>(c, c.GetTypeMetaData(t).PrimarySchema);

			CerasSerializer ceras = new CerasSerializer(config);

			var clone = ceras.Deserialize<VersionTest>(buffer);

		}
	}
}
