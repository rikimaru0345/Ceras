using System;
using System.Collections.Generic;
using System.Linq;

namespace Ceras.Helpers
{
	using System.Collections;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;

	// modified from TypeDictionary:
	// - save "loadFactor" as "1f / loadFactor" so we can quickly multiply our size to get the capacity
	// - precalculate and save "max buckets", so we can always avoid CalculateCapacity (except when we actually need to resize)
	// - TKey must be class (not sure if it helps, but it won't hurt)

	// todo: allow saving a single "checkpoint" that can be quickly restored

	/// <summary>
	/// Specialized dictionary, the default hash-code for <typeparamref name="TKey"/> will be used (even if its overriden)
	/// </summary>
	/// <typeparam name="TKey"></typeparam>
	/// <typeparam name="TValue"></typeparam>
	[DebuggerTypeProxy(typeof(RefDictionaryDebugView<,>))]
	[DebuggerDisplay("Count = {Count}")]
	class RefDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
		where TKey : class
	{
		// We want to initialize without allocating arrays. We also want to avoid null checks.
		// Array.Empty would give divide by zero in modulo operation. So we use static one element arrays.
		// The first add will cause a resize replacing these with real arrays of three elements.
		// Arrays are wrapped in a class to avoid being duplicated for each <Type, TValue>
		static readonly Entry[] InitialEntries = new Entry[1];


		int _count;
		// 0-based index into _entries of head of free chain: -1 means empty
		int _freeList = -1;
		// 1-based index into _entries; 0 means empty
		int[] _buckets;
		Entry[] _entries;

		int _bucketsLengthMinusOne;

		float _loadFactorInv;
		int _maxEntries; // if we have that or more, we need to resize to keep the load factor


		[DebuggerDisplay("({key}, {value})->{next}")]
		struct Entry
		{
			public TKey key;
			public TValue value;
			// 0-based index of next entry in chain: -1 means end of chain
			// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
			// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
			public int next;
		}


		/// <summary>
		/// Construct with at least the specified capacity for
		/// entries before resizing must occur.
		/// </summary>
		/// <param name="capacity">Requested minimum capacity</param>
		/// <param name="loadFactor">The maximum fraction that the dictionary uses internally before resizing</param>
		public RefDictionary(int capacity, float loadFactor = 0.75f)
		{
			if (capacity < 0)
				throw new ArgumentOutOfRangeException(nameof(capacity));
			if (loadFactor < 0.1 || loadFactor > 1)
				throw new ArgumentOutOfRangeException(nameof(loadFactor) + " must be between 0.1 and 1.0");

			_loadFactorInv = 1f / loadFactor;

			if (capacity < 4)
				capacity = 4; // 1 would indicate the dummy array

			_maxEntries = capacity = HashHelpers.PowerOf2(capacity);

			int actualSize = (int)(capacity * _loadFactorInv + 0.5);

			_buckets = new int[actualSize];
			_entries = new Entry[actualSize];

			_bucketsLengthMinusOne = _buckets.Length - 1;
		}

		/// <summary>
		/// Count of entries in the dictionary.
		/// </summary>
		public int Count => _count;

		/// <summary>
		/// Clears the dictionary. Note that this invalidates any active enumerators.
		/// </summary>
		public void Clear()
		{
			if (_count == 0)
				return;

			_count = 0;
			_freeList = -1;
			//_buckets = HashHelpers.SizeOneIntArray;
			_bucketsLengthMinusOne = _buckets.Length - 1;
			//_entries = InitialEntries;

			Array.Clear(_buckets, 0, _buckets.Length);
			Array.Clear(_entries, 0, _entries.Length);
		}

		/// <summary>
		/// Looks for the specified key in the dictionary.
		/// </summary>
		/// <param name="key">Key to look for</param>
		/// <returns>true if the key is present, otherwise false</returns>
		public bool ContainsKey(TKey key)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int collisionCount = 0;
			for (int i = _buckets[RuntimeHelpers.GetHashCode(key) & _bucketsLengthMinusOne] - 1;
					(uint)i < (uint)entries.Length; i = entries[i].next)
			{
				if (ReferenceEquals(key, entries[i].key))
					return true;

				if (collisionCount == entries.Length)
				{
					// The chain of entries forms a loop; which means a concurrent update has happened.
					// Break out of the loop and throw, rather than looping forever.
					throw new InvalidOperationException("concurrent operations not supported");
				}
				collisionCount++;
			}

			return false;
		}

		/// <summary>
		/// Gets the value if present for the specified key.
		/// </summary>
		/// <param name="key">Key to look for</param>
		/// <param name="value">Value found, otherwise default(TValue)</param>
		/// <returns>true if the key is present, otherwise false</returns>
		public bool TryGetValue(TKey key, out TValue value)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int collisionCount = 0;
			for (int i = _buckets[RuntimeHelpers.GetHashCode(key) & _bucketsLengthMinusOne] - 1;
					(uint)i < (uint)entries.Length; i = entries[i].next)
			{
				if (ReferenceEquals(key, entries[i].key))
				{
					value = entries[i].value;
					return true;
				}
				if (collisionCount == entries.Length)
				{
					// The chain of entries forms a loop; which means a concurrent update has happened.
					// Break out of the loop and throw, rather than looping forever.
					throw new InvalidOperationException("concurrent operations not supported");
				}
				collisionCount++;
			}

			value = default;
			return false;
		}

		/// <summary>
		/// Removes the entry if present with the specified key.
		/// </summary>
		/// <param name="key">Key to look for</param>
		/// <returns>true if the key is present, false if it is not</returns>
		public bool Remove(TKey key)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int bucketIndex = RuntimeHelpers.GetHashCode(key) & _bucketsLengthMinusOne;
			int entryIndex = _buckets[bucketIndex] - 1;

			int lastIndex = -1;
			int collisionCount = 0;
			while (entryIndex != -1)
			{
				Entry candidate = entries[entryIndex];
				if (ReferenceEquals(candidate.key, key))
				{
					if (lastIndex != -1)
					{   // Fixup preceding element in chain to point to next (if any)
						entries[lastIndex].next = candidate.next;
					}
					else
					{   // Fixup bucket to new head (if any)
						_buckets[bucketIndex] = candidate.next + 1;
					}

					entries[entryIndex] = default;

					entries[entryIndex].next = -3 - _freeList; // New head of free list
					_freeList = entryIndex;

					_count--;
					return true;
				}
				lastIndex = entryIndex;
				entryIndex = candidate.next;

				if (collisionCount == entries.Length)
				{
					// The chain of entries forms a loop; which means a concurrent update has happened.
					// Break out of the loop and throw, rather than looping forever.
					throw new InvalidOperationException("concurrent operations not supported");
				}
				collisionCount++;
			}

			return false;
		}

		// Not safe for concurrent _reads_ (at least, if either of them add)
		// For concurrent reads, prefer TryGetValue(key, out value)
		/// <summary>
		/// Gets the value for the specified key, or, if the key is not present,
		/// adds an entry and returns the value by ref. This makes it possible to
		/// add or update a value in a single look up operation.
		/// </summary>
		/// <param name="key">Key to look for</param>
		/// <returns>Reference to the new or existing value</returns>
		public ref TValue GetOrAddValueRef(TKey key)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int collisionCount = 0;

			int bucketIndex = RuntimeHelpers.GetHashCode(key) & _bucketsLengthMinusOne;

			for (int i = _buckets[bucketIndex] - 1;
					(uint)i < (uint)entries.Length;
					i = entries[i].next)
			{
				if (ReferenceEquals(key, entries[i].key))
					return ref entries[i].value;

				if (collisionCount == entries.Length)
				{
					// The chain of entries forms a loop; which means a concurrent update has happened.
					// Break out of the loop and throw, rather than looping forever.
					throw new InvalidOperationException("concurrent operations not supported");
				}
				collisionCount++;
			}

			return ref AddKey(key, bucketIndex);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		ref TValue AddKey(TKey key, int bucketIndex)
		{
			Entry[] entries = _entries;
			int entryIndex;
			if (_freeList != -1)
			{
				entryIndex = _freeList;
				_freeList = -3 - entries[_freeList].next;
			}
			else
			{
				if (_count >= _maxEntries || entries.Length == 1)
				{
					entries = Resize();
					bucketIndex = RuntimeHelpers.GetHashCode(key) & _bucketsLengthMinusOne;
					// entry indexes were not changed by Resize
				}
				entryIndex = _count;
			}

			entries[entryIndex].key = key;
			entries[entryIndex].next = _buckets[bucketIndex] - 1;
			_buckets[bucketIndex] = entryIndex + 1;
			_count++;
			return ref entries[entryIndex].value;
		}

		Entry[] Resize()
		{
			Debug.Assert(_entries.Length == _count || _entries.Length == 1); // We only copy _count, so if it's longer we will miss some

			int count = _count;

			// New dict must be able to contain at least 2x as many entries,
			// and also have enough space left for the load factor
			_maxEntries = _maxEntries * 2;
			int newSize = (int)(_maxEntries * _loadFactorInv + 0.5f);

			if ((uint)newSize > (uint)int.MaxValue) // uint cast handles overflow
				throw new InvalidOperationException("capacity overflow");


			var entries = new Entry[newSize];
			Array.Copy(_entries, 0, entries, 0, count);

			var newBuckets = new int[entries.Length];
			while (count-- > 0)
			{
				int bucketIndex = RuntimeHelpers.GetHashCode(entries[count].key) & (newBuckets.Length - 1);
				entries[count].next = newBuckets[bucketIndex] - 1;
				newBuckets[bucketIndex] = count + 1;
			}

			_buckets = newBuckets;
			_bucketsLengthMinusOne = _buckets.Length - 1;
			_entries = entries;

			return entries;
		}

		/// <summary>
		/// Gets an enumerator over the dictionary
		/// </summary>
		public Enumerator GetEnumerator() => new Enumerator(this); // avoid boxing

		/// <summary>
		/// Gets an enumerator over the dictionary
		/// </summary>
		IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
			new Enumerator(this);

		/// <summary>
		/// Gets an enumerator over the dictionary
		/// </summary>
		IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

		/// <summary>
		/// Enumerator
		/// </summary>
		public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
		{
			readonly RefDictionary<TKey, TValue> _dictionary;
			int _index;
			int _count;
			KeyValuePair<TKey, TValue> _current;

			internal Enumerator(RefDictionary<TKey, TValue> dictionary)
			{
				_dictionary = dictionary;
				_index = 0;
				_count = _dictionary._count;
				_current = default;
			}

			/// <summary>
			/// Move to next
			/// </summary>
			public bool MoveNext()
			{
				if (_count == 0)
				{
					_current = default;
					return false;
				}

				_count--;

				while (_dictionary._entries[_index].next < -1)
					_index++;

				_current = new KeyValuePair<TKey, TValue>(
					_dictionary._entries[_index].key,
					_dictionary._entries[_index++].value);
				return true;
			}

			/// <summary>
			/// Get current value
			/// </summary>
			public KeyValuePair<TKey, TValue> Current => _current;

			object IEnumerator.Current => _current;

			void IEnumerator.Reset()
			{
				_index = 0;
				_count = _dictionary._count;
				_current = default;
			}

			/// <summary>
			/// Dispose the enumerator
			/// </summary>
			public void Dispose() { }
		}
	}


	sealed class RefDictionaryDebugView<K, V> where K : class
	{
		readonly RefDictionary<K, V> _dictionary;

		public RefDictionaryDebugView(RefDictionary<K, V> dictionary)
		{
			_dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
		}

		[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
		public KeyValuePair<K, V>[] Items => _dictionary.ToArray();
	}
}
