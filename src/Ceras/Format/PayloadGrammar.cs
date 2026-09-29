using System;
using System.Buffers;
using Ceras.Exceptions;

namespace Ceras.Format
{
    /// <summary>
    /// The <b>file-side payload grammar</b>: what a value the <em>file</em> describes is written as,
    /// answered from the file's own record and never from the current CLR model.
    /// <para>
    /// Every member here was lifted out of <see cref="SchemaTraversalEngine"/> unchanged — same code,
    /// same order, same messages — and is now stated once for every reader of a save. The extraction is
    /// deliberately behaviour-preserving: the interpreter calls the same decisions it always did, from
    /// the same inputs, so no byte of any existing save changes and no diagnostic is reworded.
    /// </para>
    /// <para>
    /// <b>Why the grammar is separated from the reader that uses it.</b> Two readers need it and neither
    /// can be derived from the other. The migration interpreter
    /// (<see cref="SchemaTraversalEngine"/>) reads an old value and assigns it to a current member, so it
    /// holds a <em>current model</em> and answers the questions it cannot avoid with it — most visibly
    /// <c>CarriesReferenceMarker</c>, which decides a closed generic's value/reference representation
    /// from the CLR type this build resolves the name to. A file-faithful decoder for the old-data graph
    /// (TODO.md, Milestone 2) holds <b>no current model at all</b>: the type a save names may not exist
    /// in the build reading it, and nothing about the bytes is allowed to depend on what does. Its
    /// grammar has to come from the file's own entries — which is what this class is, and what
    /// <see cref="FileCarriesReferenceMarker"/> adds. One implementation, asked with different inputs, is
    /// what keeps the two readers from describing the same bytes two different ways.
    /// </para>
    /// <para>
    /// The single rule that governs everything here: <b>the writer's layout, not the reader's model</b>. A
    /// rename, a re-assigned id, a changed shape and a type this build no longer has all have to decode by
    /// the layout the writer used. The current CLR type supplies the object to decode into and nothing
    /// else.
    /// </para>
    /// <para>
    /// See <c>Format/LAYOUT.md</c> for the grammars themselves, and for the per-shape element-count table
    /// (<c>Queue&lt;&gt;</c>/<c>Stack&lt;&gt;</c> versus everything else) and the two-region envelope they sit
    /// in.
    /// </para>
    /// </summary>
    internal static class PayloadGrammar
    {
        /// <summary>
        /// What a file's schema entry says the payload of a generic instantiation is, once it is known that
        /// there is no reference marker in front of it. The file's definition id decides this, never the
        /// current CLR type: a rename, a re-assigned id or a changed shape must still decode by the layout
        /// the writer used, and the current type only supplies the CLR types to decode into.
        /// <para>
        /// Written once for both directions. The skipper used to re-derive the same grammar from a different
        /// question — "is the current type a value type?", then "is the definition id a dictionary?" — so a
        /// shape could be read one way and skipped another.
        /// </para>
        /// </summary>
        public enum PayloadShape
        {
            /// <summary>
            /// Not one of the structural shapes below. Two different entries land here and are written the
            /// same way — a user generic's member list and a plain class's — so this is one grammar reached by
            /// two kinds of entry, not a shape the format cannot describe. It is deliberately *not* a count
            /// followed by anything: the skipper once read a member count here, consumed the first member's
            /// bytes as a count and then that many elements' worth, and every value after the type came out
            /// wrong without failing.
            /// </summary>
            Unknown,

            /// <summary>A user custom formatter's payload: a declared length and then that many bytes.</summary>
            Opaque,

            Array,
            KeyValuePair,
            Nullable,
            Tuple,

            /// <summary>
            /// An explicit count followed by that many elements. The count's <em>encoding</em> is a separate
            /// answer — <see cref="UsesFixedElementCount"/> — because a <c>Queue&lt;&gt;</c> and a
            /// <c>List&lt;&gt;</c> are the same grammar written two incompatible ways, and a reader that applies
            /// the wrong one gets a plausible count rather than a failure.
            /// </summary>
            Elements,

            /// <summary>An explicit count followed by that many key/value entries.</summary>
            Entries,
        }

        /// <summary>Classifies a file's generic instantiation. Reads no payload.</summary>
        public static PayloadShape ClassifyPayload(OldTypeSchema old)
        {
            // An opaque entry is not a generic instantiation at all — it has no definition id and no
            // arguments — so it is classified by its kind. Asked first, because the whole point of the
            // payload-shape dispatch is that read and skip share one classifier, and an opaque value's
            // grammar is a declared length, which no definition id in the table describes.
            if (old.Kind == SchemaTypeKind.Opaque) return PayloadShape.Opaque;

            int definition = old.DefinitionTypeId;

            if (definition == WellKnownTypes.Array) return PayloadShape.Array;
            if (definition == WellKnownTypes.KeyValuePair) return PayloadShape.KeyValuePair;
            if (definition == WellKnownTypes.Nullable) return PayloadShape.Nullable;
            if (WellKnownTypes.IsTupleDefinition(definition)) return PayloadShape.Tuple;
            if (definition == WellKnownTypes.Dictionary) return PayloadShape.Entries;
            if (definition == WellKnownTypes.List || definition == WellKnownTypes.HashSet
                || definition == WellKnownTypes.Queue || definition == WellKnownTypes.Stack) return PayloadShape.Elements;

            return PayloadShape.Unknown;
        }

        /// <summary>
        /// True when a file's definition id describes a collection payload: an explicit count followed by
        /// the elements (or key/value entries) of one of the built-in collections.
        /// </summary>
        public static bool IsCollectionPayload(int definitionTypeId)
            => definitionTypeId == WellKnownTypes.List
            || definitionTypeId == WellKnownTypes.Dictionary
            || definitionTypeId == WellKnownTypes.HashSet
            || definitionTypeId == WellKnownTypes.Queue
            || definitionTypeId == WellKnownTypes.Stack;

        /// <summary>The old type's argument id at <paramref name="index"/>, or null when it did not record one.</summary>
        public static int? ArgId(OldTypeSchema old, int index)
            => old.ArgTypeIds != null && old.ArgTypeIds.Length > index ? (int?)old.ArgTypeIds[index] : null;

        /// <summary>
        /// Whether a payload written for <paramref name="definitionTypeId"/> starts with a fixed, <b>unbiased</b>
        /// 4-byte little-endian element count instead of the shared bias-<c>+1</c> varint.
        /// <para>
        /// <c>QueueFormatter</c> and <c>StackFormatter</c> write their count through the injected
        /// <c>IFormatter&lt;int&gt;</c>, which is the fixed 4-byte little-endian <c>int</c> encoding
        /// (<c>Int32Formatter</c>), and they have no null marker. Every other collection —
        /// <c>ArrayFormatter</c>, <c>CollectionFormatter</c> for <c>List</c>/<c>HashSet</c>/<c>Dictionary</c>
        /// and every collection subclass — writes <c>WriteUInt32Bias(count, 1)</c>, where <c>-1</c> is a null
        /// collection.
        /// </para>
        /// <para>
        /// The two encodings are indistinguishable for the small counts a test save usually holds, which is
        /// exactly why this matters: reading a queue or a stack with the shared biased read does not fail, it
        /// returns a plausible count, consumes the wrong number of bytes and desynchronises everything after it.
        /// </para>
        /// </summary>
        public static bool UsesFixedElementCount(int definitionTypeId)
            => definitionTypeId == WellKnownTypes.Queue || definitionTypeId == WellKnownTypes.Stack;

        /// <summary>
        /// The element count that introduces a described collection payload, plus the bound check. One reader
        /// for every shape and both directions, so the count's encoding and its limit cannot drift between them.
        /// <para>
        /// The encoding is chosen by the <em>file's</em> definition id, never by the current CLR type, for the
        /// same reason the rest of the payload grammar is: a rename, a re-assigned id or a changed shape must
        /// still decode by the layout the writer used.
        /// </para>
        /// <para>
        /// Returns <c>-1</c> for an encoded null collection, which only the biased encodings can express — a
        /// queue or a stack cannot be written as null at all, so a negative count there is corruption rather
        /// than a null.
        /// </para>
        /// <para>
        /// <b>The limit is a parameter rather than a field</b> so that this method is a pure function of
        /// (file identity, configured limit, bytes) and can be asked by a reader that holds no config. The
        /// migration interpreter passes <c>CerasConfig.SizeLimits.MaxCollectionSize</c>, which is what it has
        /// always passed — including for an <em>array</em> payload, whose writer-side bound is
        /// <c>MaxArraySize</c> (<c>ArrayFormatter</c>). That divergence is pre-existing and deliberate in
        /// neither direction, so it is preserved exactly rather than silently reconciled here: a reader that
        /// bounds a value by a limit the writer did not write is still a correct reader, and a test that pins
        /// one of the two limits must not be taken to pin the other.
        /// </para>
        /// </summary>
        public static int ReadCollectionCount(int definitionTypeId, uint maxCollectionSize, ref SequenceReader<byte> reader)
        {
            if (UsesFixedElementCount(definitionTypeId))
            {
                int fixedCount = SerializerBinary.ReadInt32Fixed(ref reader);
                if (fixedCount < 0 || fixedCount > maxCollectionSize)
                    throw new CerasDataCorruptionException(
                        $"Corrupt save: a {(definitionTypeId == WellKnownTypes.Queue ? "queue" : "stack")} declares {fixedCount} elements, " +
                        $"outside the configured maximum of {maxCollectionSize}. Its count is a plain 4-byte element count, " +
                        "so a negative one is a corrupt payload, not a null collection.");
                return fixedCount;
            }

            int count = SerializerBinary.ReadUInt32Bias(ref reader, 1);
            if (count == -1) return -1;                      // a null collection
            if (count < 0 || count > maxCollectionSize)
                throw new CerasDataCorruptionException(
                    $"Corrupt save: a collection declares {count} elements, outside the configured maximum of {maxCollectionSize}.");
            return count;
        }

        /// <summary>
        /// Consumes <paramref name="count"/> bytes. The result is always null: a skip produces no value.
        /// <para>
        /// It is here rather than private to the interpreter because accounting for a fixed width is part of
        /// the grammar, not of the reader that happens to need it today — the per-id switch, the structural
        /// skip and a file-faithful graph builder all advance by the same rule. The <see cref="object"/> return
        /// is not part of that: it exists only so a call can stand in as one arm of a read-or-skip switch,
        /// where every arm has to produce something.
        /// </para>
        /// </summary>
        public static object? Advance(ref SequenceReader<byte> reader, int count)
        {
            reader.Advance(count);
            return null;
        }

        // ── The reference marker, from the file's own record ─────────────────────────────

        /// <summary>
        /// Whether the writer put a reference marker in front of a value this entry describes, decided from
        /// the <b>file</b> alone.
        /// <para>
        /// This is the file-side counterpart of <c>SchemaTraversalEngine.CarriesReferenceMarker</c>, and it
        /// answers the same question for a different reason, so the two are deliberately kept side by side
        /// rather than merged:
        /// </para>
        /// <list type="bullet">
        /// <item>the interpreter's rule is refined by the <em>current</em> CLR type, which it has and this rule
        /// does not need: a closed generic's value/reference representation is not a fact its own entry
        /// records in general, so with a type in hand it asks the type;</item>
        /// <item>this rule asks the <em>file</em>, which is where the writer's own decision survives: the entry's
        /// kind, the opaque entry's recorded inline flag, and — for a user generic — the kind the file's own
        /// section records for the definition entry.</item>
        /// </list>
        /// <para>
        /// The two agree on everything a single writer produces, and they part company in exactly two places,
        /// both of which <c>PayloadGrammarTests</c> pins rather than leaves implied:
        /// </para>
        /// <list type="bullet">
        /// <item>an identity this build cannot resolve. The interpreter's rule then has only the null to go on
        /// and answers "behind a reference"; this rule still knows, because the definition is in the file — a
        /// <c>Nullable</c> is a struct of the format itself, so it is written inline whatever this build can
        /// name;</item>
        /// <item>a user generic whose open type changed between a struct and a class. The file still says the
        /// open type was a struct, so the payload is its member list with no marker in front of it, while the
        /// current type is a class. Milestone 1 does not change the interpreter here: it reads into the
        /// current model and has to be right about what this build declares, so the two rules are kept side
        /// by side rather than one replacing the other.</item>
        /// </list>
        /// <para>
        /// <b>What it does not decide: the position.</b> A member <em>declared</em> as a concrete array is
        /// written by <c>ArrayFormatter</c> with no marker in front of it, and the same array reached through
        /// a reference formatter — a collection element, a polymorphic slot's payload, a dictionary value —
        /// is written behind one. That is a property of the position, so it stays a caller's decision in both
        /// rules: <see cref="SchemaTraversalEngine.ReadAndApplyFields"/> asks it from the file's member entry
        /// plus the current field type today, and a graph builder will ask it from the file's member entry
        /// alone. Answering "marker" here for an array definition is therefore the correct entry-level answer
        /// for every position except that one.
        /// </para>
        /// </summary>
        public static bool FileCarriesReferenceMarker(OldTypeSchema old, FileSchemaView file)
        {
            if (old == null) throw new ArgumentNullException(nameof(old));
            if (file == null) throw new ArgumentNullException(nameof(file));

            switch (old.Kind)
            {
                // A struct and an enum are written inline: no marker, and a struct may not even be nullable.
                case SchemaTypeKind.Struct:
                case SchemaTypeKind.Enum:
                    return false;

                case SchemaTypeKind.Opaque:
                    // The file's own record, and the only place it exists: whether a marker stands in front of
                    // a custom payload's frame is what the writer did, and a type this build no longer has is
                    // exactly the case a decoder that has no current model still has to account for. (It is
                    // also a property of the type and not of the position, because every writer that emits a
                    // value of a reference type emits a marker for it: the collection and array formatters
                    // inject GetReferenceFormatterRequired(TItem).)
                    return !old.OpaqueValueWrittenInline;

                case SchemaTypeKind.Class:
                    return true;

                case SchemaTypeKind.GenericInstantiation:
                    return !DefinitionIsAValueType(old, file);

                default:
                    // The section parser only ever writes the five kinds above, so this is unreachable — and
                    // "behind a reference" is the only answer that cannot silently shift a stream if it ever
                    // stops being unreachable.
                    return true;
            }
        }

        /// <summary>
        /// Whether the value a closed generic was written as is a value type — the one fact its
        /// representation turns on, asked of the file rather than of this build's types.
        /// </summary>
        static bool DefinitionIsAValueType(OldTypeSchema old, FileSchemaView file)
        {
            // A definition nothing describes describes no payload, and a reader that went on would have to
            // guess whether a marker stands in front of it. Refused through the shared invariant and the
            // shared wording, so this door and the other three cannot describe the same defect differently.
            var verdict = StructuralShape.InspectDefinition(old, file.ContainsById);
            if (verdict.IsDefect) throw StructuralShape.UnreadableSchema(old, verdict);

            if (StructuralShape.RequiredArity(old.DefinitionTypeId) > 0)
            {
                // One of the format's own structural definitions, so the reserved table names the open type
                // and whether that open type is a value type is a fact about the format, not about this
                // build: `Nullable`, `KeyValuePair` and the `ValueTuple` family are inline, while `Array`,
                // the collections and the `Tuple` family are written through a reference formatter. The arity
                // is what distinguishes a structural definition from a scalar id, and it comes from the same
                // table that asks the argument-count clause.
                WellKnownTypes.TryGetType(old.DefinitionTypeId, out var definition);
                return definition!.IsValueType;
            }

            // A user generic: not one of the format's own, and the file describes it, so its entry's kind is
            // the writer's own record of how a value of the closed type was written.
            file.TryGetById(old.DefinitionTypeId, out var definitionEntry);
            switch (definitionEntry!.Kind)
            {
                case SchemaTypeKind.Struct:
                case SchemaTypeKind.Enum:
                    return true;

                case SchemaTypeKind.Class:
                    return false;

                case SchemaTypeKind.Opaque:
                    // The same fact a value of the definition itself would carry, recorded the same way.
                    return definitionEntry.OpaqueValueWrittenInline;

                default:
                    throw StructuralShape.UnreadableSchema(old, new StructuralShapeVerdict(
                        StructuralShapeDefect.UndescribedDefinition,
                        $"its definition type id {old.DefinitionTypeId} is an entry the save's own schema section describes as a " +
                        $"{definitionEntry.Kind} instantiation, and a definition is an open type: the writer wrote the value it stands for " +
                        "either inline or behind a reference marker according to whether that open type is a value type, which an " +
                        "instantiation does not say"));
            }
        }
    }
}
