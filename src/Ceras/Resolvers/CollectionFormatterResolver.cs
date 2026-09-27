namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;

    /// <summary>
    /// Creates formatters for array-types and all types that implement ICollection&lt;&gt;
    /// </summary>
    public class CollectionFormatterResolver : IFormatterResolver
    {
        readonly CerasSerializer _ceras;
        Dictionary<Type, IFormatter> _formatterInstances = new Dictionary<Type, IFormatter>();

        internal CollectionFormatterResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
        }

        public IFormatter GetFormatter(Type type)
        {
            //
            // Do we already have an array or collection formatter?
            //
            IFormatter formatter;
            if (_formatterInstances.TryGetValue(type, out formatter))
                return formatter;

            //
            // Array?
            //
            if (type.IsArray)
            {
                var itemType = type.GetElementType();
                int rank = type.GetArrayRank();
                var maxCount = itemType == typeof(byte)
                        ? _ceras.Config.SizeLimits.MaxByteArraySize
                        : _ceras.Config.SizeLimits.MaxArraySize;


                if (rank == 1 && (_ceras.Config is CerasRawConfig rawCfg) && rawCfg.UseReinterpretFormatter && ReflectionHelper.IsBlittableType(itemType))
                {
                    // ReinterpretArrayFormatter<> (reinterpret)
                    var formatterType = typeof(ReinterpretArrayFormatter<>).MakeGenericType(itemType);
                    formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType, maxCount);
                }
                else
                {
                    if (rank == 1)
                    {
                        // ArrayFormatter<>
                        var formatterType = typeof(ArrayFormatter<>).MakeGenericType(itemType);
                        formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType, _ceras, maxCount);
                    }
                    else if (rank <= 6)
                    {
                        // MultiDimensionalArrayFormatter
                        var formatterType = typeof(MultiDimensionalArrayFormatter<>).MakeGenericType(itemType);
                        formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType, _ceras, maxCount);
                    }
                    else
                    {
                        ArrayRankTooHigh(rank);
                    }
                }

                _formatterInstances[type] = formatter;
                return formatter;
            }


            //
            // Special collection? (Stack, Queue, ReadOnlyCollection, ReadOnlyDictionary)
            //
            // The dedicated Queue/Stack formatters construct the built-in type directly, so they are only
            // valid for the exact built-in type. A subclass (e.g. MyQueue : Queue<T>) is handled by the
            // general CollectionFormatter below, which constructs the subclass.
            //
            var closedStack = ReflectionHelper.FindClosedType(type, typeof(Stack<>));
            if (closedStack != null && type == closedStack)
            {
                var formatterType = typeof(StackFormatter<>).MakeGenericType(closedStack.GetGenericArguments());
                formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType);
                _formatterInstances[type] = formatter;
                return formatter;
            }

            var closedQueue = ReflectionHelper.FindClosedType(type, typeof(Queue<>));
            if (closedQueue != null && type == closedQueue)
            {
                var formatterType = typeof(QueueFormatter<>).MakeGenericType(closedQueue.GetGenericArguments());
                formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType);
                _formatterInstances[type] = formatter;
                return formatter;
            }

            // ReadOnlyCollection<T> has no public parameterless constructor and no Add, so the general
            // CollectionFormatter cannot construct or refill it. Its dedicated formatter writes the same
            // List<T> payload (count + elements) through a proxy List<T>, which is what the robust schema
            // records for a collection shape.
            var closedROC = ReflectionHelper.FindClosedType(type, typeof(ReadOnlyCollection<>));
            if (closedROC != null)
            {
                var formatterType = typeof(ReadOnlyCollectionFormatter<>).MakeGenericType(closedROC.GetGenericArguments());
                formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType);
                _formatterInstances[type] = formatter;
                return formatter;
            }

            // Same for ReadOnlyDictionary<K,V>: written as a Dictionary<K,V> payload, rebuilt by wrapping.
            var closedRD = ReflectionHelper.FindClosedType(type, typeof(System.Collections.ObjectModel.ReadOnlyDictionary<,>));
            if (closedRD != null)
            {
                var formatterType = typeof(ReadOnlyDictionaryFormatter<,>).MakeGenericType(closedRD.GetGenericArguments());
                formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType);
                _formatterInstances[type] = formatter;
                return formatter;
            }


            //
            // Collection?
            //
            // If it implements ICollection, we can serialize it!
            var closedCollection = ReflectionHelper.FindClosedType(type, typeof(ICollection<>));

            // If the type really implements some kind of ICollection, we can create a CollectionFormatter for it
            if (closedCollection != null)
            {
                var itemType = closedCollection.GetGenericArguments()[0];

                // Use the general case collection formatter
                var formatterType = typeof(CollectionFormatter<,>).MakeGenericType(type, itemType);
                formatter = (IFormatter)Ceras.Helpers.Instantiator.Create(formatterType, _ceras);

                _formatterInstances[type] = formatter;
                return formatter;
            }

            return null;
        }

        private static void ArrayRankTooHigh(int rank)
        {
            throw new InvalidOperationException("Multi-dimensional array of rank " + rank + " is not yet supported, please open an issue on github");
        }
    }
}