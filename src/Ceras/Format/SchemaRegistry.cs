using System;
using System.Collections.Generic;
using System.Text;

namespace Ceras.Format
{
    /// <summary>
    /// Runtime registry of all types registered via <c>[GenerateFormatter(VersionTolerant = true)]</c>.
    /// Populated during startup by <c>GeneratedFormatters.UseFormatters(config)</c>.
    /// </summary>
    public sealed class SchemaRegistry
    {
        sealed class Entry
        {
            public int            Id;
            public SchemaTypeInfo Info;
            public Type           Type;
        }

        readonly Dictionary<Type,   Entry>  _byType       = new();
        readonly Dictionary<string, Entry>  _byStableName = new();
        int    _nextId      = WellKnownTypes.UserTypeBase;
        ulong? _fingerprint = null;
        readonly object _lock = new();

        /// <summary>
        /// Clears all registered types. Internal use only.
        /// </summary>
        internal void InternalClear()
        {
            lock (_lock)
            {
                _byType.Clear();
                _byStableName.Clear();
                _nextId = WellKnownTypes.UserTypeBase;
                _fingerprint = null;
            }
        }

        // ── Registration ─────────────────────────────────────────────────────

        /// <summary>
        /// Called by the generated <c>GeneratedFormatters.UseFormatters</c> for every
        /// <c>[GenerateFormatter(VersionTolerant = true)]</c> type.
        /// Safe to call multiple times with the same type (idempotent).
        /// </summary>
        public void Register(Type type, SchemaTypeInfo info)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            lock (_lock)
            {
                if (_byType.ContainsKey(type))
                    return; // idempotent

                if (info == null)
                {
                    if (WellKnownTypes.IsWellKnown(type)) return;
                    info = CreateSchemaForUserType(type);
                }

                var entry = new Entry { Id = _nextId++, Info = info, Type = type };

                _byType[type] = entry;

                _byStableName[info.StableName] = entry;

                foreach (var prev in info.PreviousTypeNames)
                    _byStableName.TryAdd(prev, entry);

                _fingerprint = null; // invalidate
            }
        }

        // ── Lookup ────────────────────────────────────────────────────────────

        public bool TryGetId(Type type, out int id)
        {
            lock (_lock)
            {
                if (_byType.TryGetValue(type, out var e)) { id = e.Id; return true; }
                
                // Automatic registration for types encountered at runtime (e.g. generic instantiations)
                if (type != null && !WellKnownTypes.IsWellKnown(type))
                {
                    EnsureRegisteredRecursive(type);
                    if (_byType.TryGetValue(type, out e)) { id = e.Id; return true; }
                }

                id = -1;
                return false;
            }
        }

        void EnsureRegisteredRecursive(Type type)
        {
            if (type == null || WellKnownTypes.IsWellKnown(type) || _byType.ContainsKey(type))
                return;

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                // Register arguments first
                foreach (var arg in type.GetGenericArguments())
                    EnsureRegisteredRecursive(arg);

                // Create and register schema for this instantiation
                var info = CreateSchemaForGeneric(type);
                Register(type, info);
            }
            else
            {
                Register(type, null);
            }
        }

        static SchemaTypeInfo CreateSchemaForUserType(Type type)
        {
            // Simple description for types that weren't source-generated but need to be robustly handled.
            // We use the full name as the stable name to avoid collisions.
            return new SchemaTypeInfo(
                stableName: type.FullName ?? type.Name,
                previousTypeNames: Array.Empty<string>(),
                kind: type.IsValueType ? SchemaTypeKind.Struct : SchemaTypeKind.Class,
                fields: Array.Empty<SchemaFieldInfo>(), // traversal engine will use reflection
                fixedByteSize: 0);
        }

        static SchemaTypeInfo CreateSchemaForGeneric(Type type)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            var argNames = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
                argNames[i] = args[i].FullName ?? args[i].Name;

            return new SchemaTypeInfo(
                stableName: type.FullName ?? type.Name,
                previousTypeNames: Array.Empty<string>(),
                kind: SchemaTypeKind.GenericInstantiation,
                fields: Array.Empty<SchemaFieldInfo>(),
                fixedByteSize: 0,
                enumValues: null,
                underlyingTypeName: def.FullName ?? def.Name,
                argTypeNames: argNames);
        }

        public bool TryGetInfo(Type type, out SchemaTypeInfo info)
        {
            lock (_lock)
            {
                if (_byType.TryGetValue(type, out var e)) { info = e.Info; return true; }
                info = null;
                return false;
            }
        }

        /// <summary>
        /// Resolves a stable name (or previous type name) to the current registered <see cref="Type"/>.
        /// Used by the migration engine when reading old schema sections.
        /// </summary>
        public bool TryResolveType(string stableName, out Type type)
        {
            lock (_lock)
            {
                if (_byStableName.TryGetValue(stableName, out var e)) { type = e.Type; return true; }
                type = null;
                return false;
            }
        }

        /// <summary>Enumerates all registered entries in registration order.</summary>
        public IEnumerable<(Type type, int id, SchemaTypeInfo info)> All()
        {
            lock (_lock)
            {
                var copy = new List<(Type type, int id, SchemaTypeInfo info)>(_byType.Count);
                foreach (var kv in _byType)
                    copy.Add((kv.Key, kv.Value.Id, kv.Value.Info));
                return copy;
            }
        }

        // ── Fingerprint ───────────────────────────────────────────────────────

        /// <summary>
        /// Computes a stable fingerprint over all registered schemas.
        /// Cached after the first call. Invalidated whenever a new type is registered.
        /// </summary>
        public ulong ComputeFingerprint()
        {
            lock (_lock)
            {
                if (_fingerprint.HasValue)
                    return _fingerprint.Value;

                var sorted = new List<Entry>(_byType.Count);
                foreach (var e in _byType.Values)
                    sorted.Add(e);
                sorted.Sort((a, b) => string.Compare(
                    a.Info.StableName, b.Info.StableName, StringComparison.Ordinal));

                uint h1 = 0, h2 = 0x9e3779b9u;
                foreach (var entry in sorted)
                {
                    var info = entry.Info;
                    h1 = Combine(h1, Hash(info.StableName));
                    h2 = Combine(h2, (uint)info.Kind);
                    
                    if (info.Fields != null)
                    {
                        foreach (var f in info.Fields)
                        {
                            h1 = Combine(h1, Hash(f.StableName));
                            h2 = Combine(h2, Hash(f.TypeName));
                            h2 = Combine(h2, (uint)(f.IsMonomorphic ? 1 : 0));
                        }
                    }

                    if (info.EnumValues != null)
                    {
                        foreach (var ev in info.EnumValues)
                        {
                            h1 = Combine(h1, Hash(ev.Name));
                            h2 = Combine(h2, (uint)ev.Value);
                        }
                    }
                }

                _fingerprint = ((ulong)h1 << 32) | h2;
                return _fingerprint.Value;
            } 
        }

        static uint Hash(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            return xxHash.CalculateHash(bytes);
        }

        static uint Combine(uint h, uint v)
        {
            h ^= v;
            h = (h << 13) | (h >> 19);  // rotate left 13
            h += h << 3;
            h ^= h >> 17;
            return h;
        }
    }
}
