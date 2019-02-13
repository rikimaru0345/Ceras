using Xunit;

namespace Ceras.Test
{
	using System.Collections.Generic;

	public class Encoding
	{
		CerasSerializer _ceras;

		public Encoding()
		{
			_ceras = new CerasSerializer();
		}


		[Fact]
		public void Null()
		{
			byte[] data;

			data = _ceras.Serialize<object>(null);
			Assert.Equal(new byte[1] { 4 }, data); // Must be 4, because we're encoding a biased int marker

			data = _ceras.Serialize<string>(null);
			Assert.Equal(new byte[1] { 0 }, data); // Strings do not use the reference formatter (maybe adding that later as an option if any good use cases are presented). The string formatter uses its own bias-encoding for even more efficient packing, so 0 = null, 1 = string of length 0, 2 = string of length 1, ...

			data = _ceras.Serialize(0);
			Assert.Equal(new byte[1] { 0 }, data); // VarInt encoding should not use a bias
		}

		[Fact]
		public void PrimitiveEncoding()
		{
			byte[] data;

			data = _ceras.Serialize(2147483647);
			Assert.Equal(5, data.Length);

			data = _ceras.Serialize(12);
			Assert.Single(data);

			data = _ceras.Serialize(-50);
			Assert.Single(data);

			data = _ceras.Serialize(600);
			Assert.Equal(2, data.Length);


			data = _ceras.Serialize<short>(1);
			Assert.Equal(2, data.Length);

			data = _ceras.Serialize<short>(short.MaxValue);
			Assert.Equal(2, data.Length);


			data = _ceras.Serialize("12345");
			Assert.Equal(new byte[] { 6, 49, 50, 51, 52, 53 }, data); // 1byte length, 5bytes content
		}

		[Fact]
		public void Roundtrip()
		{
			var data = new List<int> { 6, 32, 573, 246, 24, 2 };

			var serialized = _ceras.Serialize(data);
			var clone = _ceras.Deserialize<List<int>>(serialized);

			Assert.Equal(data.Count, clone.Count);
			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], clone[i]);


			var serializedAsObject = _ceras.Serialize<object>(data);
			var cloneObject = _ceras.Deserialize<object>(serializedAsObject);

			Assert.Equal(data.Count, ((List<int>)cloneObject).Count);

			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], ((List<int>)cloneObject)[i]);
		}
	}

	public class Internals
	{
		CerasSerializer _ceras;

		public Internals()
		{
			_ceras = new CerasSerializer();
		}


		[Fact]
		public void MethodResolving()
		{
			// Check if the method resolver works correctly

			// - Can serialize simple MethodInfo, ConstructorInfo

			// - Serialize simple closed generic

			// - Serialize generic MethodInfo with constraints

			// - Serialize MethodInfo where generic arguments are hidden behind a non-generic type or interface. Forcing us to resolve the closed definition in the hierarchy.

			// - Also check if ref/out stuff is resolved correctly 

		}

		void HandleAnimal(IAnimal anyA, IAnimal anyB) { }
		void HandleAnimal(IAnimal any, ICat cat) { }
		void HandleAnimal(ICat cat, IAnimal any) { }
		void HandleAnimal(ICat cat1, ICat cat2) { }



		// todo: test public default ctor, private default ctor, and no parameterless ctor (and all construction modes)

		// todo: ignoreField, Caching, KeyValuePairs, Dictionaries, Typing, interfaces,
		// todo: RootObjects, reusing (overwriting) objects, arrays
		// todo: known types, hash checks for known types, assured mismatch when another type is added
	}


	interface IAnimal { }
	interface ICat : IAnimal { }
	interface IDog : IAnimal { }
	class Cat : ICat { }
	class Dog : IDog { }

}
