using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Test
{
	using System.Collections;
	using System.Numerics;
	using Xunit;

	public class Basics : TestBase
	{
		public Basics()
		{
			SetSerializerConfigurations(Config_NoReinterpret, Config_WithReinterpret, Config_WithVersioning);
		}



		[Fact]
		public void BuiltInFormatters()
		{
			TestDeepEquality(new Guid());
			TestDeepEquality(new Guid(rngInt, rngShort, rngShort, 9, 8, 7, 6, 5, 4, 3, 2));
			TestDeepEquality(Guid.NewGuid());
			CheckAndResetTotalRunCount(3 * 3);
			
			
			TestDeepEquality(new TimeSpan());
			TestDeepEquality(new TimeSpan(rngInt));
			CheckAndResetTotalRunCount(2 * 3);
			

			TestDeepEquality(new DateTime());
			TestDeepEquality(new DateTime(rngInt));
			CheckAndResetTotalRunCount(2 * 3);
			

			TestDeepEquality(new DateTimeOffset());
			TestDeepEquality(new DateTimeOffset(rngInt, TimeSpan.FromMinutes(-36)));
			CheckAndResetTotalRunCount(2 * 3);
			
			
			TestDeepEquality(new Uri("https://www.rikidev.com"));
			CheckAndResetTotalRunCount(1 * 3);
			

			TestDeepEquality(new Version(rngInt, rngInt));
			CheckAndResetTotalRunCount(1 * 3);
			

			TestDeepEquality(new BitArray(new byte[]{ rngByte, rngByte, rngByte, rngByte, rngByte, rngByte, rngByte, rngByte, rngByte }));
			CheckAndResetTotalRunCount(1 * 3);


			TestDeepEquality(BigInteger.Parse("89160398189867476451238465434753864529019527902394"));
			CheckAndResetTotalRunCount(1 * 3);


			TestDeepEquality(new Complex(rngDouble, rngDouble));
			CheckAndResetTotalRunCount(1 * 3);

		}


	}
}
