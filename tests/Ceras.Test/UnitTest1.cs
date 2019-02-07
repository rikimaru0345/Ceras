using Xunit;

namespace Ceras.Test
{
	using System.Collections;
	using System.Collections.Generic;

	public class UnitTest1
	{
		CerasSerializer _s = new CerasSerializer();

		[Fact]
		public void Null()
		{
			byte[] data;

			data = _s.Serialize<object>(null);
			Assert.Equal(new byte[1] { 4 }, data); // Must be 4, because we're encoding a biased int marker

			data = _s.Serialize<string>(null);
			Assert.Equal(new byte[1] { 0 }, data); // Strings do not use the reference formatter (maybe adding that later as an option if any good use cases are presented). The string formatter uses its own bias-encoding for even more efficient packing, so 0 = null, 1 = string of length 0, 2 = string of length 1, ...

			data = _s.Serialize(0);
			Assert.Equal(new byte[1] { 0 }, data); // VarInt encoding should not use a bias
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
		    Assert.Equal(new byte[] { 6, 49, 50, 51, 52, 53}, data); // 1byte length, 5bytes content
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

		[Fact]
		public void TestNonGenericCollectionInheritingFromGenericInterface()
		{
			var dict = new MultiLangText();
			dict.Add("a", "b");

			var data = _s.Serialize(dict);
			var clone = _s.Deserialize<MultiLangText>(data);

			Assert.Single(clone);
			Assert.Equal(dict["a"], clone["a"]);
		}

		// todo: test public default ctor, private default ctor, and no parameterless ctor (and all construction modes)

		// todo: ignoreField, Caching, KeyValuePairs, Dictionaries, Typing, interfaces,
		// todo: RootObjects, reusing (overwriting) objects, arrays
		// todo: known types, hash checks for known types, assured mismatch when another type is added
	}

	class MultiLangText : IDictionary<string, string>
	{
		#region ReSharper AutoGen

		IDictionary<string, string> _dictionaryImplementation = new Dictionary<string, string>();

		public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
		{
			return _dictionaryImplementation.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable) _dictionaryImplementation).GetEnumerator();
		}

		public void Add(KeyValuePair<string, string> item)
		{
			_dictionaryImplementation.Add(item);
		}

		public void Clear()
		{
			_dictionaryImplementation.Clear();
		}

		public bool Contains(KeyValuePair<string, string> item)
		{
			return _dictionaryImplementation.Contains(item);
		}

		public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
		{
			_dictionaryImplementation.CopyTo(array, arrayIndex);
		}

		public bool Remove(KeyValuePair<string, string> item)
		{
			return _dictionaryImplementation.Remove(item);
		}

		public int Count => _dictionaryImplementation.Count;

		public bool IsReadOnly => _dictionaryImplementation.IsReadOnly;

		public bool ContainsKey(string key)
		{
			return _dictionaryImplementation.ContainsKey(key);
		}

		public void Add(string key, string value)
		{
			_dictionaryImplementation.Add(key, value);
		}

		public bool Remove(string key)
		{
			return _dictionaryImplementation.Remove(key);
		}

		public bool TryGetValue(string key, out string value)
		{
			return _dictionaryImplementation.TryGetValue(key, out value);
		}

		public string this[string key]
		{
			get => _dictionaryImplementation[key];
			set => _dictionaryImplementation[key] = value;
		}

		public ICollection<string> Keys => _dictionaryImplementation.Keys;

		public ICollection<string> Values => _dictionaryImplementation.Values;

		#endregion
	}
}
