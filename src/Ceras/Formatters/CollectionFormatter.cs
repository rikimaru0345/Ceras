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

        internal ArrayFormatter(CerasSerializer serializer, uint maxCount)
        {
            _ceras = serializer;
            _maxCount = maxCount;
            _itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatterRequired(typeof(TItem));
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
                ar = null!;
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
        /// <summary>The CLR's own array rank limit; a declared rank above it cannot be allocated.</summary>
        const int MaxRank = 32;

        readonly uint _maxCount;
        readonly IFormatter<TItem> _itemFormatter;

        internal MultiDimensionalArrayFormatter(CerasSerializer serializer, uint maxCount)
        {
            _maxCount = maxCount;
            _itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatterRequired(typeof(TItem));
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
            uint dimensionsU = SerializerBinary.ReadUInt32(ref reader);
            if (dimensionsU == 0 || dimensionsU > MaxRank)
                throw new Ceras.Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: a multidimensional array declares {dimensionsU} dimensions (allowed: 1-{MaxRank}).");

            int dimensions = (int)dimensionsU;
            var dimensionSizes = new int[dimensions];
            int count = 1;
            for (int d = 0; d < dimensions; d++)
            {
                uint sizeU = SerializerBinary.ReadUInt32(ref reader);
                if (sizeU > int.MaxValue)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt data: dimension {d} of a multidimensional array declares {sizeU} elements.");

                // Checked: the old `count *= size` overflowed *before* the limit was compared, so a
                // crafted pair of dimensions could wrap to a small positive number and pass
                // SizeLimits.MaxArraySize, then drive a huge allocation here and in Array.CreateInstance.
                try
                {
                    count = checked(count * (int)sizeU);
                }
                catch (OverflowException e)
                {
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        "Corrupt data: the element count of a multidimensional array overflows a 32-bit int.", e);
                }

                if (count > _maxCount)
                    throw new Ceras.Exceptions.CerasDataCorruptionException(
                        $"Corrupt data: a multidimensional array of {count} elements exceeds the configured limit of {_maxCount}.");

                dimensionSizes[d] = (int)sizeU;
            }

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
                    // Seeded with default! like every other element read in this file: the out parameter
                    // of the item formatter is not nullable, and a null element is a value it handles.
                    TItem val = default!;
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
        
        // All three are resolved below and can legitimately be absent: a type that is not an
        // ICollection<TItem> is asked for a public parameterless Clear, a one-argument Add and a Count
        // property, and any of the three lookups can come back empty. The call sites below check for
        // that (a collection without an Add or a Count cannot be serialized at all) or fall back to
        // constructing a fresh instance when there is no Clear.
        //
        // The reflection below is the fallback for a directly constructed instance (the ctor is internal,
        // so only a friend assembly does that): the resolver always closes this over an ICollection<TItem>.
        readonly Action<TCollection>? _clearMethod;
        readonly Action<TCollection, TItem>? _addMethod;
        readonly Func<TCollection, int>? _getCountMethod;

        internal CollectionFormatter(CerasSerializer serializer)
        {
            _ceras = serializer;
            _itemFormatter = (IFormatter<TItem>)serializer.GetReferenceFormatterRequired(typeof(TItem));
            _maxSize = serializer.Config.SizeLimits.MaxCollectionSize;
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
            // A type without a public parameterless constructor throws MissingMethodException from
            // Activator instead of returning null, so the cast below is non-null by construction.
            if (_capacityConstructor == null) _capacityConstructor = _ => (TCollection)Activator.CreateInstance(collectionType)!;

            // Resolve access methods
            if (typeof(ICollection<TItem>).IsAssignableFrom(collectionType))
            {
                _clearMethod = c => ((ICollection<TItem>)c!).Clear();
                _addMethod = (c, i) => {
                    if (c == null) throw new Ceras.Exceptions.CerasException("_addMethod called with null collection!");
                    ((ICollection<TItem>)c).Add(i);
                };
                _getCountMethod = c => ((ICollection<TItem>)c!).Count;
            }
            else
            {
                var clear = collectionType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var add = collectionType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(TItem) }, null);
                var count = collectionType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);

                if (clear != null) _clearMethod = (Action<TCollection>)Delegate.CreateDelegate(typeof(Action<TCollection>), clear);
                if (add != null) _addMethod = (Action<TCollection, TItem>)Delegate.CreateDelegate(typeof(Action<TCollection, TItem>), add);
                // A property without a getter cannot answer "how many elements are there", so the
                // getter - not the property - is what has to be found before a delegate is built.
                var getCount = count?.GetGetMethod();
                if (getCount != null) _getCountMethod = (Func<TCollection, int>)Delegate.CreateDelegate(typeof(Func<TCollection, int>), getCount);
            }
        }

        public override void Serialize(IBufferWriter<byte> writer, TCollection value)
        {
            if (value == null)
            {
                SerializerBinary.WriteUInt32Bias(writer, -1, 1);
                return;
            }

            // The payload starts with the element count, so a collection whose count cannot be read
            // cannot be serialized at all. That is a configuration problem with the type, so it is
            // reported as such instead of as a NullReferenceException from the unresolved delegate.
            var getCount = _getCountMethod ?? throw new Ceras.Exceptions.CerasException(
                $"The collection type '{typeof(TCollection).FriendlyName()}' is not supported because no 'Count' property was found.");
            int count = getCount(value);
            SerializerBinary.WriteUInt32Bias(writer, count, 1);
            
            var f = _itemFormatter;

            // Fast paths that read the elements in place. Both write exactly the bytes the
            // IEnumerable<TItem> loop below writes, in the same order, so the wire format is
            // unchanged - they only remove the enumerator.
            //
            // `IEnumerable<TItem>.GetEnumerator()` returns a reference type, so `foreach` over the
            // interface boxes one enumerator per collection, even when the collection is a List<T>
            // with a perfectly good struct enumerator. On a graph that is mostly small lists that
            // is one throwaway allocation per list written. CollectionsMarshal.AsSpan exposes the
            // same storage as a Span<T> and the indexed loop below cannot allocate.
            //
            // A `List<TItem>` is matched by runtime type, so a List reached through an interface-typed
            // member (IList<T>/ICollection<T>/IEnumerable<T>) takes this path too, which is the
            // common case for collections - and is not reachable by special-casing the *formatter*,
            // since the formatter is chosen from the member's declared type.
            if (value is List<TItem> list)
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
                for (int i = 0; i < span.Length; i++)
                    f.Serialize(writer, span[i]);
                return;
            }

            if (value is TItem[] array)
            {
                for (int i = 0; i < array.Length; i++)
                    f.Serialize(writer, array[i]);
                return;
            }

            if (value is IEnumerable<TItem> enumerable)
            {
                foreach (var item in enumerable) f.Serialize(writer, item);
            }
            else
            {
                // TItem came from an ICollection<>/IDictionary<> interface, but the concrete collection's
                // own enumeration element differs (e.g. MySet : HashSet<long> : IEnumerable<long> with an
                // explicit IEnumerable<object>). Fall back to the non-generic enumerator and coerce
                // as the built-in collection formatter does.
                var e = (System.Collections.IEnumerable)value;
                if (typeof(TItem).IsAssignableFrom(typeof(object)))
                {
                    foreach (var item in e) f.Serialize(writer, (TItem)item);
                }
                else
                {
                    foreach (var item in e)
                        if (item is TItem typed)
                            f.Serialize(writer, typed);
                }
            }
        }

        public override void Deserialize(ref SequenceReader<byte> reader, ref TCollection value)
        {
            int itemCount = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (itemCount == -1)
            {
                // A null collection is a legal payload value (Serialize writes -1 for it), so the
                // out parameter is handed null even though the contract declares it non-null.
                value = default!;
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
            else if (_clearMethod != null)
            {
                _clearMethod(value);
            }
            else
            {
                // No usable clear operation: never silently append. Fall back to fresh construction
                // (which is always available for a concrete collection) and re-register the identity.
                value = _capacityConstructor(itemCount);
                if (value == null)
                    throw new Ceras.Exceptions.CerasException(
                        $"The collection type '{typeof(TCollection).FriendlyName()}' has no usable Clear method and could not be constructed fresh; Ceras cannot reuse it in place.");

                var proxy = _ceras.InstanceData.CurrentProxy;
                if (proxy != null && proxy.ProxyType.IsAssignableFrom(typeof(TCollection)))
                    proxy.SetObjectValue(value);
            }

            var f = _itemFormatter;
            var add = _addMethod ?? throw new Ceras.Exceptions.CerasException($"The collection type '{typeof(TCollection).FriendlyName()}' is not supported because no suitable 'Add' method was found.");

            for (int i = 0; i < itemCount; i++)
            {
                TItem item = default!;
                f.Deserialize(ref reader, ref item);
                add(value, item);
            }
        }
    }
}
