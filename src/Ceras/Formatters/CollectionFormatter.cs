namespace Ceras.Formatters
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Reflection;
    using Helpers;

    public sealed class ArrayFormatter<TItem> : FormatterBase<TItem[]>
    {
        readonly CerasSerializer _ceras;
        readonly IFormatter<TItem> _itemFormatter;
        readonly uint _maxCount;

        public ArrayFormatter(CerasSerializer serializer, uint maxCount)
        {
            _ceras = serializer;
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
            if (ar == null || ar.Length != length)
            {
                ar = new TItem[length];
                var proxy = _ceras.InstanceData.CurrentProxy;
                if (proxy != null && proxy.ProxyType.IsAssignableFrom(typeof(TItem[])))
                    proxy.SetObjectValue(ar);
            }

            var f = _itemFormatter;
            for (int i = 0; i < length; i++)
            {
                f.Deserialize(ref reader, ref ar[i]);
            }
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
    }

    public class CollectionFormatter<TCollection, TItem> : FormatterBase<TCollection>
    {
        readonly CerasSerializer _ceras;
        readonly IFormatter<TItem> _itemFormatter;
        readonly uint _maxSize;
        readonly Func<int, TCollection> _capacityConstructor;
        
        readonly Action<TCollection> _clearMethod;
        readonly Action<TCollection, TItem> _addMethod;
        readonly Func<TCollection, int> _getCountMethod;

        public CollectionFormatter(CerasSerializer serializer)
        {
            _ceras = serializer;
            _itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatter(typeof(TItem));
            _maxSize = serializer.Config.Advanced.SizeLimits.MaxCollectionSize;
            var collectionType = typeof(TCollection);
            
            CerasSerializer.AddFormatterConstructedType(collectionType);
            
            // Resolve capacity constructor
            if (collectionType.IsGenericType)
            {
                var def = collectionType.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(Dictionary<,>))
                {
                    var ctor = collectionType.GetConstructor(new[] { typeof(int) });
                    if (ctor != null)
                        _capacityConstructor = n => (TCollection)ctor.Invoke(new object[] { n });
                }
            }
            if (_capacityConstructor == null) _capacityConstructor = _ => (TCollection)Activator.CreateInstance(collectionType);

            // Resolve access methods
            if (typeof(ICollection<TItem>).IsAssignableFrom(collectionType))
            {
                _clearMethod = c => ((ICollection<TItem>)c).Clear();
                _addMethod = (c, i) => {
                    if (c == null) throw new Ceras.Exceptions.CerasException("_addMethod called with null collection!");
                    ((ICollection<TItem>)c).Add(i);
                };
                _getCountMethod = c => ((ICollection<TItem>)c).Count;
            }
            else
            {
                var clear = collectionType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var add = collectionType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(TItem) }, null);
                var count = collectionType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);

                if (clear != null) _clearMethod = (Action<TCollection>)Delegate.CreateDelegate(typeof(Action<TCollection>), clear);
                if (add != null) _addMethod = (Action<TCollection, TItem>)Delegate.CreateDelegate(typeof(Action<TCollection, TItem>), add);
                if (count != null) _getCountMethod = (Func<TCollection, int>)Delegate.CreateDelegate(typeof(Func<TCollection, int>), count.GetGetMethod());
            }
        }

        public override void Serialize(IBufferWriter<byte> writer, TCollection value)
        {
            if (value == null)
            {
                SerializerBinary.WriteUInt32Bias(writer, -1, 1);
                return;
            }

            int count = _getCountMethod(value);
            SerializerBinary.WriteUInt32Bias(writer, count, 1);
            
            var f = _itemFormatter;
            if (value is IEnumerable<TItem> enumerable)
            {
                foreach (var item in enumerable) f.Serialize(writer, item);
            }
            else
            {
                // Fallback for non-generic IEnumerable if needed, but TCollection usually implements it
                var e = (System.Collections.IEnumerable)value;
                foreach (var item in e) f.Serialize(writer, (TItem)item);
            }
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref TCollection value)
        {
            int itemCount = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (itemCount == -1)
            {
                value = default;
                return;
            }

            if (itemCount > _maxSize) throw new InvalidOperationException("Max size exceeded");
            
            if (value == null)
            {
                value = _capacityConstructor(itemCount);
                if (value == null) throw new Ceras.Exceptions.CerasException($"Failed to create an instance of collection type '{typeof(TCollection).FriendlyName()}' because the capacity constructor returned null.");

                var proxy = _ceras.InstanceData.CurrentProxy;
                if (proxy != null && proxy.ProxyType.IsAssignableFrom(typeof(TCollection)))
                    proxy.SetObjectValue(value);
            }
            else _clearMethod?.Invoke(value);

            var f = _itemFormatter;
            if (_addMethod == null) throw new Ceras.Exceptions.CerasException($"The collection type '{typeof(TCollection).FriendlyName()}' is not supported because no suitable 'Add' method was found.");

            for (int i = 0; i < itemCount; i++)
            {
                TItem item = default;
                f.Deserialize(ref reader, ref item);
                _addMethod(value, item);
            }
        }
    }
}
