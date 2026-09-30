using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ceras.Exceptions;

namespace Ceras.Format
{
    /// <summary>
    /// Binds an <see cref="OldDataGraph"/> to the current model: it decides, for every member of every
    /// object the file records, whether this build has somewhere to put that member's value, and says so
    /// on the member itself. Nothing else happens here.
    /// <para>
    /// <b>This is the graph's half of the migration decision, and it is deliberately a projection and not a
    /// construction.</b> The interpreter
    /// (<see cref="SchemaTraversalEngine.ReadAndApplyFields"/>) answers the same question while it streams,
    /// by consulting <see cref="FieldMapping"/> per member and assigning or skipping. Here the answer is
    /// written onto the member instead of applied to an object, so that a value's fate is a property of the
    /// value rather than of the act of reading it: a member this build has no field for is
    /// <em>removed from the projection</em>, a member it does have is <em>named</em>. What a materializer
    /// later does with a bound graph is a separate pass, and this class is what that pass reads.
    /// </para>
    /// <para>
    /// The two words map onto the model's own vocabulary on purpose. A member's <em>recorded</em> half
    /// (<see cref="OldMember.Recorded"/>, and the whole of
    /// <see cref="OldObjectNode.RecordedMembers"/>) is the file's own account of itself and is never
    /// rewritten — so a removed member stays in the audit trail and a migration report can still print
    /// what the writer put there. The <em>projection</em> (<see cref="OldObjectNode.Members"/>) is what this
    /// class edits, and the only two things it does are set a current field name and drop an unmapped
    /// member.
    /// </para>
    ///
    /// <para>
    /// <b>What is preserved, and why the arena makes it cheap to promise.</b> Binding moves no value. The
    /// value stays in the member it was read into, and that member is either kept or dropped whole, so every
    /// arena index is unchanged by this class — which is rule 1 of the model
    /// (<see cref="OldDataGraph.NodeCount"/>), and it is what makes a rename provably unable to move a
    /// value. In particular a <em>removed</em> member's value stays in the arena, so a retained member that
    /// holds a back-reference to it still resolves to the same node: the first occurrence may be gone from
    /// the projection and the value is still there for whoever points at it. That is the case the streaming
    /// reader currently refuses (see <c>IdentityCache.Resolve</c>), and it is refused there because a skip
    /// builds nothing to point at. Here the node exists independently of any current field, so the reference
    /// resolves and the refusal has nothing left to refuse.
    /// </para>
    ///
    /// <para>
    /// <b>The plan is the authority for what maps, and this class adds no opinion of its own.</b> Every
    /// mapping comes from the <see cref="TypeMigrationPlan"/> this build's
    /// <see cref="MigrationPlanBuilder"/> already produced: <see cref="FieldMapping.NewField"/> is null
    /// exactly when the plan decided there is no destination, and the member's wire name is the <em>same</em>
    /// <c>StableNameOf</c> rule the interpreter records — called here, not reimplemented. Renames,
    /// coercions, enums and which types exist are all answered before this class runs.
    /// </para>
    ///
    /// <para>
    /// <b>What this class does add is a set of refusals, because a plan can be internally consistent and
    /// still ambiguous.</b> A mapping is a name, and a name can be claimed twice. Two old members both
    /// mapping onto one current field, and two current fields both answering to one wire name, are the same
    /// failure seen from two sides: the file has a value and the model has one place to put it, or has two
    /// places and the file does not say which. Both are refused by name rather than resolved by rule, and
    /// neither is a property of the save — a file that produces one of them is well-formed and the
    /// <em>model</em> is ambiguous. The second is worth spelling out because the plan cannot see it:
    /// <see cref="MigrationPlanBuilder"/>'s member map inserts names with first-wins, so when a derived
    /// member's alias collides with a base member's wire name, the base member simply does not appear
    /// anywhere — not in the map, not in <see cref="TypeMigrationPlan.AddedFieldNames"/> as anything but an
    /// "added" field, and not in the graph. Its values would be dropped as if the application had removed
    /// it. This class therefore re-derives the current type's member <em>identities</em> — not its
    /// decisions — and refuses when two of them answer to one name, or when a member exists that no
    /// mapping ever names.
    /// </para>
    ///
    /// <para>
    /// <b>It validates the whole graph before it changes any of it.</b> A pass that bound member 0 of
    /// object 0 and then refused object 7 would leave a graph that is half bound and looks like a graph
    /// that was built: the names that are set are indistinguishable from names that were always set. So
    /// every refusal below is raised during a first pass that touches nothing, and the bindings are written
    /// only once the whole graph has been accepted.
    /// </para>
    ///
    /// <para>
    /// <b>It is object-independent, and it binds a graph exactly once.</b> No CLR object is created, no
    /// constructor runs, no formatter is resolved, no field is read or written, and no callback fires. The
    /// only things this class touches are the graph's own bindings and the plans it was handed. And a graph
    /// that already carries a current field name is <em>refused</em> rather than rebound, because a member
    /// with a name is the output of this pass and a name this pass did not write is either a second opinion
    /// or a hand edit; neither may be silently overwritten. That is what makes "bound" a state a graph is in
    /// rather than an operation that may be repeated.
    /// </para>
    /// <para>
    /// <b>It is deterministic.</b> The arena is walked in index order, members in recorded order, and
    /// nothing is looked up through a hash whose iteration order is consulted. Two runs over one save bind
    /// it identically, which is what makes a binding difference between two readers a finding rather than a
    /// coin flip.
    /// </para>
    /// </summary>
    sealed class GraphMigrationBinder
    {
        readonly Dictionary<string, TypeMigrationPlan> _plans;

        /// <summary>
        /// Creates a binder over the plans for one file's schema section, as
        /// <see cref="MigrationPlanBuilder.BuildAll"/> produced them. Keyed by the file's own canonical stable
        /// name, which is the only spelling a plan is indexed under and the only spelling
        /// <see cref="OldTypeIdentity.StableName"/> carries for a type the file's section describes.
        /// </summary>
        public GraphMigrationBinder(Dictionary<string, TypeMigrationPlan> plans)
        {
            _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        }

        /// <summary>
        /// Binds every object in <paramref name="graph"/>, in arena-index order — or refuses the whole graph
        /// having changed nothing.
        /// <para>
        /// The whole arena is walked, not just what the root reaches. A value whose only first occurrence is
        /// under a member this build has no field for is still an object with members of its own, and leaving
        /// it unbound would make the finished graph's bindings depend on which path happened to reach it.
        /// </para>
        /// </summary>
        /// <exception cref="CerasSchemaException">
        /// The graph and the plans do not correspond, an object has no plan it should have, a member is
        /// already bound, or a mapping is ambiguous. Every one of those is raised before anything is written.
        /// </exception>
        public void Bind(OldDataGraph graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            // Two passes, and the order is the contract. The first decides everything and writes nothing;
            // the second writes everything and decides nothing. A graph that is refused is therefore
            // byte-for-byte the graph that was handed over, which is what "refused" has to mean when the
            // alternative is a half-bound graph that no later reader could tell from a bound one.
            Validate(graph);
            Apply(graph);
        }

        // ── Pass one: decide, write nothing ──────────────────────────────────────────────────

        void Validate(OldDataGraph graph)
        {
            // One entry per current type, computed once: the wire identities below are a property of the
            // type, not of the object, and a save names the same type many times over.
            var wireIdentities = new Dictionary<Type, CurrentTypeMembers>();

            for (int i = 0; i < graph.NodeCount; i++)
                if (graph.Node(i) is OldObjectNode node)
                    ValidateOne(node, wireIdentities);
        }

        void ValidateOne(OldObjectNode node, Dictionary<Type, CurrentTypeMembers> wireIdentities)
        {
            // ── A member that already carries a name ──
            // The output of this pass, or a hand edit. Either way it is not this pass's to overwrite, and
            // refusing is the only answer that treats "already bound" and "half edited" the same way.
            for (int i = 0; i < node.Members.Count; i++)
                if (node.Members[i].CurrentFieldName != null)
                    throw new CerasSchemaException(
                        $"Internal error: member {Describe(node, i)} already carries the current field name " +
                        $"'{node.Members[i].CurrentFieldName}'. A graph is bound exactly once, and a member with a name is either the " +
                        "output of a previous bind or an edit made since; overwriting either would make a second opinion look like the " +
                        "first. Bind a freshly read graph, or one whose bindings you have already cleared.");

            // ── Which plan, and whether having no members is legitimate ──
            // A bare System.Object is the one identity the format gives no members, so it legitimately has
            // nothing to bind and legitimately has no plan — the file's section never describes it. Any
            // other memberless object is a claim that has to be checked rather than waved through: an empty
            // projection paired with a plan that describes members is the two halves of one file disagreeing.
            if (!_plans.TryGetValue(node.Type.StableName, out var plan) || plan.Mappings == null)
            {
                if (IsBareObject(node))
                {
                    if (node.Members.Count != 0)
                        throw new CerasSchemaException(
                            $"Internal error: the value the save records as a bare '{node.Type.StableName}' has " +
                            $"{node.Members.Count} member(s). System.Object is written as a reference marker and nothing else, so a member " +
                            "under it is a record the file never wrote.");
                    return;
                }

                throw new CerasSchemaException(
                    $"Internal error: the value the save records as '{node.Type.StableName}' has {node.Members.Count} member(s) and no " +
                    "migration plan describes it. A plan is built for every type the file's schema section carries, and an object with " +
                    "members is only ever one of those: a bare System.Object (which the section does not describe) or a type the file " +
                    "named. A shape decoded from a System.Type value is a collection, an array, a Nullable, a pair or a tuple, and none " +
                    "of those is an object with members, so it cannot be what this is.");
            }

            var mappings = plan.Mappings;
            Correspondence(node, mappings);

            // ── Two old members, one current field ──
            // Both names exist, both have a destination, and the destination is the same one. The file
            // wrote two values and the model has one place for them, so which one survives would be a rule
            // this library would be inventing.
            var destinations = new Dictionary<FieldInfo, int>();
            for (int i = 0; i < mappings.Length; i++)
            {
                var newField = mappings[i].NewField;
                if (newField == null) continue;

                if (destinations.TryGetValue(newField, out int first))
                    throw new CerasSchemaException(
                        $"Cannot bind '{node.Type.StableName}': the members '{mappings[first].OldField.StableName}' and " +
                        $"'{mappings[i].OldField.StableName}' both map onto the current member " +
                        $"'{MigrationPlanBuilder.StableNameOf(newField)}'. The save holds a value for each and this build has one place to " +
                        "put it, so which one wins would be a rule Ceras would be inventing. Give the two members distinct destinations — " +
                        "rename one of them, or keep the old one and build the other in your own migration step.");

                destinations.Add(newField, i);
            }

            // ── The current type's own member identities ──
            // Only for a type this build has, and only when the plan says a mapping landed, because a
            // type that is gone has nothing here to be ambiguous about: every member is removed and the
            // answer is the same for all of them.
            if (plan.CurrentType == null) return;

            var members = CurrentTypeMembers.Of(plan.CurrentType, wireIdentities);

            foreach (var collision in members.Collisions)
                throw new CerasSchemaException(
                    $"Cannot bind '{node.Type.StableName}': the current type '{TypeIdentity.GetCanonicalName(plan.CurrentType)}' has two " +
                    $"different members answering to the wire name '{collision.Name}' — '{collision.First}' and '{collision.Second}'. " +
                    "The name a file records cannot say which one a value belongs to, and the map that resolves old names to current ones " +
                    "keeps only the first, so the other would silently receive nothing at all. Rename one of them, or give one of them a " +
                    "wire name of its own ([CerasStableName]) and a [PreviousName] the other does not also claim.");

            // A member of the current type that no mapping ever names. A member whose name no old member
            // answers to is simply one the application added, and the plan already reports it as such. A
            // member that *would* have answered — one the file's own members name — and still got nothing,
            // is the shape above seen from the other side: its name was taken by a different member, so
            // its values were dropped as if it had been removed.
            // A member of the current type that no mapping ever names, although the file records a member
            // under one of its own names: its name was taken by a different member, so its values would be
            // dropped as if the application had removed it. A member the application genuinely added is not
            // in that state, and the file's own member names are what tells the two apart.
            foreach (var stranded in members.StrandedAgainst(destinations, FileNamesOf(mappings)))
                throw new CerasSchemaException(
                    $"Cannot bind '{node.Type.StableName}': the current type '{TypeIdentity.GetCanonicalName(plan.CurrentType)}' has a member " +
                    $"'{stranded.Name}' that no old member maps onto, although the save records a member under a name that is one of its " +
                    $"own ({stranded.ClaimedBy}). The name was already taken by a different member, so this one's values would be dropped as " +
                    "if the application had removed it. Give the two members distinct wire names, or keep the old member under its own " +
                    "stable name and build the new one from it in a migration step.");
        }

        /// <summary>
        /// The correspondence between the file's members and the plan's mappings: the same count, and for
        /// each position the same recorded name and the same recorded type id.
        /// <para>
        /// <b>This is an internal error, not a property of the save.</b> A genuine save cannot produce it:
        /// both sides are built from one file's section in one order, and a mismatch means this binder was
        /// handed a plan for a different file, or a graph edited before this pass ran. Reporting it as a
        /// schema problem would send an application looking at its own save for a defect it does not have,
        /// and reporting it as corruption would send it looking at its bytes.
        /// </para>
        /// </summary>
        static void Correspondence(OldObjectNode node, FieldMapping[] mappings)
        {
            if (node.Members.Count != mappings.Length)
                throw new CerasSchemaException(
                    $"Internal error: the old-data graph records {node.Members.Count} member(s) for '{node.Type.StableName}', but the " +
                    $"migration plan for that type describes {mappings.Length}. A plan and a graph are both built from one save's schema " +
                    "section in its member order, so these two lists are the same list by construction; a disagreement means the plan and " +
                    "the graph were built from different files, or the graph was reshaped before it was bound.");

            for (int i = 0; i < mappings.Length; i++)
            {
                var recorded = node.Members[i].Recorded;
                var mapped = mappings[i].OldField;

                // A member with no recorded half is one a transformation synthesised, and a plan is built
                // from the file's section and has no entry for it. See the same class of mistake in
                // ValidateOne: this is asked about from the member's side.
                if (recorded == null)
                    throw new CerasSchemaException(
                        $"Internal error: member {Describe(node, i)} has no recorded identity, so the migration plan has nothing to bind " +
                        "it to. A plan is built from the file's own schema section, and every member of that section is recorded; a member " +
                        "without a recorded half is one a transformation synthesised, and it has to be bound by that transformation rather " +
                        "than by this pass.");

                if (recorded.StableName != mapped.StableName)
                    throw new CerasSchemaException(
                        $"Internal error: member {Describe(node, i)} is recorded by the file under the name '{recorded.StableName}', but the " +
                        $"migration plan binds that position from a member named '{mapped.StableName}'. A plan and a graph are both built " +
                        $"from one save's schema section in its member order, so position {i} is the same member on both sides; a disagreement " +
                        "here means the plan and the graph were built from different files, or the graph was reshaped before it was bound.");

                // The type id, not the type: a name can be spelled two ways, but a type id is the file's own
                // numbering and the two sides were handed the same section. This is what catches a graph and
                // a plan that agree on every name and disagree about what each member holds — the case a
                // name check alone would let through as a plausible field of the wrong type.
                if (recorded.Type.TypeId != mapped.TypeId)
                    throw new CerasSchemaException(
                        $"Internal error: member {Describe(node, i)} is recorded as '{recorded.StableName}' with type id " +
                        $"{recorded.Type.TypeId}, but the migration plan binds that position from '{mapped.StableName}' with type id " +
                        $"{mapped.TypeId}. The two sides were built from one save's schema section, so they agree on a member's identity by " +
                        "construction; a disagreement here means the plan and the graph came from different files, or the graph was reshaped " +
                        "before it was bound.");
            }
        }

        /// <summary>
        /// Every wire name the file's own members are recorded under — their stable names and their
        /// previous names — which is what tells a member the application genuinely added from one whose
        /// name a different member already took.
        /// </summary>
        static HashSet<string> FileNamesOf(FieldMapping[] mappings)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in mappings)
            {
                names.Add(mapping.OldField.StableName);
                foreach (var previous in mapping.OldField.PreviousNames) names.Add(previous);
            }
            return names;
        }

        /// <summary>
        /// Whether this object is the one identity the format gives no members: <c>System.Object</c>, whose
        /// payload is a reference marker and nothing else. Asked on the node's own identity rather than on
        /// its stable name, because the name is a spelling a file or a resolver could have supplied.
        /// </summary>
        static bool IsBareObject(OldObjectNode node)
            => node.Type.TypeId == WellKnownTypes.Object && node.Type.Kind == SchemaTypeKind.Class;

        static string Describe(OldObjectNode node, int memberIndex)
        {
            var recorded = node.Members[memberIndex].Recorded;
            return recorded == null
                ? $"{memberIndex} of '{node.Type.StableName}'"
                : $"{memberIndex} of '{node.Type.StableName}' ('{recorded.StableName}')";
        }

        // ── Pass two: write, decide nothing ──────────────────────────────────────────────────

        void Apply(OldDataGraph graph)
        {
            for (int i = 0; i < graph.NodeCount; i++)
            {
                if (graph.Node(i) is not OldObjectNode node) continue;
                if (node.Members.Count == 0) continue;
                if (!_plans.TryGetValue(node.Type.StableName, out var plan) || plan.Mappings == null) continue;

                var mappings = plan.Mappings;

                // Pass forwards: name every member that has a destination. Nothing is removed yet, so `i`
                // addresses the same member here that it does in the mappings array.
                for (int m = 0; m < mappings.Length; m++)
                {
                    var newField = mappings[m].NewField;
                    if (newField == null) continue;
                    node.Members[m].CurrentFieldName = MigrationPlanBuilder.StableNameOf(newField);
                }

                // Pass backwards: drop every member that has none. Walking downwards is what keeps the
                // indices valid — removing at i shifts only i and above, all of which this loop has already
                // passed — and it needs no buffer of the indices to remove.
                //
                // A member's *value* is not touched: it stays in the arena, so anything still pointing at it
                // resolves, and its record stays in RecordedMembers, so a report can still say what the
                // writer put there. The removal is of the binding, not of the value — which is also why a
                // removed member can be left holding no name at all: the projection says it has nowhere to
                // go, and a name would say otherwise.
                for (int m = mappings.Length - 1; m >= 0; m--)
                    if (mappings[m].NewField == null)
                        node.Members.RemoveAt(m);
            }
        }

        // ── The current type's member identities ─────────────────────────────────────────────
        //
        // This is the one place a binder has to look at the current model rather than at the plan, and it
        // is here for a specific reason: the plan's member map inserts names first-wins, so a member whose
        // name was taken by another is absent from the plan entirely — not unmapped, *absent* — and the
        // graph cannot see it either. Asking the plan "which members did you not map?" cannot find it.
        // Asking the type "which members are there, and which name does each answer to?" can.
        //
        // The walk below mirrors the member rules of <see cref="MigrationPlanBuilder"/> and shares its
        // naming rule outright (StableNameOf is internal and is called, not copied). The rules it does
        // repeat are the attribute-driven ones — which fields are members, and which old names a member
        // answers to — and they are repeated rather than shared because MigrationPlanBuilder's own copies
        // are private. If they ever change, this has to change with them; the honest place for this walk is
        // beside them, which is a change to a file another agent owns.
        sealed class CurrentTypeMembers
        {
            /// <summary>Every instance field up the base chain that would be serialized, with the names it answers to.</summary>
            readonly List<MemberNames> _members;

            CurrentTypeMembers(List<MemberNames> members) => _members = members;

            /// <summary>One name two different members answer to, and who they are.</summary>
            public readonly struct Collision
            {
                public readonly string Name;
                public readonly string First;
                public readonly string Second;

                public Collision(string name, string first, string second)
                {
                    Name = name;
                    First = first;
                    Second = second;
                }
            }

            /// <summary>A member no mapping names, and the name of the member that took its name.</summary>
            public readonly struct Stranded
            {
                public readonly string Name;
                public readonly string ClaimedBy;

                public Stranded(string name, string claimedBy)
                {
                    Name = name;
                    ClaimedBy = claimedBy;
                }
            }

            /// <summary>
            /// The wire names two <em>different</em> members of this type both answer to.
            /// <para>
            /// A member answering to several names is not a collision: one field claims its own stable name
            /// and its own aliases, and that is what <c>[PreviousName]</c> is for. Two <em>distinct</em>
            /// members claiming one name is the ambiguity, and the identity compared here is the field's, not
            /// the name's.
            /// </para>
            /// </summary>
            public IEnumerable<Collision> Collisions
            {
                get
                {
                    var owner = new Dictionary<string, MemberNames>(StringComparer.Ordinal);
                    foreach (var member in _members)
                        foreach (var name in member.Names)
                        {
                            if (owner.TryGetValue(name, out var first))
                            {
                                if (first.Field != member.Field)
                                    yield return new Collision(name, first.Label, member.Label);
                            }
                            else owner.Add(name, member);
                        }
                }
            }

            /// <summary>
            /// Members that no mapping names even though the file records a member under one of their own
            /// names, with the member that took the name.
            /// <para>
            /// A member the application genuinely added is <em>not</em> stranded: the file records no member
            /// under any of its names, which is exactly what distinguishes an addition from a collision. In
            /// the collision, the file's member <em>is</em> named after this member, some other member took
            /// that name first, and this member's values were dropped as if it had been removed.
            /// </para>
            /// </summary>
            public IEnumerable<Stranded> StrandedAgainst(Dictionary<FieldInfo, int> mapped, HashSet<string> fileNames)
            {
                // Who answers to each name, so the refusal can name the member that took it.
                var claimant = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in _members)
                    foreach (var name in member.Names)
                        if (!claimant.ContainsKey(name)) claimant.Add(name, member.Label);

                foreach (var member in _members)
                {
                    if (mapped.ContainsKey(member.Field)) continue;
                    if (!member.Names.Any(fileNames.Contains)) continue;   // an ordinary added member

                    var taken = member.Names
                        .Where(name => fileNames.Contains(name) && claimant.TryGetValue(name, out var who) && who != member.Label)
                        .Select(name => $"{name} (claimed by {claimant[name]})")
                        .FirstOrDefault();

                    if (taken == null) continue;
                    yield return new Stranded(member.Label, taken);
                }
            }

            public static CurrentTypeMembers Of(Type type, Dictionary<Type, CurrentTypeMembers> cache)
            {
                if (cache.TryGetValue(type, out var cached)) return cached;

                var members = new List<MemberNames>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                // Derived first, which is the order the plan's own map uses: a member declared in a more
                // derived type claims a contested name before any base declaration of it is considered.
                for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                    foreach (var field in current.GetFields(flags))
                    {
                        if (field.DeclaringType != current) continue;
                        if (!IsSerializedMember(field, out var property)) continue;

                        var names = new List<string> { MigrationPlanBuilder.StableNameOf(field) };
                        foreach (var previous in PreviousNamesOf(field, property))
                            if (!names.Contains(previous)) names.Add(previous);

                        members.Add(new MemberNames(field, names));
                    }

                var built = new CurrentTypeMembers(members);
                cache.Add(type, built);
                return built;
            }

            /// <summary>
            /// Would a writer have put this field in the payload? The attribute half of the rule, mirroring
            /// <c>MigrationPlanBuilder.IsSerializedMember</c>: the same four attributes, read from the same
            /// declarations, and the same compiler-generated correction.
            /// </summary>
            static bool IsSerializedMember(FieldInfo field, out PropertyInfo? property)
            {
                property = AssociatedPropertyOf(field);

                if (field.GetCustomAttribute<ExcludeAttribute>() != null) return false;
                if (field.GetCustomAttribute<NonSerializedAttribute>() != null) return false;
                if (property?.GetCustomAttribute<ExcludeAttribute>() != null) return false;
                if (property?.GetCustomAttribute<NonSerializedAttribute>() != null) return false;

                // A compiler-generated field is a member only when a property stands for it, which is the
                // same correction MemberConfig makes. The base chain is what brings these into view here,
                // so the rule has to be applied as the walk goes rather than assumed away.
                if (property == null && field.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null)
                    return false;

                return true;
            }

            /// <summary>The auto-property <paramref name="field"/> is the backing field of, or null.</summary>
            static PropertyInfo? AssociatedPropertyOf(FieldInfo field)
            {
                const string suffix = ">k__BackingField";
                var name = field.Name;
                if (!name.StartsWith("<", StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) return null;

                var propertyName = name.Substring(1, name.Length - 1 - suffix.Length);
                return field.DeclaringType?.GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }

            /// <summary>The other wire names this member has been recorded under, from its own attributes.</summary>
            static IEnumerable<string> PreviousNamesOf(FieldInfo field, PropertyInfo? property)
            {
                foreach (var previous in PreviousNamesOn(field)) yield return previous;
                if (property == null) yield break;
                foreach (var previous in PreviousNamesOn(property)) yield return previous;
            }

            static IEnumerable<string> PreviousNamesOn(MemberInfo member)
            {
                foreach (var previous in member.GetCustomAttributes<PreviousNameAttribute>())
                {
                    if (previous.Name != null) yield return previous.Name;
                    foreach (var alternative in previous.AlternativeNames)
                        if (alternative != null) yield return alternative;
                }
            }

            /// <summary>One member of the current type: the field itself, and every name it answers to.</summary>
            sealed class MemberNames
            {
                public readonly FieldInfo Field;
                public readonly IReadOnlyList<string> Names;

                /// <summary>How the member is named in a refusal: the stable name, not an alias.</summary>
                public string Label => MigrationPlanBuilder.StableNameOf(Field);

                public MemberNames(FieldInfo field, IReadOnlyList<string> names)
                {
                    Field = field;
                    Names = names;
                }
            }
        }
    }
}
