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
			var itemType = typeof(TItem);
			if (itemType.IsValueType)
				_itemFormatter = (IFormatter<TItem>) serializer.GetSpecificFormatter(itemType);
			else
				_itemFormatter = (IFormatter<TItem>) serializer.GetGenericFormatter(itemType);
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

		public CollectionFormatter(CerasSerializer serializer)
		{
			// What do we use as item formatter?
			// - specific formatter (only writes data directly)
			//   use when the type is known
			// - generic formatter (writes type if needed)
			//   use when types can be polymorphic

			var itemType = typeof(TItem);
			if (itemType.IsValueType)
				_itemFormatter = (IFormatter<TItem>) serializer.GetSpecificFormatter(itemType);
			else
				_itemFormatter = (IFormatter<TItem>) serializer.GetGenericFormatter(itemType);
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