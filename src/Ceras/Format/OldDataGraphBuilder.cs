using System;
using System.Buffers;
using System.Collections.Generic;
using Ceras.Exceptions;

// The payload shapes are named through the alias the interpreter uses, so the switch statements below
// read as the shape dispatch they are rather than as a qualified enum in every arm.
using PayloadShape = Ceras.Format.PayloadGrammar.PayloadShape;

namespace Ceras.Format
{
    /// <summary>
    /// The bounds a file-only decode is held to, as values rather than as a configuration object.
    /// <para>
    /// A decoder that holds no current model has no <see cref="CerasConfig"/> to read limits from, and
    /// the one bound the payload grammar itself needs is already a parameter rather than a field
    /// (<see cref="PayloadGrammar.ReadCollectionCount"/>), precisely so a reader with no config can
    /// supply it. These three are the same idea applied to the rest of the decode: the string tables
    /// are the other two places a declared length picks an allocation, and both are the caller's to
    /// bound.
    /// </para>
    /// <para>
    /// <see cref="Default"/> is the library's own unbounded configuration, so a caller who has no
    /// opinion reads exactly what a <see cref="CerasRobustSerializer"/> would.
    /// </para>
    /// </summary>
    readonly struct OldFileGraphLimits
    {
        /// <summary>The largest element count any collection or array in the payload may declare.</summary>
        public uint MaxCollectionSize { get; }

        /// <summary>The largest decoded string, in bytes, in either string-table region.</summary>
        public uint MaxStringLength { get; }

        /// <summary>The largest decoded string-table region in bytes; a Deflate region is bounded by its decompressed size.</summary>
        public long MaxStringTableSize { get; }

        public OldFileGraphLimits(uint maxCollectionSize, uint maxStringLength, long maxStringTableSize)
        {
            MaxCollectionSize  = maxCollectionSize;
            MaxStringLength    = maxStringLength;
            MaxStringTableSize = maxStringTableSize;
        }

        /// <summary>What <see cref="CerasConfig.SizeLimits"/> holds unless a caller changed it.</summary>
        public static OldFileGraphLimits Default => new(uint.MaxValue, uint.MaxValue, long.MaxValue);
    }

    /// <summary>
    /// Decodes a save's data section into an <see cref="OldDataGraph"/>, holding <b>no current model at
    /// all</b>: no <see cref="SchemaRegistry"/>, no migration plans, no <see cref="ITypeBinder"/>, no
    /// serializer, no <c>Type</c> the application happens to have. Everything the decode is answered from
    /// is the file's own record — its schema section, its two string tables and its bytes — so a save
    /// written by a build that had a type this one has not is an ordinary input rather than a refusal.
    /// <para>
    /// The four rules the graph enforces are not re-decided here; they are produced by construction:
    /// </para>
    /// <list type="number">
    /// <item><b>Index identity.</b> Every decoded value is appended to the arena, so its identity is its
    /// index and nothing a value can be renamed or retyped into can change it. The only values that
    /// take no index are recorded nulls, which <see cref="OldDataGraph"/> refuses in the arena.</item>
    /// <item><b>Closed arena.</b> The arena grows only here and is handed over once; the graph copies it.</item>
    /// <item><b>Immutable file record.</b> Nothing is mutated after construction, and every name and
    /// identity is the file's own spelling.</item>
    /// <item><b>Back-reference ordering.</b> A cache slot's arena index is assigned <em>before</em> the
    /// payload behind it is read and published at that moment, so the index a back-reference names is
    /// always strictly earlier than the reference's own — the writer wrote a value before anything
    /// that can point back at it.</item>
    /// </list>
    /// <para>
    /// <b>The two index spaces are different, and that is the whole reason for the map.</b> The writer's
    /// object-cache numbering counts only values written behind a reference marker; the arena numbers
    /// every value. An inline scalar therefore has an arena index and no cache id at all, so a marker
    /// read as an arena index would name a different value while still satisfying every rule. The map
    /// is <see cref="IdentityCache{T}"/> over <c>int</c>, so the reserve/publish split and the bounds
    /// check are the ones the interpreter already uses rather than a second copy of them.
    /// </para>
    /// <para>
    /// <b>One instance per <see cref="FileSchemaView"/>.</b> The <c>Type</c>-value grammar interns the
    /// entries it synthesizes into the view it was handed, and hands out ids from
    /// <see cref="TypeValueShapes.FirstSynthesizedId"/> upward, so a second builder over the same view
    /// would allocate ids the first one already put there and be refused as a collision. A fresh view
    /// per build is cheap — it holds no state of its own but the caller's dictionary, which the
    /// interning does not touch.
    /// </para>
    /// <para>
    /// <b>A framed custom payload is a value, not a refusal.</b> The file already says everything that can
    /// be said about one: which formatter wrote it, whether a marker stands in front of the frame, and
    /// how many bytes the frame declares. That is enough to place the value in the arena and to keep the
    /// stream aligned, so it is recorded as an <see cref="OldOpaqueNode"/> carrying exactly those facts
    /// and the bytes themselves — copied, read-only, and never interpreted. No formatter is resolved and
    /// none is called: whether a build can read the block back is a question about the <em>current</em>
    /// model, and this decoder holds none.
    /// </para>
    /// <para>
    /// <b>One string per member and per collection, not per value.</b> A refusal has to say where the
    /// value it refused sat, and a name is the only handle a caller has on a file that is otherwise
    /// perfectly valid, so the position description is built where the decode descends — once per member
    /// and once per collection, and reused by every element of that collection. It is built on the happy
    /// path too, which is a real cost on a graph with a great many members; it was traded for a
    /// refusal that names the member and the type that declares it, and it is the first thing to revisit
    /// if this decoder ever runs where allocation matters.
    /// </para>
    /// </summary>
    sealed class OldDataGraphBuilder
    {
        /// <summary>
        /// A member's <c>Type</c>-value tag, which is a 4-byte payload string-table index rather than a
        /// schema id. The same constant the interpreter and <see cref="TypeValueShapes"/> use; a fourth
        /// spelling of it would be a fourth chance to disagree with the writer.
        /// </summary>
        const int FullNameMarker = 127;

        /// <summary>
        /// How deep a closed type identity may be built. The same bound the <c>Type</c>-value grammar
        /// applies to its own nesting (<see cref="TypeValueShapes"/>): an entry whose argument is
        /// itself reaches the bound, so a doctored section that names itself as its own argument is
        /// refused rather than recursed into forever.
        /// </summary>
        const int MaxIdentityDepth = 64;

        /// <summary>
        /// What a cache slot holds between <see cref="IdentityCache{T}.Reserve"/> and the publish that
        /// follows it on the very next line. It is a value no arena index can take, so a slot observed
        /// in this state is a defect in this class rather than something the file can cause — see
        /// <see cref="ArenaIndexOfSlot"/>.
        /// </summary>
        const int UnpublishedSlot = -1;

        readonly FileSchemaView _file;
        readonly string[] _strings;
        readonly uint _maxCollectionSize;

        /// <summary>
        /// The arena, in the writer's first-occurrence order, with a null in a slot whose value is still
        /// being read. A value is reserved here <em>before</em> its payload is decoded, which is what lets
        /// a cycle name its index; the node itself is written into the slot when the payload is done.
        /// </summary>
        readonly List<OldValue?> _arena = new();

        /// <summary>The writer's object-cache slots, mapped to arena indices. Published at reserve time.</summary>
        readonly IdentityCache<int> _slots = new();

        /// <summary>
        /// The writer's recursive <c>Type</c>-value grammar, with nothing behind it: the manifest resolver
        /// answers null for every id, so a shape this build cannot name stays a structure of the file's
        /// own ids and is interned as a synthesized entry rather than closed into a CLR type.
        /// </summary>
        readonly TypeValueShapes _typeValues;

        /// <summary>Type id to the identity the graph records for it, so one identity is one object.</summary>
        readonly Dictionary<int, OldTypeIdentity> _identities = new();

        /// <summary>
        /// Where a value sits, which is what decides the one question an entry cannot answer by itself:
        /// whether a reference marker stands in front of it.
        /// </summary>
        enum ValuePosition
        {
            /// <summary>
            /// Everywhere but an object member: the save's root, a collection element, a dictionary key
            /// or value, a tuple item, a value behind a polymorphic slot's marker. A concrete array
            /// reached here <em>is</em> written behind a marker, because the collection and array
            /// formatters inject a reference wrapper for their elements.
            /// </summary>
            Indirect,

            /// <summary>
            /// A member of an object or a struct, which the generated writer binds to the member's own
            /// declared type. A member declared as a concrete array is written by
            /// <c>ArrayFormatter</c> directly — no marker, and no object-cache slot — which is the one
            /// position where the entry-level answer is wrong and only the position can say so.
            /// </summary>
            Member,
        }

        public OldDataGraphBuilder(FileSchemaView file, string[] payloadStrings, OldFileGraphLimits limits)
        {
            _file              = file  ?? throw new ArgumentNullException(nameof(file));
            _strings           = payloadStrings ?? throw new ArgumentNullException(nameof(payloadStrings));
            _maxCollectionSize = limits.MaxCollectionSize;
            _typeValues        = new TypeValueShapes(file, _ => null);
        }

        /// <summary>
        /// Reads the save's root type id and the root's payload, and closes the arena.
        /// </summary>
        /// <param name="reader">A reader over the data section, and nothing else.</param>
        /// <param name="declaredLength">
        /// How many bytes the header says the data section holds. Compared against rather than taken from
        /// <c>reader.Sequence.Length</c>, because the report has to be about the region the file
        /// <em>declares</em>: a reader handed a longer slice would otherwise be able to satisfy "read
        /// everything" by running into whatever follows.
        /// </param>
        /// <exception cref="CerasDataCorruptionException">
        /// The payload does not end where the header says it does, or a field it names is not a shape
        /// this decoder can read.
        /// </exception>
        /// <exception cref="CerasSchemaException">
        /// The file records a value this graph has no node kind for yet — one of the structural value types
        /// — naming the type, its shape and the position.
        /// </exception>
        public OldDataGraph Build(ref SequenceReader<byte> reader, long declaredLength)
        {
            int rootTypeId = SchemaSection.ReadTypeIdLeb128(ref reader, "the root type id");
            var root = ReadValue(rootTypeId, ValuePosition.Indirect, null, ref reader);

            long consumed = reader.Consumed;
            if (consumed != declaredLength)
            {
                throw new CerasDataCorruptionException(
                    $"Corrupt save: its data section is {declaredLength} bytes, but reading the root accounted for {consumed} of " +
                    $"them and left {declaredLength - consumed} unread. The whole section is the declared root type id followed by " +
                    "that root's payload and nothing else, and the file's own schema section is what says how those bytes are laid " +
                    "out, so a leftover means the file's description and the file's bytes do not agree. The header checksum already " +
                    "matched, so this is not bit rot: the file was hand-edited or was not assembled by a Ceras Save.");
            }

            return new OldDataGraph(CompletedArena(), root);
        }

        // ── The arena ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a value to the arena, or hands a recorded null straight back: a null is a fact about a
        /// value rather than an identity, nothing the writer wrote can be a back-reference to one, and
        /// <see cref="OldDataGraph"/> refuses one in the arena.
        /// </summary>
        OldValue Publish(OldValue value)
        {
            if (value is OldNullNode) return value;

            _arena.Add(value);
            return value;
        }

        /// <summary>The arena with every reserved slot filled, which is the only thing the graph is given.</summary>
        OldValue[] CompletedArena()
        {
            var nodes = new OldValue[_arena.Count];
            for (int i = 0; i < nodes.Length; i++)
                nodes[i] = _arena[i] ?? throw new CerasException(
                    $"Internal error: arena slot {i} was reserved for a value and never filled, so the graph would be handed a hole. " +
                    "A reserved slot is filled when the payload behind it finishes, and refusing here rather than reading a null is " +
                    "what makes a defect in this decoder a loud failure instead of a null reference.");

            return nodes;
        }

        /// <summary>
        /// The arena index a writer's object-cache slot stands for, which is what a back-reference names.
        /// <para>
        /// The bounds check and the never-written-too-far check are
        /// <see cref="IdentityCache{T}.Resolve"/>'s, with its own wording: a marker past the end is
        /// corrupt data, and a marker naming a slot whose first occurrence was skipped is a save this
        /// decoder cannot express. Neither can happen here beyond the first — this decoder builds every
        /// value, so it never reserves a skipped slot — but the shared check is the one that reports a
        /// marker out of range, and a second implementation of it is a second chance to disagree.
        /// </para>
        /// </summary>
        int ArenaIndexOfSlot(int slot)
        {
            int index = _slots.Resolve(slot);

            // Unreachable while the slot's index is published the moment the slot is reserved, which is
            // the line after the reserve below. It is checked rather than assumed because the failure it
            // stands for is silent otherwise: an unpublished slot holds 0, and 0 is a real arena index,
            // so a cycle resolved through one would name the graph's first value and still satisfy every
            // rule the graph enforces.
            if (index == UnpublishedSlot)
                throw new CerasException(
                    $"Internal error: the object-cache slot {slot} has no arena index, so a back-reference to it cannot be resolved. " +
                    "A slot's index is published the moment the slot is reserved, before the payload behind it is read; that this can " +
                    "be reached means the reserve and the publish came apart, which is a defect in this decoder and not a property of " +
                    "the file.");

            return index;
        }

        /// <summary>
        /// Reserves what a new value written behind a reference marker owns: the writer's cache slot and
        /// the arena index. Both are assigned here, before the payload is decoded, and the index is
        /// published immediately — a cycle through a constructor parameter or a field resolves to the
        /// index this returns, and only the node is still missing.
        /// </summary>
        int ReserveMarkedValue()
        {
            int slot = _slots.Reserve(UnpublishedSlot);

            int index = _arena.Count;
            _arena.Add(null);

            _slots.Publish(slot, index);
            return index;
        }

        // ── One dispatch per type id ────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes one value the file's <paramref name="typeId"/> describes, and returns the node to put
        /// in the value slot: either an indexed node or a recorded null.
        /// </summary>
        OldValue ReadValue(int typeId, ValuePosition position, string? where, ref SequenceReader<byte> reader)
        {
            // 0 is the well-known null type. Nothing a writer describes is written under it: a null
            // reference is a marker, a null collection a -1 count, and a null Type value its own tag.
            if (typeId == WellKnownTypes.Null)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {At(where)} is described by type id {WellKnownTypes.Null}, which is the format's null type and names " +
                    "no value at all. A null in the payload is written as a reference marker, as a collection count of " +
                    $"{Protocol.CollectionNull}, or as a Type value's own null tag, and never as this id.");

            // The reserved id space, which the writer and the reader both know at compile time and which
            // the file's own schema section never contains.
            if (typeId < WellKnownTypes.UserTypeBase)
                return ReadWellKnownValue(typeId, position, where, ref reader);

            if (!_file.TryGetById(typeId, out var entry))
                throw UndescribedIdentity(typeId, where);

            return ReadDescribedValue(entry, position, where, ref reader);
        }

        /// <summary>
        /// A value the <em>reserved</em> table describes. The names and kinds come from that table, not
        /// from any model of the application reading the file: an id below
        /// <see cref="WellKnownTypes.UserTypeBase"/> is a property of the format, and the CLR types the
        /// table names are the BCL shapes the writer encoded, not the current model.
        /// </summary>
        OldValue ReadWellKnownValue(int typeId, ValuePosition position, string? where, ref SequenceReader<byte> reader)
        {
            switch (typeId)
            {
                // Fixed-layout scalars, and the two string-shaped values. All of them are written inline
                // wherever they appear — including as a collection element, because the serializer binds
                // the reference formatter of `string` and of `Type` to the plain one.
                case WellKnownTypes.Bool:         return Publish(Scalar(WellKnownTypes.Bool, ReadBool(ref reader)));
                case WellKnownTypes.Byte:         return Publish(Scalar(WellKnownTypes.Byte, SerializerBinary.ReadByte(ref reader)));
                case WellKnownTypes.SByte:        return Publish(Scalar(WellKnownTypes.SByte, unchecked((sbyte)SerializerBinary.ReadByte(ref reader))));
                case WellKnownTypes.Char:         return Publish(Scalar(WellKnownTypes.Char, (char)SerializerBinary.ReadInt16Fixed(ref reader)));
                case WellKnownTypes.Int16:        return Publish(Scalar(WellKnownTypes.Int16, SerializerBinary.ReadInt16Fixed(ref reader)));
                case WellKnownTypes.UInt16:       return Publish(Scalar(WellKnownTypes.UInt16, SerializerBinary.ReadUInt16Fixed(ref reader)));
                case WellKnownTypes.Int32:        return Publish(Scalar(WellKnownTypes.Int32, SerializerBinary.ReadInt32Fixed(ref reader)));
                case WellKnownTypes.UInt32:       return Publish(Scalar(WellKnownTypes.UInt32, SerializerBinary.ReadUInt32Fixed(ref reader)));
                case WellKnownTypes.Single:       return Publish(Scalar(WellKnownTypes.Single, SerializerBinary.ReadFloat32Fixed(ref reader)));
                case WellKnownTypes.Int64:        return Publish(Scalar(WellKnownTypes.Int64, SerializerBinary.ReadInt64Fixed(ref reader)));
                case WellKnownTypes.UInt64:       return Publish(Scalar(WellKnownTypes.UInt64, SerializerBinary.ReadUInt64Fixed(ref reader)));
                case WellKnownTypes.Double:       return Publish(Scalar(WellKnownTypes.Double, SerializerBinary.ReadDouble64Fixed(ref reader)));
                case WellKnownTypes.Decimal:      return Publish(Scalar(WellKnownTypes.Decimal, SerializerBinary.ReadDecimal(ref reader)));
                case WellKnownTypes.DateTime:     return Publish(Scalar(WellKnownTypes.DateTime, SerializerBinary.ReadDateTime(ref reader)));
                case WellKnownTypes.TimeSpan:     return Publish(Scalar(WellKnownTypes.TimeSpan, new TimeSpan(SerializerBinary.ReadInt64Fixed(ref reader))));
                case WellKnownTypes.DateTimeOffset: return Publish(Scalar(WellKnownTypes.DateTimeOffset, SerializerBinary.ReadDateTimeOffset(ref reader)));
                case WellKnownTypes.Guid:         return Publish(Scalar(WellKnownTypes.Guid, SerializerBinary.ReadGuid(ref reader)));

                // A null string is string-table index 0, which is a real value in the file and an
                // indexed node: it says the member was a string, which an identity-free null would not.
                case WellKnownTypes.String:       return Publish(Scalar(WellKnownTypes.String, StringTable.ReadString(ref reader, _strings)));

                // A `System.Type` value is written inline, wherever it appears: a `Type`-typed member,
                // a `Type` element of a collection, or a value behind an inline-type reference marker.
                case WellKnownTypes.Type:         return Publish(ReadTypeValueNode(where, ref reader));

                // A slot declared `object` or `System.Array` names no concrete type, so the writer
                // reaches it through a reference wrapper: a marker, the concrete runtime type as a
                // `Type` value, and then that type's own payload.
                case WellKnownTypes.Object:
                case WellKnownTypes.Array:        return ReadMarkedValue(typeId, position, where, ref reader);

                // Everything else below the manifest base is either a structural *definition* — which
                // only ever appears as a `GenericInstantiation` entry's definition id, or as a `Type`
                // value's tag, where its arguments follow — or an id no writer emits.
                default:
                    throw new CerasDataCorruptionException(
                        $"Corrupt save: {At(where)} is described by type id {typeId}, which is in the well-known range but names no " +
                        "value that can be written at this position. Ids 17-42 are structural *definitions* — a collection, an array, " +
                        "a Nullable, a KeyValuePair, a tuple — and are only ever described by a GenericInstantiation entry that points at " +
                        $"them, or by a Type value's tag followed by its arguments; 43-{WellKnownTypes.UserTypeBase - 1} are reserved " +
                        "and unassigned. A Ceras writer emits neither, so the file is damaged or was not assembled by one.");
            }
        }

        OldScalar Scalar(int typeId, object? value) => new(IdentityOf(typeId), value);

        bool ReadBool(ref SequenceReader<byte> reader) => SerializerBinary.ReadByte(ref reader) != 0;

        OldValue ReadDescribedValue(OldTypeSchema entry, ValuePosition position, string? where, ref SequenceReader<byte> reader)
        {
            if (entry.Kind == SchemaTypeKind.Enum)
            {
                // An enum is written inline as its underlying value, and a struct and an enum may not
                // even be nullable, so there is never a marker in front of one.
                return Publish(ReadEnumNode(entry, ref reader));
            }

            if (entry.Kind == SchemaTypeKind.GenericInstantiation)
            {
                // The structural invariant, asked here rather than in the section parser so the parser's
                // bytes are untouched by this step, and before a single payload byte is interpreted — a
                // collection description that carries no readable argument says a count and then nothing
                // about what follows it. The parser asks the count and definition clauses when it reads a
                // section; asking all three here as well is what makes this door safe for a caller that
                // handed over a view the parser never saw, and it costs three pure calls per described
                // instantiation.
                RequireDescribable(entry, where);
            }

            // The one question an entry cannot answer by itself. Everything else about a reference marker
            // is a property of the type — the entry's kind, the opaque entry's recorded inline flag, and
            // for a user generic the kind the file's own section records for its definition — and
            // PayloadGrammar answers all of it from the file. What it deliberately does not answer is the
            // position, and there is exactly one position that differs.
            bool marker = position == ValuePosition.Member
                              && entry.Kind == SchemaTypeKind.GenericInstantiation
                              && entry.DefinitionTypeId == WellKnownTypes.Array
                            ? false
                            : PayloadGrammar.FileCarriesReferenceMarker(entry, _file);

            return marker
                ? ReadMarkedValue(entry.TypeId, position, where, ref reader)
                : Publish(ReadUnmarkedPayload(entry, position, where, ref reader));
        }

        /// <summary>
        /// Decodes a value the file writes with no reference marker in front of it, dispatching on the
        /// very <see cref="PayloadGrammar.ClassifyPayload"/> answer the interpreter dispatches on, so the
        /// two cannot describe the same bytes differently.
        /// </summary>
        OldValue ReadUnmarkedPayload(OldTypeSchema entry, ValuePosition position, string? where, ref SequenceReader<byte> reader)
        {
            switch (PayloadGrammar.ClassifyPayload(entry))
            {
                case PayloadShape.Opaque:
                    return ReadOpaqueNode(entry, where, ref reader);

                case PayloadShape.Array:
                case PayloadShape.Elements:
                    return ReadElements(entry, where, ref reader);

                case PayloadShape.Entries:
                    return ReadEntries(entry, where, ref reader);

                case PayloadShape.Nullable:
                    throw RefuseStructuralValueType(entry, where, "a Nullable<T>", "a presence byte and then the value when it is there");

                case PayloadShape.KeyValuePair:
                    throw RefuseStructuralValueType(entry, where, "a KeyValuePair<K,V>", "a key and then a value, with no count in front of them");

                case PayloadShape.Tuple:
                    throw RefuseStructuralValueType(entry, where, "a tuple",
                        "its items in order, with no count in front of them");

                default:
                    // Two different payloads land here and are written the same way — a user generic's
                    // member list and a plain class's — so this is one grammar reached by two kinds of
                    // entry, not a shape the format cannot describe. It is deliberately not a count
                    // followed by anything.
                    return ReadObjectNode(entry, ref reader);
            }
        }

        /// <summary>
        /// Decodes a value the file writes behind a reference marker: the null marker, a back-reference,
        /// an inline <c>Type</c> value, or a new value whose payload follows the tag.
        /// </summary>
        OldValue ReadMarkedValue(int declaredTypeId, ValuePosition position, string? where, ref SequenceReader<byte> reader)
        {
            int marker = SerializerBinary.ReadUInt32Bias(ref reader, Protocol.RefBias);

            if (marker == Protocol.RefNull)
                return new OldNullNode(OldNullReason.ReferenceMarker);

            if (marker >= 0)
                return Publish(new OldReferenceNode(ArenaIndexOfSlot(marker)));

            if (marker == Protocol.RefInlineType)
            {
                // A `System.Type` in a reference slot. The writer takes this marker before it registers
                // anything, so this value has no cache slot and can never be a back-reference's target;
                // it is still a value in a value slot, so it is an ordinary node.
                return Publish(ReadTypeValueNode(where, ref reader));
            }

            bool sameType = marker == Protocol.RefNewValueSameType;
            if (!sameType && marker != Protocol.RefNewValue)
                throw new CerasException($"Invalid reference marker {marker}");

            int typeId = sameType
                ? declaredTypeId
                : SchemaSection.ReadTypeIdLeb128(ref reader, "a polymorphic value's type tag");

            // Both halves are taken before the payload is decoded, and the arena index is published
            // before it: a cycle back into this value resolves to the index, and only the node is
            // missing. The index is therefore always strictly earlier than any reference to it, which
            // is what OldDataGraph's constructor checks.
            int index = ReserveMarkedValue();
            _arena[index] = ReadMarkedPayload(typeId, declaredTypeId, sameType, where, ref reader);
            return _arena[index]!;
        }

        /// <summary>The payload that follows a reference marker, once the tag has been read.</summary>
        OldValue ReadMarkedPayload(int typeId, int declaredTypeId, bool sameType, string? where, ref SequenceReader<byte> reader)
        {
            if (sameType)
            {
                // "Same type" means the writer found the declared type exact, so it wrote no tag — and a
                // type's payload is whatever its own formatter writes. The one well-known identity that
                // reaches here is `System.Object`: `object` has no serializable members, so the marker
                // plus the object-cache slot it reserved is the whole of it.
                if (declaredTypeId == WellKnownTypes.Object)
                    return new OldObjectNode(IdentityOf(WellKnownTypes.Object), Array.Empty<OldMember>());

                if (declaredTypeId < WellKnownTypes.UserTypeBase)
                    throw new CerasDataCorruptionException(
                        $"Corrupt save: {At(where)} carries a same-type marker but declares the well-known type id {declaredTypeId}. " +
                        "A declared scalar, string, array or collection is written by its own id, so the writer never puts one behind a " +
                        "marker; only a value of a type this build does not have, or a `System.Type`, can be.");

                return ReadUnmarkedPayload(RequireEntry(declaredTypeId, where), ValuePosition.Indirect, where, ref reader);
            }

            if (typeId == WellKnownTypes.Object)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {At(where)} carries System.Object as its type tag. The writer emits a tag only when the declared and " +
                    "the runtime type differ, and nothing derives from System.Object except itself, so a declared `object` holding " +
                    "`new object()` takes the same-type marker and writes no tag at all.");

            if (typeId == WellKnownTypes.Null)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {At(where)} carries the null type as its tag. A null in a reference slot is written as the null marker, " +
                    "so a null tag behind a new-value marker is a corrupt field rather than a value.");

            // A tag at or above the manifest base is a schema type id, and the file's own section is the
            // only thing that says how such a payload is written.
            if (typeId >= WellKnownTypes.UserTypeBase)
                return ReadUnmarkedPayload(RequireEntry(typeId, where), ValuePosition.Indirect, where, ref reader);

            if (typeId == FullNameMarker)
            {
                // A type named by a string rather than by an id. Resolved against the FILE's own section
                // and never against this build's registry: the two id spaces are independent, so a
                // current registry id looked up in the file's id table is a different type. The name is
                // not lost either — it becomes the entry's own stable name, which is the spelling the
                // file used.
                string typeName = StringTable.ReadString(ref reader, _strings)
                                 ?? throw new CerasDataCorruptionException(
                                     $"Corrupt save: {At(where)} is named by a full-name reference whose string-table index is the null " +
                                     "sentinel, which names no type.");

                if (!_file.TryGetByName(typeName, out var named))
                    throw new CerasSchemaException(
                        $"Cannot read {At(where)}: the save names the type '{typeName}' for a full-name reference, but that name is not an " +
                        "entry in the save's own schema section, so nothing says how the payload that follows it is written. A name is " +
                        "only a payload's description when the file also describes it; resolve it in the file, or read the save with the " +
                        "build that wrote it.");

                return ReadUnmarkedPayload(named, ValuePosition.Indirect, where, ref reader);
            }

            // Anything else below the manifest base is a Type value: the writer records the concrete
            // runtime type of a polymorphic value as one. A terminal tag is a scalar, written inline
            // behind the marker this reader has already consumed; a tag that describes a payload is
            // interned as a synthesized entry and read through the very same path a declared member of
            // that shape takes.
            var shape = _typeValues.Decode(typeId, ref reader);

            if (!shape.DescribesPayload)
                return Scalar(shape.DefinitionId, ReadScalarById(shape.DefinitionId, ref reader));

            return ReadUnmarkedPayload(_typeValues.InternRoot(shape), ValuePosition.Indirect, where, ref reader);
        }

        /// <summary>
        /// Decodes a <c>System.Type</c> value: the writer's recursive grammar, through
        /// <see cref="TypeValueShapes"/>, so a nested shape is read by the same decoder in both the
        /// member position and the polymorphic position.
        /// </summary>
        OldValue ReadTypeValueNode(string? where, ref SequenceReader<byte> reader)
        {
            int tag = SchemaSection.ReadTypeIdLeb128(ref reader, "a System.Type value's tag");

            if (tag == WellKnownTypes.Null)
                return new OldNullNode(OldNullReason.TypeValueNull);

            if (tag == FullNameMarker)
            {
                // The tag is a string, not an identity, and the graph records identities. No Ceras writer
                // emits it: RobustTypeFormatter refuses a type its own manifest does not describe, and a
                // full-name tag exists only on the read path. The name is named in the refusal so it is
                // not lost from a diagnostic, and the refusal is a corruption report because nothing a
                // Ceras Save writes can produce these bytes.
                string name = StringTable.ReadString(ref reader, _strings) ?? "(the null sentinel)";
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {At(where)} is a System.Type value that names its type by full name ('{name}') rather than by an " +
                    "identity. The old-data graph records a type as the identity the file gave it, and a name is not one; the node model " +
                    "has no kind for a name-only type value. No Ceras writer emits this tag — a type its own manifest does not describe is " +
                    "refused at write time — so the field is damaged or the file was not assembled by a Ceras Save.");
            }

            var shape = _typeValues.Decode(tag, ref reader);

            // A terminal keeps the file's own well-known or manifest id; a shape that describes a payload
            // is interned as a synthesized entry, so one structure is one identity even when several
            // values mention it.
            return new OldTypeValueNode(IdentityOf(shape.DescribesPayload ? _typeValues.InternRoot(shape).TypeId : shape.DefinitionId), null);
        }

        // ── Payloads ─────────────────────────────────────────────────────────────────────────

        OldValue ReadObjectNode(OldTypeSchema entry, ref SequenceReader<byte> reader)
        {
            var members = new List<OldMember>(entry.Fields.Length);

            foreach (var field in entry.Fields)
            {
                // One position description per member, reused by the identity and by the value: both
                // refusals name the member and the type that declares it, and building it twice would
                // only risk the two saying different things.
                string where = $"the member '{field.StableName}' of the value the save records as '{entry.StableName}'";

                // The member's identity is recorded before its bytes are touched, so a member whose
                // declared type nothing describes is refused with this member and this declaring type
                // named rather than as a byte count read out of step further along.
                var identity = IdentityOf(field.TypeId, where);

                members.Add(new OldMember(
                    new OldFieldIdentity(field.StableName, field.PreviousNames, identity),
                    ReadValue(field.TypeId, ValuePosition.Member, where, ref reader)));
            }

            return new OldObjectNode(IdentityOf(entry.TypeId), members);
        }

        /// <summary>
        /// Reads one framed custom payload: the declared length, then exactly that many bytes, recorded as
        /// they were written and not looked at.
        /// <para>
        /// The frame is read through <see cref="OpaquePayload"/>, the one place its grammar is stated, so
        /// the bounds and truncation refusals are the format's own and not a second wording of them — and
        /// the reader is left positioned at the first byte after the block whatever this method then does
        /// with it, which is what keeps every value after a frame decodable. The block is a window onto
        /// the caller's save buffer, which the graph outlives, so the node copies it rather than aliasing
        /// it; this hands over the read-only slice and lets the node do the copying, which keeps that to
        /// one copy instead of two.
        /// </para>
        /// <para>
        /// <b>What is not asked here, deliberately.</b> No formatter is resolved and none is called, and no
        /// name is matched against this build's: the file records which formatter wrote the block, and
        /// whether this build could read it back is a question for a build that has a current model in
        /// hand, asked there and refused there by name. This decoder holds the bytes as the file wrote
        /// them, which is the whole of what the file can be asked.
        /// </para>
        /// <para>
        /// Whether a marker stood in front of the frame is not decided here either. It was decided before
        /// this call, from the entry's own recorded flag
        /// (<see cref="PayloadGrammar.FileCarriesReferenceMarker"/>), so a value written inline arrives
        /// with no marker in front of it and one written behind a reference marker has already had its
        /// slot reserved — the node this returns fills that reserved arena slot exactly as any other
        /// marked payload's does.
        /// </para>
        /// </summary>
        OldValue ReadOpaqueNode(OldTypeSchema entry, string? where, ref SequenceReader<byte> reader)
        {
            string position = DescribeOpaquePosition(entry, where);

            // The section's own defect, asked before a single payload byte is read. The node refuses a
            // nameless formatter too, because a caller building a node by hand deserves to hear about it;
            // here it is the file's own description that is damaged, so it is corruption here and not an
            // argument error — one sentence, because the node's own wording is the long one and this door
            // only has to say what is wrong with the file.
            if (entry.OpaqueFormatterName.Length == 0)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: the schema section records no formatter name for {position}, so the block belongs to no " +
                    "formatter and cannot be recorded. Every writer interns the canonical CLR name of the formatter that wrote the value, " +
                    "and no formatter has an empty name, so the section is damaged or was not assembled by a Ceras Save.");

            // The block is handed over as the read-only slice it already is, and the node copies out of it:
            // one copy rather than two, and nothing on the way hands out a writable view of the file's
            // bytes. An editor that rewrites the file after the graph was built cannot reach the value.
            var block = OpaquePayload.ReadBlock(ref reader, position, out int declaredLength);

            return new OldOpaqueNode(
                IdentityOf(entry.TypeId, where),
                entry.OpaqueFormatterName,
                entry.OpaqueValueWrittenInline,
                declaredLength,
                block.Sequence);
        }

        /// <summary>
        /// How a refusal about one frame names it: where the value sits, and which formatter the file
        /// recorded as having written it. The same two facts the interpreter names, so a reader of either
        /// refusal is told the same things about the same value.
        /// </summary>
        static string DescribeOpaquePosition(OldTypeSchema entry, string? where)
            => $"the value the save records as '{entry.StableName}' (a custom formatter's payload, written by '{entry.OpaqueFormatterName}')"
             + (where == null ? ", which is the save's root" : $", at {where}");

        OldValue ReadEnumNode(OldTypeSchema entry, ref SequenceReader<byte> reader)
        {
            // The entry's definition id is an enum's *underlying* type id, and 0 is not an id a payload
            // can carry, so a file that recorded none reads back as Int32 — the reader's own default,
            // stated here rather than inferred.
            int underlying = entry.DefinitionTypeId == 0 ? WellKnownTypes.Int32 : entry.DefinitionTypeId;
            return new OldEnumValueNode(IdentityOf(entry.TypeId), ReadUnderlyingBits(underlying, ref reader), underlying);
        }

        OldValue ReadElements(OldTypeSchema entry, string? where, ref SequenceReader<byte> reader)
        {
            int count = PayloadGrammar.ReadCollectionCount(entry.DefinitionTypeId, _maxCollectionSize, ref reader);

            var identity      = IdentityOf(entry.TypeId, where);
            int elementTypeId = PayloadGrammar.ArgId(entry, 0)!.Value;
            var elementType   = IdentityOf(elementTypeId, where);

            if (count == Protocol.CollectionNull)
                return new OldCollectionNode(identity, count, Array.Empty<OldValue>(), elementType);

            // One position description for the whole collection rather than one per element: it names
            // which value the element sits in, which is what a refusal has to say, and the elements are
            // otherwise all at the same place.
            string elementWhere = $"an element of the value the save records as '{entry.StableName}'";

            var elements = new List<OldValue>(count);
            for (int i = 0; i < count; i++)
                elements.Add(ReadValue(elementTypeId, ValuePosition.Indirect, elementWhere, ref reader));

            return new OldCollectionNode(identity, count, elements, elementType);
        }

        OldValue ReadEntries(OldTypeSchema entry, string? where, ref SequenceReader<byte> reader)
        {
            int count = PayloadGrammar.ReadCollectionCount(entry.DefinitionTypeId, _maxCollectionSize, ref reader);

            var identity     = IdentityOf(entry.TypeId, where);
            int keyTypeId    = PayloadGrammar.ArgId(entry, 0)!.Value;
            int valueTypeId  = PayloadGrammar.ArgId(entry, 1)!.Value;
            var keyType      = IdentityOf(keyTypeId, where);
            var valueType    = IdentityOf(valueTypeId, where);

            if (count == Protocol.CollectionNull)
                return new OldCollectionNode(identity, count, Array.Empty<OldValue>(), keyType,
                    Array.Empty<OldValue>(), Array.Empty<OldValue>(), keyType, valueType);

            string keyWhere   = $"a key of the dictionary the save records as '{entry.StableName}'";
            string valueWhere = $"a value of the dictionary the save records as '{entry.StableName}'";

            // Interleaved on the wire, and positional in the graph: a duplicate-key collision stays
            // visible because the two lists line up by position rather than being collapsed into a map.
            var keys   = new List<OldValue>(count);
            var values = new List<OldValue>(count);
            for (int i = 0; i < count; i++)
            {
                keys.Add(ReadValue(keyTypeId, ValuePosition.Indirect, keyWhere, ref reader));
                values.Add(ReadValue(valueTypeId, ValuePosition.Indirect, valueWhere, ref reader));
            }

            return new OldCollectionNode(identity, count, Array.Empty<OldValue>(), keyType, keys, values, keyType, valueType);
        }

        /// <summary>
        /// The structural invariant for one described instantiation, asked here so this door is safe for a
        /// caller that handed over a view the section parser never saw. The refusals are the shared
        /// wording's, so a caller can catch "this file's description is unreadable" without caring which
        /// door found it; the <em>type</em> is a corruption report because the file's own bytes contradict
        /// the file's own section and the header checksum has already verified them.
        /// </summary>
        void RequireDescribable(OldTypeSchema entry, string? where)
        {
            var verdict = StructuralShape.InspectShape(entry);
            if (!verdict.IsDefect) verdict = StructuralShape.InspectDefinition(entry, _file.ContainsById);
            if (!verdict.IsDefect) verdict = StructuralShape.InspectArgumentIdentities(entry, _file.ContainsById);
            if (!verdict.IsDefect) return;

            throw new CerasDataCorruptionException(
                $"Corrupt save: {At(where)} is described by a schema entry this decoder cannot read. " +
                StructuralShape.Describe(entry, verdict) + " " + StructuralShape.WhyUnreadable +
                $" {WhereItCameFrom(where)}");
        }

        static string WhereItCameFrom(string? where) => where == null
            ? "It is the save's root value."
            : "No Ceras writer records such an entry — a real one always carries its definition's arguments — so the section is damaged " +
              "or was assembled by something other than a Ceras Save. The header checksum already matched, so this is not bit rot.";

        /// <summary>
        /// An enum's raw bits, read with its own underlying type's width. The bits are what the file
        /// recorded and nothing interprets them here: which current member they name is a later question,
        /// and a value above <see cref="long.MaxValue"/> has to survive, so the read widens the signed
        /// forms rather than going through <see cref="long"/>.
        /// </summary>
        ulong ReadUnderlyingBits(int underlyingId, ref SequenceReader<byte> reader)
        {
            switch (underlyingId)
            {
                case WellKnownTypes.Byte:   return SerializerBinary.ReadByte(ref reader);
                case WellKnownTypes.SByte:  return unchecked((ulong)(sbyte)SerializerBinary.ReadByte(ref reader));
                case WellKnownTypes.Char:   return SerializerBinary.ReadUInt16Fixed(ref reader);
                case WellKnownTypes.Int16:  return unchecked((ulong)SerializerBinary.ReadInt16Fixed(ref reader));
                case WellKnownTypes.UInt16: return SerializerBinary.ReadUInt16Fixed(ref reader);
                case WellKnownTypes.Int32:  return unchecked((ulong)(long)SerializerBinary.ReadInt32Fixed(ref reader));
                case WellKnownTypes.UInt32: return SerializerBinary.ReadUInt32Fixed(ref reader);
                case WellKnownTypes.Int64:  return unchecked((ulong)SerializerBinary.ReadInt64Fixed(ref reader));
                case WellKnownTypes.UInt64: return SerializerBinary.ReadUInt64Fixed(ref reader);
            }

            throw new CerasDataCorruptionException(
                $"Corrupt save: an enum value declares underlying type id {underlyingId}, which is not one of the eight integral types a " +
                "C# enum may be declared over (System.Byte, System.SByte, System.Int16, System.UInt16, System.Int32, System.UInt32, " +
                "System.Int64, System.UInt64) or System.Char. Nothing says how many bytes such a value occupies, so it is refused rather " +
                "than read at a width this decoder had to guess.");
        }

        object ReadScalarById(int typeId, ref SequenceReader<byte> reader)
        {
            switch (typeId)
            {
                case WellKnownTypes.Bool:           return ReadBool(ref reader);
                case WellKnownTypes.Byte:           return SerializerBinary.ReadByte(ref reader);
                case WellKnownTypes.SByte:          return unchecked((sbyte)SerializerBinary.ReadByte(ref reader));
                case WellKnownTypes.Char:           return (char)SerializerBinary.ReadInt16Fixed(ref reader);
                case WellKnownTypes.Int16:          return SerializerBinary.ReadInt16Fixed(ref reader);
                case WellKnownTypes.UInt16:         return SerializerBinary.ReadUInt16Fixed(ref reader);
                case WellKnownTypes.Int32:          return SerializerBinary.ReadInt32Fixed(ref reader);
                case WellKnownTypes.UInt32:         return SerializerBinary.ReadUInt32Fixed(ref reader);
                case WellKnownTypes.Single:         return SerializerBinary.ReadFloat32Fixed(ref reader);
                case WellKnownTypes.Int64:          return SerializerBinary.ReadInt64Fixed(ref reader);
                case WellKnownTypes.UInt64:         return SerializerBinary.ReadUInt64Fixed(ref reader);
                case WellKnownTypes.Double:         return SerializerBinary.ReadDouble64Fixed(ref reader);
                case WellKnownTypes.Decimal:        return SerializerBinary.ReadDecimal(ref reader);
                case WellKnownTypes.DateTime:       return SerializerBinary.ReadDateTime(ref reader);
                case WellKnownTypes.TimeSpan:       return new TimeSpan(SerializerBinary.ReadInt64Fixed(ref reader));
                case WellKnownTypes.DateTimeOffset: return SerializerBinary.ReadDateTimeOffset(ref reader);
                case WellKnownTypes.Guid:           return SerializerBinary.ReadGuid(ref reader);
                case WellKnownTypes.String:         return StringTable.ReadString(ref reader, _strings);
            }

            throw new CerasDataCorruptionException(
                $"Corrupt save: a polymorphic value names type id {typeId}, which is neither one of the format's fixed-layout scalar " +
                $"types (1-{WellKnownTypes.Decimal}, {WellKnownTypes.String}, {WellKnownTypes.DateTime}-{WellKnownTypes.DateTimeOffset}) " +
                "nor one this decoder can read a payload for.");
        }

        // ── Identities ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The identity the graph records for a type id: the file's own spelling, its own rename
        /// aliases, its own kind and, for a closed instantiation, its own ordered argument identities.
        /// Memoized, so one identity is one object however many values mention it — an identity is a
        /// value and not a key, and which identities are <c>Equals</c> is not what this is.
        /// </summary>
        OldTypeIdentity IdentityOf(int typeId, string? where = null) => IdentityOf(typeId, 0, where);

        OldTypeIdentity IdentityOf(int typeId, int depth, string? where)
        {
            if (_identities.TryGetValue(typeId, out var memoized)) return memoized;

            if (typeId == WellKnownTypes.Null)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {At(where)} is described by type id 0, which is the format's null type and names no identity.");

            if (typeId < WellKnownTypes.UserTypeBase)
            {
                if (!WellKnownTypes.TryGetType(typeId, out var wellKnown))
                    throw new CerasDataCorruptionException(
                        $"Corrupt save: {At(where)} is described by type id {typeId}, which is in the well-known range but has no type " +
                        $"assigned to it. Only 0-{WellKnownTypes.DateTimeOffset} are assigned and {WellKnownTypes.UserTypeBase} and up are " +
                        "manifest ids; 43-126 are reserved and unassigned. No Ceras writer emits such an id.");

                // The name comes from the reserved table, which is the format's own id space and not this
                // build's model: an id below the manifest base stands for a BCL shape the writer encoded
                // and this reader decodes, and the application reading the file is not consulted. A
                // polymorphic slot is a class and a fixed-layout value is a struct, which is the same
                // answer the file's own entries give for the shapes it describes.
                var wellKnownIdentity = new OldTypeIdentity(
                    typeId,
                    TypeIdentity.GetCanonicalName(wellKnown),
                    null,
                    typeId is WellKnownTypes.Object or WellKnownTypes.Array
                        ? SchemaTypeKind.Class
                        : SchemaTypeKind.Struct);

                _identities[typeId] = wellKnownIdentity;
                return wellKnownIdentity;
            }

            if (!_file.TryGetById(typeId, out var entry))
                throw UndescribedIdentity(typeId, where);

            // A closed identity is a tree of the file's own ids, and a section that names an entry as its
            // own argument would otherwise be recursed into forever. The depth is bounded rather than a
            // cycle detected, so one rule covers every way a section can contradict itself, and the bound
            // is the format's own for the same structure in a Type value.
            if (depth > MaxIdentityDepth)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: {At(where)} is described by a type identity that nests more than {MaxIdentityDepth} levels deep " +
                    $"(the innermost is type id {typeId}). A schema entry whose argument is itself reaches that depth, and the format " +
                    "bounds the same nesting for a Type value, so no writer recorded this section.");

            OldTypeIdentity identity;
            if (entry.Kind == SchemaTypeKind.GenericInstantiation)
            {
                var arguments = new List<OldTypeIdentity>(entry.ArgTypeIds.Length);
                foreach (int argumentId in entry.ArgTypeIds)
                    arguments.Add(IdentityOf(argumentId, depth + 1, where));

                identity = new OldTypeIdentity(entry.TypeId, entry.StableName, entry.PreviousNames, entry.Kind, entry.DefinitionTypeId, arguments);
            }
            else
            {
                // For an enum the entry's definition id is its *underlying* type and for every other kind
                // it is 0; both are the file's own record and are carried verbatim.
                identity = new OldTypeIdentity(entry.TypeId, entry.StableName, entry.PreviousNames, entry.Kind, entry.DefinitionTypeId);
            }

            _identities[typeId] = identity;
            return identity;
        }

        OldTypeSchema RequireEntry(int typeId, string? where)
        {
            if (_file.TryGetById(typeId, out var entry)) return entry;
            throw UndescribedIdentity(typeId, where);
        }

        CerasDataCorruptionException UndescribedIdentity(int typeId, string? where)
            => new CerasDataCorruptionException(
                $"Corrupt save: {At(where)} is described by type id {typeId}, which is neither one of the format's well-known types " +
                $"(0-{WellKnownTypes.DateTimeOffset}) nor an entry in the save's own schema section, and a manifest id always comes from " +
                "the writer's own manifest and is always described there. Nothing says how such a value is written, so a reader would have " +
                "to assume it takes no bytes and every value after it would decode from the wrong offset; it is refused instead. A type the " +
                "application has since removed is not this case: the file's own section describes a value by id whatever this build can " +
                "name.");


        // ── The shapes this graph still has no node kind for ─────────────────────────────────

        // A framed custom payload is not among them any more: it has a node kind of its own
        // (OldOpaqueNode) and is read above. The three shapes left are the ones the file records no name
        // for at all, which is a different problem — there is nothing in the file to turn into a member
        // list, and nothing in it to hold the bytes either.
        CerasSchemaException RefuseStructuralValueType(OldTypeSchema entry, string? where, string shape, string payload)
            => new CerasSchemaException(
                $"Cannot build an old-data graph for the value the save records as '{entry.StableName}': the file describes it as " +
                $"{shape}, whose payload is {payload}, and the graph has no node kind for that shape yet. {At(where)}. It is deliberately " +
                "not recorded as a member list, because the file records no member name for any of it — a tuple's items and a Nullable's " +
                "value are positions, not names — and inventing one would put a member list into the file's own record that the writer " +
                "never wrote. And it is not folded into the value it holds, because a null and a present value are two different recorded " +
                "facts: for a Nullable the presence byte is the only thing that says which one the file recorded.");

        static string At(string? where) => where ?? "It is the save's root value.";
    }
}
