using BenchmarkDotNet.Attributes;
using System;

namespace LiveTesting
{
	using BenchmarkDotNet.Configs;
	using Ceras;
	using MessagePack;
	using Newtonsoft.Json;
	using ProtoBuf;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.Runtime.CompilerServices;
	using System.Runtime.Serialization;
	using Tutorial;


	// todo: compare if using constants in generated code eliminates the virtual dispatch

	// todo: come up with some actual real world benchmarks

	// todo: add Jil, and msgpack-cli 


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


	[SimpleJob(runStrategy: BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, targetCount: 8, invocationCount: 30 * 1000, id: "QuickJob")]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
	[CategoriesColumn]
	public class SerializerComparisonBenchmarks
	{
		[MessagePackObject]
		[ProtoContract]
		public class Person : IEquatable<Person>
		{
			[Key(0)]
			[DataMember]
			[ProtoMember(1)]
			public virtual int Age { get; set; }

			[Key(1)]
			[DataMember]
			[ProtoMember(2)]
			public virtual string FirstName { get; set; }

			[Key(2)]
			[DataMember]
			[ProtoMember(3)]
			public virtual string LastName { get; set; }

			[Key(3)]
			[DataMember]
			[ProtoMember(4)]
			public virtual Sex Sex { get; set; }

			[Key(4)]
			[DataMember]
			[ProtoMember(5)]
			public virtual Person Parent1 { get; set; }
			
			[Key(5)]
			[DataMember]
			[ProtoMember(6)]
			public virtual Person Parent2 { get; set; }

			[Key(6)]
			[DataMember]
			[ProtoMember(7)]
			public virtual int[] LuckyNumbers { get; set; }

			public override bool Equals(object obj)
			{
				if (obj is Person other)
					return Equals(other);
				return false;
			}

			public bool Equals(Person other)
			{
				return Age == other.Age
					   && FirstName == other.FirstName
					   && LastName == other.LastName
					   && Sex == other.Sex
					   && Equals(Parent1, other.Parent1)
					   && Equals(Parent2, other.Parent2);
			}
		}

		public enum Sex : sbyte
		{
			Unknown, Male, Female,
		}


		Person _person;
		Person _person2;
		IList<Person> _list;

		static byte[] _buffer;
		static MemoryStream _memStream = new MemoryStream();
		static CerasSerializer _ceras;


		[GlobalSetup]
		public void Setup()
		{
			var parent1 = new Person
			{
				Age = 123,
				FirstName = "1",
				LastName = "08zu",
				Sex = Sex.Male,
			};
			var parent2 = new Person
			{
				Age = 345636234,
				FirstName = "2",
				LastName = "sgh6tzr",
				Sex = Sex.Female,
			};
			_person = new Person
			{
				Age = 99999,
				FirstName = "3",
				LastName = "child",
				Sex = Sex.Unknown,
				Parent1 = parent1,
				Parent2 = parent2,
			};

			_person2 = new Person
			{
				Age = 234,
				FirstName = "rstgsrhsarhy",
				LastName = "gsdfghdfhxnxfcxg",
				Sex = Sex.Unknown,
				LuckyNumbers = Enumerable.Range(2000, 200).ToArray(),
			};
			


			_list = Enumerable.Range(25000, 100).Select(x => new Person { Age = x, FirstName = "a", LastName = "qwert", Sex = Sex.Female }).ToArray();

			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			config.KnownTypes.Add(typeof(Person));
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(Person[]));
			_ceras = new CerasSerializer(config);

			// Run each serializer once to verify they work correctly!
			void ThrowError() => throw new InvalidOperationException("Cannot continue with the benchmark because a serializer does not round-trip an object correctly. (Benchmark results will be wrong)");

			if (!Equals(RunJson(_person), _person))
				ThrowError();
			if (!Equals(RunMessagePackCSharp(_person), _person))
				ThrowError();
			if (!Equals(RunProtobuf(_person), _person))
				ThrowError();
			if (!Equals(RunCeras(_person), _person))
				ThrowError();

		}


		[BenchmarkCategory("Single"), Benchmark]
		public void Ceras_Single()
		{
			RunCeras(_person);
		}

		[BenchmarkCategory("Single"), Benchmark(Baseline = true)]
		public void MessagePackCSharp_Single()
		{
			RunMessagePackCSharp(_person);
		}

		[BenchmarkCategory("Single"), Benchmark]
		public void Protobuf_Single()
		{
			RunProtobuf(_person);
		}




		[BenchmarkCategory("Single2"), Benchmark]
		public void Ceras_Single2()
		{
			RunCeras(_person2);
		}

		[BenchmarkCategory("Single2"), Benchmark(Baseline = true)]
		public void MessagePackCSharp_Single2()
		{
			RunMessagePackCSharp(_person2);
		}

		/*
		[Benchmark, BenchmarkCategory("Single")]
		public void Json_Single()
		{
			RunJson(person);
		}
		[Benchmark, BenchmarkCategory("List")]
		public void Json_List()
		{
			RunJson(list);
		}
		*/



		/*
		[BenchmarkCategory("List"), Benchmark(OperationsPerInvoke = 1)]
		public void Ceras_List()
		{
			RunCeras(list);
		}
		[BenchmarkCategory("List"), Benchmark(Baseline = true, OperationsPerInvoke = 1)]
		public void MessagePackCSharp_List()
		{
			RunMessagePackCSharp(list);
		}
		[BenchmarkCategory("List"), Benchmark(OperationsPerInvoke = 1)]
		public void Protobuf_List()
		{
			RunProtobuf(list);
		}
		*/


		static T RunCeras<T>(T obj)
		{
			T clone = default(T);

			_ceras.Serialize(obj, ref _buffer);
			_ceras.Deserialize(ref clone, _buffer);

			return clone;
		}

		static T RunMessagePackCSharp<T>(T obj)
		{
			var data = MessagePack.MessagePackSerializer.Serialize(obj);
			var copy = MessagePack.MessagePackSerializer.Deserialize<T>(data);

			return copy;
		}

		static T RunJson<T>(T obj)
		{
			var data = JsonConvert.SerializeObject(obj);
			var clone = JsonConvert.DeserializeObject<T>(data);

			return clone;
		}

		static T RunProtobuf<T>(T obj)
		{
			_memStream.Position = 0;

			ProtoBuf.Serializer.Serialize(_memStream, obj);
			_memStream.Position = 0;
			var clone = ProtoBuf.Serializer.Deserialize<T>(_memStream);

			return clone;
		}


	}

	[SimpleJob(runStrategy: BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, targetCount: 8, invocationCount: 4 * 1000 * 1000, id: "QuickJob")]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
	[CategoriesColumn]
	public class WriteBenchmarks
	{
		int[] _numbers;
		byte[] _buffer;

		[GlobalSetup]
		public void Setup()
		{
			_buffer = new byte[1000];

			_numbers = new int[9];
			_numbers[0] = 0;
			_numbers[1] = -5;
			_numbers[2] = 5;
			_numbers[3] = 200;
			_numbers[4] = -200;
			_numbers[5] = 234235235;
			_numbers[6] = -234235235;
			_numbers[7] = -1;
			_numbers[8] = -32452362;

		}

		[Benchmark(Baseline = true)]
		public void Fixed32()
		{
			int offset = 0;
			for (int i = 0; i < _numbers.Length; i++)
			{
				var n = _numbers[i];
				SerializerBinary.WriteInt32Fixed(ref _buffer, ref offset, n);
			}
		}

		[Benchmark]
		public void NormalVarInt32()
		{
			int offset = 0;
			for (int i = 0; i < _numbers.Length; i++)
			{
				var n = _numbers[i];
				SerializerBinary.WriteInt32(ref _buffer, ref offset, n);
			}
		}




	}


	[SimpleJob(runStrategy: BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, targetCount: 12, invocationCount: 2 * 1000 * 1000, id: "QuickJob")]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
	[CategoriesColumn]
	public class CtorBenchmarks
	{
		List<Type> _createdTypes;
		TypeDictionary<Func<object>> _dynamicMethods = new TypeDictionary<Func<object>>();
		TypeDictionary<Func<object>> _expressionTrees = new TypeDictionary<Func<object>>();

		[GlobalSetup]
		public void Setup()
		{
			_createdTypes = new List<Type>
			{
					typeof(Person),
					typeof(WriteBenchmarks),
					typeof(object),
					typeof(List<int>),
			};
		}

		[Benchmark]
		public void GetUninitialized()
		{
			for (int i = 0; i < _createdTypes.Count; i++)
			{
				var t = _createdTypes[i];

				FormatterServices.GetUninitializedObject(t);
			}
		}

		[Benchmark]
		public void ActivatorCreate()
		{
			for (int i = 0; i < _createdTypes.Count; i++)
			{
				var t = _createdTypes[i];

				Activator.CreateInstance(t);
			}
		}

		[Benchmark]
		public void DynamicMethod()
		{
			for (int i = 0; i < _createdTypes.Count; i++)
			{
				var t = _createdTypes[i];

				ref var f = ref _dynamicMethods.GetOrAddValueRef(t);
				if (f == null)
				{
					var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
								   .FirstOrDefault(c => c.GetParameters().Length == 0);

					if (ctor == null)
						throw new Exception("no ctor found");

					f = (Func<object>)CreateConstructorDelegate(ctor, typeof(Func<object>));
				}

				// Invoke
				f();
			}
		}

		[Benchmark(Baseline = true)]
		public void Expressions()
		{
			for (int i = 0; i < _createdTypes.Count; i++)
			{
				var t = _createdTypes[i];

				ref var f = ref _expressionTrees.GetOrAddValueRef(t);
				if (f == null)
				{
					var lambda = Expression.Lambda<Func<object>>(Expression.New(t));
					f = lambda.Compile();
				}

				// Invoke
				f();
			}
		}

		static Delegate CreateConstructorDelegate(ConstructorInfo constructor, Type delegateType)
		{
			if (constructor == null)
				throw new ArgumentNullException(nameof(constructor));

			if (delegateType == null)
				throw new ArgumentNullException(nameof(delegateType));


			MethodInfo delMethod = delegateType.GetMethod("Invoke");
			//if (delMethod.ReturnType != constructor.DeclaringType)
			//	throw new InvalidOperationException("The return type of the delegate must match the constructors delclaring type");


			// Validate the signatures
			ParameterInfo[] delParams = delMethod.GetParameters();
			ParameterInfo[] constructorParam = constructor.GetParameters();
			if (delParams.Length != constructorParam.Length)
			{
				throw new InvalidOperationException("The delegate signature does not match that of the constructor");
			}
			for (int i = 0; i < delParams.Length; i++)
			{
				if (delParams[i].ParameterType != constructorParam[i].ParameterType ||  // Probably other things we should check ??
					delParams[i].IsOut)
				{
					throw new InvalidOperationException("The delegate signature does not match that of the constructor");
				}
			}
			// Create the dynamic method
			DynamicMethod method =
				new DynamicMethod(
					string.Format("{0}__{1}", constructor.DeclaringType.Name, Guid.NewGuid().ToString().Replace("-", "")),
					constructor.DeclaringType,
					Array.ConvertAll<ParameterInfo, Type>(constructorParam, p => p.ParameterType),
					true
					);


			// Create the il
			ILGenerator gen = method.GetILGenerator();
			for (int i = 0; i < constructorParam.Length; i++)
			{
				if (i < 4)
				{
					switch (i)
					{
					case 0:
						gen.Emit(OpCodes.Ldarg_0);
						break;
					case 1:
						gen.Emit(OpCodes.Ldarg_1);
						break;
					case 2:
						gen.Emit(OpCodes.Ldarg_2);
						break;
					case 3:
						gen.Emit(OpCodes.Ldarg_3);
						break;
					}
				}
				else
				{
					gen.Emit(OpCodes.Ldarg_S, i);
				}
			}
			gen.Emit(OpCodes.Newobj, constructor);
			gen.Emit(OpCodes.Ret);

			return method.CreateDelegate(delegateType);
		}

	}


	[SimpleJob(runStrategy: BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, targetCount: 8, invocationCount: 2000, id: "QuickJob")]
	[MarkdownExporter, HtmlExporter, CsvExporter(BenchmarkDotNet.Exporters.Csv.CsvSeparator.Comma)]
	[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
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

}
