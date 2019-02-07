using System.Collections;
using System.Collections.Generic;
using Xunit;

namespace Ceras.Test
{
	public class GitHubIssues
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




		public abstract class Person_Issue25
		{
			public string Name = "default name";
			public List<Person_Issue25> Friends { get; private set; } = new List<Person_Issue25>();
			
			public Person_Issue25()
			{
				Friends = new List<Person_Issue25>();
			}
			
			protected void SetFriendsToNullInternal()
			{
				Friends = null;
			}
		}

		public class Adult : Person_Issue25
		{
			public byte[] Serialize() => new CerasSerializer().Serialize<object>(this);

			internal void SetFriendsToNull()
			{
				SetFriendsToNullInternal();
			}
		}
		
		[Fact]
		public void Issue25_PrivateSetterOfPropertyInBaseType()
		{
			var p = new Adult();
			p.Name = "1";
			p.Friends.Add(new Adult { Name = "2" });

			var config = new SerializerConfig();
			config.DefaultTargets = TargetMember.AllPublic;
			var ceras = new CerasSerializer(config);

			var data = ceras.Serialize<object>(p);

			var clone = new Adult();
			clone.SetFriendsToNull();
			object refObj = clone;
			ceras.Deserialize<object>(ref refObj, data);

			Assert.True(refObj != null);
			clone = refObj as Adult;
			Assert.True(clone.Friends != null);
			Assert.True(clone.Friends.Count == 1);
			Assert.True(clone.Friends[0].Name == "2");

		}

	}
}

