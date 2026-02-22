using System;
using System.Buffers;
using System.Collections.Generic;

namespace Ceras.Formatters
{
    public abstract class CollectionByProxyFormatter<TCollection, TItem, TProxyCollection> : IFormatter<TCollection>
            where TCollection : ICollection<TItem>
    {
        protected IFormatter<TItem> _itemFormatter;
        protected CollectionByProxyFormatter() { CerasSerializer.AddFormatterConstructedType(typeof(TCollection)); }

        public void Serialize(IBufferWriter<byte> writer, TCollection value)
        {
            SerializerBinary.WriteUInt32(writer, (uint)value.Count);
            var itemFormatter = _itemFormatter;
            foreach (var item in value) itemFormatter.Serialize(writer, item);
        }

        public void Deserialize(ref SequenceReader<byte> reader, ref TCollection value)
        {
            int count = (int)SerializerBinary.ReadUInt32(ref reader);
            var builder = CreateProxy(count);
            for (int i = 0; i < count; i++)
            {
                TItem item = default;
                _itemFormatter.Deserialize(ref reader, ref item);
                AddToProxy(builder, item);
            }
            Finalize(builder, ref value);
        }

        protected abstract TProxyCollection CreateProxy(int knownSize);
        protected abstract void AddToProxy(TProxyCollection builder, TItem item);
        protected abstract void Finalize(TProxyCollection builder, ref TCollection collection);
    }

    public abstract class CollectionByListProxyFormatter<TCollection, TItem> : CollectionByProxyFormatter<TCollection, TItem, List<TItem>>
            where TCollection : ICollection<TItem>
    {
        protected sealed override List<TItem> CreateProxy(int knownSize) => new List<TItem>();
        protected sealed override void AddToProxy(List<TItem> builder, TItem item) => builder.Add(item);
    }
}
