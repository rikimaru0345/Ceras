// The v1 model for the *polymorphic built-in generic* fixtures: values the file records as a System.Type
// value behind a reference marker, because that is what a closed built-in generic in an `object` slot is.
//
// This lives in the same historical-writer assembly as V1Model.cs and, like it, is never referenced by
// tests/Ceras.Test: these CLR types must be absent from the reader build, so a reader test can only ever
// see the bytes plus the current model. It is a separate *context* so the ids of the three existing
// fixtures (HistoricalV1, HistoricalV1Pooled) cannot shift.
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

// A closed generic collection in an `object` slot is not discoverable by walking member types, so the
// instantiation is declared explicitly — the same declaration a real game needs for a save that holds one.
// The context is named on purpose: an unscoped CerasKnownType joins *every* context in the compilation,
// which would add these types to the other fixtures' manifests and change their bytes.
// Fully qualified, because an assembly-level attribute precedes the namespace declaration it names.
[assembly: CerasKnownType<System.Collections.Generic.List<Ceras.HistoricalWriter.V1Poly.PolyNode>>("HistoricalV1Poly")]

namespace Ceras.HistoricalWriter.V1Poly
{
    /// <summary>
    /// v1's nested member type. The current model renames it to <c>Waypoint</c>, and it is used *inside* a
    /// polymorphic slot as the element of a <c>List</c>: a renamed type reached through a type value's
    /// argument, which is the one identity the argument list has to resolve.
    /// </summary>
    [CerasContext("HistoricalV1Poly")]
    [CerasSerializable]
    public class PolyNode
    {
        public int X;
        public int Y;
        public string Label;
    }

    /// <summary>An ordinary class: the control for the polymorphic slot (a manifest id, not a type value).</summary>
    [CerasContext("HistoricalV1Poly")]
    [CerasSerializable]
    public class PolyBeast
    {
        public int Hit;
    }

    /// <summary>
    /// A polymorphic slot followed by a trailing sentinel. The sentinel is a fixed-width, non-bounds-checked
    /// <c>uint</c> and the member order puts it after the slot ("System.Object" &lt; "System.UInt32"), so a
    /// desynchronised slot shows up as a silently wrong number rather than as a loud string-index error.
    /// </summary>
    [CerasContext("HistoricalV1Poly")]
    [CerasSerializable]
    public class PolyCrate
    {
        public object Slot;
        public uint Marker;
    }

    /// <summary>v2 removes <c>Slot</c>, so its bytes have to be consumed and the sentinel has to survive.</summary>
    [CerasContext("HistoricalV1Poly")]
    [CerasSerializable]
    public class PolyEmptyCrate
    {
        public object Slot;
        public uint Marker;
    }

    /// <summary>
    /// A retained ordinary class, a removed nested built-in shape, and a retained sentinel after both: the
    /// skip path has to consume the middle value exactly and leave its neighbours alone.
    /// </summary>
    [CerasContext("HistoricalV1Poly")]
    [CerasSerializable]
    public class PolyMixedCrate
    {
        public object Head;
        public object Mid;
        public uint Marker;
    }
}
