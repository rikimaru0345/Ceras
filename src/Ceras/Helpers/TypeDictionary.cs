using System;
using System.Collections.Generic;
using System.Linq;

namespace Ceras.Helpers
{
	using System.Collections;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;

	// Based on: 
	// https://github.com/dotnet/corefxlab/blob/master/src/Microsoft.Experimental.Collections/Microsoft/Collections/Extensions/DictionarySlim.cs
	//
	// With the following changes:
	// - TKey is replaced to always be 'Type'. Since that's what we'll always use it for. (by now we could probably merge this with RefDictionary)
	// - IEquatable restriction removed
	// - We're always using ReferenceEquals to compare keys
	// - Hashing is forced to be done with RuntimeHelpers.GetHashCode() instead of .GetHashCode() to save quite some additional overhead (with 'Type' the call will end up there anyway)

	/// <summary>
	/// A lightweight Dictionary with three principal differences compared to <see cref="Dictionary{Type, TValue}"/>
	///
	/// 1) It is possible to do "get or add" in a single lookup using <see cref="GetOrAddValueRef(Type)"/>. For
	/// values that are value types, this also saves a copy of the value.
	/// 2) It assumes it is cheap to equate values.
	/// 3) It assumes the keys implement <see cref="IEquatable{Type}"/> or else Equals() and they are cheap and sufficient.
	/// </summary>
	/// <remarks>
	/// 1) This avoids having to do separate lookups (<see cref="Dictionary{Type, TValue}.TryGetValue(Type, out TValue)"/>
	/// followed by <see cref="Dictionary{Type, TValue}.Add(Type, TValue)"/>.
	/// There is not currently an API exposed to get a value by ref without adding if the key is not present.
	/// 2) This means it can save space by not storing hash codes.
	/// 3) This means it can avoid storing a comparer, and avoid the likely virtual call to a comparer.
	/// </remarks>
	[DebuggerTypeProxy(typeof(TypeDictionaryDebugView<>))]
	[DebuggerDisplay("Count = {Count}")]
	class TypeDictionary<TValue> : IReadOnlyCollection<KeyValuePair<Type, TValue>>
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


		[DebuggerDisplay("({key}, {value})->{next}")]
		struct Entry
		{
			public Type key;
			public TValue value;
			// 0-based index of next entry in chain: -1 means end of chain
			// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
			// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
			public int next;
		}

		/// <summary>
		/// Construct with default capacity.
		/// </summary>
		public TypeDictionary()
		{
			_buckets = HashHelpers.SizeOneIntArray;
			_entries = InitialEntries;
		}

		/// <summary>
		/// Construct with at least the specified capacity for
		/// entries before resizing must occur.
		/// </summary>
		/// <param name="capacity">Requested minimum capacity</param>
		public TypeDictionary(int capacity)
		{
			if (capacity < 0)
				throw new ArgumentOutOfRangeException(nameof(capacity));
			if (capacity < 2)
				capacity = 2; // 1 would indicate the dummy array
			capacity = HashHelpers.PowerOf2(capacity);
			_buckets = new int[capacity];
			_entries = new Entry[capacity];
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
			_count = 0;
			_freeList = -1;
			_buckets = HashHelpers.SizeOneIntArray;
			_entries = InitialEntries;
		}

		/// <summary>
		/// Looks for the specified key in the dictionary.
		/// </summary>
		/// <param name="key">Key to look for</param>
		/// <returns>true if the key is present, otherwise false</returns>
		public bool ContainsKey(Type key)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int collisionCount = 0;
			for (int i = _buckets[RuntimeHelpers.GetHashCode(key) & (_buckets.Length - 1)] - 1;
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
		public bool TryGetValue(Type key, out TValue value)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int collisionCount = 0;
			for (int i = _buckets[RuntimeHelpers.GetHashCode(key) & (_buckets.Length - 1)] - 1;
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
		public bool Remove(Type key)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int bucketIndex = RuntimeHelpers.GetHashCode(key) & (_buckets.Length - 1);
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
		public ref TValue GetOrAddValueRef(Type key)
		{
			if (key == null)
				throw new ArgumentNullException(nameof(key));
			Entry[] entries = _entries;
			int collisionCount = 0;
			int bucketIndex = RuntimeHelpers.GetHashCode(key) & (_buckets.Length - 1);
			for (int i = _buckets[bucketIndex] - 1;
					(uint)i < (uint)entries.Length; i = entries[i].next)
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
		ref TValue AddKey(Type key, int bucketIndex)
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
				if (_count == entries.Length || entries.Length == 1)
				{
					entries = Resize();
					bucketIndex = RuntimeHelpers.GetHashCode(key) & (_buckets.Length - 1);
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
			// We only copy _count, so if it's longer we will miss some
			Debug.Assert(_entries.Length == _count || _entries.Length == 1, $"Cannot resize: _entries.Length=={_entries.Length}, _count=={_count}");
			int count = _count;
			int newSize = _entries.Length * 2;
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
		IEnumerator<KeyValuePair<Type, TValue>> IEnumerable<KeyValuePair<Type, TValue>>.GetEnumerator() =>
			new Enumerator(this);

		/// <summary>
		/// Gets an enumerator over the dictionary
		/// </summary>
		IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

		/// <summary>
		/// Enumerator
		/// </summary>
		public struct Enumerator : IEnumerator<KeyValuePair<Type, TValue>>
		{
			readonly TypeDictionary<TValue> _dictionary;
			int _index;
			int _count;
			KeyValuePair<Type, TValue> _current;

			internal Enumerator(TypeDictionary<TValue> dictionary)
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

				_current = new KeyValuePair<Type, TValue>(
					_dictionary._entries[_index].key,
					_dictionary._entries[_index++].value);
				return true;
			}

			/// <summary>
			/// Get current value
			/// </summary>
			public KeyValuePair<Type, TValue> Current => _current;

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


	sealed class TypeDictionaryDebugView<V>
	{
		readonly TypeDictionary<V> _dictionary;

		public TypeDictionaryDebugView(TypeDictionary<V> dictionary)
		{
			_dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
		}

		[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
		public KeyValuePair<Type, V>[] Items
		{
			get
			{
				return _dictionary.ToArray();
			}
		}
	}
}
