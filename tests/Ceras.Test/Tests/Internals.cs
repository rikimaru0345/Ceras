using Xunit;
// ReSharper disable InconsistentNaming

namespace Ceras.Test
{
	using Helpers;
	using System;

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
		public void TypeChecks()
		{
			Assert.True(ReflectionHelper.IsBlittableType(typeof(bool)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(double)));
			Assert.True(ReflectionHelper.IsBlittableType(typeof(double)));


			Assert.False(ReflectionHelper.IsBlittableType(typeof(string)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(DayOfWeek)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(Enum)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(byte*)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(int*)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(IntPtr)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(UIntPtr)));
			Assert.False(ReflectionHelper.IsBlittableType(typeof(void)));

		}

		[Fact]
		public void TypeMetaData()
		{
			// We need to ensure the type meta-data is correct.

			var ceras = new CerasSerializer();

			var m1 = ceras.GetTypeMetaData(typeof(int));
			Assert.True(m1.IsFrameworkType && m1.IsPrimitive);



		}
	}



	// todo: test public default ctor, private default ctor, and no parameterless ctor (and all construction modes)

	// todo: ignoreField, Caching, KeyValuePairs, Dictionaries, Typing, interfaces,
	// todo: RootObjects, reusing (overwriting) objects, arrays
	// todo: known types, hash checks for known types, assured mismatch when another type is added

}
