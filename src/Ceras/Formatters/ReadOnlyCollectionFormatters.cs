using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
	// ReadOnlyCollection is handled differently. We can just overwrite its private fields.
	// For ReadOnlyDictionary that is not possible.

	public class ReadOnlyDictionaryFormatter<TKey, TValue> : CollectionByListProxyFormatter<ReadOnlyDictionary<TKey, TValue>, KeyValuePair<TKey, TValue>>
	{
		protected override void Finalize(List<KeyValuePair<TKey, TValue>> builder, ref ReadOnlyDictionary<TKey, TValue> collection)
		{
			var dict = new Dictionary<TKey, TValue>(builder.Count);
			
			foreach(var kvp in builder)
				dict.Add(kvp.Key, kvp.Value);

			collection = new ReadOnlyDictionary<TKey, TValue>(dict);
		}
	}

}
