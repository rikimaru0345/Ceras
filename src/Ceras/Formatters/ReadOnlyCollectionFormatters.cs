using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Formatters
{
    /// <summary>
    /// <c>ReadOnlyCollection&lt;T&gt;</c> cannot be refilled in place: it has no <c>Add</c> and no public
    /// parameterless constructor. It is written exactly like the built-in <c>List&lt;T&gt;</c> payload (a
    /// count with bias 1, then the elements) and rebuilt by wrapping a fresh <c>List&lt;T&gt;</c>, because
    /// that is the payload the robust schema records for a collection shape — writing anything else
    /// (an earlier version dumped its private fields) would leave the schema describing bytes that were
    /// never written, and the migration interpreter could not read the file back.
    /// </summary>
    public class ReadOnlyCollectionFormatter<T> : CollectionByListProxyFormatter<ReadOnlyCollection<T>, T>
    {
        protected override void Finalize(List<T> builder, ref ReadOnlyCollection<T> collection)
        {
            collection = new ReadOnlyCollection<T>(builder);
        }
    }

    /// <summary>
    /// <c>ReadOnlyDictionary&lt;K,V&gt;</c> likewise: its <c>Add</c> throws, so it is rebuilt by wrapping a
    /// fresh <c>Dictionary&lt;K,V&gt;</c>. Each entry is written as a <c>KeyValuePair&lt;K,V&gt;</c>, i.e. the key
    /// then the value, which is exactly the <c>Dictionary&lt;,&gt;</c> payload the schema describes for a
    /// dictionary shape.
    /// </summary>
    public class ReadOnlyDictionaryFormatter<TKey, TValue> : CollectionByListProxyFormatter<ReadOnlyDictionary<TKey, TValue>, KeyValuePair<TKey, TValue>>
    {
        protected override void Finalize(List<KeyValuePair<TKey, TValue>> builder, ref ReadOnlyDictionary<TKey, TValue> collection)
        {
            var dict = new Dictionary<TKey, TValue>(builder.Count);

            foreach (var kvp in builder)
                dict.Add(kvp.Key, kvp.Value);

            collection = new ReadOnlyDictionary<TKey, TValue>(dict);
        }
    }

}
