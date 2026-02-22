namespace Ceras.Formatters
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Reflection;
    using Helpers;

    public sealed class ArrayFormatter<TItem> : FormatterBase<TItem[]>
    {
        readonly IFormatter<TItem> _itemFormatter;
        readonly uint _maxCount;

        public ArrayFormatter(CerasSerializer serializer, uint maxCount)
        {
            _maxCount = maxCount;
            _itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(typeof(TItem));
        }

        public override void Serialize(IBufferWriter<byte> writer, TItem[] ar)
        {
            if (ar == null)
            {
                SerializerBinary.WriteUInt32Bias(writer, -1, 1);
                return;
            }

            SerializerBinary.WriteUInt32Bias(writer, ar.Length, 1);
            var f = _itemFormatter;
            for (int i = 0; i < ar.Length; i++)
                f.Serialize(writer, ar[i]);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref TItem[] ar)
        {
            int length = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (length == -1)
            {
                ar = null;
                return;
            }

            if (length > _maxCount) throw new InvalidOperationException("Max count exceeded");
            if (ar == null || ar.Length != length) ar = new TItem[length];

            var f = _itemFormatter;
            for (int i = 0; i < length; i++)
                f.Deserialize(ref reader, ref ar[i]);
        }
    }

    public sealed class MultiDimensionalArrayFormatter<TItem> :
        FormatterBase<Array>,
        IFormatter<TItem[,]>,
        IFormatter<TItem[,,]>,
        IFormatter<TItem[,,,]>
    {
        readonly uint _maxCount;
        readonly IFormatter<TItem> _itemFormatter;

        public MultiDimensionalArrayFormatter(CerasSerializer serializer, uint maxCount)
        {
            _maxCount = maxCount;
            _itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(typeof(TItem));
        }

        public override void Serialize(IBufferWriter<byte> writer, Array baseAr)
        {
            int dimensions = baseAr.Rank;
            SerializerBinary.WriteUInt32(writer, (uint)dimensions);
            for (int d = 0; d < dimensions; d++)
                SerializerBinary.WriteUInt32(writer, (uint)baseAr.GetLength(d));

            var f = _itemFormatter;
            foreach (var item in baseAr)
                f.Serialize(writer, (TItem)item);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref Array baseAr)
        {
            int dimensions = (int)SerializerBinary.ReadUInt32(ref reader);
            var dimensionSizes = new int[dimensions];
            int count = 1;
            for (int d = 0; d < dimensions; d++)
            {
                dimensionSizes[d] = (int)SerializerBinary.ReadUInt32(ref reader);
                count *= dimensionSizes[d];
            }

            if (count > _maxCount) throw new InvalidOperationException("Max count exceeded");
            baseAr = Array.CreateInstance(typeof(TItem), dimensionSizes);

            var indices = new int[dimensions];
            ReadArrayEntry(ref reader, _itemFormatter, baseAr, indices, dimensionSizes, 0);
        }

        static void ReadArrayEntry(ref SequenceReader<byte> reader, IFormatter<TItem> formatter, Array array, int[] index, int[] dimensionSizes, int depth)
        {
            var max = dimensionSizes[depth];
            if (depth == dimensionSizes.Length - 1)
            {
                for (int i = 0; i < max; i++)
                {
                    index[depth] = i;
                    TItem val = default;
                    formatter.Deserialize(ref reader, ref val);
                    array.SetValue(val, index);
                }
            }
            else
            {
                for (int i = 0; i < max; i++)
                {
                    index[depth] = i;
                    ReadArrayEntry(ref reader, formatter, array, index, dimensionSizes, depth + 1);
                }
            }
        }

        public void Serialize(IBufferWriter<byte> writer, TItem[,] ar) => Serialize(writer, (Array)ar);
        public void Deserialize(ref SequenceReader<byte> reader, ref TItem[,] ar) { Array a = ar; Deserialize(ref reader, ref a); ar = (TItem[,])a; }
        public void Serialize(IBufferWriter<byte> writer, TItem[,,] ar) => Serialize(writer, (Array)ar);
        public void Deserialize(ref SequenceReader<byte> reader, ref TItem[,,] ar) { Array a = ar; Deserialize(ref reader, ref a); ar = (TItem[,,])a; }
        public void Serialize(IBufferWriter<byte> writer, TItem[,,,] ar) => Serialize(writer, (Array)ar);
        public void Deserialize(ref SequenceReader<byte> reader, ref TItem[,,,] ar) { Array a = ar; Deserialize(ref reader, ref a); ar = (TItem[,,,])a; }

        public void Serialize(IBufferWriter<byte> writer, TItem[,] value) => Serialize(writer, (Array)value);
        public void Serialize(IBufferWriter<byte> writer, TItem[,,] value) => Serialize(writer, (Array)value);
        public void Serialize(IBufferWriter<byte> writer, TItem[,,,] value) => Serialize(writer, (Array)value);
    }

    public class CollectionFormatter<TCollection, TItem> : FormatterBase<TCollection>
        where TCollection : ICollection<TItem>
    {
        readonly IFormatter<TItem> _itemFormatter;
        readonly uint _maxSize;
        readonly Func<int, TCollection> _capacityConstructor;

        public CollectionFormatter(CerasSerializer serializer)
        {
            _itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(typeof(TItem));
            _maxSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;
            var collectionType = typeof(TCollection);
            if (collectionType.IsGenericType)
            {
                var def = collectionType.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(Dictionary<,>))
                {
                    var ctor = collectionType.GetConstructor(new[] { typeof(int) });
                    if (ctor != null && serializer.Config.Advanced.AotMode == AotMode.None)
                        _capacityConstructor = n => (TCollection)ctor.Invoke(new object[] { n });
                }
            }
            if (_capacityConstructor == null) _capacityConstructor = _ => (TCollection)Activator.CreateInstance(collectionType);
        }

        public override void Serialize(IBufferWriter<byte> writer, TCollection value)
        {
            SerializerBinary.WriteUInt32(writer, (uint)value.Count);
            var f = _itemFormatter;
            foreach (var item in value) f.Serialize(writer, item);
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref TCollection value)
        {
            var itemCount = SerializerBinary.ReadUInt32(ref reader);
            if (itemCount > _maxSize) throw new InvalidOperationException("Max size exceeded");
            if (value == null) value = _capacityConstructor((int)itemCount);
            else value.Clear();

            var f = _itemFormatter;
            for (int i = 0; i < itemCount; i++)
            {
                TItem item = default;
                f.Deserialize(ref reader, ref item);
                value.Add(item);
            }
        }
    }
}
