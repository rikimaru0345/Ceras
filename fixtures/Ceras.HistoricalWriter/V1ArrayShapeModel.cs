// The v1 models for the *declared-member array-shape change* fixtures: a member whose declared type is a
// concrete `T[]` on one side of the version boundary and a polymorphic slot (`System.Array` / `object`) on
// the other. Those two declarations do not start at the same byte — the concrete array is written by
// ArrayFormatter as a bare biased element count with no reference marker, the polymorphic slot goes through
// a ReferenceFormatter and is preceded by a marker — so no reader can follow both, and a load has to be
// refused before the payload is interpreted.
//
// These types live in the same historical-writer assembly as the other v1 models and, like them, are never
// referenced by tests/Ceras.Test: they must be absent from the reader build, so a reader test can only ever
// see the bytes plus the current model
// (tests/Ceras.Test/Tests/ArrayShapeChangeMigrationTests.cs).
//
// **One context per rejected type, and that is not cosmetic.** The migration plans are built for *every*
// type the file's schema section describes, not only for the ones the payload reaches, and a rejected change
// aborts the whole load — which is the pre-existing behaviour of the general "no defined conversion" refusal
// too, and the right one (a file whose schema cannot be migrated should be refused as a whole rather than
// depending on which of its types the payload happened to touch). But it means a manifest holding two
// rejected types could only ever report whichever of them came first, so each direction here gets its own
// context and its own file. A context is also what keeps the checked-in fixtures' bytes independent: the
// manifest is fingerprinted as a whole, so a type added to one context would change every file written in it.
//
// Every type ends in a fixed-width `uint` sentinel written *after* the slot, so a slot that is mis-decoded
// shows up as a silently wrong number rather than as a load that happens to succeed. The member order is by
// declared type name then member name, so the slot always precedes the sentinel: "System.Int32[]",
// "System.Array" and "System.Object" all sort before "System.UInt32".
using System;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1ArrayShape.ToObject
{
    /// <summary>
    /// A **concrete** array member: a manifest <c>GenericInstantiation</c> entry of definition
    /// <c>Array</c>, written as a biased count and the elements with no marker in front of them. The
    /// current model declares the same member as <c>object</c>, so a reader that follows the current
    /// member's grammar reads that count as a reference marker.
    /// </summary>
    [CerasContext("HistoricalV1ArrayShapeToObject")]
    [CerasSerializable]
    public class ConcreteArrayCrate
    {
        public int[] Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1ArrayShape.FromArray
{
    /// <summary>
    /// A member declared as the abstract <c>System.Array</c> base class: the well-known declared-type id
    /// 16, reached through <c>ReferenceFormatter&lt;Array&gt;</c>, so the payload is a reference marker, the
    /// concrete array type as a <c>Type</c> value and then the array. The current model declares the same
    /// member as a concrete <c>int[]</c>, which has nowhere to put either of those.
    /// </summary>
    [CerasContext("HistoricalV1ArrayShapeFromArray")]
    [CerasSerializable]
    public class ArraySlotCrate
    {
        public Array Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1ArrayShape.FromObject
{
    /// <summary>
    /// The same polymorphic grammar reached through the declared type <c>object</c>. Kept as its own type,
    /// and its own context, rather than another fixture of the <c>System.Array</c> one: the refusal has to
    /// name the well-known declared-type id that is actually in the file, and each of the two has to be
    /// pinned by a file rather than by inspection.
    /// </summary>
    [CerasContext("HistoricalV1ArrayShapeFromObject")]
    [CerasSerializable]
    public class ObjectSlotCrate
    {
        public object Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1ArrayShape.ToList
{
    /// <summary>
    /// A member declared as <c>object</c> here and as <c>List&lt;int&gt;</c> in the current model. There is no
    /// array anywhere in it, so it is not the refused shape: it gives the reader test a change between two
    /// <em>non-array</em> declarations, which is where the array-shape refusal has to stop. (This direction is
    /// refused too, by the pre-existing "no defined conversion" check rather than by the array one — the two
    /// have different reasons and the test says so.)
    /// </summary>
    [CerasContext("HistoricalV1ArrayShapeToList")]
    [CerasSerializable]
    public class ObjectToListCrate
    {
        public object Slot;
        public uint Marker;
    }
}
