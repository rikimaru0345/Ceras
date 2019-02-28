using Xunit;
// ReSharper disable InconsistentNaming

namespace Ceras.Test
{
	using Helpers;
	using System;
	using System.Collections.Generic;

	public class Internals : TestBase
	{
		[Fact]
		public void MethodResolving()
		{
			// Check if the method resolver works correctly
			IAnimal animal = null;
			ICat cat = null;
			IDog dog = null;


			// - Non-generic MethodInfo, ConstructorInfo
			{
				var ctor = GetCtor(() => new Internals());
				TestDeepEquality(ctor);

				var mAA = GetMethod(() => HandleAnimal(animal, animal));
				TestDeepEquality(mAA);

				var mCA = GetMethod(() => HandleAnimal(cat, animal));
				TestDeepEquality(mCA);

				var mAC = GetMethod(() => HandleAnimal(animal, cat));
				TestDeepEquality(mAC);

				var mCC = GetMethod(() => HandleAnimal(cat, cat));
				TestDeepEquality(mCC);
			}


			// - Simple closed generic
			{
				var mt = GetMethod(() => HandleAnimal(dog, dog));
				TestDeepEquality(mt);
			}

			// - Exception on open method
			{
				var open = GetMethod(() => HandleAnimal(dog, dog)).GetGenericMethodDefinition();
				try
				{
					Clone(open);
					throw new Exception("expected exception not thrown");
				}
				catch
				{
				}
			}
		}


		void HandleAnimal(IAnimal anyA, IAnimal anyB) { }
		void HandleAnimal(ICat cat, IAnimal any) { }
		void HandleAnimal(IAnimal any, ICat cat) { }
		void HandleAnimal(ICat cat1, ICat cat2) { }
		void HandleAnimal<T>(T obj1, T obj2) where T : IDog { }

		interface IAnimal { }
		interface ICat : IAnimal { }
		interface IDog : IAnimal { }
		class Cat : ICat { }
		class Dog : IDog { }


		[Fact]
		public void IsBlittableChecks()
		{
			Assert.True(ReflectionHelper.IsBlittableType(typeof(bool)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(double)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(double)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(DayOfWeek))); // actual enum

			Assert.False(ReflectionHelper.IsBlittableType(typeof(string)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(Enum))); // enum class itself
			Assert.False(ReflectionHelper.IsBlittableType(typeof(byte*)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(int*)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(IntPtr)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(UIntPtr)));
		}

		[Fact]
		public void TypeMetaData()
		{
			// We need to ensure the type meta-data is correct.

			var ceras = new CerasSerializer();

			// true and false keywords are both highlighted in the same color, so this makes it easier to see :P
			const bool False = false;

			// isPrimitive means "is a serialization primitive", not primitive as in "primitive type" like int or something.
			var tests = new List<(bool isFramework, bool isPrimitive, bool isBlittable, Type testType)>();

			tests.Add((isFramework: true, isPrimitive: true, isBlittable: true, typeof(int)));
			tests.Add((isFramework: true, isPrimitive: true, isBlittable: true, typeof(bool)));
			tests.Add((isFramework: true, isPrimitive: true, isBlittable: true, typeof(char)));

			tests.Add((isFramework: true, isPrimitive: true, isBlittable: False, typeof(Type)));
			tests.Add((isFramework: true, isPrimitive: true, isBlittable: False, typeof(Type).GetType()));

			tests.Add((isFramework: true, isPrimitive: False, isBlittable: False, typeof(List<int>)));


			foreach (var test in tests)
			{
				var meta = ceras.GetTypeMetaData(test.testType);

				Assert.True(meta.IsFrameworkType == test.isFramework);
				Assert.True(meta.IsPrimitive == test.isPrimitive);
				Assert.True(ReflectionHelper.IsBlittableType(test.testType) == test.isBlittable);
			}
		}

		[Fact]
		public void FastCopy()
		{
#if NET45 || NET451 || NET452
			global::System.Console.WriteLine("Testing FastCopy on NET45.x");
#elif NET47 || NET471 || NET472
			global::System.Console.WriteLine("Testing FastCopy on NET47.x");
#elif NETSTANDARD2_0 || NETCOREAPP2_0 || NETCOREAPP2_1 || NETCOREAPP2_2
			global::System.Console.WriteLine("Testing FastCopy on NET STANDARD 2.0 / NETCOREAPP2_x");
#else

#error Unknown compiler version

#endif


			var sizes = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 100, 200, 300, 400, 510, 511, 512, 513, 514, 1000, 5000, 10 * 1000, 100 * 1000 };

			List<byte[]> sourceArrays = new List<byte[]>();
			foreach (var s in sizes)
			{
				var ar = new byte[s];
				rng.NextBytes(ar);
				sourceArrays.Add(ar);
			}

			List<byte[]> targetArrays = new List<byte[]>();
			foreach (var s in sizes)
			{
				var ar = new byte[s];
				rng.NextBytes(ar);
				targetArrays.Add(ar);
			}


			for (int arIndex = 0; arIndex < sourceArrays.Count; arIndex++)
			{
				var size = sizes[arIndex];
				var source = sourceArrays[arIndex];
				var target = targetArrays[arIndex];

				SerializerBinary.FastCopy(source, 0, target, 0, size);
				
				Assert.True(size == source.Length);
				Assert.True(size == target.Length);
				for (int i = 0; i < size; i++)
					Assert.True(source[i] == target[i]);
			}
		}

	}



	// todo: test public default ctor, private default ctor, and no parameterless ctor (and all construction modes)

	// todo: ignoreField, Caching, KeyValuePairs, Dictionaries, Typing, interfaces,
	// todo: RootObjects, reusing (overwriting) objects, arrays
	// todo: known types, hash checks for known types, assured mismatch when another type is added

}
