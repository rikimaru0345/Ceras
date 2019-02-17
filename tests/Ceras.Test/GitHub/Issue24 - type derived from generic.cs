using System.Collections;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
	// Error when a type is not generic, but derives from a generic collection
	public class Issue24
	{
		
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

		[Fact]
		public void Issue24_TestNonGenericCollectionInheritingFromGenericInterface()
		{
			CerasSerializer s = new CerasSerializer();

			var dict = new MultiLangText();
			dict.Add("a", "b");

			var data = s.Serialize(dict);
			var clone = s.Deserialize<MultiLangText>(data);

			Assert.Single(clone);
			Assert.Equal(dict["a"], clone["a"]);
		}
	}
}

