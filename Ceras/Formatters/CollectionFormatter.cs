namespace Ceras.Formatters
{
	using System;
	using System.Collections.Generic;

	// todo: respect options for collection handling in both serializers

	// todo: do we want to do object caching for arrays and collections as well!?
	// todo: ..how realistic is it that two objects reference the same collection?

	public class ArrayFormatter<TItem> : IFormatter<TItem[]>
	{
		IFormatter<TItem> _itemFormatter;

		public ArrayFormatter(CerasSerializer serializer)
		{
			_itemFormatter = (IFormatter<TItem>)serializer.GetFormatter(typeof(TItem));
		}

		public void Serialize(ref byte[] buffer, ref int offset, TItem[] ar)
		{
			if (ar == null)
			{
				SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, -1, 1);
				return;
			}

			SerializerBinary.WriteUInt32Bias(ref buffer, ref offset, ar.Length, 1);

			for (int i = 0; i < ar.Length; i++)
				_itemFormatter.Serialize(ref buffer, ref offset, ar[i]);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref TItem[] ar)
		{
			int length = SerializerBinary.ReadUInt32Bias(buffer, ref offset, 1);

			if (length == -1)
			{
				ar = null;
				return;
			}

			if (ar == null || ar.Length != length)
				ar = new TItem[length];

			for (int i = 0; i < length; i++)
				_itemFormatter.Deserialize(buffer, ref offset, ref ar[i]);
		}
	}

	public class CollectionFormatter<TCollection, TItem> : IFormatter<TCollection> where TCollection : ICollection<TItem>
	{
		IFormatter<TItem> _itemFormatter;

		// Possible scenarios:
		// We have an existing collection, should we new() a new one anyway?
		// The existing collection is not the type that the buffer says it should be, should we use what's already there? Or new() the right collection?
		// There is no data in the buffer, do we null the existing data? Or clear it? Or do nothing? 
		// Should we ignore the data in the buffer if the existing collection is not null? Or what if it is null, then skip the deserialization of the collection and leave it as null?

		// There are many ways this can go... 


		public CollectionFormatter(CerasSerializer serializer)
		{
			_itemFormatter = (IFormatter<TItem>)serializer.GetFormatter(typeof(TItem));
		}

		public void Serialize(ref byte[] buffer, ref int offset, TCollection value)
		{
			// Write how many items do we have
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)value.Count);
			
			// Write each item
			foreach (var item in value)
				_itemFormatter.Serialize(ref buffer, ref offset, item);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref TCollection value)
		{
			// How many items?
			var itemCount = SerializerBinary.ReadUInt32(buffer, ref offset);

			value.Clear();

			for (int i = 0; i < itemCount; i++)
			{
				TItem item = default(TItem);
				_itemFormatter.Deserialize(buffer, ref offset, ref item);
				value.Add(item);
			}
		}
	}
}