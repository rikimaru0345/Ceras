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

		[Fact]
		public void DateTimeZone()
		{
			DateTime t1 = new DateTime(2000, 5, 5, 5, 5, 5, 5, DateTimeKind.Unspecified);
			DateTime t2 = new DateTime(2000, 5, 5, 5, 5, 5, 5, DateTimeKind.Utc);
			DateTime t3 = new DateTime(2000, 5, 5, 5, 5, 5, 5, DateTimeKind.Local);

			var clone1 = Clone(t1);
			AssertDateTimeEqual(t1, clone1);

			var clone2 = Clone(t2);
			AssertDateTimeEqual(t2, clone2);
			
			var clone3 = Clone(t3);
			AssertDateTimeEqual(t3, clone3);


		}

		static void AssertDateTimeEqual(DateTime t1, DateTime t2)
		{
			Assert.True(t1.Kind == t2.Kind);

			Assert.True(t1.Ticks == t2.Ticks);

			Assert.True(t1.Year == t2.Year &&
						t1.Month == t2.Month &&
						t1.Day == t2.Day &&
						t1.Hour == t2.Hour &&
						t1.Minute == t2.Minute &&
						t1.Second == t2.Second &&
						t1.Millisecond == t2.Millisecond);
		}

	}
}
