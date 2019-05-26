namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;
	using System.Runtime.CompilerServices;

	// todo:
	// - currently we have a List<RefProxy>, with each RefProxy being an object, so the list can be resized if it runs out of space
	// - it would be faster to just use a object[] (and unsafe casting references into it).
	//   But can we still expand it then? What if we want to get a value that was written to the old array?
	//   Assuming no corrupted data, any GetExistingObject call will only want to get a reference of an object, not a reference to the actual proxy-slot.
	//   And by the time GetExistingObject gets called, the reference will have been assigned already anyway.
	// - But what if we get 1 proxy (A), and then another one (B), but requesting proxy B triggers an expansion of the buffer?
	//   So now proxy A is invalid because it is a slot in the old array.
	//   And anything written to it doesn't matter, because lookups will happen on the new array.

	// todo: need a test to ensure this can never happen

	// Specially made exclusively for the ReferenceFormatter (previously known as CacheFormatter), maybe it should be a nested class instead since there's no way this will be re-used anywhere else?
	class ObjectCache
	{
		// While serializing we add all encountered objects and give them an ID (their index), so when we encounter them again we can just write the index instead.
		readonly Dictionary<object, int> _serializationCache = new Dictionary<object, int>(64);

		// At deserialization-time we keep adding all new objects to this list, so when we find a back-reference we can take it from here.
		object[] _deserializationCache = new object[1024];
		int _nextDeserializationSlot = 0;


		// Serialization:
		// If this object was encountered before, retrieve its ID
		internal bool TryGetExistingObjectId<T>(T value, out int id) where T : class
		{
			return _serializationCache.TryGetValue(value, out id);
		}

		// Serialization:
		// Save and object and assign an ID to it.
		// If it gets encountered again, then TryGetExistingObjectId will give you the ID to it.
		internal int RegisterObject<T>(T value) where T : class
		{
			var id = _serializationCache.Count;

			_serializationCache.Add(value, id);

			return id;
		}


		internal void ClearSerializationCache()
		{
			_serializationCache.Clear();
		}



		// Deserialization:
		// When encountering a new object
		internal ref T CreateDeserializationProxy<T>() where T : class
		{
			var index = _nextDeserializationSlot++;

			ref T slot = ref Unsafe.As<object, T>(ref _deserializationCache[index]);

			if (index + 1 >= _deserializationCache.Length)
				ExpandDeserializationCache();

			return ref slot;
		}

		// For deserialization:
		// Returns an object that was deserialized previously (seen already, and created by CreateDeserializationProxy)
		internal T GetExistingObject<T>(int id) where T : class
		{
#if DEBUG
			if (id < 0 || id >= _deserializationCache.Length)
				throw new InvalidOperationException("Object cache does not contain an object with the ID: " + id);
#endif

			return Unsafe.As<object, T>(ref _deserializationCache[id]);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		void ExpandDeserializationCache()
		{
			var newSize = _deserializationCache.Length * 2;
			if (newSize < 0x4000)
				newSize = 0x4000;

			Array.Resize(ref _deserializationCache, newSize);
		}

		internal void ClearDeserializationCache()
		{
			Array.Clear(_deserializationCache, 0, _nextDeserializationSlot);
		}
	}
}
