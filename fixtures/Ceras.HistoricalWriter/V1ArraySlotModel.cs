// The v1 model for the *System.Array-declared slot* fixtures: a member whose declared type is the
// abstract `System.Array` base class, so the file records nothing about the concrete array except the
// `Type` value written behind the reference marker (Array, element type) — the same grammar an `object`
// slot uses, reached through a different declared type.
//
// This lives in the same historical-writer assembly as V1Model.cs and, like it, is never referenced by
// tests/Ceras.Test: these CLR types must be absent from the reader build, so a reader test can only ever
// see the bytes plus the current model (tests/Ceras.Test/Tests/SystemArraySlotMigrationTests.cs). It is a
// separate *context* so the ids of the other fixtures (HistoricalV1, HistoricalV1Pooled,
// HistoricalV1EnumNames, HistoricalV1Poly) cannot shift.
using System;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1ArraySlot
{
    /// <summary>
    /// A polymorphic array slot followed by a trailing sentinel. The sentinel is a fixed-width,
    /// non-bounds-checked <c>uint</c>, and the member order puts it after the slot
    /// ("System.Array" &lt; "System.UInt32"), so a slot that is mis-decoded shows up as a silently wrong
    /// number rather than as a loud string-index error. The same shape, and for the same reason, as
    /// v1's <c>PolyCrate</c> — which is exactly the point: <c>object</c> and <c>System.Array</c> are the
    /// same grammar, so a reader that handles one and not the other is a reader that desynchronises.
    /// </summary>
    [CerasContext("HistoricalV1ArraySlot")]
    [CerasSerializable]
    public class ArrayCrate
    {
        public Array Slot;
        public uint Marker;
    }

    /// <summary>
    /// Two array slots and a sentinel. Two references to the <em>same</em> instance are written as one
    /// value plus a back-reference, because <c>ReferenceFormatter&lt;Array&gt;</c> registers the array in
    /// the writer's object cache — a slot's array is therefore a shared object, unlike a member declared
    /// as a concrete <c>T[]</c>, which <c>ArrayFormatter</c> writes by value with no marker and no cache
    /// slot at all.
    /// </summary>
    [CerasContext("HistoricalV1ArraySlot")]
    [CerasSerializable]
    public class ArrayPair
    {
        public Array First;
        public Array Second;
        public uint Marker;
    }

    /// <summary>v2 removes <c>Slot</c>, so every one of its bytes has to be consumed and the sentinel has
    /// to survive. A concrete-array member and a <c>System.Array</c> member are skipped by different code
    /// (the former has no reference marker in front of it, the latter has one plus a type value), so the
    /// removed shape is pinned rather than assumed to follow from the other fixtures.</summary>
    [CerasContext("HistoricalV1ArraySlot")]
    [CerasSerializable]
    public class ArrayEmptyCrate
    {
        public Array Slot;
        public uint Marker;
    }
}
