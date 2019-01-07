using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ceras.Helpers
{
	// Source: 
	// https://github.com/neuecc/MessagePack-CSharp/blob/master/src/MessagePack/Internal/ThreadsafeTypeKeyHashTable.cs

	// Safe for multiple-read, single-write.
	class ThreadsafeTypeKeyHashTable<TValue>
    {
        Entry[] _buckets;
        int _size; // only use in writer lock

        readonly object _writerLock = new object();
        readonly float _loadFactor;

        // IEqualityComparer.Equals is overhead if key only Type, don't use it.
        // readonly IEqualityComparer<TKey> comparer;

        public ThreadsafeTypeKeyHashTable(int capacity = 4, float loadFactor = 0.75f)
        {
            var tableSize = CalculateCapacity(capacity, loadFactor);
            _buckets = new Entry[tableSize];
            _loadFactor = loadFactor;
        }

        public bool TryAdd(Type key, TValue value)
        {
            return TryAdd(key, _ => value); // create lambda capture
        }

        public bool TryAdd(Type key, Func<Type, TValue> valueFactory)
        {
            TValue _;
            return TryAddInternal(key, valueFactory, out _);
        }

        bool TryAddInternal(Type key, Func<Type, TValue> valueFactory, out TValue resultingValue)
        {
            lock (_writerLock)
            {
                var nextCapacity = CalculateCapacity(_size + 1, _loadFactor);

                if (_buckets.Length < nextCapacity)
                {
                    // rehash
                    var nextBucket = new Entry[nextCapacity];
                    for (int i = 0; i < _buckets.Length; i++)
                    {
                        var e = _buckets[i];
                        while (e != null)
                        {
                            var newEntry = new Entry { Key = e.Key, Value = e.Value, Hash = e.Hash };
                            AddToBuckets(nextBucket, key, newEntry, null, out resultingValue);
                            e = e.Next;
                        }
                    }

                    // add entry(if failed to add, only do resize)
                    var successAdd = AddToBuckets(nextBucket, key, null, valueFactory, out resultingValue);

                    // replace field(threadsafe for read)
                    VolatileWrite(ref _buckets, nextBucket);

                    if (successAdd) _size++;
                    return successAdd;
                }
                else
                {
                    // add entry(insert last is thread safe for read)
                    var successAdd = AddToBuckets(_buckets, key, null, valueFactory, out resultingValue);
                    if (successAdd) _size++;
                    return successAdd;
                }
            }
        }

        bool AddToBuckets(Entry[] buckets, Type newKey, Entry newEntryOrNull, Func<Type, TValue> valueFactory, out TValue resultingValue)
        {
            var h = (newEntryOrNull != null) ? newEntryOrNull.Hash : newKey.GetHashCode();
            if (buckets[h & (buckets.Length - 1)] == null)
            {
                if (newEntryOrNull != null)
                {
                    resultingValue = newEntryOrNull.Value;
                    VolatileWrite(ref buckets[h & (buckets.Length - 1)], newEntryOrNull);
                }
                else
                {
                    resultingValue = valueFactory(newKey);
                    VolatileWrite(ref buckets[h & (buckets.Length - 1)], new Entry { Key = newKey, Value = resultingValue, Hash = h });
                }
            }
            else
            {
                var searchLastEntry = buckets[h & (buckets.Length - 1)];
                while (true)
                {
                    if (searchLastEntry.Key == newKey)
                    {
                        resultingValue = searchLastEntry.Value;
                        return false;
                    }

                    if (searchLastEntry.Next == null)
                    {
                        if (newEntryOrNull != null)
                        {
                            resultingValue = newEntryOrNull.Value;
                            VolatileWrite(ref searchLastEntry.Next, newEntryOrNull);
                        }
                        else
                        {
                            resultingValue = valueFactory(newKey);
                            VolatileWrite(ref searchLastEntry.Next, new Entry { Key = newKey, Value = resultingValue, Hash = h });
                        }
                        break;
                    }
                    searchLastEntry = searchLastEntry.Next;
                }
            }

            return true;
        }

        public bool TryGetValue(Type key, out TValue value)
        {
            var table = _buckets;
            var hash = key.GetHashCode();
            var entry = table[hash & table.Length - 1];

            if (entry == null) goto NOT_FOUND;

            if (entry.Key == key)
            {
                value = entry.Value;
                return true;
            }

            var next = entry.Next;
            while (next != null)
            {
                if (next.Key == key)
                {
                    value = next.Value;
                    return true;
                }
                next = next.Next;
            }

            NOT_FOUND:
            value = default(TValue);
            return false;
        }

        public TValue GetOrAdd(Type key, Func<Type, TValue> valueFactory)
        {
	        if (TryGetValue(key, out var v))
            {
                return v;
            }

            TryAddInternal(key, valueFactory, out v);
            return v;
        }

        static int CalculateCapacity(int collectionSize, float loadFactor)
        {
            var initialCapacity = (int)(((float)collectionSize) / loadFactor);
            var capacity = 1;
            while (capacity < initialCapacity)
            {
                capacity <<= 1;
            }

            if (capacity < 8)
            {
                return 8;
            }

            return capacity;
        }

        static void VolatileWrite(ref Entry location, Entry value)
        {
#if NETSTANDARD
            System.Threading.Volatile.Write(ref location, value);
#elif UNITY_WSA || NET_4_6
            System.Threading.Volatile.Write(ref location, value);
#else
            System.Threading.Thread.MemoryBarrier();
            location = value;
#endif
        }

        static void VolatileWrite(ref Entry[] location, Entry[] value)
        {
#if NETSTANDARD
            System.Threading.Volatile.Write(ref location, value);
#elif UNITY_WSA || NET_4_6
            System.Threading.Volatile.Write(ref location, value);
#else
            System.Threading.Thread.MemoryBarrier();
            location = value;
#endif
        }

        class Entry
        {
            public Type Key;
            public TValue Value;
            public int Hash;
            public Entry Next;

            // debug only
            public override string ToString()
            {
                return Key + "(" + Count() + ")";
            }

            int Count()
            {
                var count = 1;
                var n = this;
                while (n.Next != null)
                {
                    count++;
                    n = n.Next;
                }
                return count;
            }
        }
    }
}
