using System;
using System.Collections.Generic;
using System.Text;

namespace Ceras.Savegame
{
    /// <summary>
    /// Runtime registry of all types registered via <c>[GenerateFormatter(VersionTolerant = true)]</c>.
    /// Populated during startup by <c>GeneratedFormatters.UseFormatters(config)</c>.
    /// Thread-safety: registration happens once at startup before any serialization — no locking needed.
    /// </summary>
    public static class SavegameSchemaRegistry
    {
        sealed class Entry
        {
            public int            Id;
            public SchemaTypeInfo Info;
            public Type           Type;
        }

        static readonly Dictionary<Type,   Entry>  ByType       = new();
        static readonly Dictionary<string, Entry>  ByStableName = new();
        static int    _nextId      = WellKnownTypes.UserTypeBase;
        static ulong? _fingerprint = null;
        static readonly object _lock = new();

        /// <summary>
        /// Clears all registered types. Used for testing.
        /// </summary>
        internal static void Clear()
        {
            lock (_lock)
            {
                ByType.Clear();
                ByStableName.Clear();
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
        public static void Register(Type type, SchemaTypeInfo info)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (info == null) throw new ArgumentNullException(nameof(info));

            lock (_lock)
            {
                if (ByType.ContainsKey(type))
                    return; // idempotent

                var entry = new Entry { Id = _nextId++, Info = info, Type = type };

                ByType[type] = entry;

                ByStableName[info.StableName] = entry;

                foreach (var prev in info.PreviousTypeNames)
                    ByStableName.TryAdd(prev, entry);

                _fingerprint = null; // invalidate
            }
        }

        // ── Lookup ────────────────────────────────────────────────────────────

        public static bool TryGetId(Type type, out int id)
        {
            if (ByType.TryGetValue(type, out var e)) { id = e.Id; return true; }
            id = -1;
            return false;
        }

        public static bool TryGetInfo(Type type, out SchemaTypeInfo info)
        {
            if (ByType.TryGetValue(type, out var e)) { info = e.Info; return true; }
            info = null;
            return false;
        }

        /// <summary>
        /// Resolves a stable name (or previous type name) to the current registered <see cref="Type"/>.
        /// Used by the migration engine when reading old schema sections.
        /// </summary>
        public static bool TryResolveType(string stableName, out Type type)
        {
            if (ByStableName.TryGetValue(stableName, out var e)) { type = e.Type; return true; }
            type = null;
            return false;
        }

        /// <summary>Enumerates all registered entries in registration order.</summary>
        public static IEnumerable<(Type type, int id, SchemaTypeInfo info)> All()
        {
            lock (_lock)
            {
                var copy = new List<(Type type, int id, SchemaTypeInfo info)>(ByType.Count);
                foreach (var kv in ByType)
                    copy.Add((kv.Key, kv.Value.Id, kv.Value.Info));
                return copy;
            }
        }

        // ── Fingerprint ───────────────────────────────────────────────────────

        /// <summary>
        /// Computes a stable fingerprint over all registered schemas.
        /// Cached after the first call. Invalidated whenever a new type is registered.
        /// </summary>
        public static ulong ComputeFingerprint()
        {
            lock (_lock)
            {
                if (_fingerprint.HasValue)
                    return _fingerprint.Value;

                var sorted = new List<Entry>(ByType.Count);
                foreach (var e in ByType.Values)
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

        // Rotate-XOR mix — simple but sufficient for a schema fingerprint
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
