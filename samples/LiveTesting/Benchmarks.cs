using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;
using static Ceras.SerializerBinary;

namespace LiveTesting
{
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;
	using System.Runtime.Serialization;
	using BenchmarkDotNet.Configs;
	using Ceras;
	using MessagePack;
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
				if(dict.TryGetValue(t, out int value))
					sum += value;
			}

			for (var i = 0; i < _usedTypesB.Count; i++)
			{
				var t = _usedTypesB[i];
				if(dict.TryGetValue(t, out int value))
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
				if(dict.TryGetValue(t, out int value))
					sum += value;
			}

			for (var i = 0; i < _usedTypesB.Count; i++)
			{
				var t = _usedTypesB[i];
				if(dict.TryGetValue(t, out int value))
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
				if(dict.TryGetValue(t, out int value))
					sum += value;
			}

			for (var i = 0; i < _usedTypesB.Count; i++)
			{
				var t = _usedTypesB[i];
				if(dict.TryGetValue(t, out int value))
					sum += value;
			}
		}
	}

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
