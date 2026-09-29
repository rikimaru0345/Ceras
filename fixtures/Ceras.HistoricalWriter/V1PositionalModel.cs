// The v1 model for the *positional* fixtures: the three structural value types the robust format
// describes structurally rather than by member list — a `Nullable<T>`, a `KeyValuePair<K,V>` and the
// `Tuple`/`ValueTuple` families. None of them has a name in the file for any of its contents: a
// nullable's value, a pair's key and value and a tuple's items are all *positions*.
//
// This lives in the historical-writer assembly like every other v1 model and, like them, is never
// referenced by tests/Ceras.Test: the v1 CLR types must be absent from the reader build, so a reader
// test can only ever see the bytes plus its own schema section. It is a separate *context* so the ids
// of every fixture already checked in cannot shift — that is why every fixture family here gets its
// own.
//
// Every crate in this file ends in a fixed-width `uint` sentinel written *after* the positional
// members, so a positional read that is even one byte out shows up as a wrong number rather than as a
// load that happens to succeed. Member order is decided by the writer's own member sort (by canonical
// type name, then member name), so a sentinel whose type sorts after the positional ones lands last.
using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1Positional
{
    /// <summary>
    /// A plain struct, so a <c>Nullable</c> over something other than a primitive is covered: the
    /// presence byte is followed by a whole member list rather than by a fixed width.
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public struct Span
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// A plain class. It is the element that makes the positional shapes the one place a cycle can
    /// close: a <c>Nullable&lt;T&gt;</c> is a struct and so is written inline with no cache slot, but a
    /// <c>KeyValuePair</c>'s key, a <c>KeyValuePair</c>'s value and a tuple's items all go through the
    /// writer's *reference* formatter, so a class in one of those positions is registered and a cycle
    /// through it is written as a back-reference.
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public class Anchor
    {
        public int Value;
    }

    /// <summary>
    /// The nullable half of the fixtures: a <c>Nullable&lt;int&gt;</c> that is present and one that is
    /// not, and a nullable over a struct, whose payload is a whole member list rather than a fixed width.
    /// A nullable as a collection element and as a dictionary value, so the presence byte is pinned in a
    /// position that is not an object member at all.
    /// <para>
    /// <c>AbsentList</c> and <c>PresentList</c> are <em>not</em> nullables and are here on purpose: C#
    /// cannot express <c>Nullable&lt;List&lt;int&gt;&gt;</c> at all — <c>List&lt;int&gt;?</c> is
    /// <c>List&lt;int&gt;</c> with an annotation and no runtime representation — so a null there is the
    /// writer's <em>null reference marker</em>. Having both kinds of null in one file is what makes the
    /// distinction checkable rather than asserted: <see cref="Absent"/> is a presence byte of 0 and
    /// <see cref="AbsentList"/> is a null marker, and they are different recorded facts with different
    /// migrations.
    /// </para>
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public class NullableCrate
    {
        public int? Absent;
        public int? Present;
        public Span? AbsentSpan;
        public Span? PresentSpan;
        public List<int> AbsentList;
        public List<int> PresentList;
        public List<int?> Elements;
        public Dictionary<string, int?> Named;

        /// <summary>Written after every positional member, so a positional read that is off by a byte
        /// shows up as a wrong number rather than as a load that happens to succeed.</summary>
        public uint Sentinel;
    }

    /// <summary>
    /// The key/value-pair half: a pair of a string key and an int value, a pair whose key is a class and
    /// whose value is a collection (so <em>both</em> positions go through the reference formatter), a
    /// pair over two nullables, and a list of pairs one of whose keys is the null string. The null
    /// string is the case worth pinning: it is string-table index 0, which is an indexed
    /// <c>OldScalar</c> with a null value, and not a recorded null.
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public class PairCrate
    {
        public KeyValuePair<string, int> Pair;
        public KeyValuePair<Anchor, List<int>> ClassKey;
        public KeyValuePair<int?, int?> Nested;
        public List<KeyValuePair<string, int>> Pairs;
        public uint Sentinel;
    }

    /// <summary>
    /// The tuple half, and the split the file makes between the two families. A
    /// <c>ValueTuple</c> is a struct: written inline, with no reference marker and no object-cache
    /// slot. A <c>Tuple</c> is a class: written behind a reference marker, registered in the writer's
    /// object cache, and therefore shareable. The last two members are the same tuple written twice —
    /// once through a member and once as a collection element — so the second occurrence has to come
    /// back as a back-reference to the very same value.
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public class TupleCrate
    {
        public ValueTuple<int, string> ValueTuplePair;
        public Tuple<int, string> TuplePair;
        public List<Tuple<int, string>> Tuples;

        /// <summary>The <em>same</em> instance as <see cref="TuplePair"/>, in a member of a different
        /// type: sharing across a member and a collection element, the case a shifted identity-cache slot
        /// would masquerade as a passing read of.</summary>
        public Tuple<int, string> SharedAgain;

        public uint Sentinel;
    }

    /// <summary>
    /// Two equal <c>ValueTuple&lt;int,int&gt;</c> members. A struct is written in full every time and is
    /// never registered, so these are two distinct values with two distinct indices — the control for
    /// the tuple sharing above, and the reason the two families cannot share one node kind's identity
    /// rule.
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public class ValueTupleRepeatCrate
    {
        public ValueTuple<int, int> First;
        public ValueTuple<int, int> Second;
        public uint Sentinel;
    }

    /// <summary>
    /// A two-node cycle that closes through a <c>KeyValuePair</c>'s key and through a tuple's item. Both
    /// are reference-typed, so the writer registers them and the second occurrence of either is a
    /// back-reference. A cycle through a *nullable* is impossible: a <c>Nullable&lt;T&gt;</c> is a struct
    /// and takes no slot, and its inner value is written with no marker, so nothing in it can name an
    /// earlier index at all.
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public class Node
    {
        public KeyValuePair<Node, int> Pair;
        public Tuple<Node, int> Tup;
        public uint Sentinel;
    }

    /// <summary>
    /// A polymorphic slot, which records its concrete runtime type as a recursive <c>System.Type</c>
    /// value rather than as a schema entry — so the same two tuple families are reached here through the
    /// <c>Type</c>-value grammar and interned as a synthesized identity instead of read from the
    /// section's own entry.
    /// </summary>
    [CerasContext("HistoricalV1Positional")]
    [CerasSerializable]
    public class PosPolyCrate
    {
        public object Slot;
        public uint Sentinel;
    }

    // The `Rest` half of the family needs no declared type of its own: it is written as a *root* whose
    // type is declared with DeclareRoot, because the CLR spells a tuple with more than seven elements
    // as a seven-argument type whose seventh item is itself a tuple (see the fixtures below). The root
    // is a coverage choice rather than a necessity — a declared member of that type bakes fine — and
    // what it buys is a positional payload with no member list around it, so the reader's root-position
    // path is exercised over a positional shape too.
}
