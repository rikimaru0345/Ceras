// The v1 model for the *unchanged* array-shape fixtures: the shapes that must keep loading, so a reader test
// can tell a refusal that is about the declaration from one that is about the file. It is a single context
// (like every other v1 model here) because nothing in it is refused, so there is no rejected type to mask
// another one's message — and one context keeps these fixtures' ids independent of the refused ones.
//
// Everything else about these types is described in V1ArrayShapeModel.cs.
using System;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1ArrayShapeSame
{
    /// <summary>
    /// A concrete array member that is still a concrete array in the current model (the type is renamed, the
    /// member is not). It is the control that the refusal is about the <em>shape</em> and not about the file.
    /// Written with a real array and again with a null one, because a null concrete array is the biased count
    /// <c>-1</c> rather than a marker and must not be mistaken for a polymorphic null.
    /// </summary>
    [CerasContext("HistoricalV1ArrayShapeSame")]
    [CerasSerializable]
    public class SameArrayCrate
    {
        public int[] Slot;
        public uint Marker;
    }

    /// <summary>
    /// A concrete array member the current model has <b>removed</b>. Nothing maps onto it, so it is skipped
    /// by its own file-side grammar and has to consume exactly a biased count and the elements — the mirror of
    /// the read path, and a case the array-shape refusal must not touch, because there is no current member
    /// whose declaration could disagree.
    /// </summary>
    [CerasContext("HistoricalV1ArrayShapeSame")]
    [CerasSerializable]
    public class RemovedArrayCrate
    {
        public int[] Slot;
        public uint Marker;
    }

    /// <summary>
    /// A jagged array in a <c>System.Array</c> slot. The member is polymorphic on <em>both</em> sides — the
    /// rows are levels of one polymorphic value, not declared array members — so this shape is supported today
    /// and must keep being. It is the case a too-broad "is an array anywhere in the member" rule would reject.
    /// </summary>
    [CerasContext("HistoricalV1ArrayShapeSame")]
    [CerasSerializable]
    public class JaggedSlotCrate
    {
        public Array Slot;
        public uint Marker;
    }
}
