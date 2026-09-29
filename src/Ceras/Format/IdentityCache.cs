using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

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
    /// <para>
    /// Reserving is separate from publishing for a second reason as well: the interpreter has to keep this
    /// list in step with the writer's even where it builds <em>nothing</em>. A value at a removed position
    /// is skipped, but the writer still reserved a slot for it, so every marker after it is numbered as
    /// though the object existed — which makes that reservation its own state, and the one that cannot be
    /// published. <see cref="ReserveSkipped"/> records it, and <see cref="Resolve"/> refuses it: a reference
    /// to an object the reader skipped past is a reference it has nothing to point at, and answering null
    /// would drop a reference the save legitimately contains while reporting success.
    /// </para>
    /// <para>
    /// Generic over the value the slots hold, so the same arithmetic serves every reader that fills slots
    /// — the streaming interpreter instantiates it as <c>IdentityCache&lt;object?&gt;</c>, and the
    /// old-data graph's builder (TODO.md, Milestone 2) as <c>IdentityCache&lt;OldValue&gt;</c>. The
    /// parameter is what keeps a second implementation from being written by copy, which is the failure
    /// the paragraph above is about: the reserve/publish split and the bounds check are load-bearing, and
    /// a hand-rolled second copy of them is a second chance to disagree with the writer.
    /// </para>
    /// </summary>
    /// <typeparam name="T">What a slot holds. A slot is nullable, and a null is <em>not</em> one state but
    /// two: a reserved-but-unpublished slot — which is what a cycle resolves through — and a published
    /// null, which is a value the save genuinely carries. A third state is not a value at all but the
    /// absence of one (see <see cref="ReserveSkipped"/>), and it is tracked beside the list rather than
    /// inside it so that none of the three is inferred from a null.</typeparam>
    sealed class IdentityCache<T>
    {
        readonly List<T?> _slots = new();

        // The slots above, minus the ones this reader can never fill, as ascending non-overlapping
        // half-open [From, To) ranges. It is null until the first skipped occurrence: a save with nothing
        // behind a removed position — every read path, and the only kind most files are — pays one
        // reference comparison per resolve and nothing else, and never allocates.
        //
        // Ranges rather than a set of indices because the slots of one skipped occurrence are *consecutive*:
        // a skip walks its whole subtree, and nothing in between is a read (see Resolve, which is only
        // reached from the read path at all). A removed member holding a thousand objects is therefore one
        // entry. Correctness does not rest on that: a gap starts a new range, so the list stays exact
        // either way, and contiguity is only what keeps it short.
        SkippedRange[]? _skipped;
        int _skippedCount;

        public int Count => _slots.Count;

        /// <summary>
        /// Reserves the slot a writer reserved for a new object, before its payload is read.
        /// <paramref name="placeholder"/> is what a reference to it resolves to until it is published —
        /// a reused instance for a value that reuses one, otherwise null.
        /// </summary>
        public int Reserve(T? placeholder)
        {
            int index = _slots.Count;
            _slots.Add(placeholder);
            return index;
        }

        /// <summary>
        /// Reserves the slot a writer reserved for a value at a position the reader is <em>skipping</em> —
        /// a member the current model no longer has, say — and records that nothing will ever be published
        /// into it. The index arithmetic stays in step with the writer's, which is the only reason the slot
        /// exists; the identity it stood for is gone, and <see cref="Resolve"/> says so rather than
        /// answering the placeholder.
        /// </summary>
        public int ReserveSkipped()
        {
            int index = _slots.Count;
            _slots.Add(default);

            var skipped = _skipped;

            // Extend the run this one continues, or start the next one. Not a merge, because a skip's
            // slots can only be consecutive: nothing can reserve between two of them.
            if (skipped != null && _skippedCount != 0 && skipped[_skippedCount - 1].To == index)
            {
                var last = skipped[_skippedCount - 1];
                skipped[_skippedCount - 1] = new SkippedRange(last.From, index + 1);
                return index;
            }

            if (skipped == null || skipped.Length == _skippedCount) GrowSkipped();
            _skipped![_skippedCount++] = new SkippedRange(index, index + 1);
            return index;
        }

        void GrowSkipped()
        {
            int capacity = _skipped == null ? 4 : _skipped.Length * 2;
            Array.Resize(ref _skipped, capacity);
        }

        /// <summary>Publishes the object into a reserved slot.</summary>
        public void Publish(int index, T? value) => _slots[index] = value;

        /// <summary>Publishes into a reserved slot, if one was reserved.</summary>
        public void Publish(int? index, T? value)
        {
            if (index.HasValue) Publish(index.Value, value);
        }

        /// <summary>
        /// Resolves a back-reference marker.
        /// <para>
        /// Two ways a marker cannot be answered, and they are different failures. An index past the end is
        /// corrupt data: the writer only ever emits indices for objects it has written, so this used to
        /// decode as a legitimate null — the one value a caller cannot tell apart from a real one. An index
        /// <em>inside</em> the cache whose first occurrence was skipped is a save this build's model cannot
        /// express: the writer really did write the object, at a member that no longer exists, so the
        /// reader has no value to hand back, and a null here is a reference the file contains silently
        /// dropped while the load reports success. That one is a schema problem the caller can act on.
        /// </para>
        /// <para>
        /// A slot that is merely reserved — an object whose members are still being read — is neither. It
        /// resolves to its placeholder, which is what makes a cycle work, and nothing here can tell a
        /// placeholder that is still null from one that is not, which is exactly why the two states above
        /// are recorded rather than inferred from the value.
        /// </para>
        /// </summary>
        public T? Resolve(int index)
        {
            if ((uint)index >= (uint)_slots.Count)
                throw new Exceptions.CerasDataCorruptionException(
                    $"Corrupt data: a back-reference points at object {index}, but only {_slots.Count} " +
                    "objects have been read so far.");

            if (_skippedCount != 0 && IsSkipped(index))
                throw new Exceptions.CerasSchemaException(
                    $"Cannot read the save: a member refers back to object {index}, but the only place the file writes that " +
                    "object is a member this build no longer has. The interpreter accounts for a removed member's bytes without " +
                    "building what they described, so there is nothing for this reference to resolve to; returning null would drop " +
                    "a reference the save really contains and report the load as a success. Keep that member in the model — and " +
                    "keep the [PreviousTypeName] that goes with it, so the old name still resolves — or drop every reference to it " +
                    "as well, and read the save again.");

            return _slots[index];
        }

        /// <summary>Whether <paramref name="index"/> falls in one of the skipped runs. Binary search: the runs are
        /// ascending and non-overlapping, and there are as many of them as there are skipped occurrences,
        /// not as many as there are skipped objects.</summary>
        bool IsSkipped(int index)
        {
            var skipped = _skipped;
            if (skipped == null) return false;

            int low = 0, high = _skippedCount - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                var range = skipped[middle];
                if (index < range.From) high = middle - 1;
                else if (index >= range.To) low = middle + 1;
                else return true;
            }

            return false;
        }

        /// <summary>One run of skipped slots, as a half-open range of indices.</summary>
        readonly struct SkippedRange
        {
            public readonly int From;
            public readonly int To;

            public SkippedRange(int from, int to)
            {
                From = from;
                To = to;
            }
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

        // The current CLR type each entry decoded from a payload's Type *value* names, keyed the same way.
        // Such an entry has no stable name in the file, so there is no migration plan to look it up in; the
        // type it resolves to is what decides whether its payload is written behind a reference marker.
        readonly Dictionary<int, Type>              _typeValueTypes = new();

        public FileSchemaView(Dictionary<string, OldTypeSchema>? oldSchemas)
        {
            _byName = oldSchemas ?? new Dictionary<string, OldTypeSchema>();

            _byId = new Dictionary<int, OldTypeSchema>();
            foreach (var s in _byName.Values)
                _byId.TryAdd(s.TypeId, s);
        }

        public Dictionary<string, OldTypeSchema> ByName => _byName;

        /// <summary>
        /// The file's schema for a type id, or false when the file never described that id — in which
        /// case <paramref name="schema"/> is null, which is a fact about the file rather than a value
        /// a caller may use.
        /// </summary>
        public bool TryGetById(int typeId, [MaybeNullWhen(false)] out OldTypeSchema schema)
            => _byId.TryGetValue(typeId, out schema);

        /// <summary>Whether an id is already taken, so a synthesized identity can be checked for a collision.</summary>
        public bool ContainsById(int typeId) => _byId.ContainsKey(typeId);

        /// <summary>
        /// Adds a schema entry decoded from a <c>Type</c> value in the payload (see
        /// <see cref="TypeValueShapes"/>), so every reader and skipper that looks a value up by id finds it
        /// exactly as it would find a type the file's schema section described. Such an entry is keyed by id
        /// only: the file never named it, so adding it under a stable name would invent an identity it does
        /// not have.
        /// </summary>
        public void AddTypeValue(OldTypeSchema schema, Type? currentType)
        {
            _byId[schema.TypeId] = schema;
            if (currentType != null) _typeValueTypes[schema.TypeId] = currentType;
        }

        /// <summary>
        /// The current CLR type a Type-value entry names, or null for every entry the file itself described
        /// (those get their type from their migration plan) and for one this build cannot resolve.
        /// </summary>
        public Type? TypeValueCurrentType(int typeId)
            => _typeValueTypes.TryGetValue(typeId, out var type) ? type : null;

        /// <summary>
        /// The file's schema for a stable name. A null <paramref name="stableName"/> is answered as
        /// "not in this file" rather than as a lookup failure, because a value the reader could not
        /// name has no entry to find; the out parameter is then null.
        /// </summary>
        public bool TryGetByName(string? stableName, [MaybeNullWhen(false)] out OldTypeSchema schema)
        {
            if (stableName == null)
            {
                schema = null;
                return false;
            }

            return _byName.TryGetValue(stableName, out schema);
        }

        public bool Contains(string? stableName) => stableName != null && _byName.ContainsKey(stableName);

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
