namespace Ceras.ImmutableCollections
{
	using System.Collections.Generic;
	using System.Collections.Immutable;
	using System.Linq;
	using Formatters;

	public sealed class ImmutableArrayFormatter<TItem> : CollectionByProxyFormatter<ImmutableArray<TItem>, TItem, ImmutableArray<TItem>.Builder>
	{
		protected override ImmutableArray<TItem>.Builder CreateProxy(int knownSize)
			=> ImmutableArray.CreateBuilder<TItem>(knownSize);

		protected override void AddToProxy(ImmutableArray<TItem>.Builder builder, TItem item)
			=> builder.Add(item);

		protected override void Finalize(ImmutableArray<TItem>.Builder builder, ref ImmutableArray<TItem> collection)
			=> collection = builder.ToImmutable();
	}

	public sealed class ImmutableDictionaryFormatter<TKey, TValue> :
			CollectionByProxyFormatter<ImmutableDictionary<TKey, TValue>, KeyValuePair<TKey, TValue>, ImmutableDictionary<TKey, TValue>.Builder>
	{
		protected override ImmutableDictionary<TKey, TValue>.Builder CreateProxy(int knownSize)
			=> ImmutableDictionary.CreateBuilder<TKey, TValue>();

		protected override void AddToProxy(ImmutableDictionary<TKey, TValue>.Builder builder, KeyValuePair<TKey, TValue> item)
			=> builder.Add(item);

		protected override void Finalize(ImmutableDictionary<TKey, TValue>.Builder builder, ref ImmutableDictionary<TKey, TValue> collection)
			=> collection = builder.ToImmutable();
	}

	public sealed class ImmutableHashSetFormatter<TItem> : CollectionByProxyFormatter<ImmutableHashSet<TItem>, TItem, ImmutableHashSet<TItem>.Builder>
	{
		protected override ImmutableHashSet<TItem>.Builder CreateProxy(int knownSize)
			=> ImmutableHashSet.CreateBuilder<TItem>();

		protected override void AddToProxy(ImmutableHashSet<TItem>.Builder builder, TItem item)
			=> builder.Add(item);

		protected override void Finalize(ImmutableHashSet<TItem>.Builder builder, ref ImmutableHashSet<TItem> collection)
			=> collection = builder.ToImmutable();
	}

	public sealed class ImmutableListFormatter<TItem> : CollectionByProxyFormatter<ImmutableList<TItem>, TItem, ImmutableList<TItem>.Builder>
	{
		protected override ImmutableList<TItem>.Builder CreateProxy(int knownSize)
			=> ImmutableList.CreateBuilder<TItem>();

		protected override void AddToProxy(ImmutableList<TItem>.Builder builder, TItem item)
			=> builder.Add(item);

		protected override void Finalize(ImmutableList<TItem>.Builder builder, ref ImmutableList<TItem> collection)
			=> collection = builder.ToImmutable();
	}

	public sealed class ImmutableQueueFormatter<TItem> : IFormatter<ImmutableQueue<TItem>>
	{
		IFormatter<TItem[]> _itemsFormatter;

		public ImmutableQueueFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(ImmutableQueue<TItem>));
		}

		public void Serialize(ref byte[] buffer, ref int offset, ImmutableQueue<TItem> value)
		{
			var array = value.ToArray();
			_itemsFormatter.Serialize(ref buffer, ref offset, array);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref ImmutableQueue<TItem> value)
		{
			TItem[] array = null;
			_itemsFormatter.Deserialize(buffer, ref offset, ref array);
			value = ImmutableQueue.CreateRange(array);
		}
	}

	public sealed class ImmutableSortedDictionaryFormatter<TKey, TValue> :
			CollectionByProxyFormatter<ImmutableSortedDictionary<TKey, TValue>, KeyValuePair<TKey, TValue>, ImmutableSortedDictionary<TKey, TValue>.Builder>
	{
		protected override ImmutableSortedDictionary<TKey, TValue>.Builder CreateProxy(int knownSize)
			=> ImmutableSortedDictionary.CreateBuilder<TKey, TValue>();

		protected override void AddToProxy(ImmutableSortedDictionary<TKey, TValue>.Builder builder, KeyValuePair<TKey, TValue> item)
			=> builder.Add(item);

		protected override void Finalize(ImmutableSortedDictionary<TKey, TValue>.Builder builder, ref ImmutableSortedDictionary<TKey, TValue> collection)
			=> collection = builder.ToImmutable();
	}

	public sealed class ImmutableSortedSetFormatter<TItem> : CollectionByProxyFormatter<ImmutableSortedSet<TItem>, TItem, ImmutableSortedSet<TItem>.Builder>
	{
		protected override ImmutableSortedSet<TItem>.Builder CreateProxy(int knownSize)
			=> ImmutableSortedSet.CreateBuilder<TItem>();

		protected override void AddToProxy(ImmutableSortedSet<TItem>.Builder builder, TItem item)
			=> builder.Add(item);

		protected override void Finalize(ImmutableSortedSet<TItem>.Builder builder, ref ImmutableSortedSet<TItem> collection)
			=> collection = builder.ToImmutable();
	}

	public sealed class ImmutableStackFormatter<TItem> : IFormatter<ImmutableStack<TItem>>
	{
		IFormatter<TItem[]> _itemsFormatter;

		public ImmutableStackFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(ImmutableStack<TItem>));
		}
		
		public void Serialize(ref byte[] buffer, ref int offset, ImmutableStack<TItem> value)
		{
			var array = value.ToArray();
			_itemsFormatter.Serialize(ref buffer, ref offset, array);
		}

		public void Deserialize(byte[] buffer, ref int offset, ref ImmutableStack<TItem> value)
		{
			TItem[] array = null;
			_itemsFormatter.Deserialize(buffer, ref offset, ref array);
			value = ImmutableStack.CreateRange(array.Reverse());
		}
	}

}
