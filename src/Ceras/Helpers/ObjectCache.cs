namespace Ceras.Helpers
{
	using System;
	using System.Collections.Generic;

	// Specially made exclusively for the ReferenceFormatter (previously known as CacheFormatter), maybe it should be a nested class instead since there's no way this will be re-used anywhere else?
	class ObjectCache
	{
		readonly Dictionary<object, int> _serializationCache = new Dictionary<object, int>();
		readonly List<RefProxy> _deserializationCache = new List<RefProxy>();



		// Serialization:
		// If this object was encountered before, retrieve its ID
		internal bool TryGetExistingObjectId<T>(T value, out int id)
		{
			return _serializationCache.TryGetValue(value, out id);
		}

		// Serialization:
		// Save and object and assign an ID to it.
		// If it gets encoutnered again, then TryGetExistingObjectId will give you the ID to it.
		internal int RegisterObject<T>(T value)
		{
			var id = _serializationCache.Count;
			_serializationCache[value] = id;
			return id;
		}

		// Deserialization:
		// When encountering a new object
		internal RefProxy<T> CreateDeserializationProxy<T>()
		{
			var p = RefProxyPool<T>.Rent();
			_deserializationCache.Add(p);

			return p;
		}

		// Deserialization:
		// This method is only intended to be used by the Serializer to inject known
		// Types before any deserialization has even happened
		// !!
		// !! Order in which types are added is important !!
		// !!
		internal void AddKnownType(Type type)
		{
			var p = CreateDeserializationProxy<Type>();
			p.Value = type;
		}

		// For deserialization:
		// Returns an object that was deserialized previously (seen already, and created by CreateDeserializationProxy)
		internal T GetExistingObject<T>(int id)
		{
			// In case you're wondering what's up here:
			// Why are we not directly casting to RefProxy<T> and then return .Value ??
			// Answer:
			// What if a specific object (MySpecificType) was entered into the deserializer as <object>
			// because the field type was not known at the time?
			// In that case we'd try to do this conversion:  (RefProxy<MyspecificType>)((object)((RefProxy<object>)reference))
			// which of course doesn't work...

#if DEBUG
			if (id < 0 || id >= _deserializationCache.Count)
				throw new InvalidOperationException("Object cache does not contain an object with the ID: " + id);
#endif

			var reference = _deserializationCache[id];
			return (T)reference.ObjectValue;
		}


		public void ClearSerializationCache()
		{
			_serializationCache.Clear();
		}

		public void ClearDeserializationCache()
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
			public abstract object ObjectValue { get; set; }
			public abstract void ResetAndReturn();
		}

		internal class RefProxy<T> : RefProxy
		{
			FactoryPool<RefProxy<T>> _sourcePool;
			public T Value;

			public override object ObjectValue
			{
				get => Value;
				set => Value = (T)value;
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
			readonly static FactoryPool<RefProxy<T>> _proxyPool = new FactoryPool<RefProxy<T>>(CreateRefProxy, 8);
			
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
