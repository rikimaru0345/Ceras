using System;
using System.Collections.Generic;
using Ceras.Exceptions;

namespace Ceras.Format
{
    /// <summary>
    /// Why a <see cref="SchemaTypeKind.GenericInstantiation"/> entry does not describe a payload.
    /// <para>
    /// These values are a *verdict* rather than a throw, and for the same reason
    /// <see cref="CollectionShapeRejection"/> is a reported answer rather than an exception: the callers that
    /// act on a malformed shape sit at different layers (the authoring forge, the section parser, the
    /// migration plan builder) and each has to report the failure as its own kind of fault, while this is the
    /// one place that decides <em>what</em> is wrong. Every clause is answered for every kind, so a caller
    /// that supports one still has an answer for the others.
    /// </para>
    /// </summary>
    internal enum StructuralShapeDefect
    {
        /// <summary>The entry describes a payload.</summary>
        None = 0,

        /// <summary>
        /// The definition id is neither one of the format's structural definitions nor an entry the file's
        /// own schema section describes. The two are the only things whose payload this engine knows how to
        /// read: a structural definition (a collection, an array, a <c>Nullable</c>, a <c>KeyValuePair</c>,
        /// a tuple) and a user generic class, whose payload is its member list. Anything else is an
        /// identity nothing describes, and an entry behind it has no describable payload at all.
        /// </summary>
        UndescribedDefinition,

        /// <summary>
        /// The definition is a structural one and the entry does not record exactly its argument count.
        /// This is the shape the "missing element type" family reduces to: a <c>List</c> described with no
        /// argument says a count and then nothing about what follows it.
        /// </summary>
        ArgumentArity,

        /// <summary>
        /// An argument id is neither a well-known type nor an entry the file's own schema section
        /// describes, so the element, key, value, item or presence value it stands for has no readable
        /// representation.
        /// </summary>
        UndescribedArgument,
    }

    /// <summary>
    /// A structural-shape verdict: what is wrong with one schema entry, and the sentence that says it.
    /// <see cref="Detail"/> is the shared, layer-independent statement of the fact; each caller adds its own
    /// consequence and advice, because "the section's bytes are damaged" and "this build cannot migrate
    /// this file's description" are different faults even when the fact under them is one fact.
    /// </summary>
    readonly struct StructuralShapeVerdict
    {
        public StructuralShapeDefect Defect { get; }
        public string Detail { get; }

        public bool IsDefect => Defect != StructuralShapeDefect.None;

        public StructuralShapeVerdict(StructuralShapeDefect defect, string detail)
        {
            Defect = defect;
            Detail = detail;
        }

        public static readonly StructuralShapeVerdict None = default;
    }

    /// <summary>
    /// The one structural-schema invariant, and the one place that decides it.
    /// <para>
    /// A <c>GenericInstantiation</c> entry is the format's description of a *closed generic's payload*:
    /// its definition id plus its ordered argument ids are the only record of how that value is written.
    /// For the structural definitions that is a whole grammar — a biased element count followed by the
    /// elements, a presence byte, a key and a value, the items of a tuple — and the argument ids are what
    /// say what each of those is. An entry that does not carry exactly them therefore describes <b>no</b>
    /// payload, and a reader that goes on anyway has to guess: it consumes the count and then either
    /// nothing at all or a fixed number of bytes per element, and every value after the collection decodes
    /// from the wrong offset. Measured on a doctored-but-checksum-valid file, a <c>List&lt;int&gt;</c> with
    /// no argument loaded as three zeros with no error anywhere, a <c>Dictionary</c> with one of its two
    /// arguments threw a bare <see cref="ArgumentNullException"/> out of the BCL, and every malformed shape
    /// desynchronized the <em>skip</em> path used for a member the application has since removed.
    /// </para>
    /// <para>
    /// So the invariant is stated once here and asked at every layer that can first see a malformed entry,
    /// always <b>before a payload byte is interpreted</b>:
    /// <list type="number">
    /// <item><b>the authoring forge</b> — <c>SchemaRegistry.Register(type, info)</c> with an explicit
    /// descriptor, which is the documented public way to describe a historical file's shape by hand. The
    /// descriptor is checked against the definition it names, so a malformed one is refused where the
    /// author can still fix it;</item>
    /// <item><b>the section parser</b> — <c>SchemaSection.Read</c>, the earliest point at which a file's
    /// own description is known, and the one that already holds every id the file records. It asks the two
    /// clauses that need nothing but the section: the argument <em>count</em> against the definition, and
    /// the definition identity against the file's own id space;</item>
    /// <item><b>the migration plan builder</b> — <c>MigrationPlanBuilder.BuildAll</c>, which every route
    /// into the interpreter passes through (<c>CerasRobustSerializer.Load</c> builds the plans and the
    /// engine's own constructor builds them when it is handed none). It re-asks the two, for a caller that
    /// hands the engine schemas the parser never saw, and asks the <em>argument identity</em> clause that
    /// belongs to the migration layer: a member the two sides disagree about is already reported by the
    /// walk inside <c>BuildOne</c>, which names the member and the position inside its shape, so the
    /// section-level refusal covers what that walk deliberately does not look at — a <em>removed</em>
    /// member, whose payload is skipped, and an entry nothing references;</item>
    /// <item><b>the <c>Type</c> value grammar</b> — <c>TypeValueShapes</c>, the fourth way a described
    /// entry appears: one synthesized mid-decode from a file's own type values, long after the section was
    /// parsed. Its definition fixes the argument count in the grammar itself, so the count is checked
    /// where it is read.</item>
    /// </list>
    /// Because the checks run before the payload starts, the interpreter's <b>read</b> and <b>skip</b>
    /// paths cannot disagree about a malformed shape: neither can reach one. That is the property the two
    /// directions need, and it is why this is a section-level rule rather than a per-element guard — a
    /// per-element guard would be reachable from one direction only.
    /// </para>
    /// <para>
    /// <b>What is deliberately not part of it.</b> Nothing here is about whether the file's <em>names</em>
    /// resolve in this build: a save written by a build that had a type this one has not is a legitimate,
    /// supported input, and its entries describe their payloads by identity alone. Nor is anything here
    /// about the <em>current</em> model's type — the existing
    /// <see cref="MigrationPlanBuilder.RejectMovedArrayPayload"/> and
    /// <see cref="MigrationPlanBuilder.RejectChangedStructuralArgument"/> own the old-to-current
    /// comparison, and the narrow shape check here is not a substitute for either.
    /// </para>
    /// </summary>
    internal static class StructuralShape
    {
        // ── The definition table ─────────────────────────────────────────────────────────────
        // Derived from WellKnownTypes rather than written out, so it cannot drift from the id space the
        // format reserves. A structural definition is one whose payload is laid out by the format itself:
        // a reserved well-known generic definition (the collections, Nullable, KeyValuePair, the tuple
        // families) and System.Array, which is not a generic definition but whose instantiation entry
        // carries exactly one argument (the element type). Everything else that holds a well-known id is
        // a scalar, whose payload is a fixed layout the per-id walk already reads.

        /// <summary>A reserved definition: its id, the open type it stands for, and how many arguments it takes.</summary>
        readonly struct StructuralDefinition
        {
            public readonly int Id;
            public readonly Type Definition;
            public readonly int Arity;

            public StructuralDefinition(int id, Type definition, int arity)
            {
                Id = id;
                Definition = definition;
                Arity = arity;
            }

            public string Name => TypeIdentity.GetCanonicalName(Definition);
        }

        static readonly Dictionary<int, StructuralDefinition>    ById   = BuildById();
        static readonly Dictionary<string, StructuralDefinition> ByName = BuildByName(ById);

        static Dictionary<int, StructuralDefinition> BuildById()
        {
            var map = new Dictionary<int, StructuralDefinition>();

            for (int id = 0; id < WellKnownTypes.UserTypeBase; id++)
            {
                if (!WellKnownTypes.TryGetType(id, out var definition)) continue;

                if (definition == typeof(Array))
                {
                    // System.Array names no element type, so the entry supplies one: T[] is recorded as a
                    // GenericInstantiation of Array with the element as its single argument (see
                    // SchemaRegistry.TryBuildStructuralSchema and Format/LAYOUT.md).
                    map[id] = new StructuralDefinition(id, definition, 1);
                    continue;
                }

                if (!definition.IsGenericTypeDefinition) continue;   // a scalar: not a structural definition

                map[id] = new StructuralDefinition(id, definition, definition.GetGenericArguments().Length);
            }

            return map;
        }

        static Dictionary<string, StructuralDefinition> BuildByName(Dictionary<int, StructuralDefinition> byId)
        {
            var map = new Dictionary<string, StructuralDefinition>(StringComparer.Ordinal);
            foreach (var definition in byId.Values)
                map[definition.Name] = definition;
            return map;
        }

        /// <summary>
        /// How many ordered arguments a reserved definition's entry must record, or 0 for an id that is not
        /// a structural definition. The table is <em>derived</em> from <see cref="WellKnownTypes"/>, so a
        /// definition reserved there and not here is not a thing that can happen — which is what lets
        /// <c>TypeValueShapes</c> treat 0 as "not this question" rather than as an arity.
        /// </summary>
        public static int RequiredArity(int definitionTypeId)
            => ById.TryGetValue(definitionTypeId, out var definition) ? definition.Arity : 0;

        /// <summary>
        /// The reserved definition a descriptor's definition name stands for, as the two facts a message
        /// needs: the canonical name and the argument count it takes. A name this does not recognise is
        /// left alone rather than refused — an explicit descriptor is how a historical file's shape is
        /// described by hand, and its definition may be a user generic, or a type this build does not have
        /// at all, whose arity is not a fact about the format.
        /// </summary>
        public static bool TryGetDefinition(string definitionTypeName, out string canonicalName, out int arity)
        {
            if (definitionTypeName != null && ByName.TryGetValue(definitionTypeName, out var found))
            {
                canonicalName = found.Name;
                arity = found.Arity;
                return true;
            }
            canonicalName = null!;
            arity = 0;
            return false;
        }

        // ── The inspections ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The <b>argument count</b> clause, decidable from one entry alone: when the definition is one the
        /// format lays out, the entry has to record exactly that definition's argument count. This is the
        /// "missing element type" family — a <c>List</c> described with no argument says a count and then
        /// nothing about what follows it.
        /// <para>
        /// A definition id this table does not name is <b>not</b> a defect here — it is simply not this
        /// question. It may be a user generic class, whose payload is its member list, and whether the
        /// file's own section describes that identity is a cross-reference the definition clause answers
        /// (<see cref="InspectDefinition"/>). Keeping the two apart is what lets the section parser run
        /// this over a whole file before it knows which ids exist, and lets the migration plan builder ask
        /// both without either one pre-empting the other.
        /// </para>
        /// </summary>
        public static StructuralShapeVerdict InspectShape(OldTypeSchema entry)
        {
            if (entry == null || entry.Kind != SchemaTypeKind.GenericInstantiation)
                return StructuralShapeVerdict.None;

            if (!ById.TryGetValue(entry.DefinitionTypeId, out var definition))
                return StructuralShapeVerdict.None;   // not the format's own grammar; the definition clause decides

            int recorded = entry.ArgTypeIds?.Length ?? 0;
            if (recorded != definition.Arity)
            {
                return new StructuralShapeVerdict(StructuralShapeDefect.ArgumentArity,
                    $"it records {recorded} argument{(recorded == 1 ? "" : "s")} but its definition '{definition.Name}' takes " +
                    $"exactly {definition.Arity}");
            }

            return StructuralShapeVerdict.None;
        }

        /// <summary>
        /// The <b>definition</b> clause: the definition id has to be one of the format's own structural
        /// definitions, or an entry the file's own schema section describes.
        /// <para>
        /// The second half is why this is not a defect in general: an entry whose definition names a type
        /// the file itself describes is a user generic class, and its payload is its member list. What is
        /// not legal is an identity <em>nothing</em> describes, because a reader then has to assume that
        /// position takes no bytes. Note that a <em>name</em> this build cannot resolve is perfectly legal
        /// and is not this clause's business: an old file legitimately names types this build does not
        /// have.
        /// </para>
        /// </summary>
        public static StructuralShapeVerdict InspectDefinition(OldTypeSchema entry, Func<int, bool> isDescribedTypeId)
        {
            if (entry == null || entry.Kind != SchemaTypeKind.GenericInstantiation)
                return StructuralShapeVerdict.None;
            if (isDescribedTypeId == null) throw new ArgumentNullException(nameof(isDescribedTypeId));
            if (ById.ContainsKey(entry.DefinitionTypeId)) return StructuralShapeVerdict.None;
            if (isDescribedTypeId(entry.DefinitionTypeId)) return StructuralShapeVerdict.None;

            return new StructuralShapeVerdict(StructuralShapeDefect.UndescribedDefinition,
                $"its definition type id {entry.DefinitionTypeId} is neither one of the format's structural definitions " +
                $"(an array, a built-in collection, a Nullable, a KeyValuePair or a tuple) nor a type the file's own schema " +
                "section describes, so nothing says how the value it stands for is written");
        }

        /// <summary>
        /// The <b>argument identity</b> clause: every recorded argument has to be an identity the file
        /// describes — a well-known id, or an entry in the file's own schema section — so the element, key,
        /// value, item or presence value it stands for has a readable representation.
        /// </summary>
        public static StructuralShapeVerdict InspectArgumentIdentities(OldTypeSchema entry, Func<int, bool> isDescribedTypeId)
        {
            if (entry == null || entry.Kind != SchemaTypeKind.GenericInstantiation)
                return StructuralShapeVerdict.None;
            if (isDescribedTypeId == null) throw new ArgumentNullException(nameof(isDescribedTypeId));

            int[] args = entry.ArgTypeIds;
            if (args == null) return StructuralShapeVerdict.None;

            for (int i = 0; i < args.Length; i++)
            {
                int argId = args[i];
                if (WellKnownTypes.TryGetType(argId, out _)) continue;
                if (isDescribedTypeId(argId)) continue;

                return new StructuralShapeVerdict(StructuralShapeDefect.UndescribedArgument,
                    $"its argument {i} is type id {argId}, which is neither a well-known type nor an entry in the file's own " +
                    "schema section, so nothing says how that value is written");
            }

            return StructuralShapeVerdict.None;
        }

        // ── Reporting ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The shared, layer-independent statement of the fact: which entry, and what is wrong with it.
        /// Every caller prefixes this with its own consequence and follows it with its own advice, so the
        /// two statements cannot drift while the fact they state stays one fact.
        /// </summary>
        public static string Describe(OldTypeSchema entry, StructuralShapeVerdict verdict)
        {
            string name = string.IsNullOrEmpty(entry.StableName) ? "(unnamed)" : entry.StableName;
            return $"the schema entry '{name}' (type id {entry.TypeId}) describes a value Ceras cannot read: {verdict.Detail}.";
        }

        /// <summary>
        /// Why that makes the payload unreadable rather than merely unusual. One sentence for every
        /// layer, because the consequence is the same whether the entry arrived in a descriptor, in a
        /// file's section or in a type value.
        /// </summary>
        public const string WhyUnreadable =
            "A GenericInstantiation entry's definition id and ordered argument ids are the only record of how that value is " +
            "written: they say whether the payload is an element count plus elements, a presence byte, a key and a value, or a " +
            "member list, and what type each element, key, value or item is. Without them there is no way to know how many " +
            "bytes the payload occupies, so the interpreter either consumes nothing per element or a fixed number that need " +
            "not be the right one - and every value after the collection decodes from the wrong offset.";

        /// <summary>
        /// The authoring half of the invariant: a descriptor registered by hand has to name a reserved
        /// definition and record exactly its argument count.
        /// <para>
        /// This is the one door a <em>user</em> can open, and it is where the check belongs: a descriptor
        /// is code the application wrote, so a malformed one is a declaration bug with an author who can
        /// still see it. The derived paths (<c>DeclareRoot</c> and <c>Register(type, null)</c>, which
        /// derive the descriptor from the CLR type through
        /// <see cref="SchemaRegistry.TryBuildStructuralSchema"/>) cannot produce one.
        /// </para>
        /// <para>
        /// Only the argument <em>count</em> is checked here, and only for a definition the format names.
        /// Whether the recorded argument <em>names</em> resolve is a different question, already refused
        /// with a better message by <c>SchemaSection.ResolveId</c> — which knows the declaring type and
        /// the position — when the schema is baked. A user generic's arity is not a fact about the
        /// format, so a descriptor naming one is left alone.
        /// </para>
        /// </summary>
        public static void RequireDescribableDescriptor(Type type, SchemaTypeInfo info)
        {
            if (info == null || info.Kind != SchemaTypeKind.GenericInstantiation) return;
            if (!TryGetDefinition(info.UnderlyingTypeName, out var definition, out var required)) return;

            int recorded = info.ArgTypeNames?.Length ?? 0;
            if (recorded == required) return;

            string typeName = TypeIdentity.GetCanonicalNameOrUnknown(type);

            throw new CerasSchemaException(
                $"Cannot register '{typeName}': its schema is a GenericInstantiation of '{definition}' that records " +
                $"{recorded} argument{(recorded == 1 ? "" : "s")} but the definition takes exactly {required}. " +
                WhyUnreadable + " Every Ceras writer derives this entry from the CLR type, so this descriptor is a hand-written " +
                "one and the count is simply wrong: give it " + DescribeArity(required) +
                ", or let the schema be derived for you with Register(type, null) / DeclareRoot<T>().");
        }

        /// <summary>What a descriptor has to record, per definition, in the words a caller can act on.</summary>
        static string DescribeArity(int arity)
        {
            if (arity == 1) return "one type name — the element type of a collection or an array, the inner type of a Nullable";
            if (arity == 2) return "a key type name and then a value type name";
            return arity + " type names, in order";
        }

        /// <summary>
        /// The section-parser refusal. A section whose own entries contradict each other is a damaged (or
        /// hand-edited) file, not a compatibility question: nothing in this build's model is involved, and
        /// the header checksum already passed, so the only available evidence is the inconsistency itself.
        /// </summary>
        public static CerasDataCorruptionException CorruptSection(OldTypeSchema entry, StructuralShapeVerdict verdict, int entryIndex)
        {
            return new CerasDataCorruptionException(
                $"Corrupt schema section: entry {entryIndex}, {Describe(entry, verdict)} {WhyUnreadable} " +
                "No Ceras writer records such an entry — a real one always carries its definition's arguments " +
                "(SchemaRegistry.TryBuildStructuralSchema, and the schema encoder) — so the section is damaged or was " +
                "assembled by something other than a Ceras Save. The header checksum already matched, so this is not bit " +
                "rot: treat the file as untrustworthy, or load the save with the build that wrote it.");
        }

        /// <summary>
        /// The migration refusal. Same fact, different fault: the reader has the file's own description in
        /// hand and cannot honour it, which is a schema/layout problem the caller can act on. Refused
        /// while the old-to-current schema is validated and before the root type id is even read, so not one
        /// payload byte is interpreted and no callback can have fired against a half-read graph.
        /// </summary>
        public static CerasSchemaException UnreadableSchema(OldTypeSchema entry, StructuralShapeVerdict verdict)
        {
            return new CerasSchemaException(
                $"Cannot load this save: {Describe(entry, verdict)} {WhyUnreadable} Ceras refuses the load while " +
                "validating the old-to-current schema — before the root type id and before any payload byte is read — rather " +
                "than consuming a count it cannot honour, which is what turned this into a silent wrong value or a stream " +
                "that desynchronized every member after it. This is not a schema change: a save written by any Ceras build " +
                "carries each structural definition's arguments, so such an entry comes from a hand-written or damaged file. " +
                "Describe the shape by hand with SchemaRegistry.Register(type, info) recording one type name per argument of " +
                "the definition, or load the save with the build that wrote it.");
        }
    }
}
