using System.Collections.Generic;

namespace Ceras.Formatters
{
	/// <summary>
	/// Helper base class for formatters.
	/// Uses a temporary (proxy) collection for deserialization, and then creates the actual collection from the proxy.
	/// Useful for example when dealing with readonly collections where you need to deserialize the elements into a plain old List first.
	/// </summary>
	public abstract class CollectionByProxyFormatter<TCollection, TItem, TProxyCollection> : IFormatter<TCollection>
			where TCollection : ICollection<TItem>
	{
		protected IFormatter<TItem> _itemFormatter;

		protected CollectionByProxyFormatter()
		{
			CerasSerializer.AddFormatterConstructedType(typeof(TCollection));
		}


		public void Serialize(ref byte[] buffer, ref int offset, TCollection value)
		{
			SerializerBinary.WriteUInt32(ref buffer, ref offset, (uint)value.Count);

			var itemFormatter = _itemFormatter;

			// Manual enumeration to prevent boxing

			// ReSharper disable once SuggestVarOrType_Elsewhere
			IEnumerator<TItem> e = value.GetEnumerator();
			try
			{
				while (e.MoveNext())
				{
					itemFormatter.Serialize(ref buffer, ref offset, e.Current);
				}
			}
			finally
			{
				e.Dispose();
			}
		}

		public void Deserialize(byte[] buffer, ref int offset, ref TCollection value)
		{
			int count = (int)SerializerBinary.ReadUInt32(buffer, ref offset);

			var builder = CreateProxy(count);

			for (int i = 0; i < count; i++)
			{
				TItem item = default;

				_itemFormatter.Deserialize(buffer, ref offset, ref item);

				AddToProxy(builder, item);
			}

			Finalize(builder, ref value);
		}

		protected abstract TProxyCollection CreateProxy(int knownSize);
		protected abstract void AddToProxy(TProxyCollection builder, TItem item);
		protected abstract void Finalize(TProxyCollection builder, ref TCollection collection);
	}

	/// <summary>
	/// Pre-made formatter base that just uses a List as the proxy collection
	/// </summary>
	public abstract class CollectionByListProxyFormatter<TCollection, TItem> : CollectionByProxyFormatter<TCollection, TItem, List<TItem>>
			where TCollection : ICollection<TItem>
	{
		protected sealed override List<TItem> CreateProxy(int knownSize) => new List<TItem>();
		protected sealed override void AddToProxy(List<TItem> builder, TItem item) => builder.Add(item);
	}
}
