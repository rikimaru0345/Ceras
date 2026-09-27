using System;
using System.Buffers;
using System.Collections.Generic;

namespace Ceras.Formatters
{
    public abstract class CollectionByProxyFormatter<TCollection, TItem, TProxyCollection> : FormatterBase<TCollection>
            where TCollection : ICollection<TItem>
    {
        protected IFormatter<TItem> _itemFormatter;
        protected SizeLimits _sizeLimits;

        protected CollectionByProxyFormatter() { CerasSerializer.AddFormatterConstructedType(typeof(TCollection)); }

        public override void Serialize(IBufferWriter<byte> writer, TCollection value)
        {
            // Written exactly like a built-in collection: a count with bias 1 (-1 meaning null) followed by
            // the elements. The robust schema describes every collection shape with that layout, so a
            // shape that has to be built through a proxy (no public parameterless constructor, no Add)
            // must write it too — otherwise the schema would describe bytes this formatter never writes
            // and the migration interpreter could not read the file back.
            if (value == null)
            {
                SerializerBinary.WriteUInt32Bias(writer, -1, 1);
                return;
            }

            SerializerBinary.WriteUInt32Bias(writer, value.Count, 1);
            var itemFormatter = _itemFormatter;
            foreach (var item in value) itemFormatter.Serialize(writer, item);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref TCollection value)
        {
            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1)
            {
                value = default;
                return;
            }

            uint maxSize = _sizeLimits?.MaxCollectionSize ?? uint.MaxValue;
            if (count < 0 || (uint)count > maxSize)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt save: a collection declares {count} elements, outside the configured maximum of {maxSize}.");

            var builder = CreateProxy(count);
            for (int i = 0; i < count; i++)
            {
                TItem? item = default;
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
