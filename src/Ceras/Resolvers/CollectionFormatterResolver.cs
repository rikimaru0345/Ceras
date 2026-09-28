namespace Ceras.Resolvers
{
    using Formatters;
    using Helpers;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;

    /// <summary>
    /// Creates formatters for array-types and all types that implement ICollection&lt;&gt;
    /// <para>
    /// Returns null for anything else, which is how a resolver declines and lets the next one try.
    /// </para>
    /// </summary>
    public class CollectionFormatterResolver : IFormatterResolver
    {
        readonly CerasSerializer _ceras;
        Dictionary<Type, IFormatter> _formatterInstances = new Dictionary<Type, IFormatter>();

        internal CollectionFormatterResolver(CerasSerializer ceras)
        {
            _ceras = ceras;
        }

        public IFormatter? GetFormatter(Type type)
        {
            //
            // Do we already have an array or collection formatter?
            //
            if (_formatterInstances.TryGetValue(type, out var cached))
                return cached;

            // Assigned only on the paths that create a formatter, so it is non-null from here on.
            IFormatter formatter;

            //
            // Array?
            //
            if (type.IsArray)
            {
                // An array type always has an element type; every use of it below needs a non-null Type.
                var itemType = type.GetElementType() ?? throw new InvalidOperationException($"Array type '{type}' has no element type");
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
                        // Returned rather than called as a statement: [DoesNotReturn] drives nullable
                        // flow, not reachability, so a bare call would still leave `formatter`
                        // possibly unassigned at the cache write below.
                        return ArrayRankTooHigh(rank);
                    }
                }

                _formatterInstances[type] = formatter;
                return formatter;
            }


            //
            // Special collection? (Stack, Queue)
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

            // The collection shapes the owner has decided Ceras does not support — ReadOnlyCollection<T> /
            // ReadOnlyDictionary<K,V> (the built-in types and every subclass) and every List<T> /
            // Dictionary<K,V> subclass — get no formatter here, and none anywhere else either.
            //
            // This is the one place the raw/transient serializer resolves *every* collection, whatever
            // position the type is reached through: a root value, a declared member, a value behind an
            // object slot, or the type a payload names while it is being read. Refusing here is therefore
            // what makes the refusal hold on the read side too, which the per-position checks cannot (a
            // value arriving in a slot is checked on the way *in*, by name, not on the way out). The
            // predicate matches a *derived* type only, so the exact built-in List<T>/Dictionary<TKey,TValue>
            // keeps the CollectionFormatter below, and the message is the one sentence the policy states
            // so this path and the banned-type paths cannot describe the same refusal two ways.
            if (Ceras.Format.UnsupportedCollectionPolicy.IsUnsupported(type))
                throw new BannedTypeException(Ceras.Format.UnsupportedCollectionPolicy.Describe(type, "a Ceras serializer"));

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

            // Neither an array nor an ICollection<T>: decline, so the next resolver is asked.
            return null;
        }

        [DoesNotReturn]
        private static IFormatter ArrayRankTooHigh(int rank)
        {
            throw new InvalidOperationException("Multi-dimensional array of rank " + rank + " is not yet supported, please open an issue on github");
        }
    }
}