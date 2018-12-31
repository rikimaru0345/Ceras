using Xunit;

namespace Ceras.Test
{
	using System.Collections.Generic;

	public class UnitTest1
	{
		CerasSerializer _s = new CerasSerializer();

		[Fact]
		public void Null()
		{
			byte[] data;

			data = _s.Serialize<object>(null);
			Assert.Single(data);

			data = _s.Serialize<string>(null);
			Assert.Equal(new byte[1] { 4 }, data);

			data = _s.Serialize(0);
			Assert.Equal(new byte[1] { 0 }, data);
		}

		[Fact]
		public void PrimitiveEncoding()
		{
			byte[] data;

			data = _s.Serialize(2147483647);
			Assert.Equal(5, data.Length);

			data = _s.Serialize(12);
			Assert.Single(data);

			data = _s.Serialize(-50);
			Assert.Single(data);

			data = _s.Serialize(600);
			Assert.Equal(2, data.Length);


			data = _s.Serialize<short>(1);
			Assert.Equal(2, data.Length);

			data = _s.Serialize<short>(short.MaxValue);
			Assert.Equal(2, data.Length);


			data = _s.Serialize("12345");
		    Assert.Equal(new byte[] {2, 6, 49, 50, 51, 52, 53}, data);// 1byte length, 5bytes content
		}

		[Fact]
		public void Roundtrip()
		{
			var data = new List<int> { 6, 32, 573, 246, 24, 2 };

			var serialized = _s.Serialize(data);
			var clone = _s.Deserialize<List<int>>(serialized);

			Assert.Equal(data.Count, clone.Count);
			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], clone[i]);


			var serializedAsObject = _s.Serialize<object>(data);
			var cloneObject = _s.Deserialize<object>(serializedAsObject);
			
			Assert.Equal(data.Count, ((List<int>)cloneObject).Count);

			for (int i = 0; i < data.Count; i++)
				Assert.Equal(data[i], ((List<int>)cloneObject)[i]);
		}

		// todo: ignoreField, Caching, KeyValuePairs, Dictionaries, Typing, interfaces,
		// todo: RootObjects, reusing (overwriting) objects, arrays
		// todo: known types, hash checks for known types, assured mismatch when another type is added
	}
}
