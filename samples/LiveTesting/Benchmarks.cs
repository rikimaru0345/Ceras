using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;
using static Ceras.SerializerBinary;

namespace LiveTesting
{
	using System.Collections.Generic;
	using System.Reflection;

	[ClrJob]
	[RankColumn]
	public class Benchmarks
	{
		List<Type> _allTypes = new List<Type>();
		
		List<Type> _usedTypesA = new List<Type>();
		List<Type> _usedTypesB = new List<Type>();

		Dictionary<Type, int> _normalDict = new Dictionary<Type, int>();
		MyNewTypeDict<int> _newDict = new MyNewTypeDict<int>();

		[GlobalSetup]
		public void Setup()
		{
			_allTypes.AddRange(Assembly.GetExecutingAssembly().GetTypes());

			var rng = new Random(123456);
			
			for (int i = 0; i < 30; i++)
			{
				var index = rng.Next(0, _allTypes.Count);
				var t = _allTypes[index];
				_usedTypesA.Add(t);
			}

			for (int i = 0; i < 30; i++)
			{
				var index = rng.Next(0, _allTypes.Count);
				var t = _allTypes[index];
				_usedTypesB.Add(t);
			}
		}


		[Benchmark]
		public void Dictionary()
		{
		}

		public void NewDictionary()
		{

		}

		
	}

	class MyNewTypeDict<T>
	{
	}
}
