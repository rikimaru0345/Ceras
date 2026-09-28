namespace Ceras.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Reflection;

    // Specially made exclusively for the ReferenceFormatter (previously known as CacheFormatter), maybe it should be a nested class instead since there's no way this will be re-used anywhere else?
    class ObjectCache
    {
        // ── Debug tracing ──────────────────────────────────────────────────────────
        // Enable by setting DebugTraceEnabled = true before Save/Load.
        // After both runs, call DumpComparison(path) to write a diff file.
        internal static bool DebugTraceEnabled = false;
        static readonly List<string> _serializeTrace    = new();
        static readonly List<string> _deserializeTrace  = new();
        static bool _traceIsSerialize; // true during Serialize, false during Deserialize

        internal static void BeginSerializeTrace()   { _serializeTrace.Clear();   _traceIsSerialize = true;  }
        internal static void BeginDeserializeTrace() { _deserializeTrace.Clear(); _traceIsSerialize = false; }

        internal static void DumpComparison(string path)
        {
            var sb = new StringBuilder();
            int max = Math.Max(_serializeTrace.Count, _deserializeTrace.Count);
            int firstDiff = -1;
            for (int i = 0; i < max; i++)
            {
                string s = i < _serializeTrace.Count   ? _serializeTrace[i]   : "<missing>";
                string d = i < _deserializeTrace.Count ? _deserializeTrace[i] : "<missing>";
                bool diff = s != d;
                if (diff && firstDiff == -1) firstDiff = i;
                sb.AppendLine($"{(diff ? ">>>" : "   ")} [{i:D5}]  SER={s}  |  DE={d}");
            }
            if (firstDiff >= 0)
            {
                sb.Insert(0, $"FIRST DIVERGENCE at index {firstDiff}\n\n");
            }
            else
            {
                sb.Insert(0, "No divergence found — traces are identical.\n\n");
            }
            File.WriteAllText(path, sb.ToString());
        }
        // ──────────────────────────────────────────────────────────────────────────
        // While serializing we add all encountered objects and give them an ID (their index), so when we encounter them again we can just write the index instead.
        // The keys are compared with the comparer below, which is reference identity unless the
        // configuration asked for something else: an id is a position in the reader's
        // deserialization cache, so it may only ever be shared by one and the same instance.
        readonly Dictionary<object, int> _serializationCache;

        // At deserialization-time we keep adding all new objects to this list, so when we find a back-reference we can take it from here.
        // RefProxy enables us to deserialize even the most complex scenarios (For example: Objects that directly reference themselves, while they're not even fully constructed yet)
        readonly List<RefProxy> _deserializationCache = new List<RefProxy>(64);


        /// <summary>
        /// The comparer used when the caller did not ask for one. Reference identity, and it has
        /// to be: the id a slot gets is a *position* in the deserialization cache, so
        /// "already seen" has to mean "this very instance was already written", never "an equal
        /// instance was". Keying on <see cref="object.Equals(object)"/> collapsed two distinct
        /// instances of any type that overrides equality into one back-reference, so the second
        /// occurrence came back as the first object - a saved graph that is not the graph that
        /// was written - and it was worse for boxed values, which are equal far more often than
        /// anyone writing them expects. The same reasoning is why <c>TypeDictionary</c> forces
        /// <see cref="object.ReferenceEquals(object, object)"/> and
        /// <see cref="RuntimeHelpers.GetHashCode(object)"/> for <c>Type</c> keys.
        /// <para>
        /// A caller-supplied comparer is honoured as given (see
        /// <see cref="CerasConfig.ObjectCacheComparer"/>), because collapsing equal
        /// instances is a legitimate - if identity-losing - choice for a type whose instances are
        /// interchangeable by definition. Ceras does not second-guess it.
        /// </para>
        /// </summary>
        static readonly IEqualityComparer<object> ReferenceComparer = ReferenceEqualityComparer.Instance;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectCache"/> class.
        /// </summary>
        /// <param name="comparer">
        /// The <see cref="IEqualityComparer{T}"/> deciding which values share a cache id, or
        /// <c>null</c> for reference identity (<see cref="ReferenceComparer"/>).
        /// </param>
        public ObjectCache(IEqualityComparer<object>? comparer = null)
        {
            _serializationCache = new Dictionary<object, int>(64, comparer ?? ReferenceComparer);
        }

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

            // Add, not the indexer, so that a comparer which reports a value as already present in
            // this same bucket is told so rather than having its earlier id silently re-pointed at a
            // position the reader never creates a proxy for. With the default comparer neither can
            // happen: the hash is the identity hash, so TryGetExistingObjectId above always finds
            // the value itself when it is in here, and a value registered once is never added twice.
            // A caller-supplied comparer whose GetHashCode changes for a value that is still cached
            // is a different story - the lookup misses and the value is written a second time as if
            // new - which is why the comparer is not a free choice (see CerasConfig).
            _serializationCache.Add(value, id);

            if (DebugTraceEnabled)
                _serializeTrace.Add(value?.GetType().FullName ?? "null");

            return id;
        }


        // Deserialization:
        // When encountering a new object
        internal RefProxy<T> CreateDeserializationProxy<T>(Type overrideType = null!) where T : class
        {
            var p = RefProxyPool<T>.Rent();
            _deserializationCache.Add(p);

            if (DebugTraceEnabled)
                _deserializeTrace.Add(overrideType?.FullName ?? typeof(T).FullName);

            return p;
        }

        // Deserialization:
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

            var proxy = _deserializationCache[id];
            return (T)proxy.ObjectValue;
        }


        internal RefProxy GetProxy(int id)
        {
#if DEBUG
            if (id < 0 || id >= _deserializationCache.Count)
                throw new InvalidOperationException("Object cache does not contain an object with the ID: " + id);
#endif
            return _deserializationCache[id];
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
            public abstract Type ProxyType { get; }
            public abstract void SetObjectValue(object value);
            public abstract void ResetAndReturn();
        }

        internal sealed class RefProxy<T> : RefProxy where T : class
        {
            T _value = null!;
            public T Value
            {
                get => _value;
                set
                {
                    _value = value;
                }
            }

            public override object ObjectValue => _value;
            public override Type ProxyType => typeof(T);

            public override void SetObjectValue(object value)
            {
                _value = (T)value;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override void ResetAndReturn()
            {
                // Make sure we don't hold any references!
                _value = default;
                // Go back to the pool
                RefProxyPool<T>.Return(this);
            }

            public override string ToString()
            {
                return $"RefProxy({typeof(T).FriendlyName()}): {_value}";
            }
        }

        internal static class RefProxyPool<T> where T : class
        {
            static readonly FactoryPool<RefProxy<T>> _proxyPool;

            static RefProxyPool()
            {
                 _proxyPool = new FactoryPool<RefProxy<T>>(CreateProxy, 8);

                RefProxyPoolRegister.RegisterPool(_proxyPool);
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static RefProxy<T> Rent()
            {
                return _proxyPool.RentObject();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static void Return(RefProxy<T> refProxy)
            {
                _proxyPool.ReturnObject(refProxy);
            }

            internal static int GetPoolCapacity() => _proxyPool.Capacity;

            static RefProxy<T> CreateProxy()
            {
                return new RefProxy<T>();
            }
        }


        // We want to be able to clear all the static pools, but since there's no way to enumerate all "generic instances" of generic static classes,
        // we let the RefProxyPools register themselves here.
        internal static class RefProxyPoolRegister
        {
            static List<IFactoryPool> _genericPools = new List<IFactoryPool>();

            public static void RegisterPool(IFactoryPool pool)
            {
                lock(_genericPools)
                    _genericPools.Add(pool);
            }

            public static void TrimAll()
            {
                lock(_genericPools)
                {
                    foreach(var p in _genericPools)
                    {
                        p.TrimPool();
                    }
                }
            }
        }
    }
}
