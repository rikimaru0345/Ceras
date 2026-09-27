// A second "old" model, in its own serialization context, for the construction tests: the types a save
// loader obtains through a configured factory or pool.
//
// It lives in its own context on purpose. A context has one finite manifest, so adding types to the frozen
// V1 model above would renumber every type id in SaveGame.bin / Inventory.bin / PlayerCycle.bin. A
// separate context keeps those three fixtures byte-identical, and a reader still has to migrate this file
// for real: the current model (tests/Ceras.Test/Tests/HistoricalConstructionFixtureTests.cs) has different
// fields for the same stable names.
//
// See the folder README for how to regenerate.
using Ceras;
using Ceras.Generated;

namespace Ceras.HistoricalWriter.V1Pooled
{
    /// <summary>
    /// v1's "recycled" type. In the current model this is a <c>Relic</c> again, but <c>AncientName</c> is
    /// gone, <c>Charge</c> became a <c>long</c> and <c>Restored</c> was added. It is deliberately an
    /// <em>open</em> class: the current reader then goes through <c>ReferenceFormatter&lt;T&gt;</c>, the
    /// wrapper whose construction policy is the one under test.
    /// </summary>
    [CerasContext("HistoricalV1Pooled")]
    [CerasSerializable]
    public class Relic
    {
        public int Id;

        /// <summary>Removed in the current model; its bytes have to be skipped.</summary>
        public string AncientName;

        /// <summary>Retyped to <c>long</c> in the current model.</summary>
        public int Charge;

        /// <summary>A polymorphic slot: the file records the concrete type of whatever is stored here.</summary>
        public object Owner;

        /// <summary>Part of a reference cycle through the pooled type.</summary>
        public Relic Twin;
    }

    /// <summary>
    /// A sealed type, reachable both as a declared member of the vault and through
    /// <see cref="Relic.Owner"/>. The two paths are the same object, so the reader has to build it once.
    /// </summary>
    [CerasContext("HistoricalV1Pooled")]
    [CerasSerializable]
    public sealed class Charm
    {
        public int Id;

        /// <summary>Removed in the current model.</summary>
        public string Sigil;
    }

    [CerasContext("HistoricalV1Pooled")]
    [CerasSerializable]
    public class RelicVault
    {
        public int Version;
        public Relic Kept;

        /// <summary>The <em>same instance</em> as <c>Kept.Owner</c>.</summary>
        public Charm Token;
    }
}
