using BenchmarkDotNet.Attributes;
using System;

namespace LiveTesting
{
	using BenchmarkDotNet.Columns;
	using BenchmarkDotNet.Configs;
	using BenchmarkDotNet.Environments;
	using BenchmarkDotNet.Exporters;
	using BenchmarkDotNet.Exporters.Csv;
	using BenchmarkDotNet.Jobs;
	using BenchmarkDotNet.Loggers;
	using BenchmarkDotNet.Mathematics;
	using BenchmarkDotNet.Order;
	using BenchmarkDotNet.Reports;
	using BenchmarkDotNet.Toolchains.CsProj;
	using Ceras;
	using Ceras.Formatters;
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




	class CerasGlobalBenchmarkConfig : ManualConfig
	{
		public CerasGlobalBenchmarkConfig()
		{
			//Add(Job.ShortRun
			Add(Job.MediumRun
				   .WithOutlierMode(OutlierMode.OnlyUpper)

				   .With(CsProjCoreToolchain.NetCoreApp22)
				   .With(Runtime.Core)
				   //.With(CsProjClassicNetToolchain.Net472)
				   //.With(new MonoRuntime("Mono x64", @"C:\Program Files\Mono\bin\mono.exe"))

				   .With(Platform.X64)
				   .WithLaunchCount(1));

			Add(MarkdownExporter.GitHub);
			Add(HtmlExporter.Default);
			Add(new CsvExporter(CsvSeparator.Comma, new SummaryStyle
			{
				PrintUnitsInContent = false,
				PrintUnitsInHeader = true,
			}));


			Set(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest, MethodOrderPolicy.Declared));
			Add(BenchmarkLogicalGroupRule.ByCategory);

			Add(CategoriesColumn.Default);
			Add(TargetMethodColumn.Method);
			Add(BaselineRatioColumn.RatioMean);
			Add(StatisticColumn.Mean, StatisticColumn.StdErr);


			Add(BenchmarkDotNet.Columns.DefaultColumnProviders.Params);

			Add(new ConsoleLogger());

			StopOnFirstError = true;
			//KeepBenchmarkFiles = true;
		}
	}


	public class MergeBlittingBenchmarks
	{
		public struct Vector3
		{
			public float X;
			public float Y;
			public float Z;
		}


		class Vector3Formatter_Normal : IFormatter<Vector3>
		{
			public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				SerializerBinary.WriteFloat32Fixed(ref buffer, ref offset, value.X);
				SerializerBinary.WriteFloat32Fixed(ref buffer, ref offset, value.Y);
				SerializerBinary.WriteFloat32Fixed(ref buffer, ref offset, value.Z);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => throw new NotImplementedException();
		}

		class Vector3Formatter_MergeBlit1 : IFormatter<Vector3>
		{
			public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				const int v3Size = 3 * 4;
				SerializerBinary.EnsureCapacity(ref buffer, offset, v3Size);

				SerializerBinary.WriteFloat32FixedNoCheck(buffer, ref offset, value.X);
				SerializerBinary.WriteFloat32FixedNoCheck(buffer, ref offset, value.Y);
				SerializerBinary.WriteFloat32FixedNoCheck(buffer, ref offset, value.Z);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => throw new NotImplementedException();
		}

		class Vector3Formatter_MergeBlit2 : IFormatter<Vector3>
		{
			public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				const int v3Size = 3 * 4;

				SerializerBinary.EnsureCapacity(ref buffer, offset, v3Size);

				var bufferLocal = buffer;

				SerializerBinary.WriteFloat32FixedNoCheck(bufferLocal, ref offset, value.X);
				SerializerBinary.WriteFloat32FixedNoCheck(bufferLocal, ref offset, value.Y);
				SerializerBinary.WriteFloat32FixedNoCheck(bufferLocal, ref offset, value.Z);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => throw new NotImplementedException();
		}


		class Vector3Formatter_MergeBlit3 : IFormatter<Vector3>
		{
			public unsafe void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				const int v3Size = 3 * 4;

				SerializerBinary.EnsureCapacity(ref buffer, offset, v3Size);

				var bufferLocal = buffer;

				fixed (byte* pBuffer = buffer)
				{
					var ptr = (float*)(pBuffer + offset);

					*ptr = value.X;
					ptr++;

					*ptr = value.Y;
					ptr++;

					*ptr = value.Z;
				}


				offset += v3Size;
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => throw new NotImplementedException();
		}

		class Vector3Formatter_MergeBlit4 : IFormatter<Vector3>
		{
			public unsafe void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				const int v3Size = 3 * 4;

				SerializerBinary.EnsureCapacity(ref buffer, offset, v3Size);

				var bufferLocal = buffer;

				fixed (byte* pBuffer = buffer)
				{
					var ptr = (float*)(pBuffer + offset);

					*ptr = value.X;
				}
				offset += 4;

				fixed (byte* pBuffer = buffer)
				{
					var ptr = (float*)(pBuffer + offset);

					*ptr = value.Y;
				}
				offset += 4;

				fixed (byte* pBuffer = buffer)
				{
					var ptr = (float*)(pBuffer + offset);

					*ptr = value.Z;
				}
				offset += 4;
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => throw new NotImplementedException();
		}


		class Vector3Formatter_MergeBlit5 : IFormatter<Vector3>
		{
			public unsafe void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				const int v3Size = 3 * 4;

				SerializerBinary.EnsureCapacity(ref buffer, offset, v3Size);

				var bufferLocal = buffer;

				fixed (byte* pBuffer = buffer)
				{
					var ptr = (float*)(pBuffer + offset);

					*ptr = value.X;
				}

				fixed (byte* pBuffer = buffer)
				{
					var ptr = (float*)(pBuffer + offset + 4);

					*ptr = value.Y;
				}

				fixed (byte* pBuffer = buffer)
				{
					var ptr = (float*)(pBuffer + offset + 8);

					*ptr = value.Z;
				}
				offset += v3Size;
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => throw new NotImplementedException();
		}

		class Vector3Formatter_Interface : IFormatter<Vector3>
		{
			IFormatter<float> _f;

			public Vector3Formatter_Interface()
			{
				_f = new FloatFormatter();
			}

			public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				_f.Serialize(ref buffer, ref offset, value.X);
				_f.Serialize(ref buffer, ref offset, value.Y);
				_f.Serialize(ref buffer, ref offset, value.Z);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref Vector3 value) => throw new NotImplementedException();
		}
		sealed class FloatFormatter : IFormatter<float>
		{
			public void Serialize(ref byte[] buffer, ref int offset, float value)
			{
				SerializerBinary.WriteFloat32Fixed(ref buffer, ref offset, value);
			}

			public void Deserialize(byte[] buffer, ref int offset, ref float value)
			{
				value = SerializerBinary.ReadFloat32Fixed(buffer, ref offset);
			}
		}

		// Not possible to do this with expressions unfortunately. Will have to come back later to just do it in a DynamicMethod.
		/*
		class Vector3Formatter_ExpressionAutoGen : IFormatter<Vector3>
		{
			readonly SerializeDelegate<Vector3> _dynamicSerializer;

			public Vector3Formatter_ExpressionAutoGen()
			{
				List<Expression> body = new List<Expression>();
				List<ParameterExpression> parameters = new List<ParameterExpression>();
				List<ParameterExpression> locals = new List<ParameterExpression>();

				// Parameters
				var refBuffer = Expression.Parameter(typeof(byte[]).MakeByRefType());
				var refOffset = Expression.Parameter(typeof(int).MakeByRefType());
				var value = Expression.Parameter(typeof(Vector3));
				parameters.AddRange(new[] { refBuffer, refOffset, value });

				// Ensure capacity
				var v3Size = 3 * 4;
				var ensureCapacity = typeof(SerializerBinary).GetMethod(nameof(SerializerBinary.EnsureCapacity));

				body.Add(Expression.Call(ensureCapacity, arg0: refBuffer, arg1: refOffset, Expression.Constant(v3Size)));
				
				// Buffer to ptr
				var bytePtr = Expression.Variable(typeof(byte*));
				Expression.

				_dynamicSerializer = Expression.Lambda<SerializeDelegate<Vector3>>(Expression.Block(locals, body), parameters).Compile();
			}
			
			public void Serialize(ref byte[] buffer, ref int offset, Vector3 value)
			{
				const int v3Size = 3 * 4;

				SerializerBinary.EnsureCapacity(ref buffer, offset, v3Size);


			}
		}
		*/


		IFormatter<Vector3>[] _formatterInstances =
		{
				new Vector3Formatter_Normal(),
				new Vector3Formatter_MergeBlit1(),
				new Vector3Formatter_MergeBlit2(),
				new Vector3Formatter_MergeBlit3(),
				new Vector3Formatter_MergeBlit4(),
				new Vector3Formatter_MergeBlit5(),
				new Vector3Formatter_Interface(),
				new ReinterpretFormatter<Vector3>(),
		};
		[Params(0, 1, 2, 3, 4, 5, 6, 7)] // ParamsSource doesn't seem to work at all, must be a bug in benchmarkdotnet
		public int TypeIndex { get; set; }


		Vector3 _vector3 = new Vector3 { X = -5, Y = 3.121f, Z = 7.2f };
		IFormatter<Vector3> _formatter;
		byte[] _buffer = new byte[100];


		[GlobalSetup]
		public void Setup()
		{
			_formatter = _formatterInstances[TypeIndex];
		}

		[Benchmark]
		public void Test()
		{
			int offset = 0;
			_formatter.Serialize(ref _buffer, ref offset, _vector3);
		}
	}

	public class SerializerComparisonBenchmarks
	{
		[MessagePackObject]
		[ProtoContract]
		[Serializable]
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
			public virtual int[] LuckyNumbers { get; set; } = new int[0];

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
					   && Equals(Parent2, other.Parent2)
					   && LuckyNumbers.SequenceEqual(other.LuckyNumbers);
			}
		}

		public enum Sex : sbyte
		{
			Unknown, Male, Female,
		}

		Person _person;
		IList<Person> _list;

		static Wire.Serializer _wire;
		static NetSerializer.Serializer _netSerializer;

		static byte[] _buffer;
		static MemoryStream _memStream = new MemoryStream();
		static CerasSerializer _ceras;


		[GlobalSetup]
		public void Setup()
		{
			//
			// Create example data
			var parent1 = new Person
			{
				Age = -901,
				FirstName = "Parent 1",
				LastName = "abc",
				Sex = Sex.Male,
			};
			var parent2 = new Person
			{
				Age = 7881964,
				FirstName = "Parent 2",
				LastName = "xyz",
				Sex = Sex.Female,
			};
			_person = new Person
			{
				Age = 5,
				FirstName = "Riki",
				LastName = "Example Person Object",
				Sex = Sex.Unknown,
				Parent1 = parent1,
				Parent2 = parent2,
			};

			_list = Enumerable.Range(25000, 100).Select(x => new Person { Age = x, FirstName = "a", LastName = "b", Sex = Sex.Female }).ToArray();

			//
			// Config Serializers
			_wire = new Wire.Serializer(new Wire.SerializerOptions(knownTypes: new Type[] { typeof(Person), typeof(Person[]) }));

			_netSerializer = new NetSerializer.Serializer(rootTypes: new Type[] { typeof(Person), typeof(Person[]) });

			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			var knownTypes = new[] { typeof(Person), typeof(List<>), typeof(Person[]) };
			config.KnownTypes.AddRange(knownTypes);
			config.PreserveReferences = false;
			_ceras = new CerasSerializer(config);

			//
			// Run each serializer once to verify they work correctly!
			if (!Equals(RunCeras(_person), _person))
				ThrowError();
			if (!Equals(RunJson(_person), _person))
				ThrowError();
			if (!Equals(RunMessagePackCSharp(_person), _person))
				ThrowError();
			if (!Equals(RunProtobuf(_person), _person))
				ThrowError();
			if (!Equals(RunWire(_person), _person))
				ThrowError();
			if (!Equals(RunNetSerializer(_person), _person))
				ThrowError();

			void ThrowError() => throw new InvalidOperationException("Cannot continue with the benchmark because a serializer does not round-trip an object correctly. (Benchmark results will be wrong)");
		}



		[BenchmarkCategory("Single"), Benchmark]
		public void MessagePack_Single() => RunMessagePackCSharp(_person);
		[BenchmarkCategory("Single"), Benchmark(Baseline = true)]
		public void Ceras_Single() => RunCeras(_person);
		[BenchmarkCategory("Single"), Benchmark]
		public void Protobuf_Single() => RunProtobuf(_person);
		[BenchmarkCategory("Single"), Benchmark]
		public void Wire_Single() => RunWire(_person);
		[BenchmarkCategory("Single"), Benchmark]
		public void NetSerializer_Single() => RunNetSerializer(_person);

		[BenchmarkCategory("List"), Benchmark(Baseline = true)]
		public void Ceras_List() => RunCeras(_list);
		[BenchmarkCategory("List"), Benchmark]
		public void MessagePack_List() => RunMessagePackCSharp(_list);
		[BenchmarkCategory("List"), Benchmark]
		public void Protobuf_List() => RunProtobuf(_list);
		[BenchmarkCategory("List"), Benchmark]
		public void Wire_List() => RunWire(_list);
		[BenchmarkCategory("List"), Benchmark]
		public void NetSerializer_List() => RunNetSerializer(_list);


		// Json is way too slow, it messes with the graph I want to draw
		/*
		[BenchmarkCategory("Single"), Benchmark]
		public void JsonNet_Single() => RunJson(_person);
		[BenchmarkCategory("List"), Benchmark]
		public void Json_List() => RunJson(_list);
		*/

		static T RunCeras<T>(T obj) // Size = 76
		{
			T clone = default;

			int size = _ceras.Serialize(obj, ref _buffer);
			_ceras.Deserialize(ref clone, _buffer);

			return clone;
		}

		static T RunMessagePackCSharp<T>(T obj) // Size = 75
		{
			var data = MessagePackSerializer.Serialize(obj);
			var copy = MessagePackSerializer.Deserialize<T>(data);

			return copy;
		}

		static T RunJson<T>(T obj) // Size = 330
		{
			var data = JsonConvert.SerializeObject(obj);
			var clone = JsonConvert.DeserializeObject<T>(data);

			return clone;
		}

		static T RunProtobuf<T>(T obj) // Size = 85
		{
			_memStream.Position = 0;

			ProtoBuf.Serializer.SerializeWithLengthPrefix(_memStream, obj, PrefixStyle.Fixed32);
			_memStream.Position = 0;
			var clone = ProtoBuf.Serializer.DeserializeWithLengthPrefix<T>(_memStream, PrefixStyle.Fixed32);

			return clone;
		}

		static T RunWire<T>(T obj) // Size = 169
		{
			_memStream.Position = 0;
			_wire.Serialize(obj, _memStream);
			_memStream.Position = 0;
			var clone = _wire.Deserialize<T>(_memStream);
			_memStream.Position = 0;

			return clone;
		}

		static T RunNetSerializer<T>(T obj) // Size = 79
		{
			_memStream.Position = 0;
			_netSerializer.Serialize(_memStream, obj);
			_memStream.Position = 0;
			_netSerializer.Deserialize(_memStream, out object cloneObj);

			var clone = (T)cloneObj;

			return clone;
		}
	}

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

	public class CreateBenchmarks
	{
		List<Type> _createdTypes = new List<Type>();

		[GlobalSetup]
		public void Setup()
		{
			_createdTypes.Add(typeof(List<int>));
			_createdTypes.Add(typeof(List<bool>));
			_createdTypes.Add(typeof(List<DateTime>));
			_createdTypes.Add(typeof(MemoryStream));
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

	public class PersistRefs_Benchmarks
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

		static byte[] _buffer;
		static CerasSerializer _cerasNormal;
		static CerasSerializer _cerasNoRefs;


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

			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			config.KnownTypes.Add(typeof(Person));
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(Person[]));

			_cerasNormal = new CerasSerializer(config);


			config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			config.KnownTypes.Add(typeof(Person));
			config.KnownTypes.Add(typeof(List<>));
			config.KnownTypes.Add(typeof(Person[]));
			config.PreserveReferences = false;

			_cerasNoRefs = new CerasSerializer(config);

		}

		[BenchmarkCategory("Single"), Benchmark(Baseline = true)]
		public void MessagePackCSharp_Single()
		{
			RunMessagePackCSharp(_person);
		}


		[BenchmarkCategory("Single"), Benchmark()]
		public void Ceras_Normal()
		{
			Person clone = default;

			_cerasNormal.Serialize(_person, ref _buffer);
			_cerasNormal.Deserialize(ref clone, _buffer);
		}

		[BenchmarkCategory("Single"), Benchmark]
		public void Ceras_NoRefs()
		{
			Person clone = default;

			_cerasNoRefs.Serialize(_person, ref _buffer);
			_cerasNoRefs.Deserialize(ref clone, _buffer);
		}


		static T RunMessagePackCSharp<T>(T obj)
		{
			var data = MessagePackSerializer.Serialize(obj);
			var copy = MessagePackSerializer.Deserialize<T>(data);

			return copy;
		}
	}

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
	
	/*
	public class Feature_MreRefs_Benchmarks
	{
		public class Person : IEquatable<Person>
		{
			public virtual int Age { get; set; }

			public virtual string FirstName { get; set; }

			public virtual string LastName { get; set; }

			public virtual Sex Sex { get; set; }

			public virtual Person Parent1 { get; set; }

			public virtual Person Parent2 { get; set; }

			public virtual int[] LuckyNumbers { get; set; } = new int[0];

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
					   && Equals(Parent2, other.Parent2)
					   && LuckyNumbers.SequenceEqual(other.LuckyNumbers);
			}
		}

		public enum Sex : sbyte
		{
			Unknown, Male, Female,
		}

		Person _person;
		IList<Person> _list;

		static byte[] _buffer;
		static CerasSerializer _ceras;

		[Params(true, false)]
		public bool UseMreTest;

		[GlobalSetup]
		public void Setup()
		{
			//
			// Create example data
			var parent1 = new Person
			{
				Age = -901,
				FirstName = "Parent 1",
				LastName = "abc",
				Sex = Sex.Male,
			};
			var parent2 = new Person
			{
				Age = 7881964,
				FirstName = "Parent 2",
				LastName = "xyz",
				Sex = Sex.Female,
			};
			_person = new Person
			{
				Age = 5,
				FirstName = "Riki",
				LastName = "Example Person Object",
				Sex = Sex.Unknown,
				Parent1 = parent1,
				Parent2 = parent2,
			};

			_list = Enumerable.Range(25000, 100).Select(x => new Person { Age = x, FirstName = "a", LastName = "b", Sex = Sex.Female }).ToArray();

			
			//
			// Config Serializers
			CerasSerializer.UseRecentlyUsedFeature = UseMreTest;
			
			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			var knownTypes = new[] { typeof(Person), typeof(List<>), typeof(Person[]) };
			config.KnownTypes.AddRange(knownTypes);
			config.PreserveReferences = false;
			_ceras = new CerasSerializer(config);


			//
			// Run each serializer once to verify they work correctly!
			if (!Equals(RunCeras(_person), _person))
				ThrowError();
			
			void ThrowError() => throw new InvalidOperationException("Cannot continue with the benchmark because a serializer does not round-trip an object correctly. (Benchmark results will be wrong)");
		}

		[BenchmarkCategory("Single"), Benchmark(Baseline = true)]
		public void Ceras_Single() => RunCeras(_person);
		
		[BenchmarkCategory("List"), Benchmark(Baseline = true)]
		public void Ceras_List() => RunCeras(_list);
		
		static T RunCeras<T>(T obj) // Size = 76
		{
			T clone = default;

			int size = _ceras.Serialize(obj, ref _buffer);
			_ceras.Deserialize(ref clone, _buffer);

			return clone;
		}
	}
	*/

	// todo: compare if using constants in generated code eliminates the virtual dispatch

	// todo: add more real world benchmarks

	// todo: add Jil, and msgpack-cli 

}
