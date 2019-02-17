using System;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
	public class Arrays : TestBase
	{
		[Fact]
		public void Primitives()
		{
			var nullBytes = (byte[])null;
			TestDeepEquality(nullBytes, TestMode.AllowNull);

			TestDeepEquality(new byte[0]);

			var byteAr = new byte[rng.Next(100, 200)];
			rng.NextBytes(byteAr);
			TestDeepEquality(byteAr);
		}

		[Fact]
		public void Structs()
		{
			TestDeepEquality((sbyte[])null, TestMode.AllowNull);
			TestDeepEquality(new sbyte[0]);
			TestDeepEquality(new sbyte[] { -5, -128, 0, 34 });

			TestDeepEquality((decimal[])null, TestMode.AllowNull);
			TestDeepEquality(new decimal[0]);
			TestDeepEquality(new decimal[] { 1M, 2M, 3M, decimal.MinValue, decimal.MaxValue });

			TestDeepEquality(new[]
			{
				new Vector3(1, rngFloat, 3),
				new Vector3(rngFloat, rngFloat, float.NaN),
				new Vector3(float.Epsilon, rngFloat, float.NegativeInfinity),
				new Vector3(-5, float.MaxValue, rngFloat),
			});
			
			TestDeepEquality(new[]
			{
				ValueTuple.Create((byte)150, 5f, 3.0, "a"),
				ValueTuple.Create((byte)150, 5f, 3.0, "b"),
				ValueTuple.Create((byte)150, -5f, 1.0, "c"),
			});

			var r = new Random(DateTime.Now.GetHashCode());
			var decimalData = new decimal[r.Next(100, 200)];
			for (var i = 0; i < decimalData.Length; ++i)
				decimalData[i] = (decimal)r.NextDouble();

			TestDeepEquality(decimalData);
		}

		[Fact]
		public void Objects()
		{
			TestDeepEquality(new[] { new object(), new object(), new object() });
			
			TestDeepEquality(new[] { "asdfg", "asdfg", "asdfg", "", "", "1", "2", "3", ",.-üä#ß351293ß6!§`?=&=$&" });
			
			TestDeepEquality(new[] { (object)DateTime.Now, (object)DateTime.Now, (object)DateTime.Now, (object)DateTime.Now, });

			TestDeepEquality(new[]
			{
				new List<Tuple<int, string>> { Tuple.Create(5, "a"), Tuple.Create(-2222, "q"), Tuple.Create(int.MinValue, "x") },
				new List<Tuple<int, string>> { Tuple.Create(6, "a"), Tuple.Create(33333, "v"), Tuple.Create(int.MinValue / 2, "y") },
				new List<Tuple<int, string>> { Tuple.Create(7, "a"), Tuple.Create(23457, "w"), Tuple.Create(int.MaxValue, "z") },
			});
		}


	}
}
