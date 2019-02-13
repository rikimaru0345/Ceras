namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;
	
	// todo: special formatter: "BlitFormatter<T>" and "BlitFormatter<T[]>" to replace byte[] and int[] formatters
	// todo: special formatter: CollectionFormatter that reads elements into a list, then calls a ctor with list or array with parameters named "IList<T> list" "array" or "IEnumerable<T> collection". Probably best to manually select what types are affected and what ctor to use...
	// todo: special formatter: "CapacityCollectionFormatter" that uses "capacity" ctor for enabled types
	// todo: special handling for things that only implement ICollection (non generic) like Stack and Queue
	// todo: check IsReadonly for IDictionary and IList



	sealed class ListFormatter<TItem> : IFormatter<List<TItem>>
	{
		IFormatter<TItem> _itemFormatter;
		readonly uint _maxSize;

		public ListFormatter(CerasSerializer serializer)
		{
			var itemType = typeof(TItem);
			_itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(itemType);

			// We'll handle instantiation ourselves in order to call the capacity ctor
			CerasSerializer.AddFormatterConstructedType(typeof(List<TItem>));

			_maxSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;
		}

		public void Serialize(ref byte[] buffer, ref int offset, List<TItem> value)
		{
			// Write how many items do we have
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)value.Count);

			// Write each item
			var f = _itemFormatter;
			for (var i = 0; i < value.Count; i++)
			{
				var item = value[i];
				f.Serialize(ref buffer, ref offset, item);
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref List<TItem> value)
		{
			// How many items?
			var itemCount = SerializerBinary.ReadUInt32(buffer, ref offset);

			if (itemCount > _maxSize)
				throw new InvalidOperationException($"The data contains a '{typeof(TItem)}'-List with '{itemCount}' elements, which exceeds the allowed limit of '{_maxSize}'");

			if (value == null)
				value = new List<TItem>((int)itemCount);
			else
				value.Clear();

			var f = _itemFormatter;

			for (int i = 0; i < itemCount; i++)
			{
				TItem item = default;
				f.Deserialize(buffer, ref offset, ref item);
				value.Add(item);
			}
		}
	}

	sealed class DictionaryFormatter<TKey, TValue> : IFormatter<Dictionary<TKey, TValue>>
	{
		IFormatter<KeyValuePair<TKey, TValue>> _itemFormatter;
		readonly uint _maxSize;

		public DictionaryFormatter(CerasSerializer serializer)
		{
			var itemType = typeof(KeyValuePair<TKey, TValue>);
			_itemFormatter = (IFormatter<KeyValuePair<TKey, TValue>>)serializer.GetReferenceFormatter(itemType);

			// We'll handle instantiation ourselves in order to call the capacity ctor
			CerasSerializer.AddFormatterConstructedType(typeof(Dictionary<TKey, TValue>));

			_maxSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;
		}

		public void Serialize(ref byte[] buffer, ref int offset, Dictionary<TKey, TValue> value)
		{
			// Write how many items do we have
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)value.Count);

			// Write each item
			var f = _itemFormatter;
			foreach (var kvp in value)
				f.Serialize(ref buffer, ref offset, kvp);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref Dictionary<TKey, TValue> value)
		{
			// How many items?
			var itemCount = SerializerBinary.ReadUInt32(buffer, ref offset);

			if (itemCount > _maxSize)
				throw new InvalidOperationException($"The data contains a '{typeof(TKey)} {typeof(TValue)}'-Dictionary with '{itemCount}' elements, which exceeds the allowed limit of '{_maxSize}'");

			if (value == null)
				value = new Dictionary<TKey, TValue>((int)itemCount);
			else
				value.Clear();

			var f = _itemFormatter;

			for (int i = 0; i < itemCount; i++)
			{
				KeyValuePair<TKey, TValue> item = default;
				f.Deserialize(buffer, ref offset, ref item);
				value.Add(item.Key, item.Value);
			}
		}
	}
}