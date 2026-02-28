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
        readonly Dictionary<object, int> _serializationCache;

        // At deserialization-time we keep adding all new objects to this list, so when we find a back-reference we can take it from here.
        // RefProxy enables us to deserialize even the most complex scenarios (For example: Objects that directly reference themselves, while they're not even fully constructed yet)
        readonly List<RefProxy> _deserializationCache = new List<RefProxy>(64);


        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectCache"/> class.
        /// </summary>
        /// <param name="comparer">The <see cref="IEqualityComparer{T}"/> to use.</param>
        public ObjectCache(IEqualityComparer<object> comparer = null)
        {
            _serializationCache = new Dictionary<object, int>(64, comparer);
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

            _serializationCache.Add(value, id);

            if (DebugTraceEnabled)
                _serializeTrace.Add(value?.GetType().FullName ?? "null");

            return id;
        }


        // Deserialization:
        // When encountering a new object
        internal RefProxy<T> CreateDeserializationProxy<T>(Type overrideType = null) where T : class
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
            public abstract void ResetAndReturn();
            public abstract void RegisterFixup(object target, MemberInfo member, int index);
        }

        internal sealed class RefProxy<T> : RefProxy where T : class
        {
            T _value;
            public T Value
            {
                get => _value;
                set
                {
                    _value = value;
                    if (value != null)
                        ApplyFixups();
                }
            }

            List<Fixup> _fixups;

            public override object ObjectValue => _value;

            public override void RegisterFixup(object target, MemberInfo member, int index)
            {
                if (_fixups == null)
                    _fixups = new List<Fixup>();

                _fixups.Add(new Fixup
                {
                    Target = target,
                    Member = member,
                    Index = index
                });
            }

            void ApplyFixups()
            {
                if (_fixups == null || _fixups.Count == 0)
                    return;

                foreach (var fixup in _fixups)
                {
                    if (fixup.Index >= 0)
                    {
                        if (fixup.Target is System.Collections.IList list)
                        {
                            list[fixup.Index] = _value;
                            continue;
                        }
                        else if (fixup.Target is object[] array)
                        {
                            array[fixup.Index] = _value;
                            continue;
                        }
                    }

                    if (fixup.Member is FieldInfo f)
                    {
                        f.SetValue(fixup.Target, _value);
                    }
                    else if (fixup.Member is PropertyInfo p)
                    {
                        p.SetValue(fixup.Target, _value);
                    }
                }

                _fixups.Clear();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override void ResetAndReturn()
            {
                // Make sure we don't hold any references!
                _value = default;
                _fixups?.Clear();
                // Go back to the pool
                RefProxyPool<T>.Return(this);
            }

            public override string ToString()
            {
                return $"RefProxy({typeof(T).FriendlyName()}): {_value}";
            }
        }

        internal struct Fixup
        {
            public object Target;
            public MemberInfo Member;
            public int Index;
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
