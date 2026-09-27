using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Ceras.Exceptions;

namespace Ceras.Format
{
    /// <summary>
    /// One node of a decoded <c>Type</c> value: the definition id the file named, the ordered argument
    /// nodes it was written with, and the current CLR type that pair resolves to (null when this build
    /// cannot resolve it, which is not an error — the payload stays fully describable without it).
    /// </summary>
    /// <remarks>
    /// A <c>Type</c> value is the writer's record of the concrete runtime type of a polymorphic value
    /// (<see cref="RobustTypeFormatter"/>), and it is <b>recursive</b>: an array is written as
    /// <c>Array</c> followed by its element type, and a closed built-in generic as its definition id
    /// followed by its argument count and then each argument — each of which is itself a complete
    /// <c>Type</c> value. <c>List&lt;List&lt;int&gt;&gt;</c> is therefore
    /// <c>List, 1, List, 1, Int32</c> and <c>int[][]</c> is <c>Array, Array, Int32</c>.
    /// <para>
    /// Decoding that is unambiguous rather than a guess: ids 0–127 are permanently reserved for
    /// well-known types and a manifest id never falls in that range, so a single tag says which of the
    /// cases it is, and one recursive walk produces the whole tree.
    /// </para>
    /// </remarks>
    sealed class TypeShape
    {
        static readonly TypeShape[] NoArguments = Array.Empty<TypeShape>();

        /// <summary>
        /// The id the file named: a well-known id (a scalar, a collection/array/nullable/tuple definition,
        /// or <see cref="WellKnownTypes.Null"/>) or a manifest type id (>= 128).
        /// </summary>
        public int DefinitionId { get; }

        /// <summary>The ordered argument type values. Empty for a terminal tag (a scalar, a manifest id).</summary>
        public TypeShape[] Arguments { get; }

        /// <summary>
        /// The current CLR type this node names, or null when this build does not resolve it (a type the
        /// application has since removed, or one whose own arguments are unresolvable). Never null merely
        /// because a <em>sibling</em> argument failed to resolve — only what this node needs decides it.
        /// </summary>
        public Type? ClrType { get; }

        public TypeShape(int definitionId, TypeShape[] arguments, Type? clrType)
        {
            DefinitionId = definitionId;
            Arguments = arguments ?? NoArguments;
            ClrType = clrType;
        }

        public static TypeShape Leaf(int definitionId, Type? clrType) => new TypeShape(definitionId, NoArguments, clrType);

        /// <summary>
        /// True when the tag names a payload this engine can describe without decoding anything else: a
        /// collection, an array, a <c>Nullable</c>, a <c>KeyValuePair</c> or a tuple. Such a value carries
        /// a <em>payload</em> after its type value; every other tag is a leaf whose payload the ordinary
        /// per-id walk already covers.
        /// </summary>
        public bool DescribesPayload => Arguments.Length != 0;
    }

    /// <summary>
    /// Decodes the writer's recursive <c>Type</c> value grammar and turns it into schema entries the
    /// migration interpreter already knows how to read and to skip.
    /// <para>
    /// What keeps read and skip agreeing — and keeps every existing reader correct without a second
    /// implementation — is that a nested described type value is interned as a
    /// <c>GenericInstantiation</c> entry with a definition id and ordered argument ids: <b>exactly</b> the
    /// shape the file's own schema section uses for a nested collection member, and exactly the shape
    /// <c>ClassifyPayload</c>, <c>ReadGenericInstantiation</c> and <c>SkipCollectionPayload</c> consume.
    /// <c>List&lt;List&lt;int&gt;&gt;</c> in a polymorphic slot therefore becomes one synthesized entry
    /// (definition <c>List</c>, argument = another synthesized entry for <c>List&lt;int&gt;</c>), and the
    /// element loop reads the inner one through the very same path a declared
    /// <c>List&lt;List&lt;int&gt;&gt;</c> member takes — including its reference marker, its element-count
    /// encoding and the configured collection-size limit.
    /// </para>
    /// <para>
    /// The interning is by <em>value</em>, not by occurrence: two type values that decode to the same
    /// structure share one entry, so the retained cost is proportional to the number of distinct nested
    /// shapes in the save rather than to the number of values that mention them.
    /// </para>
    /// <para>
    /// Nothing here writes: the writer's bytes are untouched, and this only has to agree with what
    /// <see cref="RobustTypeFormatter"/> already emits.
    /// </para>
    /// </summary>
    sealed class TypeValueShapes
    {
        /// <summary>
        /// Where synthesized type-value entries start. Manifest ids are handed out from
        /// <see cref="WellKnownTypes.UserTypeBase"/> upwards and a manifest with sixteen million entries is
        /// not a thing that exists, so a synthesized id cannot be confused with a real one — and
        /// <see cref="AllocateId"/> checks for a collision anyway rather than trusting that argument.
        /// </summary>
        public const int FirstSynthesizedId = 1 << 24;

        /// <summary>
        /// Most nodes one <c>Type</c> value may contain. Real code nests two or three deep; the bound keeps a
        /// crafted file from turning one small tag into unbounded work.
        /// </summary>
        const int MaxNodesPerTypeValue = 256;

        /// <summary>
        /// Most levels of nesting. Each level costs at least two bytes, so without a bound a large save
        /// could nest deep enough to exhaust the stack while a single type value is decoded.
        /// </summary>
        const int MaxNestingDepth = 64;

        /// <summary>
        /// Most arguments any of the well-known generic definitions can take. The largest is the 7-argument
        /// tuple family, so this is a tight bound rather than a guess — and a tight one matters, because the
        /// count sizes an array before a single argument is read.
        /// </summary>
        const int MaxDefinitionArity = 8;

        /// <summary>
        /// Most <em>distinct</em> nested type identities one load may synthesize. Occurrences do not count:
        /// a save holding ten thousand <c>List&lt;int&gt;</c> values in object slots has exactly one of them,
        /// because equal type values share an entry. What remains is bounded by the number of distinct
        /// closed built-in shapes the file's manifest can name, so this guards against a crafted file
        /// rather than limiting a real save.
        /// </summary>
        const int MaxDistinctShapes = 1 << 16;

        const int FullNameMarker = 127;

        readonly FileSchemaView _file;
        readonly Func<int, Type?> _resolveManifestType;

        // Keyed by the decoded structure, so a repeated value costs a dictionary lookup and nothing more.
        // All of it is per-load state — the engine that owns this object is created by a single Load and
        // becomes garbage with it, so nothing is retained across operations.
        readonly Dictionary<ShapeKey, OldTypeSchema> _interned = new();

        int _nextSynthesizedId = FirstSynthesizedId;
        int _nodes;

        public TypeValueShapes(FileSchemaView file, Func<int, Type?> resolveManifestType)
        {
            _file = file;
            _resolveManifestType = resolveManifestType;
        }

        /// <summary>The number of distinct nested identities interned so far: a test and a diagnostic hook.</summary>
        internal int InternedCount => _interned.Count;

        /// <summary>
        /// Decodes one <c>Type</c> value whose leading tag has already been consumed, recursively.
        /// <para>
        /// The cases a tag can be, all decided by the tag itself:
        /// <list type="bullet">
        /// <item>a manifest id (>= 128) or a well-known scalar: terminal, nothing follows;</item>
        /// <item><c>Array</c> (16): exactly one nested <c>Type</c> value, the element type;</item>
        /// <item>a well-known generic definition (the collections, <c>Nullable</c>, <c>KeyValuePair</c>,
        /// the tuple families): an argument count, then that many nested <c>Type</c> values;</item>
        /// <item>a reserved but unassigned well-known id (43–126), which the writer cannot produce: refused.</item>
        /// </list>
        /// A <c>Null</c> tag is terminal and legal as a whole value, but never as an argument or an element
        /// type. A <c>FullNameMarker</c> (127) is left to the caller, because only a top-level reference may be
        /// resolved through the binder: a full-name <em>argument</em> has no id, and the engine has no payload
        /// grammar for a type it can only name, so it is refused rather than guessed at.
        /// </para>
        /// </summary>
        public TypeShape Decode(int tag, ref SequenceReader<byte> reader)
        {
            // A null *Type value* is legal, and is exactly what RobustTypeFormatter writes for a null
            // reference. A null nested anywhere else is not, because a generic argument and an array element
            // type are not optional - so DecodeCore refuses it and it is admitted here, at the root only.
            if (tag == WellKnownTypes.Null) return TypeShape.Leaf(WellKnownTypes.Null, null);

            int outerNodes = _nodes;
            _nodes = 0;
            try
            {
                return DecodeCore(tag, ref reader, 0);
            }
            finally
            {
                _nodes = outerNodes;
            }
        }

        TypeShape DecodeCore(int tag, ref SequenceReader<byte> reader, int depth)
        {
            if (depth > MaxNestingDepth)
                throw new CerasDataCorruptionException(
                    $"Corrupt System.Type payload: type values nest more than {MaxNestingDepth} levels deep.");

            if (++_nodes > MaxNodesPerTypeValue)
                throw new CerasDataCorruptionException(
                    $"Corrupt System.Type payload: one type value names more than {MaxNodesPerTypeValue} types.");

            if (tag == FullNameMarker)
                throw new CerasDataCorruptionException(
                    "Corrupt System.Type payload: a type is named by full name where a type identity is needed (a " +
                    "generic argument or an array element type). A name is not an identity this reader can account " +
                    "for a payload with.");

            if (tag == WellKnownTypes.Null)
                throw new CerasDataCorruptionException(
                    "Corrupt System.Type payload: null is not a type, so it cannot be a generic argument or an element type.");

            if (tag >= WellKnownTypes.UserTypeBase)
                return TypeShape.Leaf(tag, _resolveManifestType(tag));

            if (tag == WellKnownTypes.Array)
            {
                int elementTag = SchemaSection.ReadTypeIdLeb128(ref reader, "an array type's element type");
                if (elementTag == WellKnownTypes.Null)
                    throw new CerasDataCorruptionException("Corrupt System.Type payload: an array type has no element type.");
                var element = DecodeCore(elementTag, ref reader, depth + 1);
                return new TypeShape(WellKnownTypes.Array, new[] { element }, MakeArrayOf(element));
            }

            if (WellKnownTypes.TryGetType(tag, out var definition) && definition.IsGenericTypeDefinition)
            {
                int argCount = ReadBoundedArgCount(ref reader, definition.Name);
                var args = new TypeShape[argCount];
                for (int i = 0; i < argCount; i++)
                {
                    int argTag = SchemaSection.ReadTypeIdLeb128(ref reader, $"generic argument {i} of '{definition.Name}'");
                    if (argTag == WellKnownTypes.Null)
                        throw new CerasDataCorruptionException(
                            $"Corrupt System.Type payload: generic argument {i} of '{definition.Name}' is null.");
                    args[i] = DecodeCore(argTag, ref reader, depth + 1);
                }
                return new TypeShape(tag, args, MakeGeneric(definition, args));
            }

            // 1-42 minus the cases above: a scalar well-known type. Its payload is a fixed layout the
            // per-id walk already reads and skips, so nothing follows the tag.
            if (tag <= WellKnownTypes.DateTimeOffset)
                return TypeShape.Leaf(tag, WellKnownTypes.TryGetType(tag, out var scalar) ? scalar : null);

            throw new CerasDataCorruptionException(
                $"Corrupt System.Type payload: type id {tag} is in the well-known range but no type is assigned to it " +
                $"(only 0-{WellKnownTypes.DateTimeOffset} are assigned, and {WellKnownTypes.UserTypeBase} and up are " +
                "manifest ids). No Ceras writer emits this.");
        }

        /// <summary>
        /// The id to use for a decoded shape when it appears as an <em>argument</em> of another one, or the
        /// payload root of a polymorphic value. A terminal shape keeps the file's own id — a well-known
        /// scalar id, or the manifest id of a user type, which the file's schema section already describes —
        /// so only a described shape needs an entry.
        /// <para>
        /// Equal shapes share one entry. That is not only cheaper: the shape doubles as the engine's
        /// identity for the payload, so two equal type values must not be two identities.
        /// </para>
        /// </summary>
        public int InternId(TypeShape shape)
        {
            if (!shape.DescribesPayload) return TerminalId(shape);

            var key = KeyOf(shape);
            return _interned.TryGetValue(key, out var existing) ? existing.TypeId : Intern(shape, key).TypeId;
        }

        /// <summary>
        /// Interns a decoded shape as the schema entry its payload is read and skipped through. The shape
        /// must describe a payload; a terminal tag is a leaf the ordinary per-id walk already handles.
        /// </summary>
        public OldTypeSchema InternRoot(TypeShape shape)
        {
            if (!shape.DescribesPayload)
                throw new CerasSchemaException(
                    $"Internal error: type value id {shape.DefinitionId} does not describe a payload, so there is no " +
                    "schema entry to read it through.");

            var key = KeyOf(shape);
            if (_interned.TryGetValue(key, out var existing)) return existing;
            return Intern(shape, key);
        }

        OldTypeSchema Intern(TypeShape shape, ShapeKey key)
        {
            var argIds = new int[shape.Arguments.Length];
            for (int i = 0; i < argIds.Length; i++) argIds[i] = InternId(shape.Arguments[i]);

            int id = AllocateId();
            var schema = new OldTypeSchema(
                id,
                Describe(shape),
                Array.Empty<string>(),
                SchemaTypeKind.GenericInstantiation,
                Array.Empty<OldFieldSchema>(),
                0,
                shape.DefinitionId,
                argIds);
            _file.AddTypeValue(schema, shape.ClrType);
            _interned.Add(key, schema);
            return schema;
        }

        int TerminalId(TypeShape shape)
        {
            // A terminal argument is a manifest id, and the file's own schema section is the only thing that
            // says how such a value's payload is written. An id the section does not contain has no
            // describable payload, so it is refused here rather than read as a value of no bytes - which
            // would put every following field out of step without failing.
            if (shape.DefinitionId >= WellKnownTypes.UserTypeBase && !_file.ContainsById(shape.DefinitionId))
                throw new CerasSchemaException(
                    $"Corrupt save: a polymorphic value names type id {shape.DefinitionId} as a nested type, but the save's own " +
                    "schema section does not describe it, so nothing says how its payload is written. A type id always comes from the " +
                    "writer's manifest and is always described there.");

            return shape.DefinitionId;
        }

        static ShapeKey KeyOf(TypeShape shape)
        {
            var arguments = new ShapeKey[shape.Arguments.Length];
            for (int i = 0; i < arguments.Length; i++) arguments[i] = KeyOf(shape.Arguments[i]);
            return new ShapeKey(shape.DefinitionId, arguments);
        }

        int AllocateId()
        {
            // The whole design rests on a synthesized id never colliding with a file id, so the check is
            // made rather than assumed, even though FirstSynthesizedId leaves a very wide margin.
            int id = _nextSynthesizedId++;
            if (_file.ContainsById(id))
                throw new CerasSchemaException(
                    $"Internal error: the type-value identity {id} collides with a type id the save already uses.");

            if (_interned.Count >= MaxDistinctShapes)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: the polymorphic values in it name more than {MaxDistinctShapes} distinct nested type identities.");

            return id;
        }

        /// <summary>
        /// A readable name for a synthesized entry, in the same <c>Definition&lt;arg,…&gt;</c> shape the
        /// file's own stable names use, so a diagnostic that prints an entry's name says what the value was.
        /// It is deliberately not a resolvable stable name: no such type exists, and the entry is keyed by id
        /// only.
        /// </summary>
        static string Describe(TypeShape shape)
        {
            var sb = new StringBuilder("<type-value:");
            sb.Append(DescribeNode(shape));
            foreach (var arg in shape.Arguments) sb.Append(',').Append(DescribeNode(arg));
            return sb.Append('>').ToString();
        }

        static string DescribeNode(TypeShape shape)
        {
            if (shape.ClrType != null) return TypeIdentity.GetCanonicalName(shape.ClrType);
            if (WellKnownTypes.TryGetType(shape.DefinitionId, out var wellKnown)) return wellKnown.Name;
            return $"id:{shape.DefinitionId}";
        }

        static Type? MakeArrayOf(TypeShape element)
        {
            var elementType = element.ClrType;
            if (elementType == null) return null;
            try
            {
                return elementType.MakeArrayType();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or TypeLoadException)
            {
                throw new CerasDataCorruptionException(
                    $"Corrupt System.Type payload: 'array of {TypeIdentity.GetCanonicalName(elementType)}' does not name a " +
                    $"type this runtime can construct ({e.Message}).", e);
            }
        }

        static Type? MakeGeneric(Type definition, TypeShape[] args)
        {
            var argTypes = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) argTypes[i] = args[i].ClrType!;
            if (Array.IndexOf(argTypes, null) >= 0) return null;   // an argument this build cannot name

            try
            {
                return definition.MakeGenericType(argTypes);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or TypeLoadException)
            {
                var names = new string[argTypes.Length];
                for (int i = 0; i < argTypes.Length; i++) names[i] = TypeIdentity.GetCanonicalName(argTypes[i]);
                throw new CerasDataCorruptionException(
                    $"Corrupt System.Type payload: '{definition.Name}<{string.Join(", ", names)}>' does not name a type this " +
                    $"runtime can construct ({e.Message}).", e);
            }
        }

        /// <summary>
        /// The CLR type a <c>Type</c> value names, for the places that decode a <see cref="Type"/> <em>value</em>
        /// rather than a polymorphic payload. A manifest id this build does not resolve yields null, as it
        /// always has: the value is a type the application has removed, and a missing type is not corruption.
        /// </summary>
        public Type? Materialize(TypeShape shape)
        {
            if (!shape.DescribesPayload)
            {
                if (shape.DefinitionId == WellKnownTypes.Null) return null;
                if (WellKnownTypes.TryGetType(shape.DefinitionId, out var wellKnown)) return wellKnown;
                return _resolveManifestType(shape.DefinitionId);
            }

            if (shape.ClrType != null) return shape.ClrType;

            // The shape described a payload but this build cannot name the type. Say which part is missing:
            // a bare null would be indistinguishable from the value really being null.
            for (int i = 0; i < shape.Arguments.Length; i++)
            {
                if (shape.Arguments[i].ClrType == null && !shape.Arguments[i].DescribesPayload)
                    throw new CerasDataCorruptionException(
                        $"Corrupt System.Type payload: argument {i} names type id {shape.Arguments[i].DefinitionId}, " +
                        "which this build does not resolve.");
            }

            return null;
        }

        static int ReadBoundedArgCount(ref SequenceReader<byte> reader, string definitionName)
        {
            long remaining = reader.Sequence.Length - reader.Consumed;
            int argCount = SchemaSection.ReadTypeIdLeb128(ref reader, $"the argument count of '{definitionName}'");

            // Every argument costs at least one byte, so the bytes that remain bound the count; a larger
            // value is a corrupt field, not a real type. The definition's own arity bounds it far more
            // tightly, and it is checked first because this count sizes an array.
            if (argCount > MaxDefinitionArity)
                throw new CerasDataCorruptionException(
                    $"Corrupt System.Type payload: '{definitionName}' declares {argCount} generic arguments. The largest " +
                    $"well-known definition takes {MaxDefinitionArity}.");
            if (argCount > remaining)
                throw new CerasDataCorruptionException(
                    $"Corrupt System.Type payload: '{definitionName}' declares {argCount} generic arguments but only " +
                    $"{remaining} bytes remain.");
            return argCount;
        }

        /// <summary>
        /// The decoded structure of a type value, as a dictionary key. Two type values that describe the
        /// same type produce equal keys — including at every level of nesting, because the key is itself
        /// recursive — and that is what makes one synthesized entry serve both.
        /// </summary>
        readonly struct ShapeKey : IEquatable<ShapeKey>
        {
            readonly int _hash;

            public ShapeKey(int definitionId, ShapeKey[] arguments)
            {
                DefinitionId = definitionId;
                Arguments = arguments;

                // Only this node's own contribution: an argument's own hash is inside its key, so the
                // combination is order-sensitive and depth-sensitive without walking the tree again.
                int hash = definitionId;
                foreach (var argument in arguments)
                    hash = unchecked(hash * 31 + argument.GetHashCode());
                _hash = hash;
            }

            public int DefinitionId { get; }

            public ShapeKey[] Arguments { get; }

            public bool Equals(ShapeKey other)
                => DefinitionId == other.DefinitionId && Arguments.AsSpan().SequenceEqual(other.Arguments);

            public override bool Equals(object? obj) => obj is ShapeKey other && Equals(other);

            public override int GetHashCode() => _hash;
        }
    }
}
