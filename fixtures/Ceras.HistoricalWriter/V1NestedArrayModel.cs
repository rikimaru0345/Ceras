// The v1 models for the *nested generic-argument* array-payload fixtures: a member whose declared type is
// the same collection family on both sides, but whose **argument** changed between a concrete array and a
// polymorphic slot. The member itself keeps its grammar — a `List<X>` member is a count and elements in both
// builds — so the member-level check cannot see it; only the argument tree can.
//
// Like V1ArrayShapeModel.cs, one context per rejected direction, because the migration plans are built for
// every type the file's schema section describes and a rejected change aborts the whole load. The shapes
// that must keep loading live together in the `…Same` context, where nothing is rejected.
//
// Every type ends in a fixed-width `uint` sentinel written after the slot, so a member whose elements were
// mis-decoded shows up as a silently wrong number rather than as a load that happens to succeed.
using System;
using System.Collections.Generic;
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1Nested.ToObject
{
    /// <summary>
    /// A <c>List&lt;int[]&gt;</c> member. The list's own grammar is identical in both builds — a biased
    /// count and then the elements — but each <b>element</b> is declared as a concrete array here and as a
    /// polymorphic <c>object</c> slot in the current model, so each element's grammar is not.
    /// </summary>
    [CerasContext("HistoricalV1NestedToObject")]
    [CerasSerializable]
    public class ListOfArraysCrate
    {
        public List<int[]> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.FromObject
{
    /// <summary>
    /// A <c>List&lt;object&gt;</c> member holding concrete arrays — the mirror of
    /// <c>V1Nested.ToObject.ListOfArraysCrate</c>. The file records each element as the well-known
    /// <c>System.Object</c> id, and the current model declares the element as <c>int[]</c>, so the element
    /// moves between the two shapes in the other direction.
    /// </summary>
    [CerasContext("HistoricalV1NestedFromObject")]
    [CerasSerializable]
    public class ListOfObjectsCrate
    {
        public List<object> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.DictToObject
{
    /// <summary>
    /// A <c>Dictionary&lt;string, int[]&gt;</c> member. A keyed shape writes the same count and the same
    /// key/value entries as any other collection; only the <b>value</b> argument changed, so the key stays
    /// a plain string and the value moves between a concrete array payload and a polymorphic slot. It is
    /// pinned separately from the list case because the two arguments are read from different positions of
    /// the same entry.
    /// </summary>
    [CerasContext("HistoricalV1NestedDictToObject")]
    [CerasSerializable]
    public class DictOfArraysCrate
    {
        public Dictionary<string, int[]> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.DeepToObject
{
    /// <summary>
    /// A <c>List&lt;List&lt;int[]&gt;&gt;</c> member: the array payload sits two argument levels down, behind
    /// a list whose own argument (the inner list) is unchanged. This is the case a check that only looks at
    /// the outermost argument would let through, and the reason the walk has to be recursive rather than a
    /// single comparison.
    /// </summary>
    [CerasContext("HistoricalV1NestedDeepToObject")]
    [CerasSerializable]
    public class DeepListCrate
    {
        public List<List<int[]>> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.Widened
{
    /// <summary>
    /// A <c>List&lt;int&gt;</c> member whose current counterpart is <c>List&lt;long&gt;</c>. The member keeps
    /// the collection family the file recorded, so nothing above the argument can see the change, and the
    /// reader's element-coercion path cannot be reached: a migration plan's type comes from resolving the
    /// *file's* closed name, so the interpreter would construct a <c>List&lt;int&gt;</c> for a
    /// <c>List&lt;long&gt;</c> member and fail while assigning it. It is refused, with the direct-member
    /// widening set named so a caller can see what still converts.
    /// </summary>
    [CerasContext("HistoricalV1NestedWidened")]
    [CerasSerializable]
    public class WidenedListCrate
    {
        public List<int> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.ListToArray
{
    /// <summary>
    /// A <c>List&lt;List&lt;int&gt;&gt;</c> member whose current counterpart is <c>List&lt;int[]&gt;</c>. The
    /// definition matches and the single argument does not, so the general "no defined conversion" rule
    /// accepts this pair; the argument walk is what sees it.
    /// </summary>
    [CerasContext("HistoricalV1NestedListToArray")]
    [CerasSerializable]
    public class ListOfListsCrate
    {
        public List<List<int>> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.DictKeyChange
{
    /// <summary>
    /// A <c>Dictionary&lt;string, int&gt;</c> member whose current counterpart is
    /// <c>Dictionary&lt;int, int&gt;</c>: argument 0 changed and argument 1 did not, so a rule that only
    /// looked at the value would miss it.
    /// </summary>
    [CerasContext("HistoricalV1NestedDictKeyChange")]
    [CerasSerializable]
    public class DictKeyCrate
    {
        public Dictionary<string, int> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.DeepArgChange
{
    /// <summary>
    /// A <c>List&lt;List&lt;int&gt;&gt;</c> member whose current counterpart is <c>List&lt;List&lt;long&gt;&gt;</c>.
    /// The outer argument is <c>List&lt;int&gt;</c> on both sides and only the <em>inner</em> one changed, so
    /// this is the case a check that only looked at the first argument level would pass. It is also the pin
    /// for the argument walk actually recursing.
    /// </summary>
    [CerasContext("HistoricalV1NestedDeepArgChange")]
    [CerasSerializable]
    public class DeepArgCrate
    {
        public List<List<int>> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.RemovedArg
{
    /// <summary>A type the current model has <b>removed</b> — it claims no name and no alias here, so this
    /// build has no type to resolve the file's entry to.</summary>
    [CerasContext("HistoricalV1NestedRemovedArg")]
    [CerasSerializable]
    public class Vanished
    {
        public int Beat;
    }

    /// <summary>
    /// A <c>List&lt;Vanished&gt;</c> member whose current counterpart is a <c>List&lt;int&gt;</c>. The argument
    /// position is one this build cannot name, which is not a schema change: the reader is written to consume
    /// a removed type structurally — its members are skipped and the element becomes the current type's
    /// default — so the stream stays aligned. This is the case the migration checks deliberately leave alone,
    /// and the reader test asserts the alignment rather than trusting it.
    /// </summary>
    [CerasContext("HistoricalV1NestedRemovedArg")]
    [CerasSerializable]
    public class RemovedArgCrate
    {
        public List<Vanished> Slot;
        public uint Marker;
    }
}

namespace Ceras.HistoricalWriter.V1Nested.Same
{
    /// <summary>
    /// The controls: shapes whose argument tree is unchanged (or changed in a way Ceras already migrates),
    /// so nothing about them may be refused.
    /// </summary>
    [CerasContext("HistoricalV1NestedSame")]
    [CerasSerializable]
    public class SameListOfArraysCrate
    {
        public List<int[]> Slot;
        public uint Marker;
    }

    /// <summary>A keyed shape with the same unchanged array value, so the value argument is pinned as well
    /// as the list element one.</summary>
    [CerasContext("HistoricalV1NestedSame")]
    [CerasSerializable]
    public class SameDictOfArraysCrate
    {
        public Dictionary<string, int[]> Slot;
        public uint Marker;
    }

    /// <summary>A <c>List&lt;List&lt;int&gt;&gt;</c> member, unchanged. Two argument levels, neither of them
    /// an array, so this is the control that a walk which compared argument <em>types</em> instead of shapes
    /// — or which stopped at the outermost level — would get wrong while still looking right on the
    /// array-free cases. It is the nested shape that genuinely loads today, so it is the one control that
    /// asserts real values rather than the absence of a refusal.</summary>
    [CerasContext("HistoricalV1NestedSame")]
    [CerasSerializable]
    public class SameNestedListCrate
    {
        public List<List<int>> Slot;
        public uint Marker;
    }

    /// <summary>A renamed user type, reached through its own <c>[PreviousTypeName]</c>.</summary>
    [CerasContext("HistoricalV1NestedSame")]
    [CerasSerializable]
    public class Ping
    {
        public int Beat;
    }

    /// <summary>A <c>List&lt;Ping&gt;</c> member whose element type is renamed in the current model. An
    /// argument whose <em>type</em> changed but whose shape did not: the argument tree is identical apart
    /// from the element's own members, and renaming must not reach the shape comparison at all.</summary>
    [CerasContext("HistoricalV1NestedSame")]
    [CerasSerializable]
    public class ListOfRenamedCrate
    {
        public List<Ping> Slot;
        public uint Marker;
    }

    /// <summary>A <c>List&lt;int[]&gt;</c> member the current model has <b>removed</b>. Nothing maps onto it,
    /// so its bytes are skipped by the file's own grammar and there is no current argument tree to compare
    /// against — the removed-field path must be untouched by the shape check.</summary>
    [CerasContext("HistoricalV1NestedSame")]
    [CerasSerializable]
    public class RemovedListOfArraysCrate
    {
        public List<int[]> Slot;
        public uint Marker;
    }
}
