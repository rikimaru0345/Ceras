namespace Ceras.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.Serialization;
    using Ceras.Format;

    /// <summary>
    /// The order the members of a type are written in, for the one writer that derives it from
    /// reflection rather than from the source: <see cref="Formatters.ReflectionFallbackFormatter{T}"/>.
    /// Its generator half is <c>TypeHelpers.MemberSortKey</c>, and the two have to be the *same*
    /// order, because the same type can be written by either writer and a save records no member
    /// order of its own — only a schema section that names the fields, which the matching-layout
    /// reader never looks at. Two writers that disagree are each self-consistent and mutually
    /// unreadable.
    /// <para>
    /// The key is <b>canonical type identity</b> — <see cref="TypeIdentity.GetCanonicalName"/> of the
    /// member's type, the member's name, the canonical name of its declaring type, and <c>"f"</c> or
    /// <c>"p"</c> — for three reasons.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// It is the spelling the <em>generated</em> side can produce. The generator sorts by
    /// <c>TypeHelpers.GetRobustName</c>, which is the same function by contract
    /// (<c>CanonicalIdentityTests</c>), and a Roslyn display string is not an option here: there is
    /// no compiler, only a <see cref="Type"/>. The two spellings it used to disagree — a display
    /// string against <see cref="Type.FullName"/> — and they sort differently for the same type:
    /// <c>(int, string)</c> against <c>ValueTuple`2[[int, …]]</c>, <c>int?</c> against
    /// <c>Nullable`1[[int, …]]</c>, <c>Outer.Inner</c> against <c>Outer+Inner</c>,
    /// <c>List&lt;int&gt;</c> against <c>List`1[[int, …]]</c>. A tuple-typed member was the clearest
    /// case, because <c>(</c> sorts below every letter: the generated writer put it first and this one
    /// put it after <c>System.Int32</c>.
    /// </description></item>
    /// <item><description>
    /// It carries no assembly version or public key token. <see cref="Type.FullName"/> embeds both
    /// for a generic argument, so a key built from it depended on the BCL build the process ran on —
    /// a member order that is not the same on two machines is not a member order.
    /// </description></item>
    /// <item><description>
    /// It spells a declared type parameter by position (<c>!0</c>), which is what makes a generic
    /// definition's order a property of the definition: the one emitted writer serves every closure,
    /// so the order it writes has to come from the declaration and cannot move when the parameter is
    /// renamed. See <c>GenericMemberOrderTests</c>, which is the one place the two sides can still
    /// differ, because reflection substitutes a closure's argument into the member's type.
    /// </description></item>
    /// </list>
    /// <para>
    /// A member's wire name (<c>CerasStableNameAttribute</c>, <c>PreviousNameAttribute</c>) is not in
    /// the key, on either side: the order follows the CLR name, so renaming a member on the wire
    /// moves nothing. Each member's own declaring type is used rather than the type being formatted,
    /// so an inherited member sorts the same way on both sides.
    /// </para>
    /// <para>
    /// <see cref="ReflectionHelper.MemberLayoutMatchesMemoryImage"/> sorts with this comparer too, and
    /// deliberately: it is asking whether this order <em>is</em> the CLR's field layout, and a
    /// restatement of the key could drift away from the order that is actually written.
    /// </para>
    /// </summary>
    class SchemaMemberComparer : IComparer<SchemaMember>
    {
        public static readonly SchemaMemberComparer Instance = new SchemaMemberComparer();

        public int Compare(SchemaMember? x, SchemaMember? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (ReferenceEquals(null, y)) return 1;
            if (ReferenceEquals(null, x)) return -1;

            return string.Compare(GetSortKey(x), GetSortKey(y), StringComparison.Ordinal);
        }

        static string GetSortKey(SchemaMember m)
        {
            string typeName = TypeIdentity.GetCanonicalName(m.MemberType);
            string memberName = m.Member.Name;
            // DeclaringType is null only for a global type, which a serialized member cannot be.
            var declaringType = m.Member.DeclaringType!;
            string declaringFqn = TypeIdentity.GetCanonicalName(declaringType);
            string kind = m.Member is FieldInfo ? "f" : "p";

            return typeName + memberName + declaringFqn + kind;
        }
    }
}
