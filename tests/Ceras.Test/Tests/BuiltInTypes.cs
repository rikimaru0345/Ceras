using System;

namespace Ceras.Test
{
	using Ceras.Helpers;
	using System.Collections;
	using System.Collections.Generic;
	using System.Collections.Immutable;
	using System.Collections.ObjectModel;
	using System.Linq;
	using System.Numerics;
	using System.Runtime.CompilerServices;
	using System.Runtime.InteropServices;
	using Xunit;

	public class BuiltInTypes : TestBase
	{
		public BuiltInTypes()
		{
			SetSerializerConfigurations(
				Config_DefaultIntEncoding,
				Config_VarIntEncoding,
				Config_FixedIntEncoding,
				Config_WithVersioning);
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


			TestDeepEquality(new BitArray(new byte[] { rngByte, rngByte, rngByte, rngByte, rngByte, rngByte, rngByte, rngByte, rngByte }));
			CheckAndResetTotalRunCount(1 * 3);


			TestDeepEquality(BigInteger.Parse("89160398189867476451238465434753864529019527902394"));
			CheckAndResetTotalRunCount(1 * 3);


			TestDeepEquality(new Complex(rngDouble, rngDouble));
			CheckAndResetTotalRunCount(1 * 3);
		}


		[Fact]
		public void Delegates()
		{
			var config = CreateConfig(f => f.Advanced.DelegateSerialization = DelegateSerializationFlags.AllowStatic | DelegateSerializationFlags.AllowInstance);

			// Simple Action and Func
			Action<int> delegate1 = DelegateTest.TakeIntStatic;
			Func<int> delegate2 = DelegateTest.Get5Static;

			//TestDeepEquality(delegate1, TestMode.Default, config);
			//TestDeepEquality(delegate2, TestMode.Default, config);


			// Merged Recursive ((x+x) + x)
			Func<int> inner = (Func<int>)DelegateTest.Get5Static + DelegateTest.Get5Static;
			Func<int> outer = inner + DelegateTest.Get5Static;

			TestDeepEquality(inner, TestMode.Default, config);
			TestDeepEquality(outer, TestMode.Default, config);


			// Mixed with Instances: Some delegates contain instances
			var a = new DelegateTest();
			var b = new DelegateTest();

			Func<int> mixedInner = (Func<int>)a.Get7 + DelegateTest.Get5Static;
			Func<int> mixedOuter = mixedInner + b.Get7 + a.Get7;
			Func<int> wrapper = new Func<int>(mixedOuter);

			TestDeepEquality(wrapper, TestMode.Default, config);

			Func<int> delA = new Func<int>(a.Get7);
			Func<int> delB = new Func<int>(b.Get7);

			Func<int> delCombined = delA + delB;

			TestDeepEquality(delCombined, TestMode.Default, config);
		}

		class DelegateTest
		{
			static int _counter;
			public string Name;
			public DelegateTest()
			{
				_counter++;
				Name = "Instance #" + _counter;
			}

			public static int Get5Static() => 5;
			public static void TakeIntStatic(int x) { }

			public int Get7() => 7;
			public void TakeIntInstance(int x) { }
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


		[Fact]
		public void PrimitiveArrays()
		{
			var nullBytes = (byte[])null;
			TestDeepEquality(nullBytes, TestMode.AllowNull);

			TestDeepEquality(new byte[0]);

			var byteAr = new byte[rng.Next(100, 200)];
			rng.NextBytes(byteAr);
			TestDeepEquality(byteAr);
		}

		[Fact]
		public void StructArrays()
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
		public void ObjectArrays()
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

		[Fact]
		public void MultidimensionalArrays()
		{
			TestDeepEquality(new int[,]
			{
				{ 1,2,3 },
				{ 4,5,6 },
				{ 7,8,9 },
			});

			TestDeepEquality(new Vector3[,]
			{
				{ rngVec, rngVec },
				{ rngVec, rngVec },
				{ rngVec, rngVec },
				{ rngVec, rngVec },
			});



			var ar3 = (bool[,,])Array.CreateInstance(typeof(bool), 2, 2, 2);
			for (int x = 0; x < 2; x++)
				for (int y = 0; y < 2; y++)
					for (int z = 0; z < 2; z++)
						ar3[x, y, z] = rngByte < 128;
			TestDeepEquality(ar3);



			TestDeepEquality(new string[,]
			{
				{ "a", "b" },
				{ "c", "d" },
				{ "e", "f" },
				{ "g", "h" },
			});

			TestDeepEquality(new string[,]
			{
				{ "a", "b", "c" },
				{ "d", "e", "f" },
			});


			KeyValuePair<Vector3, bool>[,] ar6 = new[,]
			{
				{ new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), },
				{ new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128), },
			};
			TestDeepEquality(ar6);


			KeyValuePair<Vector3, bool>[] ar7 = new[]
			{
				new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128),
				new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128),
				new KeyValuePair<Vector3, bool>(rngVec, rngByte < 128),
			};
			TestDeepEquality(ar7);
		}





#if NETFRAMEWORK

		[Fact]
		public void Bitmap()
		{
			var appveyor = Environment.GetEnvironmentVariable("APPVEYOR");
			if (appveyor != null && appveyor.Equals("true", StringComparison.OrdinalIgnoreCase))
				// No GDI
				return;
			appveyor = Environment.GetEnvironmentVariable("CI");
			if (appveyor != null && appveyor.Equals("true", StringComparison.OrdinalIgnoreCase))
				// No GDI
				return;


			var cBmp = CreateConfig(c => c.Advanced.BitmapMode = BitmapMode.SaveAsBmp);
			var cPng = CreateConfig(c => c.Advanced.BitmapMode = BitmapMode.SaveAsPng);
			var cJpg = CreateConfig(c => c.Advanced.BitmapMode = BitmapMode.SaveAsJpg);

			// Create original bitmap
			var bmp = new System.Drawing.Bitmap(16, 16);
			for (int y = 0; y < 16; y++)
				for (int x = 0; x < 16; x++)
					bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(255, rngByte, rngByte, rngByte));


			// Clone as BMP
			var bmpClone = Clone(bmp, cBmp);
			Assert.True(bmpClone != null);
			for (int y = 0; y < 16; y++)
				for (int x = 0; x < 16; x++)
					Assert.True(bmp.GetPixel(x, y) == bmpClone.GetPixel(x, y));


			// Clone as PNG
			var pngClone = Clone(bmp, cPng);
			Assert.True(pngClone != null);
			for (int y = 0; y < 16; y++)
				for (int x = 0; x < 16; x++)
					Assert.True(bmp.GetPixel(x, y) == pngClone.GetPixel(x, y));

			// Clone as JPG
			var jpgClone = Clone(bmp, cJpg);
			Assert.True(jpgClone != null);
			Assert.True(jpgClone.Width == bmp.Width && jpgClone.Height == bmp.Height);

			// Can't test for equality since jpg is lossy

		}

#endif

		[Fact]
		public void Stack()
		{
			Stack<int> stack = new Stack<int>();
			stack.Push(1);
			stack.Push(2);
			stack.Push(3);

			var clone = Clone(stack);

			Assert.True(stack.Count == clone.Count);
			for (int i = 0; i < 3; i++)
				Assert.True(stack.Pop() == clone.Pop());
		}

		[Fact]
		public void Queue()
		{
			Queue<int> queue = new Queue<int>();
			queue.Enqueue(1);
			queue.Enqueue(2);
			queue.Enqueue(3);

			var clone = Clone(queue);

			Assert.True(queue.Count == clone.Count);
			for (int i = 0; i < 3; i++)
				Assert.True(queue.Dequeue() == clone.Dequeue());
		}

		[Fact]
		public void ImmutableCollections()
		{
			var rc = new ReadOnlyCollection<int>(new[] { 4, 5, rngInt, 6, rngInt, 7, 8, rngInt, 98, 34, 2435, 32131 });
			TestDeepEquality(rc);


			var immAr = ImmutableArray.Create(9, 8, 7, 6, 5, 4, 3, 32, rngInt, 123, 15, 42, 5, rngInt, rngInt);
			TestDeepEquality(immAr);


			ImmutableDictionary<int, string> immDict = ImmutableDictionary<int, string>.Empty.AddRange(new[]
			{
				new KeyValuePair<int, string>(rng.Next(0, 10), "a"),
				new KeyValuePair<int, string>(rng.Next(30, 40), "d"),
			});
			TestDeepEquality(immDict);


			var iq = ImmutableQueue.Create(1, 2, 3, rngInt, 5, 6, 7);
			TestDeepEquality(iq);

			var istack = ImmutableStack.Create(1, 23, rngInt, 2, 4, 5, 22);
			TestDeepEquality(istack);

			var ihs = ImmutableHashSet.Create(5, 5, 5, 5, 5, 5, rngInt, rngInt, 1, 2, 3, 4, 5, 6, 7, 8, 9);
			TestDeepEquality(ihs);
		}

		[Fact]
		public void Collections()
		{
			object[] randomStuff = new object[]
			{
				DateTime.Now,
				Environment.TickCount,
				rngLong,
				rngFloat.ToString(),
				new List<bool>{ true, true, false},
				StructuralComparisons.StructuralComparer,
			};
			TestDeepEquality(randomStuff);


			var link = new LinkedList<string>(new[] { "abc", "123", "xyz", "!!!" });
			TestDeepEquality(link);


			TestDeepEquality(new Dictionary<int, int> { [1] = 1 });
			TestDeepEquality(new Dictionary<string, int> { ["abc"] = 1 });
			TestDeepEquality(new Dictionary<int, string> { [1] = "a" });
			TestDeepEquality(new Dictionary<string, string> { ["abc"] = "abc" });

			TestDeepEquality(new Dictionary<KeyValuePair<int, int>, KeyValuePair<int, int>> { [new KeyValuePair<int, int>(2, 3)] = new KeyValuePair<int, int>(5, 3) });

			TestDeepEquality(new byte[] { 1, 2, 3 });
			TestDeepEquality(new[] { 1, 2, 3 });
			TestDeepEquality(new[] { "a", "b", "c" });

			TestDeepEquality(new[] { new KeyValuePair<int, int>(1, 2) });
			TestDeepEquality(new[] { new KeyValuePair<string, int>("a", 2) });
			TestDeepEquality(new[] { new KeyValuePair<string, string>("a", "b") });
		}


		[Fact]
		public void BasicUsage()
		{
			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllFields;
			config.Advanced.BitmapMode = BitmapMode.SaveAsPng;
			config.Advanced.RespectNonSerializedAttribute = false;
			config.VersionTolerance.Mode = VersionToleranceMode.Standard;
			config.VersionTolerance.VerifySizes = false;

			var ceras = new CerasSerializer(config);

			var c = new Container
			{
				Elements = new Element[]
				{
					new Element {Id = 5, Name = "a"},
					new Element {Id = 123, Name = "z"},
#if NETFRAMEWORK
					new Element { Id=0, Name ="color",  Color = System.Drawing.Color.FromArgb(rngInt)},
#endif
				},
			};

			var data = ceras.Serialize(c);
			var clone = ceras.Deserialize<Container>(data);

			Assert.True(clone.Elements[0].Name == "a");
			Assert.True(clone.Elements[1].Id == 123);

#if NETFRAMEWORK
			Assert.True(clone.Elements[2].Color == c.Elements[2].Color);
#endif
		}



		[Fact]
		public void EnumArrays()
		{
			ConsoleKey[] keys =
			{
				ConsoleKey.A,
				ConsoleKey.Enter,
				ConsoleKey.LeftArrow,
				ConsoleKey.Backspace,
			};

			TestDeepEquality(keys);

			ByteEnum[] byteEnumAr =
			{
				ByteEnum.D,
				ByteEnum.B,
				ByteEnum.G,
				ByteEnum.A,
				ByteEnum.C,
			};

			TestDeepEquality(byteEnumAr);

			UInt64Enum[] longAr =
			{
				UInt64Enum.D,
				UInt64Enum.E,
				UInt64Enum.A,
				UInt64Enum.B,
				UInt64Enum.G,
				UInt64Enum.C,
				UInt64Enum.G,
			};

			TestDeepEquality(longAr);
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

	enum ByteEnum : byte
	{
		A, B, C, D, E, F, G
	}

	enum UInt64Enum : ulong
	{
		A, B, C, D, E, F, G
	}

	class Container
	{
		public Element[] Elements;
	}

	class Element
	{
		public string Name;
		public int Id;
#if NETFRAMEWORK
		public System.Drawing.Color Color;
#endif
	}
}
