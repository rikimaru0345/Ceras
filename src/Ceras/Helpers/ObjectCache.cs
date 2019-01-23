namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;

	// Specially made exclusively for the ReferenceFormatter (previously known as CacheFormatter), maybe it should be a nested class instead since there's no way this will be re-used anywhere else?
	class ObjectCache
	{
		// While serializing we add all encountered objects and give them an ID (their index), so when we encounter them again we can just write the index instead.
		readonly RefDictionary<object, int> _serializationCache = new RefDictionary<object, int>(64, 0.75f);
		// At deserialization-time we keep adding all new objects to this list, so when we find a back-reference we can take it from here.
		// RefProxy enables us to deserialize even the most complex scenarios (For example: Objects that directly reference themselves, while they're not even fully constructed yet)
		readonly List<RefProxy> _deserializationCache = new List<RefProxy>(64);


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

			_serializationCache.GetOrAddValueRef(value) = id;

			return id;
		}

		// Deserialization:
		// When encountering a new object
		internal RefProxy<T> CreateDeserializationProxy<T>() where T : class
		{
			var p = RefProxyPool<T>.Rent();
			_deserializationCache.Add(p);

			return p;
		}


		// For deserialization:
		// Returns an object that was deserialized previously (seen already, and created by CreateDeserializationProxy)
		internal T GetExistingObject<T>(int id) where T : class
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
			return (T)reference.ObjectValue;
		}


		internal void ClearSerializationCache()
		{
			_serializationCache.Clear();
		}

		internal void ClearDeserializationCache()
		{
			for (int i = 0; i < _deserializationCache.Count; i++)
			{
				var proxy = _deserializationCache[i];
				// The pool will keep the ref proxy alive, so we absolutely have to make sure
				// that we're not keeping any outside objects alive (they're potentially really large!)
				proxy.ResetAndReturn();
			}

			_deserializationCache.Clear();
		}


		internal abstract class RefProxy
		{
			public abstract object ObjectValue { get; }
			public abstract void ResetAndReturn();
		}

		internal class RefProxy<T> : RefProxy
		{
			FactoryPool<RefProxy<T>> _sourcePool;
			public T Value;

			public override object ObjectValue
			{
				get => Value;
			}

			public RefProxy(FactoryPool<RefProxy<T>> sourcePool)
			{
				_sourcePool = sourcePool;
			}

			public override void ResetAndReturn()
			{
				// Make sure we don't hold any references!
				Value = default;
				// Go back to the pool
				_sourcePool.ReturnObject(this);
			}

			public override string ToString()
			{
				return $"RefProxy({typeof(T).Name}): {Value}";
			}
		}

		static class RefProxyPool<T>
		{
			static readonly FactoryPool<RefProxy<T>> _proxyPool = new FactoryPool<RefProxy<T>>(CreateRefProxy, 32);
			
			internal static RefProxy<T> Rent()
			{
				return _proxyPool.RentObject();
			}

			static RefProxy<T> CreateRefProxy(FactoryPool<RefProxy<T>> pool)
			{
				return new RefProxy<T>(pool);
			}
		}
	}
}
