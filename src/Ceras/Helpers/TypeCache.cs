using System;
using System.Collections.Generic;

namespace Ceras.Helpers
{
	// A copy of ObjectCache, but specialized to Types
	class TypeCache
	{
		readonly Type[] _knownTypes;

		// While serializing we add all encountered objects and give them an ID (their index), so when we encounter them again we can just write the index instead.
		readonly TypeDictionary<int> _serializationCache = new TypeDictionary<int>();

		// At deserialization-time we keep adding all new objects to this list, so when we find a back-reference we can take it from here.
		// RefProxy enables us to deserialize even the most complex scenarios (For example: Objects that directly reference themselves, while they're not even fully constructed yet)
		readonly List<TypeRefProxy> _deserializationCache = new List<TypeRefProxy>();

		readonly StackSlim<TypeRefProxy> _typeRefProxyPool = new StackSlim<TypeRefProxy>(16);


		public TypeCache(Type[] knownTypes)
		{
			_knownTypes = knownTypes;
			
			for (int i = 0; i < knownTypes.Length; i++)
			{
				var t = knownTypes[i];

				// Serialization
				RegisterObject(t);

				// Deserialization
				var p = CreateDeserializationProxy();
				p.Type = t;
			}
		}


		// Serialization:
		// If this object was encountered before, retrieve its ID
		internal bool TryGetExistingObjectId(Type value, out int id)
		{
			return _serializationCache.TryGetValue(value, out id);
		}

		// Serialization:
		// Save and object and assign an ID to it.
		// If it gets encountered again, then TryGetExistingObjectId will give you the ID to it.
		internal int RegisterObject(Type value)
		{
			var id = _serializationCache.Count;

			_serializationCache.GetOrAddValueRef(value) = id;

			return id;
		}

		// Deserialization:
		// When encountering a new object
		internal TypeRefProxy CreateDeserializationProxy()
		{
			TypeRefProxy proxy;

			if (_typeRefProxyPool.Count == 0)
				proxy = new TypeRefProxy();
			else
				proxy = _typeRefProxyPool.Pop();

			_deserializationCache.Add(proxy);

			return proxy;
		}



		// For deserialization:
		// Returns an object that was deserialized previously (seen already, and created by CreateDeserializationProxy)
		internal Type GetExistingObject(int id)
		{
			// In case you're wondering what's up here:
			// Why are we not directly casting to RefProxy<T> and then return .Value ??
			// Answer:
			// What if a specific object (MySpecificType) was entered into the deserializer as <object>
			// because the field type was not known at the time?
			// In that case we'd try to do this conversion:  (RefProxy<MySpecificType>)((object)((RefProxy<object>)reference))
			// which of course doesn't work...

#if DEBUG
			if (id < 0 || id >= _deserializationCache.Count)
				throw new InvalidOperationException("Object cache does not contain an object with the ID: " + id);
#endif

			var reference = _deserializationCache[id];
			return reference.Type;
		}


		internal void ResetSerializationCache()
		{
			// Do we even have to clear the cache?
			if (_serializationCache.Count == _knownTypes.Length)
				return; // No modifications, no need to reset

			_serializationCache.Clear();

			for (int i = 0; i < _knownTypes.Length; i++)
			{
				var t = _knownTypes[i];
				RegisterObject(t);
			}
		}


		internal void ResetDeserializationCache()
		{
			for (int i = _knownTypes.Length; i < _deserializationCache.Count; i++)
			{
				var proxy = _deserializationCache[i];

				// No need to clear the inner value of the proxy because they're 'Type's.

				_typeRefProxyPool.Push(proxy);
			}

			// Remove all entries above the KnownTypes
			var addedTypes = _deserializationCache.Count - _knownTypes.Length;
			_deserializationCache.RemoveRange(_knownTypes.Length, addedTypes);
		}



		internal class TypeRefProxy
		{
			public Type Type;

			public override string ToString()
			{
				return $"TypeRefProxy: {Type}";
			}
		}
	}
}
