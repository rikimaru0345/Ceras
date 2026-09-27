using System;
using System.Collections.Generic;

namespace Ceras.Format
{
    /// <summary>
    /// The identity cache the writer's reference markers index into: one slot per object the reader has
    /// started to build, in payload order. A back-reference marker is an index into this list, so the
    /// reader and the writer agree on identity for shared references and cycles.
    /// <para>
    /// The slot is reserved <em>before</em> the object's payload is read, and published when the object
    /// exists, which is what lets a cycle through a constructor parameter or a field resolve to the
    /// half-built instance. Reserving is therefore separate from publishing, and both are here so the
    /// index arithmetic exists in exactly one place.
    /// </para>
    /// </summary>
    sealed class IdentityCache
    {
        readonly List<object?> _slots = new();

        public int Count => _slots.Count;

        /// <summary>
        /// Reserves the slot a writer reserved for a new object, before its payload is read.
        /// <paramref name="placeholder"/> is what a reference to it resolves to until it is published —
        /// a reused instance for a value that reuses one, otherwise null.
        /// </summary>
        public int Reserve(object? placeholder)
        {
            int index = _slots.Count;
            _slots.Add(placeholder);
            return index;
        }

        /// <summary>Publishes the object into a reserved slot.</summary>
        public void Publish(int index, object? value) => _slots[index] = value;

        /// <summary>Publishes into a reserved slot, if one was reserved.</summary>
        public void Publish(int? index, object? value)
        {
            if (index.HasValue) Publish(index.Value, value);
        }

        /// <summary>
        /// Resolves a back-reference marker. An index past the end is corrupt data: the writer only ever
        /// emits indices for objects it has written, so this used to decode as a legitimate null — the one
        /// value a caller cannot tell apart from a real one.
        /// </summary>
        public object? Resolve(int index)
        {
            if ((uint)index >= (uint)_slots.Count)
                throw new Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: a back-reference points at object {index}, but only {_slots.Count} " +
                    "objects have been read so far.");

            return _slots[index];
        }
    }

    /// <summary>
    /// The file's own schema section, indexed the way the reader needs it: by stable name (which is how a
    /// type survives a rename, because the entry carries its previous names) and by the file's type id
    /// (which is what the payload references).
    /// <para>
    /// This is deliberately the only place that knows how a file's schemas are indexed, and the only place
    /// that maps a <em>current</em> CLR type onto the name the file used for it.
    /// </para>
    /// </summary>
    sealed class FileSchemaView
    {
        readonly Dictionary<string, OldTypeSchema> _byName;
        readonly Dictionary<int, OldTypeSchema>     _byId;

        public FileSchemaView(Dictionary<string, OldTypeSchema> oldSchemas)
        {
            _byName = oldSchemas ?? new Dictionary<string, OldTypeSchema>();

            _byId = new Dictionary<int, OldTypeSchema>();
            foreach (var s in _byName.Values)
                _byId.TryAdd(s.TypeId, s);
        }

        public Dictionary<string, OldTypeSchema> ByName => _byName;

        public bool TryGetById(int typeId, out OldTypeSchema schema) => _byId.TryGetValue(typeId, out schema);

        public bool TryGetByName(string stableName, out OldTypeSchema schema)
        {
            if (stableName == null)
            {
                schema = null!;
                return false;
            }

            return _byName.TryGetValue(stableName, out schema);
        }

        public bool Contains(string stableName) => stableName != null && _byName.ContainsKey(stableName);

        /// <summary>
        /// The name the file used for a current CLR type: its canonical stable name, or the first of its
        /// rename aliases the file also knows. Returns the canonical name when the file describes neither,
        /// so a lookup miss reports the name the reader was looking for.
        /// <para>
        /// A renamed <em>generic</em> cannot be found by either of those lookups, because neither string
        /// is the one in the file: the file names the closed type <c>Old.Box`1&lt;System.Int32&gt;</c>
        /// while this build only records the open alias <c>Old.Box`1</c>, and the arguments can be
        /// renamed as well, so no expanded form can be spelled here either. The last step therefore asks
        /// the registry instead: it resolves a file's name onto the current type structurally, so a name
        /// that lands on <em>this</em> type is the name this file used for it. Ordered by the file's type
        /// id so the answer never depends on dictionary iteration order.
        /// </para>
        /// </summary>
        public string ResolveStableName(Type currentType, SchemaRegistry registry)
        {
            if (registry != null && registry.TryGetInfo(currentType, out var info))
            {
                if (_byName.ContainsKey(info.StableName)) return info.StableName;
                foreach (var previous in info.PreviousTypeNames)
                    if (_byName.ContainsKey(previous)) return previous;
            }

            if (registry != null)
            {
                foreach (var schema in ByIdOrder())
                    if (registry.TryResolveType(schema.StableName, out var resolved) && resolved == currentType)
                        return schema.StableName;
            }

            return TypeIdentity.GetCanonicalName(currentType);
        }

        /// <summary>The file's schemas in type-id order, so every derived decision is deterministic.</summary>
        IEnumerable<OldTypeSchema> ByIdOrder()
        {
            var ordered = new List<OldTypeSchema>(_byName.Count);
            foreach (var s in _byName.Values)
                if (s != null) ordered.Add(s);

            ordered.Sort((a, b) => a.TypeId.CompareTo(b.TypeId));
            return ordered;
        }
    }
}
