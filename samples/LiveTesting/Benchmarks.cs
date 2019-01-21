using BenchmarkDotNet.Attributes;
using System;

namespace LiveTesting
{
	using BenchmarkDotNet.Configs;
	using Ceras;
	using MessagePack;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.Runtime.CompilerServices;
	using System.Runtime.Serialization;
	using Tutorial;

	[ClrJob]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	public class DictionaryBenchmarks
	{
		List<Type> _allTypes = new List<Type>();

		List<Type> _usedTypesA = new List<Type>();
		List<Type> _usedTypesB = new List<Type>();

		Dictionary<Type, int> _normalDict = new Dictionary<Type, int>();

		TypeDictionary<int> _testDict1 = new TypeDictionary<int>();
		TypeDictionary2<int> _testDict2 = new TypeDictionary2<int>();




		[GlobalSetup]
		public void Setup()
		{
			var allTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(asm => asm.GetTypes());
			_allTypes.AddRange(allTypes);

			var rng = new Random(123456);

			for (int i = 0; i < 80; i++)
			{
				var index = rng.Next(0, _allTypes.Count);
				var t = _allTypes[index];
				_usedTypesA.Add(t);
			}

			for (int i = 0; i < 80; i++)
			{
				var index = rng.Next(0, _allTypes.Count);
				var t = _allTypes[index];
				_usedTypesB.Add(t);
			}
		}


		[Benchmark(Baseline = true)]
		public void Dictionary()
		{
			var dict = _normalDict;

			// 1. Add all A
			for (var i = 0; i < _usedTypesA.Count; i++)
			{
				var t = _usedTypesA[i];
				dict[t] = i;
			}

			// 2. Test all A and B and sum their values
			long sum = 0;

			for (var i = 0; i < _usedTypesA.Count; i++)
			{
				var t = _usedTypesA[i];
				if (dict.TryGetValue(t, out int value))
					sum += value;
			}

			for (var i = 0; i < _usedTypesB.Count; i++)
			{
				var t = _usedTypesB[i];
				if (dict.TryGetValue(t, out int value))
					sum += value;
			}
		}

		[Benchmark]
		public void NewDictionary()
		{
			var dict = _testDict1;

			// 1. Add all A
			for (var i = 0; i < _usedTypesA.Count; i++)
			{
				var t = _usedTypesA[i];
				ref var entry = ref dict.GetOrAddValueRef(t);
				entry = i;
			}

			// 2. Test all A and B and sum their values
			long sum = 0;

			for (var i = 0; i < _usedTypesA.Count; i++)
			{
				var t = _usedTypesA[i];
				if (dict.TryGetValue(t, out int value))
					sum += value;
			}

			for (var i = 0; i < _usedTypesB.Count; i++)
			{
				var t = _usedTypesB[i];
				if (dict.TryGetValue(t, out int value))
					sum += value;
			}
		}

		[Benchmark]
		public void NewDictionary2()
		{
			var dict = _testDict2;

			// 1. Add all A
			for (var i = 0; i < _usedTypesA.Count; i++)
			{
				var t = _usedTypesA[i];
				ref var entry = ref dict.GetOrAddValueRef(t);
				entry = i;
			}

			// 2. Test all A and B and sum their values
			long sum = 0;

			for (var i = 0; i < _usedTypesA.Count; i++)
			{
				var t = _usedTypesA[i];
				if (dict.TryGetValue(t, out int value))
					sum += value;
			}

			for (var i = 0; i < _usedTypesB.Count; i++)
			{
				var t = _usedTypesB[i];
				if (dict.TryGetValue(t, out int value))
					sum += value;
			}
		}
	}

	[ClrJob]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
	[CategoriesColumn]
	public class CreateBenchmarks
	{
		List<Type> _createdTypes = new List<Type>();

		[GlobalSetup]
		public void Setup()
		{
			_createdTypes.Add(typeof(List<int>));
			_createdTypes.Add(typeof(List<bool>));
			_createdTypes.Add(typeof(List<DateTime>));
			_createdTypes.Add(typeof(System.IO.MemoryStream));
			_createdTypes.Add(typeof(Person));
		}

		[Benchmark(Baseline = true)]
		public void Activator()
		{
			for (int i = 0; i < _createdTypes.Count; i++)
			{
				var t = _createdTypes[i];
				System.Activator.CreateInstance(t);
			}
		}

		[Benchmark]
		public void CreateMethod()
		{
			for (int i = 0; i < _createdTypes.Count; i++)
			{
				var t = _createdTypes[i];
				var ctor = CreateCtor(t);
				ctor();
			}
		}

		[Benchmark]
		public void CreateExprTree()
		{
			for (int i = 0; i < _createdTypes.Count; i++)
			{
				var t = _createdTypes[i];
				var ctor = CreateExpressionTree(t);
				ctor();
			}
		}


		public static Func<object> CreateCtor(Type type)
		{
			if (type == null)
				throw new NullReferenceException("type");

			ConstructorInfo emptyConstructor = type.GetConstructor(Type.EmptyTypes);

			if (emptyConstructor == null)
				throw new NullReferenceException("cannot find a parameterless constructor for " + type.FullName);

			var dynamicMethod = new DynamicMethod("CreateInstance", type, Type.EmptyTypes, true);
			ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
			ilGenerator.Emit(OpCodes.Nop);
			ilGenerator.Emit(OpCodes.Newobj, emptyConstructor);
			ilGenerator.Emit(OpCodes.Ret);
			return (Func<object>)dynamicMethod.CreateDelegate(typeof(Func<object>));
		}

		public static Func<object> CreateExpressionTree(Type type)
		{
			if (type == null)
				throw new NullReferenceException("type");

			ConstructorInfo emptyConstructor = type.GetConstructor(Type.EmptyTypes);

			if (emptyConstructor == null)
				throw new NullReferenceException("cannot find a parameterless constructor for " + type.FullName);

			var newExp = Expression.New(emptyConstructor);
			var lambda = Expression.Lambda<Func<object>>(newExp);

			return lambda.Compile();
		}

	}

	[ClrJob]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
	[CategoriesColumn]
	public class TemplateBenchmarks
	{


		[GlobalSetup]
		public void Setup()
		{

		}

		[Benchmark(Baseline = true)]
		public void Method1()
		{
		}

		[Benchmark]
		public void Method2()
		{
		}
	}


	[SimpleJob(runStrategy: BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, targetCount: 8, invocationCount: 10000, id: "QuickJob")]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
	[CategoriesColumn]
	public class PrimitiveBenchmarks
	{
		int _someBaseNumber;
		Action _empty1;
		Action _empty2;

		static readonly Type _iTupleInterface = typeof(Tuple<>).GetInterfaces().First(t => t.Name == "ITuple");

		Type[] _types;

		[GlobalSetup]
		public void Setup()
		{
			_someBaseNumber = Environment.TickCount;
			_empty1 = () => { };
			_empty2 = Expression.Lambda<Action>(Expression.Empty()).Compile();

			_types = new Type[] { typeof(Nullable<int>), typeof(Tuple<int, int, int, int, int, int, int, int>), typeof(Tuple<int, int>), typeof(Nullable<ByteEnum>), typeof(Tuple<string>), };
		}


		[BenchmarkCategory("All", "Int")]
		[Benchmark(Baseline = true)]
		[MethodImpl(MethodImplOptions.NoOptimization)]
		public void IntegerIncrement()
		{
			var x = _someBaseNumber;

			x = x + 1;
			x = x + 1;

			x = x + 1;
			x = x + 1;
		}

		[BenchmarkCategory("All", "Int")]
		[Benchmark]
		public void FieldIncrement()
		{
			_someBaseNumber++;
			_someBaseNumber++;

			_someBaseNumber++;
			_someBaseNumber++;
		}


		[BenchmarkCategory("All", "Action")]
		[Benchmark(Baseline = true)]
		public void EmptyAction4x()
		{
			_empty1();
			_empty1();

			_empty1();
			_empty1();
		}

		[BenchmarkCategory("All", "Action")]
		[Benchmark]
		public void EmptyExpression4x()
		{
			_empty2();
			_empty2();

			_empty2();
			_empty2();
		}

		[BenchmarkCategory("All", "Action")]
		[Benchmark]
		public void EmptyAction4xCached()
		{
			var e = _empty1;

			e();
			e();

			e();
			e();
		}

		[BenchmarkCategory("All", "Action")]
		[Benchmark]
		public void EmptyExpression4xCache()
		{
			var e = _empty2;

			e();
			e();

			e();
			e();
		}



		[BenchmarkCategory("All", "TypeCheck")]
		[Benchmark(Baseline = true)]
		public void CheckTypeByGenericTypeDef()
		{
			var types = _types;

			int tuplesFound = 0;

			for (int i = 0; i < types.Length; i++)
			{
				var t = types[i];

				if (t.IsGenericType)
				{
					var genericDef = t.GetGenericTypeDefinition();

					if (genericDef == typeof(Tuple<>) ||
						genericDef == typeof(Tuple<,>) ||
						genericDef == typeof(Tuple<,,>) ||
						genericDef == typeof(Tuple<,,,>) ||
						genericDef == typeof(Tuple<,,,,>) ||
						genericDef == typeof(Tuple<,,,,,>) ||
						genericDef == typeof(Tuple<,,,,,,>) ||
						genericDef == typeof(Tuple<,,,,,,,>))
						tuplesFound++;

				}
			}
		}

		[BenchmarkCategory("All", "TypeCheck")]
		[Benchmark]
		public void CheckTypeByName()
		{
			var types = _types;

			int tuplesFound = 0;

			for (int i = 0; i < types.Length; i++)
			{
				var t = types[i];

				if (t.FullName.StartsWith("System.Tuple"))
					tuplesFound++;
			}
		}

		[BenchmarkCategory("All", "TypeCheck")]
		[Benchmark]
		public void CheckTypeByInterface()
		{
			var types = _types;

			int tuplesFound = 0;

			for (int i = 0; i < types.Length; i++)
			{
				var t = types[i];

				if (_iTupleInterface.IsAssignableFrom(t))
					tuplesFound++;
			}
		}
	}

	// todo: compare write string without get string length

	// todo: compare if using constants in generated code eliminates the virtual dispatch


	[ClrJob]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
	[CategoriesColumn]
	public class SerializerComparisonBenchmarks
	{
		[MessagePackObject]
		public class Person : IEquatable<Person>
		{
			[Key(0)]
			[DataMember]
			public virtual int Age { get; set; }
			[Key(1)]
			[DataMember]
			public virtual string FirstName { get; set; }
			[Key(2)]
			[DataMember]
			public virtual string LastName { get; set; }
			[Key(3)]
			[DataMember]
			public virtual Sex Sex { get; set; }

			public bool Equals(Person other)
			{
				return Age == other.Age && FirstName == other.FirstName && LastName == other.LastName && Sex == other.Sex;
			}
		}

		public enum Sex : sbyte
		{
			Unknown, Male, Female,
		}


		Person p;
		IList<Person> l;
		byte[] _buffer;

		CerasSerializer _ceras;


		[GlobalSetup]
		public void Setup()
		{
			p = new Person
			{
				Age = 99999,
				FirstName = "Windows",
				LastName = "Server",
				Sex = Sex.Male,
			};
			l = Enumerable.Range(1000, 1000).Select(x => new Person { Age = x, FirstName = "Windows", LastName = "Server", Sex = Sex.Female }).ToArray();

			var config = new SerializerConfig();
			config.KnownTypes.Add(typeof(Person));
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(Person[]));
			_ceras = new CerasSerializer(config);
		}


		[Benchmark, BenchmarkCategory("Single")]
		public void Ceras_Single()
		{
			RunCeras(p);
		}

		[Benchmark, BenchmarkCategory("List")]
		public void Ceras_List()
		{
			RunCeras(l);
		}

		[BenchmarkCategory("Single"), Benchmark(Baseline = true)]
		public void MessagePackCSharp_Single()
		{
			RunMessagePackCSharp(p);
		}

		[BenchmarkCategory("List"), Benchmark(Baseline = true)]
		public void MessagePackCSharp_List()
		{
			RunMessagePackCSharp(l);
		}


		T RunCeras<T>(T obj)
		{
			T clone = default(T);

			_ceras.Serialize(obj, ref _buffer);
			_ceras.Deserialize(ref clone, _buffer);

			return clone;
		}

		T RunMessagePackCSharp<T>(T obj)
		{
			T copy = default(T);

			var data = MessagePack.MessagePackSerializer.Serialize(obj);
			copy = MessagePack.MessagePackSerializer.Deserialize<T>(data);

			return copy;
		}


	}


}
