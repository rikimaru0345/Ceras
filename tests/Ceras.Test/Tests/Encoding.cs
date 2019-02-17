namespace Ceras.Test
{
	using System.Collections.Generic;
	using Xunit;

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
}