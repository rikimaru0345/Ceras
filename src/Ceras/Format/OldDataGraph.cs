using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Ceras.Exceptions;

namespace Ceras.Format
{
    // ── The old-data graph (TODO.md, Milestone 2) ─────────────────────────────────────────────
    //
    // This is the *node model*: the four rules the whole migration design rests on, expressed as a set
    // of types that enforce them structurally rather than describing them in prose. Nothing here is
    // reached by a runtime path yet — the streaming interpreter (SchemaTraversalEngine) still decodes
    // straight into current objects, and this step changes no byte it reads and no byte it writes.
    //
    //   1. Index identity.  A value's identity is its index in the arena (OldDataGraph.Node /
    //      OldDataGraph.IndexOf) — never its CLR type, never a name, never its position in a member
    //      list. A rename therefore cannot change which node a value is, and the graph's only key is
    //      reference identity.
    //
    //   2. Closed arena.  The node list is copied into the graph by the constructor and no public path
    //      appends, reserves or removes a node. That is what lets a later transform re-point and
    //      reshape the graph without ever invalidating a back-reference: a back-reference is a bare
    //      index, so it is only safe because the set of indices never changes. The mutation vocabulary
    //      is on the *bindings* and can only move values that already have an index.
    //
    //   3. Immutable file record.  Everything the file said — identities, names, kinds, a recorded
    //      count, the recorded members of an object, an enum's raw bits, a reference's target index —
    //      is get-only and copied on construction. Only the four bindings may change:
    //      OldMember.Value, OldMember.CurrentFieldName, the OldObjectNode.Members list and
    //      OldDataGraph.Root.
    //
    //   4. Back-reference ordering.  A reference names a strictly *earlier* arena index, because the
    //      writer writes a value before anything that can point back at it, and the arena is built in
    //      that first-occurrence order. OldDataGraph's constructor refuses any other ordering as
    //      corruption; because the arena is closed (2) and TargetIndex is immutable (3), no later
    //      mutation can make the invariant false, so a later builder may keep treating a violation as
    //      corruption rather than re-checking.
    //
    // The null model is the fourth thing this file has to get right, and it is explained on
    // OldNullReason: a null is a real recorded fact, it is never encoded as a null reference in a value
    // slot, and the reason it was null is part of the record.

    /// <summary>
    /// The one place "copy what the caller handed us, then wrap it so it cannot be written through" is
    /// written. A get-only property whose backing list is the caller's is not immutable at all — the
    /// caller still holds the list — and every "file record" collection in this model has to be a copy,
    /// so the copy is a helper rather than four near-identical private methods that could drift.
    /// </summary>
    static class OldDataGraphFrozen
    {
        /// <summary>
        /// A read-only copy, or an empty list for null. The result is a
        /// <see cref="ReadOnlyCollection{T}"/> rather than the array behind it, so a caller holding the
        /// original list cannot get at the copy either.
        /// </summary>
        public static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T>? items)
        {
            if (items == null || items.Count == 0) return Array.Empty<T>();

            var copy = new T[items.Count];
            for (int i = 0; i < items.Count; i++) copy[i] = items[i];
            return new ReadOnlyCollection<T>(copy);
        }

        /// <summary>
        /// The same copy for an <see cref="IList{T}"/>, which is not itself an
        /// <see cref="IReadOnlyList{T}"/> (only its implementations are) — hence the second door rather
        /// than a cast that a hand-written list implementation would fail.
        /// </summary>
        public static IReadOnlyList<T> Freeze<T>(IList<T> items)
        {
            var copy = new T[items.Count];
            for (int i = 0; i < items.Count; i++) copy[i] = items[i];
            return new ReadOnlyCollection<T>(copy);
        }

        /// <summary>
        /// The same copy, keeping null as null — for a member that exists for one shape only, where null
        /// and empty would be two different recorded facts.
        /// </summary>
        public static IReadOnlyList<T>? FreezeOrNull<T>(IReadOnlyList<T>? items)
            => items == null ? null : Freeze(items);
    }

    /// <summary>
    /// Why a value in an <see cref="OldDataGraph"/> is null. A writer has exactly three ways to record a
    /// null in a value slot, they are encoded completely differently, and nothing in the bytes
    /// afterwards says which one was used — so the reason is part of the record and is carried by a node
    /// kind (<see cref="OldNullNode"/>) instead of by a nullable field on every node.
    /// <para>
    /// The three decoded reasons are exactly the cases where the file stated <em>no</em> type identity
    /// for the nulled value, so the reason is all there is to know about it. Where the file <em>did</em>
    /// state an identity, the null keeps it and is not one of these: a null collection is an
    /// <see cref="OldCollectionNode"/> whose recorded <see cref="OldCollectionNode.Count"/> is the -1 bias
    /// (<see cref="OldCollectionNode.IsNullCollection"/>, so the file's
    /// <c>List&lt;Dictionary&lt;string,int&gt;&gt;</c> is still known to be a list of dictionaries, which
    /// an identity-free null node would have thrown away), and a null string is an
    /// <see cref="OldScalar"/> whose <see cref="OldScalar.Value"/> is null — the writer wrote
    /// string-table index 0 there, not a reference marker.
    /// </para>
    /// <para>
    /// <see cref="MemberDetached"/> is the one reason that is not decoded: it records that a transform
    /// took a value out of a member and left the member in place (OldDataGraph.DetachMemberValue), so
    /// the materializer must not assign anything to it. It is a fact about the binding rather than
    /// about the file, which is why nothing but the reason itself can tell the two apart.
    /// </para>
    /// </summary>
    public enum OldNullReason : byte
    {
        /// <summary>
        /// A reference-typed value whose marker was the null reference (<see cref="Protocol.RefNull"/>).
        /// The file wrote no type for it, so the enclosing member's own recorded identity is the only
        /// thing that says what kind of reference this was.
        /// </summary>
        ReferenceMarker = 0,

        /// <summary>
        /// A <c>Nullable&lt;T&gt;</c> whose presence byte was 0 (<c>NullableFormatter&lt;T&gt;</c>'s
        /// layout). Distinct from <see cref="ReferenceMarker"/> because a <c>Nullable&lt;T&gt;</c> is
        /// written inline with no reference marker at all: materializing one is not the same act as
        /// materializing a null reference, and the two must not be able to produce the same result by
        /// accident.
        /// </summary>
        NullablePresenceByte = 1,

        /// <summary>
        /// A <c>System.Type</c> value whose tag was the well-known null type
        /// (<see cref="WellKnownTypes.Null"/>, written by RobustTypeFormatter). Not a type identity: the
        /// file said "null" and named nothing, so there is no <see cref="OldTypeIdentity"/> to record and
        /// this is a null rather than an <see cref="OldTypeValueNode"/>.
        /// </summary>
        TypeValueNull = 2,

        /// <summary>
        /// A value taken out of a member by OldDataGraph.DetachMemberValue. Not a decoded fact: the
        /// member still exists and may still be bound to a current field, but no value is bound to it
        /// any more.
        /// </summary>
        MemberDetached = 3,
    }

    /// <summary>
    /// Base of every value in an <see cref="OldDataGraph"/>. A value is one of: a recorded null
    /// (<see cref="OldNullNode"/>), a scalar, an enum, a recorded <c>Type</c> value, an object, a
    /// collection, or a back-reference to an earlier value.
    /// <para>
    /// Enforces rule 3 (the file's record is immutable) by having no settable member at all, and carries
    /// rule 1 (a value's identity is its arena index) by being a reference type: the arena keys values by
    /// reference identity, so two values that are <c>Equals</c> are two nodes with two indices.
    /// </para>
    /// </summary>
    public abstract class OldValue
    {
    }

    /// <summary>
    /// A recorded null: the file wrote a null in this position, and <see cref="Reason"/> says which
    /// encoding it used. It is a node like any other, so a value slot never holds a null CLR reference and
    /// a reader of the graph can always say which of the recorded nulls it is looking at.
    /// <para>
    /// A null is deliberately <em>not</em> a node of the arena: it has no referent, so nothing the
    /// writer wrote can be a back-reference to it, and giving it an index would suggest otherwise.
    /// OldDataGraph.IndexOf therefore answers -1 for one, and OldDataGraph's constructor refuses to place
    /// one in the arena.
    /// </para>
    /// </summary>
    public sealed class OldNullNode : OldValue
    {
        /// <summary>Which of the recorded nulls this is. Immutable: it is the file's record (rule 3).</summary>
        public OldNullReason Reason { get; }

        /// <summary>Records a null with the encoding that produced it.</summary>
        public OldNullNode(OldNullReason reason)
        {
            Reason = reason;
        }
    }

    /// <summary>
    /// What the FILE said about a type: the id its payload carries, the names the file knows it by, what
    /// kind of thing it is, and — for a closed generic instantiation — the well-known definition it
    /// instantiates and its ordered argument identities.
    /// <para>
    /// This is <see cref="OldTypeSchema"/> projected into the graph, verbatim and complete. The stable
    /// name is the spelling the file used even when this build has renamed the type, and
    /// <see cref="PreviousNames"/> are the older spellings the file would also accept. It is
    /// <em>not</em> resolved against the current model and never mentions one.
    /// </para>
    /// <para>
    /// <see cref="ArgumentTypeIds"/> holds identities rather than bare ids, so a closed instantiation is
    /// one recursive value: <c>List&lt;List&lt;int&gt;&gt;</c> is a GenericInstantiation whose definition
    /// is <c>List</c> and whose single argument is itself a GenericInstantiation of <c>List</c> over
    /// <c>Int32</c> — which is what the writer's recursive <c>Type</c> value recorded.
    /// </para>
    /// <para>
    /// Enforces rule 3 (every member is get-only, and both name lists are copied so a caller cannot
    /// reach in afterwards) and rule 1 (it is a value, not a key: nothing in the graph is looked up by
    /// one, so renaming a type changes this record but changes no index).
    /// </para>
    /// </summary>
    public sealed class OldTypeIdentity
    {
        /// <summary>
        /// The id the file's payload uses for this type: a well-known id below
        /// <see cref="WellKnownTypes.UserTypeBase"/> for a built-in shape, the file's own manifest id for
        /// a user type, and — for a shape synthesized from a <c>Type</c> value — a synthesized id that
        /// nothing in the file's schema section claims.
        /// </summary>
        public int TypeId { get; }

        /// <summary>
        /// The name the file used, verbatim, whether or not this build still has a type by that name.
        /// Never the current type's name: a value that resolved to a renamed type keeps the old spelling
        /// here, which is what makes the file's record auditable.
        /// </summary>
        public string StableName { get; }

        /// <summary>
        /// The older names this entry is also known by, exactly as the file recorded them (from
        /// <c>[PreviousTypeName]</c>). Copied, and empty rather than null when the file recorded none.
        /// </summary>
        public IReadOnlyList<string> PreviousNames { get; }

        /// <summary>The kind the file described: an enum, a struct, a class, or a generic instantiation.</summary>
        public SchemaTypeKind Kind { get; }

        /// <summary>
        /// The well-known definition a <see cref="SchemaTypeKind.GenericInstantiation"/> instantiates —
        /// <c>List</c>, <c>Dictionary</c>, <c>Array</c>, <c>Nullable</c>, a tuple family — and 0 for
        /// anything else. It is the file's id, never the current model's: it is what says which payload
        /// grammar the bytes follow.
        /// </summary>
        public int DefinitionTypeId { get; }

        /// <summary>
        /// The closed instantiation's argument identities, in order; empty unless <see cref="Kind"/> is
        /// <see cref="SchemaTypeKind.GenericInstantiation"/>. Copied, so the recursive tree it forms is
        /// immutable with the rest of the record.
        /// </summary>
        public IReadOnlyList<OldTypeIdentity> ArgumentTypeIds { get; }

        /// <summary>Records what the file said about one type.</summary>
        public OldTypeIdentity(
            int                            typeId,
            string                         stableName,
            IReadOnlyList<string>?         previousNames,
            SchemaTypeKind                 kind,
            int                            definitionTypeId   = 0,
            IReadOnlyList<OldTypeIdentity>? argumentTypeIds = null)
        {
            TypeId           = typeId;
            StableName       = stableName ?? throw new ArgumentNullException(nameof(stableName));
            PreviousNames    = OldDataGraphFrozen.Freeze(previousNames);
            Kind             = kind;
            DefinitionTypeId = definitionTypeId;
            ArgumentTypeIds  = OldDataGraphFrozen.Freeze(argumentTypeIds);
        }
    }

    /// <summary>
    /// What the FILE said about one member: the stable name the payload's field list is keyed by, the
    /// older names it would also accept, and the identity of the member's type.
    /// <para>
    /// This is <see cref="OldFieldSchema"/> projected into the graph. It deliberately does <em>not</em>
    /// carry <see cref="OldFieldSchema.IsMonomorphic"/>: whether the writer wrote a per-element type id
    /// for this member's elements is a fact about the member's <em>encoding</em>, and the graph already
    /// expresses it structurally — a monomorphic member's elements are plain children of an
    /// <see cref="OldObjectNode"/> or an <see cref="OldCollectionNode"/>, a polymorphic member's are a
    /// mix of those and <see cref="OldReferenceNode"/>s. The flag is fingerprint and dump input (it is
    /// part of the layout two builds are compared on) and the read path never needs it from here.
    /// </para>
    /// <para>
    /// Enforces rule 3 the way <see cref="OldTypeIdentity"/> does — get-only, with the name list copied
    /// — and rule 1 by being a value: nothing is looked up by it, so a renamed field is a different
    /// spelling of the same record and never a different value.
    /// </para>
    /// </summary>
    public sealed class OldFieldIdentity
    {
        /// <summary>
        /// The name the file wrote this member under, verbatim. The value is found by the member's
        /// <em>position</em> in the payload order, not by this name — the name is how a rename is
        /// recognized, not how a value is located (rule 1).
        /// </summary>
        public string StableName { get; }

        /// <summary>
        /// The older names the file would also accept for this member (<c>[PreviousName]</c>). Copied,
        /// and empty rather than null when the file recorded none.
        /// </summary>
        public IReadOnlyList<string> PreviousNames { get; }

        /// <summary>The identity of the member's declared type, as the file recorded it.</summary>
        public OldTypeIdentity Type { get; }

        /// <summary>Records what the file said about one member.</summary>
        public OldFieldIdentity(string stableName, IReadOnlyList<string>? previousNames, OldTypeIdentity type)
        {
            StableName    = stableName ?? throw new ArgumentNullException(nameof(stableName));
            PreviousNames = OldDataGraphFrozen.Freeze(previousNames);
            Type          = type ?? throw new ArgumentNullException(nameof(type));
        }
    }

    /// <summary>
    /// A scalar value: an int, a bool, a <c>DateTime</c>, a string, a <c>Guid</c> — anything the file
    /// wrote with a fixed layout and no members of its own.
    /// <para>
    /// <see cref="Value"/> is deliberately <em>boxed</em>. This step is a representation change and
    /// nothing more: the primitives are still decoded exactly once, by the existing reader, and the CLR
    /// value it produced is kept as it is. Widening, converting to the current field's type and
    /// unboxing all belong to the materialize half, which is a separate pass that needs nothing but the
    /// current model; decoding them a second time here to get a narrower value type would be a second
    /// decode of primitives, which is exactly what this milestone must not introduce.
    /// </para>
    /// <para>
    /// <see cref="Value"/> is null only for a null string, which the writer recorded as string-table
    /// index 0 (RobustStringFormatter) rather than as a reference marker. That is a different recorded
    /// fact from an <see cref="OldNullNode"/> with reason
    /// <see cref="OldNullReason.ReferenceMarker"/>, which is why it is null here and not there.
    /// </para>
    /// <para>
    /// Enforces rule 3: both members are get-only, and the boxed value is the decoder's own rather than
    /// a value this build computed.
    /// </para>
    /// </summary>
    public sealed class OldScalar : OldValue
    {
        /// <summary>The identity the file gave this value, e.g. the well-known <c>Int32</c> or <c>String</c> id.</summary>
        public OldTypeIdentity Type { get; }

        /// <summary>
        /// The decoded value, boxed, or null for a null string. Immutable: rule 3 forbids re-deriving it
        /// against a different current type.
        /// </summary>
        public object? Value { get; }

        /// <summary>Records one decoded scalar.</summary>
        public OldScalar(OldTypeIdentity type, object? value)
        {
            Type  = type ?? throw new ArgumentNullException(nameof(type));
            Value = value;
        }
    }

    /// <summary>
    /// An enum value, as the file wrote it: the raw bits, and the id of the type those bits read back as.
    /// <para>
    /// Named for the node it is rather than <c>OldEnumValue</c>, which is the file's <em>schema</em>
    /// entry — a name→bits pair — in SchemaSection.cs. A value and the definition it belongs to are
    /// different facts and must not be confused at a call site.
    /// </para>
    /// <para>
    /// The bits are raw and unsigned, so a value above <see cref="long.MaxValue"/> survives, exactly as
    /// <see cref="SchemaEnumValueInfo.Value"/> records them. <see cref="UnderlyingTypeId"/> is what
    /// decides whether they mean a signed or an unsigned number, and it is the FILE's id — the one the
    /// bytes were read with — not the current enum's, which may differ after a widening migration. Which
    /// current member the bits name is a later question, answered from the current model.
    /// </para>
    /// <para>
    /// Enforces rule 3: all three members are get-only, because a re-mapped value is a different value
    /// (a different node), never a mutation of this one.
    /// </para>
    /// </summary>
    public sealed class OldEnumValueNode : OldValue
    {
        /// <summary>The enum type's identity, as the file recorded it.</summary>
        public OldTypeIdentity Type { get; }

        /// <summary>
        /// The value's raw bits, before any interpretation. A boxed <c>MyEnum</c> from this build would
        /// be a claim about the current model, which is not what this node records.
        /// </summary>
        public ulong RawBits { get; }

        /// <summary>
        /// The id of the type the bits read back as — the file's own underlying type (a well-known integer
        /// id), already carrying the reader's default for a file that recorded no definition id, because
        /// 0 is not an id a payload can carry.
        /// </summary>
        public int UnderlyingTypeId { get; }

        /// <summary>Records one enum value, uninterpreted.</summary>
        public OldEnumValueNode(OldTypeIdentity type, ulong rawBits, int underlyingTypeId)
        {
            Type             = type ?? throw new ArgumentNullException(nameof(type));
            RawBits          = rawBits;
            UnderlyingTypeId = underlyingTypeId;
        }
    }

    /// <summary>
    /// A <c>System.Type</c> value the file wrote as a polymorphic value's type tag
    /// (RobustTypeFormatter), recorded structurally.
    /// <para>
    /// <see cref="Type"/> is the recorded <em>closed</em> identity and is recursive, because the grammar
    /// is: <c>List&lt;List&lt;int&gt;&gt;</c> is <c>List</c>, one argument, <c>List</c>, one argument,
    /// <c>Int32</c>, and <c>int[][]</c> is <c>Array</c> over <c>Array</c> over <c>Int32</c>.
    /// <see cref="CurrentType"/> is the fact this build resolved that tag to, and is null for a type the
    /// application has since removed — which is not an error, since the payload stays fully describable
    /// without it.
    /// </para>
    /// <para>
    /// <see cref="CurrentType"/> is deliberately <em>not</em> part of the node's identity. It is
    /// precisely the fact that changes when a type is renamed or removed, and rule 1 requires that
    /// renaming a type cannot change which node a value is; it is recorded beside the identity for the
    /// same reason <see cref="TypeMigrationPlan.CurrentType"/> exists in the streaming interpreter: it is
    /// about the current model, not about the file.
    /// </para>
    /// <para>
    /// <see cref="RecordedFullName"/> keeps the file's own spelling for the tags that carry one (the
    /// <c>FullNameMarker</c> tag, which is a string-table index rather than a schema id) even when
    /// nothing resolves it — a removed type's name is then still in the graph, which is what a migration
    /// report has to print. A null here means the file recorded no full name for this value; it never
    /// means a name was lost.
    /// </para>
    /// <para>
    /// Enforces rule 3: all three members are get-only. A <c>Type</c> value that is null is not this
    /// node — see <see cref="OldNullReason.TypeValueNull"/>.
    /// </para>
    /// </summary>
    public sealed class OldTypeValueNode : OldValue
    {
        /// <summary>The closed identity the file's tag named, recursively. Immutable (rule 3).</summary>
        public OldTypeIdentity Type { get; }

        /// <summary>
        /// The current CLR type this tag resolves to in <em>this</em> build, or null when it resolves to
        /// nothing. A fact about the current model: kept off the node's identity (rule 1) and immutable
        /// all the same (rule 3).
        /// </summary>
        public Type? CurrentType { get; }

        /// <summary>
        /// The name the file spelled this type with, for a <c>FullNameMarker</c> tag. Null for every other
        /// tag — a tag naming a schema id or a well-known id is already fully recorded by
        /// <see cref="Type"/> and has no separate spelling. Kept even when
        /// <see cref="CurrentType"/> is null.
        /// </summary>
        public string? RecordedFullName { get; }

        /// <summary>Records one <c>Type</c> value.</summary>
        public OldTypeValueNode(OldTypeIdentity type, Type? currentType, string? recordedFullName = null)
        {
            Type             = type ?? throw new ArgumentNullException(nameof(type));
            CurrentType      = currentType;
            RecordedFullName = recordedFullName;
        }
    }

    /// <summary>
    /// One recorded member of an <see cref="OldObjectNode"/>, and the one place a binding between the
    /// file and the current model is mutable.
    /// <para>
    /// <see cref="Recorded"/> is the file's half — the member's identity, or null for a member a
    /// transform synthesised (OldDataGraph.AddMember, which is how an inlined type's values reach the type
    /// they were inlined into: that current field is one the file never had for it).
    /// <see cref="Value"/> and <see cref="CurrentFieldName"/> are the two mutable halves (rule 3), and
    /// they are the only settable members anywhere in this model.
    /// </para>
    /// <para>
    /// A value slot never holds a null CLR reference: a recorded null is an <see cref="OldNullNode"/>
    /// saying why, so nothing here can be confused with "this member was never filled in".
    /// </para>
    /// </summary>
    public sealed class OldMember
    {
        /// <summary>
        /// What the file said about this member, or null for a member a transform synthesised. Immutable
        /// (rule 3): a member removed from the projection keeps its recorded identity, and one added to
        /// the projection never acquires one.
        /// </summary>
        public OldFieldIdentity? Recorded { get; }

        /// <summary>
        /// The value bound to this member. Mutable: re-pointing a member at a different value is the one
        /// reshaping a transform is made of. It must be a value of the owning graph or a recorded null,
        /// which OldDataGraph's member methods enforce.
        /// </summary>
        public OldValue Value { get; set; }

        /// <summary>
        /// The name of the current field this member is bound to, or null when the member is removed or
        /// unmapped — the file wrote it and this build has nowhere to put it. Mutable: that binding is
        /// exactly what a migration decides.
        /// </summary>
        public string? CurrentFieldName { get; set; }

        /// <summary>
        /// Whether this member is bound to a current field, i.e. whether
        /// <see cref="CurrentFieldName"/> is set. Derived rather than stored, so it cannot disagree with
        /// the name it is derived from (rule 3: a second stored fact about a binding is one more thing to
        /// be able to get wrong).
        /// </summary>
        public bool IsRetained => CurrentFieldName != null;

        /// <summary>Records one member and the binding it starts with.</summary>
        public OldMember(OldFieldIdentity? recorded, OldValue value, string? currentFieldName = null)
        {
            Recorded         = recorded;
            Value            = value ?? throw new ArgumentNullException(nameof(value));
            CurrentFieldName = currentFieldName;
        }
    }

    /// <summary>
    /// An object: a value with members, in the order the file wrote them.
    /// <para>
    /// <see cref="RecordedMembers"/> is the file's record — the payload order, never edited, and the
    /// audit trail a migration report is printed from. It is a copy taken at construction, so no mutation
    /// of the projection can rewrite it: a member removed from <see cref="Members"/> is still in
    /// <see cref="RecordedMembers"/>, and a member added to <see cref="Members"/> never appears there.
    /// </para>
    /// <para>
    /// <see cref="Members"/> is the mutable projection a materializer reads, and the only mutable list in
    /// the model (rule 3). It holds bindings, never nodes, so editing it cannot change an index (rule 1),
    /// add or remove a node (rule 2), or make a back-reference name a different node (rule 4).
    /// OldDataGraph's member methods are the checked path and additionally verify that the node belongs
    /// to the graph and that the value has an index.
    /// </para>
    /// <para>
    /// <see cref="Type"/> is the file's closed identity and is immutable: an object is identified by
    /// which node it is, and by nothing else.
    /// </para>
    /// </summary>
    public sealed class OldObjectNode : OldValue
    {
        /// <summary>The file's closed identity for this object. Immutable (rule 3).</summary>
        public OldTypeIdentity Type { get; }

        /// <summary>
        /// The members the file wrote, in payload order. Immutable, and a copy: this list is the audit
        /// trail and is not the list a migration edits.
        /// </summary>
        public IReadOnlyList<OldMember> RecordedMembers { get; }

        /// <summary>
        /// The current projection of <see cref="RecordedMembers"/> — the members a materializer walks.
        /// Mutable (rule 3), and the only mutable list in the model; use OldDataGraph's member methods,
        /// which check what a bare <see cref="IList{T}"/> edit cannot.
        /// </summary>
        public IList<OldMember> Members { get; }

        /// <summary>Records one object and the members the file wrote for it.</summary>
        public OldObjectNode(OldTypeIdentity type, IList<OldMember> members)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            if (members == null) throw new ArgumentNullException(nameof(members));

            // Two lists, not one: the recorded one is frozen here, before any transform can touch
            // anything, and the projection starts as a copy of it.
            RecordedMembers = OldDataGraphFrozen.Freeze(members);
            Members         = new List<OldMember>(members);
        }
    }

    /// <summary>
    /// A collection value: an array, a list, a set, a queue, a stack or a dictionary, with the entries
    /// the file wrote, in order.
    /// <para>
    /// <see cref="ElementType"/>, <see cref="KeyType"/> and <see cref="ValueType"/> are the FILE'S
    /// identities, never the current model's. That is the whole point of keeping them here: a save whose
    /// <c>List&lt;Player&gt;</c> member is now a <c>List&lt;Actor&gt;</c> is still fully described by the
    /// graph, and answering "what did the file say" and "what does the current model say" with the same
    /// member is how a migration ends up silently coercing in the wrong direction.
    /// </para>
    /// <para>
    /// <see cref="Count"/> is the count exactly as recorded, which for every shape but a queue and a stack
    /// is a bias-1 count in which -1 means a null collection. <see cref="IsNullCollection"/> is derived
    /// from it, so a null collection keeps the file's identity of <em>what</em> was null: an
    /// identity-free null node could not say whether a nulled member was a list or a dictionary, and at a
    /// root or as a dictionary value there is no enclosing member to ask.
    /// </para>
    /// <para>
    /// <see cref="Keys"/> and <see cref="Values"/> are a dictionary's entries in the file's order (so a
    /// duplicate-key collision stays visible) and are null for every other shape, whose content is
    /// <see cref="Elements"/>. Every member is immutable (rule 3): reshaping a collection is later work,
    /// and until it exists the file's record is the only content there is.
    /// </para>
    /// </summary>
    public sealed class OldCollectionNode : OldValue
    {
        /// <summary>The file's closed identity for this collection, e.g. <c>List&lt;int&gt;</c>.</summary>
        public OldTypeIdentity Type { get; }

        /// <summary>
        /// The element count as recorded: a bias-1 count, or -1 for a null collection, or an unbiased
        /// count for a queue or a stack (whose formatters write a plain element count, so -1 is not a null
        /// marker there and is refused at construction). Immutable.
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// The elements, in the file's order. Empty for a dictionary — whose entries are in
        /// <see cref="Keys"/>/<see cref="Values"/> — and for a null collection, which is enforced: a -1
        /// count says no elements follow, so an element here would be a record the file never wrote.
        /// </summary>
        public IReadOnlyList<OldValue> Elements { get; }

        /// <summary>A dictionary's keys, in the file's order. Null for every other shape.</summary>
        public IReadOnlyList<OldValue>? Keys { get; }

        /// <summary>A dictionary's values, positionally matching <see cref="Keys"/>. Null for every other shape.</summary>
        public IReadOnlyList<OldValue>? Values { get; }

        /// <summary>The file's identity for one element, whatever this collection's shape.</summary>
        public OldTypeIdentity ElementType { get; }

        /// <summary>The file's identity for one key. Null unless this is a dictionary.</summary>
        public OldTypeIdentity? KeyType { get; }

        /// <summary>The file's identity for one value. Null unless this is a dictionary.</summary>
        public OldTypeIdentity? ValueType { get; }

        /// <summary>
        /// Whether the file recorded a null collection: exactly a count of
        /// <see cref="Protocol.CollectionNull"/> (-1). Derived, so it cannot disagree with
        /// <see cref="Count"/>. Impossible for a queue or a stack, whose count is unbiased — so -1 is a
        /// corrupt count there and is refused at construction rather than read as a null.
        /// </summary>
        public bool IsNullCollection => Count == Protocol.CollectionNull;

        /// <summary>Records one collection and the entries the file wrote for it.</summary>
        public OldCollectionNode(
            OldTypeIdentity          type,
            int                      count,
            IReadOnlyList<OldValue>   elements,
            OldTypeIdentity          elementType,
            IReadOnlyList<OldValue>? keys     = null,
            IReadOnlyList<OldValue>? values   = null,
            OldTypeIdentity?         keyType  = null,
            OldTypeIdentity?         valueType = null)
        {
            Type        = type ?? throw new ArgumentNullException(nameof(type));
            ElementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
            if (elements == null) throw new ArgumentNullException(nameof(elements));

            if (count < Protocol.CollectionNull)
                throw new ArgumentOutOfRangeException(nameof(count), count,
                    $"A recorded element count below {Protocol.CollectionNull} is not a thing a Ceras writer can write: the only " +
                    "negative count is the bias-1 null marker.");

            // A queue and a stack write their count through the plain int formatter, so -1 is a corrupt
            // count there and not a null collection. Refusing it here is what makes "impossible for
            // queue/stack" structural rather than a remark in a doc comment.
            if (count == Protocol.CollectionNull &&
                (type.DefinitionTypeId == WellKnownTypes.Queue || type.DefinitionTypeId == WellKnownTypes.Stack))
                throw new ArgumentOutOfRangeException(nameof(count), count,
                    $"The file recorded {type.StableName} with a count of {Protocol.CollectionNull}, but a queue and a stack write an " +
                    "unbiased element count: -1 is a corrupt count there, not a null collection.");

            // Keys and values are one fact about one dictionary. Half an entry list is a record the file
            // never wrote, so it is refused rather than read as "a dictionary with no values".
            if ((keys == null) != (values == null))
                throw new ArgumentException("Keys and Values are one fact about a dictionary: pass both or neither.", nameof(keys));
            if (keys != null && keys.Count != values!.Count)
                throw new ArgumentException(
                    $"A dictionary recorded {keys.Count} keys but {values.Count} values; the two lists are positional.", nameof(values));

            if (count == Protocol.CollectionNull && elements.Count != 0)
                throw new ArgumentException(
                    $"The file recorded a null collection (a count of {Protocol.CollectionNull}), which is followed by no elements at all.",
                    nameof(elements));

            Count     = count;
            Elements  = OldDataGraphFrozen.Freeze(elements);
            Keys      = OldDataGraphFrozen.FreezeOrNull(keys);
            Values    = OldDataGraphFrozen.FreezeOrNull(values);
            KeyType   = keyType;
            ValueType = valueType;
        }
    }

    /// <summary>
    /// A back-reference: this occurrence of a value is the same value as the one at
    /// <see cref="TargetIndex"/>. The first occurrence of a value is the node itself and every later one
    /// is one of these, which is what keeps sharing and cycles in the graph and what lets a removed
    /// field's first occurrence still be the node a retained field later refers to.
    /// <para>
    /// <see cref="TargetIndex"/> is an index in the owning graph's arena, and it names the node that
    /// holds the first occurrence. The ordering rule is that a reference names a strictly earlier index
    /// (rule 4), which OldDataGraph's constructor checks; <see cref="TargetIndex"/> being immutable
    /// (rule 3) and the arena closed (rule 2) together mean that check cannot be invalidated later — so a
    /// later builder may keep treating a violation as corruption instead of re-checking every reference
    /// after every transformation.
    /// </para>
    /// <para>
    /// The arena index is <em>not</em> the writer's object-cache id and must not be read as one. The
    /// writer registers only values written behind a reference marker, while the arena holds every value
    /// node — an inline scalar has an arena index and no object-cache id at all — so the two are the same
    /// ordering but different index spaces. The ordering is the part rule 4 rests on; mapping one onto
    /// the other is the builder's job.
    /// </para>
    /// </summary>
    public sealed class OldReferenceNode : OldValue
    {
        /// <summary>
        /// The arena index of the value this occurrence repeats; strictly less than this node's own index,
        /// which is not known when the node is built and is checked by OldDataGraph's constructor.
        /// </summary>
        public int TargetIndex { get; }

        /// <summary>Records a back-reference to the value at <paramref name="targetIndex"/>.</summary>
        public OldReferenceNode(int targetIndex)
        {
            if (targetIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(targetIndex), targetIndex,
                    "A back-reference names an arena index, and no index is negative.");

            TargetIndex = targetIndex;
        }
    }

    /// <summary>
    /// An identity-preserving graph of one save's values: the arena, the root, and the mutation
    /// vocabulary a migration uses over the bindings.
    /// <para>
    /// The arena holds every value node in the writer's first-occurrence order, so the index a
    /// <see cref="OldReferenceNode"/> names is the index of the value it repeats. Nothing else is
    /// identity: a type's name, a field's name and a value's position in a member list can all change
    /// (rule 1), and a rename is the change that must not be able to move a value.
    /// </para>
    /// <para>
    /// The arena is closed (rule 2). The constructor copies the node list and no public member appends,
    /// reserves or removes a node — which is what makes the bare indices in
    /// <see cref="OldReferenceNode"/> safe for the graph's whole lifetime, and why the mutation
    /// vocabulary below can only re-point bindings at values that already have an index. The constructor
    /// also walks the arena once, so in a constructed graph every value reachable from any node either
    /// has an index or is a recorded null (rule 1): there is no third state in which a value is in the
    /// graph but has no identity.
    /// </para>
    /// <para>
    /// <see cref="Root"/> and the five member methods are the entire mutable surface (rule 3). Everything
    /// else the graph hands out is get-only, and every collection it stores is copied on construction, so
    /// no caller can reach through the graph and edit the file's record.
    /// </para>
    /// </summary>
    public sealed class OldDataGraph
    {
        readonly OldValue[] _nodes;

        // Reference identity, not value equality: two nodes that are Equals are two nodes with two
        // indices. That is the whole of rule 1 — IndexOf asks "which of my values is this object", and
        // the graph's own key must not answer by anything a value can be renamed or retyped into.
        readonly Dictionary<OldValue, int> _indexByNode;

        /// <summary>
        /// The value the save's root held, or null when the save's root value was null — a value a save
        /// can carry, and the one position where a null reference is unambiguous, because a constructed
        /// graph is never missing a root. Mutable (rule 3), through <see cref="SetRoot"/> alone.
        /// </summary>
        public OldValue? Root { get; private set; }

        /// <summary>
        /// How many values the arena holds. Fixed for the life of the graph (rule 2): this is the index
        /// space every <see cref="OldReferenceNode"/> names into, and it never grows.
        /// </summary>
        public int NodeCount => _nodes.Length;

        /// <summary>
        /// Builds a graph from a complete arena and the root that reaches into it. This is the only way a
        /// node enters a graph; there is no later append (rule 2).
        /// <para>
        /// The list is copied, so a caller that keeps mutating the one it passed changes nothing here.
        /// The order it is given is the arena order and must be the writer's first-occurrence order:
        /// every <see cref="OldReferenceNode"/> is checked against it (rule 4), and that check is what
        /// the later builder relies on instead of re-validating each reference.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="nodes"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// A slot is null, is a recorded null (a null is a fact, not an identity, and takes no index), or
        /// repeats a value that already has one — each of which would give one value two indices or one
        /// value none.
        /// </exception>
        /// <exception cref="CerasDataCorruptionException">
        /// A back-reference does not name a strictly earlier index: a value is written before anything
        /// that can point back at it, so any other order is a corrupt payload (rule 4).
        /// </exception>
        public OldDataGraph(IReadOnlyList<OldValue> nodes, OldValue? root)
        {
            if (nodes == null) throw new ArgumentNullException(nameof(nodes));

            _nodes       = new OldValue[nodes.Count];
            _indexByNode = new Dictionary<OldValue, int>(nodes.Count, ReferenceEqualityComparer.Instance);

            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node == null)
                    throw new ArgumentException($"Arena slot {i} is null.", nameof(nodes));

                if (node is OldNullNode)
                    throw new ArgumentException(
                        $"Arena slot {i} is a recorded null. A null is a fact about a value, not an identity: nothing the writer " +
                        "wrote is a back-reference to a null, so a null takes no arena index.",
                        nameof(nodes));

                if (node is OldReferenceNode reference && reference.TargetIndex >= i)
                    throw new CerasDataCorruptionException(
                        $"Corrupt data: the back-reference at arena index {i} names index {reference.TargetIndex}, which is not " +
                        "earlier. A value is written before anything that can point back at it, so a back-reference always names " +
                        "an index before its own.");

                // Naming an earlier index puts it inside the arena already: an index is >= 0
                // (OldReferenceNode's own check) and strictly less than this one, and the loop only
                // reaches an index the arena has.
                if (!_indexByNode.TryAdd(node, i))
                    throw new ArgumentException(
                        $"The value at arena index {i} is the same value as the one at index {_indexByNode[node]}: one value has one " +
                        "index, and which one is its first occurrence.",
                        nameof(nodes));

                _nodes[i] = node;
            }

            if (root != null && root is not OldNullNode && !_indexByNode.ContainsKey(root))
                throw new ArgumentException(
                    "The root is not a value of this arena, so it has no index and could never be the target of a reference.",
                    nameof(root));

            Root = root;

            // Rule 1 over the whole graph, not just the arena: every value in a value slot is a node of
            // this arena or a recorded null. Checking it here is what lets a materializer assume that
            // anything it can reach has an index, instead of asking IndexOf at every step and having to
            // decide what -1 means. The member methods below keep it true afterwards; a bare
            // OldObjectNode.Members edit is the one way to break it, which is what the node's own doc
            // says that its methods are for.
            ValidateEveryValueIsIndexed();
        }

        /// <summary>
        /// The value at an arena index, or null when the index is outside the arena. Out of range is not
        /// corrupt data — the arena is built by Ceras, so a reference into it is only ever an index that
        /// exists — so this answers null rather than throwing, and every index an
        /// <see cref="OldReferenceNode"/> carries is already known to be in range.
        /// </summary>
        public OldValue? Node(int index) => (uint)index < (uint)_nodes.Length ? _nodes[index] : null;

        /// <summary>
        /// The index of a value in this arena, which is that value's identity (rule 1) — or -1 for a value
        /// that has none, which is a recorded null (a null takes no index because nothing can be a
        /// back-reference to it). Keyed on reference identity, so two values that are equal but distinct
        /// keep two indices.
        /// </summary>
        public int IndexOf(OldValue value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return _indexByNode.TryGetValue(value, out var index) ? index : -1;
        }

        /// <summary>
        /// Re-points the root. Null is a value a save can carry (a null root) and is not the same thing
        /// as an absent root: a constructed graph always has one, so this can only mean the save's root
        /// was null.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="value"/> is neither a value of this arena nor a recorded null.
        /// </exception>
        public void SetRoot(OldValue? value)
        {
            if (value != null) RequireBindable(value, nameof(value));
            Root = value;
        }

        /// <summary>
        /// Takes the value out of a member and leaves the member in place, bound to nothing. The member
        /// itself is not removed — that is <see cref="RemoveMember"/>, and the difference matters:
        /// detaching is for a member that is still a field of the current type but whose value is being
        /// moved or spliced elsewhere, while removing is for a field the current type no longer has.
        /// <para>
        /// This is half of reparenting. A value whose first occurrence is in a removed field is still the
        /// node a retained field must point at, and it cannot be <em>copied</em> there: the arena is
        /// closed (rule 2), so there is exactly one such value and it has to be moved. The member it came
        /// from is therefore left holding a recorded null with reason
        /// <see cref="OldNullReason.MemberDetached"/>, which is distinguishable from every null the file
        /// itself wrote, so a materializer never mistakes it for a null the application stored.
        /// </para>
        /// </summary>
        /// <returns>The value that was taken, which the caller can then bind elsewhere.</returns>
        /// <exception cref="ArgumentException"><paramref name="node"/> is not a value of this arena.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="memberIndex"/> is outside the member list.</exception>
        public OldValue DetachMemberValue(OldObjectNode node, int memberIndex)
        {
            RequireNode(node, nameof(node));
            var member = MemberAt(node, memberIndex);

            var value = member.Value;
            member.Value = new OldNullNode(OldNullReason.MemberDetached);
            return value;
        }

        /// <summary>
        /// Re-points a member at another value of this arena (or at a recorded null). The member itself —
        /// its recorded identity and its current field — is untouched; only the value it carries changes.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="node"/> is not a value of this arena, or <paramref name="value"/> is a value
        /// with no index in it.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="memberIndex"/> is outside the member list.</exception>
        public void SetMemberValue(OldObjectNode node, int memberIndex, OldValue value)
        {
            RequireNode(node, nameof(node));
            var member = MemberAt(node, memberIndex);
            RequireBindable(value, nameof(value));
            member.Value = value;
        }

        /// <summary>
        /// Takes a member out of the current projection and returns it, for a field the current type no
        /// longer has. The member is not forgotten: it stays in
        /// <see cref="OldObjectNode.RecordedMembers"/>, which is the file's record and is never edited
        /// (rule 3), so a removed field stays auditable and its value stays reachable in the arena — which
        /// is what lets a retained field point at a value whose first occurrence was in it. Its own
        /// bindings are left as they were.
        /// </summary>
        /// <returns>The member as it was, so a caller can read what it held and where it pointed.</returns>
        /// <exception cref="ArgumentException"><paramref name="node"/> is not a value of this arena.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="memberIndex"/> is outside the member list.</exception>
        public OldMember RemoveMember(OldObjectNode node, int memberIndex)
        {
            RequireNode(node, nameof(node));
            var member = MemberAt(node, memberIndex);
            node.Members.RemoveAt(memberIndex);
            return member;
        }

        /// <summary>
        /// Adds a member the file never recorded for this object, bound to a current field — the inlining
        /// primitive: when an old type is inlined into the type that held it, the values its members
        /// carried become members of the parent, and the parent is the object that has to grow.
        /// <para>
        /// The new member's <see cref="OldMember.Recorded"/> is null, which is how a synthesized member is
        /// told from a recorded one, and it does not appear in
        /// <see cref="OldObjectNode.RecordedMembers"/>: the file's record of what <em>this</em> object
        /// held is unchanged by a transformation (rule 3).
        /// </para>
        /// <para>
        /// A current field can be bound once. A second member claiming the same name would leave a
        /// materializer with two candidates and no way to choose, so it is refused here rather than
        /// resolved arbitrarily later.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="node"/> is not a value of this arena, <paramref name="value"/> is a value with
        /// no index in it, or a member is already bound to <paramref name="currentFieldName"/>.
        /// </exception>
        public OldMember AddMember(OldObjectNode node, string currentFieldName, OldValue value)
        {
            RequireNode(node, nameof(node));
            if (currentFieldName == null) throw new ArgumentNullException(nameof(currentFieldName));
            RequireBindable(value, nameof(value));

            foreach (var existing in node.Members)
                if (string.Equals(existing.CurrentFieldName, currentFieldName, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"A member is already bound to the current field '{currentFieldName}'; one current field, one member.",
                        nameof(currentFieldName));

            var member = new OldMember(recorded: null, value, currentFieldName);
            node.Members.Add(member);
            return member;
        }

        /// <summary>
        /// The current field a member is bound to, or null when it is removed or unmapped. The read side
        /// of the same question <see cref="AddMember"/> and <see cref="RemoveMember"/> answer, so a
        /// migration asks it in one place instead of reaching for the property and re-deciding what a null
        /// means.
        /// </summary>
        public static string? CurrentFieldNameOf(OldMember member)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            return member.CurrentFieldName;
        }

        /// <summary>
        /// Walks the whole arena once and refuses any value in a value slot that is neither a node of
        /// this arena nor a recorded null. One pass, at construction, is where it belongs: a later
        /// transformation would have to re-walk to know, and the point of the arena is that this is a
        /// fact rather than a check.
        /// <para>
        /// The walk keeps its own visited set, so a cycle is a normal case here and not a stack overflow
        /// — the graph holds cycles precisely because shared references are supported.
        /// </para>
        /// </summary>
        void ValidateEveryValueIsIndexed()
        {
            var visited = new HashSet<OldValue>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<OldValue>();

            for (int i = 0; i < _nodes.Length; i++)
            {
                if (!visited.Add(_nodes[i])) continue;
                pending.Push(_nodes[i]);

                while (pending.Count > 0)
                {
                    switch (pending.Pop())
                    {
                        case OldObjectNode obj:
                            foreach (var member in obj.Members)
                            {
                                RequireBindable(member.Value, "a member's value");
                                if (visited.Add(member.Value)) pending.Push(member.Value);
                            }
                            break;

                        case OldCollectionNode collection:
                            foreach (var value in ChildrenOf(collection))
                            {
                                RequireBindable(value, "a collection's entry");
                                if (visited.Add(value)) pending.Push(value);
                            }
                            break;
                    }
                }
            }
        }

        /// <summary>A collection's values: its elements, or its entries for a dictionary.</summary>
        static IEnumerable<OldValue> ChildrenOf(OldCollectionNode collection)
        {
            foreach (var element in collection.Elements) yield return element;

            if (collection.Keys == null) yield break;

            foreach (var key in collection.Keys) yield return key;
            foreach (var value in collection.Values!) yield return value;
        }

        /// <summary>
        /// The value an operation may bind, and why a value that is not one of this arena's is refused
        /// here rather than discovered later: an unindexed value could never be a back-reference target
        /// and has no identity, so binding one is exactly the break of rule 1 that the arena exists to
        /// prevent. A recorded null is the one exception, because it deliberately has no identity to
        /// preserve.
        /// </summary>
        void RequireBindable(OldValue value, string parameterName)
        {
            if (value == null) throw new ArgumentNullException(parameterName);
            if (value is OldNullNode) return;

            if (!_indexByNode.ContainsKey(value))
                throw new ArgumentException(
                    "The value has no index in this arena: the arena is closed, so a value that is not one of its nodes has no " +
                    "identity and could never be the target of a back-reference. Move a value the graph already holds instead of " +
                    "creating one.",
                    parameterName);
        }

        void RequireNode(OldObjectNode node, string parameterName)
        {
            if (node == null) throw new ArgumentNullException(parameterName);

            if (!_indexByNode.ContainsKey(node))
                throw new ArgumentException(
                    "The object is not a value of this arena, so it has no index here; bindings are per-graph, and a value of " +
                    "another graph cannot be reshaped through this one.",
                    parameterName);
        }

        OldMember MemberAt(OldObjectNode node, int memberIndex)
        {
            if ((uint)memberIndex >= (uint)node.Members.Count)
                throw new ArgumentOutOfRangeException(nameof(memberIndex), memberIndex,
                    $"The object has {node.Members.Count} members in its current projection.");

            return node.Members[memberIndex];
        }
    }
}
