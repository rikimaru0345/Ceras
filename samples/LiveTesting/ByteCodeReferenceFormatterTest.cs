using Ceras;
using Ceras.Formatters;
using Ceras.Resolvers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveTesting.NewRefFormatter
{
	using TestType = Tutorial.Person;

	//
	// Result:
	// New Byte-Code prefix is 5-10% faster


	internal static class RefFormatterTests
	{
		static byte[] _buffer = new byte[1000];

		static void DoTest<T>(T value, IFormatter<T> formatter)
		{
			int offset = 0;
			formatter.Serialize(ref _buffer, ref offset, value);

			offset = 0;
			T cloneTarget = default;
			formatter.Deserialize(_buffer, ref offset, ref cloneTarget);
		}

		static void DoTestSerialize<T>(T value, IFormatter<T> formatter)
		{
			int offset = 0;
			formatter.Serialize(ref _buffer, ref offset, value);
		}
		static void DoTestDeserialize<T>(IFormatter<T> formatter)
		{
			int offset = 0;
			T cloneTarget = default;
			formatter.Deserialize(_buffer, ref offset, ref cloneTarget);
		}

		static IFormatter<T> CreateDynamicFormatterWithOptions<T>(Action<SerializerConfig> changeConfig)
		{
			var config = new SerializerConfig();
			config.ConfigType<T>().CustomResolver = (c, t) => c.Advanced.GetFormatterResolver<DynamicObjectFormatterResolver>().GetFormatter(t);
			var ceras = new CerasSerializer(config);
			return (DynamicFormatter<T>)ceras.GetSpecificFormatter(typeof(T));
		}

		internal static void Test()
		{
			IFormatter<object> formatterOldRef = CreateDynamicFormatterWithOptions<object>(c =>
			{
				//c.Experimental.UseNewCache = false;
			});

			IFormatter<object> formatterBytePrefix = CreateDynamicFormatterWithOptions<object>(c =>
			{
				//c.Experimental.UseNewCache = true;
			});

			var value = CreateTestValue();

			var jobs = new BenchJob[]
				{
					("Default", () => DoTest(value, formatterOldRef)),
					("BytePrefix", () => DoTest(value, formatterBytePrefix)),
				};

			var runTimes = new[] { 5, 10, 20, 30, 30 };
			foreach (var t in runTimes)
				MicroBenchmark.Run(t, jobs);

			Console.WriteLine("done");
			Console.ReadKey();
		}

		static object CreateTestValue()
		{
			var rng = new Random(1235);

			// Create a list of objects
			List<TestType> list = new List<TestType>();
			int numObjects = 100;
			for (int i = 0; i < numObjects; i++)
			{
				list.Add(new TestType
				{
					Name = "abcde",
					Health = rng.Next(-5, 5000),
				});
			}

			// Everyone has a random friend
			foreach (var obj in list)
				obj.BestFriend = list[rng.Next(0, list.Count)];

			return list;
		}
	}

}
